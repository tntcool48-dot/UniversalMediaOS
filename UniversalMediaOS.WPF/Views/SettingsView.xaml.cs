using System.Windows.Controls;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF.Views
{
    public partial class SettingsView : UserControl
    {
        private bool _syncingPassword;
        private bool _isLoaded;
        private SettingsViewModel? _currentViewModel;

        public SettingsView()
        {
            InitializeComponent();
            DataContextChanged += SettingsView_DataContextChanged;
            Loaded += SettingsView_Loaded;
            Unloaded += SettingsView_Unloaded;
        }

        private void SettingsView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            DetachViewModel();
            if (_isLoaded && e.NewValue is SettingsViewModel viewModel)
            {
                AttachViewModel(viewModel);
            }
        }

        private void SettingsView_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _isLoaded = true;
            if (_currentViewModel == null && DataContext is SettingsViewModel viewModel)
            {
                AttachViewModel(viewModel);
            }
        }

        private void SettingsView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _isLoaded = false;
            DetachViewModel();
        }

        private void AttachViewModel(SettingsViewModel viewModel)
        {
            if (ReferenceEquals(_currentViewModel, viewModel))
            {
                return;
            }

            DetachViewModel();
            _currentViewModel = viewModel;
            _currentViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
            SyncPasswordFromViewModel();
        }

        private void DetachViewModel()
        {
            if (_currentViewModel == null)
            {
                return;
            }

            _currentViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
            _currentViewModel = null;
        }

        private void SettingsViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SettingsViewModel.QbitPassword) or
                nameof(SettingsViewModel.TmdbApiKey) or
                nameof(SettingsViewModel.GoogleBooksApiKey))
                Dispatcher.Invoke(SyncPasswordFromViewModel);
        }

        private void SyncPasswordFromViewModel()
        {
            if (_currentViewModel == null)
                return;

            _syncingPassword = true;
            try
            {
                if (QbitPasswordBox.Password != _currentViewModel.QbitPassword)
                    QbitPasswordBox.Password = _currentViewModel.QbitPassword;
                if (TmdbApiKeyBox.Password != _currentViewModel.TmdbApiKey)
                    TmdbApiKeyBox.Password = _currentViewModel.TmdbApiKey;
                if (GoogleBooksApiKeyBox.Password != _currentViewModel.GoogleBooksApiKey)
                    GoogleBooksApiKeyBox.Password = _currentViewModel.GoogleBooksApiKey;
            }
            finally
            {
                _syncingPassword = false;
            }
        }

        /// <summary>Delete removes its own focused row. Runs before the command; afterwards focus moves to the row that
        /// takes its place (or the last one), or to the add form when none is left, instead of falling to the pane.</summary>
        private void DubProviderDelete_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is not Button { IsKeyboardFocused: true } button) return;
            int index = DubProvidersList.Items.IndexOf(button.DataContext);
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                int remaining = DubProvidersList.Items.Count;
                if (remaining == 0) { NewDubProviderNameBox.Focus(); return; }
                if (DubProvidersList.ItemContainerGenerator.ContainerFromIndex(System.Math.Clamp(index, 0, remaining - 1))
                    is System.Windows.UIElement row)
                    row.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void QbitPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_syncingPassword || _currentViewModel == null)
                return;

            _currentViewModel.QbitPassword = QbitPasswordBox.Password;
        }

        private void TmdbApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_syncingPassword || _currentViewModel == null)
                return;

            _currentViewModel.TmdbApiKey = TmdbApiKeyBox.Password;
        }

        private void GoogleBooksApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_syncingPassword || _currentViewModel == null)
                return;

            _currentViewModel.GoogleBooksApiKey = GoogleBooksApiKeyBox.Password;
        }
    }
}
