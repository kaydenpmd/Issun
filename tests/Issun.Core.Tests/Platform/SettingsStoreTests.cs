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
            DiscordClientId = "123456789012345678",
            Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Port = 9123,
            PublicBase = "https://mypc.example-tailnet.ts.net",
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
        Assert.Equal("123456789012345678", store.Current.DiscordClientId);
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
    [InlineData("""{ "port": "not a number" }""")]
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
        Assert.Equal("123456789012345678", store.Current.DiscordClientId);
        Assert.Equal(9005, store.Current.Port);
        Assert.Equal("details", store.Current.StatusLine);
        Assert.Equal("https://mypc.example-tailnet.ts.net", store.Current.PublicBase);
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
