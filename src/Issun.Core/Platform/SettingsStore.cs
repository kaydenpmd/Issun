using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
/// that happens the log says so in words that point at Ammy's Key field. A
/// value of the wrong type elsewhere in the file costs only that value (see
/// <see cref="FromFields"/>).
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
        // The resolver the serializer would pick anyway, named so the loader
        // can ask which fields Settings has before anything is serialized.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>The same forgiveness for the first pass, which only splits the file into its fields.</summary>
    private static readonly JsonDocumentOptions Document = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
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
    /// settings existed, the file was unreadable and had to be set aside, it
    /// held no key, or its key was something no key can be (true, a list) and
    /// the file was set aside for it. All of them mean the same thing to the
    /// person — pair the source with the new key — so all report true. (An
    /// empty key refuses every request, so a file without one was never paired
    /// with anything.)
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

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Document);
        }
        catch (JsonException ex)
        {
            created = true;
            return SetAside($"not valid settings JSON: {ex.Message}");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                created = true;
                return SetAside($"the file holds {Kind(document.RootElement)} rather than settings");
            }
            return FromFields(document.RootElement, out created);
        }
    }

    /// <summary>
    /// Reads each setting on its own, so a hand edit that leaves one value of
    /// the wrong type — a port typed as "87o7", "yes" for true — costs that
    /// value alone: it falls back to its default, a WARNING names it, and the
    /// key and every other setting load as written. Until 16 Nov 2026 one such
    /// value made the whole file unreadable, and setting it aside cost the key
    /// with it. The file is left as the person wrote it, so the warning comes
    /// back every start until the value is corrected or a save writes the
    /// default over it.
    ///
    /// What counts as readable is exactly what it always was: each field is
    /// tried through the same serializer and options, alone in an object of
    /// its own, so a quoted port or a comment is still fine and a new setting
    /// added to <see cref="Settings"/> is covered with no change here.
    /// </summary>
    private Settings FromFields(JsonElement root, out bool created)
    {
        created = false;

        // Only fields that name a setting matter: the serializer ignores the
        // rest, so they're dropped before anything in them is decoded. A name
        // given twice counts once, the later one, as the serializer reads it,
        // and a null is that setting's default, whatever its type.
        var fields = new List<(string Name, JsonProperty Field)>();
        foreach (var field in root.EnumerateObject())
        {
            if (NameOf(field) is not { } name || Setting(name) is null)
                continue;
            fields.RemoveAll(f => SameName(f.Name, name));
            if (field.Value.ValueKind != JsonValueKind.Null)
                fields.Add((name, field));
        }

        var readable = new List<JsonProperty>();
        var unreadable = new List<(string Name, JsonElement Value)>();
        JsonElement? badKey = null;
        foreach (var (name, field) in fields)
        {
            if (Readable(field))
                readable.Add(field);
            else if (SameName(name, nameof(Settings.Key)))
                badKey = field.Value;
            else
                unreadable.Add((name, field.Value));
        }

        Settings loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Settings>(ObjectOf(readable), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Every field read alone, so this isn't expected; the old answer
            // to an unreadable file is still the safe one.
            created = true;
            return SetAside($"not valid settings JSON: {ex.Message}");
        }

        // A key typed without quotes is a number to JSON. Its digits are the
        // only key it can mean, and the source has them, so they are kept.
        var keyAsNumber = false;
        if (badKey is { ValueKind: JsonValueKind.Number } number)
        {
            keyAsNumber = true;
            loaded = loaded with { Key = number.GetRawText() };
        }

        var settings = Normalise(loaded);
        var keyKept = (badKey is null || keyAsNumber) && settings.Key.Length > 0;
        foreach (var (name, value) in unreadable)
        {
            Log.Write($"[config] WARNING: \"{name}\" in {FilePath} is {Problem(name, value)} — using "
                      + $"{DefaultOf(name)} instead. "
                      + (keyKept ? "The key and the file's other settings are kept." : "The file's other settings are kept."));
        }
        if (keyAsNumber)
        {
            Log.Write($"[config] WARNING: the key in {FilePath} is a number, without quotes — using it as written "
                      + $"({settings.Key.Length} characters). Put it in quotes to stop this warning.");
        }

        if (badKey is { } key && !keyAsNumber)
        {
            created = true;
            return SetAside(key.ValueKind == JsonValueKind.String ? "it is text that can't be decoded" : $"it is {Kind(key)}, not text",
                            keep: settings);
        }

        if (settings.Key.Length == 0)
        {
            created = true;
            settings = settings with { Key = KeyGenerator.New() };
            Log.Write($"[config] {FilePath} had no key — generated a new one ({settings.Key.Length} characters). "
                      + "Your source needs it before it can connect.");
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
    /// The file exists and its key cannot be used: the file can't be read at
    /// all, or (given <paramref name="keep"/>, the rest of it as read) its key
    /// is something no key can be. Moved aside rather than deleted or
    /// overwritten: it may still hold the only copy of the key Ammy has, and
    /// someone may want to read it back out by hand.
    /// </summary>
    private Settings SetAside(string reason, Settings? keep = null)
    {
        var fresh = (keep ?? new Settings()) with { Key = KeyGenerator.New() };
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
            Log.Write(keep is null
                ? $"[config] WARNING: settings at {FilePath} could not be read ({reason}), and could not be "
                  + $"moved aside either ({ex.Message}). Running this session on a temporary new key "
                  + $"({fresh.Key.Length} characters) and leaving the file untouched. Your source will not connect "
                  + "until this is fixed — close anything that has the file open and restart Issun."
                : $"[config] WARNING: the key in {FilePath} can't be used ({reason}), and the file could not be "
                  + $"moved aside ({ex.Message}). Running this session on its other settings and a temporary new key "
                  + $"({fresh.Key.Length} characters), leaving the file untouched. Your source will not connect "
                  + "until this is fixed — close anything that has the file open and restart Issun.");
            return fresh;
        }

        Log.Write(keep is null
            ? $"[config] WARNING: settings at {FilePath} could not be read ({reason}). Moved it to "
              + $"{Path.GetFileName(aside)} and started fresh with a new key ({fresh.Key.Length} characters). "
              + "Your source will need the new key before it can connect again."
            : $"[config] WARNING: the key in {FilePath} can't be used ({reason}). Moved the file to "
              + $"{Path.GetFileName(aside)} and kept its other settings, with a new key ({fresh.Key.Length} characters). "
              + "Your source will need the new key before it can connect again.");
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
    /// become defaults, the key loses surrounding whitespace (as
    /// relay.py's .strip() did), the member-list choice is lowercased as
    /// STATUS_LINE was, and an impossible port becomes the default port. Nothing inside the key is
    /// touched — it has to match Ammy's character for character.
    /// </summary>
    private static Settings Normalise(Settings s)
    {
        var defaults = new Settings();
        var fixedUp = s with
        {
            Key = PyText.Strip(s.Key ?? defaults.Key),
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

    /// <summary>
    /// Whether this field, alone, reads as the setting it names. Text with a
    /// lone surrogate escape ("\uD800") is valid JSON that can't be decoded,
    /// and fails here as it does in the serializer.
    /// </summary>
    private static bool Readable(JsonProperty field)
    {
        try
        {
            JsonSerializer.Deserialize<Settings>(ObjectOf([field]), Json);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A field's name, or null when it can't be decoded (so it names no setting).</summary>
    private static string? NameOf(JsonProperty field)
    {
        try
        {
            return field.Name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What's wrong with a field that didn't read: what it holds, against what it should.</summary>
    private static string Problem(string name, JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Setting(name)?.PropertyType == typeof(string)
            ? $"{Shown(value)}, text that can't be decoded"
            : $"{Shown(value)}, not {Expected(name)}";

    /// <summary>Fields as a JSON object of their own, in UTF-8.</summary>
    private static byte[] ObjectOf(IEnumerable<JsonProperty> fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in fields)
                field.WriteTo(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Names match the way the serializer matches them, ignoring case.</summary>
    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The serializer's own view of the setting a field names; null for a name it ignores.</summary>
    private static JsonPropertyInfo? Setting(string name) =>
        Json.GetTypeInfo(typeof(Settings)).Properties.FirstOrDefault(p => SameName(p.Name, name));

    /// <summary>What a setting has to be, in the words a hand edit needs.</summary>
    private static string Expected(string name)
    {
        var type = Setting(name)?.PropertyType;
        if (type == typeof(int))
            return "a whole number";
        if (type == typeof(double))
            return "a number";
        if (type == typeof(bool))
            return "true or false";
        if (type == typeof(string))
            return "text in quotes";
        return "a value Issun can read";
    }

    /// <summary>A setting's default as the file would hold it: 8787, false, "state".</summary>
    private static string DefaultOf(string name) =>
        Setting(name) is { Get: { } get } setting
            ? JsonSerializer.Serialize(get(new Settings()), setting.PropertyType, Json)
            : "its default";

    /// <summary>What a JSON value is, in words — never what it says, since it may be the key.</summary>
    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    /// <summary>
    /// A value as the file has it, for a WARNING about a setting other than
    /// the key: long text cut short, an object or a list named rather than printed.
    /// </summary>
    private static string Shown(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return Kind(value);
        var raw = value.GetRawText();
        return raw.Length <= 40 ? raw : string.Concat(raw.AsSpan(0, 39), "…");
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
