using System.IO;
using Issun.Core;
using Issun.Presentation;

namespace Issun.ViewModels;

// The Settings section. The form holds text as typed and is checked only on
// Apply (Presentation.SettingsForm), so a half-typed port never reaches the
// host. Start with Windows is the exception: it isn't a Setting, it applies the
// moment it is clicked, and the tray menu shows the same value.
public sealed partial class MainViewModel
{
    /// <summary>The host's settings when the form was last loaded, to tell "the person edited this" from "the host changed underneath".</summary>
    private Settings? _formBase;

    /// <summary>One item in a drop-down. A class rather than the tuple SettingsForm uses, because WPF binds to properties, not fields.</summary>
    public sealed record Choice(string Value, string Label);

    public IReadOnlyList<Choice> StatusLineOptions { get; } =
        SettingsForm.StatusLines.Select(s => new Choice(s.Value, s.Label)).ToList();

    public AsyncCommand ApplySettingsCommand { get; private set; } = null!;
    public Command RevertSettingsCommand { get; private set; } = null!;
    public AsyncCommand ImportEnvCommand { get; private set; } = null!;
    public Flash SettingsSaved { get; }

    /// <summary>Set by the view: shows a file picker and returns the chosen path, or null.</summary>
    public Func<string?>? PickEnvFile { get; set; }

    private void InitSettings()
    {
        ApplySettingsCommand = new AsyncCommand("Apply settings", ApplySettingsAsync,
            ex => SettingsErrors = $"Couldn't save: {ex.Message}", () => SettingsDirty);
        RevertSettingsCommand = new Command(() =>
        {
            LoadFields(_host.Settings);
            SettingsErrors = null;
        });
        ImportEnvCommand = new AsyncCommand("Import relay .env", ImportEnvAsync,
            ex => ImportNotes = $"Couldn't import: {ex.Message}");
    }

    private void RefreshSettings()
    {
        var current = _host.Settings;
        if (_formBase == current)
            return;

        // Settings changed underneath the form — an import, a new key, the host
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
        SettingsSaved.Show("Saved");
    }

    private async Task ImportEnvAsync()
    {
        if (PickEnvFile?.Invoke() is not { } path)
            return;
        ImportNotes = null;
        var result = await _host.ImportEnvAsync(path);
        var notes = result.Notes.ToList();
        if (result.UptimeLogImportedFrom is { } from)
            notes.Add($"Check-in history carried over from {from}.");
        if (notes.Count == 0)
            notes.Add($"Nothing to import in {Path.GetFileName(path)}.");
        ImportNotes = string.Join(Environment.NewLine, notes);
        _formBase = null;
        RefreshSettings();
    }

    private SettingsFields Fields() => new()
    {
        DiscordClientId = _clientId,
        StatusLine = _statusLine,
        ShowAlbum = _showAlbum,
        PublicRead = _publicRead,
        Port = _port,
        PublicBase = _publicBase,
        ArtMinScore = _artMinScore,
    };

    private void LoadFields(Settings s)
    {
        var f = SettingsFields.From(s);
        _loading = true;
        try
        {
            ClientId = f.DiscordClientId;
            StatusLine = f.StatusLine;
            ShowAlbum = f.ShowAlbum;
            PublicRead = f.PublicRead;
            Port = f.Port;
            PublicBase = f.PublicBase;
            ArtMinScore = f.ArtMinScore;
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
        SettingsDirty = SettingsForm.Differs(Fields(), _host.Settings);
        ApplySettingsCommand.RaiseCanExecuteChanged();
    }

    public bool SettingsDirty { get => _settingsDirty; private set => Set(ref _settingsDirty, value); }
    private bool _settingsDirty;

    public string? SettingsErrors { get => _settingsErrors; private set => Set(ref _settingsErrors, value); }
    private string? _settingsErrors;

    public string? ImportNotes { get => _importNotes; private set => Set(ref _importNotes, value); }
    private string? _importNotes;

    public string ClientId { get => _clientId; set { if (Set(ref _clientId, value ?? "")) Edited(); } }
    private string _clientId = "";

    /// <summary>"state", "details" or "name" — relay.py's STATUS_LINE values.</summary>
    public string StatusLine { get => _statusLine; set { if (Set(ref _statusLine, value ?? "state")) Edited(); } }
    private string _statusLine = "state";

    public bool ShowAlbum { get => _showAlbum; set { if (Set(ref _showAlbum, value)) Edited(); } }
    private bool _showAlbum;

    public bool PublicRead { get => _publicRead; set { if (Set(ref _publicRead, value)) Edited(); } }
    private bool _publicRead;

    public string Port { get => _port; set { if (Set(ref _port, value ?? "")) Edited(); } }
    private string _port = "";

    public string PublicBase { get => _publicBase; set { if (Set(ref _publicBase, value ?? "")) Edited(); } }
    private string _publicBase = "";

    public string ArtMinScore { get => _artMinScore; set { if (Set(ref _artMinScore, value ?? "")) Edited(); } }
    private string _artMinScore = "";

    public bool AdvancedExpanded { get => _advancedExpanded; set => Set(ref _advancedExpanded, value); }
    private bool _advancedExpanded;

    // ─────────────────────────── Start with Windows ───────────────────────────

    /// <summary>
    /// Bound to the checkbox and mirrored in the tray menu. The host's property
    /// touches the registry or Task Scheduler, so both reading and writing it
    /// happen off the UI thread; this is the last value read back.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows)
                return;
            _startWithWindows = value;
            Raise();
            _ = ApplyStartWithWindowsAsync(value);
        }
    }
    private bool _startWithWindows;

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

    private async Task<bool?> ReadStartWithWindowsAsync()
    {
        try
        {
            var actual = await Task.Run(() => _host.StartWithWindows);
            Set(ref _startWithWindows, actual, nameof(StartWithWindows));
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
