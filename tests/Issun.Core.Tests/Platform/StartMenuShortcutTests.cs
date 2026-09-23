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
