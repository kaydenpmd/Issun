using System.Runtime.InteropServices;

namespace Issun.Core.Platform;

/// <summary>
/// Issun's entry in the Start menu.
///
/// Issun ships as one .exe with no installer, so nothing else would ever put it
/// there — and once "Start with Windows" hides it in the tray, the Start menu is
/// the only way most people will think to open it again. So a run does what an
/// installer would have: it adds the entry the first time, and repoints it when
/// the .exe has moved (the same rule <see cref="Autostart.RepairIfMoved"/>
/// follows, so the two always agree about which copy is Issun).
///
/// What it deliberately does not do is put back an entry the person deleted. A
/// marker file records that the entry was added once; after that, a missing
/// shortcut means somebody removed it, and recreating it on every launch would
/// be fighting them. Deleting the marker brings the behaviour back.
///
/// Local builds (build 0, "dev") leave the Start menu alone: they run out of a
/// bin folder, and repointing the owner's entry at one is exactly the kind of
/// quiet damage nobody would notice until the build folder was cleaned.
/// </summary>
public sealed class StartMenuShortcut
{
    public const string FileName = "Issun.lnk";

    private readonly string _markerPath;
    private readonly bool _releaseBuild;

    /// <param name="programsFolder">The per-user Start menu Programs folder; null means the real one.</param>
    /// <param name="markerPath">Where "the entry was added once" is recorded; null means beside Issun's data.</param>
    /// <param name="exePath">The .exe the entry opens; null means this process's own.</param>
    /// <param name="releaseBuild">False for local builds, which never touch the Start menu.</param>
    public StartMenuShortcut(
        string? programsFolder = null,
        string? markerPath = null,
        string? exePath = null,
        bool? releaseBuild = null)
    {
        ShortcutPath = Path.Combine(
            programsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), FileName);
        _markerPath = markerPath ?? Path.Combine(Paths.DataDir, "start-menu-added");
        ExePath = exePath ?? Environment.ProcessPath ?? "";
        _releaseBuild = releaseBuild ?? IssunInfo.Build > 0;
    }

    public string ShortcutPath { get; }

    public string ExePath { get; }

    /// <summary>
    /// Adds the entry on first run, repoints it if Issun has moved, and otherwise
    /// leaves it alone. Never throws: a Start menu problem is logged, not allowed
    /// to stop the receiver from starting.
    /// </summary>
    public void Ensure()
    {
        try
        {
            if (!_releaseBuild)
                return;
            if (string.IsNullOrWhiteSpace(ExePath)
                || string.Equals(Path.GetFileName(ExePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("[startmenu] not adding a Start menu entry: not running as Issun.exe");
                return;
            }

            if (File.Exists(ShortcutPath))
            {
                // Also covers an entry made by hand before Issun did this itself:
                // it counts as added, so deleting it later is respected too.
                MarkAdded();
                var target = ReadTarget(ShortcutPath);
                if (target is null || !SamePath(target, ExePath))
                {
                    Write();
                    Log.Write($"[startmenu] the Start menu entry opened {target ?? "nothing"}; it now opens {ExePath}");
                }
                return;
            }

            if (File.Exists(_markerPath))
                return; // added once, since removed by the person: leave it removed

            Write();
            MarkAdded();
            Log.Write($"[startmenu] added Issun to the Start menu ({ShortcutPath})");
        }
        catch (Exception ex)
        {
            Log.Write($"[startmenu] could not update the Start menu entry: {ex.Message}");
        }
    }

    /// <summary>The .exe an existing shortcut opens, or null if it has none.</summary>
    internal static string? ReadTarget(string shortcutPath) =>
        WithShortcut(shortcutPath, link => (string?)link.TargetPath is { Length: > 0 } t ? t : null);

    private void Write() =>
        WithShortcut(ShortcutPath, link =>
        {
            link.TargetPath = ExePath;
            link.WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "";
            link.IconLocation = ExePath + ",0";
            link.Description = "Shows what you're playing on Discord";
            link.Save();
            return 0;
        });

    private void MarkAdded()
    {
        if (File.Exists(_markerPath))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_markerPath))!);
        File.WriteAllText(_markerPath, "Issun added its Start menu entry once. Delete this file to have it added again.\r\n");
    }

    /// <summary>
    /// .lnk files are written through Windows Script Host's shell object — the
    /// same one PowerShell scripts use — rather than hand-declaring IShellLinkW
    /// and IPersistFile, which is fifty lines of interop for four properties.
    /// </summary>
    private static T WithShortcut<T>(string shortcutPath, Func<dynamic, T> use)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows Script Host (WScript.Shell) is not available");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            try { return use(link); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
