using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Tests.E2E.Tests;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class PlaybackSeekInteractionTests
{
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
