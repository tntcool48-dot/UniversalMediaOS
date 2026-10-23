using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.WPF.Views
{
    public partial class MangaView : UserControl
    {
        private static readonly HashSet<string> BlockedDomains = PlaybackView.GetBlockedDomains();
        private static readonly string AdBlockScript = PlaybackView.GetAdBlockScript();

        private bool _adBlockerConfigured;
        private bool _isLoaded;
        private ViewModels.MangaViewModel? _subscribedViewModel;
        private CoreWebView2? _navigationCore;
        private ExternalNavigation? _externalNavigation;
        private readonly DispatcherTimer _pageReportTimer;
        private readonly DispatcherTimer _restoreTimer;
        private int _restoreTicks;
        private int _restoreStableTicks;

        private sealed class ExternalNavigation(ViewModels.MangaViewModel viewModel, MangaChapter chapter, string url)
        {
            public ViewModels.MangaViewModel ViewModel { get; } = viewModel;
            public MangaChapter Chapter { get; } = chapter;
            public string Url { get; } = url;
            public int Generation { get; } = viewModel.ExternalReaderGeneration;
            public ulong? NavigationId { get; set; }
        }

        public MangaView() : this(null) { }

        internal MangaView(System.Windows.ResourceDictionary? resources)
        {
            if (resources != null) Resources.MergedDictionaries.Add(resources);
            InitializeComponent();
            _pageReportTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _pageReportTimer.Tick += PageReportTimer_Tick;
            _restoreTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _restoreTimer.Tick += RestoreTimer_Tick;
            DataContextChanged += MangaView_DataContextChanged;
            Loaded += MangaView_Loaded;
            Unloaded += MangaView_Unloaded;
        }

        private void MangaPageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_isLoaded || _subscribedViewModel is not { CurrentViewMode: 2 }) return;
            // Record the page once scrolling settles rather than on every pixel.
            _pageReportTimer.Stop();
            _pageReportTimer.Start();
        }

        private void PageReportTimer_Tick(object? sender, EventArgs e)
        {
            _pageReportTimer.Stop();
            if (_isLoaded && _subscribedViewModel is { CurrentViewMode: 2 } vm && TopVisiblePage() is int page and >= 0)
                vm.ReportVisiblePage(page);
        }

        private int TopVisiblePage()
        {
            if (FindDescendant<ScrollViewer>(MangaPageList) is not { } viewer) return -1;
            for (int i = 0; i < MangaPageList.Items.Count; i++)
            {
                if (PageTop(viewer, i) is double top &&
                    MangaPageList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement item &&
                    top + item.ActualHeight > 1)
                {
                    return i;
                }
            }
            return -1;
        }

        private double? PageTop(ScrollViewer viewer, int index)
        {
            if (MangaPageList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement { IsVisible: true } item)
                return null;
            try { return item.TransformToAncestor(viewer).Transform(new Point()).Y; }
            catch (InvalidOperationException) { return null; }
        }

        private void StartPageRestore()
        {
            _restoreTicks = 0;
            _restoreStableTicks = 0;
            _restoreTimer.Stop();
            _restoreTimer.Start();
        }

        private void RestoreTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isLoaded || _subscribedViewModel is not { CurrentViewMode: 2 } vm ||
                vm.PendingRestorePage < 0 || vm.PendingRestorePage >= MangaPageList.Items.Count)
            {
                _restoreTimer.Stop();
                return;
            }

            int index = vm.PendingRestorePage;
            if (FindDescendant<ScrollViewer>(MangaPageList) is not { } viewer) return;
            if (PageTop(viewer, index) is not double top)
            {
                MangaPageList.ScrollIntoView(MangaPageList.Items[index]);
                _restoreStableTicks = 0;
            }
            else if (Math.Abs(top) > 1 && !(top > 0 && viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5))
            {
                // Pages above the restored one change height as they decode; keep its top aligned.
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top);
                _restoreStableTicks = 0;
            }
            else
            {
                _restoreStableTicks++;
            }

            if (_restoreStableTicks >= 5 || ++_restoreTicks >= 100)
            {
                _restoreTimer.Stop();
                vm.CompletePageRestore();
            }
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is T nested) return nested;
            }
            return null;
        }

        private void RetryMangaPage_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is Button { Tag: Image image })
                Controls.AsyncImageLoader.Retry(image);
        }

        private async void RetryMangaWebsite_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_subscribedViewModel is { CurrentViewMode: 3, CanRetryWebsite: true } vm)
                await NavigateExternalAsync(vm.ExternalUrl);
        }

        private void MangaView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            DetachViewModel();

            if (_isLoaded && e.NewValue is ViewModels.MangaViewModel vm)
                AttachViewModel(vm);
        }

        private void MangaView_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _isLoaded = true;
            if (_subscribedViewModel == null && DataContext is ViewModels.MangaViewModel vm)
            {
                AttachViewModel(vm);
            }
        }

        private void MangaView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
            => CloseForTab();

        internal void CloseForTab()
        {
            _isLoaded = false;
            DetachViewModel();
            // A running DispatcherTimer is rooted by the dispatcher and would keep this view alive.
            _pageReportTimer.Stop();
            _restoreTimer.Stop();
            if (_navigationCore != null)
            {
                _navigationCore.NavigationStarting -= ExternalNavigationStarting;
                _navigationCore.NavigationCompleted -= ExternalNavigationCompleted;
                _navigationCore = null;
            }
            try
            {
                // Even an uninitialized hidden browser registers with the parent
                // HWND. Dispose alone leaves that site holding the closed reader.
                ((IKeyboardInputSink)MangaWebReader).KeyboardInputSite?.Unregister();
                MangaWebReader.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MangaView] WebView cleanup failed: {ex.Message}", "WARNING");
            }
        }

        private void AttachViewModel(ViewModels.MangaViewModel vm)
        {
            if (ReferenceEquals(_subscribedViewModel, vm))
            {
                return;
            }

            DetachViewModel();
            _subscribedViewModel = vm;
            vm.PropertyChanged += Vm_PropertyChanged;
            if (vm.CurrentViewMode == 3)
                _ = NavigateExternalAsync(vm.ExternalUrl);
            else if (vm.PendingRestorePage >= 0)
                StartPageRestore();
        }

        private void DetachViewModel()
        {
            StopExternalNavigation();
            if (_subscribedViewModel == null)
            {
                return;
            }

            _subscribedViewModel.PropertyChanged -= Vm_PropertyChanged;
            _subscribedViewModel = null;
        }

        private async void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            if (sender is ViewModels.MangaViewModel vm)
            {
                if (e.PropertyName == nameof(ViewModels.MangaViewModel.CurrentViewMode))
                {
                    if (vm.CurrentViewMode == 3)
                        await NavigateExternalAsync(vm.ExternalUrl);
                    else
                        StopExternalNavigation();
                }
                else if (e.PropertyName == nameof(ViewModels.MangaViewModel.PendingRestorePage))
                {
                    if (vm.PendingRestorePage >= 0) StartPageRestore();
                    else _restoreTimer.Stop();
                }
                else if (e.PropertyName == nameof(ViewModels.MangaViewModel.ExternalUrl))
                {
                    if (vm.CurrentViewMode == 3 && !string.IsNullOrEmpty(vm.ExternalUrl))
                        await NavigateExternalAsync(vm.ExternalUrl);
                }
            }
        }

        private async Task NavigateExternalAsync(string url)
        {
            if (!_isLoaded || _subscribedViewModel is not { CurrentViewMode: 3, SelectedChapter: not null } vm)
                return;
            StopExternalNavigation();
            var navigation = new ExternalNavigation(vm, vm.SelectedChapter, url);
            _externalNavigation = navigation;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? readerUri) || readerUri.Scheme is not ("http" or "https"))
            {
                AppLogger.Log($"[MangaView] Refused unsafe external reader URL: '{url}'", "WARNING");
                vm.ReportExternalReader(navigation.Generation, navigation.Chapter, url,
                    "Website link is unavailable. Go back and select another chapter.", false);
                return;
            }
            vm.ReportExternalReader(navigation.Generation, navigation.Chapter, url, "Opening website reader...", false);
            try
            {
                if (MangaWebReader.CoreWebView2 == null)
                    await PlaybackView.EnsureWebViewWithUBlockAsync(MangaWebReader);
                if (!IsCurrent(navigation)) return;
                var core = MangaWebReader.CoreWebView2 ?? throw new InvalidOperationException("The website reader did not initialize.");
                ConfigureAdBlocker(core);
                if (!ReferenceEquals(_navigationCore, core))
                {
                    _navigationCore = core;
                    _navigationCore.NavigationStarting += ExternalNavigationStarting;
                    _navigationCore.NavigationCompleted += ExternalNavigationCompleted;
                }
                AppLogger.Log($"[MangaView] Navigating WebView to external chapter: {readerUri.AbsoluteUri}");
                core.Navigate(readerUri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MangaView] WebView navigation failed: {ex.Message}", "ERROR");
                if (IsCurrent(navigation))
                    vm.ReportExternalReader(navigation.Generation, navigation.Chapter, url,
                        "Website could not load. Retry website or go back.", true);
            }
        }

        private bool IsCurrent(ExternalNavigation navigation) => _isLoaded &&
            ReferenceEquals(_externalNavigation, navigation) && ReferenceEquals(_subscribedViewModel, navigation.ViewModel) &&
            navigation.ViewModel.IsCurrentExternalReader(navigation.Generation, navigation.Chapter, navigation.Url);

        private void StopExternalNavigation()
        {
            _externalNavigation = null;
            try { _navigationCore?.Stop(); }
            catch (Exception ex) { AppLogger.Log($"[MangaView] Website stop failed: {ex.Message}", "WARNING"); }
        }

        private void ExternalNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (_externalNavigation is not { } navigation || !IsCurrent(navigation))
            {
                e.Cancel = true;
                return;
            }
            if (navigation.NavigationId == null && !e.IsRedirected &&
                (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var requested) || requested != new Uri(navigation.Url)))
            {
                e.Cancel = true;
                return;
            }
            navigation.NavigationId = e.NavigationId;
            navigation.ViewModel.ReportExternalReader(navigation.Generation, navigation.Chapter, navigation.Url,
                "Opening website reader...", false);
        }

        private void ExternalNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_externalNavigation is not { } navigation || !IsCurrent(navigation) || navigation.NavigationId != e.NavigationId) return;
            bool failed = !e.IsSuccess || e.HttpStatusCode >= 400;
            AppLogger.Log($"[MangaView] Website navigation completed: HTTP {e.HttpStatusCode}, success={e.IsSuccess}, error={e.WebErrorStatus}.", failed ? "WARNING" : "DEBUG");
            navigation.ViewModel.ReportExternalReader(navigation.Generation, navigation.Chapter, navigation.Url,
                failed ? "Website could not load. Retry website or go back." : "Website reader", failed);
        }

        private void ConfigureAdBlocker(CoreWebView2 core)
        {
            if (_adBlockerConfigured) return;
            _adBlockerConfigured = true;

            core.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockScript);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) =>
            {
                try
                {
                    if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
                    string host = uri.Host.TrimStart('.');
                    foreach (var domain in BlockedDomains)
                    {
                        if (host == domain || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.Log($"[AdBlock/Manga] Blocked: {e.Request.Uri}");
                            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                            return;
                        }
                    }
                }
                catch { }
            };

            core.NewWindowRequested += (s, e) =>
            {
                AppLogger.Log($"[AdBlock/Manga] Blocked new window popup: {e.Uri}");
                e.Handled = true;
            };

            AppLogger.Log("[AdBlock] Manga WebView2 ad-blocker configured.");
        }
    }
}
