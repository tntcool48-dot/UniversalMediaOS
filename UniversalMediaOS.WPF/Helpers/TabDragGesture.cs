using System;
using System.Windows;

namespace UniversalMediaOS.WPF.Helpers;

/// <summary>
/// Decides when a pointer gesture on the tab strip becomes a tab drag. Only the
/// press that is still held, and that began on a tab header, can drag that tab.
/// </summary>
internal sealed class TabDragGesture<T> where T : class
{
    private Point _start;
    private T? _pressed;

    /// <summary>Any left press in the window, before a header sees it.</summary>
    public void PointerPressed() => _pressed = null;

    /// <summary>A left press that began on this tab's header.</summary>
    public void PressedOnHeader(T tab, Point at)
    {
        _pressed = tab;
        _start = at;
    }

    /// <summary>Any left release in the window: a click that never dragged.</summary>
    public void PointerReleased() => _pressed = null;

    /// <summary>The tab to start dragging for this move, or null.</summary>
    public T? DragToStart(bool leftPressed, Point at, Size minimumDistance)
    {
        if (!leftPressed || _pressed == null) return null;
        if (Math.Abs(at.X - _start.X) < minimumDistance.Width && Math.Abs(at.Y - _start.Y) < minimumDistance.Height)
            return null;
        T tab = _pressed;
        _pressed = null;
        return tab;
    }
}
