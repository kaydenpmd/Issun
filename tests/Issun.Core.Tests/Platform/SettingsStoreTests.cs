using System.Text.Json;
using System.Text.RegularExpressions;
using Issun.Core.Platform;

namespace Issun.Core.Tests.Platform;

public class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();

    private string SettingsPath => _dir.File("settings.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void First_run_generates_a_key_saves_it_and_never_logs_it()
    {
        using var log = Log.Capture();
        var store = new SettingsStore(SettingsPath);

        Assert.True(store.CreatedOnThisRun);
        Assert.Equal(32, store.Current.Key.Length);
        Assert.Matches("^[A-Za-z0-9]{32}$", store.Current.Key);
        Assert.Equal(new Settings { Key = store.Current.Key }, store.Current);
        Assert.True(File.Exists(SettingsPath));

        Assert.Contains("[config] generated a new key (32 characters)", log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(store.Current.Key, StringComparison.Ordinal));
        Assert.DoesNotContain(store.Current.Key, string.Join("\n", Log.Recent().Select(e => e.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_run_keeps_the_key()
    {
        var first = new SettingsStore(SettingsPath);
        var second = new SettingsStore(SettingsPath);

        Assert.False(second.CreatedOnThisRun);
        Assert.Equal(first.Current, second.Current);
    }

    [Fact]
    public void Save_round_trips_every_field_and_raises_changed()
    {
        var store = new SettingsStore(SettingsPath);
        var updated = new Settings
        {
            Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Port = 9123,
            StatusLine = "details",
            ShowAlbum = true,
            PublicRead = true,
            ArtMinScore = 0.5,
        };
        Settings? raised = null;
        store.Changed += s => raised = s;

        store.Save(updated);

        Assert.Equal(updated, store.Current);
        Assert.Equal(updated, raised);
        Assert.Equal(updated, new SettingsStore(SettingsPath).Current);
    }

    [Fact]
    public void Save_leaves_no_temporary_files_behind()
    {
        var store = new SettingsStore(SettingsPath);
        for (var i = 0; i < 5; i++)
            store.Save(store.Current with { Port = 9000 + i });

        Assert.Equal(["settings.json"], Directory.GetFiles(_dir.Path).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public void Numbers_are_written_in_the_invariant_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var store = new SettingsStore(SettingsPath);
            store.Save(store.Current with { ArtMinScore = 0.35 });

            Assert.Contains("0.35", File.ReadAllText(SettingsPath));
            Assert.Equal(0.35, new SettingsStore(SettingsPath).Current.ArtMinScore);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void A_broken_listener_does_not_fail_the_save()
    {
        var store = new SettingsStore(SettingsPath);
        var reached = false;
        store.Changed += _ => throw new InvalidOperationException("listener bug");
        store.Changed += _ => reached = true;

        using var log = Log.Capture();
        store.Save(store.Current with { Port = 9001 });

        Assert.True(reached);
        Assert.Equal(9001, new SettingsStore(SettingsPath).Current.Port);
        Assert.Contains(log.Lines, l => l.StartsWith("[config] a settings listener failed: InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public void Concurrent_saves_leave_a_whole_file_that_matches_current()
    {
        var store = new SettingsStore(SettingsPath);
        Parallel.For(0, 40, i => store.Save(store.Current with { Port = 10000 + i }));

        var onDisk = new SettingsStore(SettingsPath).Current;
        Assert.Equal(store.Current, onDisk);
        Assert.InRange(onDisk.Port, 10000, 10039);
    }

    [Fact]
    public void An_empty_key_is_replaced_and_the_new_one_saved()
    {
        File.WriteAllText(SettingsPath, """{ "discordClientId": "123456789012345678", "key": "", "port": 9000 }""");
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.True(store.CreatedOnThisRun);
        Assert.Equal(32, store.Current.Key.Length);
        Assert.Equal(9000, store.Current.Port);
        Assert.Equal(store.Current.Key, new SettingsStore(SettingsPath).Current.Key);
        Assert.Contains(log.Lines, l => l.StartsWith("[config] ", StringComparison.Ordinal) && l.Contains("had no key", StringComparison.Ordinal)
                                        && l.Contains("(32 characters)", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains(store.Current.Key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just text\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void A_corrupt_file_is_moved_aside_not_overwritten(string contents)
    {
        File.WriteAllText(SettingsPath, contents);
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.True(store.CreatedOnThisRun);
        Assert.Equal(32, store.Current.Key.Length);

        var aside = Directory.GetFiles(_dir.Path, "settings.json.bad-*").Single();
        Assert.Matches(new Regex(@"settings\.json\.bad-\d{14}$"), aside);
        Assert.Equal(contents, File.ReadAllText(aside));
        Assert.Equal(store.Current.Key, new SettingsStore(SettingsPath).Current.Key);

        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(aside), warning);
        Assert.Contains("Your source will need the new key", warning);
        Assert.DoesNotContain(store.Current.Key, warning);
    }

    [Fact]
    public void A_second_bad_file_in_the_same_second_does_not_overwrite_the_first()
    {
        File.WriteAllText(SettingsPath, "first bad");
        _ = new SettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath, "second bad");
        _ = new SettingsStore(SettingsPath);

        var contents = Directory.GetFiles(_dir.Path, "settings.json.bad-*").Select(File.ReadAllText).Order().ToArray();
        Assert.Equal(["first bad", "second bad"], contents);
    }

    // ─── A value of the wrong type (the 23 Sept 2026 review's "type slip") ───

    private const string PairedKey = "abcdefghijklmnopqrstuvwxyz012345";

    /// <summary>What a file a source was paired with reads as: every setting changed from its default.</summary>
    private static readonly Settings Paired = new()
    {
        Key = PairedKey,
        Port = 9005,
        StatusLine = "details",
        ShowAlbum = true,
        PublicRead = true,
        ArtMinScore = 0.5,
    };

    /// <summary>That file, as JSON, with one field's value replaced.</summary>
    private static string PairedFile(string field, string json)
    {
        var fields = new Dictionary<string, string>
        {
            ["key"] = $"\"{PairedKey}\"",
            ["port"] = "9005",
            ["statusLine"] = "\"details\"",
            ["showAlbum"] = "true",
            ["publicRead"] = "true",
            ["artMinScore"] = "0.5",
        };
        fields[field] = json;
        return "{\n" + string.Join(",\n", fields.Select(f => $"  \"{f.Key}\": {f.Value}")) + "\n}\n";
    }

    [Theory]
    [InlineData("port", "\"not a number\"")]
    [InlineData("port", "\"87o7\"")]
    [InlineData("port", "8787.5")]
    [InlineData("port", "99999999999")]
    [InlineData("port", "true")]
    [InlineData("port", "{ \"value\": 9005 }")]
    [InlineData("port", "[9005]")]
    [InlineData("statusLine", "1")]
    [InlineData("statusLine", "[\"details\"]")]
    [InlineData("showAlbum", "\"yes\"")]
    [InlineData("showAlbum", "1")]
    [InlineData("publicRead", "\"true\"")]
    [InlineData("artMinScore", "\"high\"")]
    [InlineData("artMinScore", "false")]
    public void A_value_of_the_wrong_type_costs_only_itself(string field, string json)
    {
        var contents = PairedFile(field, json);
        File.WriteAllText(SettingsPath, contents);
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        // The key, so the pairing, survives, and so does every other setting.
        var defaults = new Settings();
        var expected = field switch
        {
            "port" => Paired with { Port = defaults.Port },
            "statusLine" => Paired with { StatusLine = defaults.StatusLine },
            "showAlbum" => Paired with { ShowAlbum = defaults.ShowAlbum },
            "publicRead" => Paired with { PublicRead = defaults.PublicRead },
            "artMinScore" => Paired with { ArtMinScore = defaults.ArtMinScore },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.Equal(expected, store.Current);
        Assert.False(store.CreatedOnThisRun);

        // Nothing moved aside or rewritten: the file is the person's to correct.
        Assert.Empty(Directory.GetFiles(_dir.Path, "settings.json.bad-*"));
        Assert.Equal(contents, File.ReadAllText(SettingsPath));

        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.StartsWith($"[config] WARNING: \"{field}\" in {SettingsPath} is ", warning);
        Assert.EndsWith("The key and the file's other settings are kept.", warning);
        Assert.Contains("[config] settings loaded from " + SettingsPath, log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(PairedKey, StringComparison.Ordinal));
    }

    [Fact]
    public void The_warning_says_what_was_there_what_belongs_there_and_what_is_used()
    {
        File.WriteAllText(SettingsPath, $$"""{ "key": "{{PairedKey}}", "port": "not a number", "showAlbum": "yes", "statusLine": 1, "artMinScore": [1] }""");
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.Equal(new Settings { Key = PairedKey }, store.Current);
        var warnings = log.Lines.Where(l => l.StartsWith("[config] WARNING", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            [
                $"[config] WARNING: \"port\" in {SettingsPath} is \"not a number\", not a whole number — using 8787 instead. The key and the file's other settings are kept.",
                $"[config] WARNING: \"showAlbum\" in {SettingsPath} is \"yes\", not true or false — using false instead. The key and the file's other settings are kept.",
                $"[config] WARNING: \"statusLine\" in {SettingsPath} is 1, not text in quotes — using \"state\" instead. The key and the file's other settings are kept.",
                $"[config] WARNING: \"artMinScore\" in {SettingsPath} is a list, not a number — using 0.35 instead. The key and the file's other settings are kept.",
            ],
            warnings);
    }

    [Fact]
    public void Long_text_in_a_warning_is_cut_short()
    {
        var essay = new string('x', 500);
        File.WriteAllText(SettingsPath, $$"""{ "key": "{{PairedKey}}", "port": "{{essay}}" }""");
        using var log = Log.Capture();

        _ = new SettingsStore(SettingsPath);

        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.Contains($" is \"{new string('x', 38)}…, not a whole number", warning);
    }

    [Fact]
    public void A_null_is_the_default_for_every_setting_without_a_warning()
    {
        File.WriteAllText(SettingsPath, $$"""
            { "key": "{{PairedKey}}", "port": null, "statusLine": null, "showAlbum": null, "publicRead": null, "artMinScore": null }
            """);
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.Equal(new Settings { Key = PairedKey }, store.Current);
        Assert.False(store.CreatedOnThisRun);
        Assert.DoesNotContain(log.Lines, l => l.Contains("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void A_setting_given_twice_counts_once_the_later_one()
    {
        File.WriteAllText(SettingsPath, $$"""
            { "key": "{{PairedKey}}", "port": "x", "Port": 9005, "showAlbum": true, "SHOWALBUM": "yes" }
            """);
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        // The bad port was overruled by a good one; the bad switch overruled a good one.
        Assert.Equal(new Settings { Key = PairedKey, Port = 9005 }, store.Current);
        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.StartsWith("[config] WARNING: \"SHOWALBUM\" in ", warning);
    }

    [Fact]
    public void The_next_save_writes_the_default_over_a_bad_value()
    {
        File.WriteAllText(SettingsPath, $$"""{ "key": "{{PairedKey}}", "port": "87o7" }""");
        var store = new SettingsStore(SettingsPath);

        store.Save(store.Current);
        using var log = Log.Capture();
        var again = new SettingsStore(SettingsPath);

        Assert.Equal(new Settings { Key = PairedKey }, again.Current);
        Assert.DoesNotContain(log.Lines, l => l.Contains("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void A_type_slip_in_a_file_with_no_key_keeps_the_rest_and_adds_a_key()
    {
        File.WriteAllText(SettingsPath, """{ "port": "not a number", "publicRead": true }""");
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.True(store.CreatedOnThisRun);
        Assert.Matches("^[A-Za-z0-9]{32}$", store.Current.Key);
        Assert.Equal(new Settings { Key = store.Current.Key, PublicRead = true }, store.Current);
        Assert.Equal(store.Current, new SettingsStore(SettingsPath).Current);
        Assert.Empty(Directory.GetFiles(_dir.Path, "settings.json.bad-*"));

        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.EndsWith("The file's other settings are kept.", warning);
        Assert.Contains(log.Lines, l => l.Contains("had no key", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_written_as_a_number_is_used_as_written()
    {
        const string digits = "12345678901234567890123456789012";
        var contents = PairedFile("key", digits);
        File.WriteAllText(SettingsPath, contents);
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.False(store.CreatedOnThisRun);
        Assert.Equal(Paired with { Key = digits }, store.Current);
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir.Path, "settings.json.bad-*"));

        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.Equal($"[config] WARNING: the key in {SettingsPath} is a number, without quotes — using it as written "
                     + "(32 characters). Put it in quotes to stop this warning.", warning);
        Assert.DoesNotContain(log.Lines, l => l.Contains(digits, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{ \"value\": \"abcdefghijklmnopqrstuvwxyz012345\" }")]
    [InlineData("[\"abcdefghijklmnopqrstuvwxyz012345\"]")]
    public void A_key_that_cannot_be_a_key_is_replaced_keeping_the_rest_with_the_file_set_aside(string json)
    {
        var contents = PairedFile("key", json);
        File.WriteAllText(SettingsPath, contents);
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.True(store.CreatedOnThisRun);
        Assert.Matches("^[A-Za-z0-9]{32}$", store.Current.Key);
        Assert.Equal(Paired with { Key = store.Current.Key }, store.Current);

        // The file as it was, which may hold the old key in some form, is kept whole.
        var aside = Directory.GetFiles(_dir.Path, "settings.json.bad-*").Single();
        Assert.Equal(contents, File.ReadAllText(aside));

        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.StartsWith($"[config] WARNING: the key in {SettingsPath} can't be used (it is ", warning);
        Assert.Contains($"Moved the file to {Path.GetFileName(aside)} and kept its other settings", warning);
        Assert.EndsWith("Your source will need the new key before it can connect again.", warning);
        Assert.DoesNotContain(log.Lines, l => l.Contains(PairedKey, StringComparison.Ordinal)
                                              || l.Contains(store.Current.Key, StringComparison.Ordinal));

        // And the new key is saved, with the rest, for the next start.
        Assert.Equal(store.Current, new SettingsStore(SettingsPath).Current);
    }

    // A lone surrogate is legal JSON syntax and can't be decoded to text.
    [Theory]
    [InlineData("""{ "key": "abcdefghijklmnopqrstuvwxyz012345", "statusLine": "\uD800", "publicRead": true }""")]
    [InlineData("""{ "key": "abcdefghijklmnopqrstuvwxyz012345", "\uD800": 1, "publicRead": true }""")]
    [InlineData("""{ "key": "abcdefghijklmnopqrstuvwxyz012345", "other": "\uD800", "publicRead": true }""")]
    public void Text_that_cannot_be_decoded_costs_no_more_than_its_own_field(string contents)
    {
        File.WriteAllText(SettingsPath, contents);

        var store = new SettingsStore(SettingsPath);

        Assert.False(store.CreatedOnThisRun);
        Assert.Equal(new Settings { Key = PairedKey, PublicRead = true }, store.Current);
    }

    [Fact]
    public void A_key_that_cannot_be_decoded_is_replaced_keeping_the_rest()
    {
        File.WriteAllText(SettingsPath, """{ "key": "abc\uD800", "publicRead": true }""");
        using var log = Log.Capture();

        var store = new SettingsStore(SettingsPath);

        Assert.True(store.CreatedOnThisRun);
        Assert.Equal(new Settings { Key = store.Current.Key, PublicRead = true }, store.Current);
        Assert.Single(Directory.GetFiles(_dir.Path, "settings.json.bad-*"));
        var warning = Assert.Single(log.Lines, l => l.StartsWith("[config] WARNING", StringComparison.Ordinal));
        Assert.Contains("can't be used (it is text that can't be decoded)", warning);
    }

    [Fact]
    public void Hand_edits_are_forgiven_the_way_env_was()
    {
        File.WriteAllText(SettingsPath, """
            {
              // edited by hand
              "Key": "  abcdefghijklmnopqrstuvwxyz012345 ",
              "DISCORDCLIENTID": " 123456789012345678 ",
              "port": "9005",
              "statusLine": " Details ",
              "publicBase": "https://mypc.example-tailnet.ts.net/",
              "unknownThing": true,
            }
            """);

        var store = new SettingsStore(SettingsPath);

        Assert.False(store.CreatedOnThisRun);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz012345", store.Current.Key);
        Assert.Equal(9005, store.Current.Port);
        Assert.Equal("details", store.Current.StatusLine);
        // publicBase and discordClientId, both removed settings, are ignored
        // like any unknown field.
    }

    [Fact]
    public void A_saved_application_id_is_dropped_from_the_file_on_the_next_save()
    {
        File.WriteAllText(SettingsPath, """{ "discordClientId": "123456789012345678", "key": "k" }""");

        var store = new SettingsStore(SettingsPath);
        store.Save(store.Current with { Port = 9002 });

        Assert.DoesNotContain("discordClientId", File.ReadAllText(SettingsPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nulls_become_defaults_and_an_impossible_port_the_default_port()
    {
        File.WriteAllText(SettingsPath, """{ "key": "k", "statusLine": null, "publicBase": null, "discordClientId": null, "port": 70000 }""");
        using var log = Log.Capture();

        var s = new SettingsStore(SettingsPath).Current;

        Assert.Equal(new Settings { Key = "k" }, s);
        Assert.Contains(log.Lines, l => l.StartsWith("[config] port 70000 in settings is not a usable port", StringComparison.Ordinal));
    }

    [Fact]
    public void The_file_is_camel_case_utf8_json_without_a_bom()
    {
        var store = new SettingsStore(SettingsPath);
        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.NotEqual(0xEF, bytes[0]);

        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal(store.Current.Key, doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(8787, doc.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public void A_missing_folder_is_created()
    {
        var nested = Path.Combine(_dir.Path, "a", "b", "settings.json");
        var store = new SettingsStore(nested);
        Assert.True(store.CreatedOnThisRun);
        Assert.True(File.Exists(nested));
    }
}
