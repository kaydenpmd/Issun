using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Issun.Views;

/// <summary>
/// Windows 11's indeterminate ProgressRing, which WPF doesn't have, drawn from
/// WinUI's own source (microsoft-ui-xaml, controls/dev/ProgressRing, as of
/// 10 Oct 2026; the design library's microsoft/progress-ring):
/// <list type="bullet">
/// <item>An 80-unit canvas scaled to the control: a circle of radius 35 with a
/// 7.5-unit stroke and round caps, in the foreground (AccentFillColorDefaultBrush
/// by default); the track behind it is transparent, so it isn't drawn.</item>
/// <item>A 2 s loop. In the first half the arc's end runs from the start to half
/// the circle; in the second its start follows, to meet the end. The whole
/// thing turns 900° over the loop, 450° a half.</item>
/// <item>Every segment eases with a cubic Bézier of (0.167, 0.167) and (0.833,
/// 0.833), whose x and y match at every point, so it is linear.</item>
/// <item>The arc starts at three o'clock and runs clockwise, as a Composition
/// ellipse's trim does.</item>
/// </list>
/// The template's minimum size is 16 × 16, its default 32 × 32.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration Loop = new(TimeSpan.FromSeconds(2));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public ProgressRing()
    {
        Width = 32;
        Height = 32;
        MinWidth = 16;
        MinHeight = 16;
        SetResourceReference(ForegroundProperty, "AccentFillColorDefaultBrush");
        // Only animates while it can be seen, as WinUI's runs only while active.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, Loop) { RepeatBehavior = RepeatBehavior.Forever });
            else
                BeginAnimation(ProgressProperty, null);
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var unit = size / 80.0;
        var radius = 35 * unit;
        // Not frozen: the brush is the theme's and can change, and freezing a
        // pen around it threw "This Freezable cannot be frozen" (sandbox,
        // 12 Nov 2026), which stopped Issun starting.
        var pen = new Pen(Foreground, 7.5 * unit) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        var p = (double)GetValue(ProgressProperty);
        double trimStart, trimEnd;
        if (p < 0.5)
        {
            trimStart = 0;
            trimEnd = 0.0001 + (0.5 - 0.0001) * (p / 0.5);
        }
        else
        {
            trimStart = 0.5 * ((p - 0.5) / 0.5);
            trimEnd = 0.5;
        }
        var rotation = 900 * p;
        var from = trimStart * 360 + rotation;
        var to = trimEnd * 360 + rotation;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        if (to - from < 0.01)
        {
            // A zero-length arc still shows its round cap, as the trimmed
            // Composition stroke does at the loop's ends.
            var dot = PointOn(centre, radius, from);
            dc.DrawEllipse(Foreground, null, dot, 3.75 * unit, 3.75 * unit);
            return;
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(PointOn(centre, radius, from), false, false);
            g.ArcTo(PointOn(centre, radius, to), new Size(radius, radius), 0, to - from > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Degrees clockwise from three o'clock, on screen (y down).</summary>
    private static Point PointOn(Point centre, double radius, double degrees)
    {
        var r = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Cos(r), centre.Y + radius * Math.Sin(r));
    }
}
