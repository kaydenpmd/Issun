using Microsoft.Win32;

namespace Issun.Core.Platform;

/// <summary>
/// Start with Windows, through the per-user Run key.
///
/// The Run key is what relay.py's Scheduled Task had to be configured into
/// being. Discord's IPC pipe belongs to the logged-in desktop session, and
/// install-task.ps1 insisted on <c>-LogonType Interactive</c> because "run
/// whether user is logged on or not" registers a session-0 task that starts
/// cleanly, listens on 8787, and never reaches the pipe — a failure that looks
/// like a Discord problem rather than a task one. Explorer launches Run-key
/// entries inside the user's own session at logon, so Issun gets the
/// interactive session for free, with no admin rights and nothing that can be
/// misconfigured into session 0.
///
/// Explorer also staggers startup apps by a few seconds, doing the job of the
/// task's 30 s delay. Neither ever mattered for correctness: the presence
/// worker retries until Discord is up.
///
/// Task Manager's Startup tab (and Settings → Apps → Startup) doesn't touch
/// the Run value; it records its switch separately, under
/// Explorer\StartupApproved\Run, as a 12-byte binary value of the same name.
/// Microsoft doesn't document it. What forensic write-ups agree on (e.g.
/// windowsir.blogspot.com, July 2022): the first DWORD is the state — 02 or 06
/// enabled, 03 disabled, and a disabled entry's last eight bytes are the
/// FILETIME it was switched off. Enabled is what Windows writes as 02 followed
/// by eleven zeros. Checked on the owner's machine in September 2026: 02 with
/// eleven zeros for every enabled entry, 03 with a timestamp for the one
/// switched off in Task Manager — and one 01 with no timestamp, which nothing
/// documents. Reading the low bit as the switch fits every value seen — 02 and
/// 06 on, 01 and 03 off — so that is the rule: odd means disabled. Misreading
/// an odd value as off costs little, because ticking the box rewrites it as 02.
/// An absent value means enabled; Windows only writes one once somebody flips
/// the switch. Ignoring the value altogether would show "Start with Windows"
/// ticked for an app Windows will not start.
/// </summary>
public sealed class Autostart : IAutostart
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The argument that tells Issun it was started at logon: come up in the tray, no window.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>What Task Manager writes when it enables an entry.</summary>
    private static readonly byte[] EnabledFlag = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly string _runKeyPath;
    private readonly string _approvedKeyPath;
    private readonly string _valueName;
    private readonly object _gate = new();
    private string? _lastReadProblem;

    /// <param name="runKeyPath">Under HKEY_CURRENT_USER.</param>
    /// <param name="approvedKeyPath">Under HKEY_CURRENT_USER.</param>
    /// <param name="valueName">The name Task Manager lists the entry under.</param>
    /// <param name="exePath">The .exe to start; null means this process's own.</param>
    public Autostart(
        string runKeyPath = DefaultRunKeyPath,
        string approvedKeyPath = DefaultApprovedKeyPath,
        string valueName = "Issun",
        string? exePath = null)
    {
        _runKeyPath = runKeyPath;
        _approvedKeyPath = approvedKeyPath;
        _valueName = valueName;
        ExePath = exePath ?? Environment.ProcessPath ?? "";
    }

    public string ExePath { get; }

    /// <summary>The Run value Issun writes: <c>"C:\path\Issun.exe" --background</c>.</summary>
    public string Command => $"\"{ExePath}\" {BackgroundArgument}";

    /// <summary>
    /// A Run entry exists and Task Manager hasn't switched it off. True even when
    /// the entry points at a different copy of Issun; <see cref="RepairIfMoved"/>
    /// deals with that.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                var command = ReadRunCommand();
                var enabled = !string.IsNullOrWhiteSpace(command) && !IsSwitchedOffInTaskManager();
                _lastReadProblem = null;
                return enabled;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                // Polled by the window, so logged once per distinct problem
                // rather than once per repaint.
                var problem = $"[autostart] could not read the startup entry: {ex.Message}";
                if (problem != Interlocked.Exchange(ref _lastReadProblem, problem))
                    Log.Write(problem);
                return false;
            }
        }
    }

    /// <summary>
    /// Registers this .exe to start at logon, and clears Task Manager's
    /// "disabled" if it was set — ticking the box in Issun should mean Issun
    /// starts, whatever was clicked elsewhere before. Failures are logged and
    /// leave <see cref="IsEnabled"/> telling the truth, rather than thrown into
    /// a checkbox binding.
    /// </summary>
    public void Enable()
    {
        lock (_gate)
        {
            if (!CanRegister(out var why))
            {
                Log.Write($"[autostart] not enabled: {why}");
                return;
            }
            try
            {
                var before = ReadRunCommand();
                using (var run = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true))
                    run.SetValue(_valueName, Command, RegistryValueKind.String);
                if (before != Command)
                    Log.Write($"[autostart] Issun will start with Windows: {Command}");

                if (IsSwitchedOffInTaskManager())
                {
                    using var approved = Registry.CurrentUser.CreateSubKey(_approvedKeyPath, writable: true);
                    approved.SetValue(_valueName, EnabledFlag, RegistryValueKind.Binary);
                    Log.Write("[autostart] cleared the \"disabled\" switch Task Manager had set for Issun");
                }
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                Log.Write($"[autostart] could not enable starting with Windows: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Removes the Run entry, and Task Manager's record of it with it, so the
    /// Startup tab doesn't keep listing an entry that no longer exists.
    /// </summary>
    public void Disable()
    {
        lock (_gate)
        {
            try
            {
                var existed = ReadRunCommand() is not null;
                using (var run = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true))
                    run?.DeleteValue(_valueName, throwOnMissingValue: false);
                using (var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath, writable: true))
                    approved?.DeleteValue(_valueName, throwOnMissingValue: false);
                if (existed)
                    Log.Write("[autostart] Issun will no longer start with Windows");
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                Log.Write($"[autostart] could not stop Issun starting with Windows: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Keeps the Run entry pointing at this .exe after it has been moved — a
    /// single-file download lives wherever the browser put it, and gets tidied
    /// into a folder later. Without this, the entry silently goes on naming a
    /// file that no longer exists, and Issun just doesn't start one morning.
    ///
    /// Repoints whenever the entry names any other file, not only a missing
    /// one. A newer download kept beside the old copy is the common case, and
    /// leaving the entry alone there is the worse failure: the old copy wins
    /// every logon, takes the port first, and the box the person ticked in the
    /// new copy shows ticked the whole time. The copy that is running is the
    /// one the person chose to run, so it is the one that starts at logon.
    /// Both paths go in the log, so a surprise is traceable.
    ///
    /// Task Manager's switch is left as it is either way: repointing fixes the
    /// path, not the person's choice.
    /// </summary>
    public void RepairIfMoved()
    {
        lock (_gate)
        {
            try
            {
                var command = ReadRunCommand();
                if (string.IsNullOrWhiteSpace(command))
                    return;
                if (!CanRegister(out var why))
                {
                    Log.Write($"[autostart] not checking the startup entry: {why}");
                    return;
                }

                var registered = ExeFromCommand(command);
                if (registered is not null && SamePath(registered, ExePath))
                {
                    if (command != Command)
                    {
                        Write(Command);
                        Log.Write($"[autostart] startup entry rewritten as {Command}");
                    }
                    return;
                }

                Write(Command);
                var what = registered is null ? $"\"{command}\""
                    : File.Exists(registered) ? $"another copy of Issun at {registered}"
                    : $"{registered}, which no longer exists";
                Log.Write($"[autostart] startup entry repointed from {what} to {ExePath}");
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                Log.Write($"[autostart] could not check or repair the startup entry: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The .exe named by a Run value — quoted, as Issun writes it, or bare, as
    /// a hand edit might leave it. Null when nothing that looks like a path is there.
    /// </summary>
    internal static string? ExeFromCommand(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
            return null;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            var quoted = close < 0 ? text[1..] : text[1..close];
            return quoted.Length > 0 ? quoted : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0 && (exe + 4 == text.Length || char.IsWhiteSpace(text[exe + 4])))
            return text[..(exe + 4)];
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>Task Manager's switch: odd first byte means disabled. See the class comment.</summary>
    internal static bool IsDisabledFlag(object? value) =>
        value is byte[] { Length: > 0 } data && (data[0] & 1) == 1;

    private bool IsSwitchedOffInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath, writable: false);
        return IsDisabledFlag(approved?.GetValue(_valueName));
    }

    private string? ReadRunCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
        return run?.GetValue(_valueName) as string;
    }

    private void Write(string command)
    {
        using var run = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
        run.SetValue(_valueName, command, RegistryValueKind.String);
    }

    /// <summary>
    /// Under <c>dotnet Issun.dll</c>, the process path is dotnet.exe, and a Run
    /// entry for it would start the .NET host with no app at every logon.
    /// </summary>
    private bool CanRegister(out string why)
    {
        if (string.IsNullOrWhiteSpace(ExePath))
        {
            why = "could not tell which .exe is running";
            return false;
        }
        if (string.Equals(Path.GetFileName(ExePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            why = "running under dotnet.exe rather than as Issun.exe — only the published Issun.exe can start with Windows";
            return false;
        }
        why = "";
        return true;
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

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException;
}
