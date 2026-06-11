using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public SearchViewModel    SearchViewModel    { get; }
        public MangaViewModel     MangaViewModel     { get; }
        public DownloadsViewModel DownloadsViewModel { get; }
        public PlaybackViewModel  PlaybackViewModel  { get; }
        public SettingsViewModel  SettingsViewModel  { get; }

        /// <summary>
        /// The view model currently displayed in the main content area.
        /// App.xaml DataTemplates automatically resolve the correct view.
        /// </summary>
        [ObservableProperty]
        private ObservableObject _currentViewModel = null!;

        [ObservableProperty]
        private string _toastText = string.Empty;

        [ObservableProperty]
        private bool _isToastVisible;

        private int _toastId;

        public MainViewModel(
            SearchViewModel    searchViewModel,
            MangaViewModel     mangaViewModel,
            DownloadsViewModel downloadsViewModel,
            PlaybackViewModel  playbackViewModel,
            SettingsViewModel  settingsViewModel)
        {
            SearchViewModel    = searchViewModel;
            MangaViewModel     = mangaViewModel;
            DownloadsViewModel = downloadsViewModel;
            PlaybackViewModel  = playbackViewModel;
            SettingsViewModel  = settingsViewModel;

            CurrentViewModel = SearchViewModel;

            DownloadsViewModel.RegisterPlayMediaAction(PlayMedia);

            WeakReferenceMessenger.Default.Register<ToastNotificationMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                System.Windows.Application.Current.Dispatcher.Invoke(async () =>
                {
                    vm.ToastText      = m.Message;
                    vm.IsToastVisible = true;

                    int currentId = ++vm._toastId;
                    await Task.Delay(3000);
                    if (vm._toastId == currentId)
                        vm.IsToastVisible = false;
                });
            });
        }

        public void PlayMedia(string path, string title)
        {
            NavigateToPlayback();
            PlaybackViewModel.LoadMedia(path, title);
        }

        [RelayCommand]
        private void NavigateToSearch()
        {
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = SearchViewModel;
        }

        [RelayCommand]
        private void NavigateToManga()
        {
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = MangaViewModel;
        }

        [RelayCommand]
        private void NavigateToDownloads()
        {
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = DownloadsViewModel;
        }

        [RelayCommand]
        private void NavigateToPlayback()
        {
            CurrentViewModel = PlaybackViewModel;
        }

        [RelayCommand]
        private void NavigateToSettings()
        {
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = SettingsViewModel;
        }
    }
}
