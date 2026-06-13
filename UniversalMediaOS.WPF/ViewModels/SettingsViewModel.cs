using System;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly DomainHotSwapper _config;
        private readonly Helpers.IDialogService _dialogService;

        [ObservableProperty] private string _consumetApiBase = string.Empty;
        [ObservableProperty] private string _consumetProvider = string.Empty;
        [ObservableProperty] private string _qbitHost = string.Empty;
        [ObservableProperty] private string _qbitPort = string.Empty;
        [ObservableProperty] private string _qbitUsername = string.Empty;
        [ObservableProperty] private string _qbitPassword = string.Empty;
        [ObservableProperty] private string _malOAuthToken = string.Empty;
        [ObservableProperty] private string _defaultAudioPref = "Sub";
        [ObservableProperty] private bool _autoPlayAfterDownload;
        [ObservableProperty] private bool _autoManageServices;
        [ObservableProperty] private bool _enableDebugLogging;
        [ObservableProperty] private string _logFileSizeText = "0 KB";
        [ObservableProperty] private string _downloadDirectory = string.Empty;

        [ObservableProperty] private string _newSourceName = string.Empty;
        [ObservableProperty] private string _newSourceUrl = string.Empty;

        public Helpers.ObservableRangeCollection<CustomSource> CustomSourcesList { get; } = new();

        public SettingsViewModel(DomainHotSwapper config, Helpers.IDialogService dialogService)
        {
            _config = config;
            _dialogService = dialogService;
            
            // Run load on a background thread to prevent blocking the UI thread on startup
            _ = Task.Run(() => Load());
        }

        private void Load()
        {
            AppLogger.Log("Loading settings from configuration...");
            var baseApi = _config.GetSetting("ConsumetApiBase");
            ConsumetApiBase = string.IsNullOrEmpty(baseApi) ? "http://localhost:3000" : baseApi;

            var provider = _config.GetSetting("ConsumetProvider");
            ConsumetProvider = string.IsNullOrEmpty(provider) ? "gogoanime" : provider;

            QbitHost     = _config.GetSetting("QBitHost");
            QbitPort     = _config.GetSetting("QBitPort");
            QbitUsername = _config.GetSetting("QBitUsername");
            QbitPassword = _config.GetSetting("QBitPassword");
            MalOAuthToken = _config.GetSetting("MalOAuthToken");
            DefaultAudioPref = _config.GetSetting("DefaultAudioPref");
            AutoPlayAfterDownload = _config.GetSetting("AutoPlayAfterDownload") == "true";
            EnableDebugLogging    = _config.GetSetting("EnableDebugLogging") != "false";
            
            var dDir = _config.GetSetting("DownloadDirectory");
            DownloadDirectory = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;
            
            AppLogger.Log($"Settings loaded. ConsumetBase='{ConsumetApiBase}', ConsumetProvider='{ConsumetProvider}', QBitHost='{QbitHost}', DefaultAudioPref='{DefaultAudioPref}', EnableDebugLogging={EnableDebugLogging}");
            _ = RefreshLogSizeAsync();

            var list = _config.GetCustomSources();
            AppLogger.Log($"Loading {list.Count} custom sources...");
            CustomSourcesList.ReplaceRange(list);
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
            
            var baseApi = ConsumetApiBase;
            var provider = ConsumetProvider;
            var host = QbitHost;
            var port = QbitPort;
            var user = QbitUsername;
            var pass = QbitPassword;
            var oauth = MalOAuthToken;
            var audio = DefaultAudioPref;
            var autoplay = AutoPlayAfterDownload;
            var automanage = AutoManageServices;
            var debug = EnableDebugLogging;
            var dir = DownloadDirectory;
            var list = System.Linq.Enumerable.ToList(CustomSourcesList);

            await Task.Run(() =>
            {
                _config.SetSetting("ConsumetApiBase",        baseApi);
                _config.SetSetting("ConsumetProvider",       provider);
                _config.SetSetting("QBitHost",               host);
                _config.SetSetting("QBitPort",               port);
                _config.SetSetting("QBitUsername",           user);
                _config.SetSetting("QBitPassword",           pass);
                _config.SetSetting("MalOAuthToken",          oauth);
                _config.SetSetting("DefaultAudioPref",       audio);
                _config.SetSetting("AutoPlayAfterDownload",  autoplay ? "true" : "false");
                _config.SetSetting("AutoManageServices",     automanage ? "true" : "false");
                _config.SetSetting("EnableDebugLogging",     debug ? "true" : "false");
                _config.SetSetting("DownloadDirectory",      dir);
                _config.SaveCustomSources(list);
            });
            
            AppLogger.IsEnabled = EnableDebugLogging;
            await RefreshLogSizeAsync();

            AppLogger.Log("Settings saved successfully.");
            _dialogService.ShowInfoDialog("Settings saved. Restart the app for service changes to take effect.", "Settings Saved");
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
