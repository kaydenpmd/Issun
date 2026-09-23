using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Issun.Core;

namespace Issun.Platform;

/// <summary>The few things the window asks of Windows itself: the clipboard, a browser, Explorer.</summary>
public static class DesktopShell
{
    /// <summary>
    /// Copies text, retrying briefly. The clipboard is a shared lock, and
    /// another program holding it for a moment (a clipboard manager, a remote
    /// desktop session) makes a single attempt fail with CLIPBRD_E_CANT_OPEN.
    /// Returns false — and logs why — when it still can't; the caller then
    /// says so instead of letting the person paste something stale into Ammy.
    /// Must be called on the UI thread.
    /// </summary>
    public static async Task<bool> CopyAsync(string text, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // SetDataObject(copy: true) leaves the text on the clipboard
                // after Issun exits, unlike SetText's delayed rendering.
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                if (attempt == 5)
                {
                    Log.Write($"[ui] couldn't copy the {what}: the clipboard is busy ({ex.Message})");
                    return false;
                }
                await Task.Delay(60 * attempt);
            }
        }
    }

    /// <summary>Opens an http(s) link in the default browser. Anything else is refused and logged.</summary>
    public static void OpenLink(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            Log.Write($"[ui] not opening a {uri.Scheme}: link");
            return;
        }
        Start(uri.AbsoluteUri, "the link");
    }

    /// <summary>Opens a folder in Explorer, creating it first so the click never lands on "can't find".</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"[ui] couldn't create {path}: {ex.Message}");
            return;
        }
        Start(path, "the folder");
    }

    // Off the UI thread: ShellExecute can stall for seconds on a cold Explorer
    // or a browser that is still starting.
    private static void Start(string target, string what) => Task.Run(() =>
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't open {what} ({target}): {ex.Message}");
        }
    });
}
