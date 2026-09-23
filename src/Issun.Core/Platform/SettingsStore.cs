using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Issun.Core.Platform;

/// <summary>
/// settings.json — what relay.py's .env was, minus the parts that made .env
/// mandatory rather than convenient. The Scheduled Task inherited no variables
/// from any PowerShell window, so a value set in one shell looked configured
/// and was empty to the running process. Issun has one source: this file.
///
/// The one value in here that cannot be recreated is the key, because Ammy
/// holds a copy. Every path below is shaped around not losing it silently: a
/// fresh key is only ever generated when there was no key to keep, and when
/// that happens the log says so in words that point at Ammy's Key field.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Tolerate the hand edits people make to a JSON file: any capitalisation,
        // a comment, a trailing comma, a port typed in quotes.
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly object _gate = new();
    private Settings _current;

    /// <param name="path">Normally <see cref="Paths.SettingsFile"/>.</param>
    public SettingsStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        _current = Load(out var created);
        CreatedOnThisRun = created;
    }

    public string FilePath { get; }

    public Settings Current => Volatile.Read(ref _current);

    /// <summary>
    /// A key was generated on this run and Ammy does not have it yet: no
    /// settings existed, the file was unreadable and had to be set aside, or it
    /// held no key. All three mean the same thing to the person — pair Ammy, or
    /// import relay.py's .env to keep the key Ammy already has — so all three
    /// report true. (An empty key refuses every request, so a file without one
    /// was never paired with anything.)
    /// </summary>
    public bool CreatedOnThisRun { get; }

    /// <summary>
    /// Raised after every successful save, on the saving thread, with
    /// <see cref="Current"/> as it stands when the handler runs. Handlers run
    /// outside the store's lock, so a handler that marshals to the UI thread
    /// can't deadlock against a save made from it.
    /// </summary>
    public event Action<Settings>? Changed;

    public void Save(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            try
            {
                AtomicFile.WriteAllText(FilePath, Serialize(settings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"[config] could not save settings to {FilePath}: {ex.Message}");
                throw;
            }
            Volatile.Write(ref _current, settings);
        }
        Log.Write($"[config] settings saved to {FilePath}");

        // Current rather than the argument: two saves racing each other can
        // notify out of order, and this way the last notification anyone
        // receives always carries the settings that actually won.
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Settings>>())
        {
            try
            {
                handler(Current);
            }
            catch (Exception ex)
            {
                // The file is written; a broken listener must not make the
                // caller believe the save failed and try again or give up.
                Log.Write($"[config] a settings listener failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal static string Serialize(Settings settings) => JsonSerializer.Serialize(settings, Json);

    private Settings Load(out bool created)
    {
        created = false;

        string text;
        try
        {
            text = ReadWithRetry(FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            created = true;
            return StartFresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            created = true;
            return SetAside($"{ex.GetType().Name}: {ex.Message}");
        }

        Settings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Settings>(text, Json);
        }
        catch (JsonException ex)
        {
            created = true;
            return SetAside($"not valid settings JSON: {ex.Message}");
        }
        if (loaded is null)
        {
            created = true;
            return SetAside("the file holds null rather than settings");
        }

        var settings = Normalise(loaded);
        if (settings.Key.Length == 0)
        {
            created = true;
            settings = settings with { Key = KeyGenerator.New() };
            Log.Write($"[config] {FilePath} had no key — generated a new one ({settings.Key.Length} characters). "
                      + "Ammy needs it in its Key field before it can connect.");
            TryPersist(settings);
        }
        Log.Write($"[config] settings loaded from {FilePath}");
        return settings;
    }

    private Settings StartFresh()
    {
        var fresh = new Settings { Key = KeyGenerator.New() };
        Log.Write($"[config] no settings at {FilePath} — starting fresh");
        Log.Write($"[config] generated a new key ({fresh.Key.Length} characters)");
        TryPersist(fresh);
        return fresh;
    }

    /// <summary>
    /// The file exists and cannot be used. Moved aside rather than deleted or
    /// overwritten: it may still hold the only copy of the key Ammy has, and
    /// someone may want to read it back out by hand.
    /// </summary>
    private Settings SetAside(string reason)
    {
        var fresh = new Settings { Key = KeyGenerator.New() };
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var aside = $"{FilePath}.bad-{stamp}";
        for (var n = 2; File.Exists(aside); n++)
            aside = $"{FilePath}.bad-{stamp}-{n.ToString(CultureInfo.InvariantCulture)}";

        try
        {
            File.Move(FilePath, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't move it, so don't write over it either: whatever is
            // holding it may let go, and the next start can read it after all.
            Log.Write($"[config] WARNING: settings at {FilePath} could not be read ({reason}), and could not be "
                      + $"moved aside either ({ex.Message}). Running this session on a temporary new key "
                      + $"({fresh.Key.Length} characters) and leaving the file untouched. Ammy will not connect "
                      + "until this is fixed — close anything that has the file open and restart Issun.");
            return fresh;
        }

        Log.Write($"[config] WARNING: settings at {FilePath} could not be read ({reason}). Moved it to "
                  + $"{Path.GetFileName(aside)} and started fresh with a new key ({fresh.Key.Length} characters). "
                  + "Ammy will need the new key in its Key field before it can connect again.");
        TryPersist(fresh);
        return fresh;
    }

    /// <summary>Saving during load: failure is logged and survived — this session still has its settings in memory.</summary>
    private void TryPersist(Settings settings)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"[config] WARNING: could not write {FilePath} ({ex.Message}). These settings, the key "
                      + "included, only last until Issun closes.");
        }
    }

    /// <summary>
    /// A hand-edited file gets the same forgiveness relay.py gave .env: nulls
    /// become defaults, the key and client ID lose surrounding whitespace (as
    /// relay.py's .strip() did), the member-list choice is lowercased as
    /// STATUS_LINE was, the public address loses a trailing slash, and an
    /// impossible port becomes the default port. Nothing inside the key is
    /// touched — it has to match Ammy's character for character.
    /// </summary>
    private static Settings Normalise(Settings s)
    {
        var defaults = new Settings();
        var fixedUp = s with
        {
            DiscordClientId = PyText.Strip(s.DiscordClientId ?? defaults.DiscordClientId),
            Key = PyText.Strip(s.Key ?? defaults.Key),
            PublicBase = PyText.Strip(s.PublicBase ?? defaults.PublicBase).TrimEnd('/'),
            StatusLine = s.StatusLine is null ? defaults.StatusLine : PyText.Lower(PyText.Strip(s.StatusLine)),
        };
        if (fixedUp.Port is < 1 or > 65535)
        {
            Log.Write($"[config] port {fixedUp.Port.ToString(CultureInfo.InvariantCulture)} in settings is not a "
                      + $"usable port — using {defaults.Port.ToString(CultureInfo.InvariantCulture)}");
            fixedUp = fixedUp with { Port = defaults.Port };
        }
        return fixedUp;
    }

    /// <summary>A moment's grace for a scanner or sync client that has the file open as Issun starts.</summary>
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException ex) when (attempt < 3 && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
