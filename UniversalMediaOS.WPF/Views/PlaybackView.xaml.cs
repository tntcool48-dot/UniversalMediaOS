using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Views
{
    public partial class PlaybackView : UserControl
    {
        // Domains to block (ads, tracking, popups)
        private static readonly HashSet<string> BlockedDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            "doubleclick.net", "googlesyndication.com", "adservice.google.com",
            "googletagmanager.com", "googletagservices.com", "analytics.google.com",
            "pagead2.googlesyndication.com", "adnxs.com", "rubiconproject.com",
            "openx.net", "pubmatic.com", "criteo.com", "taboola.com", "outbrain.com",
            "amazon-adsystem.com", "advertising.com", "akamaihd.net",
            "adsafeprotected.com", "quantserve.com", "scorecardresearch.com",
            "ads.yahoo.com", "casalemedia.com", "serving-sys.com", "yieldmanager.com",
            "adf.ly", "popads.net", "popcash.net", "exoclick.com", "trafficjunky.net",
            "realsrv.com", "go2speed.org", "ero-advertising.com", "juicyads.com",
            "clickio.com", "bidvertiser.com", "cpmstar.com", "contentabc.com",
            "track.clickadu.com", "clickadu.com", "propellerads.com", "hilltopads.net",
            "trafficstars.com", "adsterra.com", "yllix.com", "zedo.com",
            "googlevideo.com", // blocked only when it's an ad intermediary
        };

        public static HashSet<string> GetBlockedDomains() => BlockedDomains;
        public static string GetAdBlockScript() => AdBlockScript;

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
                            if ((pos === 'fixed' || pos === 'absolute') && z > 9000 && disp !== 'none') {
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
            DataContextChanged += PlaybackView_DataContextChanged;
        }

        private bool _viewLoaded;

        private async void PlaybackView_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewLoaded = true;
            if (DataContext is ViewModels.PlaybackViewModel vm)
            {
                VlcPlayer.MediaPlayer = vm.MediaPlayer;
                vm.PropertyChanged += Vm_PropertyChanged;
                UpdatePlayerVisibility(vm);
                await UpdateWebViewUrlAsync(vm);

                // If media was queued before view was loaded, replay it now
                if (!string.IsNullOrEmpty(vm.PendingMediaPath))
                {
                    AppLogger.Log($"[PlaybackView] Loaded — playing deferred media: '{vm.PendingMediaPath}'");
                    vm.PlayPending();
                }
            }
        }

        private void PlaybackView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ViewModels.PlaybackViewModel oldVm)
            {
                oldVm.PropertyChanged -= Vm_PropertyChanged;
            }
            if (e.NewValue is ViewModels.PlaybackViewModel vm)
            {
                if (_viewLoaded)
                {
                    VlcPlayer.MediaPlayer = vm.MediaPlayer;
                    vm.PropertyChanged += Vm_PropertyChanged;
                    UpdatePlayerVisibility(vm);
                    _ = UpdateWebViewUrlAsync(vm);

                    // Replay pending if already loaded
                    if (!string.IsNullOrEmpty(vm.PendingMediaPath))
                    {
                        AppLogger.Log($"[PlaybackView] DataContext changed while loaded — playing deferred: '{vm.PendingMediaPath}'");
                        vm.PlayPending();
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
                }
                else if (e.PropertyName == nameof(ViewModels.PlaybackViewModel.IsWebViewActive))
                {
                    UpdatePlayerVisibility(vm);
                }
                else if (e.PropertyName == nameof(ViewModels.PlaybackViewModel.PendingMediaPath))
                {
                    // PendingMediaPath was just set from LoadMedia — play it if view is ready
                    if (_viewLoaded && !string.IsNullOrEmpty(vm.PendingMediaPath))
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
            }
        }

        private async Task UpdateWebViewUrlAsync(ViewModels.PlaybackViewModel vm)
        {
            if (vm.IsWebViewActive && !string.IsNullOrEmpty(vm.EmbedUrl))
            {
                try
                {
                    await WebViewPlayer.EnsureCoreWebView2Async(null);
                    ConfigureAdBlocker(WebViewPlayer.CoreWebView2);
                    WebViewPlayer.CoreWebView2.Navigate(vm.EmbedUrl);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Error navigating WebView in PlaybackView: {ex.Message}", "ERROR");
                }
            }
        }

        private static bool _adBlockerConfigured = false;
        private void ConfigureAdBlocker(CoreWebView2 core)
        {
            if (_adBlockerConfigured) return;
            _adBlockerConfigured = true;

            // Register JS ad-block script at document creation
            core.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockScript);

            // Block network requests to known ad domains
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) =>
            {
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
                catch { }
            };

            // Suppress new window popup requests (ads opening new windows)
            core.NewWindowRequested += (s, e) =>
            {
                try
                {
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Handled = true; return; }
                    string host = uri.Host.TrimStart('.');
                    foreach (var domain in BlockedDomains)
                    {
                        if (host == domain || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.Log($"[AdBlock] Blocked popup: {e.Uri}");
                            e.Handled = true;
                            return;
                        }
                    }
                    // Block any popup that isn't from the same origin
                    e.Handled = true;
                    AppLogger.Log($"[AdBlock] Blocked unsolicited new window: {e.Uri}");
                }
                catch { e.Handled = true; }
            };

            AppLogger.Log("[AdBlock] Playback WebView2 ad-blocker configured.");
        }
    }
}
