using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF
{
    public partial class App : Application
    {
        public IServiceProvider Services { get; }

        public new static App Current => (App)Application.Current;

        private ConsumetBootstrapper? _consumetServer;
        private PythonBootstrapper? _pythonServer;
        private System.Diagnostics.Process? _qbitProcess;

        public App()
        {
            Services = ConfigureServices();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Initialize LibVLC core globally once at app startup
            LibVLCSharp.Shared.Core.Initialize();

            // Hook global exception handlers
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            string appDataDir = Path.Combine(appData, "UniversalMediaOS");
            
            UniversalMediaOS.Core.Helpers.AppLogger.Initialize(appDataDir);
            var config = Services.GetRequiredService<DomainHotSwapper>();
            UniversalMediaOS.Core.Helpers.AppLogger.IsEnabled = config.GetSetting("EnableDebugLogging") != "false";
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Application session started.");

            // Resolve and display MainWindow from DI
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // Auto-manage local services if the user has enabled it in Settings
            if (config.GetSetting("AutoManageServices") == "true")
            {
                _consumetServer = Services.GetRequiredService<ConsumetBootstrapper>();
                _pythonServer = Services.GetRequiredService<PythonBootstrapper>();

                _ = InitServicesAsync();
            }
        }

        private async Task InitServicesAsync()
        {
            try
            {
                var dep = Services.GetRequiredService<DependencyBootstrapper>();
                await dep.EnsureDependenciesAsync();

                if (_consumetServer != null)
                {
                    await _consumetServer.EnsureLatestConsumetAsync();
                }
                if (_pythonServer != null)
                {
                    await _pythonServer.BootPythonServiceAsync();
                }

                if (!string.IsNullOrEmpty(dep.DetectedQBitPath))
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dep.DetectedQBitPath,
                        Arguments = "--webui-port=8080",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    _qbitProcess = System.Diagnostics.Process.Start(startInfo);
                }
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Error during startup services boot: {ex.Message}", "ERROR");
                
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show(
                        $"Failed to initialize background services: {ex.Message}\n\nSome application features may not work correctly.",
                        "Service Initialization Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }));
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
            try { _consumetServer?.Dispose(); } catch { }
            try { _pythonServer?.Dispose(); } catch { }

            try
            {
                if (_qbitProcess != null)
                {
                    if (!_qbitProcess.HasExited)
                    {
                        _qbitProcess.Kill(entireProcessTree: true);
                        _qbitProcess.WaitForExit(3000);
                    }
                }
            }
            catch { }
            finally
            {
                _qbitProcess?.Dispose();
                _qbitProcess = null;
            }

            base.OnExit(e);
        }

        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            string baseDir = Path.Combine(appData, "UniversalMediaOS");
            string configPath = Path.Combine(baseDir, "config.json");

            string localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalMediaOS");
            Directory.CreateDirectory(localAppData);

            // Core Services
            services.AddSingleton<DomainHotSwapper>(provider => new DomainHotSwapper(configPath));
            services.AddSingleton<DependencyBootstrapper>(provider => new DependencyBootstrapper(localAppData));
            services.AddSingleton<ConsumetBootstrapper>(provider =>
            {
                var config = provider.GetRequiredService<DomainHotSwapper>();
                return new ConsumetBootstrapper(localAppData, config);
            });
            services.AddSingleton<PythonBootstrapper>(provider => new PythonBootstrapper(localAppData));

            services.AddTransient<FuzzyShieldSearch>();
            services.AddTransient<MangaService>();
            services.AddTransient<TripleNetHandoff>();
            services.AddTransient<SeasonDownloader>();
            services.AddTransient<ServiceManager>();
            services.AddTransient<EpubReaderService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<SearchViewModel>();
            services.AddTransient<MangaViewModel>();
            services.AddTransient<DownloadsViewModel>();
            services.AddTransient<PlaybackViewModel>();
            services.AddTransient<AnimeDetailsViewModel>();

            // Views
            services.AddSingleton<MainWindow>();

            // Dialog Service
            services.AddSingleton<Helpers.IDialogService, Helpers.WpfDialogService>();

            // Factory Delegate for AnimeDetailsViewModel (resolving service locator anti-pattern)
            services.AddSingleton<Func<AnimeDetailsViewModel>>(provider => () => provider.GetRequiredService<AnimeDetailsViewModel>());

            return services.BuildServiceProvider();
        }
    }
}
