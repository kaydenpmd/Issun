using System.Globalization;
using System.Text;

namespace Issun.Core.Platform;

/// <summary>
/// Reads the .env that sat beside relay.py and turns it into Issun settings,
/// so switching over keeps Ammy's key — and with it, Ammy keeps working
/// without anyone touching the phone.
///
/// The file is parsed exactly the way relay.py's <c>_load_env_file()</c>
/// parsed it, quirks included, because the point is to import the values the
/// relay was actually running with. Where relay.py read something other than
/// what the file appears to say, the notes say so.
///
/// Secrets never appear in a note or a log line — only their lengths. That is
/// the rule relay-era setup followed by hand ("print .Length rather than the
/// value when checking secrets"), and notes are shown on screen, where a
/// screenshot is one keypress away. The Discord client ID is treated the same
/// way.
/// </summary>
public static class EnvImporter
{
    private const string DefaultUptimeLogName = "ammy-uptime.log";

    private static readonly string[] Known =
    [
        "DISCORD_CLIENT_ID", "RELAY_KEY", "RELAY_SECRET", "RELAY_PORT", "PUBLIC_BASE", "STATUS_LINE",
        "SHOW_ALBUM", "PUBLIC_READ", "ART_MIN_SCORE", "ART_DIR", "UPTIME_LOG",
    ];

    /// <summary>What relay.py's <c>... .strip().lower() in ("1", "true", "yes", "on")</c> accepted as on.</summary>
    private static readonly string[] OnValues = ["1", "true", "yes", "on"];

    /// <summary>Values that are plainly meant as off, so they don't earn an "isn't recognised" remark.</summary>
    private static readonly string[] OffValues = ["", "0", "false", "no", "off"];

    /// <param name="envPath">relay.py's .env.</param>
    /// <param name="current">Issun's settings now. Anything the .env doesn't set is kept.</param>
    /// <param name="uptimeLogTarget">Issun's uptime log — normally <see cref="Paths.UptimeLog"/>.</param>
    public static EnvImportResult Import(string envPath, Settings current, string uptimeLogTarget) =>
        Import(envPath, current, uptimeLogTarget, PersistentEnvironmentVariable);

    /// <param name="persistentEnvironment">
    /// Looks a name up among the user's and the machine's saved environment
    /// variables — the ones a Scheduled Task inherits. Injected so tests don't
    /// depend on this machine's environment.
    /// </param>
    internal static EnvImportResult Import(
        string envPath, Settings current, string uptimeLogTarget, Func<string, string?> persistentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(current);
        var notes = new List<string>();
        var path = Path.GetFullPath(envPath);

        string text;
        try
        {
            text = ReadText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail(current, notes, $"There is no file at {path} — nothing was imported.");
        }
        catch (DecoderFallbackException)
        {
            return Fail(current, notes, $"{path} isn't UTF-8 text, which relay.py required — nothing was imported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(current, notes, $"Couldn't read {path} ({ex.Message}) — nothing was imported.");
        }

        var env = EnvFile.Parse(text);
        var settings = current;
        var imported = 0;

        // ── DISCORD_CLIENT_ID ──
        // relay.py: os.environ.get("DISCORD_CLIENT_ID", "").strip()
        if (env.Values.TryGetValue("DISCORD_CLIENT_ID", out var clientRaw))
        {
            var clientId = PyText.Strip(clientRaw);
            if (clientId.Length == 0)
            {
                notes.Add("DISCORD_CLIENT_ID is empty in the .env — " + KeptClientId(current));
            }
            else
            {
                settings = settings with { DiscordClientId = clientId };
                imported++;
                notes.Add(LooksLikeSnowflake(clientId)
                    ? $"Discord application ID imported ({clientId.Length} digits)."
                    : $"Discord application ID imported ({clientId.Length} characters), but it doesn't look like "
                      + "one — they are 17 to 20 digits. Discord answers a wrong one with \"Client ID is Invalid\".");
            }
        }
        else
        {
            notes.Add("No DISCORD_CLIENT_ID in the .env — " + KeptClientId(current));
        }

        // ── RELAY_KEY, or RELAY_SECRET (its old name) ──
        // relay.py: (os.environ.get("RELAY_KEY") or os.environ.get("RELAY_SECRET") or "").strip()
        // Python's `or` skips an empty string, so an empty RELAY_KEY falls
        // through to RELAY_SECRET rather than winning with nothing.
        env.Values.TryGetValue("RELAY_KEY", out var relayKey);
        env.Values.TryGetValue("RELAY_SECRET", out var relaySecret);
        var fromOldName = string.IsNullOrEmpty(relayKey) && !string.IsNullOrEmpty(relaySecret);
        var key = PyText.Strip(!string.IsNullOrEmpty(relayKey) ? relayKey : relaySecret ?? "");
        if (key.Length > 0)
        {
            settings = settings with { Key = key };
            imported++;
            notes.Add(fromOldName
                ? $"Key imported from RELAY_SECRET, relay.py's old name for it ({key.Length} characters) — your source keeps working without re-pairing."
                : $"Key imported ({key.Length} characters) — your source keeps working without re-pairing.");
            if (!fromOldName && !string.IsNullOrEmpty(relaySecret))
                notes.Add("RELAY_SECRET ignored — RELAY_KEY is set too, and relay.py preferred it.");
        }
        else
        {
            notes.Add((relayKey is null && relaySecret is null ? "No RELAY_KEY in the .env" : "RELAY_KEY is empty in the .env")
                      + " — kept Issun's own key. Your source needs Issun's key.");
        }

        // ── RELAY_PORT ──
        // relay.py: int(os.environ.get("RELAY_PORT", "8787")) — and a bad value
        // stopped it from starting at all.
        if (env.Values.TryGetValue("RELAY_PORT", out var portRaw))
        {
            if (!PyText.TryParseInt(portRaw, out var port))
            {
                notes.Add($"RELAY_PORT={portRaw} isn't a port number — kept {Num(current.Port)}.");
            }
            else if (port is < 1 or > 65535)
            {
                notes.Add($"RELAY_PORT={portRaw} is outside 1–65535 — kept {Num(current.Port)}.");
            }
            else
            {
                settings = settings with { Port = (int)port };
                imported++;
                notes.Add($"Port {Num(port)} imported — Tailscale Funnel has to forward to localhost:{Num(port)}.");
            }
        }

        // ── PUBLIC_BASE ──
        // relay.py: os.environ.get("PUBLIC_BASE", "").rstrip("/") — with no
        // strip(), so PUBLIC_BASE=" https://pc.example-tailnet.ts.net " (spaces
        // inside the quotes survive the loader) became an artwork URL starting
        // with a space. Stripped here.
        if (env.Values.TryGetValue("PUBLIC_BASE", out var baseRaw))
        {
            var publicBase = PyText.Strip(baseRaw).TrimEnd('/');
            settings = settings with { PublicBase = publicBase };
            imported++;
            if (publicBase.Length == 0)
            {
                notes.Add("PUBLIC_BASE is empty — Issun will work the public address out from Tailscale.");
            }
            else
            {
                var looksRight = Uri.TryCreate(publicBase, UriKind.Absolute, out var uri)
                                 && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
                notes.Add(looksRight
                    ? $"Public address imported: {publicBase}. Clear it in Settings to have Issun work it out from Tailscale instead."
                    : $"Public address imported as written: {publicBase} — it doesn't look like an https:// address, "
                      + "so phone-uploaded artwork may not reach Discord.");
            }
        }

        // ── STATUS_LINE, SHOW_ALBUM, ART_MIN_SCORE ──
        // Settings relay.py had and Issun's window no longer offers (removed
        // 23 Sept 2026 at the owner's request): Discord's member list always
        // shows the artist, the album is never a third line, and cover matching
        // uses relay.py's default floor. Importing a value for any of them would
        // set something the person then has no way to see or undo.
        if (env.Values.ContainsKey("STATUS_LINE"))
            notes.Add("STATUS_LINE doesn't apply — Issun always shows the artist in Discord's member list. Ignored.");
        if (env.Values.ContainsKey("SHOW_ALBUM"))
            notes.Add("SHOW_ALBUM doesn't apply — Issun doesn't show the album on the Discord card. Ignored.");
        if (env.Values.ContainsKey("ART_MIN_SCORE"))
            notes.Add("ART_MIN_SCORE doesn't apply — Issun uses a fixed cover-matching threshold. Ignored.");

        // ── PUBLIC_READ ──
        if (env.Values.TryGetValue("PUBLIC_READ", out var readRaw))
        {
            var on = IsOn(readRaw);
            settings = settings with { PublicRead = on };
            imported++;
            notes.Add((on
                          ? "Public now-playing feed imported: on — GET /now-playing answers anyone who has the link, with no key."
                          : "Public now-playing feed imported: off.")
                      + UnrecognisedSwitch("PUBLIC_READ", readRaw));
        }

        // ── Relay-only paths ──
        if (env.Values.ContainsKey("ART_DIR"))
            notes.Add("ART_DIR doesn't apply — Issun keeps uploaded artwork in its own folder. Ignored.");
        if (env.Values.ContainsKey("UPTIME_LOG"))
            notes.Add($"UPTIME_LOG doesn't apply — Issun keeps its uptime log at {Path.GetFullPath(uptimeLogTarget)}. "
                      + "Used only to find relay.py's history.");

        // ── Everything relay.py would have skipped or shadowed ──
        foreach (var name in env.Unknown)
        {
            // "export RELAY_KEY=..." is shell syntax. relay.py took the whole of
            // "export RELAY_KEY" as the name, so the setting never took effect.
            var shellStyle = name.StartsWith("export", StringComparison.OrdinalIgnoreCase) && name.Length > 6
                             && PyText.IsSpace(name[6])
                ? PyText.Strip(name[6..]).ToUpperInvariant()
                : null;
            notes.Add(shellStyle is not null && Known.Contains(shellStyle)
                ? $"\"{name}\" is written shell-style. relay.py read all of it as the name, so {shellStyle} was never "
                  + "set from this line — ignored here too."
                : $"{name} isn't a relay.py setting — ignored.");
        }
        foreach (var name in env.Duplicates)
            notes.Add($"{name} appears more than once — used the first, as relay.py did.");
        foreach (var name in env.CommentedOut.Where(n => !env.Values.ContainsKey(n)))
            notes.Add($"{name} is commented out — ignored, as relay.py ignored it.");
        foreach (var n in env.LinesWithoutEquals)
            notes.Add($"Line {Num(n)} has no '=' and was skipped, as relay.py skipped it.");
        foreach (var n in env.LinesWithoutName)
            notes.Add($"Line {Num(n)} has nothing before its '=' and was skipped, as relay.py skipped it.");

        // ── Real environment variables ──
        // relay.py's loader never overwrote a variable that was already set
        // ("Real environment variables still win"). A variable typed into one
        // PowerShell window reached only a relay started from that window, but
        // one saved for the user or the machine reached the Scheduled Task too —
        // and then the relay ran on that value, not on the file's. Values never
        // appear here, only which names were overridden.
        var savedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Known)
        {
            try
            {
                if (persistentEnvironment(name) is { } value)
                    savedValues[name] = value;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Write($"[config] import: could not check for a saved {name} environment variable: {ex.Message}");
            }
        }
        // RELAY_SECRET only ever mattered when no RELAY_KEY was set anywhere.
        var keyFromEitherPlace = savedValues.TryGetValue("RELAY_KEY", out var savedKey) ? savedKey : relayKey;
        foreach (var name in Known)
        {
            if (!savedValues.TryGetValue(name, out var saved))
                continue;
            if (name == "RELAY_SECRET" && !string.IsNullOrEmpty(keyFromEitherPlace))
                continue;
            var inFile = env.Values.TryGetValue(name, out var fileValue);
            if ((inFile && saved == fileValue) || (!inFile && saved.Length == 0))
                continue;
            var consequence = name is "RELAY_KEY" or "RELAY_SECRET"
                ? "If your source's key is refused, give it Issun's key again."
                : "Check that setting in Issun if anything looks different.";
            notes.Add(inFile
                ? $"{name} is also saved as a Windows environment variable, and relay.py used that instead of the "
                  + $".env's — imported the .env's. {consequence}"
                : $"{name} isn't in the .env, but it is saved as a Windows environment variable, which relay.py "
                  + $"read and Issun doesn't. {consequence}");
        }

        // ── Uptime history ──
        var importedFrom = ImportUptimeHistory(path, env, uptimeLogTarget, notes);

        Log.Write($"[config] imported {Num(imported)} settings from {path}");
        foreach (var note in notes)
            Log.Write($"[config] import: {note}");
        return new EnvImportResult(settings, notes, importedFrom);
    }

    /// <summary>A saved user variable, else a saved machine one — what a logon task's environment is built from.</summary>
    private static string? PersistentEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private static EnvImportResult Fail(Settings current, List<string> notes, string note)
    {
        notes.Add(note);
        Log.Write($"[config] import failed: {note}");
        return new EnvImportResult(current, notes, null);
    }

    /// <summary>
    /// Strict UTF-8, as Python's <c>read_text(encoding="utf-8")</c> was — except
    /// that a byte-order mark is honoured and removed. relay.py kept a UTF-8 BOM
    /// as part of the first line, so a .env written by Windows PowerShell 5's
    /// <c>Set-Content -Encoding UTF8</c> silently lost whatever setting came
    /// first; here it imports. A UTF-16 BOM (PowerShell 5's <c>&gt;</c> and
    /// <c>Out-File</c>) is honoured too, where relay.py refused to start.
    /// </summary>
    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, throwOnInvalidBytes: true),
                                            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? ImportUptimeHistory(string envPath, EnvFile env, string uptimeLogTarget, List<string> notes)
    {
        // The Scheduled Task ran relay.py with its own folder as the working
        // directory, so a relative UPTIME_LOG — the default included — lived
        // beside the .env.
        var configured = env.Values.TryGetValue("UPTIME_LOG", out var custom) && custom.Length > 0;
        var name = configured ? custom! : DefaultUptimeLogName;
        var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(envPath)!, name));
        var target = Path.GetFullPath(uptimeLogTarget);

        if (!File.Exists(source))
        {
            notes.Add(configured
                ? $"No uptime history at {source} — nothing to carry over."
                : $"No uptime history ({DefaultUptimeLogName}) beside the .env — nothing to carry over.");
            return null;
        }
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"The uptime history at {source} is already Issun's own log — nothing to carry over.");
            return null;
        }

        UptimeHistory.Outcome outcome;
        try
        {
            outcome = UptimeHistory.Merge(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Couldn't carry the uptime history over from {source} ({ex.Message}).");
            return null;
        }

        if (outcome.Lossy)
            notes.Add($"{source} isn't entirely valid UTF-8 — unreadable characters were replaced in the copy.");
        if (outcome.SourceLines == 0)
            notes.Add($"The uptime history at {source} is empty — nothing to carry over.");
        else if (outcome.Added == 0)
            notes.Add($"Uptime history from {source} is already in Issun's log — nothing new to add.");
        else if (outcome.Added == outcome.SourceLines)
            notes.Add($"Uptime history carried over from {source}: {Num(outcome.Added)} lines, placed before Issun's own.");
        else
            notes.Add($"Uptime history from {source}: {Num(outcome.Added)} new lines added; the rest were already imported.");
        return source;
    }

    private static string KeptClientId(Settings current) => current.DiscordClientId.Length > 0
        ? "kept the one Issun already has."
        : "Issun still needs one: the Application ID from discord.com/developers.";

    /// <summary>Discord IDs are snowflakes: 17 to 20 decimal digits.</summary>
    private static bool LooksLikeSnowflake(string id) =>
        id.Length is >= 17 and <= 20 && id.All(char.IsAsciiDigit);

    private static bool IsOn(string raw) => OnValues.Contains(PyText.Lower(PyText.Strip(raw)));

    private static string UnrecognisedSwitch(string name, string raw)
    {
        var value = PyText.Lower(PyText.Strip(raw));
        return OnValues.Contains(value) || OffValues.Contains(value)
            ? ""
            : $" ({name}={raw} isn't 1, true, yes or on, so relay.py read it as off.)";
    }

    private static string DescribeStatusLine(string value) => value switch
    {
        "name" => "the app name",
        "details" => "the song title",
        _ => "the artist",
    };

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// relay.py's <c>_load_env_file()</c>, with what it silently skipped kept
    /// alongside so it can be reported.
    /// </summary>
    internal sealed class EnvFile
    {
        /// <summary>
        /// Name → value, first occurrence winning. Names are upper-cased because
        /// relay.py stored them in os.environ, which on Windows is
        /// case-insensitive and upper-cases what it is given: "relay_key=" set
        /// RELAY_KEY, and a later "RELAY_KEY=" was then skipped as already set.
        /// </summary>
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>Names relay.py never read, as written in the file.</summary>
        public List<string> Unknown { get; } = [];

        public List<string> Duplicates { get; } = [];

        /// <summary>Known names that appear only in comments, like the owner's "# SHOW_ALBUM=1".</summary>
        public List<string> CommentedOut { get; } = [];

        public List<int> LinesWithoutEquals { get; } = [];
        public List<int> LinesWithoutName { get; } = [];

        public static EnvFile Parse(string text)
        {
            var env = new EnvFile();
            var lines = PyText.SplitLines(text);
            for (var i = 0; i < lines.Count; i++)
            {
                // for raw in path.read_text(...).splitlines():
                //     line = raw.strip()
                //     if not line or line.startswith("#") or "=" not in line: continue
                //     key, _, value = line.partition("=")
                //     key, value = key.strip(), value.strip().strip('"').strip("'")
                //     if key and key not in os.environ: os.environ[key] = value
                var line = PyText.Strip(lines[i]);
                if (line.Length == 0)
                    continue;
                if (line.StartsWith('#'))
                {
                    env.NoteComment(line);
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    env.LinesWithoutEquals.Add(i + 1);
                    continue;
                }

                var key = PyText.Strip(line[..eq]);
                var value = PyText.Strip(PyText.Strip(PyText.Strip(line[(eq + 1)..]), '"'), '\'');
                if (key.Length == 0)
                {
                    env.LinesWithoutName.Add(i + 1);
                    continue;
                }

                var name = key.ToUpperInvariant();
                if (!env.Values.TryAdd(name, value))
                {
                    if (!env.Duplicates.Contains(name))
                        env.Duplicates.Add(name);
                    continue;
                }
                if (!Known.Contains(name))
                    env.Unknown.Add(key);
            }
            return env;
        }

        private void NoteComment(string line)
        {
            var body = PyText.Strip(line.TrimStart('#'));
            var eq = body.IndexOf('=');
            if (eq <= 0)
                return;
            var name = PyText.Strip(body[..eq]).ToUpperInvariant();
            if (Known.Contains(name) && !CommentedOut.Contains(name))
                CommentedOut.Add(name);
        }
    }
}
