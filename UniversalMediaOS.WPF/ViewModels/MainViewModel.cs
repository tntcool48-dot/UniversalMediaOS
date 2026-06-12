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
        private readonly IServiceProvider _serviceProvider;

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
            IServiceProvider   serviceProvider,
            SearchViewModel    searchViewModel,
            MangaViewModel     mangaViewModel,
            DownloadsViewModel downloadsViewModel,
            PlaybackViewModel  playbackViewModel,
            SettingsViewModel  settingsViewModel)
        {
            _serviceProvider   = serviceProvider;
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

            WeakReferenceMessenger.Default.Register<NavigateToDetailsMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (m.Media == null)
                    {
                        vm.CurrentViewModel = vm.SearchViewModel;
                    }
                    else
                    {
                        var detailsVm = vm._serviceProvider.GetRequiredService<AnimeDetailsViewModel>();
                        detailsVm.Media = m.Media;
                        vm.CurrentViewModel = detailsVm;
                    }
                });
            });

            WeakReferenceMessenger.Default.Register<PlayMediaMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (m.IsWebView)
                        vm.PlayEmbed(m.Value, m.Title);
                    else
                        vm.PlayMedia(m.Value, m.Title, m.Referer);
                });
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
