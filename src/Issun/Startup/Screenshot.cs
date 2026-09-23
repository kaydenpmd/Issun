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

            var page = new Border
            {
                Background = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White,
                Width = Width,
                Child = new MainView { DataContext = vm },
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

    private static void Layout(FrameworkElement page)
    {
        page.Measure(new Size(Width, double.PositiveInfinity));
        page.Arrange(new Rect(page.DesiredSize));
        page.UpdateLayout();
    }
}
