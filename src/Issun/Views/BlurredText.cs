using System.Runtime.InteropServices.WindowsRuntime;
using Issun.Core;
using Issun.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Issun.Views;

/// <summary>
/// One line of text that can be blurred: the setup window's key, blurred
/// until Show so a glance or a screenshot doesn't give it away. WPF drew it
/// with a BlurEffect; WinUI 3 has none (the winui3-spike's report, question
/// 5), so this renders the text to pixels itself (RenderTargetBitmap of an
/// invisible twin), blurs them on the CPU (<see cref="Presentation.Blur"/>),
/// and shows that image where the text would be. The sharp text is never
/// drawn while blurred, and the blur stays inside the text's own bounds, as
/// WPF's ClipToBounds kept it.
/// </summary>
public sealed partial class BlurredText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BlurredText), new PropertyMetadata("", (d, _) => ((BlurredText)d).Changed()));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(BlurredText), new PropertyMetadata(0.0, (d, _) => ((BlurredText)d).Changed()));

    private readonly TextBlock _sharp;
    private readonly TextBlock _twin;
    private readonly Border _twinHost;
    private readonly Image _blurred;
    private int _generation;

    public BlurredText()
    {
        IsTabStop = false;
        var style = (Style)Application.Current.Resources["MonoBlock"];
        _sharp = new TextBlock { Style = style, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        _twin = new TextBlock { Style = style, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        // Laid out exactly where the text goes, never seen: its parent is
        // transparent, and RenderTargetBitmap draws the twin itself.
        _twinHost = new Border { Child = _twin, Opacity = 0, IsHitTestVisible = false };
        _blurred = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Content = new Grid { Children = { _twinHost, _sharp, _blurred } };
        SizeChanged += (_, _) => Changed();
        ActualThemeChanged += (_, _) => Changed();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The blur's reach in view pixels, as WPF's BlurEffect.Radius; 0 shows the text.</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private void Changed()
    {
        var text = Text ?? "";
        _twin.Text = text;
        var gen = ++_generation;
        if (Radius <= 0 || text.Length == 0)
        {
            _sharp.Text = text;
            _sharp.Visibility = Visibility.Visible;
            _blurred.Visibility = Visibility.Collapsed;
            _blurred.Source = null;
            return;
        }
        // Blurred: the sharp text goes at once, and the blurred picture
        // follows once the twin has been laid out and drawn.
        _sharp.Text = "";
        _sharp.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = CaptureAsync(gen));
    }

    private async Task CaptureAsync(int gen)
    {
        try
        {
            if (gen != _generation || XamlRoot is null || _twin.ActualWidth < 1 || _twin.ActualHeight < 1)
                return;
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_twin);
            if (gen != _generation || rtb.PixelWidth == 0)
                return;
            var bgra = (await rtb.GetPixelsAsync()).ToArray();
            var pixels = new Pixels(bgra, rtb.PixelWidth, rtb.PixelHeight);
            var scale = rtb.PixelWidth / _twin.ActualWidth;
            var radius = Radius * scale;
            var blurred = await Task.Run(() => Blur.Gaussian(pixels, radius));
            if (gen != _generation)
                return;
            _blurred.Source = Images.FromPixels(blurred);
            _blurred.Width = _twin.ActualWidth;
            _blurred.Height = _twin.ActualHeight;
            _blurred.HorizontalAlignment = HorizontalAlignment.Left;
            _blurred.VerticalAlignment = VerticalAlignment.Top;
            _blurred.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Nothing is shown rather than the key: the blur's whole point.
            Log.Write($"[ui] couldn't blur the key: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
