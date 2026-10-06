using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Views
{
    public partial class PlaybackView : UserControl
    {
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        // Domains to block (ads, tracking, popups)
        private static readonly HashSet<string> BlockedDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            "doubleclick.net", "googlesyndication.com", "adservice.google.com",
            "googletagmanager.com", "googletagservices.com", "analytics.google.com",
            "pagead2.googlesyndication.com", "adnxs.com", "rubiconproject.com",
            "openx.net", "pubmatic.com", "criteo.com", "taboola.com", "outbrain.com",
            "amazon-adsystem.com", "advertising.com",
            "adsafeprotected.com", "quantserve.com", "scorecardresearch.com",
            "ads.yahoo.com", "casalemedia.com", "serving-sys.com", "yieldmanager.com",
            "adf.ly", "popads.net", "popcash.net", "exoclick.com", "trafficjunky.net",
            "realsrv.com", "go2speed.org", "ero-advertising.com", "juicyads.com",
            "clickio.com", "bidvertiser.com", "cpmstar.com", "contentabc.com",
            "track.clickadu.com", "clickadu.com", "propellerads.com", "hilltopads.net",
            "trafficstars.com", "adsterra.com", "yllix.com", "zedo.com",
        };

        private static readonly HashSet<string> ForbiddenWebRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Cookie", "User-Agent", "Referer", "Content-Length",
            "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade", "TE",
            "Trailer", "Accept-Encoding", "Range"
        };

        public static HashSet<string> GetBlockedDomains() => BlockedDomains;
        public static string GetAdBlockScript() => AdBlockScript;

        private const string PlaybackTelemetryScriptTemplate = """
            (function() {
                if (__UMS_TOP_ONLY__ && window !== window.top) return;
                const sessionToken = __UMS_SESSION_TOKEN__;
                if (window.__umsIsPlaybackSession && window.__umsIsPlaybackSession(sessionToken)) return;
                Object.defineProperty(window, '__umsIsPlaybackSession', {
                    value: token => token === sessionToken, configurable: true
                });
                const bridge = window.chrome && window.chrome.webview;
                if (!bridge || typeof bridge.postMessage !== 'function') return;
                const postToHost = bridge.postMessage.bind(bridge);
                const serialize = JSON.stringify.bind(JSON);
                let lastProgressSent = 0;
                let lastActionKey = '';
                let lastActionSent = 0;

                function visibleArea(video) {
                    try {
                        const rect = video.getBoundingClientRect();
                        const style = window.getComputedStyle(video);
                        if (style.display === 'none' || style.visibility === 'hidden' ||
                            Number(style.opacity || 1) <= 0 || rect.width < 80 || rect.height < 45) return 0;
                        const width = Math.max(0, Math.min(rect.right, window.innerWidth) - Math.max(rect.left, 0));
                        const height = Math.max(0, Math.min(rect.bottom, window.innerHeight) - Math.max(rect.top, 0));
                        return width * height;
                    } catch(e) { return 0; }
                }

                function choosePrimaryVideo() {
                    const videos = Array.from(document.querySelectorAll('video'));
                    let best = null;
                    let bestScore = -1;
                    for (const video of videos) {
                        const area = visibleArea(video);
                        if (area <= 0) continue;
                        const duration = Number.isFinite(video.duration) ? video.duration : 0;
                        const score = area + (duration >= 120 ? 100000000 : 0) + (!video.paused ? 10000000 : 0);
                        if (score > bestScore) {
                            best = video;
                            bestScore = score;
                        }
                    }
                    return best;
                }

                function isPrimary(video) {
                    return window.__umsIsPlaybackSession(sessionToken) && video && choosePrimaryVideo() === video;
                }

                function readVideo(video) {
                    const duration = Number.isFinite(video.duration) ? video.duration : 0;
                    const currentTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
                    return { currentTime, duration };
                }

                function postProgress(video, ended) {
                    try {
                        if (!window.chrome || !window.chrome.webview || !video || !isPrimary(video)) return;
                        const state = readVideo(video);
                        if (ended && (state.duration < 120 || state.currentTime < state.duration * 0.85)) return;
                        if (!ended && Date.now() - lastProgressSent < 1000) return;
                        lastProgressSent = Date.now();
                        postToHost(serialize({
                            type: 'ums-video-progress',
                            sessionToken,
                            currentTime: state.currentTime,
                            duration: state.duration,
                            paused: !!video.paused,
                            ended: !!ended
                        }));
                    } catch(e) {}
                }

                function postAction(video, action) {
                    try {
                        if (!window.chrome || !window.chrome.webview || !video || !action || !isPrimary(video)) return;
                        const state = readVideo(video);
                        if (state.duration > 0 && state.duration < 120) return;
                        if (action === 'ended' && (state.duration < 120 || state.currentTime < state.duration * 0.85)) return;
                        const now = Date.now();
                        const key = action + ':' + Math.round(state.currentTime * 2) / 2;
                        if (key === lastActionKey && now - lastActionSent < 400) return;
                        lastActionKey = key;
                        lastActionSent = now;
                        postToHost(serialize({
                            type: 'ums-video-action',
                            sessionToken,
                            action,
                            currentTime: state.currentTime,
                            duration: state.duration
                        }));
                    } catch(e) {}
                }

                function wireVideo(video) {
                    if (!video || video.__umsTelemetryWired === sessionToken) return;
                    video.__umsTelemetryWired = sessionToken;
                    video.addEventListener('timeupdate', () => postProgress(video, false), true);
                    video.addEventListener('durationchange', () => postProgress(video, false), true);
                    video.addEventListener('play', () => postAction(video, 'play'), true);
                    video.addEventListener('pause', () => { if (!video.ended) postAction(video, 'pause'); }, true);
                    video.addEventListener('seeked', () => postAction(video, 'seeked'), true);
                    video.addEventListener('waiting', () => postAction(video, 'waiting'), true);
                    video.addEventListener('playing', () => postAction(video, 'playing'), true);
                    video.addEventListener('loadedmetadata', () => {
                        postProgress(video, false);
                        postAction(video, video.paused ? 'pause' : 'playing');
                    }, true);
                    video.addEventListener('ended', () => {
                        postProgress(video, true);
                        postAction(video, 'ended');
                    }, true);
                    postProgress(video, false);
                    if (video.readyState >= 1) postAction(video, video.paused ? 'pause' : 'playing');
                }

                function scan() {
                    try {
                        if (!window.__umsIsPlaybackSession(sessionToken)) return;
                        document.querySelectorAll('video').forEach(wireVideo);
                    } catch(e) {}
                }

                document.addEventListener('keydown', event => {
                    try {
                        if (!window.__umsIsPlaybackSession(sessionToken)) return;
                        const target = event.target;
                        if (target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName))) return;
                        const video = choosePrimaryVideo();
                        if (!video) return;
                        const key = String(event.key || '').toLowerCase();
                        if (key === ' ' || key === 'k') {
                            event.preventDefault();
                            if (video.paused) {
                                const play = video.play();
                                if (play && typeof play.catch === 'function') play.catch(() => {});
                            } else {
                                video.pause();
                            }
                        } else if (key === 'arrowleft') {
                            event.preventDefault();
                            video.currentTime = Math.max(0, video.currentTime - 10);
                        } else if (key === 'arrowright') {
                            event.preventDefault();
                            const duration = Number.isFinite(video.duration) ? video.duration : video.currentTime + 30;
                            video.currentTime = Math.min(duration, video.currentTime + 30);
                        } else if (key === 'm') {
                            event.preventDefault();
                            video.muted = !video.muted;
                        } else if (key === 'f') {
                            event.preventDefault();
                            if (document.fullscreenElement) {
                                document.exitFullscreen().catch(() => {});
                            } else if (video.requestFullscreen) {
                                video.requestFullscreen().catch(() => {});
                            }
                        }
                    } catch(e) {}
                }, true);

                document.addEventListener('DOMContentLoaded', scan, true);
                setInterval(scan, 1000);
                scan();
            })();
            """;

        // JS to suppress popups, overlay ads, and window.open hijacks
        private const string AdBlockScript = """
            (function() {
                // Suppress window.open popups
                const _origOpen = window.open;
                window.open = function(url, ...args) {
                    if (!url) return null;
                    const blocked = ['adf.ly','popads','popcash','exoclick','clickadu','propellerads'];
                    if (blocked.some(d => url.includes(d))) return null;
                    return _origOpen.call(window, url, ...args);
                };

                // Kill overlay ad elements (high z-index, fixed/absolute positioned divs)
                function removeOverlays() {
                    const els = document.querySelectorAll('*');
                    for (const el of els) {
                        try {
                            const s = window.getComputedStyle(el);
                            const z = parseInt(s.zIndex);
                            const pos = s.position;
                            const disp = s.display;
                            const hint = `${el.id || ''} ${el.className || ''} ${el.getAttribute('aria-label') || ''}`.toLowerCase();
                            const looksLikeAd = /(^|[\s_-])(ad|ads|advert|popup|interstitial|sponsor)([\s_-]|$)/.test(hint);
                            const containsPlayer = el.matches('video,iframe') || !!el.querySelector('video,iframe');
                            if (!containsPlayer && looksLikeAd &&
                                (pos === 'fixed' || pos === 'absolute') && z > 9000 && disp !== 'none') {
                                const rect = el.getBoundingClientRect();
                                if (rect.width > window.innerWidth * 0.5 && rect.height > window.innerHeight * 0.3) {
                                    el.style.display = 'none';
                                }
                            }
                        } catch(e) {}
                    }
                }
                // Run once on load
                document.addEventListener('DOMContentLoaded', removeOverlays);
                // Run every 2s to catch late-appearing overlays
                setInterval(removeOverlays, 2000);

                // Block suspicious redirects
                const _origLocation = Object.getOwnPropertyDescriptor(window, 'location');
                // Suppress alert/confirm popups from ads
                window.alert = function() {};
            })();
            """;

        public PlaybackView()
        {
            InitializeComponent();
            Loaded += PlaybackView_Loaded;
            Unloaded += PlaybackView_Unloaded;
            DataContextChanged += PlaybackView_DataContextChanged;
            IsVisibleChanged += PlaybackView_IsVisibleChanged;
        }

        private bool _viewLoaded;
        private bool _webViewDisposed;
        private bool _closedForTab;
        private Window? _surfaceWindow;
        private bool _isFullscreen;
        private bool _isPictureInPicture;
        private bool _seekMouseDown;
        private WindowStyle _previousWindowStyle;
        private WindowState _previousWindowState;
        private ResizeMode _previousResizeMode;
        private bool _previousTopmost;
        private double _previousLeft;
        private double _previousTop;
        private double _previousWidth;
        private double _previousHeight;
        private double _previousMinWidth;
        private double _previousMinHeight;
        private WindowChrome? _previousWindowChrome;
        private Grid? _fullscreenRootGrid;
        private GridLength[]? _previousRootRowHeights;
        private Transform? _previousRootLayoutTransform;
        private readonly List<(UIElement Element, Visibility Visibility)> _fullscreenHiddenElements = new();
        private bool _fullscreenHostApplied;
        private bool _webViewRequestedFullscreen;
        private Window? _presentationWindow;
        private CoreWebView2? _fullscreenEventCore;
        private CoreWebView2? _telemetryEventCore;
        private CoreWebView2? _navigationEventCore;
        private CoreWebView2? _adBlockEventCore;
        private CoreWebView2? _requestHeaderEventCore;
        private CoreWebView2? _adBlockScriptCore;
        private string? _adBlockScriptId;
        private CoreWebView2? _telemetryScriptCore;
        private string? _telemetryScriptId;
        private double? _pendingBrowserResumeSeconds;
        private readonly Dictionary<CoreWebView2Frame, FramePlaybackDocument> _playbackFrames = new();
        private CoreWebView2Frame? _activePlaybackFrame;
        private bool _browserAudioSelectionPending;
        private readonly SemaphoreSlim _webViewConfigurationLock = new(1, 1);
        private int _webNavigationGeneration;
        private string _telemetrySessionToken = string.Empty;
        private string _telemetryExpectedSource = string.Empty;
        private bool _telemetryInitialNavigationPending;
        private string _webRequestHeaderOrigin = string.Empty;
        private string _webRequestReferer = string.Empty;
        private IReadOnlyDictionary<string, string> _webRequestHeaders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string? _defaultWebViewUserAgent;
        private CoreWebView2CookieManager? _injectedCookieManager;
        private readonly List<InjectedCookieState> _injectedCookies = new();
        private int _volumeBeforeMute = 100;
        private int _controlsRevealVersion;
        private bool _controlsKeyboardInteraction;
        private readonly DispatcherTimer _controlsHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        private sealed record InjectedCookieState(
            string Name,
            string Domain,
            string Path,
            CoreWebView2Cookie? PreviousCookie);

        private async void PlaybackView_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_webViewDisposed || _closedForTab)
            {
                AppLogger.Log("[PlaybackView] Ignored a reload after the browser surface was disposed.", "WARNING");
                return;
            }

            // Window chrome changes can unload and reload the same visual tree.
            // Keep the existing browser document and subscriptions in that case.
            if (_viewLoaded)
            {
                RefreshVisibleNativeSurface();
                return;
            }

            _viewLoaded = true;
            _surfaceWindow = Window.GetWindow(this);
            if (_surfaceWindow != null)
            {
                _surfaceWindow.Activated += SurfaceWindow_Activated;
                _surfaceWindow.PreviewKeyDown += SurfaceWindow_PreviewKeyDown;
            }
            _controlsHideTimer.Tick -= ControlsHideTimer_Tick;
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;
            ShowControlsTemporarily();
            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                vm.ActivateWatchTogetherPlayback();
                AttachNativeVideoSurface(vm);
                vm.PropertyChanged += Vm_PropertyChanged;
                vm.WebPlaybackCommandRequested -= Vm_WebPlaybackCommandRequested;
                vm.WebPlaybackCommandRequested += Vm_WebPlaybackCommandRequested;
                UpdatePlayerVisibility(vm);
                ShowControlsTemporarily();
                await UpdateWebViewUrlAsync(vm);

                // If media was queued before view was loaded, replay it now
                if (!string.IsNullOrEmpty(vm.PendingMediaPath))
                {
                    AppLogger.Log($"[PlaybackView] Loaded — playing deferred media: '{vm.PendingMediaPath}'");
                    vm.PlayPending();
                    ShowControlsTemporarily();
                }
            }
        }

        private void PlaybackView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            // A presentation-mode transition rebuilds the window template. Wait
            // for its Loaded events before treating this as a closed player.
            // Run after Loaded, but before the tab's Background native disposal.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!IsLoaded) ReleaseUnloadedPlayback();
            }));
        }

        internal void CloseForTab()
        {
            if (_closedForTab) return;
            _closedForTab = true;
            ReleaseUnloadedPlayback();
        }

        private void PlaybackView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_closedForTab) return;
            if (IsVisible && DataContext is ViewModels.PlaybackViewModel vm && vm.IsTabActive)
            {
                vm.ActivateWatchTogetherPlayback();
                RefreshVisibleNativeSurface();
                ShowControlsTemporarily();
            }
            else
            {
                _controlsHideTimer.Stop();
                NativeOverlay.Cursor = Cursors.Arrow;
                // Window chrome changes briefly hide the view while its tab
                // remains active. Only a real tab change ends presentation.
                if ((_isFullscreen || _isPictureInPicture) &&
                    DataContext is ViewModels.PlaybackViewModel { IsTabActive: false })
                    RestorePresentationMode("[PlaybackView] Restored the app window because its player tab became inactive.");
            }
        }

        private void SurfaceWindow_Activated(object? sender, EventArgs e)
        {
            RefreshVisibleNativeSurface();
            if (IsVisible) ShowControlsTemporarily();
        }

        private void SurfaceWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Activating the owner can restore its previous keyboard focus,
            // outside VLC's foreground overlay. Only the visible tab handles
            // shortcuts. Site playback accepts app presentation keys when
            // focus is outside the page (for example on its selected tab).
            if (!IsVisible || DataContext is not ViewModels.PlaybackViewModel { IsTabActive: true } vm) return;
            if (vm.IsWebViewActive && (IsBrowserInput(e.OriginalSource as DependencyObject) ||
                e.Key is not (Key.P or Key.Escape))) return;
            PlaybackRoot_PreviewKeyDown(sender, e);
        }

        private void RefreshVisibleNativeSurface()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_webViewDisposed && !_closedForTab && IsLoaded && IsVisible &&
                    DataContext is ViewModels.PlaybackViewModel { IsTabActive: true } vm)
                    AttachNativeVideoSurface(vm);
            }));
        }

        private void ReleaseUnloadedPlayback()
        {
            if (_webViewDisposed) return;
            _viewLoaded = false;
            if (_surfaceWindow != null)
            {
                _surfaceWindow.Activated -= SurfaceWindow_Activated;
                _surfaceWindow.PreviewKeyDown -= SurfaceWindow_PreviewKeyDown;
            }
            _surfaceWindow = null;
            Interlocked.Increment(ref _webNavigationGeneration);
            _controlsHideTimer.Stop();
            _pendingBrowserResumeSeconds = null;
            DetachPlaybackFrames();
            RestorePresentationMode("[PlaybackView] Restored the app window because playback was closed.");

            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                vm.DeactivateWatchTogetherPlayback();
                vm.PropertyChanged -= Vm_PropertyChanged;
                vm.WebPlaybackCommandRequested -= Vm_WebPlaybackCommandRequested;
                DetachNativeVideoSurface(vm);
            }
            else
            {
                DetachNativeVideoSurface(null);
            }

            // VideoView owns a separate foreground overlay window as well as
            // its HWND host. Unloading alone only hides that overlay.
            try { VlcPlayer.Dispose(); }
            catch (Exception ex) { AppLogger.Log($"[PlaybackView] Native surface cleanup failed: {ex.Message}", "WARNING"); }
            DisposePlaybackWebView();
        }

        private void DisposePlaybackWebView()
        {
            if (_webViewDisposed)
            {
                return;
            }

            _webViewDisposed = true;
            _telemetrySessionToken = string.Empty;
            _telemetryExpectedSource = string.Empty;
            _telemetryInitialNavigationPending = false;
            _webRequestHeaderOrigin = string.Empty;
            _webRequestReferer = string.Empty;
            _webRequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ClearInjectedWebCookies();

            CoreWebView2? core = null;
            try
            {
                core = WebViewPlayer.CoreWebView2;
                core?.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to stop WebView2 during unload: {ex.Message}", "WARNING");
            }

            _pendingBrowserResumeSeconds = null;
            DetachPlaybackFrames();
            DetachCoreWebView2Handlers();

            try
            {
                core?.Navigate("about:blank");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to navigate WebView2 away during unload: {ex.Message}", "WARNING");
            }

            try
            {
                WebViewPlayer.Dispose();
                AppLogger.Log("[PlaybackView] WebView2 stopped, navigated away, and disposed on unload.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to dispose WebView2 during unload: {ex.Message}", "WARNING");
            }
        }

        private void DetachNativeVideoSurface(ViewModels.PlaybackViewModel? vm)
        {
            if (vm?.IsDisposed == true)
            {
                AppLogger.Log("[PlaybackView] Skipped native video detach because playback resources were already disposed.", "WARNING");
                return;
            }

            try
            {
                if (VlcPlayer.MediaPlayer == null)
                {
                    AppLogger.Log("[PlaybackView] Native video surface already detached on tab unload.");
                    return;
                }

                VlcPlayer.MediaPlayer = null;
                AppLogger.Log("[PlaybackView] Native video surface detached on tab unload.");
            }
            catch (ObjectDisposedException ex)
            {
                AppLogger.Log($"[PlaybackView] Native video surface was already disposed during detach: {ex.Message}", "WARNING");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to detach native video surface: {ex.Message}", "WARNING");
            }
        }

        private void PlaybackRoot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _controlsKeyboardInteraction = false;
            ShowControlsTemporarily();
        }

        private void PlaybackRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _controlsKeyboardInteraction = false;
            if (!IsBrowserInput(e.OriginalSource as DependencyObject) &&
                !IsControlsInput(e.OriginalSource as DependencyObject)) FocusPlaybackSurface();
            ShowControlsTemporarily();
        }

        private void FocusPlaybackSurface()
        {
            // Keep a focus target in the owner too, so its activation restores
            // player focus. The native overlay also receives its own key events.
            Focus();
        }

        private bool IsControlsInput(DependencyObject? source)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, TitlePanel) || ReferenceEquals(source, OptionsPanel) ||
                    ReferenceEquals(source, TransportPanel)) return true;
                // A ComboBox Popup has its own visual tree. Rejoin its owning
                // selector before deciding this is a video click: focusing the
                // player here dismisses the menu before its choice is committed.
                source = source is ComboBoxItem item
                    ? ItemsControl.ItemsControlFromItemContainer(item)
                    : source is Visual visual ? VisualTreeHelper.GetParent(visual)
                    : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }

        private bool IsBrowserInput(DependencyObject? source) =>
            source is Visual visual &&
            (ReferenceEquals(visual, WebViewPlayer) || WebViewPlayer.IsAncestorOf(visual));

        private void PlaybackRoot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ShowControlsTemporarily();
        }

        private void ShowControlsTemporarily()
        {
            _controlsRevealVersion++;
            _controlsHideTimer.Stop();
            ControlsLayer.BeginAnimation(OpacityProperty, null);
            ControlsLayer.Opacity = 1;
            UpdateControlsLayout();
            NativeOverlay.Cursor = Cursors.Arrow;
            // Site playback always uses the site's controls. Keep the app's
            // transport/source panel hidden even when the pointer moves.
            if (_closedForTab || DataContext is ViewModels.PlaybackViewModel { IsWebViewActive: true } or
                ViewModels.PlaybackViewModel { IsTabActive: false })
            {
                ControlsLayer.Visibility = Visibility.Collapsed;
                ControlsLayer.IsHitTestVisible = false;
                return;
            }
            ControlsLayer.Visibility = Visibility.Visible;
            ControlsLayer.IsHitTestVisible = true;
            if (ShouldAutoHideControls())
            {
                _controlsHideTimer.Start();
            }
        }

        private bool ShouldAutoHideControls()
        {
            return DataContext is ViewModels.PlaybackViewModel vm &&
                   !_closedForTab && vm.IsTabActive && !vm.IsWebViewActive &&
                   vm.IsPlaying && !vm.IsPlaybackBusy && !vm.HasPlaybackError;
        }

        private bool IsInteractingWithControls() => _seekMouseDown || TransportPanel.IsMouseOver ||
            TitlePanel.IsMouseOver || OptionsPanel.IsMouseOver || PlaybackOptions.IsExpanded ||
            (_controlsKeyboardInteraction && ControlsLayer.IsKeyboardFocusWithin);

        private void PlaybackOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateControlsLayout();

        private void UpdateControlsLayout()
        {
            if (TransportPanel == null || DataContext is not ViewModels.PlaybackViewModel vm || vm.IsDisposed) return;
            uint width = 0, height = 0;
            vm.MediaPlayer.Size(0, ref width, ref height);
            bool captionsEnabled = vm.SelectedCaption is { Key: not "off" };
            TransportPanel.Margin = new Thickness(0, 0, 0,
                CaptionControlsBottomInset(NativeOverlay.ActualWidth, NativeOverlay.ActualHeight,
                    width, height, captionsEnabled));
        }

        internal static double CaptionControlsBottomInset(double viewportWidth, double viewportHeight,
            uint videoWidth, uint videoHeight, bool captionsEnabled)
        {
            if (!captionsEnabled || viewportWidth <= 0 || viewportHeight <= 0) return 0;
            // VLC renders captions inside the fitted picture, including in a
            // letterboxed viewport. Move the transport instead of restarting
            // media to change VLC 3's subtitle margin. These are WPF units, so
            // the clearance follows window/DPI scaling with the overlay.
            double pictureHeight = videoWidth > 0 && videoHeight > 0
                ? Math.Min(viewportHeight, viewportWidth * videoHeight / videoWidth) : viewportHeight;
            double captionHeight = Math.Max(60, pictureHeight * 0.14);
            return Math.Min(viewportHeight * 0.55, (viewportHeight - pictureHeight) / 2 + captionHeight);
        }

        private void ControlsLayer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (ControlsLayer.Visibility == Visibility.Visible) ShowControlsTemporarily();
        }

        private void PlaybackOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewLoaded) ShowControlsTemporarily();
        }

        private void ControlsHideTimer_Tick(object? sender, EventArgs e)
        {
            _controlsHideTimer.Stop();
            if (!ShouldAutoHideControls())
            {
                ShowControlsTemporarily();
                return;
            }
            if (IsInteractingWithControls())
            {
                _controlsHideTimer.Start();
                return;
            }

            int revealVersion = _controlsRevealVersion;
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            animation.Completed += (_, _) =>
            {
                if (revealVersion != _controlsRevealVersion || !ShouldAutoHideControls() || IsInteractingWithControls()) return;
                ControlsLayer.IsHitTestVisible = false;
                ControlsLayer.Visibility = Visibility.Collapsed;
                NativeOverlay.Cursor = Cursors.None;
            };
            ControlsLayer.BeginAnimation(OpacityProperty, animation);
        }

        private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsBrowserInput(e.OriginalSource as DependencyObject) ||
                IsControlsInput(e.OriginalSource as DependencyObject)) return;
            FocusPlaybackSurface();
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        private void PlaybackRoot_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || IsBrowserInput(e.OriginalSource as DependencyObject) ||
                DataContext is not ViewModels.PlaybackViewModel { IsTabActive: true } vm)
            {
                return;
            }

            _controlsKeyboardInteraction = true;
            ShowControlsTemporarily();
            // Escape must always restore the app, including while editing the
            // source or using a selector. Other keys retain those controls' use.
            if (e.Key == Key.Escape)
            {
                PlaybackOptions.IsExpanded = false;
                if (_isFullscreen || _isPictureInPicture)
                    RestorePresentationMode("[PlaybackView] Exited the special playback window with Escape.");
                e.Handled = true;
                return;
            }
            if (Keyboard.FocusedElement is TextBox or ComboBox or Slider ||
                (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase && e.Key is Key.Space or Key.Enter)) return;

            bool handled = true;
            switch (e.Key)
            {
                case Key.Space:
                case Key.K:
                    vm.TogglePlayPauseCommand.Execute(null);
                    break;
                case Key.Left:
                    vm.SkipBackwardCommand.Execute(null);
                    break;
                case Key.Right:
                    vm.SkipForwardCommand.Execute(null);
                    break;
                case Key.M:
                    if (vm.Volume > 0)
                    {
                        _volumeBeforeMute = vm.Volume;
                        vm.Volume = 0;
                    }
                    else
                    {
                        vm.Volume = Math.Clamp(_volumeBeforeMute, 1, 100);
                    }
                    break;
                case Key.F:
                    ToggleFullscreen();
                    break;
                case Key.P:
                    TogglePictureInPicture();
                    break;
                case Key.Escape when _isFullscreen || _isPictureInPicture:
                    RestorePresentationMode("[PlaybackView] Exited the special playback window with Escape.");
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled)
            {
                e.Handled = true;
                ShowControlsTemporarily();
            }
        }

        private void OpenMediaFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open media file",
                Filter = "Media files|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v;*.mp3;*.m4a;*.aac;*.flac;*.wav;*.ogg;*.m3u8|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true &&
                DataContext is ViewModels.PlaybackViewModel vm)
            {
                vm.SourceInput = dialog.FileName;
                vm.OpenSourceCommand.Execute(null);
            }
        }

        private void PlaybackView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ViewModels.PlaybackViewModel oldVm)
            {
                oldVm.DeactivateWatchTogetherPlayback();
                oldVm.PropertyChanged -= Vm_PropertyChanged;
                oldVm.WebPlaybackCommandRequested -= Vm_WebPlaybackCommandRequested;
            }
            if (e.NewValue is ViewModels.PlaybackViewModel vm)
            {
                if (_viewLoaded && !_webViewDisposed)
                {
                    vm.ActivateWatchTogetherPlayback();
                    AttachNativeVideoSurface(vm);
                    vm.PropertyChanged += Vm_PropertyChanged;
                    vm.WebPlaybackCommandRequested -= Vm_WebPlaybackCommandRequested;
                    vm.WebPlaybackCommandRequested += Vm_WebPlaybackCommandRequested;
                    UpdatePlayerVisibility(vm);
                    ShowControlsTemporarily();
                    _ = UpdateWebViewUrlAsync(vm);

                    // Replay pending if already loaded
                    if (!string.IsNullOrEmpty(vm.PendingMediaPath))
                    {
                        AppLogger.Log($"[PlaybackView] DataContext changed while loaded — playing deferred: '{vm.PendingMediaPath}'");
                        vm.PlayPending();
                        ShowControlsTemporarily();
                    }
                }
            }
        }

        private async void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is ViewModels.PlaybackViewModel vm)
            {
                if (e.PropertyName == nameof(ViewModels.PlaybackViewModel.EmbedUrl))
                {
                    await UpdateWebViewUrlAsync(vm);
                    ShowControlsTemporarily();
                }
                else if (e.PropertyName == nameof(ViewModels.PlaybackViewModel.IsWebViewActive))
                {
                    UpdatePlayerVisibility(vm);
                    ShowControlsTemporarily();
                }
                else if (e.PropertyName is nameof(ViewModels.PlaybackViewModel.IsPlaying) or
                         nameof(ViewModels.PlaybackViewModel.IsPlaybackBusy) or
                         nameof(ViewModels.PlaybackViewModel.HasPlaybackError) or
                         nameof(ViewModels.PlaybackViewModel.IsTabActive))
                {
                    ShowControlsTemporarily();
                }
                else if (e.PropertyName is nameof(ViewModels.PlaybackViewModel.SelectedCaption) or
                         nameof(ViewModels.PlaybackViewModel.PlaybackDuration))
                {
                    UpdateControlsLayout();
                }
                else if (e.PropertyName == nameof(ViewModels.PlaybackViewModel.PendingMediaPath))
                {
                    // PendingMediaPath was just set from LoadMedia — play it if view is ready
                    if (_viewLoaded && vm.IsTabActive && !string.IsNullOrEmpty(vm.PendingMediaPath))
                    {
                        AppLogger.Log($"[PlaybackView] PendingMediaPath changed — triggering PlayPending immediately (view already loaded).");
                        vm.PlayPending();
                    }
                }
            }
        }

        private void UpdatePlayerVisibility(ViewModels.PlaybackViewModel vm)
        {
            if (vm.IsWebViewActive)
            {
                VlcPlayer.Visibility = System.Windows.Visibility.Collapsed;
                WebViewPlayer.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                VlcPlayer.Visibility = System.Windows.Visibility.Visible;
                WebViewPlayer.Visibility = System.Windows.Visibility.Collapsed;
                try
                {
                    WebViewPlayer.CoreWebView2?.Navigate("about:blank");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Unable to blank Playback WebView2: {ex.Message}", "WARNING");
                }
            }
        }

        private void AttachNativeVideoSurface(ViewModels.PlaybackViewModel vm)
        {
            if (vm.IsDisposed || vm.IsWebViewActive)
            {
                return;
            }

            try
            {
                if (!ReferenceEquals(VlcPlayer.MediaPlayer, vm.MediaPlayer))
                {
                    VlcPlayer.MediaPlayer = vm.MediaPlayer;
                }

                VlcPlayer.InvalidateVisual();
                // A window-template transition can recreate the HWND without
                // changing the MediaPlayer dependency property. Repair only
                // that binding; keep the existing media and paused position.
                if (VlcPlayer.Template?.FindName("PART_PlayerHost", VlcPlayer) is HwndHost host &&
                    host.Handle != IntPtr.Zero && vm.MediaPlayer.Hwnd != host.Handle)
                    vm.MediaPlayer.Hwnd = host.Handle;
                AppLogger.Log($"[PlaybackView] Native video surface attached hwnd={vm.MediaPlayer.Hwnd.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to attach native video surface: {ex.Message}", "WARNING");
            }
        }

        private void PlaybackSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                _seekMouseDown = true;
                vm.BeginUserSeek();
                ShowControlsTemporarily();
            }
        }

        private void PlaybackSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CommitPlaybackSliderSeek();
        }

        private void PlaybackSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_seekMouseDown && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CommitPlaybackSliderSeek();
            }
        }

        private void CommitPlaybackSliderSeek()
        {
            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                vm.CommitUserSeek(PlaybackSlider.Value);
                ShowControlsTemporarily();
            }

            _seekMouseDown = false;
        }

        private void FullscreenButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void PictureInPictureButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePictureInPicture();
        }

        private void MediaTitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isPictureInPicture || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                e.Handled = true;
                Window.GetWindow(this)?.DragMove();
            }
            catch (InvalidOperationException)
            {
                // Mouse capture can end between the event and DragMove.
            }
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                ExitFullscreen("[PlaybackView] Exited fullscreen playback.");
            }
            else
            {
                if (_isPictureInPicture)
                {
                    ExitPictureInPicture("[PlaybackView] Exited picture-in-picture for fullscreen playback.");
                }
                EnterFullscreen("[PlaybackView] Entered fullscreen playback.");
            }
        }

        private void TogglePictureInPicture()
        {
            if (_isPictureInPicture)
            {
                ExitPictureInPicture("[PlaybackView] Exited picture-in-picture playback.");
                return;
            }

            if (_isFullscreen)
            {
                ExitFullscreen("[PlaybackView] Exited fullscreen for picture-in-picture playback.");
            }

            EnterPictureInPicture();
        }

        private void EnterFullscreen(string logMessage)
        {
            var window = Window.GetWindow(this);
            if (window == null)
            {
                return;
            }

            if (!_isFullscreen)
            {
                CapturePresentationWindow(window);

                if (window.WindowState == WindowState.Maximized)
                {
                    window.WindowState = WindowState.Normal;
                }

                WindowChrome.SetWindowChrome(window, null);
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.Topmost = true;
                window.WindowState = WindowState.Normal;
                ApplyCurrentMonitorBounds(window);
                _isFullscreen = true;
            }

            ApplyFullscreenHost(window);
            AppLogger.Log(logMessage);
        }

        private void ExitFullscreen(string logMessage)
        {
            var window = Window.GetWindow(this);
            if (window == null)
            {
                return;
            }

            _isFullscreen = false;
            RestorePresentationWindow(window);
            AppLogger.Log(logMessage);
        }

        private void EnterPictureInPicture()
        {
            var window = Window.GetWindow(this);
            if (window == null)
            {
                return;
            }

            CapturePresentationWindow(window);
            // Remove the app navigation before shrinking the window. Otherwise
            // the controls can consume all available height and WebView2 tries
            // to recreate its graphics capture surface with a zero dimension.
            ApplyFullscreenHost(window);
            if (window.WindowState == WindowState.Maximized)
            {
                window.WindowState = WindowState.Normal;
            }

            WindowChrome.SetWindowChrome(window, null);
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.CanResizeWithGrip;
            window.Topmost = true;
            window.MinWidth = 360;
            window.MinHeight = 240;
            window.Width = 560;
            window.Height = 390;
            PlaceAtWorkAreaBottomRight(window);
            _isPictureInPicture = true;
            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                vm.IsPictureInPictureMode = true;
            }
            ShowControlsTemporarily();
            AppLogger.Log("[PlaybackView] Entered picture-in-picture playback.");
        }

        private void ExitPictureInPicture(string logMessage)
        {
            var window = Window.GetWindow(this) ?? _presentationWindow;
            if (window == null)
            {
                return;
            }

            _isPictureInPicture = false;
            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                vm.IsPictureInPictureMode = false;
            }
            RestorePresentationWindow(window);
            ShowControlsTemporarily();
            AppLogger.Log(logMessage);
        }

        private void RestorePresentationMode(string logMessage)
        {
            if (_presentationWindow is { IsLoaded: false })
            {
                RestoreFullscreenHost();
                _isFullscreen = false;
                _isPictureInPicture = false;
                _presentationWindow = null;
                if (DataContext is ViewModels.PlaybackViewModel unloadedVm)
                {
                    unloadedVm.IsPictureInPictureMode = false;
                }
                return;
            }

            if (_isFullscreen)
            {
                ExitFullscreen(logMessage);
            }
            else if (_isPictureInPicture)
            {
                ExitPictureInPicture(logMessage);
            }
        }

        private void CapturePresentationWindow(Window window)
        {
            _presentationWindow = window;
            _previousWindowStyle = window.WindowStyle;
            _previousWindowState = window.WindowState;
            _previousResizeMode = window.ResizeMode;
            _previousTopmost = window.Topmost;
            _previousLeft = window.RestoreBounds.Left;
            _previousTop = window.RestoreBounds.Top;
            _previousWidth = window.RestoreBounds.Width;
            _previousHeight = window.RestoreBounds.Height;
            _previousMinWidth = window.MinWidth;
            _previousMinHeight = window.MinHeight;
            _previousWindowChrome = WindowChrome.GetWindowChrome(window);
        }

        private void RestorePresentationWindow(Window window)
        {
            window.WindowState = WindowState.Normal;
            window.MinWidth = _previousMinWidth;
            window.MinHeight = _previousMinHeight;
            WindowChrome.SetWindowChrome(window, _previousWindowChrome);
            window.Topmost = _previousTopmost;
            window.WindowStyle = _previousWindowStyle;
            window.ResizeMode = _previousResizeMode;
            window.Left = _previousLeft;
            window.Top = _previousTop;
            window.Width = _previousWidth;
            window.Height = _previousHeight;
            ClampWindowToNearestWorkArea(window);
            window.WindowState = _previousWindowState;
            // Restore navigation only after there is room for the full layout.
            RestoreFullscreenHost();
            _presentationWindow = null;
        }

        private static void PlaceAtWorkAreaBottomRight(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                window.Left = Math.Max(0, SystemParameters.WorkArea.Right - window.Width - 16);
                window.Top = Math.Max(0, SystemParameters.WorkArea.Bottom - window.Height - 16);
                return;
            }

            Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
                                ?? Matrix.Identity;
            Point bottomRight = fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
            window.Left = bottomRight.X - window.Width - 16;
            window.Top = bottomRight.Y - window.Height - 16;
        }

        private static void ApplyCurrentMonitorBounds(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                window.WindowState = WindowState.Maximized;
                return;
            }

            Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
                                ?? Matrix.Identity;
            Point topLeft = fromDevice.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
            Point bottomRight = fromDevice.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));

            window.Left = topLeft.X;
            window.Top = topLeft.Y;
            window.Width = Math.Max(1, bottomRight.X - topLeft.X);
            window.Height = Math.Max(1, bottomRight.Y - topLeft.Y);
        }

        private static void ClampWindowToNearestWorkArea(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
                                ?? Matrix.Identity;
            Point workTopLeft = fromDevice.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
            Point workBottomRight = fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
            double workWidth = Math.Max(1, workBottomRight.X - workTopLeft.X);
            double workHeight = Math.Max(1, workBottomRight.Y - workTopLeft.Y);

            window.Width = Math.Min(Math.Max(1, window.Width), workWidth);
            window.Height = Math.Min(Math.Max(1, window.Height), workHeight);
            window.Left = Math.Min(Math.Max(window.Left, workTopLeft.X), workBottomRight.X - window.Width);
            window.Top = Math.Min(Math.Max(window.Top, workTopLeft.Y), workBottomRight.Y - window.Height);
        }

        private void ApplyFullscreenHost(Window window)
        {
            if (_fullscreenHostApplied || window.Content is not Grid rootGrid)
            {
                return;
            }

            DependencyObject? host = this;
            while (host != null && !ReferenceEquals(VisualTreeHelper.GetParent(host), rootGrid))
                host = VisualTreeHelper.GetParent(host);
            if (host is not UIElement playbackHost) return;
            int playbackRow = Grid.GetRow(playbackHost);

            _fullscreenRootGrid = rootGrid;
            _previousRootRowHeights = new GridLength[rootGrid.RowDefinitions.Count];
            GridLength[] fullscreenRowHeights = CreateFullscreenRowHeights(rootGrid.RowDefinitions.Count, playbackRow);
            for (int i = 0; i < rootGrid.RowDefinitions.Count; i++)
            {
                _previousRootRowHeights[i] = rootGrid.RowDefinitions[i].Height;
                rootGrid.RowDefinitions[i].Height = fullscreenRowHeights[i];
            }

            _previousRootLayoutTransform = rootGrid.LayoutTransform;
            rootGrid.LayoutTransform = Transform.Identity;

            _fullscreenHiddenElements.Clear();
            foreach (UIElement child in rootGrid.Children)
            {
                if (ReferenceEquals(child, playbackHost))
                {
                    continue;
                }

                _fullscreenHiddenElements.Add((child, child.Visibility));
                child.Visibility = Visibility.Collapsed;
            }

            _fullscreenHostApplied = true;
        }

        internal static GridLength[] CreateFullscreenRowHeights(int rowCount, int playbackRowIndex)
        {
            if (rowCount <= 0)
            {
                return Array.Empty<GridLength>();
            }

            if (playbackRowIndex < 0 || playbackRowIndex >= rowCount)
            {
                throw new ArgumentOutOfRangeException(nameof(playbackRowIndex));
            }

            var heights = new GridLength[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                heights[i] = i == playbackRowIndex
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
            }

            return heights;
        }

        private void RestoreFullscreenHost()
        {
            if (!_fullscreenHostApplied || _fullscreenRootGrid == null)
            {
                return;
            }

            if (_previousRootRowHeights != null)
            {
                for (int i = 0; i < _fullscreenRootGrid.RowDefinitions.Count && i < _previousRootRowHeights.Length; i++)
                {
                    _fullscreenRootGrid.RowDefinitions[i].Height = _previousRootRowHeights[i];
                }
            }

            _fullscreenRootGrid.LayoutTransform = _previousRootLayoutTransform;

            foreach (var (element, visibility) in _fullscreenHiddenElements)
            {
                element.Visibility = visibility;
            }

            _fullscreenHiddenElements.Clear();
            _fullscreenRootGrid = null;
            _previousRootRowHeights = null;
            _previousRootLayoutTransform = null;
            _fullscreenHostApplied = false;
        }

        public static async Task<CoreWebView2Environment?> CreateUBlockEnvironmentAsync()
        {
            try
            {
                string localAppData = UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory;
                string userDataPath = System.IO.Path.Combine(localAppData, "UniversalMediaOS", "WebView2UserData");
                string uBlockPath = GetUBlockOriginPath();

                var options = new CoreWebView2EnvironmentOptions
                {
                    AreBrowserExtensionsEnabled = true
                };

                if (!System.IO.File.Exists(System.IO.Path.Combine(uBlockPath, "manifest.json")))
                    AppLogger.Log($"uBlock Origin extension path not found: '{uBlockPath}'. Starting WebView2 with script blocker fallback.", "WARNING");

                return await CoreWebView2Environment.CreateAsync(null, userDataPath, options);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to initialize WebView2 uBlock Environment: {ex.Message}", "WARNING");
            }
            return null;
        }

        public static string GetUBlockOriginPath()
        {
            string localAppData = UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory;
            string root = System.IO.Path.Combine(localAppData, "UniversalMediaOS", "Extensions", "ublock-origin");
            string directManifest = System.IO.Path.Combine(root, "manifest.json");
            if (System.IO.File.Exists(directManifest))
            {
                return root;
            }

            string chromiumPath = System.IO.Path.Combine(root, "uBlock0.chromium");
            string chromiumManifest = System.IO.Path.Combine(chromiumPath, "manifest.json");
            if (System.IO.File.Exists(chromiumManifest))
            {
                return chromiumPath;
            }

            try
            {
                string? nestedManifest = System.IO.Directory.Exists(root)
                    ? System.IO.Directory.EnumerateFiles(root, "manifest.json", System.IO.SearchOption.AllDirectories)
                        .OrderBy(path => path.Split(System.IO.Path.DirectorySeparatorChar).Length)
                        .FirstOrDefault()
                    : null;
                if (!string.IsNullOrWhiteSpace(nestedManifest))
                {
                    return System.IO.Path.GetDirectoryName(nestedManifest) ?? root;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to inspect nested uBlock Origin path: {ex.Message}", "WARNING");
            }

            return root;
        }

        private static readonly SemaphoreSlim UBlockInitializationLock = new(1, 1);
        private static readonly HashSet<string> InitializedUBlockProfiles = new(StringComparer.OrdinalIgnoreCase);

        public static async Task EnsureWebViewWithUBlockAsync(IWebView2 webView)
        {
            var env = await CreateUBlockEnvironmentAsync();
            await webView.EnsureCoreWebView2Async(env);

            string uBlockPath = GetUBlockOriginPath();
            string manifestPath = System.IO.Path.Combine(uBlockPath, "manifest.json");
            if (!System.IO.File.Exists(manifestPath))
                return;

            await UBlockInitializationLock.WaitAsync();
            try
            {
                string profileKey = webView.CoreWebView2.Profile.ProfilePath + "|" + uBlockPath;
                // Adding the extension again reloads it for every browser in
                // the shared profile and aborts an already navigating tab.
                if (InitializedUBlockProfiles.Contains(profileKey))
                {
                    AppLogger.Log("[uBlock] Reused extension setup for the shared WebView2 profile.");
                    return;
                }
                var ext = await webView.CoreWebView2.Profile.AddBrowserExtensionAsync(uBlockPath);
                InitializedUBlockProfiles.Add(profileKey);
                AppLogger.Log($"[uBlock] Loaded WebView2 extension: {ext.Name}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[uBlock] Extension load skipped or failed: {ex.Message}", "WARNING");
            }
            finally
            {
                UBlockInitializationLock.Release();
            }
        }

        private async Task UpdateWebViewUrlAsync(ViewModels.PlaybackViewModel vm)
        {
            if (_webViewDisposed || !_viewLoaded || !vm.IsWebViewActive || string.IsNullOrWhiteSpace(vm.EmbedUrl))
            {
                return;
            }

            string requestedUrl = vm.EmbedUrl;
            if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out Uri? target) ||
                (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                vm.ReportPlaybackError("The browser player URL is invalid. Retry the stream.");
                return;
            }

            var setupTimer = System.Diagnostics.Stopwatch.StartNew();
            int generation = Interlocked.Increment(ref _webNavigationGeneration);
            string sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await _webViewConfigurationLock.WaitAsync();
            try
            {
                if (!IsCurrentWebNavigation(vm, requestedUrl, generation))
                {
                    return;
                }

                _telemetrySessionToken = sessionToken;
                DetachPlaybackFrames();
                _telemetryExpectedSource = target.AbsoluteUri;
                _telemetryInitialNavigationPending = true;

                // An initialized control must retain its original environment.
                // Recreating one on Retry throws before navigation can begin.
                if (WebViewPlayer.CoreWebView2 == null)
                    await EnsureWebViewWithUBlockAsync(WebViewPlayer);
                if (!IsCurrentWebNavigation(vm, requestedUrl, generation))
                {
                    return;
                }

                CoreWebView2 core = WebViewPlayer.CoreWebView2 ??
                    throw new InvalidOperationException("WebView2 did not create a browser core.");
                ConfigureWebViewFullscreenHandling(core);
                ConfigureNavigationStatus(core);
                await ConfigureAdBlockerAsync(core);
                await ConfigureWebRequestContextAsync(core, vm, target);
                _browserAudioSelectionPending = !string.IsNullOrEmpty(vm.BrowserAudioPreference);
                await ConfigurePlaybackTelemetryAsync(core, sessionToken, vm.BrowserAudioPreference);
                _pendingBrowserResumeSeconds = vm.TryConsumePendingWebResumePosition(out double resumeSeconds)
                    ? resumeSeconds : null;

                if (!IsCurrentWebNavigation(vm, requestedUrl, generation))
                {
                    return;
                }

                AppLogger.Log($"[Playback startup] WebView setup ready in {setupTimer.ElapsedMilliseconds} ms; navigating player page.");
                core.Navigate(target.AbsoluteUri);
            }
            catch (ObjectDisposedException) when (_webViewDisposed)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error navigating WebView in PlaybackView: {ex.Message}", "ERROR");
                if (!_webViewDisposed && ReferenceEquals(DataContext, vm))
                {
                    vm.ReportPlaybackError("The browser player could not be opened. Retry the stream.");
                }
            }
            finally
            {
                _webViewConfigurationLock.Release();
            }
        }

        private bool IsCurrentWebNavigation(
            ViewModels.PlaybackViewModel vm,
            string requestedUrl,
            int generation) =>
            !_webViewDisposed &&
            _viewLoaded &&
            ReferenceEquals(DataContext, vm) &&
            vm.IsWebViewActive &&
            vm.EmbedUrl.Equals(requestedUrl, StringComparison.Ordinal) &&
            generation == Volatile.Read(ref _webNavigationGeneration);

        private void ConfigureNavigationStatus(CoreWebView2 core)
        {
            if (ReferenceEquals(_navigationEventCore, core))
            {
                return;
            }

            if (_navigationEventCore != null)
            {
                _navigationEventCore.NavigationStarting -= WebView_NavigationStarting;
                _navigationEventCore.NavigationCompleted -= WebView_NavigationCompleted;
            }

            _navigationEventCore = core;
            _navigationEventCore.NavigationStarting += WebView_NavigationStarting;
            _navigationEventCore.NavigationCompleted += WebView_NavigationCompleted;
        }

        private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!ReferenceEquals(sender, _navigationEventCore) ||
                !_telemetryInitialNavigationPending ||
                !e.IsRedirected ||
                !Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? redirect) ||
                (redirect.Scheme != Uri.UriSchemeHttp && redirect.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            _telemetryExpectedSource = redirect.AbsoluteUri;
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!ReferenceEquals(sender, _navigationEventCore))
            {
                return;
            }

            _telemetryInitialNavigationPending = false;
            if (DataContext is not ViewModels.PlaybackViewModel { IsWebViewActive: true } vm)
            {
                return;
            }

            if (!e.IsSuccess)
            {
                vm.ReportPlaybackError($"The browser player failed to load ({e.WebErrorStatus}). Retry the stream.");
            }
            else
            {
                vm.ReportWebPageLoaded();
            }
        }

        private void ConfigureWebViewFullscreenHandling(CoreWebView2 core)
        {
            if (ReferenceEquals(_fullscreenEventCore, core))
            {
                return;
            }

            if (_fullscreenEventCore != null)
            {
                _fullscreenEventCore.ContainsFullScreenElementChanged -= WebView_ContainsFullScreenElementChanged;
            }

            _fullscreenEventCore = core;
            _fullscreenEventCore.ContainsFullScreenElementChanged += WebView_ContainsFullScreenElementChanged;
        }

        private void WebView_ContainsFullScreenElementChanged(object? sender, object e)
        {
            if (sender is not CoreWebView2 core)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (core.ContainsFullScreenElement)
                {
                    _webViewRequestedFullscreen = true;
                    EnterFullscreen("[PlaybackView] WebView player requested fullscreen.");
                }
                else if (_webViewRequestedFullscreen)
                {
                    _webViewRequestedFullscreen = false;
                    ExitFullscreen("[PlaybackView] WebView player exited fullscreen.");
                }
            });
        }

        private async Task ConfigureAdBlockerAsync(CoreWebView2 core)
        {
            if (ReferenceEquals(_adBlockEventCore, core) &&
                ReferenceEquals(_adBlockScriptCore, core) &&
                !string.IsNullOrWhiteSpace(_adBlockScriptId))
            {
                return;
            }

            DetachAdBlocker();

            // Register JS ad-block script at document creation
            string scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockScript);
            if (_webViewDisposed)
            {
                RemoveRegisteredScript(core, scriptId, "ad blocker");
                throw new ObjectDisposedException(nameof(PlaybackView));
            }

            _adBlockScriptId = scriptId;
            _adBlockScriptCore = core;

            // Block network requests to known ad domains
            _adBlockEventCore = core;
            try
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += WebView_WebResourceRequested;
                core.NewWindowRequested += WebView_NewWindowRequested;
            }
            catch
            {
                DetachAdBlocker();
                throw;
            }

            AppLogger.Log("[AdBlock] Playback WebView2 ad-blocker configured.");
        }

        private void WebView_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (sender is not CoreWebView2 core || !ReferenceEquals(core, _adBlockEventCore))
            {
                return;
            }

            try
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
                string host = uri.Host.TrimStart('.');
                bool blocked = false;
                foreach (var domain in BlockedDomains)
                {
                    if (host == domain || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked)
                {
                    AppLogger.Log($"[AdBlock] Blocked request: {e.Request.Uri}");
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                }
            }
            catch
            {
            }
        }

        private void WebView_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (!ReferenceEquals(sender, _adBlockEventCore))
            {
                return;
            }

            e.Handled = true;
            AppLogger.Log($"[AdBlock] Blocked new window popup: {e.Uri}");
        }

        private async Task ConfigureWebRequestContextAsync(
            CoreWebView2 core,
            ViewModels.PlaybackViewModel vm,
            Uri target)
        {
            ViewModels.WebPlaybackRequestContext context = vm.GetWebPlaybackRequestContext();
            _defaultWebViewUserAgent ??= core.Settings.UserAgent;
            core.Settings.UserAgent = IsSafeHeaderValue(context.UserAgent)
                ? context.UserAgent
                : _defaultWebViewUserAgent;

            ClearInjectedWebCookies();
            await InjectPlaybackCookiesAsync(core, target, context.Cookie);

            if (!ReferenceEquals(_requestHeaderEventCore, core))
            {
                if (_requestHeaderEventCore != null)
                {
                    _requestHeaderEventCore.WebResourceRequested -= WebView_RequestHeadersNeeded;
                }

                _requestHeaderEventCore = core;
                _requestHeaderEventCore.WebResourceRequested += WebView_RequestHeadersNeeded;
            }

            _webRequestHeaderOrigin = target.GetLeftPart(UriPartial.Authority);
            _webRequestReferer = IsValidWebReferer(context.Referer)
                ? context.Referer
                : string.Empty;
            _webRequestHeaders = context.RequestHeaders;
        }

        private void WebView_RequestHeadersNeeded(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!ReferenceEquals(sender, _requestHeaderEventCore) ||
                !ShouldApplyWebRequestHeaders(e.Request.Uri, _webRequestHeaderOrigin))
            {
                return;
            }

            try
            {
                foreach (var header in _webRequestHeaders)
                {
                    if (IsAllowedWebRequestHeader(header.Key, header.Value))
                    {
                        e.Request.Headers.SetHeader(header.Key, header.Value);
                    }
                }

                if (!string.IsNullOrWhiteSpace(_webRequestReferer))
                {
                    e.Request.Headers.SetHeader("Referer", _webRequestReferer);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to apply scoped WebView request headers: {ex.Message}", "WARNING");
            }
        }

        private async Task InjectPlaybackCookiesAsync(CoreWebView2 core, Uri target, string cookieHeader)
        {
            IReadOnlyList<KeyValuePair<string, string>> cookies = ParseCookieHeader(cookieHeader);
            if (cookies.Count == 0)
            {
                return;
            }

            CoreWebView2CookieManager manager = core.CookieManager;
            IReadOnlyList<CoreWebView2Cookie> existingCookies =
                await manager.GetCookiesAsync(target.AbsoluteUri);
            if (_webViewDisposed)
            {
                throw new ObjectDisposedException(nameof(PlaybackView));
            }

            _injectedCookieManager = manager;
            try
            {
                foreach (var pair in cookies)
                {
                    const string path = "/";
                    string domain = target.Host;
                    CoreWebView2Cookie? previous = existingCookies.FirstOrDefault(cookie =>
                        cookie.Name.Equals(pair.Key, StringComparison.Ordinal) &&
                        cookie.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                        cookie.Path.Equals(path, StringComparison.Ordinal));
                    CoreWebView2Cookie? previousCopy = previous == null
                        ? null
                        : manager.CopyCookie(previous);

                    CoreWebView2Cookie cookie = manager.CreateCookie(pair.Key, pair.Value, domain, path);
                    cookie.IsSecure = target.Scheme == Uri.UriSchemeHttps;
                    manager.AddOrUpdateCookie(cookie);
                    _injectedCookies.Add(new InjectedCookieState(
                        pair.Key,
                        domain,
                        path,
                        previousCopy));
                }
            }
            catch
            {
                ClearInjectedWebCookies();
                throw;
            }
        }

        private void ClearInjectedWebCookies()
        {
            CoreWebView2CookieManager? manager = _injectedCookieManager;
            if (manager == null)
            {
                _injectedCookies.Clear();
                return;
            }

            foreach (InjectedCookieState state in _injectedCookies)
            {
                try
                {
                    manager.DeleteCookiesWithDomainAndPath(state.Name, state.Domain, state.Path);
                    if (state.PreviousCookie != null)
                    {
                        manager.AddOrUpdateCookie(state.PreviousCookie);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[PlaybackView] Failed to clear an injected playback cookie: {ex.Message}", "WARNING");
                }
            }

            _injectedCookies.Clear();
            _injectedCookieManager = null;
        }

        internal static IReadOnlyList<KeyValuePair<string, string>> ParseCookieHeader(string? cookieHeader)
        {
            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!IsSafeHeaderValue(cookieHeader))
            {
                return Array.Empty<KeyValuePair<string, string>>();
            }

            foreach (string segment in cookieHeader!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = segment.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string name = segment[..separator].Trim();
                string value = segment[(separator + 1)..].Trim();
                if (!IsCookieName(name) ||
                    IsCookieAttributeName(name) ||
                    !IsSafeHeaderValue(value))
                {
                    continue;
                }

                cookies[name] = value;
            }

            return cookies.ToArray();
        }

        internal static bool ShouldApplyWebRequestHeaders(string? requestUri, string? expectedOrigin) =>
            IsSameWebOrigin(requestUri, expectedOrigin);

        internal static bool IsAllowedWebRequestHeader(string? name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !IsHttpToken(name) ||
                !IsSafeHeaderValue(value) ||
                name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !ForbiddenWebRequestHeaders.Contains(name);
        }

        private static bool IsValidWebReferer(string? value) =>
            IsSafeHeaderValue(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static bool IsSafeHeaderValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 8192 &&
            value.IndexOfAny(['\r', '\n', '\0']) < 0;

        private static bool IsCookieName(string value) =>
            value.Length is > 0 and <= 256 && IsHttpToken(value);

        private static bool IsCookieAttributeName(string value) =>
            value.Equals("Path", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Domain", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Expires", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SameSite", StringComparison.OrdinalIgnoreCase);

        private static bool IsHttpToken(string value)
        {
            const string separators = "()<>@,;:\\\"/[]?={} \t";
            return value.All(character =>
                character > 0x20 &&
                character < 0x7f &&
                !separators.Contains(character));
        }

        private async Task ConfigurePlaybackTelemetryAsync(CoreWebView2 core, string sessionToken, string audioPreference)
        {
            if (!ReferenceEquals(_telemetryEventCore, core))
            {
                if (_telemetryEventCore != null)
                {
                    _telemetryEventCore.WebMessageReceived -= WebView_WebMessageReceived;
                    _telemetryEventCore.FrameCreated -= WebView_FrameCreated;
                }

                _telemetryEventCore = core;
                _telemetryEventCore.WebMessageReceived += WebView_WebMessageReceived;
                _telemetryEventCore.FrameCreated += WebView_FrameCreated;
            }

            RemoveTelemetryScript();
            string scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildPlaybackTelemetryScript(sessionToken) + BuildBrowserAudioSelectionScript(audioPreference, sessionToken));
            if (_webViewDisposed)
            {
                RemoveRegisteredScript(core, scriptId, "playback telemetry");
                throw new ObjectDisposedException(nameof(PlaybackView));
            }

            _telemetryScriptId = scriptId;
            _telemetryScriptCore = core;
            AppLogger.Log("[PlaybackView] Authenticated WebView playback telemetry configured.");
        }

        internal sealed class FramePlaybackDocument
        {
            public FramePlaybackDocument(string source, ulong navigationId)
            {
                Source = source;
                NavigationId = navigationId;
            }

            public string Source { get; }
            public ulong NavigationId { get; }
            public string SessionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            public bool IsReady { get; private set; }
            private bool _invalidated;

            public bool MarkReady(ulong navigationId)
            {
                IsReady = !_invalidated && navigationId == NavigationId && IsSameWebOrigin(Source, Source);
                return IsReady;
            }

            public void Invalidate() { _invalidated = true; IsReady = false; }

            public bool TryRead(string message, string source, out AuthenticatedWebTelemetry telemetry)
            {
                telemetry = default;
                return IsReady && TryParseAuthenticatedTelemetry(message, source, Source, SessionToken, out telemetry);
            }
        }

        private void WebView_FrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
        {
            if (_webViewDisposed || !_viewLoaded || _playbackFrames.Count >= 32 ||
                !(ReferenceEquals(sender, _telemetryEventCore) || sender is CoreWebView2Frame parent && _playbackFrames.ContainsKey(parent)))
                return;
            var frame = e.Frame;
            if (_playbackFrames.ContainsKey(frame)) return;
            _playbackFrames.Add(frame, new FramePlaybackDocument(string.Empty, 0));
            frame.NavigationStarting += Frame_NavigationStarting;
            frame.DOMContentLoaded += Frame_DOMContentLoaded;
            frame.WebMessageReceived += Frame_WebMessageReceived;
            frame.FrameCreated += WebView_FrameCreated;
            frame.Destroyed += Frame_Destroyed;
        }

        private void Frame_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (sender is not CoreWebView2Frame frame || !_playbackFrames.TryGetValue(frame, out var previous)) return;
            previous.Invalidate();
            if (ReferenceEquals(_activePlaybackFrame, frame)) _activePlaybackFrame = null;
            _playbackFrames[frame] = new FramePlaybackDocument(e.Uri, e.NavigationId);
        }

        private async void Frame_DOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            if (_webViewDisposed || !_viewLoaded || sender is not CoreWebView2Frame frame ||
                !_playbackFrames.TryGetValue(frame, out var document) || !document.MarkReady(e.NavigationId)) return;
            try
            {
                await frame.ExecuteScriptAsync(BuildPlaybackTelemetryScript(document.SessionToken, topOnly: false));
            }
            catch (Exception ex)
            {
                document.Invalidate();
                AppLogger.Log($"[PlaybackView] Frame telemetry setup failed: {ex.Message}", "WARNING");
            }
        }

        private void Frame_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_webViewDisposed || !_viewLoaded || _browserAudioSelectionPending || sender is not CoreWebView2Frame frame ||
                !_playbackFrames.TryGetValue(frame, out var document) ||
                !IsSameWebOrigin(WebViewPlayer.CoreWebView2?.Source, _telemetryExpectedSource)) return;
            try
            {
                if (!document.TryRead(e.TryGetWebMessageAsString(), e.Source, out var telemetry) || telemetry.Duration < 120) return;
                if (_activePlaybackFrame != null && !ReferenceEquals(_activePlaybackFrame, frame)) return;
                if (_activePlaybackFrame == null)
                {
                    _activePlaybackFrame = frame;
                    AppLogger.Log($"[PlaybackView] Selected video frame at {new Uri(document.Source).GetLeftPart(UriPartial.Authority)}.");
                }
                ApplyBrowserTelemetry(telemetry);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Ignored invalid frame telemetry: {ex.Message}", "WARNING");
            }
        }

        private async void ApplyBrowserTelemetry(AuthenticatedWebTelemetry telemetry)
        {
            if (_webViewDisposed || !_viewLoaded || DataContext is not ViewModels.PlaybackViewModel { IsWebViewActive: true } vm) return;
            if (!vm.ResumeLoadCompleted.IsCompleted && telemetry.Type == "ums-video-progress") return;
            if (_pendingBrowserResumeSeconds == null && vm.TryConsumePendingWebResumePosition(out double loadedResume))
                _pendingBrowserResumeSeconds = loadedResume;
            // Restore only the selected video document, not every embedded frame.
            if (_pendingBrowserResumeSeconds is { } resume && telemetry.Duration >= 120)
            {
                _pendingBrowserResumeSeconds = null;
                if (resume > 5 && resume < telemetry.Duration - 20)
                {
                    int generation = _webNavigationGeneration;
                    try
                    {
                        if (await ExecuteWebPlaybackCommandAsync("seek", resume) == "true")
                        {
                            AppLogger.Log($"[Resume] Applied browser position {resume:0.0}s to the selected video document.");
                            return; // Its seeked event supplies the new position.
                        }
                    }
                    catch (Exception ex) { AppLogger.Log($"[Resume] Browser seek failed: {ex.Message}", "WARNING"); }
                    if (_webViewDisposed || generation != _webNavigationGeneration || !ReferenceEquals(DataContext, vm)) return;
                    _pendingBrowserResumeSeconds = resume;
                }
            }
            if (telemetry.Type == "ums-video-progress")
                vm.ReportWebPlaybackProgress(telemetry.CurrentTime, telemetry.Duration, telemetry.Ended, telemetry.Paused);
            else
                vm.ReportWebPlaybackAction(telemetry.Action, telemetry.CurrentTime, telemetry.Duration);
        }

        private void Frame_Destroyed(object? sender, object e)
        {
            if (sender is CoreWebView2Frame frame) DetachPlaybackFrame(frame);
        }

        private void DetachPlaybackFrame(CoreWebView2Frame frame)
        {
            if (!_playbackFrames.Remove(frame, out var document)) return;
            document.Invalidate();
            if (ReferenceEquals(_activePlaybackFrame, frame)) _activePlaybackFrame = null;
            try
            {
                frame.NavigationStarting -= Frame_NavigationStarting;
                frame.DOMContentLoaded -= Frame_DOMContentLoaded;
                frame.WebMessageReceived -= Frame_WebMessageReceived;
                frame.FrameCreated -= WebView_FrameCreated;
                frame.Destroyed -= Frame_Destroyed;
            }
            catch (Exception ex) { AppLogger.Log($"[PlaybackView] Frame already released during cleanup: {ex.Message}", "WARNING"); }
        }

        private void DetachPlaybackFrames()
        {
            foreach (var frame in _playbackFrames.Keys.ToArray()) DetachPlaybackFrame(frame);
            _activePlaybackFrame = null;
        }

        internal static string BuildPlaybackTelemetryScript(string sessionToken, bool topOnly = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
            return PlaybackTelemetryScriptTemplate.Replace(
                "__UMS_SESSION_TOKEN__",
                JsonSerializer.Serialize(sessionToken),
                StringComparison.Ordinal).Replace("__UMS_TOP_ONLY__", topOnly ? "true" : "false", StringComparison.Ordinal);
        }

        internal static string BuildBrowserAudioSelectionScript(string preference, string sessionToken)
        {
            if (preference is not ("sub" or "dub")) return string.Empty;
            return $$"""
                ;(function() {
                    if (window !== window.top) return;
                    const desired = {{JsonSerializer.Serialize(preference)}};
                    const sessionToken = {{JsonSerializer.Serialize(sessionToken)}};
                    let attempts = 0, done = false;
                    const bridge = window.chrome && window.chrome.webview;
                    const post = bridge && bridge.postMessage.bind(bridge);
                    function mode(value) {
                        const text = String(value || '').trim().toLowerCase();
                        if (/^(dub|dubbed|english dub)$/.test(text)) return 'dub';
                        if (/^(sub|subbed|subtitled)$/.test(text)) return 'sub';
                        return '';
                    }
                    function finish(selected) {
                        if (done) return;
                        done = true;
                        if (post) post(JSON.stringify({type:'ums-audio-selection', sessionToken,
                            preference:desired, selected:!!selected}));
                    }
                    function scan() {
                        if (done || !window.__umsIsPlaybackSession || !window.__umsIsPlaybackSession(sessionToken)) return;
                        try {
                            for (const select of document.querySelectorAll('select')) {
                                const choices = Array.from(select.options);
                                if (!choices.some(o => mode(o.value || o.textContent) === 'sub') ||
                                    !choices.some(o => mode(o.value || o.textContent) === 'dub')) continue;
                                const option = choices.find(o => mode(o.value || o.textContent) === desired && !o.disabled);
                                if (!option || select.disabled) continue;
                                if (select.value === option.value) { finish(true); return; }
                                const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
                                setter.call(select, option.value);
                                select.dispatchEvent(new Event('change', {bubbles:true}));
                                return;
                            }
                            for (const trigger of document.querySelectorAll('button[aria-haspopup="listbox"], [role="combobox"]')) {
                                if (!/^(audio|language)$/i.test(trigger.getAttribute('aria-label') || '') || trigger.disabled) continue;
                                if (mode(trigger.textContent) === desired) { finish(true); return; }
                                const root = trigger.parentElement;
                                if (trigger.getAttribute('aria-expanded') !== 'true') { trigger.click(); return; }
                                const option = Array.from(root.querySelectorAll('button,[role="option"]')).find(o =>
                                    o !== trigger && mode(o.textContent) === desired && !o.disabled &&
                                    o.getAttribute('aria-disabled') !== 'true' && o.getClientRects().length > 0);
                                if (option) { option.click(); return; }
                            }
                        } catch(e) {}
                    }
                    const timer = setInterval(() => {
                        if (++attempts > 40) finish(false);
                        if (done) { clearInterval(timer); return; }
                        scan();
                    }, 500);
                    document.addEventListener('DOMContentLoaded', scan, {once:true});
                    scan();
                })();
                """;
        }

        private void RemoveTelemetryScript()
        {
            RemoveRegisteredScript(
                _telemetryScriptCore,
                _telemetryScriptId,
                "playback telemetry");
            _telemetryScriptCore = null;
            _telemetryScriptId = null;
        }

        private void DetachAdBlocker()
        {
            if (_adBlockEventCore != null)
            {
                try
                {
                    _adBlockEventCore.WebResourceRequested -= WebView_WebResourceRequested;
                    _adBlockEventCore.NewWindowRequested -= WebView_NewWindowRequested;
                    _adBlockEventCore.RemoveWebResourceRequestedFilter(
                        "*",
                        CoreWebView2WebResourceContext.All);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[PlaybackView] Failed to detach the ad blocker: {ex.Message}", "WARNING");
                }
            }

            _adBlockEventCore = null;
            RemoveRegisteredScript(_adBlockScriptCore, _adBlockScriptId, "ad blocker");
            _adBlockScriptCore = null;
            _adBlockScriptId = null;
        }

        private void DetachCoreWebView2Handlers()
        {
            if (_fullscreenEventCore != null)
            {
                try
                {
                    _fullscreenEventCore.ContainsFullScreenElementChanged -=
                        WebView_ContainsFullScreenElementChanged;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[PlaybackView] Failed to detach fullscreen handling: {ex.Message}", "WARNING");
                }

                _fullscreenEventCore = null;
            }

            if (_navigationEventCore != null)
            {
                try
                {
                    _navigationEventCore.NavigationStarting -= WebView_NavigationStarting;
                    _navigationEventCore.NavigationCompleted -= WebView_NavigationCompleted;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[PlaybackView] Failed to detach navigation handling: {ex.Message}", "WARNING");
                }

                _navigationEventCore = null;
            }

            if (_telemetryEventCore != null)
            {
                try
                {
                    _telemetryEventCore.WebMessageReceived -= WebView_WebMessageReceived;
                    _telemetryEventCore.FrameCreated -= WebView_FrameCreated;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[PlaybackView] Failed to detach playback telemetry: {ex.Message}", "WARNING");
                }

                _telemetryEventCore = null;
            }

            if (_requestHeaderEventCore != null)
            {
                try
                {
                    _requestHeaderEventCore.WebResourceRequested -= WebView_RequestHeadersNeeded;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[PlaybackView] Failed to detach scoped request headers: {ex.Message}", "WARNING");
                }

                _requestHeaderEventCore = null;
            }

            DetachAdBlocker();
            RemoveTelemetryScript();
            _pendingBrowserResumeSeconds = null;
            DetachPlaybackFrames();
        }

        private static void RemoveRegisteredScript(
            CoreWebView2? core,
            string? scriptId,
            string description)
        {
            if (core == null || string.IsNullOrWhiteSpace(scriptId))
            {
                return;
            }

            try
            {
                core.RemoveScriptToExecuteOnDocumentCreated(scriptId);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to remove the {description} script: {ex.Message}", "WARNING");
            }
        }

        private async void Vm_WebPlaybackCommandRequested(object? sender, ViewModels.WebPlaybackCommandEventArgs e)
        {
            try
            {
                if (WebViewPlayer.CoreWebView2 == null)
                {
                    return;
                }

                await ExecuteWebPlaybackCommandAsync(e.Action, e.PositionSeconds);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Failed to execute remote WebView playback command: {ex.Message}", "WARNING");
            }
        }

        private Task<string> ExecuteWebPlaybackCommandAsync(string action, double positionSeconds)
        {
            if (_webViewDisposed || !_viewLoaded || WebViewPlayer.CoreWebView2 is not { } core)
                return Task.FromResult("false");
            if (_activePlaybackFrame is { } frame)
            {
                if (!_playbackFrames.TryGetValue(frame, out var document) || !document.IsReady)
                    return Task.FromResult("false");
                return frame.ExecuteScriptAsync(BuildWebPlaybackCommandScript(action, positionSeconds, document.SessionToken));
            }
            return core.ExecuteScriptAsync(BuildWebPlaybackCommandScript(action, positionSeconds, _telemetrySessionToken));
        }

        internal static string BuildWebPlaybackCommandScript(string action, double positionSeconds, string sessionToken)
        {
            string actionJson = JsonSerializer.Serialize(action ?? string.Empty);
            string targetSeconds = positionSeconds.ToString("R", CultureInfo.InvariantCulture);
            string tokenJson = JsonSerializer.Serialize(sessionToken);
            return $$"""
                (function() {
                    if (!window.__umsIsPlaybackSession || !window.__umsIsPlaybackSession({{tokenJson}})) return false;
                    const action = {{actionJson}};
                    const targetSeconds = {{targetSeconds}};
                    const videos = Array.from(document.querySelectorAll('video'));
                    const video = videos
                        .map(v => {
                            const rect = v.getBoundingClientRect();
                            const duration = Number.isFinite(v.duration) ? v.duration : 0;
                            const visible = rect.width >= 80 && rect.height >= 45;
                            const score = (visible ? rect.width * rect.height : 0) +
                                          (duration >= 120 ? 100000000 : 0) +
                                          (!v.paused ? 10000000 : 0);
                            return { v, score };
                        })
                        .sort((a, b) => b.score - a.score)[0]?.v;
                    if (!video) return false;

                    function seekIfUseful() {
                        try {
                            const duration = Number.isFinite(video.duration) ? video.duration : 0;
                            const currentTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
                            if (!Number.isFinite(targetSeconds) || targetSeconds < 0 ||
                                (duration > 0 && targetSeconds > duration + 2)) return false;
                            if (Math.abs(currentTime - targetSeconds) > 0.5) {
                                video.currentTime = targetSeconds;
                            }
                            return Math.abs(video.currentTime - targetSeconds) <= 0.5;
                        } catch(e) { return false; }
                    }

                    try {
                        if (action === 'play' || action === 'buffer_resume') {
                            seekIfUseful();
                            const promise = video.play();
                            if (promise && typeof promise.catch === 'function') promise.catch(error => {
                                if (error && error.name === 'NotAllowedError' && window.chrome && window.chrome.webview &&
                                    window.__umsIsPlaybackSession({{tokenJson}})) {
                                    window.chrome.webview.postMessage(JSON.stringify({
                                        type:'ums-video-action', action:'play-blocked', sessionToken:{{tokenJson}},
                                        currentTime:Number.isFinite(video.currentTime) ? video.currentTime : 0,
                                        duration:Number.isFinite(video.duration) ? video.duration : 0
                                    }));
                                }
                            });
                        } else if (action === 'pause' || action === 'buffer_pause') {
                            seekIfUseful();
                            video.pause();
                        } else if (action === 'seek' || action === 'seeked') {
                            return seekIfUseful();
                        } else if (action === 'rate') {
                            if (Number.isFinite(targetSeconds) && targetSeconds >= 0.25 && targetSeconds <= 4) {
                                video.playbackRate = targetSeconds;
                            }
                        }
                        return true;
                    } catch(e) {
                        return false;
                    }
                })();
                """;

        }

        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_webViewDisposed ||
                !_viewLoaded ||
                sender is not CoreWebView2 core ||
                !ReferenceEquals(core, _telemetryEventCore))
            {
                return;
            }

            try
            {
                string message = e.TryGetWebMessageAsString();
                if (DataContext is ViewModels.PlaybackViewModel vm && TryParseAudioSelection(
                        message, e.Source, _telemetryExpectedSource, _telemetrySessionToken,
                        vm.BrowserAudioPreference, out bool selected))
                {
                    _browserAudioSelectionPending = false;
                    string audio = vm.BrowserAudioPreference == "dub" ? "Dub" : "Sub";
                    vm.BrowserAudioStatus = selected ? $"Opened with {audio} selected on the site."
                        : $"Select {audio} in the page; automatic selection was unavailable.";
                    AppLogger.Log($"[Playback audio] Requested {audio}; site selection confirmed={selected}.");
                    return;
                }
                if (!TryParseAuthenticatedTelemetry(
                        message,
                        e.Source,
                        _telemetryExpectedSource,
                        _telemetrySessionToken,
                        out AuthenticatedWebTelemetry telemetry))
                {
                    AppLogger.Log("[PlaybackView] Rejected unauthenticated or invalid WebView telemetry.", "WARNING");
                    return;
                }

                if (_activePlaybackFrame == null && !_browserAudioSelectionPending)
                    ApplyBrowserTelemetry(telemetry);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlaybackView] Ignored malformed WebView telemetry message: {ex.Message}", "WARNING");
            }
        }

        internal readonly record struct AuthenticatedWebTelemetry(
            string Type,
            string Action,
            double CurrentTime,
            double Duration,
            bool Ended,
            bool? Paused = null);

        internal static bool TryParseAudioSelection(string message, string source, string expectedSource,
            string token, string preference, out bool selected)
        {
            selected = false;
            if (message.Length > 4096 || string.IsNullOrEmpty(token) || preference is not ("sub" or "dub") ||
                !IsSameWebOrigin(source, expectedSource)) return false;
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "ums-audio-selection" ||
                    !FixedTimeTokenEquals(root.GetProperty("sessionToken").GetString(), token) ||
                    root.GetProperty("preference").GetString() != preference) return false;
                selected = root.GetProperty("selected").GetBoolean();
                return true;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
        }

        internal static bool TryParseAuthenticatedTelemetry(
            string? message,
            string? messageSource,
            string? expectedSource,
            string? expectedSessionToken,
            out AuthenticatedWebTelemetry telemetry)
        {
            telemetry = default;
            if (string.IsNullOrWhiteSpace(message) ||
                message.Length > 4096 ||
                string.IsNullOrWhiteSpace(expectedSessionToken) ||
                !IsSameWebOrigin(messageSource, expectedSource))
            {
                return false;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(message);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("sessionToken", out JsonElement tokenProperty) ||
                    tokenProperty.ValueKind != JsonValueKind.String ||
                    !FixedTimeTokenEquals(tokenProperty.GetString(), expectedSessionToken) ||
                    !root.TryGetProperty("type", out JsonElement typeProperty) ||
                    typeProperty.ValueKind != JsonValueKind.String ||
                    !TryReadTelemetryNumber(root, "currentTime", out double currentTime) ||
                    !TryReadTelemetryNumber(root, "duration", out double duration) ||
                    currentTime < 0 ||
                    duration < 0 ||
                    currentTime > 86_400 ||
                    duration > 86_400 ||
                    (duration > 0 && currentTime > duration + 60))
                {
                    return false;
                }

                string type = typeProperty.GetString() ?? string.Empty;
                if (type == "ums-video-progress")
                {
                    bool? paused = null;
                    if (root.TryGetProperty("paused", out JsonElement pausedProperty))
                    {
                        if (pausedProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                        paused = pausedProperty.GetBoolean();
                    }
                    bool ended = false;
                    if (root.TryGetProperty("ended", out JsonElement endedProperty))
                    {
                        if (endedProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            return false;
                        }

                        ended = endedProperty.GetBoolean();
                    }

                    telemetry = new AuthenticatedWebTelemetry(
                        type,
                        string.Empty,
                        currentTime,
                        duration,
                        ended,
                        paused);
                    return true;
                }

                if (type != "ums-video-action" ||
                    !root.TryGetProperty("action", out JsonElement actionProperty) ||
                    actionProperty.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string action = actionProperty.GetString() ?? string.Empty;
                if (action is not ("play" or "pause" or "seek" or "seeked" or "seeking" or
                    "waiting" or "buffer_pause" or "playing" or "buffer_resume" or "ended" or "play-blocked"))
                {
                    return false;
                }

                telemetry = new AuthenticatedWebTelemetry(
                    type,
                    action,
                    currentTime,
                    duration,
                    action == "ended");
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool TryReadTelemetryNumber(
            JsonElement root,
            string propertyName,
            out double value)
        {
            value = 0;
            return root.TryGetProperty(propertyName, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetDouble(out value) &&
                   double.IsFinite(value);
        }

        private static bool IsSameWebOrigin(string? first, string? second)
        {
            if (!Uri.TryCreate(first, UriKind.Absolute, out Uri? firstUri) ||
                !Uri.TryCreate(second, UriKind.Absolute, out Uri? secondUri) ||
                (firstUri.Scheme != Uri.UriSchemeHttp && firstUri.Scheme != Uri.UriSchemeHttps) ||
                (secondUri.Scheme != Uri.UriSchemeHttp && secondUri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            return firstUri.Scheme.Equals(secondUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   firstUri.IdnHost.Equals(secondUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                   firstUri.Port == secondUri.Port;
        }

        private static bool FixedTimeTokenEquals(string? candidate, string expected)
        {
            if (candidate == null || candidate.Length != expected.Length)
            {
                return false;
            }

            byte[] candidateBytes = Encoding.UTF8.GetBytes(candidate);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            return CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
        }
    }
}
