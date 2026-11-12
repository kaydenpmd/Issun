using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Issun.Views;

/// <summary>
/// WPF's WrapPanel, horizontal only, which WinUI doesn't have: children left
/// to right, onto the next line when the next one won't fit. For the
/// now-playing card's source and Discord captions, which sit side by side
/// until a long one pushes the other down.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0)
                continue;
            if (x > 0 && x + size.Width > available.Width)
            {
                y += lineHeight;
                x = 0;
                lineHeight = 0;
            }
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            width = Math.Max(width, x);
        }
        return new Size(width, y + lineHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            if (x > 0 && x + size.Width > final.Width)
            {
                y += lineHeight;
                x = 0;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return final;
    }
}
