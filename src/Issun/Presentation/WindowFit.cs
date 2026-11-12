using System.Windows;
using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// Shrinks a window to fit the screen it opens on. The main window asks for
/// 820 tall, and on a screen with less room than that (a 1366 x 768 laptop has
/// about 728 above the taskbar) CenterScreen centred it anyway, so its title
/// bar sat above the top edge where it couldn't be dragged: seen in Windows
/// Sandbox on 12 Nov 2026, 820 tall in a 775 work area, top at -23.
/// </summary>
public static class WindowFit
{
    public static void ToWorkArea(Window window, string name)
    {
        var area = SystemParameters.WorkArea;
        if (window.Height <= area.Height && window.Width <= area.Width)
            return;
        var asked = $"{window.Width:0}x{window.Height:0}";
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.Height = Math.Min(window.Height, area.Height);
        window.Width = Math.Min(window.Width, area.Width);
        Log.Write($"[ui] {name} window shrunk from {asked} to {window.Width:0}x{window.Height:0} to fit a {area.Width:0}x{area.Height:0} work area");
    }
}
