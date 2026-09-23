using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Issun.Core;
using Issun.Core.Server;

namespace Issun.Core.Tests.Server;

/// <summary>
/// Real Kestrel on a free loopback port, driven by HttpClient (and a raw socket
/// where HttpClient won't send what's needed). One class, so the tests that
/// watch the process-wide log never see another server's lines.
/// </summary>
public class RelayServerTests
{
    private const string Key = ServerHarness.Key;

    private static async Task<string> AssertReply(HttpResponseMessage response, HttpStatusCode status, string? body,
        string contentType = "text/plain")
    {
        Assert.Equal(status, response.StatusCode);
        AssertFromIssun(response);
        var text = await response.Content.ReadAsStringAsync();
        if (body is not null)
        {
            Assert.Equal(body, text);
            Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
            Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        }
        return text;
    }

    /// <summary>Ammy tells Issun's own 404 from Funnel's by this header alone.</summary>
    private static void AssertFromIssun(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(RelayServer.RelayHeader, out var values),
            $"{(int)response.StatusCode} response is missing {RelayServer.RelayHeader}");
        Assert.Equal(IssunInfo.Wire, Assert.Single(values));
    }

    // ─────────────────────────────── Open routes ───────────────────────────────

    [Fact]
    public async Task Health_is_exactly_ok_and_needs_no_key()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync("/health"), HttpStatusCode.OK, "ok");
    }

    [Fact]
    public async Task Version_is_the_wire_string()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync("/version"), HttpStatusCode.OK, IssunInfo.Wire);
        Assert.StartsWith("issun ", IssunInfo.Wire);
    }

    [Theory]
    [InlineData("/health/")]
    [InlineData("/health//")]
    [InlineData("/health?cache=1")]
    public async Task Trailing_slashes_and_query_strings_are_ignored(string path)
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync(path), HttpStatusCode.OK, "ok");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Health")]
    [InlineData("/nope")]
    [InlineData("/art")]
    [InlineData("/art/")]
    [InlineData("/now-playing/extra")]
    public async Task Anything_else_is_a_404_from_issun(string path)
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync(path, key: Key), HttpStatusCode.NotFound, "not found");
    }

    // ─────────────────────────────── POST /now-playing ───────────────────────────

    [Fact]
    public async Task A_push_is_parsed_handed_over_and_answered_204()
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.PostAsync("""
            {"playing": true, "app_version": "1.0 (80)", "seq": 1758400000123, "title": "i",
             "artist": "Kendrick Lamar", "album": "i - Single", "duration": 231.5, "elapsed": 12,
             "store_id": "1440894925", "diag": {"engine_running": true}}
            """);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertFromIssun(response);
        Assert.Equal("", await response.Content.ReadAsStringAsync());

        var push = Assert.Single(h.Checkins.Accepted);
        Assert.True(push.Playing);
        Assert.Equal("1.0 (80)", push.AppVersion);
        Assert.Equal(1758400000123, push.Seq);
        Assert.Equal("i", push.Track!.Title);
        Assert.Equal(231.5, push.Track.Duration);
        Assert.True(push.Diag!["engine_running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_dropped_push_is_still_a_204()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Checkins.OnAccept = _ => new CheckinResult(false, "out of order");
        var response = await h.PostAsync("""{"playing": false, "seq": 1}""");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertFromIssun(response);
    }

    [Fact]
    public async Task An_empty_body_is_an_empty_object_as_in_relay_py()
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.SendAsync(HttpMethod.Post, "/now-playing", key: Key);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var push = Assert.Single(h.Checkins.Accepted);
        Assert.False(push.Playing);
        Assert.Empty(push.Raw);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"playing\"")]
    [InlineData("   ")]
    [InlineData("{\"playing\": true} trailing")]
    public async Task Unparseable_or_non_object_json_is_400(string body)
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.PostAsync(body), HttpStatusCode.BadRequest, "bad json");
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task A_number_too_big_for_a_double_is_infinity_as_in_python()
    {
        // json.loads('{"playing": 1e400}') is {'playing': inf}, and relay.py took it.
        await using var h = await ServerHarness.StartAsync();
        var response = await h.PostAsync("""{"playing": 1e400, "duration": 1e400, "seq": 1e400}""");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var push = Assert.Single(h.Checkins.Accepted);
        Assert.True(push.Playing);
        Assert.Equal(double.PositiveInfinity, push.Track!.Duration);
        Assert.Null(push.Seq);
    }

    [Theory]
    [InlineData("""{"playing": true, "title": "\ud800"}""")]
    [InlineData("""{"playing": true, "diag": {"route": "\udc00 speaker"}}""")]
    [InlineData("""{"\ud800": 1}""")]
    public async Task A_lone_surrogate_anywhere_is_400_not_500(string body)
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.PostAsync(body), HttpStatusCode.BadRequest, "bad json");
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task Strings_arrive_decoded_and_numbers_keep_their_literal()
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.PostAsync("""{"playing": true, "title": "Caf\u00e9 \ud83c\udfb5", "seq": 5, "elapsed": 5.0}""");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var push = Assert.Single(h.Checkins.Accepted);
        Assert.Equal("Café 🎵", push.Track!.Title);
        Assert.Equal(5, push.Seq);
        Assert.Equal("5.0", push.Raw["elapsed"]!.ToJsonString());
    }

    [Fact]
    public async Task Duplicate_keys_keep_the_last_value_like_a_python_dict()
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.PostAsync("""{"playing": true, "title": "first", "title": "second"}""");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("second", Assert.Single(h.Checkins.Accepted).Track!.Title);
    }

    [Fact]
    public async Task A_utf8_bom_is_skipped()
    {
        await using var h = await ServerHarness.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/now-playing")
        {
            Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. """{"playing": true, "title": "Björk"}"""u8.ToArray()]),
        };
        request.Headers.Add("X-Relay-Key", Key);
        var response = await h.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Björk", Assert.Single(h.Checkins.Accepted).Track!.Title);
    }

    [Fact]
    public async Task A_wrong_path_is_404_before_the_key_is_checked()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.PostAsync("{}", key: null, path: "/"), HttpStatusCode.NotFound, "not found");
        await AssertReply(await h.PostAsync("{}", key: null, path: "/health"), HttpStatusCode.NotFound, "not found");
        var slash = await h.PostAsync("{}", path: "/now-playing/");
        Assert.Equal(HttpStatusCode.NoContent, slash.StatusCode);
    }

    [Fact]
    public async Task Concurrent_pushes_are_all_answered()
    {
        await using var h = await ServerHarness.StartAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => h.PostAsync($$"""{"playing": true, "seq": {{i}}, "title": "t{{i}}"}""")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(40, h.Checkins.Accepted.Count);
    }

    // ────────────────────────────────── Keys ───────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("wrong", null)]
    [InlineData(Key + "x", null)]
    [InlineData(null, "wrong")]
    [InlineData("wrong", Key)]   // a non-empty X-Relay-Key wins, even when wrong
    public async Task Refused_keys_get_401_and_the_body_is_never_read(string? key, string? secret)
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.SendAsync(HttpMethod.Post, "/now-playing", "{\"playing\": true}", key, secret);
        await AssertReply(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Empty(h.Checkins.Accepted);
    }

    [Theory]
    [InlineData(Key, null)]
    [InlineData(null, Key)]      // X-Relay-Secret: what builds before relay 1.7.0 send
    [InlineData("", Key)]        // Python's `or`: an empty X-Relay-Key falls through
    [InlineData(Key, "wrong")]
    public async Task Accepted_keys(string? key, string? secret)
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.SendAsync(HttpMethod.Post, "/now-playing", "{}", key, secret);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_configured_key_refuses_everything()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Config.Settings = h.Config.Settings with { Key = "   " };

        await AssertReply(await h.SendAsync(HttpMethod.Post, "/now-playing", "{}"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertReply(await h.SendAsync(HttpMethod.Post, "/now-playing", "{}", key: ""), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertReply(await h.SendAsync(HttpMethod.Post, "/now-playing", "{}", key: "   "), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertReply(await h.GetAsync("/status"), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task The_key_is_read_per_request()
    {
        await using var h = await ServerHarness.StartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync("{}")).StatusCode);

        h.Config.Settings = h.Config.Settings with { Key = "regenerated" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync("{}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync("{}", key: "regenerated")).StatusCode);
    }

    [Fact]
    public async Task A_configured_key_is_trimmed_as_relay_py_stripped_it()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Config.Settings = h.Config.Settings with { Key = "  padded-key \n" };
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync("{}", key: "padded-key")).StatusCode);
    }

    // ────────────────────────────── /diag and /status ───────────────────────────

    [Fact]
    public async Task Diag_needs_the_key()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync("/diag"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Diag_before_any_report()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync("/diag", Key), HttpStatusCode.OK, "no diagnostics yet");

        // relay.py tested `not _phone_diag`, so an empty report counts as none.
        h.Diagnostics.Latest = new JsonObject();
        h.Diagnostics.LatestAt = h.Clock.Now;
        await AssertReply(await h.GetAsync("/diag", Key), HttpStatusCode.OK, "no diagnostics yet");
    }

    [Fact]
    public async Task Diag_prefixes_the_summary_with_its_truncated_age()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Diagnostics.Latest = new JsonObject { ["engine_running"] = true };
        h.Diagnostics.LatestAt = h.Clock.Now - 12.9;
        h.Diagnostics.SummaryText = "engine=yes want=yes route=Speaker last_error='café'";

        await AssertReply(await h.GetAsync("/diag", Key), HttpStatusCode.OK,
            "12s ago: engine=yes want=yes route=Speaker last_error='café'");
    }

    [Theory]
    [InlineData(0.0, false)]         // never checked in
    [InlineData(10.0, true)]
    [InlineData(89.9, true)]
    [InlineData(90.0, false)]        // IDLE_TIMEOUT, strictly less-than
    [InlineData(600.0, false)]
    public async Task Status_is_alive_or_stale_from_the_last_check_in(double ago, bool alive)
    {
        await using var h = await ServerHarness.StartAsync();
        h.Phone.LastCheckinAt = ago == 0 ? 0 : h.Clock.Now - ago;
        await AssertReply(await h.GetAsync("/status"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertReply(await h.GetAsync("/status", Key), HttpStatusCode.OK, alive ? "alive" : "stale");
    }

    // ─────────────────────────────────── /art ───────────────────────────────────

    [Fact]
    public async Task Art_is_served_without_a_key()
    {
        await using var h = await ServerHarness.StartAsync();
        var file = Path.Combine(Path.GetTempPath(), $"issun-art-{Guid.NewGuid():N}.jpg");
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
        await File.WriteAllBytesAsync(file, jpeg);
        try
        {
            h.Artwork.Files["0123456789abcdef0123.jpg"] = file;
            var response = await h.GetAsync("/art/0123456789abcdef0123.jpg");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertFromIssun(response);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.ToString());
            Assert.Equal(jpeg.Length, response.Content.Headers.ContentLength);
            Assert.Equal("public, max-age=604800", response.Headers.CacheControl?.ToString());
            Assert.Equal(jpeg, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Missing_art_is_a_404_from_issun()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync("/art/nothing.jpg"), HttpStatusCode.NotFound, "not found");
    }

    [Fact]
    public async Task Art_that_vanishes_before_it_is_read_is_a_404()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Artwork.Files["gone.jpg"] = Path.Combine(Path.GetTempPath(), $"issun-missing-{Guid.NewGuid():N}.jpg");
        await AssertReply(await h.GetAsync("/art/gone.jpg"), HttpStatusCode.NotFound, "not found");
    }

    [Theory]
    [InlineData("/art/x.jpg", "x.jpg")]
    [InlineData("/art/x.jpg/", "x.jpg")]
    [InlineData("/art/sub/dir/x.jpg", "x.jpg")]
    [InlineData("/art/..%5Cx.jpg", "x.jpg")]
    [InlineData("/art/x.png", "x.png")]
    public async Task Only_the_last_segment_is_ever_looked_up(string path, string asked)
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync(path), HttpStatusCode.NotFound, "not found");
        Assert.Equal(asked, Assert.Single(h.Artwork.ArtNamesAsked));
    }

    // ────────────────────────────── GET /now-playing ────────────────────────────

    [Fact]
    public async Task Now_playing_needs_the_key_unless_public_read()
    {
        await using var h = await ServerHarness.StartAsync();
        await AssertReply(await h.GetAsync("/now-playing"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertReply(await h.GetAsync("/now-playing", "wrong"), HttpStatusCode.Unauthorized, "unauthorized");

        var keyed = await h.GetAsync("/now-playing", Key);
        await AssertReply(keyed, HttpStatusCode.OK, """{"playing": false, "stale": true, "updated_ago": null}""", "application/json");
        Assert.Equal("no-store", keyed.Headers.CacheControl?.ToString());
        Assert.False(keyed.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Public_read_drops_the_key_and_adds_cors()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Config.Settings = h.Config.Settings with { PublicRead = true };

        foreach (var key in new[] { null, "wrong", Key })
        {
            var response = await h.GetAsync("/now-playing", key);
            await AssertReply(response, HttpStatusCode.OK, """{"playing": false, "stale": true, "updated_ago": null}""", "application/json");
            Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        // Only this route opens up.
        await AssertReply(await h.GetAsync("/status"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertReply(await h.GetAsync("/diag"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Now_playing_matches_relay_py_byte_for_byte_and_never_looks_anything_up()
    {
        await using var h = await ServerHarness.StartAsync();
        var parity = ProjectionTests.Cases.Single(c => c.Name == "full");
        ProjectionTests.Arrange(parity, h.Phone, h.Artwork, h.Clock);

        var response = await h.GetAsync("/now-playing", Key);
        await AssertReply(response, HttpStatusCode.OK, parity.Expected, "application/json");
        Assert.Equal(0, h.Artwork.ResolveCalls);
    }

    // ───────────────────────────── Other methods ────────────────────────────────

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("PATCH")]
    public async Task Methods_relay_py_had_no_handler_for_are_501(string method)
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.SendAsync(new HttpMethod(method), "/now-playing", "{}", Key);
        await AssertReply(response, HttpStatusCode.NotImplemented, $"Unsupported method ('{method}')");
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task Head_is_501_too()
    {
        await using var h = await ServerHarness.StartAsync();
        var response = await h.SendAsync(HttpMethod.Head, "/health");
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        AssertFromIssun(response);
    }

    [Fact]
    public async Task Methods_are_case_sensitive()
    {
        await using var h = await ServerHarness.StartAsync();
        var raw = await h.RawAsync("get /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 501", raw);
        Assert.Contains($"{RelayServer.RelayHeader}: {IssunInfo.Wire}\r\n", raw);
    }

    // ───────────────────────────── Failure paths ────────────────────────────────

    [Fact]
    public async Task A_failure_inside_is_a_500_that_still_says_it_came_from_issun()
    {
        await using var h = await ServerHarness.StartAsync();
        h.Checkins.OnAccept = _ => throw new InvalidOperationException("boom from the checkin processor");

        HttpResponseMessage? response = null;
        var line = await ServerHarness.WaitForLogAsync(
            l => l.Contains("boom from the checkin processor"),
            async () => response = await h.PostAsync("{}"));

        await AssertReply(response!, HttpStatusCode.InternalServerError, "internal error");
        Assert.StartsWith("[http] POST /now-playing failed: System.InvalidOperationException", line);
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_body_is_logged_as_normal()
    {
        await using var h = await ServerHarness.StartAsync();
        var line = await ServerHarness.WaitForLogAsync(
            l => l.StartsWith("[http] client disconnected", StringComparison.Ordinal) || l.Contains("POST /now-playing"),
            async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, h.Port);
                var head = $"POST /now-playing HTTP/1.1\r\nHost: localhost\r\nX-Relay-Key: {Key}\r\n" +
                           "Content-Type: application/json\r\nContent-Length: 5000\r\n\r\n{\"playing\": tr";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
                await Task.Delay(100);
                client.Client.LingerState = new LingerOption(true, 0);   // reset, as a dying tunnel does
                client.Close();
            });

        Assert.Equal("[http] client disconnected mid-request (normal on tunnel restart)", line);
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task A_client_that_half_closes_mid_body_is_logged_as_normal()
    {
        await using var h = await ServerHarness.StartAsync();
        var line = await ServerHarness.WaitForLogAsync(
            l => l.StartsWith("[http] client disconnected", StringComparison.Ordinal) || l.Contains("POST /now-playing"),
            async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, h.Port);
                var head = $"POST /now-playing HTTP/1.1\r\nHost: localhost\r\nX-Relay-Key: {Key}\r\n" +
                           "Content-Length: 5000\r\n\r\n{\"playing\"";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
                client.Client.Shutdown(SocketShutdown.Send);
                await Task.Delay(500);
            });

        Assert.Equal("[http] client disconnected mid-request (normal on tunnel restart)", line);
    }

    [Fact]
    public async Task An_oversized_body_is_413_without_being_read()
    {
        await using var h = await ServerHarness.StartAsync();
        var raw = await h.RawAsync($"POST /now-playing HTTP/1.1\r\nHost: localhost\r\nX-Relay-Key: {Key}\r\n" +
                                   "Content-Length: 900000000\r\nConnection: close\r\n\r\n{");
        Assert.StartsWith("HTTP/1.1 413", raw);
        Assert.Contains($"{RelayServer.RelayHeader}: {IssunInfo.Wire}\r\n", raw);
        Assert.EndsWith("too large", raw);
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task A_malformed_body_is_the_senders_400_not_a_500_with_a_trace()
    {
        await using var h = await ServerHarness.StartAsync();
        string raw = "";
        var line = await ServerHarness.WaitForLogAsync(
            l => l.Contains("POST /now-playing"),
            async () => raw = await h.RawAsync(
                $"POST /now-playing HTTP/1.1\r\nHost: localhost\r\nX-Relay-Key: {Key}\r\n" +
                "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\nZZ\r\n{}\r\n0\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 400", raw);
        Assert.Contains($"{RelayServer.RelayHeader}: {IssunInfo.Wire}\r\n", raw);
        Assert.EndsWith("bad request", raw);
        Assert.Equal("[http] 400 POST /now-playing: Bad chunk size data.", line);
        Assert.Empty(h.Checkins.Accepted);
    }

    [Fact]
    public async Task Refusals_are_logged_and_throttled_per_kind()
    {
        // relay.py logged no 401 or 404 at all, so a phone with the wrong key
        // looked, in the log, exactly like a phone that had stopped pushing.
        // Exact counting is RefusalLogTests' job; this checks the server wires
        // each refusal to it. Log.Written is process-wide, so the check is for
        // these lines in this order, not for nothing else.
        await using var h = await ServerHarness.StartAsync();
        var lines = new List<string>();
        void Collect(LogEntry e)
        {
            if (e.Text.StartsWith("[http] 40", StringComparison.Ordinal))
                lock (lines) lines.Add(e.Text);
        }

        Log.Written += Collect;
        try
        {
            await h.PostAsync("{}", key: "wrong");
            await h.PostAsync("{}", key: "wrong");
            await h.PostAsync("{}", key: "wrong");
            h.Clock.Advance(RefusalLog.WindowSeconds + 1);
            await h.PostAsync("{}", key: "wrong");
            await h.PostAsync("{}", key: null);
            await h.PostAsync("{}", key: null, path: "/");
            await h.GetAsync("/status");
            await h.PostAsync("{\"playing\": tr");
        }
        finally
        {
            Log.Written -= Collect;
        }

        string[] expected =
        [
            "[http] 401 POST /now-playing: the key did not match",
            "[http] 401 POST /now-playing: the key did not match  (+2 more like it since the last line)",
            // A missing key is a different kind from a wrong one, so it gets
            // its own line even inside the wrong key's quiet minute.
            "[http] 401 POST /now-playing: the request carried no key",
            "[http] 404 POST /: not found — the address should end in /now-playing",
            "[http] 401 GET /status: the request carried no key",
        ];
        lock (lines)
        {
            var at = 0;
            foreach (var line in lines)
                if (at < expected.Length && line == expected[at])
                    at++;
            Assert.True(at == expected.Length,
                "missing <" + (at < expected.Length ? expected[at] : "") + "> in: " + string.Join(" | ", lines));
            Assert.Contains(lines, l => l.StartsWith("[http] 400 POST /now-playing: bad json (", StringComparison.Ordinal));
        }
    }

    // ───────────────────────────── Lifecycle ────────────────────────────────────

    [Fact]
    public async Task Start_reports_listening_and_logs_it()
    {
        var config = new MutableConfig(new Settings { Key = Key });
        await using var server = new RelayServer(config, new FakeCheckins(), new FakePhoneState(),
            new FakeDiagnostics(), new FakeArtwork(), new ManualClock());
        var changes = 0;
        server.Changed += () => Interlocked.Increment(ref changes);

        Assert.False(server.Status.Listening);
        using (var capture = Log.Capture())
        {
            await server.StartAsync(0, CancellationToken.None);
            // relay.py's line, and nothing else: a clean start must not leave
            // warnings in the log for the owner to wonder about.
            Assert.Equal([$"[http] listening on 127.0.0.1:{server.Status.Port}"], capture.Lines);
        }

        Assert.True(server.Status.Listening);
        Assert.NotEqual(0, server.Status.Port);
        Assert.NotEqual(8787, server.Status.Port);
        Assert.Null(server.Status.Error);
        Assert.Equal(1, changes);

        await server.StopAsync();
        Assert.False(server.Status.Listening);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_port_in_use_throws_sets_the_error_and_logs()
    {
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var port = ((IPEndPoint)squatter.LocalEndpoint).Port;
        try
        {
            await using var server = new RelayServer(new MutableConfig(new Settings { Key = Key }), new FakeCheckins(),
                new FakePhoneState(), new FakeDiagnostics(), new FakeArtwork(), new ManualClock());
            var changed = false;
            server.Changed += () => changed = true;

            using var capture = Log.Capture();
            var ex = await Assert.ThrowsAsync<PortInUseException>(() => server.StartAsync(port, CancellationToken.None));

            Assert.Equal(port, ex.Port);
            Assert.False(server.Status.Listening);
            Assert.Equal(port, server.Status.Port);
            Assert.Equal(ex.Message, server.Status.Error);
            Assert.True(changed);
            Assert.Contains(capture.Lines, l => l.StartsWith($"[http] can't listen on 127.0.0.1:{port}: Port {port} is already in use"));
        }
        finally
        {
            squatter.Stop();
        }
    }

    /// <summary>
    /// relay.py's HTTPServer sets SO_REUSEADDR, which on Windows can let a
    /// second socket bind a port that is already listening. If the old
    /// "Ammy Relay" task started after Issun and got the port too, pushes would
    /// split between two receivers with no error anywhere. Checked against
    /// Python 3.14 directly while writing this: its bind is refused with
    /// WinError 10013. This keeps that true without needing Python.
    /// </summary>
    [Fact]
    public async Task A_socket_with_reuse_address_cannot_bind_on_top_of_issun()
    {
        await using var h = await ServerHarness.StartAsync();
        using var intruder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        intruder.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var refused = Assert.Throws<SocketException>(() => intruder.Bind(new IPEndPoint(IPAddress.Loopback, h.Port)));
        Assert.Contains(refused.SocketErrorCode, new[] { SocketError.AccessDenied, SocketError.AddressAlreadyInUse });
    }

    [Fact]
    public async Task A_port_held_with_reuse_address_is_still_port_in_use()
    {
        // The other direction: relay.py already listening, Issun starting.
        using var relayPy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        relayPy.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        relayPy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        relayPy.Listen();
        var port = ((IPEndPoint)relayPy.LocalEndPoint!).Port;

        await using var server = new RelayServer(new MutableConfig(new Settings { Key = Key }), new FakeCheckins(),
            new FakePhoneState(), new FakeDiagnostics(), new FakeArtwork(), new ManualClock());
        var ex = await Assert.ThrowsAsync<PortInUseException>(() => server.StartAsync(port, CancellationToken.None));
        Assert.Equal(port, ex.Port);
        Assert.False(server.Status.Listening);
    }

    [Fact]
    public async Task Stop_then_start_on_another_port_works()
    {
        await using var h = await ServerHarness.StartAsync();
        var first = h.Port;
        await AssertReply(await h.GetAsync("/health"), HttpStatusCode.OK, "ok");

        await h.Server.StopAsync();
        Assert.False(h.Server.Status.Listening);
        using (var stale = ServerHarness.NewClient(first))
            await Assert.ThrowsAsync<HttpRequestException>(() => stale.GetAsync("/health"));

        await h.Server.StartAsync(0, CancellationToken.None);
        Assert.True(h.Server.Status.Listening);
        using var fresh = ServerHarness.NewClient(h.Port);
        await AssertReply(await fresh.GetAsync("/health"), HttpStatusCode.OK, "ok");
    }

    [Fact]
    public async Task Starting_while_running_moves_to_the_new_port()
    {
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var port = ((IPEndPoint)target.LocalEndpoint).Port;
        target.Stop();

        await using var h = await ServerHarness.StartAsync();
        var first = h.Port;
        await h.Server.StartAsync(port, CancellationToken.None);

        Assert.Equal(port, h.Server.Status.Port);
        using var moved = ServerHarness.NewClient(port);
        await AssertReply(await moved.GetAsync("/health"), HttpStatusCode.OK, "ok");
        using var old = ServerHarness.NewClient(first);
        await Assert.ThrowsAsync<HttpRequestException>(() => old.GetAsync("/health"));
    }

    [Fact]
    public async Task Binds_loopback_only()
    {
        await using var h = await ServerHarness.StartAsync();
        var lan = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lan is null)
            return;   // no non-loopback IPv4 address to try

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(lan, h.Port));
    }
}
