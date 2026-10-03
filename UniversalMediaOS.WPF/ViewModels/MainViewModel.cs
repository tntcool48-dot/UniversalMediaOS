using System;
using System.Collections.Generic;
using UniversalMediaOS.Core.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UniversalMediaOS.Core.OtherMedia;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private bool _isDisposed;

        public DownloadsViewModel DownloadsViewModel { get; }
        public SettingsViewModel SettingsViewModel { get; }
        public MyListViewModel MyListViewModel { get; }
        public WatchTogetherViewModel WatchTogetherViewModel { get; }

        public ObservableCollection<MediaTabViewModel> Tabs { get; } = new();
        public bool HasOpenTabs => Tabs.Count > 0;
        public double ActiveContentScale =>
            ActiveTab?.ContentViewModel is global::UniversalMediaOS.WPF.ViewModels.PlaybackViewModel or
                global::UniversalMediaOS.WPF.ViewModels.MangaViewModel or
                global::UniversalMediaOS.WPF.ViewModels.BookReaderViewModel
                ? 1.0
                : SettingsViewModel.UiScale;

        [ObservableProperty]
        private MediaTabViewModel? _activeTab;

        [ObservableProperty]
        private string _toastText = string.Empty;

        [ObservableProperty]
        private bool _isToastVisible;

        private int _toastId;

        public MainViewModel(
            IServiceScopeFactory scopeFactory,
            DownloadsViewModel downloadsViewModel,
            SettingsViewModel settingsViewModel,
            MyListViewModel myListViewModel,
            WatchTogetherViewModel watchTogetherViewModel)
        {
            _scopeFactory = scopeFactory;
            DownloadsViewModel = downloadsViewModel;
            SettingsViewModel = settingsViewModel;
            MyListViewModel = myListViewModel;
            WatchTogetherViewModel = watchTogetherViewModel;

            DownloadsViewModel.RegisterPlayMediaAction((path, title) => PlayMedia(path, title));
            SettingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;

            Tabs.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasOpenTabs));
                MoveTabLeftCommand.NotifyCanExecuteChanged();
                MoveTabRightCommand.NotifyCanExecuteChanged();
            };
            RegisterMessages();
            NavigateToSearch();
        }

        partial void OnActiveTabChanging(MediaTabViewModel? oldValue, MediaTabViewModel? newValue)
        {
            if (oldValue?.ContentViewModel is PlaybackViewModel player)
                player.SetTabActive(false);
        }

        partial void OnActiveTabChanged(MediaTabViewModel? value)
        {
            if (value?.ContentViewModel is PlaybackViewModel player)
                player.SetTabActive(true);
            foreach (var tab in Tabs)
            {
                tab.IsSelected = ReferenceEquals(tab, value);
            }

            OnPropertyChanged(nameof(ActiveContentScale));
            MoveTabLeftCommand.NotifyCanExecuteChanged();
            MoveTabRightCommand.NotifyCanExecuteChanged();
        }

        private void SettingsViewModel_PropertyChanged(
            object? sender,
            PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SettingsViewModel.UiScale))
            {
                OnPropertyChanged(nameof(ActiveContentScale));
            }
        }

        private void RegisterMessages()
        {
            WeakReferenceMessenger.Default.Register<ToastNotificationMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.InvokeAsync(async () =>
                    {
                        vm.ToastText = m.Message;
                        vm.IsToastVisible = true;

                        int currentId = System.Threading.Interlocked.Increment(ref vm._toastId);
                        await Task.Delay(3000);
                        if (System.Threading.Volatile.Read(ref vm._toastId) == currentId)
                            vm.IsToastVisible = false;
                    });
                }
                else
                {
                    vm.ToastText = m.Message;
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
                        vm.NavigateToSearch();
                        return;
                    }

                    var (detailsVm, scope) = vm.CreateScopedViewModel<AnimeDetailsViewModel>();
                    detailsVm.Media = m.Media;
                    vm.AddTab(m.Media.OfficialTitle, "Detail", "\uE7C3", "#8B5CF6", detailsVm, scope);
                };

                if (dispatcher != null)
                    dispatcher.Invoke(navAction);
                else
                    navAction();
            });

            WeakReferenceMessenger.Default.Register<CloseTabMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action closeAction = () =>
                {
                    var tab = vm.Tabs.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.ContentViewModel, m.ContentViewModel));
                    if (tab != null)
                    {
                        vm.CloseTab(tab);
                    }
                };

                if (dispatcher != null)
                    dispatcher.Invoke(closeAction);
                else
                    closeAction();
            });

            WeakReferenceMessenger.Default.Register<PlayMediaMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action playAction = () =>
                {
                    if (m.IsWebView)
                        vm.PlayEmbed(
                            m.Value,
                            m.Title,
                            m.EpisodeNumber,
                            m.TotalEpisodes,
                            m.MalId,
                            m.EpisodeContext,
                            m.Referer,
                            m.ContentType,
                            m.UserAgent,
                            m.Cookie,
                            m.RequestHeaders,
                            m.AudiovisualContext);
                    else
                        vm.PlayMedia(
                            m.Value,
                            m.Title,
                            m.Referer,
                            m.EpisodeNumber,
                            m.TotalEpisodes,
                            m.MalId,
                            m.EpisodeContext,
                            m.ContentType,
                            m.UserAgent,
                            m.Cookie,
                            m.RequestHeaders,
                            m.AudiovisualContext,
                            m.Subtitles,
                            m.AudioNotice,
                            m.TemporaryWatchLease,
                            m.LocalCaptionPaths,
                            m.ValidatedHlsVariant);
                };

                if (dispatcher != null)
                    dispatcher.Invoke(playAction);
                else
                    playAction();
            });

            WeakReferenceMessenger.Default.Register<NavigateToVaDetectMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action navAction = () =>
                {
                    var (vaVm, scope) = vm.CreateScopedViewModel<VaDetectViewModel>();
                    if (m.Media != null)
                    {
                        vaVm.LoadMedia(m.Media);
                    }

                    vm.AddTab("VA Detect", "VA Detect", "\uE77C", "#F59E0B", vaVm, scope);
                };

                if (dispatcher != null)
                    dispatcher.Invoke(navAction);
                else
                    navAction();
            });

            WeakReferenceMessenger.Default.Register<NavigateToBookDetailsMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action navAction = () =>
                {
                    var (detailsVm, scope) = vm.CreateScopedViewModel<BookDetailsViewModel>();
                    vm.AddTab(m.Book.Title, "Book Detail", "\uE82D", "#F59E0B", detailsVm, scope);
                    _ = detailsVm.InitializeAsync(m.Book);
                };

                if (dispatcher != null)
                    dispatcher.Invoke(navAction);
                else
                    navAction();
            });

            WeakReferenceMessenger.Default.Register<NavigateToAudiovisualDetailsMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action navAction = () => vm.OpenAudiovisualDetails(m.Media);

                if (dispatcher != null)
                    dispatcher.Invoke(navAction);
                else
                    navAction();
            });

            WeakReferenceMessenger.Default.Register<NavigateToBookReaderMessage>(this, (r, m) =>
            {
                var vm = (MainViewModel)r;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Action navAction = () =>
                {
                    var (readerVm, scope) = vm.CreateScopedViewModel<BookReaderViewModel>();
                    vm.AddTab(m.Book.Title, "Book Reader", "\uE736", "#F59E0B", readerVm, scope);
                    _ = readerVm.InitializeAsync(m.Book, m.Asset);
                };

                if (dispatcher != null)
                    dispatcher.Invoke(navAction);
                else
                    navAction();
            });
        }

        private void AddTab(
            string title,
            string kind,
            string icon,
            string accent,
            ObservableObject content,
            IDisposable? lifetime = null)
        {
            var tab = new MediaTabViewModel(title, kind, icon, accent, content, lifetime);
            Tabs.Add(tab);
            ActiveTab = tab;
        }

        private void OpenAudiovisualDetails(AudiovisualMediaItem item)
        {
            switch (item.Identity.Kind)
            {
                case AudiovisualMediaKind.Movie:
                    OpenAudiovisualDetails<MovieCatalogViewModel>(
                        item,
                        "Movies",
                        "\uE8B2",
                        "#06B6D4");
                    break;
                case AudiovisualMediaKind.Television:
                    OpenAudiovisualDetails<TvCatalogViewModel>(
                        item,
                        "TV Shows",
                        "\uE7F4",
                        "#10B981");
                    break;
                case AudiovisualMediaKind.Cartoon:
                    OpenAudiovisualDetails<CartoonCatalogViewModel>(
                        item,
                        "Cartoons",
                        "\uE789",
                        "#F97316");
                    break;
            }
        }

        private void OpenAudiovisualDetails<TViewModel>(
            AudiovisualMediaItem item,
            string kind,
            string icon,
            string accent)
            where TViewModel : AudiovisualCatalogViewModel
        {
            var (viewModel, scope) = CreateScopedViewModel<TViewModel>();
            AddTab(item.Title, kind, icon, accent, viewModel, scope);
            _ = viewModel.OpenItemAsync(item);
        }

        private (T ViewModel, IServiceScope Scope) CreateScopedViewModel<T>()
            where T : ObservableObject
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            IServiceScope scope = _scopeFactory.CreateScope();
            try
            {
                return (scope.ServiceProvider.GetRequiredService<T>(), scope);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        private void SelectOrAddSingleton(string title, string kind, string icon, string accent, ObservableObject content)
        {
            var existing = Tabs.FirstOrDefault(t => t.Kind == kind && t.Title == title);
            if (existing != null)
            {
                ActiveTab = existing;
                return;
            }

            AddTab(title, kind, icon, accent, content);
        }

        public void PlayMedia(
            string path,
            string title,
            string referer = "",
            string episodeNumber = "",
            int totalEpisodes = 0,
            int malId = 0,
            EpisodePlaybackContext? episodeContext = null,
            string contentType = "",
            string userAgent = "",
            string cookie = "",
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            AudiovisualPlaybackContext? audiovisualContext = null,
            IEnumerable<MediaSubtitleTrack>? subtitles = null,
            string audioNotice = "",
            IDisposable? temporaryWatchLease = null,
            IEnumerable<string>? localCaptionPaths = null,
            string validatedHlsVariant = "")
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"PlayMedia invoked for path: '{path}', title: '{title}', referer: '{referer}'");
            PlaybackViewModel playbackViewModel;
            IServiceScope scope;
            try { (playbackViewModel, scope) = CreateScopedViewModel<PlaybackViewModel>(); }
            catch { temporaryWatchLease?.Dispose(); throw; }
            AddTab(title, "Playback", "\uE768", "#06B6D4", playbackViewModel,
                temporaryWatchLease == null ? scope : new PlaybackTabLifetime(scope, temporaryWatchLease));
            playbackViewModel.LoadMedia(
                path,
                title,
                referer,
                episodeNumber,
                totalEpisodes,
                malId,
                episodeContext,
                contentType,
                userAgent,
                cookie,
                requestHeaders,
                audiovisualContext,
                subtitles,
                audioNotice,
                localCaptionPaths: localCaptionPaths,
                validatedHlsVariant: validatedHlsVariant);
        }

        private sealed class PlaybackTabLifetime(IDisposable playbackScope, IDisposable temporaryWatchLease) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { playbackScope.Dispose(); }
                finally { temporaryWatchLease.Dispose(); }
            }
        }

        public void PlayEmbed(
            string embedUrl,
            string title,
            string episodeNumber = "",
            int totalEpisodes = 0,
            int malId = 0,
            EpisodePlaybackContext? episodeContext = null,
            string referer = "",
            string contentType = "",
            string userAgent = "",
            string cookie = "",
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            AudiovisualPlaybackContext? audiovisualContext = null)
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"PlayEmbed invoked for URL: '{embedUrl}', title: '{title}'");
            var (playbackViewModel, scope) = CreateScopedViewModel<PlaybackViewModel>();
            AddTab(title, "Playback", "\uE768", "#06B6D4", playbackViewModel, scope);
            playbackViewModel.LoadEmbed(
                embedUrl,
                title,
                episodeNumber,
                totalEpisodes,
                malId,
                episodeContext,
                referer,
                contentType,
                userAgent,
                cookie,
                requestHeaders,
                audiovisualContext);
        }

        [RelayCommand]
        private void SelectTab(MediaTabViewModel? tab)
        {
            if (tab != null)
            {
                ActiveTab = tab;
            }
        }

        [RelayCommand]
        private void CloseTab(MediaTabViewModel? tab)
        {
            if (tab == null || !Tabs.Contains(tab))
            {
                return;
            }

            int index = Tabs.IndexOf(tab);
            bool wasActiveTab = ReferenceEquals(ActiveTab, tab);
            MediaTabViewModel? nextActiveTab = null;
            if (wasActiveTab && Tabs.Count > 1)
            {
                int nextIndex = index > 0 ? index - 1 : 1;
                nextActiveTab = Tabs[nextIndex];
            }

            var playback = tab.ContentViewModel as PlaybackViewModel;
            IDisposable? lifetime = tab.Lifetime ??
                (tab.ContentViewModel is WatchTogetherViewModel
                    ? null
                    : tab.ContentViewModel as IDisposable);

            void DetachTab()
            {
                if (wasActiveTab)
                {
                    ActiveTab = nextActiveTab;
                }

                Tabs.Remove(tab);
                if (Tabs.Count == 0)
                {
                    ActiveTab = null;
                }
            }

            if (playback != null)
            {
                Action disposePlayback = lifetime != null
                    ? lifetime.Dispose
                    : playback.Dispose;
                DetachPlaybackThenDispose(DetachTab, disposePlayback);
            }
            else
            {
                DetachThenDispose(DetachTab, lifetime);
            }
        }

        internal static void DetachThenDispose(Action detachView, IDisposable? disposable)
        {
            detachView();
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Tab content disposal failed: {ex.Message}", "WARNING");
            }
        }

        internal static void DetachPlaybackThenDispose(
            Action detachView,
            Action disposePlayback,
            Action<Action>? scheduleDisposal = null)
        {
            detachView();

            void DisposePlayback()
            {
                try
                {
                    disposePlayback();
                }
                catch (Exception ex)
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log($"Playback disposal after tab close failed: {ex.Message}", "WARNING");
                }
            }

            if (scheduleDisposal != null)
            {
                scheduleDisposal(DisposePlayback);
                return;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.BeginInvoke((Action)DisposePlayback, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            DisposePlayback();
        }

        [RelayCommand(CanExecute = nameof(CanMoveTabLeft))]
        private void MoveTabLeft(MediaTabViewModel? tab)
        {
            if (tab == null) return;
            int index = Tabs.IndexOf(tab);
            if (index <= 0) return;
            Tabs.Move(index, index - 1);
            MoveTabLeftCommand.NotifyCanExecuteChanged();
            MoveTabRightCommand.NotifyCanExecuteChanged();
        }

        private bool CanMoveTabLeft(MediaTabViewModel? tab) =>
            tab != null && Tabs.IndexOf(tab) > 0;

        [RelayCommand(CanExecute = nameof(CanMoveTabRight))]
        private void MoveTabRight(MediaTabViewModel? tab)
        {
            if (tab == null) return;
            int index = Tabs.IndexOf(tab);
            if (index < 0 || index >= Tabs.Count - 1) return;
            Tabs.Move(index, index + 1);
            MoveTabLeftCommand.NotifyCanExecuteChanged();
            MoveTabRightCommand.NotifyCanExecuteChanged();
        }

        private bool CanMoveTabRight(MediaTabViewModel? tab)
        {
            if (tab == null)
            {
                return false;
            }

            int index = Tabs.IndexOf(tab);
            return index >= 0 && index < Tabs.Count - 1;
        }

        [RelayCommand]
        private void NavigateToSearch()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Anime tab.");
            var (searchViewModel, scope) = CreateScopedViewModel<SearchViewModel>();
            AddTab("Anime", "Anime", "\uE7FC", "#8B5CF6", searchViewModel, scope);
            searchViewModel.Initialize();
        }

        [RelayCommand]
        private void NavigateToManga()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Manga tab.");
            var (mangaViewModel, scope) = CreateScopedViewModel<MangaViewModel>();
            AddTab("Manga", "Manga", "\uE82D", "#EC4899", mangaViewModel, scope);
            mangaViewModel.Initialize();
        }

        [RelayCommand]
        private void NavigateToMovies()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Movies tab.");
            var (viewModel, scope) = CreateScopedViewModel<MovieCatalogViewModel>();
            AddTab("Movies", "Movies", "\uE8B2", "#06B6D4", viewModel, scope);
            viewModel.Initialize();
        }

        [RelayCommand]
        private void NavigateToBooks()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Books tab.");
            var (viewModel, scope) = CreateScopedViewModel<BookBrowseViewModel>();
            AddTab("Books", "Books", "\uE82D", "#F59E0B", viewModel, scope);
            viewModel.Initialize();
        }

        [RelayCommand]
        private void NavigateToTvShows()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening TV Shows tab.");
            var (viewModel, scope) = CreateScopedViewModel<TvCatalogViewModel>();
            AddTab("TV Shows", "TV Shows", "\uE7F4", "#10B981", viewModel, scope);
            viewModel.Initialize();
        }

        [RelayCommand]
        private void NavigateToCartoons()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Cartoons tab.");
            var (viewModel, scope) = CreateScopedViewModel<CartoonCatalogViewModel>();
            AddTab("Cartoons", "Cartoons", "\uE789", "#F97316", viewModel, scope);
            viewModel.Initialize();
        }

        [RelayCommand]
        private void NavigateToWatchTogether()
        {
            SelectOrAddSingleton("Watch Together", "Watch Together", "\uE902", "#06B6D4", WatchTogetherViewModel);
        }

        [RelayCommand]
        private void NavigateToVaDetect()
        {
            var (viewModel, scope) = CreateScopedViewModel<VaDetectViewModel>();
            AddTab("VA Detect", "VA Detect", "\uE77C", "#F59E0B", viewModel, scope);
        }

        [RelayCommand]
        private void NavigateToMyList()
        {
            SelectOrAddSingleton("My List", "My List", "\uE8FD", "#10B981", MyListViewModel);
            MyListViewModel.RefreshCommand.Execute(null);
        }

        [RelayCommand]
        private void NavigateToDownloads()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Downloads tab.");
            SelectOrAddSingleton("Downloads", "Downloads", "\uE896", "#8B5CF6", DownloadsViewModel);
            _ = DownloadsViewModel.RefreshDownloadsCommand.ExecuteAsync(null);
        }

        [RelayCommand]
        private void NavigateToPlayback()
        {
            var (viewModel, scope) = CreateScopedViewModel<PlaybackViewModel>();
            AddTab("Playback", "Playback", "\uE768", "#06B6D4", viewModel, scope);
        }

        [RelayCommand]
        private void NavigateToSettings()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("Opening Settings tab.");
            SelectOrAddSingleton("Settings", "Settings", "\uE713", "#8B5CF6", SettingsViewModel);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            SettingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
            WeakReferenceMessenger.Default.UnregisterAll(this);
            MediaTabViewModel[] closingTabs = Tabs.ToArray();
            ActiveTab = null;
            Tabs.Clear(); // Release retained views before their native scopes.
            foreach (MediaTabViewModel tab in closingTabs)
            {
                IDisposable? lifetime = tab.Lifetime ??
                    (tab.ContentViewModel is WatchTogetherViewModel
                        ? null
                        : tab.ContentViewModel as IDisposable);
                try
                {
                    lifetime?.Dispose();
                }
                catch (Exception ex)
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log($"Tab content disposal during shutdown failed: {ex.Message}", "WARNING");
                }
            }

            Tabs.Clear();
            ActiveTab = null;
        }
    }
}
