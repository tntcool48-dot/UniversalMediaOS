using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Reflection;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Tests.E2E.Tests;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class PlaybackSeekInteractionTests
{
    [Fact]
    public void PersistenceWaitingConsumesNativeInputButRetainsEscapeAndUnrelatedKeys()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var resources = ControlThemeRenderTests.LoadResources(true);
            resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
            using var player = new PlaybackViewModel(new DatabaseContext());
            player.PlaybackDuration = 600_000;
            player.PlaybackTime = 120_000;
            player.Volume = 75;
            player.IsPersistenceWaiting = true;
            var view = new PlaybackView(resources) { DataContext = player };
            using var inputSource = new HwndSource(new HwndSourceParameters("Admission input regression")
                { WindowStyle = 0, Width = 1, Height = 1 });
            try
            {
                var overlay = (Grid)view.FindName("NativeOverlay");
                overlay.DataContext = player;
                overlay.Measure(new Size(900, 500));
                overlay.Arrange(new Rect(0, 0, 900, 500));
                var slider = (Slider)view.FindName("PlaybackSlider");
                slider.ApplyTemplate();
                overlay.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(120_000, player.PlaybackTime);
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent };
                slider.RaiseEvent(down);
                Assert.True(down.Handled);
                Assert.False(player.IsSeekingFromSlider);
                Assert.Equal(120_000, player.PlaybackTime);

                var handler = typeof(PlaybackView).GetMethod("PlaybackRoot_PreviewKeyDown",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (var key in new[] { Key.Space, Key.K, Key.Left, Key.Right, Key.M, Key.F, Key.P })
                {
                    var input = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    handler.Invoke(view, [view, input]);
                    Assert.True(input.Handled);
                }
                Assert.Equal(75, player.Volume);
                Assert.Equal(120_000, player.PlaybackTime);
                Assert.False(player.IsPlaying);

                var options = (Expander)view.FindName("PlaybackOptions");
                options.IsExpanded = true;
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                handler.Invoke(view, [view, escape]);
                Assert.True(escape.Handled);
                Assert.False(options.IsExpanded);
                var tab = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Tab)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                handler.Invoke(view, [view, tab]);
                Assert.False(tab.Handled);

                player.IsPersistenceWaiting = false;
                var mute = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.M)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                handler.Invoke(view, [view, mute]);
                Assert.Equal(0, player.Volume);
            }
            finally { view.CloseForTab(); }
        });
    }

    [Fact]
    public void TrackClickBeginsSeekBeforeWpfUpdatesTheBoundValueAndMouseUpCompletesIt()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var resources = ControlThemeRenderTests.LoadResources(true);
            resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
            using var player = new PlaybackViewModel(new DatabaseContext());
            player.PlaybackDuration = 600_000;
            player.PlaybackTime = 120_000;
            var view = new PlaybackView(resources) { DataContext = player };
            try
            {
                var overlay = (Grid)view.FindName("NativeOverlay");
                var slider = (Slider)view.FindName("PlaybackSlider");
                // The production VideoView supplies this context through its
                // foreground window; this routed-event check creates no window.
                overlay.DataContext = player;
                overlay.Measure(new Size(900, 500));
                overlay.Arrange(new Rect(0, 0, 900, 500));
                slider.ApplyTemplate();
                overlay.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(120_000, slider.Value);
                var statesDuringValueChange = new List<bool>();
                slider.ValueChanged += (_, _) => statesDuringValueChange.Add(player.IsSeekingFromSlider);

                // Raise the real routed mouse event through the production view.
                // WPF's move-to-point class handler changes Value and marks the
                // left-button event handled before a slider instance handler.
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent };
                slider.RaiseEvent(down);
                Assert.True(down.Handled);
                Assert.NotEmpty(statesDuringValueChange);
                Assert.All(statesDuringValueChange, seeking => Assert.True(seeking));
                Assert.True(player.IsSeekingFromSlider);

                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseUpEvent });
                Assert.False(player.IsSeekingFromSlider);
                Assert.Equal(slider.Value, player.PlaybackTime);
                Assert.False(player.IsPlaying);
                Assert.Empty(player.SourceInput);
            }
            finally { view.CloseForTab(); }
        });
    }
}
