using System.Text;
using Issun.Core.Platform;

namespace Issun.Core.Tests.Platform;

public class EnvImporterTests : IDisposable
{
    // Synthetic values only. Shaped like the real ones — a 19-digit snowflake,
    // a 32-character key — and never taken from them.
    private const string ClientId = "1234567890123456789";
    private const string RelayKey = "AbCdEfGhIjKlMnOpQrStUvWxYz012345";

    private static readonly Settings Current = new() { Key = "IssunOwnKeyIssunOwnKeyIssunOwn99" };

    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private string UptimeTarget => _dir.File(Path.Combine("issun-data", "ammy-uptime.log"));

    private string WriteEnv(string text, string name = ".env")
    {
        var path = _dir.File(name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    /// <summary>Never consults this machine's real environment variables.</summary>
    private EnvImportResult Import(string envPath, Settings? current = null, Func<string, string?>? environment = null) =>
        EnvImporter.Import(envPath, current ?? Current, UptimeTarget, environment ?? (_ => null));

    // ───────────── parity with relay.py's _load_env_file ─────────────
    //
    // Each case was run through relay.py's own _load_env_file() and the module-
    // level assignments that read it (CPython 3.14.7, on Windows, where
    // os.environ upper-cases names). What relay.py ended up running with is
    // asserted here as Issun settings.

    [Fact]
    public void Owner_shaped_file_imports_the_key_from_its_old_name()
    {
        var env = WriteEnv(
            "# Ammy relay settings\r\n"
            + "# written by PowerShell from the live variables\r\n"
            + "\r\n"
            + $"DISCORD_CLIENT_ID={ClientId}\r\n"
            + $"RELAY_SECRET={RelayKey}\r\n"
            + "PUBLIC_BASE=https://mypc.example-tailnet.ts.net/\r\n"
            + "# SHOW_ALBUM=1\r\n"
            + "STATUS_LINE=state\r\n");

        var result = Import(env);

        Assert.Equal(Current with
        {
            DiscordClientId = ClientId,
            Key = RelayKey,
            PublicBase = "https://mypc.example-tailnet.ts.net",
            StatusLine = "state",
        }, result.Settings);
        Assert.False(result.Settings.ShowAlbum);
        Assert.Contains(result.Notes, n => n.StartsWith("Key imported from RELAY_SECRET", StringComparison.Ordinal)
                                           && n.Contains("(32 characters)", StringComparison.Ordinal)
                                           && n.Contains("Ammy keeps working without re-pairing", StringComparison.Ordinal));
        Assert.Contains("SHOW_ALBUM is commented out — ignored, as relay.py ignored it.", result.Notes);
        Assert.Contains("Discord application ID imported (19 digits).", result.Notes);
    }

    [Fact]
    public void Quotes_and_spacing_are_peeled_the_way_relay_py_peeled_them()
    {
        var env = WriteEnv(
            $"  DISCORD_CLIENT_ID = \"{ClientId}\"  \n"
            + "RELAY_KEY='\"quoted-key\"'\n"
            + "RELAY_SECRET=ignored-because-key-is-set\n"
            + "\tSTATUS_LINE\t=\t DETAILS \n"
            + "SHOW_ALBUM=Yes\n"
            + "PUBLIC_READ=On\n"
            + "ART_MIN_SCORE= 0.5 \n"
            + "RELAY_PORT= 9000 \n"
            + "PUBLIC_BASE=\"https://mypc.example-tailnet.ts.net//\"\n");

        var result = Import(env);

        Assert.Equal(new Settings
        {
            DiscordClientId = ClientId,
            Key = "\"quoted-key\"",
            Port = 9000,
            PublicBase = "https://mypc.example-tailnet.ts.net",
            StatusLine = "details",
            ShowAlbum = true,
            PublicRead = true,
            ArtMinScore = 0.5,
        }, result.Settings);
        Assert.Contains("RELAY_SECRET ignored — RELAY_KEY is set too, and relay.py preferred it.", result.Notes);
    }

    [Fact]
    public void The_first_occurrence_wins_and_names_are_case_insensitive()
    {
        var env = WriteEnv(
            "RELAY_KEY=first=with=equals\n"
            + "relay_key=second\n"
            + "RELAY_KEY=third\n"
            + "show_album=1\n"
            + "SHOW_ALBUM=0\n");

        var result = Import(env);

        Assert.Equal("first=with=equals", result.Settings.Key);
        Assert.True(result.Settings.ShowAlbum);
        Assert.Contains("RELAY_KEY appears more than once — used the first, as relay.py did.", result.Notes);
        Assert.Contains("SHOW_ALBUM appears more than once — used the first, as relay.py did.", result.Notes);
    }

    [Fact]
    public void An_empty_relay_key_falls_through_to_relay_secret()
    {
        var env = WriteEnv(
            "RELAY_KEY=\n"
            + "RELAY_SECRET=\"old-name-key\"\n"
            + "SHOW_ALBUM=2\n"
            + "PUBLIC_READ=false\n"
            + "no equals sign on this line\n"
            + "=value-without-key\n"
            + "export RELAY_PORT=1234\n"
            + "UPTIME_LOG=logs/uptime.log\n"
            + "ART_DIR=D:/art\n");

        var result = Import(env);

        Assert.Equal(Current with { Key = "old-name-key" }, result.Settings);
        Assert.Contains(result.Notes, n => n.StartsWith("Key imported from RELAY_SECRET", StringComparison.Ordinal));
        Assert.Contains("Album line imported: off. (SHOW_ALBUM=2 isn't 1, true, yes or on, so relay.py read it as off.)", result.Notes);
        Assert.Contains("Public now-playing feed imported: off.", result.Notes);
        Assert.Contains("Line 5 has no '=' and was skipped, as relay.py skipped it.", result.Notes);
        Assert.Contains("Line 6 has nothing before its '=' and was skipped, as relay.py skipped it.", result.Notes);
        Assert.Contains(result.Notes, n => n.StartsWith("\"export RELAY_PORT\" is written shell-style", StringComparison.Ordinal));
        Assert.Contains("ART_DIR doesn't apply — Issun keeps uploaded artwork in its own folder. Ignored.", result.Notes);
        Assert.Contains(result.Notes, n => n.StartsWith("UPTIME_LOG doesn't apply", StringComparison.Ordinal));
    }

    [Fact]
    public void Python_whitespace_and_line_breaks_are_honoured()
    {
        var env = WriteEnv(
            "\u00a0RELAY_KEY\u00a0=\u2003spaced\u3000\u000c"
            + "STATUS_LINE=Name\u2028"
            + "SHOW_ALBUM=TRUE\u001c"
            + "PUBLIC_READ=\u001f1\u001f\u000b"
            + $"DISCORD_CLIENT_ID=\"'{ClientId}'\"\r"
            + "ART_MIN_SCORE=1_0e-1\u0085"
            + "RELAY_PORT=+8_788\n");

        var result = Import(env);

        Assert.Equal(new Settings
        {
            DiscordClientId = ClientId,
            Key = "spaced",
            Port = 8788,
            StatusLine = "name",
            ShowAlbum = true,
            PublicRead = true,
            ArtMinScore = 1.0,
        }, result.Settings);
    }

    // ───────────── where Issun deliberately differs ─────────────

    [Fact]
    public void A_utf8_bom_does_not_swallow_the_first_setting()
    {
        // relay.py kept U+FEFF as part of the first name, so this file's client
        // ID never reached it.
        var path = _dir.File(".env");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($"DISCORD_CLIENT_ID={ClientId}\r\nRELAY_KEY={RelayKey}\r\n")]);

        var result = Import(path);

        Assert.Equal(ClientId, result.Settings.DiscordClientId);
        Assert.Equal(RelayKey, result.Settings.Key);
    }

    [Fact]
    public void A_utf16_file_from_powershell_5_imports()
    {
        var path = _dir.File(".env");
        File.WriteAllText(path, $"RELAY_KEY={RelayKey}\r\n", Encoding.Unicode);

        Assert.Equal(RelayKey, Import(path).Settings.Key);
    }

    [Fact]
    public void Public_base_loses_surrounding_spaces_as_well_as_the_slash()
    {
        var env = WriteEnv("PUBLIC_BASE=\" https://mypc.example-tailnet.ts.net/ \"\n");
        Assert.Equal("https://mypc.example-tailnet.ts.net", Import(env).Settings.PublicBase);
    }

    [Fact]
    public void An_unknown_status_line_imports_as_name_which_is_what_discord_showed()
    {
        // relay.py sent no status_display_type for an unrecognised value, and
        // Discord's default for that is the application name.
        var result = Import(WriteEnv("STATUS_LINE=artist\n"));

        Assert.Equal("name", result.Settings.StatusLine);
        Assert.Contains(result.Notes, n => n.StartsWith("STATUS_LINE=artist isn't name, state or details", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RELAY_PORT=http\n", "RELAY_PORT=http isn't a port number — kept 8787.")]
    [InlineData("RELAY_PORT=70000\n", "RELAY_PORT=70000 is outside 1–65535 — kept 8787.")]
    [InlineData("RELAY_PORT=0\n", "RELAY_PORT=0 is outside 1–65535 — kept 8787.")]
    [InlineData("ART_MIN_SCORE=0,5\n", "ART_MIN_SCORE=0,5 isn't a number — kept 0.35.")]
    [InlineData("ART_MIN_SCORE=nan\n", "ART_MIN_SCORE=nan isn't a usable number — kept 0.35.")]
    [InlineData("ART_MIN_SCORE=1e400\n", "ART_MIN_SCORE=1e400 isn't a usable number — kept 0.35.")]
    public void Values_relay_py_would_have_crashed_or_misbehaved_on_are_kept_as_they_were(string line, string note)
    {
        var result = Import(WriteEnv(line));

        Assert.Equal(Current, result.Settings);
        Assert.Contains(note, result.Notes);
    }

    [Fact]
    public void Art_min_score_is_read_in_the_invariant_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var result = Import(WriteEnv("ART_MIN_SCORE=0.45\n"));
            Assert.Equal(0.45, result.Settings.ArtMinScore);
            Assert.Contains("Artwork match floor imported: 0.45.", result.Notes);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    // ───────────── secrets ─────────────

    [Fact]
    public void Secrets_never_appear_in_notes_or_the_log()
    {
        var env = WriteEnv(
            $"DISCORD_CLIENT_ID={ClientId}\nRELAY_KEY={RelayKey}\nRELAY_SECRET=OldSecretValueOldSecretValue1234\n"
            + $"RELAY_KEY={RelayKey}-dupe\nBOGUS={RelayKey}\n");

        using var log = Log.Capture();
        var result = Import(env, environment: name => name == "RELAY_KEY" ? "SavedEnvKeySavedEnvKeySavedEnv12" : null);

        Assert.Equal(RelayKey, result.Settings.Key);
        Assert.Contains("Key imported (32 characters) — Ammy keeps working without re-pairing.", result.Notes);
        var everything = string.Join("\n", result.Notes.Concat(log.Lines));
        foreach (var secret in new[] { ClientId, RelayKey, "OldSecretValueOldSecretValue1234", "SavedEnvKeySavedEnvKeySavedEnv12" })
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);

        Assert.Contains(log.Lines, l => l.StartsWith("[config] imported ", StringComparison.Ordinal));
        Assert.All(result.Notes, n => Assert.Contains($"[config] import: {n}", log.Lines));
    }

    [Fact]
    public void Saved_environment_variables_that_relay_py_preferred_are_pointed_out()
    {
        var env = WriteEnv($"RELAY_KEY={RelayKey}\nSTATUS_LINE=state\n");
        var saved = new Dictionary<string, string>
        {
            ["RELAY_KEY"] = "SavedEnvKeySavedEnvKeySavedEnv12",
            ["STATUS_LINE"] = "state",          // same value: nothing to say
            ["PUBLIC_READ"] = "1",              // only in the environment
            ["RELAY_SECRET"] = "irrelevant",    // RELAY_KEY is set, so relay.py never looked
        };

        var result = Import(env, environment: n => saved.GetValueOrDefault(n));

        Assert.Equal(RelayKey, result.Settings.Key);
        Assert.Contains(result.Notes, n => n.StartsWith("RELAY_KEY is also saved as a Windows environment variable", StringComparison.Ordinal)
                                           && n.Contains("pair it again with Issun's key", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("PUBLIC_READ isn't in the .env, but it is saved as a Windows environment variable", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("STATUS_LINE is also saved", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("RELAY_SECRET", StringComparison.Ordinal));
    }

    // ───────────── files that aren't there or can't be read ─────────────

    [Fact]
    public void A_missing_env_changes_nothing_and_says_so()
    {
        using var log = Log.Capture();
        var result = Import(_dir.File("nope.env"));

        Assert.Same(Current, result.Settings);
        Assert.Null(result.UptimeLogImportedFrom);
        var note = Assert.Single(result.Notes);
        Assert.StartsWith("There is no file at ", note);
        Assert.Contains($"[config] import failed: {note}", log.Lines);
    }

    [Fact]
    public void Invalid_utf8_is_refused_like_relay_py_refused_it()
    {
        var path = _dir.File(".env");
        File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("RELAY_KEY=caf"), 0xE9, (byte)'\n']);

        var result = Import(path);

        Assert.Same(Current, result.Settings);
        Assert.Contains(result.Notes, n => n.Contains("isn't UTF-8 text", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_the_env_does_not_mention_are_kept()
    {
        var current = Current with { DiscordClientId = "999999999999999999", Port = 9100, ShowAlbum = true, ArtMinScore = 0.2 };
        var result = Import(WriteEnv($"RELAY_KEY={RelayKey}\n"), current);

        Assert.Equal(current with { Key = RelayKey }, result.Settings);
        Assert.Contains("No DISCORD_CLIENT_ID in the .env — kept the one Issun already has.", result.Notes);
    }

    [Fact]
    public void A_file_with_no_key_keeps_issuns_and_says_ammy_needs_it()
    {
        var result = Import(WriteEnv("# nothing but a comment\n"));

        Assert.Equal(Current, result.Settings);
        Assert.Contains("No RELAY_KEY in the .env — kept Issun's own key. Ammy needs Issun's key in its Key field.", result.Notes);
    }

    // ───────────── uptime history ─────────────

    private const string RelayLine1 = "2026-09-14T14:21:02  relay started  [relay 1.8.0 / app unknown]";
    private const string RelayGap = "2026-09-14T14:59:40  gap 00:38:12  phone silent, relay up throughout  [relay 1.8.0 / app 1.0 (51)]";
    private const string RelayLine3 = "2026-09-18T09:00:00  relay started  [relay 1.9.0 / app 1.0 (80)]";
    private const string IssunLine1 = "2026-09-21T10:00:00  relay started  [issun 0.1.0 (dev) / app unknown]";
    private const string IssunLine2 = "2026-09-21T10:05:00  gap 00:01:40  phone silent  [issun 0.1.0 (dev) / app 1.0 (83)]";

    private string WriteRelayHistory(string folder, params string[] lines)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ammy-uptime.log");
        // What Python's text mode wrote on Windows: UTF-8, no BOM, CRLF.
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\r\n")), new UTF8Encoding(false));
        return path;
    }

    private string[] TargetLines() => File.ReadAllLines(UptimeTarget);

    [Fact]
    public void Relay_history_goes_first_and_issuns_own_lines_after()
    {
        var source = WriteRelayHistory(_dir.Path, RelayLine1, RelayGap, RelayGap, RelayLine3);
        Directory.CreateDirectory(Path.GetDirectoryName(UptimeTarget)!);
        File.WriteAllLines(UptimeTarget, [IssunLine1, IssunLine2]);
        var env = WriteEnv($"RELAY_KEY={RelayKey}\n");

        var result = Import(env);

        Assert.Equal(source, result.UptimeLogImportedFrom);
        // The 14 Sept gap was written twice by relay.py; both copies are history.
        Assert.Equal([RelayLine1, RelayGap, RelayGap, RelayLine3, IssunLine1, IssunLine2], TargetLines());
        Assert.Contains($"Uptime history carried over from {source}: 4 lines, placed before Issun's own.", result.Notes);
    }

    [Fact]
    public void Importing_twice_adds_nothing_and_never_touches_the_source()
    {
        var source = WriteRelayHistory(_dir.Path, RelayLine1, RelayGap, RelayGap);
        var sourceBytes = File.ReadAllBytes(source);
        var sourceWritten = File.GetLastWriteTimeUtc(source);
        var env = WriteEnv($"RELAY_KEY={RelayKey}\n");

        Import(env);
        File.AppendAllText(UptimeTarget, IssunLine1 + Environment.NewLine);
        var second = Import(env);

        Assert.Equal([RelayLine1, RelayGap, RelayGap, IssunLine1], TargetLines());
        Assert.Contains($"Uptime history from {source} is already in Issun's log — nothing new to add.", second.Notes);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Equal(sourceWritten, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void Importing_again_after_relay_py_ran_longer_adds_only_the_new_lines()
    {
        var source = WriteRelayHistory(_dir.Path, RelayLine1, RelayGap);
        var env = WriteEnv($"RELAY_KEY={RelayKey}\n");
        Import(env);
        File.AppendAllText(UptimeTarget, IssunLine1 + Environment.NewLine);

        WriteRelayHistory(_dir.Path, RelayLine1, RelayGap, RelayLine3);
        var again = Import(env);

        Assert.Equal([RelayLine1, RelayGap, RelayLine3, IssunLine1], TargetLines());
        Assert.Contains($"Uptime history from {source}: 1 new lines added; the rest were already imported.", again.Notes);
    }

    [Fact]
    public void A_relative_uptime_log_setting_is_resolved_beside_the_env()
    {
        var source = WriteRelayHistory(Path.Combine(_dir.Path, "logs"), RelayLine1);
        var env = WriteEnv("UPTIME_LOG=logs/ammy-uptime.log\n");
        // The default name beside the .env must not be picked instead.
        WriteRelayHistory(_dir.Path, RelayLine3);

        var result = Import(env);

        Assert.Equal(source, result.UptimeLogImportedFrom);
        Assert.Equal([RelayLine1], TargetLines());
    }

    [Fact]
    public void No_history_beside_the_env_is_a_note_not_a_failure()
    {
        var result = Import(WriteEnv($"RELAY_KEY={RelayKey}\n"));

        Assert.Null(result.UptimeLogImportedFrom);
        Assert.False(File.Exists(UptimeTarget));
        Assert.Contains("No uptime history (ammy-uptime.log) beside the .env — nothing to carry over.", result.Notes);
    }

    [Fact]
    public void Issuns_own_log_is_not_imported_into_itself()
    {
        var env = Path.Combine(Path.GetDirectoryName(UptimeTarget)!, ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, $"RELAY_KEY={RelayKey}\n");
        File.WriteAllLines(UptimeTarget, [IssunLine1]);

        var result = Import(env);

        Assert.Null(result.UptimeLogImportedFrom);
        Assert.Equal([IssunLine1], TargetLines());
    }
}
