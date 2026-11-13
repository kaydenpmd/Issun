using System.Runtime.InteropServices;

namespace Issun.Core.Platform;

/// <summary>
/// Tells Explorer that the installed .exe and the Start menu entry may have a
/// new icon. An update replaces the .exe at the same path, so nothing else
/// tells the shell, and its icon cache keeps the old picture: on 12 Nov 2026
/// the taskbar went on drawing build 48's icon through builds 111, 113 and 114,
/// whose icon had changed, until these same notifications went out by hand
/// (this repo's CLAUDE.md, "Same night, builds 113 and 114"). Issun isn't
/// pinned, so its taskbar button takes the Start menu entry's icon, and that
/// comes from the cache. Never throws.
/// </summary>
public static class ShellIcons
{
    private const int SHCNE_UPDATEITEM = 0x2000, SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0, SHCNF_PATHW = 5, SHCNF_FLUSH = 0x1000;

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, nint item2);

    public static void Refresh(string exe, string shortcut)
    {
        try
        {
            // The shortcut's write time moved too when it was done by hand, and
            // which of these the cache needed isn't known, so both are done.
            if (File.Exists(shortcut))
                File.SetLastWriteTime(shortcut, DateTime.Now);
            foreach (var path in new[] { exe, shortcut })
                if (File.Exists(path))
                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, path, 0);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, null, 0);
            Log.Write("[install] told Windows the icons of the .exe and the Start menu entry may have changed");
        }
        catch (Exception ex)
        {
            Log.Write($"[install] couldn't tell Windows the icon may have changed: {ex.Message}");
        }
    }
}
