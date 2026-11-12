using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;
using static IssunSpike.Native;

namespace IssunSpike;

/// <summary>
/// IssunSpike.exe                                     window (the Connect your source page) and tray icon
///   [--theme dark|light] [--connected] [--ready-file f] [--quit-after s]
/// IssunSpike.exe --screenshot a.png                  render the page to a PNG and exit
///   [--theme dark|light] [--connected] [--reference] [--mode cloak|offscreen|nowindow] [--scale 1.25] [--delay ms]
/// IssunSpike.exe --tray-test dir                     add the tray icon, open its menu, capture both, exit
/// </summary>
public partial class App : Application
{
    private readonly Dictionary<string, string?> _opt = new();
    private Window? _window;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { SpikeLog.Write($"[crash] {e.Exception}"); SpikeLog.Flush(); };
    }

    private string? Opt(string name) => _opt.TryGetValue(name, out var v) ? v : null;
    private bool Has(string name) => _opt.ContainsKey(name);

    protected override async void OnLaunched(LaunchActivatedEventArgs _)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        SpikeLog.Start(args);
        SpikeLog.Write($"[env] carrying Issun.Core {Issun.Core.IssunInfo.Display}, Kestrel {typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.GetName().Version}");
        SpikeLog.Write($"[start] OnLaunched {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after process start");
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var name = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            _opt[name] = value;
        }
        PackageTest.LogActivation();
        int code;
        try
        {
            if (Has("screenshot")) code = await ScreenshotAsync(Opt("screenshot")!);
            else if (Has("tray-test")) code = await TrayTestAsync(Opt("tray-test")!);
            else if (Has("ring-frames")) code = await RingFramesAsync(Opt("ring-frames")!);
            else if (Has("gallery")) code = await Gallery.RunAsync(Opt("gallery")!, Theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark);
            else if (Has("package-test")) code = await PackageTest.RunAsync(Opt("package-test")!, int.Parse(Opt("port")!), Opt("pipe")!, Has("leave-startup-on"));
            else { ShowWindow(); return; }
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"[fail] {ex}");
            Console.Error.WriteLine(ex);
            code = 1;
        }
        SpikeLog.Write($"[exit] code {code}");
        SpikeLog.Flush();
        Environment.Exit(code);
    }

    private ElementTheme Theme => Opt("theme") switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };

    /// <summary>Where each Windows App SDK DLL this process loaded came from: its own unpacked copy, or a copy Windows ships (the CBS packages under WindowsApps).</summary>
    private static void LogRuntimeModules()
    {
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            var n = m.ModuleName.ToLowerInvariant();
            if (n.StartsWith("microsoft.ui") || n.StartsWith("microsoft.windowsappruntime") || n is "dcompi.dll" or "dwmcorei.dll" or "dwritecore.dll" or "microsoft.internal.frameworkudk.dll")
                SpikeLog.Write($"[env] loaded {m.FileName} {m.FileVersionInfo.FileVersion}");
        }
    }

    // ───────────── Window and tray ─────────────

    private void ShowWindow()
    {
        var page = new ConnectPage { RequestedTheme = Theme };
        page.SetConnected(Has("connected"));
        page.RenderRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Mica shows through, as in Issun
        _window = new Window { Content = page, Title = "Issun", SystemBackdrop = new MicaBackdrop() };
        var hwnd = WindowNative.GetWindowHandle(_window);
        if (Theme == ElementTheme.Dark) { var on = 1; DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4); }
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _window.AppWindow.ResizeClient(new SizeInt32((int)(600 * scale), (int)(640 * scale)));
        try { _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "issun.ico")); }
        catch (Exception ex) { SpikeLog.Write($"[ui] window icon: {ex.Message}"); }
        _window.AppWindow.Closing += (_, e) => { e.Cancel = true; _window.AppWindow.Hide(); SpikeLog.Write("[ui] window closed: hidden, still in the tray"); };

        var first = true;
        void OnRendering(object? s, object e)
        {
            if (!first) return;
            first = false;
            CompositionTarget.Rendering -= OnRendering;
            var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            SpikeLog.Write($"[start] first frame {ms:0} ms after process start");
            LogRuntimeModules();
            if (Opt("ready-file") is { } f) File.WriteAllText(f, ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }
        page.Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;

        _tray = new TrayIcon("Issun", Open, Quit);
        _window.Activate();
        SpikeLog.Write("[ui] window shown");
        if (Opt("quit-after") is { } q)
            _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(q, System.Globalization.CultureInfo.InvariantCulture)))
                .ContinueWith(_ => _window.DispatcherQueue.TryEnqueue(Quit));
    }

    private void Open()
    {
        SpikeLog.Write("[ui] open");
        _window?.AppWindow.Show();
        _window?.Activate();
    }

    private void Quit()
    {
        SpikeLog.Write("[ui] quit");
        _tray?.Dispose();
        SpikeLog.Flush();
        Environment.Exit(0);
    }

    // ───────────── --screenshot ─────────────

    private async Task<int> ScreenshotAsync(string path)
    {
        path = Path.GetFullPath(path);
        var mode = Opt("mode") ?? "cloak";
        var scale = double.Parse(Opt("scale") ?? "1.25", System.Globalization.CultureInfo.InvariantCulture);
        var sw = Stopwatch.StartNew();

        var page = new ConnectPage { RequestedTheme = Theme, Width = 600, VerticalAlignment = VerticalAlignment.Top };
        page.SetConnected(Has("connected"));
        page.ShowReferenceInfoBar(Has("reference"));
        var loaded = new TaskCompletionSource();
        page.Loaded += (_, _) => loaded.TrySetResult();

        Window? w = null;
        if (mode != "nowindow")
        {
            w = new Window { Content = page, Title = "IssunSpike screenshot" };
            var hwnd = WindowNative.GetWindowHandle(w);
            // No taskbar button, never activated, and (cloak) never composed
            // onto the screen, or (offscreen) put where no monitor is.
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~(nint)WS_EX_APPWINDOW);
            if (mode == "cloak") { var one = 1; var hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref one, 4); SpikeLog.Write($"[shot] cloak hr=0x{hr:x8}"); }
            var dpi = GetDpiForWindow(hwnd) / 96.0;
            if (mode == "offscreen") w.AppWindow.Move(new PointInt32(-32000, -32000));
            w.AppWindow.ResizeClient(new SizeInt32((int)(600 * dpi), (int)(1400 * dpi)));
            w.AppWindow.Show(false);
            SpikeLog.Write($"[shot] window 0x{hwnd:x} mode {mode}, dpi scale {dpi}, visible flag {IsWindowVisible(hwnd)}");
        }

        if (await Task.WhenAny(loaded.Task, Task.Delay(5000)) != loaded.Task)
            SpikeLog.Write("[shot] the page never loaded (5 s)");
        // Into the ring's loop, so the picture shows an arc rather than its start.
        await Task.Delay(int.Parse(Opt("delay") ?? "700"));
        page.UpdateLayout();
        SpikeLog.Write($"[shot] page {page.ActualWidth}x{page.ActualHeight}, rasterization scale {page.XamlRoot?.RasterizationScale}");

        if (!Has("upscale")) { page.RasterizationScale = scale; page.UpdateLayout(); await Task.Delay(200); SpikeLog.Write($"[shot] RasterizationScale set to {scale}"); }
        var rtb = new RenderTargetBitmap();
        int pw = (int)Math.Ceiling(page.ActualWidth * scale), ph = (int)Math.Ceiling(page.ActualHeight * scale);
        await rtb.RenderAsync(page, pw, ph);
        var pixels = (await rtb.GetPixelsAsync()).ToArray();
        SpikeLog.Write($"[shot] rendered {rtb.PixelWidth}x{rtb.PixelHeight} ({pixels.Length} bytes) in {sw.ElapsedMilliseconds} ms");

        var drawn = false;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { drawn = true; break; }
        if (!drawn) throw new InvalidOperationException("the render came out blank (every pixel transparent)");

        await SavePngAsync(path, pixels, rtb.PixelWidth, rtb.PixelHeight);
        Console.Out.WriteLine($"wrote {path} ({rtb.PixelWidth}x{rtb.PixelHeight})");
        SpikeLog.Write($"[shot] wrote {path}");
        WriteTextBoxes(page, Path.ChangeExtension(path, ".text.json"), rtb.PixelWidth / page.ActualWidth);
        w?.Close();
        return 0;
    }

    private static void WriteTextBoxes(FrameworkElement page, string jsonPath, double scale)
    {
        var boxes = new List<object>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            string? text = node switch { TextBlock tb => tb.Text, TextBox box => box.Text, _ => null };
            if (text is { Length: > 0 } && node is FrameworkElement fe && fe.ActualWidth > 0)
            {
                var r = fe.TransformToVisual(page).TransformBounds(new Windows.Foundation.Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                boxes.Add(new { text, x = Math.Round(r.X * scale), y = Math.Round(r.Y * scale), w = Math.Round(r.Width * scale), h = Math.Round(r.Height * scale) });
                if (node is TextBox) return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(page);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(boxes, new JsonSerializerOptions { WriteIndented = true }));
        SpikeLog.Write($"[shot] wrote {jsonPath} ({boxes.Count} pieces of text)");
    }

    internal static async Task SavePngAsync(string path, byte[] bgra, int w, int h)
    {
        using var mem = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)w, (uint)h, 96, 96, bgra);
        await enc.FlushAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await mem.AsStreamForRead().CopyToAsync(file);
    }

    // ───────────── --ring-frames ─────────────

    /// <summary>The status row, rendered over and over from the moment the ring is switched on, each PNG named by its time in ms since then.</summary>
    private async Task<int> RingFramesAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var page = new ConnectPage { RequestedTheme = Theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark, Width = 600, VerticalAlignment = VerticalAlignment.Top, RasterizationScale = 1.25 };
        page.SetConnected(false);
        page.RingControl.IsActive = false;
        var loaded = new TaskCompletionSource();
        page.Loaded += (_, _) => loaded.TrySetResult();
        var w = new Window { Content = page };
        var hwnd = WindowNative.GetWindowHandle(w);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        var one = 1; DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref one, 4);
        w.AppWindow.ResizeClient(new SizeInt32(600, 1400));
        w.AppWindow.Show(false);
        await loaded.Task;
        await Task.Delay(500);
        var row = page.StatusRow;
        var sw = Stopwatch.StartNew();
        page.RingControl.IsActive = true;
        while (sw.ElapsedMilliseconds < 4200)
        {
            await BackToUi(page.DispatcherQueue); // a WinRT await can resume off the UI thread (RPC_E_WRONG_THREAD seen here)
            var t = sw.ElapsedMilliseconds;
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(page, (int)Math.Ceiling(page.ActualWidth * 1.25), (int)Math.Ceiling(page.ActualHeight * 1.25));
            var px = (await rtb.GetPixelsAsync()).ToArray();
            await SavePngAsync(Path.Combine(dir, $"winui-ring-{t:0000}.png"), px, rtb.PixelWidth, rtb.PixelHeight);
            await Task.Delay(30);
        }
        GC.KeepAlive(w); // collected mid-loop otherwise: the window closes and the app exits with 0
        SpikeLog.Write($"[ring] frames written to {dir}");
        return 0;
    }

    private static Task BackToUi(Microsoft.UI.Dispatching.DispatcherQueue q)
    {
        if (q.HasThreadAccess) return Task.CompletedTask;
        SpikeLog.Write("[ring] continuation was off the UI thread; hopping back");
        var tcs = new TaskCompletionSource();
        q.TryEnqueue(() => tcs.SetResult());
        return tcs.Task;
    }

    // ───────────── --tray-test ─────────────

    private async Task<int> TrayTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var menuShown = new TaskCompletionSource();
        _tray = new TrayIcon("Issun", () => SpikeLog.Write("[test] open requested"), () => SpikeLog.Write("[test] quit requested"));
        await Task.Delay(800);
        var rect = _tray.IconRect();
        File.WriteAllText(Path.Combine(dir, "tray-rect.txt"), rect?.ToString() ?? "none");

        // Ask for the menu the way the shell does on a right-click, at the
        // icon (or the cursor if the shell won't say where the icon is).
        int x, y;
        if (rect is { } r) { x = (r.Left + r.Right) / 2; y = (r.Top + r.Bottom) / 2; }
        else { GetCursorPos(out var p); x = p.X; y = p.Y; }
        PostMessageW(_tray.Hwnd, TrayIcon.CallbackMessage, (nint)((x & 0xFFFF) | (y << 16)), (nint)(WM_CONTEXTMENU | (1 << 16)));

        // The menu's modal loop runs on this thread; look for it from another.
        var found = await Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                var menu = FindWindowW("#32768", null);
                if (menu != 0 && IsWindowVisible(menu))
                {
                    await Task.Delay(400); // let the open animation finish
                    GetWindowRect(menu, out var mr);
                    SpikeLog.Write($"[test] menu window 0x{menu:x} at {mr}");
                    var pad = 24;
                    var area = new RECT { Left = mr.Left - pad, Top = mr.Top - pad, Right = mr.Right + pad, Bottom = mr.Bottom + pad };
                    var px = CaptureScreen(area);
                    await SavePngAsync(Path.Combine(dir, "tray-menu.png"), px, area.Right - area.Left, area.Bottom - area.Top);
                    File.WriteAllText(Path.Combine(dir, "tray-menu-rect.txt"), mr.ToString());
                    PostMessageW(_tray!.Hwnd, TrayIcon.EndMenuMessage, 0, 0);
                    return true;
                }
            }
            return false;
        });
        SpikeLog.Write(found ? "[test] tray menu opened and captured" : "[test] FAIL: no menu window appeared");
        await Task.Delay(500);
        _tray.Dispose();
        return found ? 0 : 2;
    }
}
