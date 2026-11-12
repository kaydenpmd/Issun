using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Issun.Core;
using Issun.ViewModels;
using Issun.Views;

namespace Issun.Startup;

/// <summary>
/// --screenshot &lt;png&gt;: lays out the window's content on the demo host and
/// renders it to a PNG, without a window ever existing. No HWND is created, so
/// nothing can appear on the desktop, take focus or show in the taskbar — this
/// runs unattended, often while someone is using the machine.
///
/// The whole scrollable content is rendered, top to bottom, at the default
/// window width: a picture of every section is more use than a picture of the
/// first screenful. Mica can't be rendered offscreen, so the page gets the
/// theme's solid base colour, which is what Windows shows where Mica is off.
/// </summary>
public static class Screenshot
{
    public const double Width = 560;
    private const double Scale = 1.25;

    public static async Task<int> RunAsync(IIssunHost host, StartupOptions options, Dispatcher dispatcher)
    {
        var path = Path.GetFullPath(options.ScreenshotPath!);
        MainViewModel? vm = null;
        try
        {
            await host.StartAsync();
            vm = new MainViewModel(host, dispatcher);
            if (options.Expanded)
                vm.ExpandAll();

            // --setup-page N renders the setup window's content instead, at
            // its own width, the whole page whatever its height.
            SetupViewModel? setup = options.SetupPage is { } n ? new SetupViewModel(host, dispatcher, (SetupPage)n) : null;
            if (options.Expanded)
                setup?.ShowQrForScreenshot();
            var page = new Border
            {
                Background = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White,
                Width = setup is null ? Width : 600,
                Child = setup is null ? new MainView { DataContext = vm } : new SetupView { DataContext = setup },
            };

            // Let bindings, the cover and the uptime summary settle. Awaiting
            // yields to the dispatcher, so everything posted to it runs.
            Layout(page);
            await Task.WhenAny(vm.CoverSettled, Task.Delay(TimeSpan.FromSeconds(5)));
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            vm.RefreshNow();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Layout(page);

            var pixelsWide = (int)Math.Ceiling(page.ActualWidth * Scale);
            var pixelsHigh = (int)Math.Ceiling(page.ActualHeight * Scale);
            var bitmap = new RenderTargetBitmap(pixelsWide, pixelsHigh, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(page);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = File.Create(path))
                encoder.Save(file);

            Console.Out.WriteLine($"wrote {path} ({pixelsWide}x{pixelsHigh})");
            WriteTextBoxes(page, Path.ChangeExtension(path, ".text.json"));
            return 0;
        }
        catch (Exception ex)
        {
            // No window and, for a WinExe, usually no console either — but a
            // caller that redirected stderr gets the reason, and the debugger
            // output always does.
            Log.Write($"[ui] screenshot failed: {CrashHandlers.Describe(ex)}");
            Console.Error.WriteLine($"screenshot failed: {ex}");
            return 1;
        }
        finally
        {
            vm?.Dispose();
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// Beside the PNG, where every visible piece of text sits in it, in the
    /// PNG's pixels: <c>[{"text": "...", "x": .., "y": .., "w": .., "h": ..}]</c>.
    /// For pages that put a field over each piece of text so the owner can
    /// write the wording (their rule since 12 Nov 2026: the words are theirs).
    /// </summary>
    private static void WriteTextBoxes(FrameworkElement page, string jsonPath)
    {
        var boxes = new List<object>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) // IsVisible is false off-screen
                return;
            string? text = node switch
            {
                TextBlock tb => tb.Text,
                TextBox box => box.Text,
                _ => null,
            };
            if (text is { Length: > 0 } && node is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                var r = fe.TransformToAncestor(page).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                boxes.Add(new { text, x = Math.Round(r.X * Scale), y = Math.Round(r.Y * Scale), w = Math.Round(r.Width * Scale), h = Math.Round(r.Height * Scale) });
                if (node is TextBox)
                    return; // its inner parts repeat the same text
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(page);
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(boxes, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.Out.WriteLine($"wrote {jsonPath} ({boxes.Count} pieces of text)");
    }

    private static void Layout(FrameworkElement page)
    {
        page.Measure(new Size(double.IsNaN(page.Width) ? Width : page.Width, double.PositiveInfinity));
        page.Arrange(new Rect(page.DesiredSize));
        page.UpdateLayout();
    }
}
