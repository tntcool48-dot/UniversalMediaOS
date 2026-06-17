using System.Windows.Controls;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF.Views
{
    public partial class SettingsView : UserControl
    {
        private bool _syncingPassword;
        private SettingsViewModel? _currentViewModel;

        public SettingsView()
        {
            InitializeComponent();
            DataContextChanged += SettingsView_DataContextChanged;
        }

        private void SettingsView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (_currentViewModel != null)
                _currentViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;

            _currentViewModel = e.NewValue as SettingsViewModel;
            if (_currentViewModel != null)
            {
                _currentViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
                SyncPasswordFromViewModel();
            }
        }

        private void SettingsViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.QbitPassword))
                Dispatcher.Invoke(SyncPasswordFromViewModel);
        }

        private void SyncPasswordFromViewModel()
        {
            if (_currentViewModel == null || QbitPasswordBox.Password == _currentViewModel.QbitPassword)
                return;

            _syncingPassword = true;
            try
            {
                QbitPasswordBox.Password = _currentViewModel.QbitPassword;
            }
            finally
            {
                _syncingPassword = false;
            }
        }

        private void QbitPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_syncingPassword || _currentViewModel == null)
                return;

            _currentViewModel.QbitPassword = QbitPasswordBox.Password;
        }
    }
}
