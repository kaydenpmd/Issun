using Issun.Core;
using Issun.Platform;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Issun.Presentation;

/// <summary>
/// What every Issun window gets: the icon, Mica, a size in view pixels with a
/// minimum, centred on its screen, and shrunk to fit a screen too small for it.
///
/// The shrinking is from the WPF build: the main window asks for 820 tall,
/// and on a screen with less room than that (a 1366 x 768 laptop has about
/// 728 above the taskbar) it was centred anyway, so its title bar sat above
/// the top edge where it couldn't be dragged: seen in Windows Sandbox on
/// 12 Nov 2026, 820 tall in a 775 work area, top at -23.
/// </summary>
public static class WindowFit
{
    public static void Prepare(Window window, string title, double width, double height, double minWidth, double minHeight, string name)
    {
        window.Title = title;
        // No background in the views: Mica shows through, and the cards are
        // drawn as layers over it, as in Windows 11 Settings.
        window.SystemBackdrop = new MicaBackdrop();
        var hwnd = WindowNative.GetWindowHandle(window);
        var appWindow = window.AppWindow;

        // Both icons at this window's DPI: the big one is the taskbar's and
        // Alt+Tab's, the small one the title bar's. One icon for both, as
        // AppWindow.SetIcon takes, left the taskbar a 32 px one at 150%.
        int bigSize = Native.IconSize(hwnd, Native.SM_CXICON), smallSize = Native.IconSize(hwnd, Native.SM_CXSMICON);
        nint big = Native.LoadAppIcon(bigSize), small = Native.LoadAppIcon(smallSize);
        if (big != 0 && small != 0)
        {
            Native.SendMessageW(hwnd, Native.WM_SETICON, Native.ICON_BIG, big);
            Native.SendMessageW(hwnd, Native.WM_SETICON, Native.ICON_SMALL, small);
            Log.Write($"[ui] {name} window: icons {bigSize} and {smallSize} px at {Native.GetDpiForWindow(hwnd)} dpi");
        }
        else
            Log.Write($"[ui] {name} window: the icon is missing from the build");

        // The title bar follows the app's theme, so --theme dark draws a dark
        // one on a light Windows.
        appWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double areaWidth = area.Width / scale, areaHeight = area.Height / scale;
        if (height > areaHeight || width > areaWidth)
        {
            var asked = $"{width:0}x{height:0}";
            minHeight = Math.Min(minHeight, areaHeight);
            minWidth = Math.Min(minWidth, areaWidth);
            height = Math.Min(height, areaHeight);
            width = Math.Min(width, areaWidth);
            Log.Write($"[ui] {name} window shrunk from {asked} to {width:0}x{height:0} to fit a {areaWidth:0}x{areaHeight:0} work area");
        }
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * scale);
        }
        int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
        // Centred on the screen, as WPF's CenterScreen put it.
        appWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    /// <summary>Visible and not minimised: when the clock-driven text is worth ticking.</summary>
    public static bool IsShowing(Window window) =>
        window.AppWindow.IsVisible && window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>Brings a window forward, restoring it if minimised.</summary>
    public static void Bring(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
            p.Restore();
        window.AppWindow.Show();
        window.Activate();
    }
}
