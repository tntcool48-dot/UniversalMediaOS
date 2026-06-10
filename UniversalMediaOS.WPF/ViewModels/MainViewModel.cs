using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.Messaging;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public SearchViewModel SearchViewModel { get; }
        public MangaViewModel MangaViewModel { get; }
        public DownloadsViewModel DownloadsViewModel { get; }
        public PlaybackViewModel PlaybackViewModel { get; }

        [ObservableProperty]
        private bool _isSearchVisible = true;

        [ObservableProperty]
        private bool _isMangaVisible;

        [ObservableProperty]
        private bool _isDownloadsVisible;

        [ObservableProperty]
        private bool _isPlaybackVisible;

        [ObservableProperty]
        private string _toastText = string.Empty;

        [ObservableProperty]
        private bool _isToastVisible;

        private int _toastId;

        public MainViewModel(
            SearchViewModel searchViewModel,
            MangaViewModel mangaViewModel,
            DownloadsViewModel downloadsViewModel,
            PlaybackViewModel playbackViewModel)
        {
            SearchViewModel = searchViewModel;
            MangaViewModel = mangaViewModel;
            DownloadsViewModel = downloadsViewModel;
            PlaybackViewModel = playbackViewModel;
            
            DownloadsViewModel.RegisterPlayMediaAction(PlayMedia);

            WeakReferenceMessenger.Default.Register<ToastNotificationMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                System.Windows.Application.Current.Dispatcher.Invoke(async () =>
                {
                    vm.ToastText = m.Message;
                    vm.IsToastVisible = true;
                    
                    int currentId = ++vm._toastId;
                    await Task.Delay(3000);
                    if (vm._toastId == currentId)
                    {
                        vm.IsToastVisible = false;
                    }
                });
            });
        }

        public void PlayMedia(string path, string title)
        {
            NavigateToPlayback();
            PlaybackViewModel.LoadMedia(path, title);
        }

        private void HideAll()
        {
            IsSearchVisible = false;
            IsMangaVisible = false;
            IsDownloadsVisible = false;
            
            if (IsPlaybackVisible)
            {
                PlaybackViewModel.StopAndRelease();
            }
            IsPlaybackVisible = false;
        }

        [RelayCommand]
        private void NavigateToSearch()
        {
            HideAll();
            IsSearchVisible = true;
        }

        [RelayCommand]
        private void NavigateToManga()
        {
            HideAll();
            IsMangaVisible = true;
        }

        [RelayCommand]
        private void NavigateToDownloads()
        {
            HideAll();
            IsDownloadsVisible = true;
        }

        [RelayCommand]
        private void NavigateToPlayback()
        {
            HideAll();
            IsPlaybackVisible = true;
        }
    }
}
