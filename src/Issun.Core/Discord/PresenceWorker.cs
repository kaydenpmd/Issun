using System.Diagnostics;
using System.Globalization;

namespace Issun.Core.Discord;

/// <summary>How often the worker does things. The defaults are relay.py's; tests shrink them.</summary>
public sealed record PresenceWorkerOptions
{
    /// <summary>One pass of the loop — relay.py's <c>time.sleep(1)</c> at the bottom of rpc_worker.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>After a failed connect: "Discord not reachable (...); retrying in 10s".</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// After a lost connection, before reconnecting. Never zero in practice:
    /// relay.py's "never spin: reconnecting has a real cost". The 1.3.0 incident
    /// — a refused payload read as a dead socket — reconnected and re-sent in a
    /// tight loop until the song changed.
    /// </summary>
    public TimeSpan LostDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long an unchanged failure stays out of the log. relay.py wrote
    /// "Discord not reachable" every 10 s for as long as Discord was closed.
    /// </summary>
    public TimeSpan RepeatLogInterval { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// relay.py's rpc_worker: owns the Discord connection and turns the phone's
/// latest reading into Rich Presence, once a tick.
///
/// Everything the loop owns — the client, what was last sent, when — is
/// touched only by <see cref="RunAsync"/>'s own flow. What other threads read
/// (<see cref="Status"/>, <see cref="Current"/>, <see cref="CurrentArtworkUrl"/>)
/// is an immutable value swapped in whole.
/// </summary>
public sealed class PresenceWorker : IPresenceWorker
{
    internal const string MissingIdDetail =
        "Set the Discord application ID in Settings. Presence stays off until there is one.";

    // The 4000 message reads like a deleted application. It has been a single
    // misread digit, copied from a screenshot (Ammy's CLAUDE.md, "Don't
    // transcribe config from screenshots").
    internal const string InvalidIdDetail =
        "Discord says the application ID is invalid. Check it for a misread digit: "
        + "copy it from the Discord Developer Portal rather than retyping it.";

    internal const string NotRunningDetail =
        "Discord isn't running. Open the Discord desktop app and Issun will connect by itself.";

    internal const string LostDetail = "Lost the connection to Discord. Reconnecting.";

    private readonly IConfig _config;
    private readonly Func<IDiscordClient> _clientFactory;
    private readonly IPhoneState _state;
    private readonly IActivityBuilder _builder;
    private readonly IArtworkResolver _resolver;
    private readonly IUptimeLog _uptime;
    private readonly IClock _clock;
    private readonly PresenceWorkerOptions _options;

    private readonly RepeatGate _connectFailures;
    private readonly RepeatGate _workerErrors;

    // Published to other threads.
    private DiscordStatus _status = new(DiscordLinkState.Disconnected, null, null);
    private DiscordActivity? _current;
    private string? _artworkUrl;
    private int _running;

    // Owned by the loop.
    private IDiscordClient? _client;
    private string? _user;
    private string? _seenClientId;
    private bool _failing;
    private bool _missingIdLogged;
    // Stopwatch timestamps. The waits between attempts are real time, like
    // relay.py's sleeps; the push gap is IClock time, like its time.time().
    private long _nextAttemptAt;
    private DiscordActivity? _lastIntended;
    private double _lastPush;

    public PresenceWorker(
        IConfig config,
        Func<IDiscordClient> clientFactory,
        IPhoneState state,
        IActivityBuilder builder,
        IArtworkResolver resolver,
        IUptimeLog uptime,
        IClock clock,
        PresenceWorkerOptions? options = null)
    {
        _config = config;
        _clientFactory = clientFactory;
        _state = state;
        _builder = builder;
        _resolver = resolver;
        _uptime = uptime;
        _clock = clock;
        _options = options ?? new PresenceWorkerOptions();
        _connectFailures = new RepeatGate(_options.RepeatLogInterval.TotalSeconds);
        _workerErrors = new RepeatGate(_options.RepeatLogInterval.TotalSeconds);
    }

    public event Action? Changed;

    public DiscordStatus Status => Volatile.Read(ref _status);

    public DiscordActivity? Current => Volatile.Read(ref _current);

    public DiscordActivity? Intended => Volatile.Read(ref _intended);
    private DiscordActivity? _intended;

    public string? CurrentArtworkUrl => Volatile.Read(ref _artworkUrl);

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("The presence worker is already running.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await StepAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // relay.py had no handler out here. Anything build_payload
                    // raised — float() of a duration that wasn't a number, a
                    // disk error writing uploaded art — ended the worker
                    // thread, and presence stopped for good while the HTTP
                    // side went on accepting pushes as if nothing had happened.
                    // Held to one line per ten minutes while it repeats: a
                    // tick-rate failure would otherwise write 3,600 an hour.
                    var text = $"{ex.GetType().Name}: {ex.Message}";
                    if (_workerErrors.ShouldLog(text, _clock.Now, out var held, out var since))
                        Log.Write($"[rpc] worker error: {text}{HeldSuffix(held, since)}");
                }

                try
                {
                    await Task.Delay(_options.TickInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            // Closing the IPC connection is what clears presence: Discord drops
            // an application's activity when its connection goes.
            if (_client is not null)
                Log.Write("[rpc] disconnecting from Discord (shutting down)");
            await DropClientAsync().ConfigureAwait(false);
            SetStatus(new DiscordStatus(DiscordLinkState.Disconnected, null, null));
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var clientId = (_config.Settings.DiscordClientId ?? "").Trim();
        await KeepConnectedAsync(clientId, ct).ConfigureAwait(false);

        var payload = await ObserveAsync(ct).ConfigureAwait(false);
        SetIntended(payload);

        if (_client is not null
            && _builder.MateriallyDifferent(payload, _lastIntended)
            && _clock.Now - _lastPush >= Timing.MinPushGap)
        {
            await PushAsync(_client, payload, ct).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────── connection ───────────────────────────────

    private async Task KeepConnectedAsync(string clientId, CancellationToken ct)
    {
        // An ID edited in Settings applies now. relay.py read DISCORD_CLIENT_ID
        // once at import and needed a restart; the window has to do better.
        if (clientId != _seenClientId)
        {
            if (_seenClientId is { Length: > 0 })
                Log.Write((clientId.Length > 0, _client is not null) switch
                {
                    (true, true) => "[rpc] Discord application ID changed; reconnecting",
                    (true, false) => "[rpc] Discord application ID changed; trying the new one",
                    (false, true) => "[rpc] Discord application ID removed; disconnecting",
                    (false, false) => "[rpc] Discord application ID removed",
                });
            if (_client is not null)
                await DropClientAsync().ConfigureAwait(false);
            _seenClientId = clientId;
            _nextAttemptAt = 0;          // a deliberate change skips the retry wait
            _failing = false;
        }

        // Discord quitting while nothing was being sent. relay.py only found
        // out on its next push, which for a paused phone could be hours; until
        // then it believed it was connected. The client can tell sooner.
        if (_client is { IsConnected: false })
            await LoseConnectionAsync("Discord closed the pipe").ConfigureAwait(false);

        if (_client is null)
            await TryConnectAsync(clientId, ct).ConfigureAwait(false);
    }

    private async Task TryConnectAsync(string clientId, CancellationToken ct)
    {
        if (clientId.Length == 0)
        {
            // relay.py refused to start without one ("Set DISCORD_CLIENT_ID").
            // Issun has a window to say it in instead, and keeps looking.
            if (!_missingIdLogged)
            {
                Log.Write("[rpc] no Discord application ID set; presence is off until one is entered in Settings");
                _missingIdLogged = true;
            }
            SetStatus(new DiscordStatus(DiscordLinkState.Disconnected, null, MissingIdDetail));
            return;
        }
        _missingIdLogged = false;

        if (Stopwatch.GetTimestamp() < _nextAttemptAt)
            return;

        // Only the first attempt of a run of failures shows Connecting; after
        // that the reason stays on screen rather than flickering every retry.
        if (!_failing)
            SetStatus(new DiscordStatus(DiscordLinkState.Connecting, null, null));

        var client = _clientFactory();
        try
        {
            var user = await client.ConnectAsync(clientId, ct).ConfigureAwait(false);
            _client = client;
            _user = user is null ? null : user.GlobalName is { Length: > 0 } g ? g : user.Username;
            _failing = false;
            _connectFailures.Reset();
            // A fresh connection shows nothing, so whatever the phone says now
            // is news — relay.py's last_payload = None on connect.
            _lastIntended = null;
            Log.Write("[rpc] connected to Discord");
            SetStatus(new DiscordStatus(DiscordLinkState.Connected, _user, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            _failing = true;
            _nextAttemptAt = Deadline(_options.ReconnectDelay);

            // Keyed on the ID as well as the reason, so an ID corrected in
            // Settings that fails the same way still gets its own line.
            if (_connectFailures.ShouldLog(clientId + "\n" + ex.Message, _clock.Now, out var held, out var since))
                Log.Write(string.Create(CultureInfo.InvariantCulture,
                    $"[rpc] Discord not reachable ({ex.Message}); retrying in {_options.ReconnectDelay.TotalSeconds:0.###}s")
                    + HeldSuffix(held, since));

            SetStatus(new DiscordStatus(DiscordLinkState.Disconnected, null, DetailFor(ex)));
        }
    }

    private static long Deadline(TimeSpan delay) =>
        Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);

    private static string DetailFor(Exception ex) => ex switch
    {
        DiscordHandshakeException { Code: 4000 } => InvalidIdDetail,
        DiscordHandshakeException h => $"Discord refused the connection: {h.Message}",
        DiscordUnavailableException u when u.Message == DiscordIpcClient.NotFoundMessage => NotRunningDetail,
        _ => $"Couldn't reach Discord: {ex.Message}",
    };

    private async Task LoseConnectionAsync(string reason)
    {
        Log.Write($"[rpc] lost connection ({reason})");
        await DropClientAsync().ConfigureAwait(false);
        SetStatus(new DiscordStatus(DiscordLinkState.Disconnected, null, LostDetail));
        _failing = false;
        _nextAttemptAt = Deadline(_options.LostDelay);
    }

    private async Task DropClientAsync()
    {
        var client = _client;
        _client = null;
        _user = null;
        _lastIntended = null;
        SetCurrent(null);
        if (client is not null)
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
    }

    private static async Task DisposeQuietlyAsync(IDiscordClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write($"[rpc] closing the Discord connection failed ({ex.Message})");
        }
    }

    // ──────────────────────────────── the tick ────────────────────────────────

    /// <summary>
    /// What Discord should be showing right now, or null to clear. Runs whether
    /// or not Discord is connected: the window wants the cover either way, the
    /// playhead anchor stays current, and a phone that goes silent while Discord
    /// is closed still gets its line in the uptime log.
    /// </summary>
    private async Task<DiscordActivity?> ObserveAsync(CancellationToken ct)
    {
        // updatedAt is when this reading *arrived*. It goes to the builder as
        // observedAt and nothing here may substitute the current time for it:
        // build_payload runs every second against a reading the phone refreshes
        // every thirty, and anchoring that frozen elapsed to now slid Discord's
        // bar a second behind per second — the rubberbanding that was the
        // project's most expensive bug.
        var (track, updatedAt) = _state.Get();
        _uptime.NoteSilence(_state.LastCheckinAt);

        // The phone stopped reporting — force-quit, or evicted in the background.
        if (track is not null && _clock.Now - updatedAt > Timing.IdleTimeout)
            track = null;

        if (track is null)
        {
            // A pause of unknown length invalidates the anchor; Discord would
            // otherwise keep ticking through it.
            _builder.Reset();
            SetArtwork(null);
            return null;
        }

        var art = await _resolver.ResolveAsync(track, ct).ConfigureAwait(false);
        SetArtwork(art.Url);

        // Links only ever come from an exact store-ID lookup. A near-miss cover
        // is cosmetic; a link that opens the wrong song is a broken promise.
        var storeId = TrackText.StoreId(track);
        var links = storeId is null ? null : _resolver.CachedLinks(storeId);

        return _builder.Build(track, updatedAt, art, links);
    }

    private async Task PushAsync(IDiscordClient client, DiscordActivity? payload, CancellationToken ct)
    {
        try
        {
            var accepted = await SendWithFallbackAsync(client, payload, ct).ConfigureAwait(false);
            _lastIntended = payload;
            _lastPush = _clock.Now;
            SetCurrent(accepted);
            Log.Write(accepted is null ? "[rpc] cleared" : $"[rpc] {accepted.Details}");
            if (Status.Detail is not null)
                SetStatus(new DiscordStatus(DiscordLinkState.Connected, _user, null));
        }
        catch (DiscordRejectedException ex)
        {
            // The socket is fine; Discord refused this activity. Tearing the
            // connection down would reconnect and re-send the same payload
            // forever with presence empty the whole time — which is exactly what
            // happened on "i" by Kendrick Lamar, until the song changed. Record
            // the intended payload as sent so the next tick moves on instead.
            //
            // Don't fold this into the clause below. Two except clauses in
            // relay.py collapsed into one was the whole of that bug.
            Log.Write($"[rpc] Discord refused the payload, skipping it ({ex.Message})");
            _lastIntended = payload;
            _lastPush = _clock.Now;
            SetStatus(new DiscordStatus(DiscordLinkState.Connected, _user, $"Discord refused the last update: {ex.Message}"));
        }
        catch (Exception ex) when (ex is DiscordConnectionLostException or IOException)
        {
            await LoseConnectionAsync(ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected, and left to the worker-error handler to report. The
            // stamp paces the retry at MIN_PUSH_GAP instead of once a tick.
            _lastPush = _clock.Now;
            throw;
        }
    }

    /// <summary>
    /// The analogue of relay.py's push_with_fallback: a refusal is a payload
    /// problem, not a dead socket, so shed the optional extras and try once
    /// more rather than lose presence altogether. Returns what Discord took.
    ///
    /// push_with_fallback shed keyword arguments pypresence didn't know, one at
    /// a time, which only ever happens on a library version mismatch. The IPC
    /// client has no keywords to mismatch; what it can meet is Discord refusing
    /// a field, so the retry hangs off the refusal instead. Title, artist,
    /// cover and progress bar survive it: the links, the album line and the
    /// member-list choice are what go.
    /// </summary>
    private static async Task<DiscordActivity?> SendWithFallbackAsync(IDiscordClient client, DiscordActivity? payload, CancellationToken ct)
    {
        try
        {
            await client.SetActivityAsync(payload, ct).ConfigureAwait(false);
            return payload;
        }
        catch (DiscordRejectedException ex) when (payload is not null && Degraded(payload) != payload)
        {
            var degraded = Degraded(payload);
            Log.Write($"[rpc] dropping {DroppedFields(payload)} ({ex.Message})");
            // A second refusal propagates as DiscordRejectedException and is
            // skipped by the caller; a lost pipe propagates as that.
            await client.SetActivityAsync(degraded, ct).ConfigureAwait(false);
            return degraded;
        }
    }

    internal static DiscordActivity Degraded(DiscordActivity a) => a with
    {
        DetailsUrl = null,
        StateUrl = null,
        LargeUrl = null,
        LargeText = null,
        StatusDisplayType = null,
    };

    private static string DroppedFields(DiscordActivity a)
    {
        var names = new List<string>();
        if (a.DetailsUrl is not null) names.Add("'details_url'");
        if (a.StateUrl is not null) names.Add("'state_url'");
        if (a.LargeUrl is not null) names.Add("'large_url'");
        if (a.LargeText is not null) names.Add("'large_text'");
        if (a.StatusDisplayType is not null) names.Add("'status_display_type'");
        return string.Join(", ", names);
    }

    // ─────────────────────────────── publishing ───────────────────────────────

    /// <summary>Appended when a <see cref="RepeatGate"/> held identical lines back, e.g. " (repeated 59x since 14:02:11)".</summary>
    private static string HeldSuffix(int held, double since) => held <= 0
        ? ""
        : string.Create(CultureInfo.InvariantCulture, $" (repeated {held}x since {UnixTime.ToLocal(since):HH:mm:ss})");

    private void SetStatus(DiscordStatus status)
    {
        if (Interlocked.Exchange(ref _status, status) != status)
            RaiseChanged();
    }

    private void SetCurrent(DiscordActivity? activity)
    {
        if (Interlocked.Exchange(ref _current, activity) != activity)
            RaiseChanged();
    }

    /// <summary>
    /// Rebuilt every tick, but value-equal between pushes because the playhead
    /// anchor holds still, so Changed fires only when something the window
    /// shows actually moved.
    /// </summary>
    private void SetIntended(DiscordActivity? activity)
    {
        var previous = Interlocked.Exchange(ref _intended, activity);
        if (!Equals(previous, activity))
            RaiseChanged();
    }

    private void SetArtwork(string? url)
    {
        if (Interlocked.Exchange(ref _artworkUrl, url) != url)
            RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log.Write($"[rpc] a status listener failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }
}
