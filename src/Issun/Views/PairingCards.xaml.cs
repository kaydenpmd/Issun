using System.ComponentModel;
using System.Numerics;
using Issun.Core;
using Issun.Presentation;
using Issun.ViewModels;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace Issun.Views;

public sealed partial class PairingCards : UserControl
{
    private readonly Compositor _compositor;
    private readonly SpriteVisual _code;
    private readonly CompositionEffectBrush _blurred;
    private readonly CompositionSurfaceBrush _surface;
    private readonly UISettings _uiSettings = new();
    private Pixels? _drawn;
    private (float Amount, float Opacity)? _target;

    public PairingCards(PairingViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // The QR code, blurred on the compositor (the owner, 13 Nov 2026: "do
        // a fancy blur/unblur animation none of that fake cross fade shit
        // either"): Win2D's GaussianBlurEffect over the sharp code, its
        // BlurAmount (a standard deviation in DIPs, as CSS blur's length is)
        // animated between the view model's values, so every frame is the
        // code blurred by that frame's amount. Soft borders, as a CSS blur
        // treats what's outside the picture as transparent, so the edges blur
        // into the card as MetaMask's do.
        _compositor = ElementCompositionPreview.GetElementVisual(QrHost).Compositor;
        var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = 0f,
            BorderMode = EffectBorderMode.Soft,
            Optimization = EffectOptimization.Quality,
            Source = new CompositionEffectSourceParameter("Code"),
        };
        var factory = _compositor.CreateEffectFactory(blur, ["Blur.BlurAmount"]);
        _surface = _compositor.CreateSurfaceBrush();
        _surface.Stretch = CompositionStretch.Fill;
        _blurred = factory.CreateBrush();
        _blurred.SetSourceParameter("Code", _surface);
        _code = _compositor.CreateSpriteVisual();
        _code.Brush = _blurred;
        _code.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(QrHost, _code);

        viewModel.PropertyChanged += OnViewModelChanged;
        _ = DrawCodeAsync();
        ApplyBlur(animate: false);
    }

    public PairingViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PairingViewModel.QrPixels):
                _ = DrawCodeAsync();
                break;
            case nameof(PairingViewModel.QrBlurAmount):
            case nameof(PairingViewModel.QrOpacity):
                ApplyBlur(animate: true);
                break;
        }
    }

    /// <summary>Hands the compositor the sharp code, as a PNG it decodes into a surface of its own.</summary>
    private async Task DrawCodeAsync()
    {
        var pixels = ViewModel.QrPixels;
        if (ReferenceEquals(pixels, _drawn))
            return;
        _drawn = pixels;
        if (pixels is null)
        {
            _surface.Surface = null;
            return;
        }
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Bgra);
            await encoder.FlushAsync();
            stream.Seek(0);
            if (!ReferenceEquals(pixels, _drawn))
                return; // a newer code arrived meanwhile
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            surface.LoadCompleted += (_, e) =>
            {
                if (e.Status != LoadedImageSourceLoadStatus.Success)
                    Log.Write($"[ui] the QR code didn't load into the compositor: {e.Status}");
                stream.Dispose();
            };
            _surface.Surface = surface;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't hand the QR code to the compositor: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves the blur and opacity to the view model's values. The blur takes
    /// WinUI's normal duration (ControlNormalAnimationDuration, 250 ms) on the
    /// curve Windows gives an existing element moving from one state to
    /// another, "Point to Point", (0.55, 0.55, 0, 1); Windows' motion guidance
    /// has no row for a blur, so that row is the nearest. The 30% dim of an
    /// out-of-date code fades as Windows fades opacity: linear, at
    /// ControlFasterAnimationDuration (83 ms). Both from the design library's
    /// microsoft/windows11-long-text-and-motion. With Windows' animation
    /// effects off, both change at once.
    /// </summary>
    private void ApplyBlur(bool animate)
    {
        var amount = (float)ViewModel.QrBlurAmount;
        var opacity = (float)ViewModel.QrOpacity;
        // The blur and opacity are reported one after the other: move once.
        if (animate && (amount, opacity) == _target)
            return;
        _target = (amount, opacity);
        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            _blurred.Properties.InsertScalar("Blur.BlurAmount", amount);
            _code.Opacity = opacity;
            if (animate)
                Log.Write($"[ui] QR code blur to {amount:0.#}, opacity {opacity:0.##}, at once: Windows' animation effects are off");
            return;
        }

        var blur = _compositor.CreateScalarKeyFrameAnimation();
        blur.InsertKeyFrame(1f, amount, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.55f), new Vector2(0f, 1f)));
        blur.Duration = Duration("ControlNormalAnimationDuration", 250);
        _blurred.StartAnimation("Blur.BlurAmount", blur);

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, opacity, _compositor.CreateLinearEasingFunction());
        fade.Duration = Duration("ControlFasterAnimationDuration", 83);
        _code.StartAnimation(nameof(Visual.Opacity), fade);
        Log.Write($"[ui] QR code blur to {amount:0.#}, opacity {opacity:0.##}");
    }

    /// <summary>A WinUI duration resource, or the value it has in WinUI 2.6.5's generic.xaml if it can't be read.</summary>
    private static TimeSpan Duration(string key, int fallbackMs)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value))
        {
            switch (value)
            {
                case Microsoft.UI.Xaml.Duration { HasTimeSpan: true } d:
                    return d.TimeSpan;
                case TimeSpan t:
                    return t;
            }
        }
        Log.Write($"[ui] no {key} resource; using {fallbackMs} ms");
        return TimeSpan.FromMilliseconds(fallbackMs);
    }
}
