using System.IO;
using Issun.Core;
using Issun.Presentation;

namespace Issun.ViewModels;

// The Settings section. The form holds text as typed and is checked only on
// Apply (Presentation.SettingsForm), so a half-typed port never reaches the
// host. Start with Windows waits for Apply too (the owner, 12 Nov 2026: "don't
// apply it straight away for the sake of consistency with the other
// settings"), though it isn't a Setting but the Run key; the tray menu's item
// still applies at once, having no Apply to wait for.
public sealed partial class MainViewModel
{
    /// <summary>The host's settings when the form was last loaded, to tell "the person edited this" from "the host changed underneath".</summary>
    private Settings? _formBase;

    public AsyncCommand ApplySettingsCommand { get; private set; } = null!;
    public Command RevertSettingsCommand { get; private set; } = null!;
    public Flash SettingsSaved { get; }

    private void InitSettings()
    {
        ApplySettingsCommand = new AsyncCommand("Apply settings", ApplySettingsAsync,
            ex => SettingsErrors = $"Couldn't save: {ex.Message}", () => SettingsDirty);
        RevertSettingsCommand = new Command(() =>
        {
            LoadFields(_host.Settings);
            _loading = true;
            try { StartWithWindowsField = _startWithWindows; }
            finally { _loading = false; }
            SettingsErrors = null;
            UpdateDirty();
        });
    }

    private void RefreshSettings()
    {
        var current = _host.Settings;
        if (_formBase == current)
            return;

        // Settings changed underneath the form — the setup window, a new key, the host
        // filling something in. Reload unless the person is mid-edit, in which
        // case their edits stay and Apply lays them over whatever is current.
        var editing = _formBase is not null && SettingsForm.Differs(Fields(), _formBase);
        _formBase = current;
        if (!editing)
            LoadFields(current);
        UpdateDirty();
    }

    private async Task ApplySettingsAsync()
    {
        SettingsSaved.Clear();
        var result = SettingsForm.Validate(Fields(), _host.Settings);
        if (result.Settings is not { } updated)
        {
            SettingsErrors = string.Join(Environment.NewLine, result.Errors);
            return;
        }
        SettingsErrors = null;
        await _host.UpdateSettingsAsync(updated);
        _formBase = null;          // reload from what the host actually kept
        RefreshSettings();
        if (_startWithWindowsField != _startWithWindows)
        {
            Log.Write($"[ui] Apply: start with Windows {OnOff(_startWithWindowsField)}");
            _startWithWindows = _startWithWindowsField;
            Raise(nameof(StartWithWindows));
            await ApplyStartWithWindowsAsync(_startWithWindowsField);
        }
        UpdateDirty();
        SettingsSaved.Show("Saved");
    }

    private SettingsFields Fields() => new()
    {
        PublicRead = _publicRead,
        Port = _port,
    };

    private void LoadFields(Settings s)
    {
        var f = SettingsFields.From(s);
        _loading = true;
        try
        {
            PublicRead = f.PublicRead;
            Port = f.Port;
        }
        finally
        {
            _loading = false;
        }
        UpdateDirty();
    }

    private bool _loading;

    private void Edited()
    {
        if (_loading)
            return;
        SettingsSaved.Clear();
        // Once errors are showing, re-check as the person types so each one
        // disappears the moment it is fixed.
        if (SettingsErrors is not null)
        {
            var result = SettingsForm.Validate(Fields(), _host.Settings);
            SettingsErrors = result.Ok ? null : string.Join(Environment.NewLine, result.Errors);
        }
        UpdateDirty();
    }

    private void UpdateDirty()
    {
        SettingsDirty = SettingsForm.Differs(Fields(), _host.Settings) || _startWithWindowsField != _startWithWindows;
        ApplySettingsCommand.RaiseCanExecuteChanged();
    }

    public bool SettingsDirty { get => _settingsDirty; private set => Set(ref _settingsDirty, value); }
    private bool _settingsDirty;

    public string? SettingsErrors { get => _settingsErrors; private set => Set(ref _settingsErrors, value); }
    private string? _settingsErrors;

    public bool PublicRead { get => _publicRead; set { if (Set(ref _publicRead, value)) Edited(); } }
    private bool _publicRead;

    public string Port { get => _port; set { if (Set(ref _port, value ?? "")) Edited(); } }
    private string _port = "";


    // ─────────────────────────── Start with Windows ───────────────────────────

    /// <summary>
    /// What Windows does: the tray menu's tick, and set from it at once. The
    /// host's property touches the registry, so both reading and writing it
    /// happen off the UI thread; this is the last value read back.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows)
                return;
            var fieldFollows = _startWithWindowsField == _startWithWindows;
            _startWithWindows = value;
            Raise();
            if (fieldFollows)
                FollowStartWithWindows();
            _ = ApplyStartWithWindowsAsync(value);
        }
    }
    private bool _startWithWindows;

    /// <summary>The window's checkbox: a form field, applied by Apply with the rest.</summary>
    public bool StartWithWindowsField { get => _startWithWindowsField; set { if (Set(ref _startWithWindowsField, value)) Edited(); } }
    private bool _startWithWindowsField;

    /// <summary>The checkbox takes Windows' value, as a reload, not an edit.</summary>
    private void FollowStartWithWindows()
    {
        _loading = true;
        try { StartWithWindowsField = _startWithWindows; }
        finally { _loading = false; }
        UpdateDirty();
    }

    public string? StartWithWindowsError { get => _startWithWindowsError; private set => Set(ref _startWithWindowsError, value); }
    private string? _startWithWindowsError;

    private async Task ApplyStartWithWindowsAsync(bool value)
    {
        StartWithWindowsError = null;
        try
        {
            await Task.Run(() => _host.StartWithWindows = value);
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't turn start with Windows {OnOff(value)}: {ex.GetType().Name}: {ex.Message}");
            StartWithWindowsError = $"Couldn't change this: {ex.Message}";
        }

        // Read back rather than trusting the click: what the checkbox shows is
        // what Windows will actually do at the next sign-in. A change that
        // didn't take and didn't throw is exactly the kind of failure that
        // otherwise goes unnoticed until the morning Issun isn't running.
        if (await ReadStartWithWindowsAsync() is { } actual && actual != value && StartWithWindowsError is null)
        {
            Log.Write($"[ui] start with Windows is still {OnOff(actual)} after turning it {OnOff(value)}");
            StartWithWindowsError = $"Windows still has this {OnOff(actual)}. The log may say why.";
        }
    }

    /// <summary>
    /// Reads it again: the setup window changes it through the host, and this
    /// view model, made before setup ran, would otherwise go on showing the
    /// first reading, in the checkbox and the tray menu's tick both. Seen on
    /// 12 Nov 2026: setup's Yes, then an unticked Start with Windows in the
    /// tray menu.
    /// </summary>
    public void RefreshStartWithWindows() => _ = ReadStartWithWindowsAsync();

    private async Task<bool?> ReadStartWithWindowsAsync()
    {
        try
        {
            var actual = await Task.Run(() => _host.StartWithWindows);
            // The checkbox follows what Windows does, unless it's been edited.
            var fieldFollows = _startWithWindowsField == _startWithWindows;
            Set(ref _startWithWindows, actual, nameof(StartWithWindows));
            if (fieldFollows)
                FollowStartWithWindows();
            else
                UpdateDirty();
            return actual;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't read whether Issun starts with Windows: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
