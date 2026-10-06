using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Views
{
    public partial class MangaView : UserControl
    {
        private static readonly HashSet<string> BlockedDomains = PlaybackView.GetBlockedDomains();
        private static readonly string AdBlockScript = PlaybackView.GetAdBlockScript();

        private bool _adBlockerConfigured;
        private bool _isLoaded;
        private ViewModels.MangaViewModel? _subscribedViewModel;

        public MangaView()
        {
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
            try
            {
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
        }

        private void DetachViewModel()
        {
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
            if (!_isLoaded ||
                string.IsNullOrEmpty(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? readerUri) ||
                readerUri.Scheme is not ("http" or "https"))
            {
                AppLogger.Log($"[MangaView] Refused unsafe external reader URL: '{url}'", "WARNING");
                return;
            }
            try
            {
                await PlaybackView.EnsureWebViewWithUBlockAsync(MangaWebReader);
                if (!_isLoaded)
                {
                    return;
                }

                ConfigureAdBlocker(MangaWebReader.CoreWebView2);
                AppLogger.Log($"[MangaView] Navigating WebView to external chapter: {readerUri.AbsoluteUri}");
                MangaWebReader.CoreWebView2.Navigate(readerUri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MangaView] WebView navigation failed: {ex.Message}", "ERROR");
            }
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
