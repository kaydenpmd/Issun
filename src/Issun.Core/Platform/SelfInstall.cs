using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Issun.Core.Platform;

/// <summary>
/// Issun installs itself. A release is one .exe that lands wherever the browser
/// put it; the first time it runs from anywhere else, it copies itself to
/// <c>%LOCALAPPDATA%\Programs\Issun\Issun.exe</c>, where per-user apps live on
/// Windows, registers an entry in Settings › Apps so it can be uninstalled
/// like anything else, and hands over to the copy. No installer, no admin.
///
/// Opening a newer download does the same over the installed copy, which is
/// how an update happens: the running Issun is asked to quit, the old .exe is
/// kept beside the new one as <c>Issun.exe.previous</c>, and the new one
/// starts. That is the procedure this repo's CLAUDE.md records doing by hand
/// ("Updating the installed copy"), rename first, because Windows lets a
/// running .exe be renamed but not overwritten.
///
/// The Run key and the Start menu entry need nothing from here: both already
/// repoint themselves at whichever copy is running
/// (<see cref="Autostart.RepairIfMoved"/>, <see cref="StartMenuShortcut.Ensure"/>).
///
/// Local builds (build 0) never install, for the same reason they leave the
/// Start menu alone: a bin folder is not somewhere to point the owner's setup.
/// </summary>
public sealed class SelfInstall
{
    public const string DefaultUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Issun";
    public const string ExeName = "Issun.exe";
    public const string PreviousName = "Issun.exe.previous";
    public const string UninstallArgument = "--uninstall";

    private readonly string _uninstallKeyPath;
    private readonly bool _releaseBuild;

    /// <param name="installDir">Where Issun lives once installed; null means <see cref="DefaultInstallDir"/>.</param>
    /// <param name="exePath">The .exe that is running; null means this process's own.</param>
    /// <param name="uninstallKeyPath">Under HKEY_CURRENT_USER. Tests use a throwaway key.</param>
    /// <param name="releaseBuild">False for local builds, which never install.</param>
    public SelfInstall(
        string? installDir = null,
        string? exePath = null,
        string uninstallKeyPath = DefaultUninstallKeyPath,
        bool? releaseBuild = null)
    {
        InstallDir = installDir ?? DefaultInstallDir;
        ExePath = exePath ?? Environment.ProcessPath ?? "";
        _uninstallKeyPath = uninstallKeyPath;
        _releaseBuild = releaseBuild ?? IssunInfo.Build > 0;
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Programs\Issun</c>, the folder the owner's copy has
    /// lived in since 22 Sept 2026. Under ISSUN_HOME, inside it instead, so a
    /// test copy never lands on the real one.
    /// </summary>
    public static string DefaultInstallDir =>
        Environment.GetEnvironmentVariable("ISSUN_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "Programs", "Issun")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Issun");

    public string InstallDir { get; }

    public string ExePath { get; }

    public string InstalledExe => Path.Combine(InstallDir, ExeName);

    public string PreviousExe => Path.Combine(InstallDir, PreviousName);

    /// <summary>Running from the installed path already.</summary>
    public bool IsInstalledCopy => SamePath(ExePath, InstalledExe);

    /// <summary>A release build running from somewhere other than the install folder: a download to install.</summary>
    public bool ShouldInstall =>
        _releaseBuild
        && ExePath.Length > 0
        && !string.Equals(Path.GetFileName(ExePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
        && !IsInstalledCopy;

    /// <summary>
    /// Copies this .exe into the install folder, keeping any copy already there
    /// as <see cref="PreviousExe"/>, and registers the uninstall entry. Throws
    /// on failure, having put the previous copy back where it was; the caller
    /// shows why. Whoever was running the installed copy must have quit first.
    /// </summary>
    public void Install()
    {
        Directory.CreateDirectory(InstallDir);
        var hadPrevious = File.Exists(InstalledExe);
        var oldVersion = hadPrevious ? DescribeVersion(InstalledExe) : null;
        if (hadPrevious)
        {
            if (File.Exists(PreviousExe))
                File.Delete(PreviousExe);
            File.Move(InstalledExe, PreviousExe);
        }

        try
        {
            File.Copy(ExePath, InstalledExe, overwrite: false);
            if (new FileInfo(InstalledExe).Length != new FileInfo(ExePath).Length)
                throw new IOException("the copy came out a different size from the download");
        }
        catch (Exception ex)
        {
            Log.Write($"[install] copying {ExePath} to {InstalledExe} failed: {ex.Message}");
            try
            {
                if (File.Exists(InstalledExe))
                    File.Delete(InstalledExe);
                if (hadPrevious)
                    File.Move(PreviousExe, InstalledExe);
            }
            catch (Exception undo)
            {
                Log.Write($"[install] and putting the previous copy back failed too: {undo.Message}");
            }
            throw;
        }

        Log.Write(hadPrevious
            ? $"[install] replaced {oldVersion} in {InstallDir} with {IssunInfo.Display}; the old one is kept as {PreviousName}"
            : $"[install] installed {IssunInfo.Display} to {InstallDir} from {ExePath}");
        WriteUninstallEntry(InstalledExe);
    }

    /// <summary>
    /// The installed copy keeps its Settings › Apps entry current: the version
    /// after an update, and an entry at all for a copy installed by hand before
    /// Issun did this itself. Never throws.
    /// </summary>
    public void EnsureRegistered()
    {
        try
        {
            if (!_releaseBuild || !IsInstalledCopy)
                return;
            using var key = Registry.CurrentUser.OpenSubKey(_uninstallKeyPath);
            if (key?.GetValue("DisplayVersion") as string == IssunInfo.Display
                && SamePath(key.GetValue("InstallLocation") as string ?? "", InstallDir))
                return;
            WriteUninstallEntry(InstalledExe);
            Log.Write($"[install] registered {IssunInfo.Display} in Settings › Apps");
        }
        catch (Exception ex)
        {
            Log.Write($"[install] could not update the Settings › Apps entry: {ex.Message}");
        }
    }

    /// <summary>
    /// The values Windows' Installed apps list reads. Microsoft documents them
    /// under "Uninstall Registry Key" (learn.microsoft.com, Windows Installer).
    /// No Publisher: that is public text, and the owner's to write.
    /// </summary>
    internal void WriteUninstallEntry(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_uninstallKeyPath);
        key.SetValue("DisplayName", "Issun");
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("DisplayVersion", IssunInfo.Display);
        key.SetValue("InstallLocation", Path.GetDirectoryName(exe) ?? InstallDir);
        key.SetValue("UninstallString", $"\"{exe}\" {UninstallArgument}");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (File.Exists(exe))
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>
    /// Everything the install added, except the person's settings, key, logs
    /// and history, which stay in %APPDATA% and %LOCALAPPDATA% so a reinstall
    /// picks up where this left off. The .exe can't delete itself while it
    /// runs, so a hidden cmd removes the folder once this process has gone.
    /// </summary>
    public void Uninstall(IAutostart autostart, StartMenuShortcut shortcut)
    {
        try { autostart.Disable(); Log.Write("[install] uninstall: removed Start with Windows"); }
        catch (Exception ex) { Log.Write($"[install] uninstall: could not remove Start with Windows: {ex.Message}"); }

        try { shortcut.Remove(); }
        catch (Exception ex) { Log.Write($"[install] uninstall: could not remove the Start menu entry: {ex.Message}"); }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_uninstallKeyPath, throwOnMissingSubKey: false);
            Log.Write("[install] uninstall: removed the Settings › Apps entry");
        }
        catch (Exception ex) { Log.Write($"[install] uninstall: could not remove the Settings › Apps entry: {ex.Message}"); }

        if (!Directory.Exists(InstallDir))
            return;
        try
        {
            // ping waits about two seconds without needing a console, which
            // timeout.exe does; by then this process has exited.
            var dir = InstallDir.TrimEnd('\\');
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            });
            Log.Write($"[install] uninstall: {dir} will be removed once Issun has quit");
        }
        catch (Exception ex)
        {
            Log.Write($"[install] uninstall: could not remove {InstallDir}: {ex.Message}");
        }
    }

    private static string DescribeVersion(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return info.FilePrivatePart > 0
                ? $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart} ({info.FilePrivatePart})"
                : "an earlier build";
        }
        catch
        {
            return "an earlier build";
        }
    }

    internal static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
