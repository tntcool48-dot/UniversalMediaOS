using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.WPF;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class PlaybackViewCollectibilityTests
{
    [Fact]
    public Task ClosedViewsAreCollectibleWhileTheirParentWindowRemainsOpen() =>
        NativeShortMediaCompletionTests.OnDispatcher(async () =>
        {
            // This hidden parent creates a real HWND/input registration without
            // taking focus or displaying a UI acceptance sample.
            var window = new Window
            {
                Width = 640, Height = 400, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                WeakReference[] closed = Enumerable.Range(0, 4).Select(_ => OpenAndClose(window)).ToArray();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                for (int attempt = 0; attempt < 3 && closed.Any(reference => reference.IsAlive); attempt++)
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
    private static WeakReference OpenAndClose(Window window)
    {
        var resources = ControlThemeRenderTests.LoadResources(true);
        resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        var view = new PlaybackView(resources);
        window.Content = view;
        if (!window.IsVisible) window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var video = (VideoView)view.FindName("VlcPlayer");
        var sink = (IKeyboardInputSink)video.Template.FindName("PART_PlayerHost", video);
        Assert.NotNull(sink.KeyboardInputSite);
        view.CloseForTab();
        view.CloseForTab(); // Repeated final-close requests remain harmless.
        Assert.Null(sink.KeyboardInputSite);
        window.Content = null;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return new WeakReference(view);
    }
}
