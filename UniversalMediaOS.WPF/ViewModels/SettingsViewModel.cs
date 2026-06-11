using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Configuration;

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

        public SettingsViewModel(DomainHotSwapper config)
        {
            _config = config;
            Load();
        }

        private void Load()
        {
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
            AutoManageServices    = _config.GetSetting("AutoManageServices") == "true";
        }

        [RelayCommand]
        private void Save()
        {
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

            MessageBox.Show("Settings saved. Restart the app for service changes to take effect.", 
                            "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
