using System.Collections.Concurrent;
using Issun.Core.Discord;

namespace Issun.Core.Tests.Discord;

public class PresenceWorkerTests
{
    private const string RefusalText = "child \"activity\" fails because [child \"details\" fails because [\"details\" length must be at least 2 characters long]]";

    private static readonly TrackInfo NotLikeUs = new()
    {
        Title = "Not Like Us",
        Artist = "Kendrick Lamar",
        Album = "GNX",
        Duration = 274,
        Elapsed = 30,
        StoreId = "1781270323",
    };

    private static TrackInfo Titled(string title) => NotLikeUs with { Title = title, StoreId = null };

    /// <summary>A running worker over fakes, ticking every 10 ms against a hand-moved clock.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _run;

        /// <param name="factory">The real IPC client against a fake server, say; <see cref="Discord"/> otherwise.</param>
        public Rig(string clientId = "111", PresenceWorkerOptions? options = null, Func<IDiscordClient>? factory = null)
        {
            Config = new MutableConfig(new Settings { DiscordClientId = clientId });
            Worker = new PresenceWorker(Config, factory ?? Discord.Factory, Phone, Builder, Resolver, Uptime, Clock,
                options ?? new PresenceWorkerOptions
                {
                    TickInterval = TimeSpan.FromMilliseconds(10),
                    ReconnectDelay = TimeSpan.FromMilliseconds(50),
                    LostDelay = TimeSpan.FromMilliseconds(50),
                });
            Worker.Changed += () => Statuses.Enqueue(Worker.Status);
        }

        public ManualClock Clock { get; } = new();
        public MutableConfig Config { get; }
        public FakeDiscord Discord { get; } = new();
        public FakePhoneState Phone { get; } = new();
        public FakeBuilder Builder { get; } = new();
        public FakeResolver Resolver { get; } = new();
        public FakeUptimeLog Uptime { get; } = new();
        public PresenceWorker Worker { get; }
        public ConcurrentQueue<DiscordStatus> Statuses { get; } = new();
        public Log.LogCapture? Capture { get; private set; }

        public IReadOnlyList<string> Lines => Capture!.Lines;

        public int Count(string prefix) => Lines.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

        /// <summary>Synchronous on purpose: the log capture is an AsyncLocal, and it has to be set in the flow RunAsync starts from.</summary>
        public void Start()
        {
            Capture = Log.Capture();
            _run = Worker.RunAsync(_cts.Token);
        }

        public void PlayNow(TrackInfo? track) => Phone.Push(track, Clock.Now);

        /// <summary>Waits for <paramref name="n"/> more ticks to begin — NoteSilence runs once per tick.</summary>
        public Task Ticks(int n)
        {
            var target = Uptime.SilenceChecks + n;
            return Until(() => Uptime.SilenceChecks >= target, $"{n} ticks");
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            await _run!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            if (!_cts.IsCancellationRequested)
                await StopAsync();
            Capture?.Dispose();
            _cts.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Connects_pushes_the_track_and_leaves_heartbeats_alone()
    {
        await using var rig = new Rig();
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 1, "the first push");
        await rig.Ticks(5);

        Assert.Single(rig.Discord.Sets);
        Assert.Equal("Not Like Us", rig.Discord.Sets[0]!.Details);
        Assert.Equal(rig.Discord.Sets[0], rig.Worker.Current);
        Assert.Equal(new DiscordStatus(DiscordLinkState.Connected, "Example Person", null), rig.Worker.Status);
        Assert.Equal("https://art.example/Not Like Us.jpg", rig.Worker.CurrentArtworkUrl);
        Assert.Equal("111", rig.Discord.Connects[0].ClientId);
        Assert.Contains("[rpc] connected to Discord", rig.Lines);
        Assert.Contains("[rpc] Not Like Us", rig.Lines);
        Assert.Contains(rig.Statuses, s => s.State == DiscordLinkState.Connecting);
    }

    [Fact]
    public async Task The_builder_is_given_the_readings_arrival_time_never_now()
    {
        // The rubberbanding bug: anchoring a frozen elapsed to the current time
        // slid Discord's bar back a second per second between pushes.
        await using var rig = new Rig();
        var arrived = rig.Clock.Now;
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await Until(() => rig.Discord.Sets.Count == 1, "the first push");

        for (var i = 0; i < 5; i++)
        {
            rig.Clock.Advance(5);
            await rig.Ticks(2);
        }

        Assert.All(rig.Builder.ObservedAt, at => Assert.Equal(arrived, at));
        Assert.Single(rig.Discord.Sets);
    }

    [Fact]
    public async Task Presence_clears_once_the_phone_has_been_quiet_past_the_idle_timeout()
    {
        await using var rig = new Rig();
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await Until(() => rig.Discord.Sets.Count == 1, "the first push");

        // relay.py: time.time() - updated_at > IDLE_TIMEOUT — strictly more.
        rig.Clock.Advance(Timing.IdleTimeout);
        await rig.Ticks(3);
        Assert.Single(rig.Discord.Sets);

        var resetsBefore = rig.Builder.Resets;
        rig.Clock.Advance(1);
        await Until(() => rig.Discord.Sets.Count == 2, "the clear");
        await rig.Ticks(5);

        Assert.Equal(2, rig.Discord.Sets.Count);          // cleared once, not every tick
        Assert.Null(rig.Discord.Sets[1]);
        Assert.Null(rig.Worker.Current);
        Assert.Null(rig.Worker.CurrentArtworkUrl);
        Assert.True(rig.Builder.Resets > resetsBefore);
        Assert.Contains("[rpc] cleared", rig.Lines);
    }

    [Fact]
    public async Task Rapid_changes_inside_the_minimum_push_gap_are_coalesced()
    {
        await using var rig = new Rig();
        rig.PlayNow(Titled("One"));
        rig.Start();
        await Until(() => rig.Discord.Sets.Count == 1, "the first push");

        // Skipping through a playlist: two changes, no time passing.
        rig.PlayNow(Titled("Two"));
        await rig.Ticks(4);
        rig.PlayNow(Titled("Three"));
        rig.Clock.Advance(2.5);
        await rig.Ticks(4);
        Assert.Single(rig.Discord.Sets);

        rig.Clock.Advance(0.5);
        await Until(() => rig.Discord.Sets.Count == 2, "the coalesced push");
        await rig.Ticks(4);

        Assert.Equal(2, rig.Discord.Sets.Count);
        Assert.Equal("Three", rig.Discord.Sets[1]!.Details);
        Assert.DoesNotContain("[rpc] Two", rig.Lines);
    }

    [Fact]
    public async Task A_refused_payload_is_retried_once_stripped_then_skipped_and_the_connection_kept()
    {
        await using var rig = new Rig();
        rig.Resolver.Links["1781270323"] = new CatalogLinks("https://music.example/song", "https://music.example/artist", "https://music.example/album");
        rig.Discord.OnSet = _ => throw new DiscordRejectedException(4000, RefusalText);
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 2, "the original and the stripped retry");
        await rig.Ticks(5);
        rig.Clock.Advance(30);                 // well past the push gap
        await rig.Ticks(5);

        // Two attempts and then silence — not one a tick, not one a reconnect.
        Assert.Equal(2, rig.Discord.Sets.Count);
        Assert.Equal("https://music.example/song", rig.Discord.Sets[0]!.DetailsUrl);
        Assert.Equal(PresenceWorker.Degraded(rig.Discord.Sets[0]!), rig.Discord.Sets[1]);
        Assert.Single(rig.Discord.Connects);
        Assert.False(rig.Discord.Clients[0].Disposed);
        Assert.Equal(1, rig.Count("[rpc] dropping 'details_url', 'state_url', 'large_url', 'status_display_type' ("));
        Assert.Equal(1, rig.Count($"[rpc] Discord refused the payload, skipping it ({RefusalText})"));
        Assert.Equal(0, rig.Count("[rpc] lost connection"));
        Assert.Null(rig.Worker.Current);
        Assert.Equal(DiscordLinkState.Connected, rig.Worker.Status.State);
        Assert.Contains("refused", rig.Worker.Status.Detail);

        // The next track is news again, and goes out.
        rig.PlayNow(Titled("Next"));
        await Until(() => rig.Discord.Sets.Count >= 3, "the next track");
        Assert.Equal("Next", rig.Discord.Sets[2]!.Details);
    }

    [Fact]
    public async Task When_the_stripped_payload_is_accepted_it_is_what_current_reports()
    {
        await using var rig = new Rig();
        rig.Resolver.Links["1781270323"] = new CatalogLinks("https://music.example/song", "https://music.example/artist", "https://music.example/album");
        rig.Discord.OnSet = a =>
        {
            if (a?.DetailsUrl is not null)
                throw new DiscordRejectedException(4000, "details_url is not allowed");
        };
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 2, "the stripped retry");
        await rig.Ticks(3);
        rig.Clock.Advance(30);
        await rig.Ticks(5);

        Assert.Equal(2, rig.Discord.Sets.Count);   // the intended payload counts as sent
        var current = rig.Worker.Current!;
        Assert.Equal(rig.Discord.Sets[1], current);
        Assert.Null(current.DetailsUrl);
        Assert.Null(current.LargeUrl);
        Assert.Null(current.StatusDisplayType);
        Assert.Equal("https://art.example/Not Like Us.jpg", current.LargeImage);
        Assert.NotNull(current.Start);
        Assert.Contains("[rpc] Not Like Us", rig.Lines);
        Assert.Equal(0, rig.Count("[rpc] Discord refused the payload"));
        Assert.Null(rig.Worker.Status.Detail);
    }

    [Fact]
    public async Task A_refusal_with_nothing_to_strip_is_not_retried()
    {
        await using var rig = new Rig();
        rig.Builder.DisplayType = null;
        rig.Discord.OnSet = _ => throw new DiscordRejectedException(4000, RefusalText);
        rig.PlayNow(Titled("i"));
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 1, "the push");
        await rig.Ticks(5);
        rig.Clock.Advance(30);
        await rig.Ticks(5);

        Assert.Single(rig.Discord.Sets);
        Assert.Equal(0, rig.Count("[rpc] dropping"));
        Assert.Equal(1, rig.Count("[rpc] Discord refused the payload, skipping it"));
    }

    [Fact]
    public async Task A_lost_connection_reconnects_after_the_delay_and_repushes()
    {
        await using var rig = new Rig(options: new PresenceWorkerOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
            LostDelay = TimeSpan.FromMilliseconds(300),
        });
        var failures = 1;
        rig.Discord.OnSet = _ =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
                throw new DiscordConnectionLostException(DiscordIpcClient.PipeClosedMessage);
        };
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 2, "the push after reconnecting");

        var connects = rig.Discord.Connects;
        Assert.Equal(2, connects.Count);
        Assert.True(connects[1].At - connects[0].At >= TimeSpan.FromMilliseconds(280),
            $"reconnected after {(connects[1].At - connects[0].At).TotalMilliseconds} ms");
        Assert.True(rig.Discord.Clients[0].Disposed);
        Assert.Equal(1, rig.Count("[rpc] lost connection (The pipe was closed.)"));
        Assert.Equal(rig.Discord.Sets[0], rig.Discord.Sets[1]);
        await Until(() => rig.Worker.Current is not null, "current");
        Assert.Equal(rig.Discord.Sets[1], rig.Worker.Current);
        Assert.Contains(rig.Statuses, s => s is { State: DiscordLinkState.Disconnected, Detail: PresenceWorker.LostDetail });
        Assert.Equal(DiscordLinkState.Connected, rig.Worker.Status.State);
    }

    [Fact]
    public async Task A_connection_that_keeps_dropping_is_retried_at_the_lost_delay_not_in_a_spin()
    {
        await using var rig = new Rig(options: new PresenceWorkerOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(5),
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
            LostDelay = TimeSpan.FromMilliseconds(250),
        });
        rig.Discord.OnSet = _ => throw new DiscordConnectionLostException("The pipe broke");
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Task.Delay(1000);
        var connects = rig.Discord.Connects.Count;

        Assert.InRange(connects, 2, 6);
        Assert.All(rig.Discord.Clients.SkipLast(1), c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task A_pipe_that_closes_quietly_is_noticed_and_reconnected()
    {
        await using var rig = new Rig();
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await Until(() => rig.Discord.Sets.Count == 1, "the first push");

        rig.Discord.Clients[0].Sever();
        await Until(() => rig.Discord.Connects.Count == 2, "the reconnect");

        Assert.Equal(1, rig.Count("[rpc] lost connection (Discord closed the pipe)"));
        Assert.True(rig.Discord.Clients[0].Disposed);
    }

    [Fact]
    public async Task Correcting_a_rejected_client_id_is_tried_at_once_not_after_the_retry_wait()
    {
        // Default ten-second retry: only the change itself can explain a
        // second attempt inside this test.
        await using var rig = new Rig("1234567890", new PresenceWorkerOptions { TickInterval = TimeSpan.FromMilliseconds(10) });
        rig.Discord.OnConnect = id => id == "1234567890"
            ? throw new DiscordHandshakeException(4000, DiscordIpcClient.InvalidIdMessage)
            : new DiscordUser("1", "example", null);
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await Until(() => rig.Worker.Status.Detail == PresenceWorker.InvalidIdDetail, "the invalid-ID detail");

        rig.Config.Settings = rig.Config.Settings with { DiscordClientId = "1234567891" };
        await Until(() => rig.Discord.Sets.Count == 1, "the push under the corrected ID");

        Assert.Equal(new[] { "1234567890", "1234567891" }, rig.Discord.Connects.Select(c => c.ClientId));
        Assert.Equal(1, rig.Count("[rpc] Discord application ID changed; trying the new one"));
        Assert.Equal(new DiscordStatus(DiscordLinkState.Connected, "example", null), rig.Worker.Status);
    }

    [Fact]
    public async Task Against_the_real_client_a_refusal_keeps_the_one_connection()
    {
        // The whole chain the 1.3.0 incident ran through, with the real IPC
        // client: Discord refuses, the stripped retry is refused too, and the
        // same pipe carries the next track. The fake server accepts exactly one
        // connection, so a reconnect would leave the script waiting and fail.
        var frames = new ConcurrentQueue<FakeFrame>();
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            for (var i = 0; i < 2; i++)
            {
                var refused = await c.ReadAsync(ct);
                frames.Enqueue(refused);
                await c.WriteAsync(1, FakeDiscordServer.Error(refused.Json!), ct);
            }
            var next = await c.ReadAsync(ct);
            frames.Enqueue(next);
            await c.WriteAsync(1, FakeDiscordServer.Ack(next.Json!), ct);
            frames.Enqueue(await c.ReadAsync(ct)); // CLOSE on shutdown
        });

        await using var rig = new Rig(factory: () => new DiscordIpcClient(server.Prefix, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
        rig.Resolver.Links["1781270323"] = new CatalogLinks("https://music.example/song", "https://music.example/artist", "https://music.example/album");
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Count("[rpc] Discord refused the payload, skipping it") == 1, "the final refusal");
        rig.Clock.Advance(30);
        await rig.Ticks(5);
        Assert.Equal(2, frames.Count);         // not re-sent while nothing changed

        rig.PlayNow(Titled("Next"));
        await Until(() => rig.Worker.Current?.Details == "Next", "the next track");
        await rig.StopAsync();
        await server.Completion;

        var sent = frames.ToArray();
        Assert.Equal("https://music.example/song", sent[0].Json!["args"]!["activity"]!["details_url"]!.GetValue<string>());
        Assert.Null(sent[1].Json!["args"]!["activity"]!["details_url"]);
        Assert.Equal("Next", sent[2].Json!["args"]!["activity"]!["details"]!.GetValue<string>());
        Assert.Equal(2, sent[3].Op);
        Assert.Equal(1, rig.Count("[rpc] connected to Discord"));
        Assert.Equal(0, rig.Count("[rpc] lost connection"));
        Assert.Contains($"[rpc] Discord refused the payload, skipping it ({FakeDiscordServer.ShortDetailsMessage})", rig.Lines);
    }

    [Fact]
    public async Task Changing_the_client_id_reconnects_with_the_new_one()
    {
        await using var rig = new Rig("111");
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await Until(() => rig.Discord.Sets.Count == 1, "the first push");

        rig.Config.Settings = rig.Config.Settings with { DiscordClientId = " 222 " };
        await Until(() => rig.Discord.Connects.Count == 2, "the reconnect");

        Assert.Equal("222", rig.Discord.Connects[1].ClientId);
        Assert.True(rig.Discord.Clients[0].Disposed);
        Assert.Equal(1, rig.Count("[rpc] Discord application ID changed; reconnecting"));

        // A fresh connection shows nothing, so the track goes out again once
        // the push gap allows.
        rig.Clock.Advance(Timing.MinPushGap);
        await Until(() => rig.Discord.Sets.Count == 2, "the re-push");
        Assert.Equal("Not Like Us", rig.Discord.Sets[1]!.Details);
    }

    [Fact]
    public async Task No_client_id_means_no_connection_a_clear_detail_and_one_log_line()
    {
        await using var rig = new Rig("");
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await rig.Ticks(10);

        Assert.Empty(rig.Discord.Clients);
        Assert.Equal(new DiscordStatus(DiscordLinkState.Disconnected, null, PresenceWorker.MissingIdDetail), rig.Worker.Status);
        Assert.Contains("Settings", rig.Worker.Status.Detail);
        Assert.Equal(1, rig.Count("[rpc] no Discord application ID set"));
        // The window still gets the cover.
        Assert.Equal("https://art.example/Not Like Us.jpg", rig.Worker.CurrentArtworkUrl);

        rig.Config.Settings = rig.Config.Settings with { DiscordClientId = "333" };
        await Until(() => rig.Discord.Sets.Count == 1, "the push once an ID exists");
        Assert.Equal("333", rig.Discord.Connects[0].ClientId);
    }

    [Fact]
    public async Task An_invalid_client_id_gets_its_own_detail_and_the_log_is_not_flooded()
    {
        await using var rig = new Rig();
        rig.Discord.OnConnect = _ => throw new DiscordHandshakeException(4000, DiscordIpcClient.InvalidIdMessage);
        rig.Start();

        await Until(() => rig.Discord.Connects.Count >= 5, "several attempts");

        Assert.Equal(new DiscordStatus(DiscordLinkState.Disconnected, null, PresenceWorker.InvalidIdDetail), rig.Worker.Status);
        Assert.Contains("misread digit", rig.Worker.Status.Detail);
        Assert.Equal(1, rig.Count("[rpc] Discord not reachable"));
        Assert.Contains("[rpc] Discord not reachable (Error Code: 4000 Message: Client ID is Invalid); retrying in 0.05s", rig.Lines);
        Assert.All(rig.Discord.Clients.SkipLast(1), c => Assert.True(c.Disposed));

        // Ten minutes on, still failing: one more line, saying how many were held back.
        rig.Clock.Advance(601);
        await Until(() => rig.Count("[rpc] Discord not reachable") == 2, "the reminder");
        Assert.Matches(@"^\[rpc\] Discord not reachable \(Error Code: 4000 Message: Client ID is Invalid\); retrying in 0\.05s \(repeated \d+x since \d\d:\d\d:\d\d\)$",
            rig.Lines.Last(l => l.StartsWith("[rpc] Discord not reachable", StringComparison.Ordinal)));

        // A different reason is news, and is written straight away.
        rig.Discord.OnConnect = _ => throw new DiscordUnavailableException(DiscordIpcClient.NotFoundMessage);
        await Until(() => rig.Count("[rpc] Discord not reachable") == 3, "the new reason");
        Assert.Contains("[rpc] Discord not reachable (Could not find Discord installed and running on this machine.); retrying in 0.05s", rig.Lines);
        await Until(() => rig.Worker.Status.Detail == PresenceWorker.NotRunningDetail, "the not-running detail");
    }

    [Fact]
    public async Task The_default_retry_wording_matches_relay_logs()
    {
        await using var rig = new Rig(options: new PresenceWorkerOptions { TickInterval = TimeSpan.FromMilliseconds(10) });
        rig.Discord.OnConnect = _ => throw new DiscordUnavailableException(DiscordIpcClient.NotFoundMessage);
        rig.Start();
        await rig.Ticks(20);

        Assert.Single(rig.Discord.Connects);   // the next attempt is ten real seconds away
        Assert.Equal(new[] { "[rpc] Discord not reachable (Could not find Discord installed and running on this machine.); retrying in 10s" },
            rig.Lines.Where(l => l.StartsWith("[rpc] Discord", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Artwork_and_silence_checks_carry_on_while_discord_is_away()
    {
        await using var rig = new Rig();
        rig.Discord.OnConnect = _ => throw new DiscordUnavailableException(DiscordIpcClient.NotFoundMessage);
        var changes = 0;
        rig.Worker.Changed += () => Interlocked.Increment(ref changes);
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Worker.CurrentArtworkUrl is not null, "the artwork");
        await rig.Ticks(3);

        Assert.Equal("https://art.example/Not Like Us.jpg", rig.Worker.CurrentArtworkUrl);
        Assert.True(Volatile.Read(ref changes) > 0);
        Assert.Empty(rig.Discord.Sets);
        Assert.Equal(rig.Phone.LastCheckinAt, rig.Uptime.LastSeen.Last());
        Assert.Equal(DiscordLinkState.Disconnected, rig.Worker.Status.State);
        Assert.Equal(PresenceWorker.NotRunningDetail, rig.Worker.Status.Detail);
    }

    [Fact]
    public async Task A_new_track_with_the_same_cover_still_tells_the_window()
    {
        // The next song from the same album has the same cover URL, but its
        // lookup settles whether the window's title gets an E. With Discord
        // closed, this Changed is the only prompt the window gets before its
        // five-second heartbeat.
        await using var rig = new Rig();
        rig.Discord.OnConnect = _ => throw new DiscordUnavailableException(DiscordIpcClient.NotFoundMessage);
        rig.Resolver.Answer = _ => new ArtworkResult("https://art.example/album.jpg", "Album", ArtworkSource.StoreId);
        var changes = 0;
        rig.Worker.Changed += () => Interlocked.Increment(ref changes);
        rig.PlayNow(Titled("First"));
        rig.Start();
        await Until(() => rig.Worker.CurrentArtworkUrl is not null, "the first track's cover");
        await rig.Ticks(3);

        var settled = Volatile.Read(ref changes);
        await rig.Ticks(3);
        Assert.Equal(settled, Volatile.Read(ref changes));   // the same track again says nothing

        rig.PlayNow(Titled("Second"));
        await rig.Ticks(3);

        Assert.Equal("https://art.example/album.jpg", rig.Worker.CurrentArtworkUrl);
        Assert.True(Volatile.Read(ref changes) > settled, "no Changed after the second track resolved");
    }

    [Fact]
    public async Task An_unexpected_error_is_logged_and_the_worker_keeps_going()
    {
        await using var rig = new Rig();
        rig.Builder.FailNextBuild = new InvalidOperationException("boom");
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 1, "the push after the error");
        Assert.Contains("[rpc] worker error: InvalidOperationException: boom", rig.Lines);
    }

    [Fact]
    public async Task A_throwing_status_listener_does_not_stop_the_worker()
    {
        await using var rig = new Rig();
        rig.Worker.Changed += () => throw new InvalidOperationException("listener broke");
        rig.PlayNow(NotLikeUs);
        rig.Start();

        await Until(() => rig.Discord.Sets.Count == 1, "the push");
        Assert.Contains(rig.Lines, l => l.StartsWith("[rpc] a status listener failed (InvalidOperationException: listener broke)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_returns_and_closes_the_connection_which_clears_presence()
    {
        var rig = new Rig();
        rig.PlayNow(NotLikeUs);
        rig.Start();
        await Until(() => rig.Discord.Sets.Count == 1, "the first push");

        await rig.StopAsync();   // returns rather than throwing

        Assert.True(rig.Discord.Clients[0].Disposed);
        Assert.Null(rig.Worker.Current);
        Assert.Equal(DiscordLinkState.Disconnected, rig.Worker.Status.State);
        Assert.Contains("[rpc] disconnecting from Discord (shutting down)", rig.Lines);
        await rig.DisposeAsync();
    }
}
