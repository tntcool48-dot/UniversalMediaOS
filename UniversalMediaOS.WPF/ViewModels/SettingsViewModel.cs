using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly DomainHotSwapper _config;
        private readonly Helpers.IDialogService _dialogService;
        private readonly Helpers.IExternalLauncher _externalLauncher;
        private readonly Helpers.IFolderPicker _folderPicker;
        private readonly DependencyBootstrapper _dependencies;
        private readonly PythonBootstrapper _python;
        private readonly HlsLoopbackProxy _hlsProxy;
        private readonly MalOAuthService _malOAuthService;
        private readonly EpisodeAlertHistoryService _episodeAlertHistory;

        [ObservableProperty] private string _qbitHost = string.Empty;
        [ObservableProperty] private string _qbitPort = string.Empty;
        [ObservableProperty] private string _qbitUsername = string.Empty;
        [ObservableProperty] private string _qbitPassword = string.Empty;
        [ObservableProperty] private string _malClientId = string.Empty;
        [ObservableProperty] private string _malClientSecret = string.Empty;
        [ObservableProperty] private string _malRedirectUri = string.Empty;
        [ObservableProperty] private string _malConnectedUsername = string.Empty;
        [ObservableProperty] private string _malOAuthToken = string.Empty;
        [ObservableProperty] private string _defaultAudioPref = "Sub";
        [ObservableProperty] private bool _autoPlayAfterDownload;
        [ObservableProperty] private bool _autoManageServices;
        [ObservableProperty] private string _scraperSiteAttemptLimit = "6";
        [ObservableProperty] private int _scraperSiteAttemptLimitValue = 6;
        [ObservableProperty] private bool _enableDebugLogging;
        [ObservableProperty] private string _logFileSizeText = "0 KB";
        [ObservableProperty] private string _downloadDirectory = string.Empty;
        [ObservableProperty] private string _serviceHealthText = "Checking services...";
        [ObservableProperty] private string _serviceSummaryText = "Checking services...";
        [ObservableProperty] private Brush _serviceSummaryBrush = Brushes.Gray;
        [ObservableProperty] private string _pythonScraperStatus = "Checking";
        [ObservableProperty] private double _pythonScraperHealth = 0;
        [ObservableProperty] private Brush _pythonScraperBrush = Brushes.Gray;
        [ObservableProperty] private string _hlsProxyStatus = "Checking";
        [ObservableProperty] private double _hlsProxyHealth = 0;
        [ObservableProperty] private Brush _hlsProxyBrush = Brushes.Gray;
        [ObservableProperty] private string _uBlockStatus = "Checking";
        [ObservableProperty] private double _uBlockHealth = 0;
        [ObservableProperty] private Brush _uBlockBrush = Brushes.Gray;
        [ObservableProperty] private string _ffmpegStatusText = "Checking";
        [ObservableProperty] private double _ffmpegHealth = 0;
        [ObservableProperty] private Brush _ffmpegBrush = Brushes.Gray;
        [ObservableProperty] private string _qBittorrentStatus = "Optional";
        [ObservableProperty] private double _qBittorrentHealth = 35;
        [ObservableProperty] private Brush _qBittorrentBrush = Brushes.Gray;
        [ObservableProperty] private string _selectedSection = "Appearance";
        [ObservableProperty] private bool _newEpisodeAlerts = true;
        [ObservableProperty] private bool _autoSyncMal;
        [ObservableProperty] private bool _showAdultContent;
        [ObservableProperty] private bool _isDarkMode = true;
        [ObservableProperty] private string _selectedLanguage = "English";
        [ObservableProperty] private FlowDirection _appFlowDirection = FlowDirection.LeftToRight;
        [ObservableProperty] private string _uiDensity = "Comfortable";
        [ObservableProperty] private string _posterFit = "Contain";
        [ObservableProperty] private Stretch _posterStretch = Stretch.Uniform;
        [ObservableProperty] private string _accentColor = "Teal";
        [ObservableProperty] private bool _showServiceBar = true;
        [ObservableProperty] private bool _reduceMotion;
        [ObservableProperty] private bool _startMaximized = true;
        [ObservableProperty] private int _cornerRadiusPreview = 8;
        [ObservableProperty] private int _uiScalePercent = 100;
        [ObservableProperty] private double _uiScale = 1.0;
        [ObservableProperty] private string _appearancePreviewStatus = "Preview action ready.";
        [ObservableProperty] private string _malConnectionStatus = "Not connected";
        [ObservableProperty] private bool _isMalConnected;
        [ObservableProperty] private bool _isMalConnecting;
        private int _appearancePreviewRunCount;

        [ObservableProperty] private string _newDubProviderName = string.Empty;
        [ObservableProperty] private string _newDubProviderUrl = string.Empty;
        [ObservableProperty] private int _newDubProviderTimeoutSeconds = 8;
        [ObservableProperty] private bool _newDubProviderEnabled = true;
        [ObservableProperty] private string _tmdbApiKey = string.Empty;
        [ObservableProperty] private string _tmdbLanguage = "en-US";
        [ObservableProperty] private bool _otherMediaEnableInternetArchive = true;
        [ObservableProperty] private string _otherMediaProviderIndexes = string.Empty;
        [ObservableProperty] private string _googleBooksApiKey = string.Empty;
        [ObservableProperty] private string _bookCacheDirectory = string.Empty;
        [ObservableProperty] private string _annasArchiveUrl = "https://annas-archive.org";
        [ObservableProperty] private string _otherMediaScraperUrl = "https://vidsrc.to";

        public bool IsTealAccent => IsAccent("Teal");
        public bool IsPurpleAccent => IsAccent("Purple");
        public bool IsPinkAccent => IsAccent("Pink");
        public bool IsCyanAccent => IsAccent("Cyan");
        public bool IsAmberAccent => IsAccent("Amber");
        public bool IsRedAccent => IsAccent("Red");
        public bool IsCompactDensity => UiDensity.Equals("Compact", StringComparison.OrdinalIgnoreCase);
        public bool IsComfortableDensity => UiDensity.Equals("Comfortable", StringComparison.OrdinalIgnoreCase);
        public bool IsSpaciousDensity => UiDensity.Equals("Spacious", StringComparison.OrdinalIgnoreCase);
        public bool IsPosterContain => PosterFit.Equals("Contain", StringComparison.OrdinalIgnoreCase);
        public bool IsPosterCover => PosterFit.Equals("Cover", StringComparison.OrdinalIgnoreCase);
        public bool IsPosterSmartCrop => PosterFit.Equals("Smart crop", StringComparison.OrdinalIgnoreCase);
        public string MalConnectButtonText => IsMalConnecting ? "Connecting..." : IsMalConnected ? "Reconnect MAL" : "Connect MAL";

        public Helpers.ObservableRangeCollection<DubAvailabilityProviderSettingsItem> DubAvailabilityProvidersList { get; } = new();
        public Helpers.ObservableRangeCollection<EpisodeAlertHistoryEntry> EpisodeAlertHistory { get; } = new();
        public bool HasEpisodeAlertHistory => EpisodeAlertHistory.Count > 0;

        public SettingsViewModel(
            DomainHotSwapper config,
            Helpers.IDialogService dialogService,
            Helpers.IExternalLauncher externalLauncher,
            Helpers.IFolderPicker folderPicker,
            DependencyBootstrapper dependencies,
            HlsLoopbackProxy hlsProxy,
            MalOAuthService malOAuthService,
            EpisodeAlertHistoryService episodeAlertHistory,
            PythonBootstrapper python)
        {
            _config = config;
            _dialogService = dialogService;
            _externalLauncher = externalLauncher;
            _folderPicker = folderPicker;
            _dependencies = dependencies;
            _python = python;
            _python.StateChanged += ServiceStateChanged;
            _dependencies.HealthChanged += ServiceStateChanged;
            _hlsProxy = hlsProxy;
            _malOAuthService = malOAuthService;
            _episodeAlertHistory = episodeAlertHistory;
            _episodeAlertHistory.HistoryChanged += EpisodeAlertHistory_HistoryChanged;
            // Settings are local and small. Loading them on a worker thread caused
            // PropertyChanged/CollectionChanged notifications to reach WPF from the
            // wrong thread when the Settings tab opened quickly after startup.
            Load();
            RefreshEpisodeAlertHistory();
        }

        private void EpisodeAlertHistory_HistoryChanged(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(RefreshEpisodeAlertHistory);
                return;
            }

            RefreshEpisodeAlertHistory();
        }

        private void RefreshEpisodeAlertHistory()
        {
            EpisodeAlertHistory.ReplaceRange(_episodeAlertHistory.GetHistory());
            OnPropertyChanged(nameof(HasEpisodeAlertHistory));
        }

        [RelayCommand]
        private void ClearEpisodeAlertHistory()
        {
            if (!HasEpisodeAlertHistory ||
                !_dialogService.ShowConfirmDialog("Clear all saved episode alerts?", "Clear Alert History"))
            {
                return;
            }

            _episodeAlertHistory.Clear();
        }

        private void Load()
        {
            AppLogger.Log("Loading settings from configuration...");

            QbitHost     = _config.GetSetting("QBitHost");
            QbitPort     = _config.GetSetting("QBitPort");
            QbitUsername = _config.GetSetting("QBitUsername");
            QbitPassword = _config.GetSetting("QBitPassword");
            MalClientId = _config.GetSetting("MalClientId");
            MalClientSecret = _config.GetSetting("MalClientSecret");
            MalRedirectUri = _malOAuthService.RedirectUri;
            MalConnectedUsername = _malOAuthService.Username;
            MalOAuthToken = _config.GetSetting("MalOAuthToken");
            DefaultAudioPref = _config.GetSetting("DefaultAudioPref");
            AutoPlayAfterDownload = _config.GetSetting("AutoPlayAfterDownload") == "true";
            AutoManageServices    = ServiceStartupPolicy.AutoManageEnabled(_config.GetSetting("AutoManageServices"));
            ScraperSiteAttemptLimit = string.IsNullOrWhiteSpace(_config.GetSetting("ScraperSiteAttemptLimit"))
                ? "6"
                : _config.GetSetting("ScraperSiteAttemptLimit");
            ScraperSiteAttemptLimitValue = int.Parse(NormalizeScraperSiteAttemptLimit(ScraperSiteAttemptLimit));
            EnableDebugLogging    = _config.GetSetting("EnableDebugLogging") != "false";
            NewEpisodeAlerts = _config.GetSetting("NewEpisodeAlerts") != "false";
            AutoSyncMal = _config.GetSetting("AutoSyncMal") == "true";
            ShowAdultContent = _config.GetSetting("ShowAdultContent") == "true";
            IsDarkMode = _config.GetSetting("IsDarkMode") != "false";
            SelectedLanguage = string.IsNullOrWhiteSpace(_config.GetSetting("SelectedLanguage"))
                ? "English"
                : _config.GetSetting("SelectedLanguage");
            NormalizeLanguage();
            UiDensity = string.IsNullOrWhiteSpace(_config.GetSetting("UiDensity"))
                ? "Comfortable"
                : _config.GetSetting("UiDensity");
            PosterFit = string.IsNullOrWhiteSpace(_config.GetSetting("PosterFit"))
                ? "Contain"
                : _config.GetSetting("PosterFit");
            AccentColor = string.IsNullOrWhiteSpace(_config.GetSetting("AccentColor"))
                ? "Teal"
                : _config.GetSetting("AccentColor");
            ShowServiceBar = _config.GetSetting("ShowServiceBar") != "false";
            ReduceMotion = _config.GetSetting("ReduceMotion") == "true";
            StartMaximized = _config.GetSetting("StartMaximized") != "false";
            CornerRadiusPreview = int.TryParse(_config.GetSetting("CornerRadiusPreview"), out int radius)
                ? Math.Clamp(radius, 0, 16)
                : 8;
            UiScalePercent = int.TryParse(_config.GetSetting("UiScalePercent"), out int scale)
                ? Math.Clamp(scale, 90, 140)
                : 100;
            TmdbApiKey = _config.GetSetting("TmdbApiKey");
            TmdbLanguage = string.IsNullOrWhiteSpace(_config.GetSetting("TmdbLanguage"))
                ? "en-US"
                : _config.GetSetting("TmdbLanguage");
            OtherMediaEnableInternetArchive =
                _config.GetSetting("OtherMediaEnableInternetArchive") != "false";
            string providerIndexes = _config.GetSetting("OtherMediaProviderIndexes");
            OtherMediaProviderIndexes = providerIndexes == "[]" ? string.Empty : providerIndexes;
            GoogleBooksApiKey = _config.GetSetting("GoogleBooksApiKey");
            AnnasArchiveUrl = string.IsNullOrWhiteSpace(_config.GetSetting("AnnasArchiveUrl"))
                ? "https://annas-archive.org"
                : _config.GetSetting("AnnasArchiveUrl");
            OtherMediaScraperUrl = string.IsNullOrWhiteSpace(_config.GetSetting("OtherMediaScraperUrl"))
                ? "https://vidsrc.to"
                : _config.GetSetting("OtherMediaScraperUrl");
            string configuredBookCache = _config.GetSetting("BookCacheDirectory");
            BookCacheDirectory = string.IsNullOrWhiteSpace(configuredBookCache)
                ? Path.Combine(
                    UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                    "UniversalMediaOS",
                    "book_cache")
                : configuredBookCache;
            RefreshMalConnectionStatus();
            
            var dDir = _config.GetSetting("DownloadDirectory");
            DownloadDirectory = string.IsNullOrEmpty(dDir)
                ? Path.Combine(
                    UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                    "UniversalMediaOS",
                    "Downloads")
                : dDir;
            
            AppLogger.Log($"Settings loaded. QBitHost='{QbitHost}', DefaultAudioPref='{DefaultAudioPref}', EnableDebugLogging={EnableDebugLogging}");
            Helpers.ThemeRuntime.Apply(IsDarkMode, AccentColor);
            Helpers.ThemeRuntime.ApplyUiMetrics(UiDensity, CornerRadiusPreview, ReduceMotion);
            _ = RefreshLogSizeAsync();
            RefreshServiceHealth();


            var dubProviders = LoadDubAvailabilityProviders();
            AppLogger.Log($"Loading {dubProviders.Count} dub availability providers...");
            DubAvailabilityProvidersList.ReplaceRange(dubProviders);
        }

        [RelayCommand]
        private void SelectSection(string? section)
        {
            if (!string.IsNullOrWhiteSpace(section))
            {
                SelectedSection = section.Equals("Custom Sources", StringComparison.OrdinalIgnoreCase)
                    ? "Media Providers"
                    : section;
            }
        }

        partial void OnScraperSiteAttemptLimitValueChanged(int value)
        {
            int normalized = value < 0 ? 6 : value;
            if (normalized != value)
            {
                ScraperSiteAttemptLimitValue = normalized;
                return;
            }

            string text = normalized.ToString();
            if (ScraperSiteAttemptLimit != text)
            {
                ScraperSiteAttemptLimit = text;
            }
        }

        partial void OnScraperSiteAttemptLimitChanged(string value)
        {
            if (!int.TryParse(value, out int limit))
            {
                return;
            }

            if (limit < 0)
            {
                ScraperSiteAttemptLimit = "6";
                return;
            }
            if (ScraperSiteAttemptLimitValue != limit)
            {
                ScraperSiteAttemptLimitValue = limit;
            }
        }

        partial void OnUiScalePercentChanged(int value)
        {
            int normalized = Math.Clamp(value, 90, 140);
            if (normalized != value)
            {
                UiScalePercent = normalized;
                return;
            }

            UiScale = normalized / 100.0;
            DecreaseUiScaleCommand.NotifyCanExecuteChanged();
            IncreaseUiScaleCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsDarkModeChanged(bool value)
        {
            Helpers.ThemeRuntime.Apply(value, AccentColor);
        }

        partial void OnAccentColorChanged(string value)
        {
            Helpers.ThemeRuntime.Apply(IsDarkMode, value);
            RaiseAccentSelectionChanged();
        }

        partial void OnUiDensityChanged(string value)
        {
            Helpers.ThemeRuntime.ApplyUiMetrics(value, CornerRadiusPreview, ReduceMotion);
            RaiseDensitySelectionChanged();
        }

        partial void OnCornerRadiusPreviewChanged(int value)
        {
            int normalized = Math.Clamp(value, 0, 16);
            if (normalized != value)
            {
                CornerRadiusPreview = normalized;
                return;
            }

            Helpers.ThemeRuntime.ApplyUiMetrics(UiDensity, normalized, ReduceMotion);
        }

        partial void OnReduceMotionChanged(bool value)
        {
            Helpers.ThemeRuntime.ApplyUiMetrics(UiDensity, CornerRadiusPreview, value);
        }

        partial void OnStartMaximizedChanged(bool value)
        {
            ApplyCurrentWindowState(value);
        }

        partial void OnSelectedLanguageChanged(string value)
        {
            NormalizeLanguage();
        }

        partial void OnPosterFitChanged(string value)
        {
            PosterStretch = value switch
            {
                "Cover" => Stretch.UniformToFill,
                "Smart crop" => Stretch.UniformToFill,
                _ => Stretch.Uniform
            };
            RaisePosterFitSelectionChanged();
        }

        partial void OnMalOAuthTokenChanged(string value)
        {
            RefreshMalConnectionStatus(string.IsNullOrWhiteSpace(value)
                ? null
                : "Manual token entered. Save settings to store it locally, or use Connect MAL for automatic refresh.");
        }

        partial void OnMalClientIdChanged(string value)
        {
            ConnectMalCommand.NotifyCanExecuteChanged();
            RefreshMalConnectionStatus();
        }

        partial void OnIsMalConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(MalConnectButtonText));
        }

        partial void OnIsMalConnectingChanged(bool value)
        {
            OnPropertyChanged(nameof(MalConnectButtonText));
            ConnectMalCommand.NotifyCanExecuteChanged();
        }

        private bool CanDecreaseUiScale() => UiScalePercent > 90;

        private bool CanIncreaseUiScale() => UiScalePercent < 140;

        [RelayCommand(CanExecute = nameof(CanDecreaseUiScale))]
        private void DecreaseUiScale()
        {
            UiScalePercent = Math.Clamp(UiScalePercent - 5, 90, 140);
        }

        [RelayCommand]
        private void ResetUiScale()
        {
            UiScalePercent = 100;
        }

        [RelayCommand(CanExecute = nameof(CanIncreaseUiScale))]
        private void IncreaseUiScale()
        {
            UiScalePercent = Math.Clamp(UiScalePercent + 5, 90, 140);
        }

        [RelayCommand]
        private void PreviewAppearance()
        {
            _appearancePreviewRunCount++;
            AppearancePreviewStatus = $"Sample action completed ({_appearancePreviewRunCount}).";
        }

        [RelayCommand]
        private void SelectAccentColor(string? color)
        {
            if (!string.IsNullOrWhiteSpace(color))
            {
                AccentColor = color;
            }
        }

        [RelayCommand]
        private void SelectUiDensity(string? density)
        {
            if (density is "Compact" or "Comfortable" or "Spacious")
            {
                UiDensity = density;
            }
        }

        [RelayCommand]
        private void SelectPosterFit(string? fit)
        {
            if (fit is "Contain" or "Cover" or "Smart crop")
            {
                PosterFit = fit;
            }
        }

        [RelayCommand]
        private void OpenMalApiConfig()
        {
            if (!_externalLauncher.OpenUrl("https://myanimelist.net/apiconfig"))
            {
                AppLogger.Log("Unable to open MAL API config page.", "WARNING");
                _dialogService.ShowErrorDialog("Could not open the MAL API config page.", "MAL");
            }
        }

        private bool CanConnectMal()
        {
            return !IsMalConnecting && !string.IsNullOrWhiteSpace(MalClientId);
        }

        [RelayCommand(CanExecute = nameof(CanConnectMal), AllowConcurrentExecutions = false)]
        private async Task ConnectMalAsync()
        {
            if (string.IsNullOrWhiteSpace(MalClientId))
            {
                _dialogService.ShowErrorDialog("Add your MyAnimeList Client ID first.", "MyAnimeList");
                return;
            }

            _config.SetSetting("MalClientId", MalClientId.Trim());
            _config.SetSetting("MalClientSecret", MalClientSecret.Trim());
            MalRedirectUri = _malOAuthService.RedirectUri;

            IsMalConnecting = true;
            MalConnectionStatus = "Opening MyAnimeList authorization in your browser...";
            try
            {
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
                var result = await _malOAuthService.ConnectAsync(
                    uri => _externalLauncher.OpenUrl(uri.ToString()),
                    timeout.Token);

                MalOAuthToken = _config.GetSetting("MalOAuthToken");
                MalConnectedUsername = _malOAuthService.Username;
                RefreshMalConnectionStatus(result.Message);

                if (result.Success)
                {
                    _dialogService.ShowInfoDialog(result.Message, "MyAnimeList");
                }
                else
                {
                    _dialogService.ShowErrorDialog(result.Message, "MyAnimeList");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"MAL connect command failed: {ex.Message}", "WARNING");
                RefreshMalConnectionStatus($"MAL connection failed: {ex.Message}");
                _dialogService.ShowErrorDialog($"MAL connection failed: {ex.Message}", "MyAnimeList");
            }
            finally
            {
                IsMalConnecting = false;
                RefreshMalConnectionStatus();
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task RefreshMalConnectionAsync()
        {
            _config.SetSetting("MalClientId", MalClientId.Trim());
            _config.SetSetting("MalClientSecret", MalClientSecret.Trim());

            if (!_malOAuthService.HasAnyToken)
            {
                RefreshMalConnectionStatus("No MAL token is stored yet. Use Connect MAL first.");
                return;
            }

            IsMalConnecting = true;
            MalConnectionStatus = "Refreshing MyAnimeList connection...";
            try
            {
                string token = await _malOAuthService.GetValidAccessTokenAsync(forceRefresh: true);
                if (string.IsNullOrWhiteSpace(token))
                {
                    RefreshMalConnectionStatus("MAL connection is missing an access token. Use Connect MAL again.");
                    return;
                }

                string username = await _malOAuthService.RefreshConnectedUserAsync();
                MalOAuthToken = _config.GetSetting("MalOAuthToken");
                MalConnectedUsername = username;
                RefreshMalConnectionStatus(string.IsNullOrWhiteSpace(username)
                    ? "MAL token refreshed."
                    : $"MAL token refreshed for {username}.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"MAL refresh command failed: {ex.Message}", "WARNING");
                RefreshMalConnectionStatus($"MAL refresh failed: {ex.Message}");
                _dialogService.ShowErrorDialog($"MAL refresh failed: {ex.Message}", "MyAnimeList");
            }
            finally
            {
                IsMalConnecting = false;
            }
        }

        [RelayCommand]
        private void DisconnectMal()
        {
            if (!_dialogService.ShowConfirmDialog("Disconnect MyAnimeList from this app?", "MyAnimeList"))
            {
                return;
            }

            _malOAuthService.Disconnect();
            MalOAuthToken = string.Empty;
            MalConnectedUsername = string.Empty;
            RefreshMalConnectionStatus("MyAnimeList disconnected.");
        }

        private void ServiceStateChanged(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(RefreshServiceHealth));
                return;
            }
            RefreshServiceHealth();
        }

        [RelayCommand]
        private async Task RepairServicesAsync()
        {
            await Task.WhenAll(Task.Run(() => _dependencies.EnsureDependenciesAsync()), _python.EnsureScraperReadyAsync());
            RefreshServiceHealth();
        }

        [RelayCommand]
        private void RefreshServiceHealth()
        {
            var preparation = _python.Snapshot;
            bool scraperReady = _python.IsAvailable;
            string scraper = preparation.State + " (" + preparation.DiagnosticCode + ")" +
                (preparation.MissingImports.Count == 0 ? "" : ": " + string.Join(", ", preparation.MissingImports));

            bool proxyReady = _hlsProxy.IsRunning;
            string proxy = _hlsProxy.IsRunning
                ? $"Listening on {_hlsProxy.ListeningEndpoint}"
                : $"Offline{(string.IsNullOrWhiteSpace(_hlsProxy.LastStartupError) ? "" : $": {_hlsProxy.LastStartupError}")}";

            bool qbitDetected = !string.IsNullOrWhiteSpace(_dependencies.DetectedQBitPath);
            string qbit = !string.IsNullOrWhiteSpace(_dependencies.DetectedQBitPath)
                ? $"Detected ({_dependencies.DetectedQBitPath})"
                : "Not detected locally; WebUI settings will still be used";

            bool ublockReady = _dependencies.IsUBlockOriginAvailable;
            string ublock = _dependencies.UBlockOriginStatus;

            bool ffmpegReady = _dependencies.IsFfmpegAvailable;
            string ffmpeg = _dependencies.FfmpegStatus;

            ServiceHealthText =
                $"Python scraper: {scraper}\n" +
                $"HLS proxy: {proxy}\n" +
                $"uBlock Origin: {ublock}\n" +
                $"FFmpeg: {ffmpeg}\n" +
                $"qBittorrent: {qbit}";

            PythonScraperStatus = preparation.State.ToString();
            PythonScraperHealth = scraperReady ? 100 : preparation.State == PythonPreparationState.Preparing ? 40 : 0;
            PythonScraperBrush = scraperReady ? Brushes.LimeGreen : Brushes.OrangeRed;

            HlsProxyStatus = proxyReady ? "Listening" : "Offline";
            HlsProxyHealth = proxyReady ? 100 : 0;
            HlsProxyBrush = proxyReady ? Brushes.LimeGreen : Brushes.OrangeRed;

            UBlockStatus = _dependencies.IsPreparingUBlock ? "Preparing" : ublockReady ? "Installed" :
                _dependencies.UBlockOriginStatus == "Not checked." ? "Not checked" : "Unavailable";
            UBlockHealth = ublockReady ? 100 : 0;
            UBlockBrush = ublockReady ? Brushes.LimeGreen : Brushes.OrangeRed;

            FfmpegStatusText = _dependencies.IsCheckingFfmpeg ? "Checking" : ffmpegReady ? "Verified" :
                _dependencies.FfmpegStatus == "Not checked." ? "Not checked" : "Unavailable";
            FfmpegHealth = ffmpegReady ? 100 : 0;
            FfmpegBrush = ffmpegReady ? Brushes.LimeGreen : Brushes.OrangeRed;

            QBittorrentStatus = qbitDetected ? "Detected" : "Optional";
            QBittorrentHealth = qbitDetected ? 100 : 35;
            QBittorrentBrush = qbitDetected ? Brushes.LimeGreen : Brushes.Gray;

            bool requiredOk = scraperReady && proxyReady && ublockReady && ffmpegReady;
            bool preparing = preparation.State == PythonPreparationState.Preparing ||
                _dependencies.IsCheckingFfmpeg || _dependencies.IsPreparingUBlock;
            bool pending = preparation.State == PythonPreparationState.NotStarted ||
                _dependencies.FfmpegStatus == "Not checked." || _dependencies.UBlockOriginStatus == "Not checked.";
            ServiceSummaryText = preparing ? "Preparing services..." : pending ? "Service checks pending" :
                requiredOk ? "Service checks passed" : "Service attention needed";
            ServiceSummaryBrush = preparing || pending ? Brushes.Gray : requiredOk ? Brushes.LimeGreen : Brushes.OrangeRed;

            AppLogger.Log($"Service health refreshed. RequiredOk={requiredOk}; Scraper={scraperReady}; HLS={proxyReady}; uBlock={ublockReady}; FFmpeg={ffmpegReady}; qBit={qbitDetected}");
        }

        [RelayCommand]
        private void AddDubAvailabilityProvider()
        {
            AppLogger.Log($"AddDubAvailabilityProvider invoked. Name='{NewDubProviderName}', Url='{NewDubProviderUrl}'");
            if (string.IsNullOrWhiteSpace(NewDubProviderName) || string.IsNullOrWhiteSpace(NewDubProviderUrl))
            {
                AppLogger.Log("AddDubAvailabilityProvider validation failed: Name or URL is empty.", "WARNING");
                _dialogService.ShowErrorDialog("Please enter both a provider name and a provider URL.", "Validation Error");
                return;
            }

            if (!DubAvailabilityService.IsValidProviderTemplate(NewDubProviderUrl))
            {
                AppLogger.Log("AddDubAvailabilityProvider validation failed: URL must be HTTP(S).", "WARNING");
                _dialogService.ShowErrorDialog("Please enter a valid HTTP(S) provider root, search URL, or exact template.", "Validation Error");
                return;
            }

            if (DubAvailabilityProvidersList.Any(provider =>
                    provider.Name.Equals(NewDubProviderName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    provider.SuggestUrlTemplate.Equals(NewDubProviderUrl.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                AppLogger.Log("AddDubAvailabilityProvider validation failed: duplicate provider.", "WARNING");
                _dialogService.ShowErrorDialog("That dub badge provider is already in the list.", "Validation Error");
                return;
            }

            DubAvailabilityProvidersList.Add(new DubAvailabilityProviderSettingsItem
            {
                Name = NewDubProviderName.Trim(),
                SuggestUrlTemplate = NewDubProviderUrl.Trim(),
                Enabled = NewDubProviderEnabled,
                TimeoutSeconds = Math.Clamp(NewDubProviderTimeoutSeconds, 1, 30),
                ParserType = "Badge"
            });

            NewDubProviderName = string.Empty;
            NewDubProviderUrl = string.Empty;
            NewDubProviderTimeoutSeconds = 8;
            NewDubProviderEnabled = true;
        }

        [RelayCommand]
        private void RemoveDubAvailabilityProvider(DubAvailabilityProviderSettingsItem provider)
        {
            if (provider != null)
            {
                AppLogger.Log($"RemoveDubAvailabilityProvider invoked for provider: Name='{provider.Name}', Url='{provider.SuggestUrlTemplate}'");
                DubAvailabilityProvidersList.Remove(provider);
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task SaveAsync()
        {
            AppLogger.Log("Saving settings to configuration file...");

            if (!string.IsNullOrWhiteSpace(QbitPort) &&
                (!int.TryParse(QbitPort, out int parsedPort) || parsedPort < 1 || parsedPort > 65535))
            {
                AppLogger.Log($"Save validation failed: invalid qBittorrent port '{QbitPort}'.", "WARNING");
                _dialogService.ShowErrorDialog("qBittorrent port must be a number between 1 and 65535.", "Validation Error");
                return;
            }
            
            var host = QbitHost;
            var port = QbitPort;
            var user = QbitUsername;
            var pass = QbitPassword;
            var malClientId = MalClientId.Trim();
            var malClientSecret = MalClientSecret.Trim();
            var oauth = MalOAuthToken;
            var hasManagedMalConnection = _malOAuthService.HasRefreshToken;
            var audio = DefaultAudioPref;
            var autoplay = AutoPlayAfterDownload;
            var automanage = AutoManageServices;
            var scraperLimit = NormalizeScraperSiteAttemptLimit(ScraperSiteAttemptLimit);
            var debug = EnableDebugLogging;
            var dir = DownloadDirectory;
            var newEpisodeAlerts = NewEpisodeAlerts;
            var autoSyncMal = AutoSyncMal;
            var showAdultContent = ShowAdultContent;
            var isDarkMode = IsDarkMode;
            var selectedLanguage = SelectedLanguage;
            var uiScalePercent = Math.Clamp(UiScalePercent, 90, 140).ToString();
            var uiDensity = UiDensity;
            var posterFit = PosterFit;
            var accentColor = AccentColor;
            var showServiceBar = ShowServiceBar;
            var reduceMotion = ReduceMotion;
            var startMaximized = StartMaximized;
            var cornerRadiusPreview = Math.Clamp(CornerRadiusPreview, 0, 16).ToString();
            var tmdbApiKey = TmdbApiKey.Trim();
            var tmdbLanguage = string.IsNullOrWhiteSpace(TmdbLanguage) ? "en-US" : TmdbLanguage.Trim();
            var otherMediaEnableInternetArchive = OtherMediaEnableInternetArchive;
            var otherMediaProviderIndexes = OtherMediaProviderIndexes.Trim();
            var googleBooksApiKey = GoogleBooksApiKey.Trim();
            var annasArchiveUrl = AnnasArchiveUrl.Trim();
            var otherMediaScraperUrl = OtherMediaScraperUrl.Trim();
            string bookCacheDirectory;
            try
            {
                string requestedBookCache = BookCacheDirectory.Trim();
                bookCacheDirectory = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(requestedBookCache)
                        ? Path.Combine(
                            UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                            "UniversalMediaOS",
                            "book_cache")
                        : requestedBookCache);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _dialogService.ShowErrorDialog(
                    "The book cache directory is not a valid local path.",
                    "Validation Error");
                return;
            }
            var dubProviders = DubAvailabilityProvidersList
                .Select(provider => provider.ToConfig())
                .ToList();

            var settings = new Dictionary<string, string>
            {
                ["QBitHost"] = host,
                ["QBitPort"] = port,
                ["QBitUsername"] = user,
                ["QBitPassword"] = pass,
                ["MalClientId"] = malClientId,
                ["MalClientSecret"] = malClientSecret,
                ["DefaultAudioPref"] = audio,
                ["AutoPlayAfterDownload"] = autoplay ? "true" : "false",
                ["AutoManageServices"] = automanage ? "true" : "false",
                ["ScraperSiteAttemptLimit"] = scraperLimit,
                ["EnableDebugLogging"] = debug ? "true" : "false",
                ["DownloadDirectory"] = dir,
                ["NewEpisodeAlerts"] = newEpisodeAlerts ? "true" : "false",
                ["AutoSyncMal"] = autoSyncMal ? "true" : "false",
                ["ShowAdultContent"] = showAdultContent ? "true" : "false",
                ["IsDarkMode"] = isDarkMode ? "true" : "false",
                ["SelectedLanguage"] = selectedLanguage,
                ["UiScalePercent"] = uiScalePercent,
                ["UiDensity"] = uiDensity,
                ["PosterFit"] = posterFit,
                ["AccentColor"] = accentColor,
                ["ShowServiceBar"] = showServiceBar ? "true" : "false",
                ["ReduceMotion"] = reduceMotion ? "true" : "false",
                ["StartMaximized"] = startMaximized ? "true" : "false",
                ["CornerRadiusPreview"] = cornerRadiusPreview,
                ["TmdbApiKey"] = tmdbApiKey,
                ["TmdbLanguage"] = tmdbLanguage,
                ["OtherMediaEnableInternetArchive"] = otherMediaEnableInternetArchive ? "true" : "false",
                ["OtherMediaProviderIndexes"] = otherMediaProviderIndexes,
                ["GoogleBooksApiKey"] = googleBooksApiKey,
                ["AnnasArchiveUrl"] = annasArchiveUrl,
                ["OtherMediaScraperUrl"] = otherMediaScraperUrl,
                ["BookCacheDirectory"] = bookCacheDirectory,
                [DubAvailabilityService.ProviderConfigKey] = JsonSerializer.Serialize(dubProviders)
            };
            if (!hasManagedMalConnection)
            {
                settings["MalOAuthToken"] = oauth;
            }

            bool settingsSaved = await Task.Run(() => _config.SetSettings(settings));
            if (!settingsSaved)
            {
                _dialogService.ShowErrorDialog("Settings could not be written. Check the application log and try again.", "Save Error");
                return;
            }

            AppLogger.IsEnabled = EnableDebugLogging;
            Helpers.ThemeRuntime.Apply(IsDarkMode, AccentColor);
            Helpers.ThemeRuntime.ApplyUiMetrics(UiDensity, CornerRadiusPreview, ReduceMotion);
            ScraperSiteAttemptLimit = scraperLimit;
            ScraperSiteAttemptLimitValue = int.Parse(scraperLimit);
            UiScalePercent = int.Parse(uiScalePercent);
            MalOAuthToken = _config.GetSetting("MalOAuthToken");
            RefreshMalConnectionStatus();
            await RefreshLogSizeAsync();

            AppLogger.Log("Settings saved successfully.");
            _dialogService.ShowInfoDialog(
                "Settings saved. Reopen any Movie, TV, Cartoon, or Book tabs to apply provider changes.",
                "Settings Saved");
        }

        private void RefreshMalConnectionStatus(string? overrideStatus = null)
        {
            MalRedirectUri = _malOAuthService.RedirectUri;
            MalConnectedUsername = _malOAuthService.Username;
            IsMalConnected = _malOAuthService.HasRefreshToken;

            if (!string.IsNullOrWhiteSpace(overrideStatus))
            {
                MalConnectionStatus = overrideStatus;
                return;
            }

            if (string.IsNullOrWhiteSpace(MalClientId))
            {
                MalConnectionStatus = $"Add a MyAnimeList Client ID, register redirect URI {MalRedirectUri}, then press Connect MAL.";
                return;
            }

            if (IsMalConnected)
            {
                string account = string.IsNullOrWhiteSpace(MalConnectedUsername)
                    ? "your MAL account"
                    : MalConnectedUsername;
                MalConnectionStatus = $"Connected to {account}. Tokens refresh automatically for status, progress, and VA Detect library sync.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(MalOAuthToken))
            {
                MalConnectionStatus = "Manual access token stored. This can work temporarily, but Connect MAL is recommended because manual tokens expire.";
                return;
            }

            MalConnectionStatus = $"Ready to connect. Make sure your MAL API app lists this redirect URI exactly: {MalRedirectUri}";
        }

        private static string NormalizeScraperSiteAttemptLimit(string value)
        {
            if (!int.TryParse(value, out int limit))
                limit = 6;

            return (limit < 0 ? 6 : limit).ToString();
        }

        private List<DubAvailabilityProviderSettingsItem> LoadDubAvailabilityProviders()
        {
            string raw = _config.GetSetting(DubAvailabilityService.ProviderConfigKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<DubAvailabilityProviderSettingsItem>();
            }

            try
            {
                var providers = JsonSerializer.Deserialize<List<DubAvailabilityProviderConfig>>(
                    raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<DubAvailabilityProviderConfig>();

                return providers
                    .Where(provider => !string.IsNullOrWhiteSpace(provider.Name) &&
                                       DubAvailabilityService.IsValidProviderTemplate(provider.SuggestUrlTemplate))
                    .Select(DubAvailabilityProviderSettingsItem.FromConfig)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load dub availability providers: {ex.Message}", "WARNING");
                return new List<DubAvailabilityProviderSettingsItem>();
            }
        }

        private void NormalizeLanguage()
        {
            string normalized = SelectedLanguage.Equals("Arabic", StringComparison.OrdinalIgnoreCase)
                ? "Arabic"
                : "English";

            if (SelectedLanguage != normalized)
            {
                SelectedLanguage = normalized;
                return;
            }

            AppFlowDirection = normalized == "Arabic"
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            Helpers.LocalizationRuntime.SetLanguage(normalized);
        }

        private bool IsAccent(string name)
        {
            return AccentColor.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseAccentSelectionChanged()
        {
            OnPropertyChanged(nameof(IsTealAccent));
            OnPropertyChanged(nameof(IsPurpleAccent));
            OnPropertyChanged(nameof(IsPinkAccent));
            OnPropertyChanged(nameof(IsCyanAccent));
            OnPropertyChanged(nameof(IsAmberAccent));
            OnPropertyChanged(nameof(IsRedAccent));
        }

        private void RaiseDensitySelectionChanged()
        {
            OnPropertyChanged(nameof(IsCompactDensity));
            OnPropertyChanged(nameof(IsComfortableDensity));
            OnPropertyChanged(nameof(IsSpaciousDensity));
        }

        private void RaisePosterFitSelectionChanged()
        {
            OnPropertyChanged(nameof(IsPosterContain));
            OnPropertyChanged(nameof(IsPosterCover));
            OnPropertyChanged(nameof(IsPosterSmartCrop));
        }

        private static void ApplyCurrentWindowState(bool startMaximized)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => ApplyCurrentWindowState(startMaximized));
                return;
            }

            var window = Application.Current?.MainWindow;
            if (window == null || !window.IsLoaded)
            {
                return;
            }

            window.WindowState = startMaximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task ClearLogAsync()
        {
            AppLogger.Log("ClearLog invoked by user.");
            if (!_dialogService.ShowConfirmDialog("Clear the current debug log file? This cannot be undone.", "Clear Debug Log"))
            {
                AppLogger.Log("ClearLog cancelled by user.");
                return;
            }

            await Task.Run(() => AppLogger.ClearLog());
            await RefreshLogSizeAsync();
            _dialogService.ShowInfoDialog("Debug log file cleared successfully.", "Logs");
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task RefreshLogSizeAsync()
        {
            var sizeText = await Task.Run(() => AppLogger.GetLogFileSize());
            LogFileSizeText = sizeText;
            AppLogger.Log($"Log file size refreshed: {LogFileSizeText}");
        }

        [RelayCommand]
        private void BrowseDownloadDirectory()
        {
            string? selectedFolder = _folderPicker.PickFolder("Select Download Directory", DownloadDirectory);
            if (!string.IsNullOrWhiteSpace(selectedFolder))
            {
                DownloadDirectory = selectedFolder;
                AppLogger.Log($"User selected download directory: '{DownloadDirectory}'");
            }
        }
    }

    public partial class DubAvailabilityProviderSettingsItem : ObservableObject
    {
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _suggestUrlTemplate = string.Empty;
        [ObservableProperty] private bool _enabled = true;
        [ObservableProperty] private int _timeoutSeconds = 8;
        [ObservableProperty] private string _parserType = "Badge";
        [ObservableProperty] private string _adapterType = string.Empty;

        partial void OnTimeoutSecondsChanged(int value)
        {
            int normalized = Math.Clamp(value, 1, 30);
            if (normalized != value)
            {
                TimeoutSeconds = normalized;
            }
        }

        public DubAvailabilityProviderConfig ToConfig()
        {
            return new DubAvailabilityProviderConfig
            {
                Name = Name.Trim(),
                SuggestUrlTemplate = SuggestUrlTemplate.Trim(),
                Enabled = Enabled,
                TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 30),
                ParserType = string.IsNullOrWhiteSpace(ParserType) ? "Badge" : ParserType.Trim(),
                AdapterType = AdapterType.Trim()
            };
        }

        public static DubAvailabilityProviderSettingsItem FromConfig(DubAvailabilityProviderConfig config)
        {
            return new DubAvailabilityProviderSettingsItem
            {
                Name = config.Name.Trim(),
                SuggestUrlTemplate = config.SuggestUrlTemplate.Trim(),
                Enabled = config.Enabled,
                TimeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? 8 : config.TimeoutSeconds, 1, 30),
                ParserType = string.IsNullOrWhiteSpace(config.ParserType) ? "Badge" : config.ParserType.Trim(),
                AdapterType = config.AdapterType.Trim()
            };
        }
    }
}
