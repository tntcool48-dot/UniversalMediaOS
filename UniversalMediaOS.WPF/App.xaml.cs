using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.WPF
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        public IServiceProvider Services => _serviceProvider ??
            throw new ObjectDisposedException(nameof(App));

        public new static App Current => (App)Application.Current;

        private PythonBootstrapper? _pythonServer;
        private System.Diagnostics.Process? _qbitProcess;
        private System.Threading.CancellationTokenSource? _episodeAlertCts;

        public App()
        {
            _serviceProvider = ConfigureServices();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Initialize LibVLC core globally once at app startup
            LibVLCSharp.Shared.Core.Initialize();

            // Hook global exception handlers
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;
            string appDataDir = Path.Combine(appData, "UniversalMediaOS");
            
            UniversalMediaOS.Core.Helpers.AppLogger.Initialize(appDataDir);
            var config = Services.GetRequiredService<DomainHotSwapper>();
            UniversalMediaOS.Core.Helpers.AppLogger.IsEnabled = config.GetSetting("EnableDebugLogging") != "false";
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Application session started.");
            ThemeRuntime.ApplyFromConfig(config);

            var hlsProxy = Services.GetRequiredService<UniversalMediaOS.Core.Streaming.HlsLoopbackProxy>();
            hlsProxy.Start();

            // Start required local infrastructure before constructing SettingsViewModel.
            // Its initial health snapshot is taken during construction, so resolving the
            // window first left the status bar stuck on "HLS Proxy: Offline" even though
            // the proxy started a moment later.
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            RefreshServiceHealthStatus();

            var downloadQueue = Services.GetRequiredService<DownloadQueueService>();
            downloadQueue.JobCompleted += DownloadQueue_JobCompleted;

            // Auto-manage local services if the user has enabled it in Settings
            if (ServiceStartupPolicy.AutoManageEnabled(config.GetSetting("AutoManageServices")))
            {
                _pythonServer = Services.GetRequiredService<PythonBootstrapper>();
                _ = InitServicesAsync();
            }

            _episodeAlertCts = new System.Threading.CancellationTokenSource();
            _ = MonitorNewEpisodesAsync(_episodeAlertCts.Token);

            if (config.GetSetting("VaDetectEnableBackgroundPrefetch") == "true")
            {
                VoiceLanguageMode mode = config.GetSetting("VaDetectDefaultMode")
                    .Equals("Sub", StringComparison.OrdinalIgnoreCase)
                        ? VoiceLanguageMode.Sub
                        : VoiceLanguageMode.Dub;
                _ = Services.GetRequiredService<BackgroundVoiceCastPrefetchService>()
                    .StartAsync(mode);
            }
        }

        private void DownloadQueue_JobCompleted(object? sender, DownloadQueueJob job)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                WeakReferenceMessenger.Default.Send(
                    new ToastNotificationMessage($"Season download complete: {job.Title}"));

                var config = Services.GetRequiredService<DomainHotSwapper>();
                if (config.GetSetting("AutoPlayAfterDownload") == "true" &&
                    !string.IsNullOrWhiteSpace(job.CompletedFilePath) &&
                    File.Exists(job.CompletedFilePath))
                {
                    WeakReferenceMessenger.Default.Send(new PlayMediaMessage(
                        job.CompletedFilePath,
                        $"{job.Title} - Episode 1",
                        malId: job.MalId ?? 0,
                        episodeNumber: "1",
                        totalEpisodes: job.TotalEpisodes));
                }
            });
        }

        private async Task MonitorNewEpisodesAsync(System.Threading.CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                var monitor = Services.GetRequiredService<NewEpisodeMonitorService>();
                while (!token.IsCancellationRequested)
                {
                    var alerts = await monitor.CheckSafelyAsync(token);
                    if (alerts.Count > 0)
                    {
                        Services.GetRequiredService<EpisodeAlertHistoryService>().AddAlerts(alerts);
                        string message = alerts.Count == 1
                            ? $"New episode: {alerts[0].Title} episode {alerts[0].AvailableEpisode} is available."
                            : "New episodes: " + string.Join(
                                "; ",
                                alerts.Take(3).Select(alert => $"{alert.Title} E{alert.AvailableEpisode}")) +
                              (alerts.Count > 3 ? $"; +{alerts.Count - 3} more" : string.Empty);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            WeakReferenceMessenger.Default.Send(
                                new ToastNotificationMessage(message));
                            Services.GetRequiredService<WindowsNotificationService>()
                                .TryShow(alerts.Count == 1 ? "New episode available" : "New episodes available", message);
                        });
                    }

                    await Task.Delay(TimeSpan.FromMinutes(30), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Episode alert monitor stopped: {ex.Message}", "WARNING");
            }
        }

        private async Task InitServicesAsync()
        {
            try
            {
                var dep = Services.GetRequiredService<DependencyBootstrapper>();
                // Python preparation must not wait behind an extension download.
                await Task.WhenAll(Task.Run(() => dep.EnsureDependenciesAsync()),
                    _pythonServer?.EnsureScraperReadyAsync() ?? Task.CompletedTask);

                if (!string.IsNullOrEmpty(dep.DetectedQBitPath))
                {
                    var config = Services.GetRequiredService<DomainHotSwapper>();
                    string host = config.GetSetting("QBitHost");
                    bool localHost = string.IsNullOrWhiteSpace(host) ||
                                     host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                     host == "127.0.0.1" ||
                                     host == "::1";
                    string processName = Path.GetFileNameWithoutExtension(dep.DetectedQBitPath);
                    bool alreadyRunning = !string.IsNullOrWhiteSpace(processName) &&
                                          System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
                    if (!localHost || alreadyRunning)
                    {
                        UniversalMediaOS.Core.Helpers.AppLogger.Log(!localHost
                            ? "qBittorrent auto-start skipped because Settings points to a remote WebUI host."
                            : "qBittorrent is already running; reusing the existing process.");
                    }
                    else
                    {
                        string port = config.GetSetting("QBitPort");
                        if (!int.TryParse(port, out int parsedPort) || parsedPort is < 1 or > 65535)
                        {
                            parsedPort = 8080;
                        }

                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = dep.DetectedQBitPath,
                            Arguments = $"--webui-port={parsedPort}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        _qbitProcess = System.Diagnostics.Process.Start(startInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Error during startup services boot: {ex.Message}", "ERROR");
                
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show(
                        $"Failed to initialize background services: {ex.Message}\n\nSome application features may not work correctly.",
                        "Service Initialization Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }));
            }
            finally
            {
                // Dependency verification/downloads run after the window is visible.
                // Publish the completed state instead of leaving the startup snapshot
                // ("Missing" / "Unavailable") on screen until a manual refresh.
                await Dispatcher.InvokeAsync(RefreshServiceHealthStatus);
            }
        }

        private void RefreshServiceHealthStatus()
        {
            try
            {
                Services.GetRequiredService<SettingsViewModel>()
                    .RefreshServiceHealthCommand.Execute(null);
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log(
                    $"Could not refresh service health: {ex.Message}", "WARNING");
            }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"Unhandled UI Exception: {e.Exception}", "CRITICAL");
            ShowGracefulErrorWindow(e.Exception);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"Unhandled AppDomain Exception: {ex}", "CRITICAL");
            ShowGracefulErrorWindow(ex);
        }

        private void ShowGracefulErrorWindow(Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    $"A critical error has occurred in the application:\n\n{ex.Message}\n\nThe details have been logged. The application may need to close.",
                    "Critical Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _episodeAlertCts?.Cancel(); } catch { }
            try { _episodeAlertCts?.Dispose(); } catch { }
            _episodeAlertCts = null;
            try { Services.GetService<BackgroundVoiceCastPrefetchService>()?.Stop(); } catch { }
            try { Services.GetService<WatchRoomRelayService>()?.Stop(); } catch { }
            try
            {
                var queue = Services.GetService<DownloadQueueService>();
                if (queue != null)
                {
                    queue.JobCompleted -= DownloadQueue_JobCompleted;
                }
            }
            catch { }

            try
            {
                if (_qbitProcess != null)
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log("Leaving qBittorrent running on app exit to preserve active downloads.");
                }
            }
            catch { }
            finally
            {
                _qbitProcess?.Dispose();
                _qbitProcess = null;
            }

            try { _serviceProvider?.Dispose(); } catch { }
            _serviceProvider = null;
            _pythonServer = null;

            try { UniversalMediaOS.Core.Helpers.AppLogger.Shutdown(); } catch { }

            base.OnExit(e);
        }

        internal static ServiceProvider ConfigureServices(Action<IServiceCollection>? configureOverrides = null)
        {
            var services = new ServiceCollection();

            string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;
            string baseDir = Path.Combine(appData, "UniversalMediaOS");
            string configPath = Path.Combine(baseDir, "config.json");

            string localAppData = Path.Combine(UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory, "UniversalMediaOS");
            Directory.CreateDirectory(localAppData);

            // Core Services
            services.AddSingleton<DomainHotSwapper>(provider => new DomainHotSwapper(configPath));
            services.AddSingleton<DependencyBootstrapper>(provider => new DependencyBootstrapper(localAppData));
            services.AddSingleton<PythonBootstrapper>(provider => new PythonBootstrapper());
            services.AddSingleton<UniversalMediaOS.Core.Streaming.HlsLoopbackProxy>();
            services.AddSingleton<UniversalMediaOS.Core.Services.ScraperEngine>();
            services.AddSingleton<UniversalMediaOS.Core.Services.IScraperResolver>(sp => sp.GetRequiredService<UniversalMediaOS.Core.Services.ScraperEngine>());
            services.AddSingleton<UniversalMediaOS.Core.Services.DubAvailabilityService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.FavoriteMediaService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.NewEpisodeMonitorService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.EpisodeAlertHistoryService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.MalOAuthService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.MalPublicListFallbackService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.MalLibrarySyncService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.VoiceCastService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.VoiceActorIndexService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.BackgroundVoiceCastPrefetchService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.WatchRoomRelayService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.WatchTogetherClientService>();
            services.AddSingleton<UniversalMediaOS.Core.Services.PlaybackSyncController>();
            services.AddSingleton<HttpClient>(_ =>
                OtherMediaHttpClientFactory.Create(TimeSpan.FromSeconds(30)));
            services.AddSingleton<ProviderRequestCoordinator>(provider =>
                new ProviderRequestCoordinator(provider.GetRequiredService<HttpClient>()));
            services.AddSingleton<AudiovisualLibraryService>();
            services.AddSingleton<PlaybackProgressService>();
            services.AddSingleton<AuthorizedMediaDownloadService>();
            services.AddSingleton<OtherMediaSourceSafetyService>();
            services.AddSingleton<UniversalMediaOS.Core.OtherMedia.Books.BookScraperEngine>();
            services.AddSingleton<UniversalMediaOS.Core.OtherMedia.AudiovisualScraperEngine>();
            services.AddSingleton<UniversalMediaOS.Core.OtherMedia.ScraperAudiovisualSourceProvider>();
            services.AddSingleton<AudiovisualSourceResolver>(provider =>
                new AudiovisualSourceResolver(
                    provider.GetRequiredService<DomainHotSwapper>(),
                    provider.GetRequiredService<HttpClient>(),
                    provider.GetRequiredService<ProviderRequestCoordinator>(),
                    provider.GetRequiredService<ScraperAudiovisualSourceProvider>()));
            services.AddSingleton<MovieService>(provider =>
                new MovieService(
                    provider.GetRequiredService<DomainHotSwapper>(),
                    provider.GetRequiredService<ProviderRequestCoordinator>(),
                    provider.GetRequiredService<AudiovisualSourceResolver>()));
            services.AddSingleton<TvService>(provider =>
                new TvService(
                    provider.GetRequiredService<DomainHotSwapper>(),
                    provider.GetRequiredService<ProviderRequestCoordinator>(),
                    provider.GetRequiredService<AudiovisualSourceResolver>()));
            services.AddSingleton<CartoonService>(provider =>
                new CartoonService(
                    provider.GetRequiredService<DomainHotSwapper>(),
                    provider.GetRequiredService<ProviderRequestCoordinator>(),
                    provider.GetRequiredService<AudiovisualSourceResolver>()));
            services.AddTransient<IBookCatalogProvider>(provider =>
            {
                var config = provider.GetRequiredService<DomainHotSwapper>();
                return new OpenLibraryBookProvider(
                    provider.GetRequiredService<HttpClient>(),
                    ParseOptionalHttpUri(config.GetSetting("OpenLibraryApiUrl")));
            });
            services.AddTransient<IBookCatalogProvider>(provider =>
            {
                var config = provider.GetRequiredService<DomainHotSwapper>();
                return new GoogleBooksBookProvider(
                    provider.GetRequiredService<HttpClient>(),
                    config.GetSetting("GoogleBooksApiKey"),
                    ParseOptionalHttpUri(config.GetSetting("GoogleBooksApiUrl")));
            });
            services.AddTransient<AnnasArchiveBookProvider>();
            services.AddTransient<IBookCatalogProvider>(provider => provider.GetRequiredService<AnnasArchiveBookProvider>());
            services.AddTransient<BookCatalogService>();
            services.AddTransient<InternetArchiveBookService>(provider =>
            {
                var config = provider.GetRequiredService<DomainHotSwapper>();
                Uri archiveRoot = ParseOptionalHttpUri(config.GetSetting("InternetArchiveUrl")) ??
                    new Uri("https://archive.org/");
                return new InternetArchiveBookService(
                    provider.GetRequiredService<HttpClient>(),
                    new Uri(archiveRoot, "advancedsearch.php"),
                    new Uri(archiveRoot, "metadata/"));
            });
            services.AddSingleton<LocalBookImportService>();
            services.AddTransient<BookReaderService>(provider =>
            {
                var config = provider.GetRequiredService<DomainHotSwapper>();
                string cacheDirectory = config.GetSetting("BookCacheDirectory");
                try
                {
                    cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
                        ? string.Empty
                        : Path.GetFullPath(cacheDirectory);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log(
                        $"Invalid configured book cache directory; using the default. {ex.Message}",
                        "WARNING");
                    cacheDirectory = string.Empty;
                }

                return new BookReaderService(
                    provider.GetRequiredService<HttpClient>(),
                    string.IsNullOrWhiteSpace(cacheDirectory) ? null : cacheDirectory);
            });
            services.AddSingleton<IReadingProgressStore, JsonReadingProgressStore>();

            services.AddTransient<DatabaseContext>();
            services.AddTransient<FuzzyShieldSearch>();
            services.AddTransient<MangaService>();
            services.AddSingleton<TripleNetHandoff>();
            services.AddSingleton<IDownloadJobExecutor, SeasonDownloadJobExecutor>();
            services.AddSingleton<DownloadQueueService>();
            services.AddSingleton<TemporaryEpisodeWatchService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<SearchViewModel>();
            services.AddTransient<MangaViewModel>();
            services.AddSingleton<DownloadsViewModel>();
            services.AddTransient<PlaybackViewModel>();
            services.AddTransient<AnimeDetailsViewModel>();
            services.AddTransient<VaDetectViewModel>();
            services.AddSingleton<WatchTogetherViewModel>();
            services.AddSingleton<MyListViewModel>();
            services.AddTransient<MovieCatalogViewModel>();
            services.AddTransient<TvCatalogViewModel>();
            services.AddTransient<CartoonCatalogViewModel>();
            services.AddTransient<BookBrowseViewModel>();
            services.AddTransient<BookDetailsViewModel>();
            services.AddTransient<BookReaderViewModel>();

            // Views
            services.AddSingleton<MainWindow>();

            // Dialog Service
            services.AddSingleton<Helpers.IDialogService, Helpers.WpfDialogService>();
            services.AddSingleton<Helpers.IExternalLauncher, Helpers.WpfExternalLauncher>();
            services.AddSingleton<Helpers.IFolderPicker, Helpers.WpfFolderPicker>();
            services.AddSingleton<Helpers.WindowsNotificationService>();

            configureOverrides?.Invoke(services);
            return services.BuildServiceProvider();
        }

        private static Uri? ParseOptionalHttpUri(string? value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                   uri.Scheme is "http" or "https"
                ? uri
                : null;
        }
    }
}
