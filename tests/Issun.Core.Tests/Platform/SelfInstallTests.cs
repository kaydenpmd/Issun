using Issun.Core.Platform;
using Microsoft.Win32;

namespace Issun.Core.Tests.Platform;

/// <summary>
/// Every test installs into its own temp folder and registers under a
/// throwaway HKCU\Software\IssunTests\&lt;guid&gt; key, never the real
/// Uninstall key or %LOCALAPPDATA%\Programs\Issun, where the owner's live copy runs.
/// </summary>
public sealed class SelfInstallTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly string _keyPath = $@"Software\IssunTests\{Guid.NewGuid():N}\Uninstall\Issun";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath[..(_keyPath.IndexOf(@"\Uninstall", StringComparison.Ordinal))], throwOnMissingSubKey: false);
        _dir.Dispose();
    }

    private string Download(string contents = "new build")
    {
        var path = _dir.File(@"Downloads\Issun-0.1.0-b60-abc1234.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private string InstallDir => _dir.File(@"Programs\Issun");

    private SelfInstall Make(string exe, bool release = true) =>
        new(installDir: InstallDir, exePath: exe, uninstallKeyPath: _keyPath, releaseBuild: release);

    [Fact]
    public void A_download_installs_and_the_installed_copy_does_not()
    {
        Assert.True(Make(Download()).ShouldInstall);
        Assert.False(Make(Path.Combine(InstallDir, "Issun.exe")).ShouldInstall);
        Assert.True(Make(Path.Combine(InstallDir, "Issun.exe")).IsInstalledCopy);
    }

    [Fact]
    public void Local_builds_and_dotnet_run_never_install()
    {
        Assert.False(Make(Download(), release: false).ShouldInstall);
        Assert.False(Make(@"C:\Program Files\dotnet\dotnet.exe").ShouldInstall);
    }

    [Fact]
    public void Installing_copies_the_exe_and_registers_it_in_apps()
    {
        var install = Make(Download("new build"));
        using var log = Log.Capture();

        install.Install();

        Assert.Equal("new build", File.ReadAllText(install.InstalledExe));
        Assert.False(File.Exists(install.PreviousExe));
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath)!;
        Assert.Equal("Issun", key.GetValue("DisplayName"));
        Assert.Equal($"\"{install.InstalledExe}\" --uninstall", key.GetValue("UninstallString"));
        Assert.Equal(install.InstalledExe + ",0", key.GetValue("DisplayIcon"));
        Assert.Equal(InstallDir, key.GetValue("InstallLocation"));
        Assert.Equal(1, key.GetValue("NoModify"));
        Assert.Equal(1, key.GetValue("NoRepair"));
        Assert.Null(key.GetValue("Publisher"));
        Assert.Contains(log.Lines, l => l.StartsWith("[install] installed "));
    }

    [Fact]
    public void An_update_keeps_the_previous_copy_beside_it()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "Issun.exe"), "old build");
        File.WriteAllText(Path.Combine(InstallDir, "Issun.exe.previous"), "older build");
        var install = Make(Download("new build"));

        install.Install();

        Assert.Equal("new build", File.ReadAllText(install.InstalledExe));
        Assert.Equal("old build", File.ReadAllText(install.PreviousExe));
    }

    [Fact]
    public void Hand_kept_backups_are_left_alone()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "Issun.exe"), "old build");
        File.WriteAllText(Path.Combine(InstallDir, "Issun.exe.b46.bak"), "build 46");

        Make(Download()).Install();

        Assert.Equal("build 46", File.ReadAllText(Path.Combine(InstallDir, "Issun.exe.b46.bak")));
    }

    [Fact]
    public void A_failed_copy_puts_the_previous_copy_back()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "Issun.exe"), "old build");
        var install = Make(_dir.File("missing.exe"));
        using var log = Log.Capture();

        Assert.ThrowsAny<IOException>(install.Install);

        Assert.Equal("old build", File.ReadAllText(install.InstalledExe));
        Assert.False(File.Exists(install.PreviousExe));
        Assert.Contains(log.Lines, l => l.StartsWith("[install] copying "));
    }

    [Fact]
    public void The_installed_copy_registers_itself_if_nothing_did()
    {
        Directory.CreateDirectory(InstallDir);
        var exe = Path.Combine(InstallDir, "Issun.exe");
        File.WriteAllText(exe, "build");

        Make(exe).EnsureRegistered();

        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.NotNull(key);
        Assert.Equal(IssunInfo.Display, key!.GetValue("DisplayVersion"));
    }

    [Fact]
    public void A_download_does_not_register_itself_before_installing()
    {
        Make(Download()).EnsureRegistered();

        Assert.Null(Registry.CurrentUser.OpenSubKey(_keyPath));
    }

    [Fact]
    public void Under_issun_home_the_install_folder_is_inside_it()
    {
        var before = Environment.GetEnvironmentVariable("ISSUN_HOME");
        try
        {
            Environment.SetEnvironmentVariable("ISSUN_HOME", _dir.Path);
            Assert.Equal(Path.Combine(_dir.Path, "Programs", "Issun"), SelfInstall.DefaultInstallDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ISSUN_HOME", before);
        }
    }
}
