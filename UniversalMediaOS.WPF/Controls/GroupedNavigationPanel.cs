using System;
using System.Windows;
using System.Windows.Controls;

namespace UniversalMediaOS.WPF.Controls;

/// <summary>Keeps media and utility navigation apart, wrapping the utility group as a unit.</summary>
public sealed class GroupedNavigationPanel : Panel
{
    private const double GroupGap = 24;
    private const double RowGap = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2)
            throw new InvalidOperationException("Navigation requires exactly two groups.");

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        Size media = InternalChildren[0].DesiredSize;
        Size utilities = InternalChildren[1].DesiredSize;
        bool wrap = media.Width + GroupGap + utilities.Width > availableSize.Width;
        return new Size(Math.Min(availableSize.Width, wrap ? Math.Max(media.Width, utilities.Width) : media.Width + GroupGap + utilities.Width),
            wrap ? media.Height + RowGap + utilities.Height : Math.Max(media.Height, utilities.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size media = InternalChildren[0].DesiredSize;
        Size utilities = InternalChildren[1].DesiredSize;
        bool wrap = media.Width + GroupGap + utilities.Width > finalSize.Width;
        InternalChildren[0].Arrange(new Rect(0, 0, media.Width, media.Height));
        InternalChildren[1].Arrange(new Rect(Math.Max(0, finalSize.Width - utilities.Width),
            wrap ? media.Height + RowGap : 0, utilities.Width, utilities.Height));
        return finalSize;
    }
}
