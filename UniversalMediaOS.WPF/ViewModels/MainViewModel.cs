using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly Func<AnimeDetailsViewModel> _detailsViewModelFactory;

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
            Func<AnimeDetailsViewModel> detailsViewModelFactory,
            SearchViewModel    searchViewModel,
            MangaViewModel     mangaViewModel,
            DownloadsViewModel downloadsViewModel,
            PlaybackViewModel  playbackViewModel,
            SettingsViewModel  settingsViewModel)
        {
            _detailsViewModelFactory = detailsViewModelFactory;
            SearchViewModel    = searchViewModel;
            MangaViewModel     = mangaViewModel;
            DownloadsViewModel = downloadsViewModel;
            PlaybackViewModel  = playbackViewModel;
            SettingsViewModel  = settingsViewModel;

            CurrentViewModel = SearchViewModel;

            DownloadsViewModel.RegisterPlayMediaAction((path, title) => PlayMedia(path, title));

            WeakReferenceMessenger.Default.Register<ToastNotificationMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.InvokeAsync(async () =>
                    {
                        vm.ToastText      = m.Message;
                        vm.IsToastVisible = true;

                        int currentId = System.Threading.Interlocked.Increment(ref vm._toastId);
                        await Task.Delay(3000);
                        if (System.Threading.Volatile.Read(ref vm._toastId) == currentId)
                            vm.IsToastVisible = false;
                    });
                }
                else
                {
                    vm.ToastText      = m.Message;
                    vm.IsToastVisible = true;
                }
            });

            WeakReferenceMessenger.Default.Register<NavigateToDetailsMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action navAction = () =>
                {
                    if (m.Media == null)
                    {
                        vm.CurrentViewModel = vm.SearchViewModel;
                    }
                    else
                    {
                        var detailsVm = vm._detailsViewModelFactory();
                        detailsVm.Media = m.Media;
                        vm.CurrentViewModel = detailsVm;
                    }
                };

                if (dispatcher != null)
                {
                    dispatcher.Invoke(navAction);
                }
                else
                {
                    navAction();
                }
            });

            WeakReferenceMessenger.Default.Register<PlayMediaMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action playAction = () =>
                {
                    if (m.IsWebView)
                        vm.PlayEmbed(m.Value, m.Title);
                    else
                        vm.PlayMedia(m.Value, m.Title, m.Referer);
                };

                if (dispatcher != null)
                {
                    dispatcher.Invoke(playAction);
                }
                else
                {
                    playAction();
                }
            });
        }

        public void PlayMedia(string path, string title, string referer = "")
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"PlayMedia invoked for path: '{path}', title: '{title}', referer: '{referer}'");
            NavigateToPlayback();
            PlaybackViewModel.LoadMedia(path, title, referer);
        }

        public void PlayEmbed(string embedUrl, string title)
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"PlayEmbed invoked for URL: '{embedUrl}', title: '{title}'");
            NavigateToPlayback();
            PlaybackViewModel.LoadEmbed(embedUrl, title);
        }

        [RelayCommand]
        private void NavigateToSearch()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Navigating to Search view.");
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = SearchViewModel;
        }

        [RelayCommand]
        private void NavigateToManga()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Navigating to Manga view.");
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = MangaViewModel;
        }

        [RelayCommand]
        private void NavigateToDownloads()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Navigating to Downloads view.");
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = DownloadsViewModel;
            _ = DownloadsViewModel.RefreshDownloadsCommand.ExecuteAsync(null);
        }

        [RelayCommand]
        private void NavigateToPlayback()
        {
            CurrentViewModel = PlaybackViewModel;
        }

        [RelayCommand]
        private void NavigateToSettings()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Navigating to Settings view.");
            if (CurrentViewModel is PlaybackViewModel) PlaybackViewModel.StopAndRelease();
            CurrentViewModel = SettingsViewModel;
        }
    }
}
