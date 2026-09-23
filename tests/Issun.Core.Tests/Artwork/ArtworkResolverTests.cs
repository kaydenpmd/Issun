using System.Net;
using System.Text.Json;
using Issun.Core.Artwork;

namespace Issun.Core.Tests.Artwork;

public class ArtworkResolverTests : IDisposable
{
    private const string KendrickId = "1444846349";
    private const string KendrickCover =
        "https://is1-ssl.mzstatic.com/image/thumb/Music128/v4/e4/1b/cc/e41bcc4a-0022-e8a1-7bd9-261569eaac35/00602557692174.rgb.jpg/512x512bb.jpg";
    private const string PublicBase = "https://pc.example-tailnet.ts.net";

    private readonly TempDir _dir = new();
    private readonly MutableConfig _config = new();
    private readonly ManualClock _clock = new();
    private readonly string _artDir;

    public ArtworkResolverTests()
    {
        // A folder of its own inside the temp dir, so "../outside.jpg" has somewhere to point.
        _artDir = Path.Combine(_dir.Path, "art");
        Directory.CreateDirectory(_artDir);
    }

    public void Dispose() => _dir.Dispose();

    private ArtworkResolver Make(FakeItunes itunes) => new(_config, _artDir, itunes, _clock);

    private static string Body(string name) =>
        JsonDocument.Parse(ParityData.Bodies).RootElement.GetProperty(name).GetProperty("text").GetString()!;

    private static bool Bom(string name) =>
        JsonDocument.Parse(ParityData.Bodies).RootElement.GetProperty(name).GetProperty("bom").GetBoolean();

    private static readonly string NoResults = Body("search-none.json");

    private static TrackInfo Track(string title = "Some Local Demo", string artist = "Bedroom Band", string album = "Tapes",
        string? storeId = null, byte[]? jpeg = null) => new()
    {
        Title = title,
        Artist = artist,
        Album = album,
        StoreId = storeId,
        ArtworkB64 = jpeg is null ? null : Convert.ToBase64String(jpeg),
    };

    // ── Parity with relay.py's artwork_url and artwork_by_store_id ──────────

    [Fact]
    public async Task Fuzzy_search_ranks_judges_and_logs_as_relay_py_did()
    {
        var failures = new List<string>();
        foreach (var c in Py.Cases(ParityData.Search))
        {
            var fixture = c.GetProperty("fixture").GetString()!;
            var (title, artist, album) = (c.GetProperty("title").GetString()!, c.GetProperty("artist").GetString()!,
                c.GetProperty("album").GetString()!);
            _config.Settings = new Settings { ArtMinScore = c.GetProperty("min_score").GetDouble() };
            var itunes = FakeItunes.Serving(Body(fixture), Bom(fixture));
            using var resolver = Make(itunes);

            (string? Url, string Matched) got;
            IReadOnlyList<string> logs;
            using (var capture = Log.Capture())
            {
                got = await resolver.ArtworkBySearchAsync(title, artist, album, CancellationToken.None);
                logs = capture.Lines;
            }

            var label = $"{fixture} {title} | {artist} | {album} @ {c.GetProperty("min_score").GetDouble()}";
            var expectedUrl = c.GetProperty("art").GetString();
            if (got.Url != expectedUrl)
                failures.Add($"{label}: art {got.Url ?? "None"}, relay.py {expectedUrl ?? "None"}");
            if (got.Matched != c.GetProperty("matched").GetString())
                failures.Add($"{label}: matched {got.Matched}, relay.py {c.GetProperty("matched").GetString()}");
            var expectedLogs = c.GetProperty("logs").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (!logs.SequenceEqual(expectedLogs))
                failures.Add($"{label}: logs [{string.Join(" / ", logs)}], relay.py [{string.Join(" / ", expectedLogs)}]");
            if (itunes.Urls.Single() != c.GetProperty("url_requested").GetString())
                failures.Add($"{label}: requested {itunes.Urls.Single()}, relay.py {c.GetProperty("url_requested").GetString()}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Store_id_lookup_matches_relay_py()
    {
        var failures = new List<string>();
        foreach (var c in Py.Cases(ParityData.Lookup))
        {
            var fixture = c.GetProperty("fixture").GetString()!;
            if (fixture is "badurl" or "notjson")
                continue; // deliberate deviations, tested below
            var storeId = c.GetProperty("store_id").GetString()!;
            var itunes = FakeItunes.Serving(Body(fixture));
            using var resolver = Make(itunes);

            (string? Url, string Matched) got;
            IReadOnlyList<string> logs;
            using (var capture = Log.Capture())
            {
                got = await resolver.ArtworkByStoreIdAsync(storeId, CancellationToken.None);
                logs = capture.Lines;
            }

            var expectedUrl = c.GetProperty("art").GetString();
            if (got.Url != expectedUrl)
                failures.Add($"{storeId}: art {got.Url ?? "None"}, relay.py {expectedUrl ?? "None"}");
            if (got.Matched != c.GetProperty("matched").GetString())
                failures.Add($"{storeId}: matched {got.Matched}, relay.py {c.GetProperty("matched").GetString()}");

            var links = c.GetProperty("links");
            CatalogLinks? expectedLinks = links.EnumerateObject().Any()
                ? new CatalogLinks(Py.OptStr(links, "song"), Py.OptStr(links, "artist"), Py.OptStr(links, "album"))
                : null;
            if (resolver.CachedLinks(storeId) != expectedLinks)
                failures.Add($"{storeId}: links {resolver.CachedLinks(storeId)}, relay.py {expectedLinks}");
            if (resolver.CachedArtwork(storeId) != expectedUrl)
                failures.Add($"{storeId}: cached artwork {resolver.CachedArtwork(storeId)}, relay.py {expectedUrl}");

            var expectedLogs = c.GetProperty("logs").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (!logs.SequenceEqual(expectedLogs))
                failures.Add($"{storeId}: logs [{string.Join(" / ", logs)}], relay.py [{string.Join(" / ", expectedLogs)}]");
            if (itunes.Urls.Single() != c.GetProperty("url_requested").GetString())
                failures.Add($"{storeId}: requested {itunes.Urls.Single()}, relay.py {c.GetProperty("url_requested").GetString()}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task A_malformed_album_url_costs_only_the_album_link()
    {
        // relay.py ran _album_url inside the lookup's try block, so this
        // response failed the whole lookup: no cover, no links, and the log
        // blamed the lookup. Only the album link is actually unusable.
        using var resolver = Make(FakeItunes.Serving(Body("badurl")));
        using var capture = Log.Capture();

        var (url, matched) = await resolver.ArtworkByStoreIdAsync("45", CancellationToken.None);

        Assert.Equal("https://example.invalid/z/512x512bb.jpg", url);
        Assert.Equal("Z", matched);
        Assert.Equal(new CatalogLinks("https://music.apple.com/us/album/song/2?i=3&uo=4", null, null), resolver.CachedLinks("45"));
        Assert.Equal(["[art] store id 45: album link dropped, collectionViewUrl is malformed (Invalid IPv6 URL)"], capture.Lines);
    }

    [Fact]
    public async Task A_response_that_is_not_json_is_a_logged_failure()
    {
        using var resolver = Make(FakeItunes.Serving(Body("notjson")));
        using var capture = Log.Capture();

        var (url, matched) = await resolver.ArtworkByStoreIdAsync("46", CancellationToken.None);

        Assert.Null(url);
        Assert.Equal("", matched);
        Assert.Null(resolver.CachedLinks("46"));
        var line = Assert.Single(capture.Lines);
        Assert.StartsWith("[art] id lookup failed for 46: invalid JSON", line);
    }

    // ── Resolution order ────────────────────────────────────────────────────

    [Fact]
    public async Task The_store_id_wins_and_nothing_else_runs()
    {
        _config.DetectedPublicBase = PublicBase;
        var itunes = FakeItunes.Serving(Body("lookup-1444846349.json"), NoResults);
        using var resolver = Make(itunes);

        var result = await resolver.ResolveAsync(
            Track("i", "Kendrick Lamar", "i - Single", storeId: KendrickId, jpeg: Py.Jpeg()), CancellationToken.None);

        Assert.Equal(new ArtworkResult(KendrickCover, "i - Single", ArtworkSource.StoreId), result);
        Assert.Single(itunes.Urls);
        Assert.Empty(Directory.GetFiles(_artDir));
        Assert.Equal(new CatalogLinks(
            "https://music.apple.com/us/album/i/1444846337?i=1444846349&uo=4",
            "https://music.apple.com/us/artist/kendrick-lamar/368183298?uo=4",
            "https://music.apple.com/us/album/i/1444846337?uo=4"), resolver.CachedLinks(KendrickId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task No_usable_store_id_means_no_lookup(string? storeId)
    {
        var itunes = FakeItunes.Serving(Body("lookup-1444846349.json"), NoResults);
        using var resolver = Make(itunes);

        await resolver.ResolveAsync(Track(storeId: storeId), CancellationToken.None);

        Assert.DoesNotContain(itunes.Urls, u => u.Contains("/lookup?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_id_without_artwork_still_yields_links_and_falls_through_to_search()
    {
        var itunes = FakeItunes.Serving(Body("noart"), Body("search-kendrick-i.json"));
        using var resolver = Make(itunes);
        using var capture = Log.Capture();

        var result = await resolver.ResolveAsync(Track("i", "Kendrick Lamar", "i - Single", storeId: "42"), CancellationToken.None);

        Assert.Equal(ArtworkSource.Search, result.Source);
        Assert.Equal(KendrickCover, result.Url);
        Assert.Equal("https://music.apple.com/us/album/song/2?uo=4", resolver.CachedLinks("42")!.Album);
        Assert.Null(resolver.CachedArtwork("42"));
        Assert.Equal(["[art] store id 42 resolved but has no artwork"], capture.Lines);
    }

    [Fact]
    public async Task An_uploaded_jpeg_is_stored_under_relay_py_s_filename_and_served_publicly()
    {
        _config.DetectedPublicBase = PublicBase + "/";
        var itunes = FakeItunes.Serving(Body("lookup-missing.json"), NoResults);
        using var resolver = Make(itunes);
        var jpeg = Py.Jpeg();
        var track = Track(storeId: "1", jpeg: jpeg);
        var name = UploadedArt.FileName("Bedroom Band|Some Local Demo|Tapes");

        var result = await resolver.ResolveAsync(track, CancellationToken.None);

        Assert.Equal(new ArtworkResult($"{PublicBase}/art/{name}", "Tapes", ArtworkSource.Uploaded), result);
        Assert.Equal(jpeg, File.ReadAllBytes(Path.Combine(_artDir, name)));
        Assert.Equal(Path.Combine(_artDir, name), resolver.UploadedArtPath(name));
        Assert.DoesNotContain(itunes.Urls, u => u.Contains("/search?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Heartbeats_without_the_jpeg_reuse_the_one_on_disk()
    {
        // The phone sends the ~80 KB cover once per track, not every 30 s.
        // Without reuse the cover appears on one push and vanishes on the next.
        _config.DetectedPublicBase = PublicBase;
        var itunes = FakeItunes.Serving(NoResults);
        using var resolver = Make(itunes);

        var first = await resolver.ResolveAsync(Track(jpeg: Py.Jpeg()), CancellationToken.None);
        var heartbeat = await resolver.ResolveAsync(Track(), CancellationToken.None);

        Assert.Equal(ArtworkSource.Uploaded, first.Source);
        Assert.Equal(first, heartbeat);
        Assert.Equal(0, itunes.Count);
    }

    [Fact]
    public async Task A_cover_on_disk_is_never_overwritten()
    {
        _config.DetectedPublicBase = PublicBase;
        using var resolver = Make(FakeItunes.Serving(NoResults));
        var original = Py.Jpeg(seed: 1);

        await resolver.ResolveAsync(Track(jpeg: original), CancellationToken.None);
        await resolver.ResolveAsync(Track(jpeg: Py.Jpeg(seed: 2)), CancellationToken.None);

        var path = Assert.Single(Directory.GetFiles(_artDir));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    public static TheoryData<string, string> Rejections => new()
    {
        { "not base64 at all!", "not valid base64 (Only base64 data is allowed)" },
        { "/9j/4A\nAQ", "not valid base64 (Only base64 data is allowed)" },
        { "/9j", "not valid base64 (Incorrect padding)" },
        { Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]), "not a JPEG (starts 89504E)" },
        { Convert.ToBase64String([0xFF, 0xD8]), "not a JPEG (starts FFD8)" },
        { Convert.ToBase64String([0xFF, 0xD8, 0xFF, .. new byte[UploadedArt.MaxBytes - 2]]), "3145729 bytes is over the 3 MB limit" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task A_rejected_upload_says_why_once_per_track_and_falls_through(string b64, string reason)
    {
        // relay.py returned None here without a word, which is the silent
        // failure this project keeps paying for.
        _config.DetectedPublicBase = PublicBase;
        var itunes = FakeItunes.Serving(NoResults);
        using var resolver = Make(itunes);
        using var capture = Log.Capture();
        var track = Track() with { ArtworkB64 = b64 };

        for (var i = 0; i < 5; i++)
            Assert.Equal(ArtworkResult.None, await resolver.ResolveAsync(track, CancellationToken.None));

        Assert.Equal(
        [
            $"[art] uploaded cover for Some Local Demo — Bedroom Band rejected: {reason}",
            "[art] no catalog results: Bedroom Band Some Local Demo Tapes",
        ], capture.Lines);
        Assert.Equal(1, itunes.Count);
        Assert.Empty(Directory.GetFiles(_artDir));
    }

    [Fact]
    public async Task Without_a_public_address_the_cover_is_kept_until_one_is_known()
    {
        // Discord's CDN fetches the image itself and can't reach 127.0.0.1, so
        // with no public base there is no usable URL. relay.py discarded the
        // upload; Issun learns its address from Tailscale, perhaps after the
        // only push that carried the cover, so it keeps it.
        var itunes = FakeItunes.Serving(NoResults);
        using var resolver = Make(itunes);
        using var capture = Log.Capture();

        var before = await resolver.ResolveAsync(Track(jpeg: Py.Jpeg()), CancellationToken.None);
        await resolver.ResolveAsync(Track(), CancellationToken.None);
        _config.DetectedPublicBase = PublicBase;
        var after = await resolver.ResolveAsync(Track(), CancellationToken.None);

        Assert.Equal(ArtworkResult.None, before);
        Assert.Equal(ArtworkSource.Uploaded, after.Source);
        Assert.StartsWith(PublicBase + "/art/", after.Url);
        Assert.Equal(
        [
            "[art] uploaded cover for Some Local Demo — Bedroom Band is saved but unused: no public address for Discord "
                + "to fetch it from (set PUBLIC_BASE, or turn on Tailscale Funnel)",
            "[art] no catalog results: Bedroom Band Some Local Demo Tapes",
        ], capture.Lines);
    }

    [Fact]
    public async Task The_public_base_setting_overrides_tailscale_and_is_read_per_call()
    {
        _config.DetectedPublicBase = PublicBase;
        using var resolver = Make(FakeItunes.Serving(NoResults));
        var first = await resolver.ResolveAsync(Track(jpeg: Py.Jpeg()), CancellationToken.None);

        _config.Settings = _config.Settings with { PublicBase = "https://relay.example.net" };
        var second = await resolver.ResolveAsync(Track(), CancellationToken.None);

        Assert.StartsWith(PublicBase + "/art/", first.Url);
        Assert.StartsWith("https://relay.example.net/art/", second.Url);
    }

    [Fact]
    public async Task The_cache_is_pruned_to_the_60_newest_covers()
    {
        _config.DetectedPublicBase = PublicBase;
        var old = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < 70; i++)
        {
            var path = Path.Combine(_artDir, $"{i:D20}.jpg");
            File.WriteAllBytes(path, Py.Jpeg());
            File.SetLastWriteTimeUtc(path, old.AddMinutes(i));
        }
        File.WriteAllText(Path.Combine(_artDir, "notes.txt"), "not a cover");
        using var resolver = Make(FakeItunes.Serving(NoResults));

        var result = await resolver.ResolveAsync(Track(jpeg: Py.Jpeg()), CancellationToken.None);

        var covers = Directory.GetFiles(_artDir, "*.jpg").Select(Path.GetFileName).ToHashSet();
        Assert.Equal(60, covers.Count);
        Assert.Contains(result.Url![(PublicBase.Length + "/art/".Length)..], covers);
        for (var i = 0; i < 11; i++)
            Assert.DoesNotContain($"{i:D20}.jpg", covers);
        for (var i = 11; i < 70; i++)
            Assert.Contains($"{i:D20}.jpg", covers);
        Assert.True(File.Exists(Path.Combine(_artDir, "notes.txt")));
    }

    // ── Caching ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_answer_is_cached_so_the_worker_can_ask_every_second()
    {
        var itunes = FakeItunes.Serving(Body("lookup-missing.json"), NoResults);
        using var resolver = Make(itunes);
        using var capture = Log.Capture();

        for (var i = 0; i < 10; i++)
            Assert.Equal(ArtworkResult.None, await resolver.ResolveAsync(Track(storeId: "1"), CancellationToken.None));

        Assert.Equal(2, itunes.Count);
        Assert.Equal(["[art] store id 1 not in catalog", "[art] no catalog results: Bedroom Band Some Local Demo Tapes"],
            capture.Lines);
    }

    [Fact]
    public async Task A_weak_match_is_logged_once_not_every_second()
    {
        var itunes = FakeItunes.Serving(Body("search-kendrick-i.json"));
        using var resolver = Make(itunes);
        using var capture = Log.Capture();

        for (var i = 0; i < 5; i++)
            await resolver.ResolveAsync(Track("Stars", "SZA", "Panther"), CancellationToken.None);

        Assert.Equal(1, itunes.Count);
        Assert.Equal(["[art] weak match (0.46): Stars — SZA → Black Panther: The Album"], capture.Lines);
    }

    [Fact]
    public async Task Art_min_score_is_read_per_call_without_refetching()
    {
        var itunes = FakeItunes.Serving(Body("search-kendrick-i.json"));
        using var resolver = Make(itunes);
        var track = Track("Stars", "SZA", "Panther");
        using var capture = Log.Capture();

        var accepted = await resolver.ResolveAsync(track, CancellationToken.None);
        _config.Settings = new Settings { ArtMinScore = 0.5 };
        var refused = await resolver.ResolveAsync(track, CancellationToken.None);
        var refusedAgain = await resolver.ResolveAsync(track, CancellationToken.None);
        _config.Settings = new Settings { ArtMinScore = 0 };
        var acceptedAgain = await resolver.ResolveAsync(track, CancellationToken.None);

        Assert.Equal(ArtworkSource.Search, accepted.Source);
        Assert.Equal(ArtworkResult.None, refused);
        Assert.Equal(ArtworkResult.None, refusedAgain);
        Assert.Equal(accepted, acceptedAgain);
        Assert.Equal(1, itunes.Count);
        Assert.Equal(
        [
            "[art] weak match (0.46): Stars — SZA → Black Panther: The Album",
            "[art] nothing related (0.46): Stars — SZA",
            "[art] weak match (0.46): Stars — SZA → Black Panther: The Album",
        ], capture.Lines);
    }

    [Fact]
    public async Task Concurrent_calls_for_the_same_track_share_one_request()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var itunes = new FakeItunes
        {
            Respond = async (_, _) =>
            {
                await gate.Task;
                return FakeItunes.Json(Body("lookup-1444846349.json"));
            },
        };
        using var resolver = Make(itunes);
        var track = Track("i", "Kendrick Lamar", "i - Single", storeId: KendrickId);

        var calls = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => resolver.ResolveAsync(track, CancellationToken.None)))
            .ToArray();
        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.Equal(KendrickCover, r.Url));
        Assert.Equal(1, itunes.Count);
    }

    [Fact]
    public async Task A_caller_giving_up_does_not_waste_the_lookup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var itunes = new FakeItunes
        {
            Respond = async (_, _) =>
            {
                await gate.Task;
                return FakeItunes.Json(Body("lookup-1444846349.json"));
            },
        };
        using var resolver = Make(itunes);
        var track = Track("i", "Kendrick Lamar", "i - Single", storeId: KendrickId);
        using var cts = new CancellationTokenSource();

        var abandoned = resolver.ResolveAsync(track, cts.Token);
        cts.Cancel();
        Assert.Equal(ArtworkResult.None, await abandoned);

        gate.SetResult();
        for (var i = 0; i < 50 && resolver.CachedArtwork(KendrickId) is null; i++)
            await Task.Delay(20);

        Assert.Equal(KendrickCover, (await resolver.ResolveAsync(track, CancellationToken.None)).Url);
        Assert.Equal(1, itunes.Count);
    }

    [Fact]
    public async Task Cached_reads_never_perform_a_lookup()
    {
        // GET /now-playing uses these, and a public GET must not be a way to
        // make this machine issue outbound traffic.
        var itunes = FakeItunes.Serving(Body("lookup-1444846349.json"));
        using var resolver = Make(itunes);

        Assert.Null(resolver.CachedArtwork(KendrickId));
        Assert.Null(resolver.CachedLinks(KendrickId));
        Assert.Null(resolver.CachedArtwork(""));
        Assert.Null(resolver.CachedLinks("  "));
        Assert.Equal(0, itunes.Count);

        await resolver.ArtworkByStoreIdAsync(KendrickId, CancellationToken.None);

        Assert.Equal(KendrickCover, resolver.CachedArtwork(KendrickId));
        Assert.Equal(KendrickCover, resolver.CachedArtwork($" {KendrickId} "));
        Assert.NotNull(resolver.CachedLinks(KendrickId));
        Assert.Equal(1, itunes.Count);
    }

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_lookup_is_logged_and_retried_after_a_minute_not_every_second()
    {
        var itunes = new FakeItunes { Respond = (_, _) => throw new HttpRequestException("No such host is known.") };
        using var resolver = Make(itunes);
        using var capture = Log.Capture();

        for (var i = 0; i < 5; i++)
            await resolver.ArtworkByStoreIdAsync("5", CancellationToken.None);
        Assert.Equal(1, itunes.Count);

        _clock.Advance(ArtworkResolver.FailureRetry.TotalSeconds);
        itunes.Respond = (_, _) => Task.FromResult(FakeItunes.Json(Body("lookup-1444846349.json")));
        var (url, _) = await resolver.ArtworkByStoreIdAsync("5", CancellationToken.None);

        Assert.Equal(KendrickCover, url);
        Assert.Equal(2, itunes.Count);
        Assert.Equal(["[art] id lookup failed for 5: No such host is known."], capture.Lines);
    }

    [Fact]
    public async Task A_not_in_catalog_answer_stands_and_is_not_retried()
    {
        var itunes = FakeItunes.Serving(Body("lookup-missing.json"));
        using var resolver = Make(itunes);

        await resolver.ArtworkByStoreIdAsync("1", CancellationToken.None);
        _clock.Advance(3600);
        await resolver.ArtworkByStoreIdAsync("1", CancellationToken.None);

        Assert.Equal(1, itunes.Count);
    }

    [Fact]
    public async Task Http_errors_read_as_urllib_worded_them()
    {
        var itunes = new FakeItunes
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "Forbidden" }),
        };
        using var resolver = Make(itunes);
        using var capture = Log.Capture();

        await resolver.ArtworkByStoreIdAsync("5", CancellationToken.None);
        await resolver.ArtworkBySearchAsync("Song", "Artist", "", CancellationToken.None);

        Assert.Equal(
        [
            "[art] id lookup failed for 5: HTTP Error 403: Forbidden",
            "[art] lookup failed for Song: HTTP Error 403: Forbidden",
        ], capture.Lines);
    }

    [Fact]
    public async Task A_slow_itunes_times_out_and_says_so()
    {
        var itunes = new FakeItunes
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        using var resolver = new ArtworkResolver(_config, _artDir, itunes, _clock) { LookupTimeout = TimeSpan.FromMilliseconds(200) };
        using var capture = Log.Capture();

        var result = await resolver.ResolveAsync(Track(), CancellationToken.None);

        Assert.Equal(ArtworkResult.None, result);
        Assert.Equal(["[art] lookup failed for Some Local Demo: timed out after 0.2s"], capture.Lines);
    }

    [Theory]
    [InlineData("[1, 2, 3]", "unexpected response: not a JSON object")]
    [InlineData("""{"results": {"a": 1}}""", "unexpected response: results is not a list")]
    [InlineData("""{"results": [5]}""", "unexpected response: the first result is not an object")]
    public async Task Odd_responses_are_logged_failures_not_exceptions(string body, string detail)
    {
        using var resolver = Make(FakeItunes.Serving(body));
        using var capture = Log.Capture();

        var (url, _) = await resolver.ArtworkByStoreIdAsync("9", CancellationToken.None);

        Assert.Null(url);
        Assert.Equal([$"[art] id lookup failed for 9: {detail}"], capture.Lines);
    }

    [Fact]
    public async Task Requests_identify_as_issun()
    {
        var itunes = FakeItunes.Serving(NoResults);
        using var resolver = Make(itunes);

        await resolver.ResolveAsync(Track(storeId: "1"), CancellationToken.None);

        Assert.Equal(2, itunes.Count);
        Assert.All(itunes.Requests, r => Assert.Equal($"Issun/{IssunInfo.Version}", r.Headers.UserAgent.ToString()));
        Assert.Equal(
        [
            "https://itunes.apple.com/lookup?id=1&entity=song",
            "https://itunes.apple.com/search?term=Bedroom+Band+Some+Local+Demo+Tapes&entity=song&limit=12",
        ], itunes.Urls);
    }

    [Fact]
    public async Task Resolve_never_throws()
    {
        var itunes = new FakeItunes { Respond = (_, _) => throw new InvalidOperationException("boom") };
        using var resolver = Make(itunes);

        Assert.Equal(ArtworkResult.None, await resolver.ResolveAsync(Track(storeId: "3"), CancellationToken.None));
        Assert.Equal(ArtworkResult.None, await resolver.ResolveAsync(null!, CancellationToken.None));
    }

    // ── GET /art/<name> ─────────────────────────────────────────────────────

    [Fact]
    public void Uploaded_art_path_serves_only_bare_jpg_names_that_exist()
    {
        using var resolver = Make(FakeItunes.Serving(NoResults));
        var name = "0123456789abcdef0123.jpg";
        File.WriteAllBytes(Path.Combine(_artDir, name), Py.Jpeg());
        Directory.CreateDirectory(Path.Combine(_artDir, "sub"));
        File.WriteAllBytes(Path.Combine(_artDir, "sub", "inner.jpg"), Py.Jpeg());
        Directory.CreateDirectory(Path.Combine(_artDir, "folder.jpg"));
        File.WriteAllBytes(Path.Combine(_artDir, "cover.png"), Py.Jpeg());
        File.WriteAllBytes(Path.Combine(_dir.Path, "outside.jpg"), Py.Jpeg());

        Assert.Equal(Path.Combine(_artDir, name), resolver.UploadedArtPath(name));

        string?[] refused =
        [
            null, "", ".jpg", "missing.jpg", "cover.png", "folder.jpg", "sub/inner.jpg", "sub\\inner.jpg", "inner.jpg",
            "../outside.jpg", "..\\outside.jpg", "..", "%2e%2e%2foutside.jpg", name.ToUpperInvariant(),
            name + ":stream", name + ".", name + " ", Path.Combine(_artDir, name), "C:outside.jpg",
            "\\\\?\\" + Path.Combine(_artDir, name), "CON.jpg",
        ];
        foreach (var bad in refused)
            Assert.True(resolver.UploadedArtPath(bad!) is null, $"served {Py.Show(bad ?? "null")}");
    }
}
