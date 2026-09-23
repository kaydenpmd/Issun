using Issun.Core.Platform;
using Microsoft.Win32;

namespace Issun.Core.Tests.Platform;

/// <summary>
/// Every test works under HKCU\Software\IssunTests\&lt;guid&gt;, never the real
/// Run or StartupApproved keys, and deletes that key afterwards.
/// </summary>
public class AutostartTests : IDisposable
{
    private const string TestsRoot = @"Software\IssunTests";
    private const string Exe = @"C:\Program Files\Issun\Issun.exe";
    private const string Name = "Issun";

    private readonly string _root = $@"{TestsRoot}\{Guid.NewGuid():N}";

    private string RunPath => _root + @"\Run";
    private string ApprovedPath => _root + @"\Approved";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(TestsRoot, writable: false);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            parent.Dispose();
            try
            {
                Registry.CurrentUser.DeleteSubKey(TestsRoot, throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                // Another test class created its own key in the meantime; it tidies up.
            }
        }
    }

    private Autostart Make(string exe = Exe) => new(RunPath, ApprovedPath, Name, exe);

    private object? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath);
        return key?.GetValue(Name);
    }

    private object? ApprovedValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
        return key?.GetValue(Name);
    }

    private void SetRun(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        key.SetValue(Name, command, RegistryValueKind.String);
    }

    private void SetApproved(params byte[] flag)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedPath);
        key.SetValue(Name, flag, RegistryValueKind.Binary);
    }

    /// <summary>What Task Manager writes when it disables an entry: 03, three zeros, then a FILETIME.</summary>
    private static byte[] TaskManagerDisabled() =>
        [0x03, 0, 0, 0, .. BitConverter.GetBytes(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc())];

    [Fact]
    public void Nothing_registered_reads_as_off()
    {
        Assert.False(Make().IsEnabled);
    }

    [Fact]
    public void Enable_writes_the_quoted_exe_with_the_background_switch()
    {
        using var log = Log.Capture();
        var autostart = Make();

        autostart.Enable();

        Assert.Equal($"\"{Exe}\" --background", RunValue());
        Assert.Equal(RegistryValueKind.String, Registry.CurrentUser.OpenSubKey(RunPath)!.GetValueKind(Name));
        Assert.True(autostart.IsEnabled);
        Assert.Contains($"[autostart] Issun will start with Windows: \"{Exe}\" --background", log.Lines);
        // Nothing to clear, so nothing written for Task Manager.
        Assert.Null(ApprovedValue());
    }

    [Fact]
    public void Enabling_twice_logs_once()
    {
        using var log = Log.Capture();
        var autostart = Make();
        autostart.Enable();
        autostart.Enable();

        Assert.Single(log.Lines, l => l.StartsWith("[autostart] Issun will start with Windows", StringComparison.Ordinal));
    }

    [Fact]
    public void Disable_removes_the_entry_and_task_managers_record_of_it()
    {
        var autostart = Make();
        autostart.Enable();
        SetApproved(0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        using var log = Log.Capture();
        autostart.Disable();

        Assert.Null(RunValue());
        Assert.Null(ApprovedValue());
        Assert.False(autostart.IsEnabled);
        Assert.Contains("[autostart] Issun will no longer start with Windows", log.Lines);
    }

    [Fact]
    public void Disable_with_nothing_registered_is_quiet()
    {
        using var log = Log.Capture();
        Make().Disable();
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Switched_off_in_task_manager_reads_as_off_even_with_a_run_entry()
    {
        SetRun($"\"{Exe}\" --background");
        SetApproved(TaskManagerDisabled());

        Assert.False(Make().IsEnabled);
    }

    [Fact]
    public void Enable_clears_task_managers_disabled_switch()
    {
        SetRun($"\"{Exe}\" --background");
        SetApproved(TaskManagerDisabled());
        var autostart = Make();

        using var log = Log.Capture();
        autostart.Enable();

        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ApprovedValue());
        Assert.True(autostart.IsEnabled);
        Assert.Contains("[autostart] cleared the \"disabled\" switch Task Manager had set for Issun", log.Lines);
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, true)]
    [InlineData(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void The_low_bit_of_the_first_byte_is_the_switch(byte[] flag, bool disabled)
    {
        Assert.Equal(disabled, Autostart.IsDisabledFlag(flag));

        SetRun($"\"{Exe}\" --background");
        SetApproved(flag);
        Assert.Equal(!disabled, Make().IsEnabled);
    }

    [Fact]
    public void A_non_binary_approved_value_is_not_read_as_disabled()
    {
        Assert.False(Autostart.IsDisabledFlag("03"));
        Assert.False(Autostart.IsDisabledFlag(3));
        Assert.False(Autostart.IsDisabledFlag(null));
    }

    [Fact]
    public void Repair_repoints_an_entry_naming_a_file_that_has_moved()
    {
        SetRun("\"C:\\Users\\someone\\Downloads\\Issun.exe\" --background");

        using var log = Log.Capture();
        Make().RepairIfMoved();

        Assert.Equal($"\"{Exe}\" --background", RunValue());
        Assert.Contains(log.Lines, l => l.StartsWith("[autostart] startup entry repointed from C:\\Users\\someone\\Downloads\\Issun.exe, which no longer exists", StringComparison.Ordinal)
                                        && l.EndsWith(Exe, StringComparison.Ordinal));
    }

    [Fact]
    public void Repair_repoints_an_entry_naming_another_copy_that_still_exists()
    {
        using var dir = new TempFolder();
        var other = dir.File("Issun.exe");
        File.WriteAllBytes(other, []);
        SetRun($"\"{other}\" --background");

        using var log = Log.Capture();
        Make().RepairIfMoved();

        Assert.Equal($"\"{Exe}\" --background", RunValue());
        Assert.Contains($"[autostart] startup entry repointed from another copy of Issun at {other} to {Exe}", log.Lines);
    }

    [Fact]
    public void Repair_leaves_a_current_entry_alone_and_says_nothing()
    {
        SetRun($"\"{Exe}\" --background");

        using var log = Log.Capture();
        Make().RepairIfMoved();

        Assert.Equal($"\"{Exe}\" --background", RunValue());
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Repair_restores_the_background_switch_on_the_right_exe()
    {
        SetRun(Exe);
        Make().RepairIfMoved();
        Assert.Equal($"\"{Exe}\" --background", RunValue());
    }

    [Fact]
    public void Repair_does_not_create_an_entry_or_undo_task_managers_switch()
    {
        var autostart = Make();
        autostart.RepairIfMoved();
        Assert.Null(RunValue());

        SetRun("\"D:\\old\\Issun.exe\" --background");
        SetApproved(TaskManagerDisabled());
        autostart.RepairIfMoved();

        Assert.Equal($"\"{Exe}\" --background", RunValue());
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void Running_under_dotnet_exe_never_registers_the_dotnet_host()
    {
        var underDotnet = Make(@"C:\Program Files\dotnet\dotnet.exe");

        using var log = Log.Capture();
        underDotnet.Enable();

        Assert.Null(RunValue());
        Assert.False(underDotnet.IsEnabled);
        Assert.Contains(log.Lines, l => l.StartsWith("[autostart] not enabled: running under dotnet.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_exe_is_this_process()
    {
        Assert.Equal(Environment.ProcessPath, new Autostart(RunPath, ApprovedPath, Name).ExePath);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Issun\\Issun.exe\" --background", "C:\\Program Files\\Issun\\Issun.exe")]
    [InlineData("\"C:\\Program Files\\Issun\\Issun.exe\"", "C:\\Program Files\\Issun\\Issun.exe")]
    [InlineData("C:\\Program Files\\Issun\\Issun.exe --background", "C:\\Program Files\\Issun\\Issun.exe")]
    [InlineData("C:\\Tools\\Issun.EXE", "C:\\Tools\\Issun.EXE")]
    [InlineData("  \"C:\\a b\\Issun.exe\"  ", "C:\\a b\\Issun.exe")]
    [InlineData("\"C:\\unterminated\\Issun.exe", "C:\\unterminated\\Issun.exe")]
    [InlineData("C:\\NoExtension --background", "C:\\NoExtension")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    public void The_exe_is_read_back_out_of_a_run_value(string command, string? exe)
    {
        Assert.Equal(exe, Autostart.ExeFromCommand(command));
    }
}
