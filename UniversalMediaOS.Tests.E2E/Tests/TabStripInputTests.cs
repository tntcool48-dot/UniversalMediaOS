using System.Windows;
using UniversalMediaOS.WPF.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

// The tab strip's drag decision, fed the pointer events MainWindow forwards:
// window-level press and release, a press on a header, and header moves.
public sealed class TabStripInputTests
{
    private static readonly Size Minimum = new(4, 4);

    [Fact]
    public void AClickOnATabDoesNotLeaveItReadyToDragLater()
    {
        var gesture = new TabDragGesture<string>();
        // A plain click on the Settings header.
        gesture.PointerPressed();
        gesture.PressedOnHeader("Settings", new Point(291, 96));
        gesture.PointerReleased();
        // Later a press on the page below, held and moved over the Anime header.
        gesture.PointerPressed();
        Assert.Null(gesture.DragToStart(true, new Point(200, 300), Minimum));
        Assert.Null(gesture.DragToStart(true, new Point(115, 96), Minimum));
    }

    [Fact]
    public void APressElsewhereReplacesAnUnreleasedHeaderPress()
    {
        // The release of the header press happened outside the window.
        var gesture = new TabDragGesture<string>();
        gesture.PointerPressed();
        gesture.PressedOnHeader("Settings", new Point(291, 96));
        gesture.PointerPressed();
        Assert.Null(gesture.DragToStart(true, new Point(115, 96), Minimum));
    }

    [Fact]
    public void AHeldHeaderPressDragsItsTabOnceItMovesFarEnough()
    {
        var gesture = new TabDragGesture<string>();
        gesture.PointerPressed();
        gesture.PressedOnHeader("Downloads", new Point(467, 96));
        Assert.Null(gesture.DragToStart(true, new Point(469, 97), Minimum));
        Assert.Null(gesture.DragToStart(false, new Point(400, 96), Minimum));
        Assert.Equal("Downloads", gesture.DragToStart(true, new Point(400, 96), Minimum));
        // One drag per press.
        Assert.Null(gesture.DragToStart(true, new Point(115, 96), Minimum));
    }
}
