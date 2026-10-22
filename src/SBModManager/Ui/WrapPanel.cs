using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SBModManager.Ui;

/// <summary>
/// Minimal wrap panel: children flow left-to-right and move to the next line when they no
/// longer fit, so toolbars stay usable down to very narrow widths.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;

            if (x > 0 && x + size.Width > width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += size.Width + HorizontalSpacing;
            usedWidth = Math.Max(usedWidth, x - HorizontalSpacing);
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        var totalWidth = double.IsInfinity(width) ? usedWidth : width;
        return new Size(totalWidth, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}
