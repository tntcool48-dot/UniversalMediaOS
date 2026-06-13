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

        public MangaView()
        {
            InitializeComponent();
            DataContextChanged += MangaView_DataContextChanged;
        }

        private void MangaView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ViewModels.MangaViewModel oldVm)
                oldVm.PropertyChanged -= Vm_PropertyChanged;

            if (e.NewValue is ViewModels.MangaViewModel vm)
                vm.PropertyChanged += Vm_PropertyChanged;
        }

        private async void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
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
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                var env = await PlaybackView.CreateUBlockEnvironmentAsync();
                await MangaWebReader.EnsureCoreWebView2Async(env);
                ConfigureAdBlocker(MangaWebReader.CoreWebView2);
                AppLogger.Log($"[MangaView] Navigating WebView to external chapter: {url}");
                MangaWebReader.CoreWebView2.Navigate(url);
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
