using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class MangaViewCollectibilityTests
{
    [Fact]
    public Task ClosedNativeReadersAreCollectibleWhileTheirParentWindowRemainsOpen() =>
        NativeShortMediaCompletionTests.OnDispatcher(async () =>
        {
            var window = new Window
            {
                Width = 640, Height = 400, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                WeakReference[] closed = Enumerable.Range(0, 4).SelectMany(_ => OpenAndClose(window)).ToArray();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // Polling IsAlive before a collection can itself keep its last
                // target alive in the async frame. Collect without that poll.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Task.Run(() =>
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    });
                    await Task.Delay(100);
                }
                Assert.True(window.IsLoaded);
                Assert.All(closed, reference => Assert.False(reference.IsAlive));
            }
            finally { window.Close(); }
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] OpenAndClose(Window window)
    {
        var resources = ControlThemeRenderTests.LoadResources(true);
        foreach (string theme in new[] { "Typography", "Elevation" })
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/UniversalMediaOS.WPF;component/Themes/{theme}.xaml", UriKind.Relative)));
        resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        var model = new MangaViewModel(new MangaService()) { CurrentViewMode = 2 };
        var view = new MangaView(resources) { DataContext = model };
        window.Content = view;
        if (!window.IsVisible) window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var web = (WebView2)view.FindName("MangaWebReader");
        Assert.Null(web.CoreWebView2); // This native reader never opened a website.
        var sink = (IKeyboardInputSink)web;
        Assert.NotNull(sink.KeyboardInputSite);
        view.CloseForTab();
        view.CloseForTab();
        model.Dispose();
        window.Content = null;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return [new WeakReference(view), new WeakReference(model)];
    }
}
