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

    public partial class MainWindow : Window
    {
        private readonly FuzzyShieldSearch _searchService;
        private readonly UniversalMediaOS.Core.Routing.TripleNetHandoff _routingEngine;
        private UniversalMediaOS.Core.Services.ServiceManager? _svcMgr;
        
        // Dynamic dynamic swapper
        private DomainHotSwapper _swapper;

        // Manga & Epub Reader context
        private readonly MangaService _mangaService;
        private readonly EpubReaderService _epubReader;
        private EpubBook? _currentEpubBook;
        private int _currentEpubChapterIndex = 0;

        public MainWindow()
        {
            InitializeComponent();
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
        private void Log(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{ts}] {msg}";
            Console.WriteLine(line);
            Dispatcher.Invoke(() =>
            {
                if (StatusConsole.Text.Length > 8000)
                    StatusConsole.Text = StatusConsole.Text.Substring(StatusConsole.Text.Length - 4000);

                StatusConsole.Text += "\n" + line;
                ConsoleScroll.ScrollToEnd();
            });
        }

        private void ClearConsole_Click(object sender, RoutedEventArgs e)
        {
            StatusConsole.Text = $"[{DateTime.Now:HH:mm:ss}] Console cleared.";
        }

        // ── Lifecycle ────────────────────────────────────────────
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _svcMgr?.Dispose();
            _epubReader.CleanCache();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var sysCheck = UniversalMediaOS.Core.Services.SystemResourceCheck.PerformStartupCheck();
            Log(sysCheck.IsReady ? $"System OK — {sysCheck.Message}" : $"WARNING: {sysCheck.Message}");

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
                LoadConfigurationIntoUI();

                // Start services
                Log("Starting background services...");
                _svcMgr = new UniversalMediaOS.Core.Services.ServiceManager();
                string nodePath = Path.Combine(baseDir, "services", "node.exe");
                string consumetPath = Path.Combine(baseDir, "services", "consumet", "index.js");
                if (File.Exists(nodePath) && File.Exists(consumetPath))
                {
                    _svcMgr.StartService(nodePath, consumetPath, Path.Combine(baseDir, "services", "consumet"));
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
                    _svcMgr.StartService(qbitPath, "--webui-port=8080", Path.GetDirectoryName(qbitPath) ?? baseDir);
                }

                WelcomeText.Text = "Trending Today";
                Log("Fetching trending anime from AniList...");
                var results = await _searchService.SearchAnimeAsync("");
                SearchResultsList.ItemsSource = results;
                if (results.Count > 0)
                {
                    HeroTitle.Text = results[0].OfficialTitle;
                    HeroDescription.Text = results[0].Synopsis;
                    if (!string.IsNullOrEmpty(results[0].CoverImageUrl))
                    {
                        HeroBannerImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(results[0].CoverImageUrl));
                    }
                }
                Log($"Loaded {results.Count} trending titles.");
            }
            catch (Exception ex)
            {
                Log($"INIT ERROR: {ex.Message}");
                MessageBox.Show($"Failed to initialize: {ex.Message}");
            }
        }

        // ── Load & Bind Configuration UI ─────────────────────────────
        private void LoadConfigurationIntoUI()
        {
            _swapper.LoadConfig();

            // Dynamic Custom Sources Grid
            var sources = _swapper.GetCustomSources();
            CustomSourcesGrid.ItemsSource = null;
            CustomSourcesGrid.ItemsSource = sources;

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
            RefreshDiagnosticsText();
        }

        private void RefreshDiagnosticsText()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string nodePath = Path.Combine(baseDir, "services", "node.exe");
            string consumetPath = Path.Combine(baseDir, "services", "consumet", "index.js");
            string qbitDetected = DependencyBootstrapper.DetectedQBitPath;

            bool isScraperActive = false;
            try { 
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                isScraperActive = client.GetAsync("http://localhost:3000/").Result.IsSuccessStatusCode;
            } catch { }

            string status = $"Node.js Portable: {(File.Exists(nodePath) ? "✅ Active" : "❌ Missing")}\n" +
                           $"Local Scraper Server: {(isScraperActive ? "✅ Active" : "❌ Offline")}\n" +
                           $"qBittorrent client: {((!string.IsNullOrEmpty(qbitDetected) && File.Exists(qbitDetected)) ? $"✅ Detected at {qbitDetected}" : "⚠ WebUI fallback mode")}\n" +
                           $"SQLite tracking database: {(File.Exists(Path.Combine(baseDir, "media_os.db")) ? "✅ Connected" : "⚠ Auto-recreates on launch")}\n" +
                           $"Media Download Directory: {DownloadDirTxt.Text}\n" +
                           $"Current Resource load: {SystemResourceCheck.PerformStartupCheck().Message}";

            SystemStatusTxt.Text = status;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(QBitPortTxt.Text.Trim(), out _)) throw new Exception("QBittorrent Port must be a valid number.");

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
                RefreshDiagnosticsText();
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

        private void SwitchToStorefront()
        {
            StorefrontTab.Tag = "Active";
            MangaTab.Tag = null;
            InstalledTab.Tag = null;
            ConfigTab.Tag = null;

            StorefrontView.Visibility = Visibility.Visible;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Visible;
            SearchPlaceholderText.Text = "Fuzzy Shield Search (e.g. Bleach, Naruto)...";
        }

        private void SwitchToManga()
        {
            MangaTab.Tag = "Active";
            StorefrontTab.Tag = null;
            InstalledTab.Tag = null;
            ConfigTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Visible;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Visible;
            SearchPlaceholderText.Text = "Search Manga Dex (e.g. One Piece, Solo Leveling)...";
        }

        private void SwitchToInstalled()
        {
            InstalledTab.Tag = "Active";
            StorefrontTab.Tag = null;
            MangaTab.Tag = null;
            ConfigTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Visible;
            ConfigView.Visibility = Visibility.Collapsed;

            SearchBarPanel.Visibility = Visibility.Collapsed;
            RefreshInstalledEpisodes();
        }

        private void SwitchToConfig()
        {
            ConfigTab.Tag = "Active";
            StorefrontTab.Tag = null;
            MangaTab.Tag = null;
            InstalledTab.Tag = null;

            StorefrontView.Visibility = Visibility.Collapsed;
            MangaReaderView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Collapsed;
            ConfigView.Visibility = Visibility.Visible;

            SearchBarPanel.Visibility = Visibility.Collapsed;
            LoadConfigurationIntoUI();
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

                InstalledFilesList.ItemsSource = files;
                InstalledEmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                InstalledFilesList.ItemsSource = null;
                InstalledEmptyText.Visibility = Visibility.Visible;
            }
        }

        private void PlayInstalledFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && File.Exists(path))
            {
                Log($"Launching local P2P file in Playback Theater: {Path.GetFileName(path)}");

                var player = new PlaybackTheater();
                player.Owner = this;

                // Extract clean filename without extension for display
                string fileName = Path.GetFileNameWithoutExtension(path);
                player.InitializeMedia(0, 0, fileName, "1", "");
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
        private async void ExecuteSearch()
        {
            string query = SearchBox.Text.Trim();

            // Storefront Tab: Search Anime
            if (StorefrontTab.Tag?.ToString() == "Active")
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    WelcomeText.Text = "Trending Today";
                    try { SearchResultsList.ItemsSource = await _searchService.SearchAnimeAsync(""); } catch (Exception ex) { Log($"Failed to load trending: {ex.Message}"); }
                    return;
                }

                WelcomeText.Text = "Search Results";
                Log($"Searching AniList GQL for anime '{query}'...");

                try
                {
                    var results = await _searchService.SearchAnimeAsync(query);
                    SearchResultsList.ItemsSource = results;
                    Log($"Found {results.Count} titles.");
                }
                catch (Exception ex)
                {
                    Log($"Search failed: {ex.Message}");
                    MessageBox.Show($"Search Failed:\n{ex.Message}", "API Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            // Manga & Books Tab: Search Manga
            else if (MangaTab.Tag?.ToString() == "Active")
            {
                if (string.IsNullOrWhiteSpace(query)) return;

                Log($"Searching MangaDex API for manga '{query}'...");
                try
                {
                    var results = await _mangaService.SearchMangaAsync(query);
                    MangaResultsList.ItemsSource = results;
                    Log($"Found {results.Count} manga titles.");
                }
                catch (Exception ex)
                {
                    Log($"Manga search failed: {ex.Message}");
                }
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ExecuteSearch();
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

                // Fetch episode input
                string episodeNum = "1";
                var parentPanel = (btn.Parent as Grid)?.Parent as StackPanel;
                
                // Card panel might nest differently
                TextBox? epBox = null;
                if (parentPanel != null)
                {
                    var grid = parentPanel.Children.OfType<Grid>().FirstOrDefault();
                    if (grid != null)
                    {
                        epBox = grid.Children.OfType<TextBox>().FirstOrDefault();
                    }
                }
                
                if (epBox != null)
                {
                    episodeNum = epBox.Text;
                }

                // Resolve selected dynamic Custom Provider pattern from combobox on card
                string providerDomain = "https://gogoanime3.co/search.html?keyword={query}";
                if (parentPanel != null)
                {
                    var grid = parentPanel.Children.OfType<Grid>().FirstOrDefault();
                    if (grid != null)
                    {
                        var combo = grid.Children.OfType<ComboBox>().FirstOrDefault();
                        if (combo != null && combo.SelectedItem is ComboBoxItem item && item.Tag is string pattern)
                        {
                            providerDomain = pattern;
                        }
                    }
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
                    player.InitializeMedia(mediaId, target.IdMal, target.OfficialTitle, episodeNum, audioPref);

                    player.Show();

                    if (source.Tier == UniversalMediaOS.Core.Routing.SourceTier.Tier1_LocalP2P)
                    {
                        if (File.Exists(source.UrlOrPath))
                        {
                            player.PlayLocalOrHttp(source.UrlOrPath, source.EmbedOrigin);
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
                        player.PlayLocalOrHttp(source.UrlOrPath, source.EmbedOrigin);
                    }
                    else if (source.Tier == UniversalMediaOS.Core.Routing.SourceTier.Tier3_WebViewEmbed)
                    {
                        Log("Opening WebView2 containment player...");
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
                    MangaPagesScroll.Visibility = Visibility.Collapsed;
                    MangaWebBrowser.Visibility = Visibility.Visible;
                    
                    try
                    {
                        MangaWebBrowser.Navigate(new Uri(chapter.ExternalUrl));
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
                    MangaPagesScroll.Visibility = Visibility.Visible;

                    try
                    {
                        MangaPagesScroll.ScrollToHome();
                        MangaPagesList.ItemsSource = null;

                        var pages = await _mangaService.GetPageUrlsAsync(chapter.Id);
                        MangaPagesList.ItemsSource = pages;
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
                    EpubWebBrowser.Navigate(new Uri(filePath));
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
                if (_currentEpubBook != null && _epubReader != null)
                {
                    _epubReader.CleanCache(Path.GetDirectoryName(_currentEpubBook.ChapterFiles.FirstOrDefault()));
                }
                else
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

        private async void PlayHero_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null) { btn.IsEnabled = false; btn.Content = "Loading..."; }

            // Spotlights Frieren Ep 1
            string title = "Frieren: Beyond Journey's End";
            string episode = "1";
            string audioPref = (_swapper.GetSetting("DefaultAudioPref") == "Dub") ? " Dub" : "";
            int mediaId = 156115; // AniList Frieren ID
            
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
                    player.InitializeMedia(mediaId, 0, title, episode, audioPref);
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