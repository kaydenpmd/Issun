using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Issun.Core;
using Issun.ViewModels;
using Issun.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;
using static Issun.Platform.Native;

namespace Issun.Startup;

/// <summary>
/// --screenshot &lt;png&gt;: lays out the window's content on the demo host and
/// renders it to a PNG. This runs unattended, often while someone is using
/// the machine, so nothing may appear on the desktop, take focus or show in
/// the taskbar.
///
/// WPF could render a control with no window at all. WinUI can't: a page in
/// no window never loads (the winui3-spike's report, question 3). So the page
/// goes in a real window that is a tool window (no taskbar button), never
/// activated, and cloaked, which keeps the compositor from ever putting it on
/// screen. Keep a reference to that window until the render is done: one
/// nothing refers to is collected and closes mid-render, and the process
/// exits with 0 having written nothing.
///
/// The whole scrollable content is rendered, top to bottom, at the default
/// window width: a picture of every section is more use than a picture of the
/// first screenful. Mica can't be rendered, so the page gets the theme's
/// solid base colour, which is what Windows shows where Mica is off.
/// </summary>
public static class Screenshot
{
    public const double Width = 560;
    private const double Scale = 1.25;

    public static async Task<int> RunAsync(IIssunHost host, StartupOptions options, DispatcherQueue dispatcher)
    {
        var path = Path.GetFullPath(options.ScreenshotPath!);
        MainViewModel? vm = null;
        Window? window = null;
        try
        {
            // A disconnected session renders, but wrongly: on 12 Nov 2026,
            // with the owner's session disconnected (a 1080 x 2272 virtual
            // display), the main window came out at twice the size with
            // checkboxes and section arrows drawn in states they weren't in.
            // WPF drew nothing at all then. Either way the picture would pass
            // for the window, so refuse.
            if (SessionDisconnected())
                throw new InvalidOperationException(
                    "this Windows session is disconnected, and what WinUI draws then isn't what the window looks like. Reconnect to the PC and try again.");
            await host.StartAsync();
            vm = new MainViewModel(host, dispatcher);
            if (options.Expanded)
            {
                vm.ExpandAll();
                vm.Pairing.ShowQrForScreenshot();
            }

            // --setup-page N renders the setup window's content instead, at
            // its own width, the whole page whatever its height.
            SetupViewModel? setup = options.SetupPage is { } n ? new SetupViewModel(host, dispatcher, (SetupPage)n) : null;
            if (options.Expanded)
                setup?.ShowQrForScreenshot();
            var page = new Border
            {
                Background = PageBackground(options.Theme),
                Width = setup is null ? Width : 600,
                VerticalAlignment = VerticalAlignment.Top,
                Child = setup is null ? new MainView(vm) : new SetupView(setup),
            };
            var loaded = new TaskCompletionSource();
            page.Loaded += (_, _) => loaded.TrySetResult();

            // An outer scroller gives the view as much height as it wants, as
            // WPF's Measure with an infinite height did; the window itself
            // can stay small.
            window = new Window { Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden } };
            var hwnd = WindowNative.GetWindowHandle(window);
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~(nint)WS_EX_APPWINDOW);
            var cloak = 1;
            var hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            if (hr != 0)
                throw new InvalidOperationException($"couldn't keep the screenshot window off the screen (DWM cloak 0x{hr:x8})");
            var dpi = GetDpiForWindow(hwnd) / 96.0;
            window.AppWindow.ResizeClient(new SizeInt32((int)(page.Width * dpi) + 40, (int)(900 * dpi)));
            window.AppWindow.Show(false);

            if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(10))) != loaded.Task)
                throw new InvalidOperationException("the page never loaded");

            // Let bindings, the cover and the uptime summary settle.
            await Task.WhenAny(vm.CoverSettled, Task.Delay(TimeSpan.FromSeconds(5)));
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            vm.RefreshNow();
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            // Rendered at 1.25x, as the WPF build's screenshots were, whatever
            // the screen's own scaling. Without setting it RenderAsync draws
            // at 1x and stretches the result; set to 1.25 outright, it came
            // out at 1.25 times the screen's scaling (2.19x on a 175% screen,
            // 12 Nov 2026), and a long page passed RenderTargetBitmap's
            // 4096 px limit and was cut off.
            // An element's RasterizationScale multiplies its XamlRoot's, which
            // follows the screen, and RenderAsync multiplies the size it's
            // asked for by the same (measured on a 175% screen: asked for 750
            // wide, it drew 1313). So both are divided by it.
            var root = page.XamlRoot?.RasterizationScale is > 0 and var r ? r : 1;
            page.RasterizationScale = Scale / root;
            page.UpdateLayout();
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            var rtb = new RenderTargetBitmap();
            int wide = (int)Math.Ceiling(page.ActualWidth * Scale / root), high = (int)Math.Ceiling(page.ActualHeight * Scale / root);
            await rtb.RenderAsync(page, wide, high);
            var pixels = (await rtb.GetPixelsAsync()).ToArray();
            if (rtb.PixelHeight >= 4096 || rtb.PixelWidth >= 4096)
                throw new InvalidOperationException($"the render reached RenderTargetBitmap's 4096 px limit ({rtb.PixelWidth}x{rtb.PixelHeight}) and would be cut off");

            // A locked or disconnected session renders nothing: every pixel
            // comes back transparent, and a blank PNG would pass for a picture
            // of the window. Seen 12 Nov 2026 with the PC at its lock screen.
            var drawn = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) { drawn = true; break; }
            }
            if (!drawn)
                throw new InvalidOperationException(
                    "the render came out blank (every pixel transparent). Nothing is drawn while Windows is locked or the session is disconnected; unlock the PC and try again.");

            await SavePngAsync(path, pixels, rtb.PixelWidth, rtb.PixelHeight);
            Console.Out.WriteLine($"wrote {path} ({rtb.PixelWidth}x{rtb.PixelHeight})");
            WriteTextBoxes(page, Path.ChangeExtension(path, ".text.json"), rtb.PixelWidth / page.ActualWidth);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] screenshot failed: {CrashHandlers.Describe(ex)}");
            Console.Error.WriteLine($"screenshot failed: {ex}");
            return 1;
        }
        finally
        {
            vm?.Dispose();
            window?.Close();
            GC.KeepAlive(window);
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The theme's solid base colour, what Windows shows where Mica is off.
    /// In Windows Sandbox (12 Nov 2026) the published build couldn't find
    /// SolidBackgroundFillColorBaseBrush by key, though the same build found
    /// it on this PC and real windows there were styled; so when the lookup
    /// fails, WinUI's own values for it are used (#202020 dark, #F3F3F3 light,
    /// from its theme resources) and the log says so.
    /// </summary>
    private static Brush PageBackground(ThemeChoice theme)
    {
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var found) && found is Brush brush)
            return brush;
        Log.Write("[ui] screenshot: SolidBackgroundFillColorBaseBrush isn't in the app's resources; using WinUI's value for it");
        Console.Error.WriteLine("screenshot: SolidBackgroundFillColorBaseBrush not found; using WinUI's value");
        var dark = theme == ThemeChoice.Dark || (theme == ThemeChoice.System && Presentation.AppTheme.IsDark);
        return new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3));
    }

    /// <summary>The session this process runs in is disconnected (WTSDisconnected), as when Remote Desktop was closed without signing out.</summary>
    private static bool SessionDisconnected()
    {
        try
        {
            if (!WTSQuerySessionInformationW(0, -1 /* this session */, 8 /* WTSConnectState */, out var buffer, out _))
                return false;
            var state = System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
            WTSFreeMemory(buffer);
            return state == 4; // WTSDisconnected
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't read the session's state: {ex.Message}");
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(nint server, int session, int infoClass, out nint buffer, out int bytes);

    [System.Runtime.InteropServices.DllImport("wtsapi32")]
    private static extern void WTSFreeMemory(nint memory);

    private static async Task SavePngAsync(string path, byte[] bgra, int width, int height)
    {
        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96 * Scale, 96 * Scale, bgra);
        await encoder.FlushAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await memory.AsStreamForRead().CopyToAsync(file);
    }

    /// <summary>
    /// Beside the PNG, where every visible piece of text sits in it, in the
    /// PNG's pixels: <c>[{"text": "...", "x": .., "y": .., "w": .., "h": ..}]</c>.
    /// For pages that put a field over each piece of text so the owner can
    /// write the wording (their rule since 12 Nov 2026: the words are theirs).
    /// </summary>
    private static void WriteTextBoxes(FrameworkElement page, string jsonPath, double scale)
    {
        var boxes = new List<object>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 })
                return;
            string? text = node switch
            {
                TextBlock tb => tb.Text,
                TextBox box => box.Text,
                _ => null,
            };
            if (text is { Length: > 0 } && node is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                var r = fe.TransformToVisual(page).TransformBounds(new Windows.Foundation.Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                boxes.Add(new { text, x = Math.Round(r.X * scale), y = Math.Round(r.Y * scale), w = Math.Round(r.Width * scale), h = Math.Round(r.Height * scale) });
                if (node is TextBox)
                    return; // its inner parts repeat the same text
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(page);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(boxes, new JsonSerializerOptions { WriteIndented = true }));
        Console.Out.WriteLine($"wrote {jsonPath} ({boxes.Count} pieces of text)");
    }
}
