using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.Generic;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.WPF
{
    public class InstalledEpisodeItem
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string FileSizeText { get; set; } = string.Empty;
    }

    public partial class LegacyMainWindow : Window
    {
        private readonly FuzzyShieldSearch _searchService;
        private readonly UniversalMediaOS.Core.Routing.TripleNetHandoff _routingEngine;
        private UniversalMediaOS.Core.Services.ServiceManager? _svcMgr;
        
        // Dynamic dynamic swapper
        private DomainHotSwapper _swapper;

        // Manga & Epub Reader context
        private readonly MangaService _mangaService;
        private readonly EpubReaderService _epubReader;
        private UniversalMediaOS.Core.Search.MediaResult? _currentHeroMedia;
        private EpubBook? _currentEpubBook;
        private int _currentEpubChapterIndex = 0;

        public System.Collections.ObjectModel.ObservableCollection<MediaResult> SearchResults { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<UniversalMediaOS.Core.Services.MangaSearchResult> MangaResults { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<UniversalMediaOS.Core.Configuration.CustomSource> CustomSources { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<InstalledEpisodeItem> InstalledFiles { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<string> MangaPages { get; } = new();

        public LegacyMainWindow()
        {
            InitializeComponent();
            SearchResultsList.ItemsSource = SearchResults;
            MangaResultsList.ItemsSource = MangaResults;
            CustomSourcesGrid.ItemsSource = CustomSources;
            InstalledFilesList.ItemsSource = InstalledFiles;
            MangaPagesList.ItemsSource = MangaPages;
            _searchService = new FuzzyShieldSearch();
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            _swapper = new DomainHotSwapper(configPath);
            _routingEngine = new UniversalMediaOS.Core.Routing.TripleNetHandoff(_swapper);
            _mangaService = new MangaService();
            _epubReader = new EpubReaderService();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        // ── Logging ──────────────────────────────────────────────
        private readonly System.Collections.Generic.Queue<string> _logBuffer = new System.Collections.Generic.Queue<string>(150);
        private void Log(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{ts}] {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            Dispatcher.Invoke(() =>
            {
                _logBuffer.Enqueue(line);
                while (_logBuffer.Count > 150) _logBuffer.Dequeue();
                StatusConsole.Text = string.Join("\n", _logBuffer);
                ConsoleScroll.ScrollToEnd();
            });
        }

        private void ClearConsole_Click(object sender, RoutedEventArgs e)
        {
            StatusConsole.Text = $"[{DateTime.Now:HH:mm:ss}] Console cleared.";
        }

        // ── Lifecycle ────────────────────────────────────────────
        private async void MainWindow_Closed(object sender, EventArgs e)
        {
            string host = _swapper.GetSetting("QBitHost") ?? "localhost";
            string port = _swapper.GetSetting("QBitPort") ?? "8080";
            string user = _swapper.GetSetting("QBitUsername") ?? "admin";
            string pass = _swapper.GetSetting("QBitPassword") ?? "adminadmin";
            
            var qbit = new UniversalMediaOS.Core.Routing.QBitLogicGate($"http://{host}:{port}");
            if (await qbit.AuthenticateAsync(null, user, pass))
            {
                await qbit.ShutdownAsync();
                await Task.Delay(2000); // Allow qBittorrent time to flush fastresume data
            }

            _svcMgr?.StopAll();
            _epubReader.CleanCache();
        }

        private void UpdateCollection<T>(System.Collections.ObjectModel.ObservableCollection<T> collection, IEnumerable<T>? newItems)
        {
            Dispatcher.Invoke(() =>
            {
                collection.Clear();
                if (newItems != null)
                {
                    foreach (var item in newItems)
                        collection.Add(item);
                }
            });
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await EpubWebBrowser.EnsureCoreWebView2Async(null);
            await MangaWebBrowser.EnsureCoreWebView2Async(null);
            _ = StartDownloadManagerLoopAsync();
            WelcomeText.Text = "Booting Services...";
            _ = Task.Run(async () =>
            {
                var sysCheck = UniversalMediaOS.Core.Services.SystemResourceCheck.PerformStartupCheck();
                Log(sysCheck.IsReady ? $"System OK - {sysCheck.Message}" : $"WARNING: {sysCheck.Message}");

                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                    Log("Bootstrapping dependencies...");
                    var depBoot = new UniversalMediaOS.Core.Services.DependencyBootstrapper(baseDir);
                    await depBoot.EnsureDependenciesAsync();

                    Log("Generating scraper microservice...");
                    var conBoot = new UniversalMediaOS.Core.Services.ConsumetBootstrapper(baseDir);
                    await conBoot.EnsureLatestConsumetAsync();

                    Log("Initializing SQLite tracking database...");
                    using (var db = new UniversalMediaOS.Core.Data.DatabaseContext())
                    {
                        db.Database.EnsureCreated();
                    }

                    // Bind Inline configuration fields
                    Dispatcher.Invoke(() => LoadConfigurationIntoUI());

                    // Start services
                    Log("Starting background services...");
                    _svcMgr = new UniversalMediaOS.Core.Services.ServiceManager();
                    string nodePath = Path.Combine(baseDir, "services", "node.exe");
                    string consumetPath = Path.Combine(baseDir, "services", "consumet", "index.js");
                    if (File.Exists(nodePath) && File.Exists(consumetPath))
                    {
                        _svcMgr.StartService(nodePath, consumetPath, Path.Combine(baseDir, "services", "consumet"));
                    }
                    else
                    {
                        Log("Tier-2 scraper files not found. Node.js features will be disabled.");
                    }
                    
                    string pythonPath = "python";
                    string scraperScript = Path.Combine(baseDir, "services", "python_scraper", "app.py");
                    if (File.Exists(scraperScript))
                    {
                        _svcMgr.StartService(pythonPath, $"-m uvicorn app:app --port 8000 --host 127.0.0.1", Path.Combine(baseDir, "services", "python_scraper"));
                    }

                    string qbitPath = UniversalMediaOS.Core.Services.DependencyBootstrapper.DetectedQBitPath;
                    if (!string.IsNullOrEmpty(qbitPath) && File.Exists(qbitPath))
                    {
                        string port = _swapper.GetSetting("QBitPort");
                        if (string.IsNullOrEmpty(port)) port = "8080";
                        _svcMgr.StartService(qbitPath, $"--webui-port={port}", Path.GetDirectoryName(qbitPath) ?? baseDir);
                    }

                    Dispatcher.Invoke(() => { 
                        WelcomeText.Text = "Trending Today"; 
                        SkeletonLoaderGrid.Visibility = Visibility.Visible;
                    });
                    Log("Fetching trending anime from AniList...");
                    var results = await _searchService.SearchAnimeAsync("");
                    
                    if (results.Count > 0)
                    {
                        _currentHeroMedia = results[0];
                        Dispatcher.Invoke(() => {
                            HeroTitle.Text = _currentHeroMedia.OfficialTitle;
                            HeroDescription.Text = _currentHeroMedia.Synopsis;
                            try {
                                var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri(_currentHeroMedia.CoverImageUrl));
                                HeroBannerImage.Source = bitmap;
                            } catch { }
                        });
                    }

                    Dispatcher.Invoke(() => {
                        UpdateCollection(SearchResults, results);
                        SkeletonLoaderGrid.Visibility = Visibility.Collapsed;
                    });
                    Dispatcher.Invoke(() =>
                    {
                        UpdateCollection(SearchResults, results);
                        if (results.Count > 0)
                        {
                            HeroTitle.Text = results[0].OfficialTitle;
                            HeroDescription.Text = results[0].Synopsis;
                            if (!string.IsNullOrEmpty(results[0].CoverImageUrl))
                            {
                                HeroBannerImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(results[0].CoverImageUrl));
                            }
                        }
                    });
                    Log($"Loaded {results.Count} trending titles.");
                }
                catch (Exception ex)
                {
                    Log($"INIT ERROR: {ex.Message}");
                    Dispatcher.Invoke(() => MessageBox.Show($"Failed to initialize: {ex.Message}"));
                }
            });
        }

        // ── Load & Bind Configuration UI ─────────────────────────────
        private void LoadConfigurationIntoUI()
        {
            _swapper.LoadConfig();

            // Dynamic Custom Sources Grid
            var sources = _swapper.GetCustomSources();
            UpdateCollection(CustomSources, sources);

            // qBittorrent Configuration settings
            QBitPathTxt.Text = DependencyBootstrapper.DetectedQBitPath;
            if (string.IsNullOrEmpty(QBitPathTxt.Text))
            {
                QBitPathTxt.Text = "(Not detected — install qBittorrent or configure WebUI parameters below)";
            }

            QBitPortTxt.Text = _swapper.GetSetting("QBitPort");
            if (string.IsNullOrEmpty(QBitPortTxt.Text)) QBitPortTxt.Text = "8080";

            QBitUserTxt.Text = _swapper.GetSetting("QBitUsername");
            if (string.IsNullOrEmpty(QBitUserTxt.Text)) QBitUserTxt.Text = "admin";

            QBitPassTxt.Password = _swapper.GetSetting("QBitPassword");
            if (string.IsNullOrEmpty(QBitPassTxt.Password)) QBitPassTxt.Password = "adminadmin";

            string dDir = _swapper.GetSetting("DownloadDirectory");
            DownloadDirTxt.Text = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;

            // MAL Progress Settings
            MalTokenTxt.Text = _swapper.GetSetting("MalOAuthToken");

            // Playback Options
            string audioPref = _swapper.GetSetting("DefaultAudioPref");
            AudioPrefCombo.SelectedIndex = (audioPref == "Dub") ? 1 : 0;

            string autoPlay = _swapper.GetSetting("AutoPlayAfterDownload");
            AutoPlayCheck.IsChecked = (autoPlay != "false");

            // Diagnostic Status Text
            _ = RefreshDiagnosticsTextAsync();
        }

        private async Task RefreshDiagnosticsTextAsync()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string nodePath = Path.Combine(baseDir, "services", "node.exe");
            string consumetPath = Path.Combine(baseDir, "services", "consumet", "index.js");
            string qbitDetected = DependencyBootstrapper.DetectedQBitPath;

            bool isConsumetActive = await PollServiceAsync("http://localhost:3000/");
            bool isPythonActive = await PollServiceAsync("http://localhost:8000/");

            string status = $"Node.js Portable: {(File.Exists(nodePath) ? "✅ Active" : "❌ Missing")}\n" +
                           $"Local Consumet Server: {(isConsumetActive ? "✅ Active" : "❌ Offline")}\n" +
                           $"Python FFmpeg Scraper: {(isPythonActive ? "✅ Active" : "❌ Offline")}\n" +
                           $"qBittorrent client: {((!string.IsNullOrEmpty(qbitDetected) && File.Exists(qbitDetected)) ? $"✅ Detected at {qbitDetected}" : "⚠ WebUI fallback mode")}\n" +
                           $"SQLite tracking database: {(File.Exists(Path.Combine(baseDir, "media_os.db")) ? "✅ Connected" : "⚠ Auto-recreates on launch")}\n" +
                           $"Media Download Directory: {DownloadDirTxt.Text}\n" +
                           $"Current Resource load: {SystemResourceCheck.PerformStartupCheck().Message}";

            Dispatcher.Invoke(() => SystemStatusTxt.Text = status);
        }

        private async Task<bool> PollServiceAsync(string url)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            int delay = 500;
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode) return true;
                }
                catch { }
                await Task.Delay(delay);
                delay *= 2; // exponential backoff
            }
            return false;
        }

        private async Task StartDownloadManagerLoopAsync()
        {
            var host = "localhost";
            var portStr = _swapper.GetSetting("QBitPort");
            int port = string.IsNullOrEmpty(portStr) ? 8080 : int.Parse(portStr);
            var logicGate = new UniversalMediaOS.Core.Routing.QBitLogicGate($"http://{host}:{port}");
            await logicGate.AuthenticateAsync(null, _swapper.GetSetting("QBitUsername"), _swapper.GetSetting("QBitPassword"));

            while (true)
            {
                try
                {
                    var info = await logicGate.GetGlobalTransferInfoAsync();
                    if (info != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            DlSpeedTxt.Text = FormatBytes(info.DlInfoSpeed) + "/s";
                            UlSpeedTxt.Text = FormatBytes(info.UpInfoSpeed) + "/s";
                        });
                    }
                }
                catch { }

                await Task.Delay(2000);
            }
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1048576) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1073741824) return (bytes / 1048576.0).ToString("F1") + " MB";
            return (bytes / 1073741824.0).ToString("F1") + " GB";
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(QBitPortTxt.Text.Trim(), out int port) || port < 1 || port > 65535) 
                {
                    throw new Exception("QBittorrent Port must be a valid number between 1 and 65535.");
                }

                // qBittorrent Config
                _swapper.SetSetting("QBitPort", QBitPortTxt.Text.Trim());
                _swapper.SetSetting("QBitUsername", QBitUserTxt.Text.Trim());
                _swapper.SetSetting("QBitPassword", QBitPassTxt.Password);
                _swapper.SetSetting("DownloadDirectory", DownloadDirTxt.Text.Trim());

                // MAL Token
                _swapper.SetSetting("MalOAuthToken", MalTokenTxt.Text.Trim());

                // Playback
                _swapper.SetSetting("DefaultAudioPref", AudioPrefCombo.SelectedIndex == 1 ? "Dub" : "Sub");
                _swapper.SetSetting("AutoPlayAfterDownload", AutoPlayCheck.IsChecked == true ? "true" : "false");

                bool saved = _swapper.SaveConfig();
                if (saved)
                {
                    MessageBox.Show("All configuration settings have been successfully applied and rewritten to config.json!", "Configuration Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                    Log("Configuration saved successfully.");
                }
                else
                {
                    throw new Exception("Failed to write to config.json.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save configuration settings:\n{ex.Message}", "Save Failure", MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Save Failure: {ex.Message}");
            }
        }

        // ── Custom Dynamic Sources Editor ───────────────────────────
        private void AddCustomSource_Click(object sender, RoutedEventArgs e)
        {
            string name = NewSourceNameTxt.Text.Trim();
            string urlPattern = NewSourceUrlTxt.Text.Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(urlPattern))
            {
                MessageBox.Show("Please enter a valid Source Name and URL Pattern.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!urlPattern.Contains("{query}"))
            {
                MessageBox.Show("The Search URL Pattern must contain the '{query}' placeholder string.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var list = _swapper.GetCustomSources();
                if (list.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("A dynamic provider source with this name already exists.", "Lobby Duplication", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                list.Add(new CustomSource { Name = name, Url = urlPattern });
                _swapper.SaveCustomSources(list);

                NewSourceNameTxt.Text = "";
                NewSourceUrlTxt.Text = "";

                LoadConfigurationIntoUI();
                Log($"Added custom provider source: {name}");
            }
            catch (Exception ex)
            {
                Log($"Add source error: {ex.Message}");
            }
        }

        private void DeleteCustomSource_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string sourceName)
            {
                var result = MessageBox.Show($"Delete provider source '{sourceName}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        var list = _swapper.GetCustomSources();
                        var target = list.FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                        {
                            list.Remove(target);
                            _swapper.SaveCustomSources(list);
                            
                            LoadConfigurationIntoUI();
                            Log($"Deleted custom source: {sourceName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Delete source error: {ex.Message}");
                    }
                }
            }
        }

        // ── ItemProviderCombo Loader Handler ─────────────────────────
        private void ItemProviderCombo_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox combo)
            {
                combo.Items.Clear();
                var sources = _swapper.GetCustomSources();
                foreach (var src in sources)
                {
                    combo.Items.Add(new ComboBoxItem { Content = src.Name, Tag = src.Url });
                }
                if (combo.Items.Count > 0)
                {
                    combo.SelectedIndex = 0;
                }
            }
        }

        // ── Sidebar Navigation Tabs ──────────────────────────────
        private void StorefrontTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchToStorefront();
        }

        private void MangaTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchToManga();
        }

        private void InstalledTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchToInstalled();
        }

        private void ConfigTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchToConfig();
        }

        private void MalTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchToMal();
        }

        private void SwitchToStorefront()
        {
            StorefrontTab.Tag = "Active";
            MangaTab.Tag = null;
            InstalledTab.Tag = null;
            ConfigTab.Tag = null;
            MalTab.Tag = null;

            StorefrontView.Visibility = Visibility.Visible;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Collapsed;
            MalView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Visible;
            SearchPlaceholderText.Text = "Fuzzy Shield Search (e.g. Bleach, Naruto)...";
        }

        private void SwitchToManga()
        {
            MangaTab.Tag = "Active";
            StorefrontTab.Tag = null;
            InstalledTab.Tag = null;
            ConfigTab.Tag = null;
            MalTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Visible;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Collapsed;
            MalView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Visible;
            SearchPlaceholderText.Text = "Search Manga Dex (e.g. One Piece, Solo Leveling)...";
        }

        private void SwitchToInstalled()
        {
            InstalledTab.Tag = "Active";
            StorefrontTab.Tag = null;
            MangaTab.Tag = null;
            ConfigTab.Tag = null;
            MalTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Visible;
            ConfigView.Visibility = Visibility.Collapsed;
            MalView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Collapsed;
            RefreshInstalledEpisodes();
        }

        private void SwitchToConfig()
        {
            ConfigTab.Tag = "Active";
            StorefrontTab.Tag = null;
            MangaTab.Tag = null;
            InstalledTab.Tag = null;
            MalTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Visible;
            MalView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Collapsed;
            LoadConfigurationIntoUI();
        }

        private void SwitchToMal()
        {
            MalTab.Tag = "Active";
            StorefrontTab.Tag = null;
            MangaTab.Tag = null;
            InstalledTab.Tag = null;
            ConfigTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Collapsed;
            MalView.Visibility = Visibility.Visible;

            SearchBarPanel.Visibility = Visibility.Collapsed;
        }

        private void RefreshInstalledEpisodes()
        {
            string dDir = _swapper.GetSetting("DownloadDirectory");
            string downloadDir = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;

            if (Directory.Exists(downloadDir))
            {
                var files = Directory.EnumerateFiles(downloadDir, "*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mkv") || f.EndsWith(".mp4") || f.EndsWith(".avi") || f.EndsWith(".webm"))
                    .Select(f =>
                    {
                        var fi = new FileInfo(f);
                        string size = fi.Length > 1_000_000_000 ? $"{fi.Length / 1_000_000_000.0:F2} GB"
                                    : fi.Length > 1_000_000 ? $"{fi.Length / 1_000_000.0:F1} MB"
                                    : $"{fi.Length / 1_000.0:F0} KB";
                        return new InstalledEpisodeItem { FileName = Path.GetFileName(f), FullPath = f, FileSizeText = size };
                    })
                    .ToList();

                UpdateCollection(InstalledFiles, files);
                InstalledEmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                UpdateCollection(InstalledFiles, new List<InstalledEpisodeItem>());
                InstalledEmptyText.Visibility = Visibility.Visible;
            }
        }

        private async void PlayInstalledFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && File.Exists(path))
            {
                Log($"Launching local P2P file in Playback Theater: {Path.GetFileName(path)}");

                var player = new PlaybackTheater();
                player.Owner = this;

                // Extract clean filename without extension for display
                string fileName = Path.GetFileNameWithoutExtension(path);
                await player.InitializeMediaAsync(0, 0, fileName, "1", "");
                player.Show();
                player.PlayLocalOrHttp(path);
            }
        }

        private void DeleteInstalledFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && File.Exists(path))
            {
                var result = MessageBox.Show($"Delete local file {Path.GetFileName(path)} permanently?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        File.Delete(path);
                        Log($"Deleted local file: {Path.GetFileName(path)}");
                        RefreshInstalledEpisodes();
                    }
                    catch (Exception ex) { Log($"Delete failed: {ex.Message}"); }
                }
            }
        }

        // ── Search Hub Logic (Anime Search / Manga Search Selector) ──
        private async Task ExecuteSearchAsync(System.Threading.CancellationToken token = default)
        {
            string query = SearchBox.Text.Trim();
            bool isStorefront = StorefrontTab.Tag?.ToString() == "Active";
            bool isManga = MangaTab.Tag?.ToString() == "Active";

            try
            {
                SearchBox.IsEnabled = false;

                if (isStorefront)
                {
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        WelcomeText.Text = "Trending Today";
                        SearchEmptyText.Visibility = Visibility.Collapsed;
                        SkeletonLoaderGrid.Visibility = Visibility.Visible;
                        UpdateCollection(SearchResults, new List<MediaResult>());
                        try { 
                            var res = await _searchService.SearchAnimeAsync("", token); 
                            UpdateCollection(SearchResults, res); 
                            SearchEmptyText.Visibility = res.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                        } catch (Exception ex) { Log($"Failed to load trending: {ex.Message}"); }
                        finally { SkeletonLoaderGrid.Visibility = Visibility.Collapsed; }
                        return;
                    }

                    WelcomeText.Text = "Search Results";
                    SearchEmptyText.Visibility = Visibility.Collapsed;
                    SkeletonLoaderGrid.Visibility = Visibility.Visible;
                    UpdateCollection(SearchResults, new List<MediaResult>());
                    Log($"Searching AniList GQL for anime '{query}'...");

                    var results = await _searchService.SearchAnimeAsync(query, token);
                    SkeletonLoaderGrid.Visibility = Visibility.Collapsed;
                    SearchEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    UpdateCollection(SearchResults, results);
                    Log($"Found {results.Count} titles.");
                }
                else if (isManga)
                {
                    if (string.IsNullOrWhiteSpace(query)) return;

                    SkeletonLoaderGrid.Visibility = Visibility.Visible;
                    MangaEmptyText.Visibility = Visibility.Collapsed;
                    UpdateCollection(MangaResults, new List<UniversalMediaOS.Core.Services.MangaSearchResult>());
                    Log($"Searching MangaDex API for manga '{query}'...");
                    
                    var results = await _mangaService.SearchMangaAsync(query, token);
                    SkeletonLoaderGrid.Visibility = Visibility.Collapsed;
                    MangaEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    UpdateCollection(MangaResults, results);
                    Log($"Found {results.Count} manga titles.");
                }
            }
            catch (Exception ex)
            {
                Log($"Search failed: {ex.Message}");
                if (isStorefront)
                {
                    WelcomeText.Text = "Search Failed";
                    MessageBox.Show($"Search Failed:\n{ex.Message}", "API Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                SearchBox.IsEnabled = true;
                SkeletonLoaderGrid.Visibility = Visibility.Collapsed;
                SearchBox.Focus();
            }
        }

        private System.Threading.CancellationTokenSource? _searchCts;

        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchCts?.Cancel();
            _searchCts = new System.Threading.CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                await Task.Delay(350, token);
                if (!token.IsCancellationRequested)
                {
                    await ExecuteSearchAsync(token);
                }
            }
            catch (TaskCanceledException) { }
        }

        // ── Anime Watch Trigger Handler ──────────────────────────────
        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int mediaId) return;

            btn.Content = "Routing...";
            btn.IsEnabled = false;

            try
            {
                var mediaList = SearchResultsList.ItemsSource as List<MediaResult>;
                var target = mediaList?.Find(m => m.Id == mediaId);

                if (target == null)
                {
                    Log("ERROR: Selected media result details could not be parsed.");
                    btn.Content = "Watch";
                    btn.IsEnabled = true;
                    return;
                }

                // Read Dub preference directly from configuration sidebar
                string savedAudio = _swapper.GetSetting("DefaultAudioPref");
                string audioPref = (savedAudio == "Dub") ? " Dub" : "";

                // Fetch episode input and provider domain
                string episodeNum = "1";
                string providerDomain = "https://gogoanime3.co/search.html?keyword={query}";
                
                if (btn.DataContext is UniversalMediaOS.Core.Search.MediaResult dataContextResult)
                {
                    episodeNum = dataContextResult.TargetEpisode;
                    providerDomain = dataContextResult.TargetProviderDomain;
                }

                Action<string> logger = (msg) => Log(msg);

                Log($"▶ Resolving: {target.OfficialTitle} Ep {episodeNum}{audioPref}");
                UniversalMediaOS.Core.Routing.PlaybackSource? source = null;

                var torrents = await _routingEngine.GetTorrentsAsync(target.OfficialTitle + audioPref, episodeNum, logger);

                // Load switchboard selection dialog
                var selectionWindow = new SourceSelectionWindow(torrents);
                selectionWindow.Owner = this;
                if (selectionWindow.ShowDialog() == true)
                {
                    switch (selectionWindow.SelectedTier)
                    {
                        case SelectedSourceTier.Tier1_Torrent:
                            Log("Tier 1 selected — scraping Nyaa P2P networks...");
                            torrents = await _routingEngine.GetTorrentsAsync(target.OfficialTitle + audioPref, episodeNum, logger);

                            if (torrents.Count == 0)
                            {
                                Log("No torrents found on Nyaa/AnimeTosho. Select Tier 2 or Tier 3 fallbacks.");
                                break;
                            }

                            var torrentSelection = new SourceSelectionWindow(torrents);
                            torrentSelection.Owner = this;
                            if (torrentSelection.ShowDialog() == true && torrentSelection.SelectedTorrent != null)
                            {
                                source = await _routingEngine.InjectTorrentAsync(torrentSelection.SelectedTorrent, logger);
                            }
                            break;

                        case SelectedSourceTier.Tier2_Consumet:
                            Log("Tier 2 selected — querying Consumet scraper...");
                            source = await _routingEngine.ResolveBestSourceAsync(target.OfficialTitle + audioPref, episodeNum, providerDomain, logger, UniversalMediaOS.Core.Routing.SourceTier.Tier2_ConsumetHttp);
                            break;

                        case SelectedSourceTier.Tier3_WebProvider:
                            Log("Tier 3 selected — launching dynamic Webview2 embed...");
                            source = await _routingEngine.ResolveBestSourceAsync(target.OfficialTitle + audioPref, episodeNum, providerDomain, logger, UniversalMediaOS.Core.Routing.SourceTier.Tier3_WebViewEmbed);
                            break;
                    }
                }

                if (source != null)
                {
                    Log($"Source resolved: Tier={source.Tier}, URL={source.UrlOrPath}");

                    // Launch playback theater
                    var player = new PlaybackTheater();
                    player.Owner = this;
                    
                    // Wire Casting cache overlay, AniSkip and Resume states!
                    await player.InitializeMediaAsync(mediaId, target.IdMal, target.OfficialTitle, episodeNum, audioPref);

                    if (source.Tier == UniversalMediaOS.Core.Routing.SourceTier.Tier1_LocalP2P)
                    {
                        if (File.Exists(source.UrlOrPath))
                        {
                            if (_swapper.GetSetting("AutoPlayAfterDownload") != "false")
                            {
                                player.Show();
                                player.PlayLocalOrHttp(source.UrlOrPath, source.EmbedOrigin);
                            }
                            else
                            {
                                Log("Download completed. AutoPlay is disabled.");
                                player.Close();
                                RefreshInstalledEpisodes();
                            }
                        }
                        else
                        {
                            Log($"ERROR: Torrents download file path unreachable: {source.UrlOrPath}");
                            player.Close();
                        }
                    }
                    else if (source.Tier == UniversalMediaOS.Core.Routing.SourceTier.Tier2_ConsumetHttp)
                    {
                        Log("Opening VLC engine for HTTP m3u8 stream...");
                        player.Show();
                        player.PlayLocalOrHttp(source.UrlOrPath, source.EmbedOrigin);
                    }
                    else if (source.Tier == UniversalMediaOS.Core.Routing.SourceTier.Tier3_WebViewEmbed)
                    {
                        Log("Opening WebView2 containment player...");
                        player.Show();
                        await player.PlayEmbedAsync(source.UrlOrPath);
                    }
                }
                else
                {
                    Log("Playback resolution terminated.");
                }
            }
            catch (Exception ex)
            {
                Log($"PLAYBACK SYSTEM ERROR: {ex.Message}");
            }
            finally
            {
                btn.Content = "Watch";
                btn.IsEnabled = true;
            }
        }

        // ── Manga & Book Reader Hub View Actions ───────────────────────
        private async void MangaResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MangaResultsList.SelectedItem is MangaSearchResult manga)
            {
                MangaReaderEmptyState.Visibility = Visibility.Collapsed;
                EpubReaderFrame.Visibility = Visibility.Collapsed;
                MangaReaderFrame.Visibility = Visibility.Visible;

                MangaShowTitle.Text = manga.Title;
                Log($"Loading MangaDex chapters for '{manga.Title}'...");

                try
                {
                    var chapters = await _mangaService.GetChaptersAsync(manga.Id);
                    MangaChapterCombo.SelectionChanged -= MangaChapterCombo_SelectionChanged;
                    MangaChapterCombo.Items.Clear();
                    
                    foreach (var ch in chapters)
                    {
                        MangaChapterCombo.Items.Add(new ComboBoxItem 
                        { 
                            Content = $"Ch {ch.ChapterNumber}: {ch.Title}", 
                            Tag = ch 
                        });
                    }
                    
                    MangaChapterCombo.SelectionChanged += MangaChapterCombo_SelectionChanged;

                    if (MangaChapterCombo.Items.Count > 0)
                    {
                        MangaChapterCombo.SelectedIndex = 0;
                    }
                    else
                    {
                        Log("No chapters found in English language feed for this manga.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed loading chapters: {ex.Message}");
                }
            }
        }

        private async void MangaChapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MangaChapterCombo.SelectedItem is ComboBoxItem item && item.Tag is MangaChapter chapter)
            {
                if (chapter.Pages == 0 && !string.IsNullOrEmpty(chapter.ExternalUrl))
                {
                    Log($"Loading external chapter source viewer: {chapter.ExternalUrl}...");
                    MangaPagesList.Visibility = Visibility.Collapsed;
                    MangaWebBrowser.Visibility = Visibility.Visible;
                    
                    try
                    {
                        MangaWebBrowser.CoreWebView2.Navigate(chapter.ExternalUrl);
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to open external reader: {ex.Message}");
                    }
                }
                else
                {
                    Log($"Fetching pages for chapter: {item.Content}...");
                    MangaWebBrowser.Visibility = Visibility.Collapsed;
                    MangaPagesList.Visibility = Visibility.Visible;

                    try
                    {
                        if (MangaPagesList.Items.Count > 0)
                        {
                            MangaPagesList.ScrollIntoView(MangaPagesList.Items[0]);
                        }
                        UpdateCollection(MangaPages, new List<string>());

                        var pages = await _mangaService.GetPageUrlsAsync(chapter.Id);
                        UpdateCollection(MangaPages, pages);
                        Log($"Manga pages loaded: {pages.Count} images. Swipe grid ready.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed fetching manga pages: {ex.Message}");
                    }
                }
            }
        }

        private void LoadEpub_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "EPUB Books (*.epub)|*.epub",
                Title = "Select EPUB Book to Read"
            };

            if (ofd.ShowDialog() == true)
            {
                Log($"Extracting local EPUB book: {Path.GetFileName(ofd.FileName)}...");
                try
                {
                    _currentEpubBook = _epubReader.LoadEpub(ofd.FileName);
                    if (_currentEpubBook != null && _currentEpubBook.ChapterFiles.Count > 0)
                    {
                        MangaReaderEmptyState.Visibility = Visibility.Collapsed;
                        MangaReaderFrame.Visibility = Visibility.Collapsed;
                        EpubReaderFrame.Visibility = Visibility.Visible;

                        EpubBookTitle.Text = _currentEpubBook.Title;
                        _currentEpubChapterIndex = 0;
                        
                        RenderEpubChapter();
                        Log($"EPUB loaded successfully. {_currentEpubBook.ChapterFiles.Count} chapters unpacked.");
                    }
                    else
                    {
                        MessageBox.Show("Could not parse EPUB file contents manifest. Validate zip container.", "Epub Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"EPUB unpack failure:\n{ex.Message}", "Epub Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RenderEpubChapter()
        {
            if (_currentEpubBook == null || _currentEpubChapterIndex < 0 || _currentEpubChapterIndex >= _currentEpubBook.ChapterFiles.Count) return;

            string filePath = _currentEpubBook.ChapterFiles[_currentEpubChapterIndex];
            if (File.Exists(filePath))
            {
                try
                {
                    // Render locally inside the Browser control
                    EpubWebBrowser.CoreWebView2.Navigate(filePath);
                    Log($"Rendering Epub chapter {_currentEpubChapterIndex + 1}/{_currentEpubBook.ChapterFiles.Count}");
                }
                catch (Exception ex)
                {
                    Log($"Browser render error: {ex.Message}");
                }
            }
        }

        private void EpubBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEpubBook == null) return;
            if (_currentEpubChapterIndex > 0)
            {
                _currentEpubChapterIndex--;
                RenderEpubChapter();
            }
            else
            {
                MessageBox.Show("First chapter reached.", "Book Reader", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void EpubNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEpubBook == null) return;
            if (_currentEpubChapterIndex < _currentEpubBook.ChapterFiles.Count - 1)
            {
                _currentEpubChapterIndex++;
                RenderEpubChapter();
            }
            else
            {
                MessageBox.Show("End of book reached.", "Book Reader", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ── Quick Action Utility Buttons ─────────────────────────────
        private void CleanCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentEpubBook != null)
                {
                    Log("Cannot clean cache while a book is actively open.");
                    return;
                }

                if (_epubReader != null)
                {
                    _epubReader.CleanCache();
                }
                Log("Local EPUB zipped chapter caches wiped clean.");
                MessageBox.Show("Local EPUB reader directory caches wiped clean.", "Cache Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Log($"Cache Wipe Error: {ex.Message}"); }
        }

        private void Diagnostics_Click(object sender, RoutedEventArgs e)
        {
            Log("Running detailed platform systems checks...");
            LoadConfigurationIntoUI();
            Log("Diagnostics updated.");
        }

        private void ReadHeroManga_Click(object sender, RoutedEventArgs e)
        {
            if (_currentHeroMedia != null)
            {
                SwitchToManga();
                SearchBox.Text = _currentHeroMedia.OfficialTitle;
                _ = ExecuteSearchAsync();
            }
        }

        private async void PlayHero_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null) { btn.IsEnabled = false; btn.Content = "Loading..."; }

            if (_currentHeroMedia == null) 
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "▶ Play Episode 1"; }
                return;
            }

            string title = _currentHeroMedia.OfficialTitle;
            string episode = "1";
            string audioPref = (_swapper.GetSetting("DefaultAudioPref") == "Dub") ? " Dub" : "";
            int mediaId = _currentHeroMedia.IdMal > 0 ? _currentHeroMedia.IdMal : _currentHeroMedia.Id; 
            
            string providerDomain = "https://gogoanime3.co/search.html?keyword={query}";
            if (HeroProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string pattern)
            {
                providerDomain = pattern;
            }

            Log($"▶ Resolving featured Spotlight: {title} Ep {episode}{audioPref}");
            try 
            {
                var source = await _routingEngine.ResolveBestSourceAsync(title + audioPref, episode, providerDomain, msg => Log(msg), UniversalMediaOS.Core.Routing.SourceTier.Tier2_ConsumetHttp);
                if (source != null)
                {
                    var player = new PlaybackTheater();
                    player.Owner = this;
                    await player.InitializeMediaAsync(mediaId, 0, title, episode, audioPref);
                    player.Show();
                    
                    if (source.Tier == UniversalMediaOS.Core.Routing.SourceTier.Tier3_WebViewEmbed)
                    {
                        await player.PlayEmbedAsync(source.UrlOrPath);
                    }
                    else
                    {
                        player.PlayLocalOrHttp(source.UrlOrPath, source.EmbedOrigin);
                    }
                }
                else
                {
                    Log("Spotlight resolution returned no active source.");
                }
            }
            catch (Exception ex) { Log($"Spotlight Play failed: {ex.Message}"); }
            finally { if (btn != null) { btn.IsEnabled = true; btn.Content = "Watch Hero"; } }
        }

        private void DownloadSeason_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int mediaId) return;

            btn.Content = "Queued...";
            btn.IsEnabled = false;

            try
            {
                var mediaList = SearchResultsList.ItemsSource as List<MediaResult>;
                var target = mediaList?.Find(m => m.Id == mediaId);

                if (target == null)
                {
                    Log("ERROR: Selected media result details could not be found.");
                    btn.Content = "📥 Season";
                    btn.IsEnabled = true;
                    return;
                }

                Log($"▶ Starting batch season download for: {target.OfficialTitle}");
                
                // Offload to background Task to prevent WPF UI freezing
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var downloader = new UniversalMediaOS.Core.Archiving.SeasonDownloader(_swapper);
                        await downloader.DownloadSeasonAsync(
                            target.OfficialTitle, 
                            msg => Log(msg), 
                            pct => {
                                // Progress updates can be logged or tracked
                                Log($"[Season Downloader] Progress: {pct:F0}%");
                            });
                        
                        // Re-enable button on UI thread when done
                        Dispatcher.Invoke(() =>
                        {
                            btn.Content = "Done ✔";
                            btn.IsEnabled = true;
                            RefreshInstalledEpisodes();
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"[Season Downloader] Unhandled task error: {ex.Message}");
                        Dispatcher.Invoke(() =>
                        {
                            btn.Content = "Failed";
                            btn.IsEnabled = true;
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"[Season Downloader] Click error: {ex.Message}");
                btn.Content = "📥 Season";
                btn.IsEnabled = true;
            }
        }
    }
}