using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Interop;
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
            DataContextChanged += MangaView_DataContextChanged;
            Loaded += MangaView_Loaded;
            Unloaded += MangaView_Unloaded;
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
