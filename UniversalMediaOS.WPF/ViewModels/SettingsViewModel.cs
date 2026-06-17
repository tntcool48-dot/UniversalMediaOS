using System;
using System.IO;
using System.Windows;
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
        private readonly DependencyBootstrapper _dependencies;
        private readonly HlsLoopbackProxy _hlsProxy;

        [ObservableProperty] private string _qbitHost = string.Empty;
        [ObservableProperty] private string _qbitPort = string.Empty;
        [ObservableProperty] private string _qbitUsername = string.Empty;
        [ObservableProperty] private string _qbitPassword = string.Empty;
        [ObservableProperty] private string _malOAuthToken = string.Empty;
        [ObservableProperty] private string _defaultAudioPref = "Sub";
        [ObservableProperty] private bool _autoPlayAfterDownload;
        [ObservableProperty] private bool _autoManageServices;
        [ObservableProperty] private string _scraperSiteAttemptLimit = "6";
        [ObservableProperty] private bool _enableDebugLogging;
        [ObservableProperty] private string _logFileSizeText = "0 KB";
        [ObservableProperty] private string _downloadDirectory = string.Empty;
        [ObservableProperty] private string _serviceHealthText = "Checking services...";

        [ObservableProperty] private string _newSourceName = string.Empty;
        [ObservableProperty] private string _newSourceUrl = string.Empty;

        public Helpers.ObservableRangeCollection<CustomSource> CustomSourcesList { get; } = new();

        public SettingsViewModel(
            DomainHotSwapper config,
            Helpers.IDialogService dialogService,
            DependencyBootstrapper dependencies,
            HlsLoopbackProxy hlsProxy)
        {
            _config = config;
            _dialogService = dialogService;
            _dependencies = dependencies;
            _hlsProxy = hlsProxy;
            _ = Task.Run(() => Load());
        }

        private void Load()
        {
            AppLogger.Log("Loading settings from configuration...");

            QbitHost     = _config.GetSetting("QBitHost");
            QbitPort     = _config.GetSetting("QBitPort");
            QbitUsername = _config.GetSetting("QBitUsername");
            QbitPassword = _config.GetSetting("QBitPassword");
            MalOAuthToken = _config.GetSetting("MalOAuthToken");
            DefaultAudioPref = _config.GetSetting("DefaultAudioPref");
            AutoPlayAfterDownload = _config.GetSetting("AutoPlayAfterDownload") == "true";
            AutoManageServices    = _config.GetSetting("AutoManageServices") != "false";
            ScraperSiteAttemptLimit = string.IsNullOrWhiteSpace(_config.GetSetting("ScraperSiteAttemptLimit"))
                ? "6"
                : _config.GetSetting("ScraperSiteAttemptLimit");
            EnableDebugLogging    = _config.GetSetting("EnableDebugLogging") != "false";
            
            var dDir = _config.GetSetting("DownloadDirectory");
            DownloadDirectory = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;
            
            AppLogger.Log($"Settings loaded. QBitHost='{QbitHost}', DefaultAudioPref='{DefaultAudioPref}', EnableDebugLogging={EnableDebugLogging}");
            _ = RefreshLogSizeAsync();
            RefreshServiceHealth();

            var list = _config.GetCustomSources();
            AppLogger.Log($"Loading {list.Count} custom sources...");
            CustomSourcesList.ReplaceRange(list);
        }

        [RelayCommand]
        private void RefreshServiceHealth()
        {
            string scraperPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniversalMediaOS", "Services", "scraper.py");

            string scraper = File.Exists(scraperPath)
                ? $"Ready ({scraperPath})"
                : "Not deployed yet";

            string proxy = _hlsProxy.IsRunning
                ? "Listening on 127.0.0.1:19475"
                : $"Offline{(string.IsNullOrWhiteSpace(_hlsProxy.LastStartupError) ? "" : $": {_hlsProxy.LastStartupError}")}";

            string qbit = !string.IsNullOrWhiteSpace(_dependencies.DetectedQBitPath)
                ? $"Detected ({_dependencies.DetectedQBitPath})"
                : "Not detected locally; WebUI settings will still be used";

            string ublock = _dependencies.IsUBlockOriginAvailable
                ? _dependencies.UBlockOriginStatus
                : $"Unavailable: {_dependencies.UBlockOriginStatus}";

            string ffmpeg = _dependencies.IsFfmpegAvailable
                ? _dependencies.FfmpegStatus
                : $"Warning: {_dependencies.FfmpegStatus}";

            ServiceHealthText =
                $"Python scraper: {scraper}\n" +
                $"HLS proxy: {proxy}\n" +
                $"uBlock Origin: {ublock}\n" +
                $"FFmpeg: {ffmpeg}\n" +
                $"qBittorrent: {qbit}";
        }

        [RelayCommand]
        private void AddSource()
        {
            AppLogger.Log($"AddSource invoked. NewSourceName='{NewSourceName}', NewSourceUrl='{NewSourceUrl}'");
            if (string.IsNullOrWhiteSpace(NewSourceName) || string.IsNullOrWhiteSpace(NewSourceUrl))
            {
                AppLogger.Log("AddSource validation failed: Name or URL is empty.", "WARNING");
                _dialogService.ShowErrorDialog("Please enter both a name and a search URL.", "Validation Error");
                return;
            }

            CustomSourcesList.Add(new CustomSource { Name = NewSourceName, Url = NewSourceUrl });
            AppLogger.Log($"Successfully added custom source: Name='{NewSourceName}', Url='{NewSourceUrl}'");
            NewSourceName = string.Empty;
            NewSourceUrl = string.Empty;
        }

        [RelayCommand]
        private void RemoveSource(CustomSource source)
        {
            if (source != null)
            {
                AppLogger.Log($"RemoveSource invoked for source: Name='{source.Name}', Url='{source.Url}'");
                CustomSourcesList.Remove(source);
                AppLogger.Log($"Removed custom source: Name='{source.Name}'");
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task SaveAsync()
        {
            AppLogger.Log("Saving settings to configuration file...");
            
            var host = QbitHost;
            var port = QbitPort;
            var user = QbitUsername;
            var pass = QbitPassword;
            var oauth = MalOAuthToken;
            var audio = DefaultAudioPref;
            var autoplay = AutoPlayAfterDownload;
            var automanage = AutoManageServices;
            var scraperLimit = NormalizeScraperSiteAttemptLimit(ScraperSiteAttemptLimit);
            var debug = EnableDebugLogging;
            var dir = DownloadDirectory;
            var list = System.Linq.Enumerable.ToList(CustomSourcesList);

            await Task.Run(() =>
            {
                _config.SetSetting("QBitHost",               host);
                _config.SetSetting("QBitPort",               port);
                _config.SetSetting("QBitUsername",           user);
                _config.SetSetting("QBitPassword",           pass);
                _config.SetSetting("MalOAuthToken",          oauth);
                _config.SetSetting("DefaultAudioPref",       audio);
                _config.SetSetting("AutoPlayAfterDownload",  autoplay ? "true" : "false");
                _config.SetSetting("AutoManageServices",     automanage ? "true" : "false");
                _config.SetSetting("ScraperSiteAttemptLimit", scraperLimit);
                _config.SetSetting("EnableDebugLogging",     debug ? "true" : "false");
                _config.SetSetting("DownloadDirectory",      dir);
                _config.SaveCustomSources(list);
            });

            AppLogger.IsEnabled = EnableDebugLogging;
            ScraperSiteAttemptLimit = scraperLimit;
            await RefreshLogSizeAsync();

            AppLogger.Log("Settings saved successfully.");
            _dialogService.ShowInfoDialog("Settings saved.", "Settings Saved");
        }

        private static string NormalizeScraperSiteAttemptLimit(string value)
        {
            if (!int.TryParse(value, out int limit))
                limit = 6;

            return Math.Clamp(limit, 1, 30).ToString();
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task ClearLogAsync()
        {
            AppLogger.Log("ClearLog invoked by user.");
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
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select Download Directory",
                    InitialDirectory = DownloadDirectory
                };
                if (dialog.ShowDialog() == true)
                {
                    DownloadDirectory = dialog.FolderName;
                    AppLogger.Log($"User selected download directory: '{DownloadDirectory}'");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error showing folder browser dialog: {ex.Message}", "ERROR");
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select any file in target download directory",
                    CheckFileExists = false,
                    FileName = "Folder Selection"
                };
                if (dialog.ShowDialog() == true)
                {
                    DownloadDirectory = Path.GetDirectoryName(dialog.FileName) ?? DownloadDirectory;
                    AppLogger.Log($"User selected fallback download directory: '{DownloadDirectory}'");
                }
            }
        }
    }
}
