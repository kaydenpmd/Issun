using System.Runtime.InteropServices;
using Issun.Core.Platform;

namespace Issun.Core.Tests.Platform;

public class StartMenuShortcutTests
{
    // Shortcut targets don't have to exist for WScript.Shell to write them, but
    // real files keep the tests honest about paths that Windows would resolve.
    private static string FakeExe(TempFolder dir, string name)
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static StartMenuShortcut Make(TempFolder dir, string exe, bool release = true) =>
        new(programsFolder: dir.Path, markerPath: dir.File("marker"), exePath: exe, releaseBuild: release);

    // An entry as an earlier build or the person left it, description and all.
    private static void WriteEntry(
        string path, string target, string description, string? icon = null, string? startIn = null)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        try
        {
            link.TargetPath = target;
            link.Description = description;
            if (icon is not null)
                link.IconLocation = icon;
            if (startIn is not null)
                link.WorkingDirectory = startIn;
            link.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string ReadEntry(string path, Func<dynamic, string> property)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        try
        {
            return property(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [Theory]
    [InlineData("Shows what Ammy is playing on Discord")]
    [InlineData("Shows what you're playing on Discord")]
    public void An_old_description_comes_off_an_entry_already_pointing_here(string old)
    {
        using var dir = new TempFolder();
        var exe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, exe);
        WriteEntry(shortcut.ShortcutPath, exe, old);

        using var log = Log.Capture();
        shortcut.Ensure();

        Assert.Null(StartMenuShortcut.ReadDescription(shortcut.ShortcutPath));
        Assert.Equal(exe, StartMenuShortcut.ReadTarget(shortcut.ShortcutPath), ignoreCase: true);
        Assert.Contains(log.Lines, l => l.StartsWith("[startmenu] took the old description off"));
    }

    [Fact]
    public void A_moved_exe_also_loses_an_old_description()
    {
        using var dir = new TempFolder();
        var oldExe = FakeExe(dir, "old-Issun.exe");
        var newExe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, newExe);
        WriteEntry(shortcut.ShortcutPath, oldExe, StartMenuShortcut.OldDescriptions[0]);

        shortcut.Ensure();

        Assert.Equal(newExe, StartMenuShortcut.ReadTarget(shortcut.ShortcutPath), ignoreCase: true);
        Assert.Null(StartMenuShortcut.ReadDescription(shortcut.ShortcutPath));
    }

    [Fact]
    public void Taking_off_an_old_description_keeps_the_icon_and_start_in_folder()
    {
        using var dir = new TempFolder();
        var exe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, exe);
        var icon = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",5";
        WriteEntry(shortcut.ShortcutPath, exe, StartMenuShortcut.OldDescriptions[1], icon, Environment.SystemDirectory);

        shortcut.Ensure();

        Assert.Null(StartMenuShortcut.ReadDescription(shortcut.ShortcutPath));
        Assert.Equal(icon, ReadEntry(shortcut.ShortcutPath, l => (string)l.IconLocation), ignoreCase: true);
        Assert.Equal(Environment.SystemDirectory, ReadEntry(shortcut.ShortcutPath, l => (string)l.WorkingDirectory), ignoreCase: true);
    }

    [Fact]
    public void A_description_the_person_wrote_survives_a_repoint()
    {
        using var dir = new TempFolder();
        var oldExe = FakeExe(dir, "old-Issun.exe");
        var newExe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, newExe);
        WriteEntry(shortcut.ShortcutPath, oldExe, "Discord status");

        shortcut.Ensure();

        Assert.Equal(newExe, StartMenuShortcut.ReadTarget(shortcut.ShortcutPath), ignoreCase: true);
        Assert.Equal("Discord status", StartMenuShortcut.ReadDescription(shortcut.ShortcutPath));
    }

    [Fact]
    public void A_description_the_person_wrote_stays()
    {
        using var dir = new TempFolder();
        var exe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, exe);
        WriteEntry(shortcut.ShortcutPath, exe, "Discord status");
        var written = File.GetLastWriteTimeUtc(shortcut.ShortcutPath);

        using var log = Log.Capture();
        shortcut.Ensure();

        Assert.Equal("Discord status", StartMenuShortcut.ReadDescription(shortcut.ShortcutPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(shortcut.ShortcutPath));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void A_new_entry_has_no_description()
    {
        using var dir = new TempFolder();
        var shortcut = Make(dir, FakeExe(dir, "Issun.exe"));

        shortcut.Ensure();

        Assert.Null(StartMenuShortcut.ReadDescription(shortcut.ShortcutPath));
    }

    [Fact]
    public void First_run_adds_an_entry_that_opens_this_exe()
    {
        using var dir = new TempFolder();
        var exe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, exe);

        shortcut.Ensure();

        Assert.True(File.Exists(shortcut.ShortcutPath));
        Assert.Equal(exe, StartMenuShortcut.ReadTarget(shortcut.ShortcutPath), ignoreCase: true);
        Assert.True(File.Exists(dir.File("marker")));
    }

    [Fact]
    public void An_entry_the_person_deleted_stays_deleted()
    {
        using var dir = new TempFolder();
        var exe = FakeExe(dir, "Issun.exe");
        var shortcut = Make(dir, exe);

        shortcut.Ensure();
        File.Delete(shortcut.ShortcutPath);
        shortcut.Ensure();

        Assert.False(File.Exists(shortcut.ShortcutPath));
    }

    [Fact]
    public void A_moved_exe_repoints_the_existing_entry()
    {
        using var dir = new TempFolder();
        var oldExe = FakeExe(dir, "old-Issun.exe");
        var newExe = FakeExe(dir, "Issun.exe");

        Make(dir, oldExe).Ensure();
        using var log = Log.Capture();
        var moved = Make(dir, newExe);
        moved.Ensure();

        Assert.Equal(newExe, StartMenuShortcut.ReadTarget(moved.ShortcutPath), ignoreCase: true);
        Assert.Contains(log.Lines, l => l.StartsWith("[startmenu] the Start menu entry opened"));
    }

    [Fact]
    public void An_entry_already_pointing_here_is_left_alone_and_counts_as_added()
    {
        using var dir = new TempFolder();
        var exe = FakeExe(dir, "Issun.exe");
        Make(dir, exe).Ensure();
        File.Delete(dir.File("marker"));
        var written = File.GetLastWriteTimeUtc(dir.File(StartMenuShortcut.FileName));

        using var log = Log.Capture();
        Make(dir, exe).Ensure();

        Assert.Equal(written, File.GetLastWriteTimeUtc(dir.File(StartMenuShortcut.FileName)));
        Assert.True(File.Exists(dir.File("marker")));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Local_builds_never_touch_the_start_menu()
    {
        using var dir = new TempFolder();
        var shortcut = Make(dir, FakeExe(dir, "Issun.exe"), release: false);

        shortcut.Ensure();

        Assert.False(File.Exists(shortcut.ShortcutPath));
        Assert.False(File.Exists(dir.File("marker")));
    }

    [Fact]
    public void Running_under_dotnet_is_refused_and_logged()
    {
        using var dir = new TempFolder();
        using var log = Log.Capture();

        Make(dir, FakeExe(dir, "dotnet.exe")).Ensure();

        Assert.False(File.Exists(dir.File(StartMenuShortcut.FileName)));
        Assert.Contains(log.Lines, l => l.StartsWith("[startmenu] not adding"));
    }
}
