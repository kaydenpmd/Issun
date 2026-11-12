using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Issun.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Issun.Platform;

/// <summary>The few things the window asks of Windows itself: the clipboard, a browser, Explorer, a file to open, a message box.</summary>
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
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                // Flush leaves the text on the clipboard after Issun exits,
                // rather than rendering it on demand from a process that's gone.
                Clipboard.Flush();
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

    /// <summary>
    /// Windows' open-file dialog, as WPF's OpenFileDialog showed it: a title,
    /// named filters ("Name|*.ext|..."), and the file must exist. Null when
    /// the person cancels. UI thread; modal to <paramref name="owner"/>.
    /// </summary>
    public static string? PickFile(nint owner, string title, string filter)
    {
        const int max = 32768;
        var buffer = Marshal.AllocHGlobal(max * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new Native.OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<Native.OPENFILENAMEW>(),
                hwndOwner = owner,
                // The Win32 form of a filter is "Name\0pattern\0...\0\0".
                lpstrFilter = filter.Replace('|', '\0') + "\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = max,
                lpstrTitle = title,
                Flags = Native.OFN_EXPLORER | Native.OFN_FILEMUSTEXIST | Native.OFN_PATHMUSTEXIST | Native.OFN_NOCHANGEDIR,
            };
            if (Native.GetOpenFileNameW(ref ofn))
                return Marshal.PtrToStringUni(buffer);
            if (Native.CommDlgExtendedError() is var error and not 0)
                Log.Write($"[ui] the open-file dialog failed (error 0x{error:x})");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public enum Icon : uint { None = 0, Error = Native.MB_ICONERROR, Warning = Native.MB_ICONWARNING, Information = Native.MB_ICONINFORMATION, Question = Native.MB_ICONQUESTION }

    /// <summary>Windows' message box with OK: what WPF's MessageBox.Show drew, through the same user32 call.</summary>
    public static void Message(string text, string caption, Icon icon) =>
        Native.MessageBoxW(0, text, caption, Native.MB_OK | (uint)icon);

    /// <summary>Yes and No, with No the default, as Uninstall asks.</summary>
    public static bool AskYesNo(string text, string caption) =>
        Native.MessageBoxW(0, text, caption, Native.MB_YESNO | Native.MB_ICONQUESTION | Native.MB_DEFBUTTON2) == Native.IDYES;
}
