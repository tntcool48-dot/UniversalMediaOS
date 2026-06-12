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

        public System.Collections.ObjectModel.ObservableCollection<CustomSource> CustomSourcesList { get; } = new();

        public SettingsViewModel(DomainHotSwapper config)
        {
            _config = config;
            Load();
        }

        private void Load()
        {
            AppLogger.Log("Loading settings from configuration...");
            ConsumetApiBase = _config.GetSetting("ConsumetApiBase");
            if (string.IsNullOrEmpty(ConsumetApiBase)) ConsumetApiBase = "http://localhost:3000";

            ConsumetProvider = _config.GetSetting("ConsumetProvider");
            if (string.IsNullOrEmpty(ConsumetProvider)) ConsumetProvider = "gogoanime";

            QbitHost     = _config.GetSetting("QBitHost");
            QbitPort     = _config.GetSetting("QBitPort");
            QbitUsername = _config.GetSetting("QBitUsername");
            QbitPassword = _config.GetSetting("QBitPassword");
            MalOAuthToken = _config.GetSetting("MalOAuthToken");
            DefaultAudioPref = _config.GetSetting("DefaultAudioPref");
            AutoPlayAfterDownload = _config.GetSetting("AutoPlayAfterDownload") == "true";
            EnableDebugLogging    = _config.GetSetting("EnableDebugLogging") != "false";
            DownloadDirectory     = _config.GetSetting("DownloadDirectory");
            if (string.IsNullOrEmpty(DownloadDirectory)) DownloadDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            
            AppLogger.Log($"Settings loaded. ConsumetBase='{ConsumetApiBase}', ConsumetProvider='{ConsumetProvider}', QBitHost='{QbitHost}', DefaultAudioPref='{DefaultAudioPref}', EnableDebugLogging={EnableDebugLogging}");
            RefreshLogSize();

            CustomSourcesList.Clear();
            var list = _config.GetCustomSources();
            AppLogger.Log($"Loading {list.Count} custom sources...");
            foreach (var src in list)
            {
                CustomSourcesList.Add(src);
                AppLogger.Log($"Loaded custom source: Name='{src.Name}', Url='{src.Url}'");
            }
        }

        [RelayCommand]
        private void AddSource()
        {
            AppLogger.Log($"AddSource invoked. NewSourceName='{NewSourceName}', NewSourceUrl='{NewSourceUrl}'");
            if (string.IsNullOrWhiteSpace(NewSourceName) || string.IsNullOrWhiteSpace(NewSourceUrl))
            {
                AppLogger.Log("AddSource validation failed: Name or URL is empty.", "WARNING");
                MessageBox.Show("Please enter both a name and a search URL.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        [RelayCommand]
        private void Save()
        {
            AppLogger.Log("Saving settings to configuration file...");
            _config.SetSetting("ConsumetApiBase",        ConsumetApiBase);
            _config.SetSetting("ConsumetProvider",       ConsumetProvider);
            _config.SetSetting("QBitHost",               QbitHost);
            _config.SetSetting("QBitPort",               QbitPort);
            _config.SetSetting("QBitUsername",           QbitUsername);
            _config.SetSetting("QBitPassword",           QbitPassword);
            _config.SetSetting("MalOAuthToken",          MalOAuthToken);
            _config.SetSetting("DefaultAudioPref",       DefaultAudioPref);
            _config.SetSetting("AutoPlayAfterDownload",  AutoPlayAfterDownload ? "true" : "false");
            _config.SetSetting("AutoManageServices",     AutoManageServices ? "true" : "false");
            _config.SetSetting("EnableDebugLogging",     EnableDebugLogging ? "true" : "false");
            _config.SetSetting("DownloadDirectory",      DownloadDirectory);
            
            AppLogger.IsEnabled = EnableDebugLogging;
            AppLogger.Log($"AppLogger.IsEnabled set to {EnableDebugLogging}");

            var list = new System.Collections.Generic.List<CustomSource>();
            foreach (var src in CustomSourcesList)
            {
                list.Add(src);
            }
            _config.SaveCustomSources(list);
            AppLogger.Log($"Saved {list.Count} custom sources to config.");

            RefreshLogSize();

            AppLogger.Log("Settings saved successfully.");
            MessageBox.Show("Settings saved. Restart the app for service changes to take effect.", 
                            "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private void ClearLog()
        {
            AppLogger.Log("ClearLog invoked by user.");
            AppLogger.ClearLog();
            RefreshLogSize();
            MessageBox.Show("Debug log file cleared successfully.", "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private void RefreshLogSize()
        {
            LogFileSizeText = AppLogger.GetLogFileSize();
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
