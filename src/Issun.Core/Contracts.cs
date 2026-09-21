using System.Text.Json.Nodes;

namespace Issun.Core;

// The seams between Issun's modules. Each module implements its interfaces in
// its own folder under src/Issun.Core; the host (IssunHost) wires them
// together. Behaviour is ported from bridge/relay.py in the Ammy repo, and the
// doc comments below name the relay.py function each member replaces.
//
// Every implementation must be thread-safe: HTTP requests arrive on Kestrel's
// threads, the presence worker runs on its own loop, and the window reads
// snapshots from the UI thread.

// ───────────────────────── Phone state & check-ins ─────────────────────────

public interface IPhoneState
{
    /// <summary>
    /// relay.py's <c>state.get()</c>: the track Discord should show (null when
    /// the phone said it isn't playing) and when that reading *arrived*. That
    /// arrival time is the <c>observed_at</c> the playhead anchors to — never
    /// the current time. See "The playhead anchor is paired with the push's
    /// arrival time" in Ammy's CLAUDE.md; it was the project's most expensive bug.
    /// </summary>
    (TrackInfo? Track, double UpdatedAt) Get();

    /// <summary>
    /// When the phone last checked in with an authorised push, whether that push
    /// was applied or dropped as out of order. 0 = never. Gap and silence
    /// detection measure this.
    /// </summary>
    double LastCheckinAt { get; }
}

public sealed record CheckinResult(bool Applied, string? DropReason);

public interface ICheckinProcessor
{
    /// <summary>
    /// Everything relay.py's do_POST did after authentication and JSON parsing:
    /// record the app version and diag snapshot, apply the track unless its seq
    /// says it is stale, and log any gap that just ended.
    ///
    /// Atomic with respect to other calls. relay.py was not, and on 14 Sept 2026
    /// two near-simultaneous pushes both read the same pre-gap "previous"
    /// check-in, so one 38-minute gap was logged twice — the second time with a
    /// verdict computed against the first push's uptime ("app restarted 3358s ->
    /// 3359s — it died"), which was wrong.
    /// </summary>
    CheckinResult Accept(NowPlayingPush push);
}

public interface IPhoneDiagnostics
{
    /// <summary>The Ammy build reported by app_version, clipped to 32 chars; "unknown" until one arrives.</summary>
    string PhoneVersion { get; }

    /// <summary>The most recent diag snapshot; null before the first.</summary>
    JsonObject? Latest { get; }

    /// <summary>When <see cref="Latest"/> arrived, unix seconds; 0 before the first.</summary>
    double LatestAt { get; }

    /// <summary>relay.py's <c>_diag_summary()</c>, same format character for character.</summary>
    string Summary();
}

public interface IUptimeLog
{
    string FilePath { get; }

    /// <summary>
    /// relay.py's <c>_log_line()</c>: "&lt;local ISO time&gt;  &lt;text&gt;  [issun 0.1.0 (12) / app 1.0 (80)]"
    /// appended to the file, and echoed to <see cref="Log"/> as "[uptime] &lt;text&gt;  &lt;tags&gt;".
    /// </summary>
    void Line(string text);

    /// <summary>relay.py's <c>note_silence()</c>. Called once per presence-worker tick with <see cref="IPhoneState.LastCheckinAt"/>.</summary>
    void NoteSilence(double lastSeen);

    /// <summary>relay.py's <c>print_summary()</c>, returned as text.</summary>
    string Summarize();
}

// ───────────────────────────────── Artwork ─────────────────────────────────

public enum ArtworkSource { None, StoreId, Uploaded, Search }

public sealed record ArtworkResult(string? Url, string MatchedAlbum, ArtworkSource Source)
{
    public static readonly ArtworkResult None = new(null, "", ArtworkSource.None);
}

/// <summary>Apple Music URLs from an exact store-ID lookup. Never from fuzzy search.</summary>
public sealed record CatalogLinks(string? Song, string? Artist, string? Album);

public interface IArtworkResolver
{
    /// <summary>
    /// build_payload's artwork chain, best first: exact store-ID lookup →
    /// JPEG uploaded with this push → JPEG uploaded earlier for this track →
    /// fuzzy iTunes Search. Caches every answer, negative ones included, the way
    /// relay.py's _artwork_cache did. Logs every failure path. Never throws.
    /// </summary>
    Task<ArtworkResult> ResolveAsync(TrackInfo track, CancellationToken ct);

    /// <summary>Links from a store-ID lookup that has already run; null otherwise. Never performs a lookup.</summary>
    CatalogLinks? CachedLinks(string storeId);

    /// <summary>
    /// Cover URL from a store-ID lookup that has already run; null otherwise.
    /// Never performs a lookup: GET /now-playing uses this, and a public GET must
    /// not be a way to make this machine issue outbound traffic.
    /// </summary>
    string? CachedArtwork(string storeId);

    /// <summary>
    /// Absolute path for GET /art/&lt;name&gt;, or null unless <paramref name="name"/>
    /// is a .jpg directly inside the art cache and the file exists.
    /// </summary>
    string? UploadedArtPath(string name);
}

// ───────────────────────────────── Discord ─────────────────────────────────

/// <summary>
/// One Rich Presence activity — the fields relay.py handed to pypresence's
/// update(). Serialised onto the IPC wire by <see cref="IDiscordClient"/>.
/// </summary>
public sealed record DiscordActivity
{
    public required string Details { get; init; }
    public required string State { get; init; }

    /// <summary>2 = Listening, which renders "Listening to &lt;application name&gt;".</summary>
    public int? Type { get; init; }

    /// <summary>0 = name, 1 = state, 2 = details: the line the compact member list shows.</summary>
    public int? StatusDisplayType { get; init; }

    /// <summary>Unix seconds, as relay.py sent them.</summary>
    public long? Start { get; init; }
    public long? End { get; init; }

    public string? LargeImage { get; init; }
    public string? LargeText { get; init; }
    public string? LargeUrl { get; init; }
    public string? DetailsUrl { get; init; }
    public string? StateUrl { get; init; }
}

public sealed record DiscordUser(string Id, string Username, string? GlobalName);

public class DiscordException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>No discord-ipc pipe to open: the Discord desktop app isn't running.</summary>
public sealed class DiscordUnavailableException(string message) : DiscordException(message);

/// <summary>Discord closed the handshake — e.g. 4000 "Invalid Client ID", which reads like a deleted application but is usually a typo.</summary>
public sealed class DiscordHandshakeException(int code, string message) : DiscordException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Discord answered SET_ACTIVITY with an ERROR. The connection is fine; this
/// payload was refused. **Not a dead socket** — tearing the connection down
/// re-sends the same payload forever. See "A rejected payload is not a dead
/// socket" in Ammy's CLAUDE.md.
/// </summary>
public sealed class DiscordRejectedException(int code, string message) : DiscordException(message)
{
    public int Code { get; } = code;
}

/// <summary>The pipe broke or closed. Reconnect.</summary>
public sealed class DiscordConnectionLostException(string message, Exception? inner = null) : DiscordException(message, inner);

public interface IDiscordClient : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// Opens the first of \\.\pipe\discord-ipc-0..9 that answers and handshakes.
    /// Throws <see cref="DiscordUnavailableException"/> or <see cref="DiscordHandshakeException"/>.
    /// </summary>
    Task<DiscordUser?> ConnectAsync(string clientId, CancellationToken ct);

    /// <summary>
    /// SET_ACTIVITY; null clears. Throws <see cref="DiscordRejectedException"/> when
    /// Discord refuses the payload and <see cref="DiscordConnectionLostException"/>
    /// when the pipe is gone. Those two must stay distinct.
    /// </summary>
    Task SetActivityAsync(DiscordActivity? activity, CancellationToken ct);
}

public interface IActivityBuilder
{
    /// <summary>
    /// build_payload() minus the artwork lookup, which the caller has already
    /// done. <paramref name="observedAt"/> is when the reading arrived from the
    /// phone — <see cref="IPhoneState.Get"/>'s UpdatedAt — never now.
    /// </summary>
    DiscordActivity Build(TrackInfo track, double observedAt, ArtworkResult art, CatalogLinks? links);

    /// <summary>playhead.reset(): playback stopped, so the anchor is invalid.</summary>
    void Reset();

    /// <summary>_materially_different(): Start/End within 2 s of each other count as equal.</summary>
    bool MateriallyDifferent(DiscordActivity? next, DiscordActivity? previous);
}

public enum DiscordLinkState { Disconnected, Connecting, Connected }

/// <param name="User">Display name of the Discord account, when connected.</param>
/// <param name="Detail">Why it isn't connected, or the last refusal — shown in the window as-is.</param>
public sealed record DiscordStatus(DiscordLinkState State, string? User, string? Detail);

public interface IPresenceWorker
{
    /// <summary>Raised on the worker's thread whenever Status, Current or CurrentArtworkUrl changes.</summary>
    event Action? Changed;

    DiscordStatus Status { get; }

    /// <summary>The activity Discord last accepted; null when cleared or never set.</summary>
    DiscordActivity? Current { get; }

    /// <summary>
    /// Cover for the phone's current track, resolved even while Discord is
    /// unavailable so the window can show it. Null when none.
    /// </summary>
    string? CurrentArtworkUrl { get; }

    /// <summary>relay.py's rpc_worker loop. Returns when cancelled; logs and survives everything else.</summary>
    Task RunAsync(CancellationToken ct);
}

// ────────────────────────────────── Server ─────────────────────────────────

public sealed record ServerStatus(bool Listening, int Port, string? Error);

public sealed class PortInUseException(int port, Exception? inner = null)
    : Exception($"Port {port} is already in use — is relay.py (the \"Ammy Relay\" task) still running?", inner)
{
    public int Port { get; } = port;
}

public interface IRelayServer : IAsyncDisposable
{
    event Action? Changed;

    ServerStatus Status { get; }

    /// <summary>Binds 127.0.0.1 only — the tunnel is the sole ingress. Throws <see cref="PortInUseException"/>.</summary>
    Task StartAsync(int port, CancellationToken ct);

    Task StopAsync();
}

// ───────────────────────────────── Platform ────────────────────────────────

/// <param name="Installed">tailscale.exe was found.</param>
/// <param name="Running">The daemon answered and this machine is logged in.</param>
/// <param name="DnsName">This machine's MagicDNS name, without the trailing dot.</param>
/// <param name="FunnelOn">Funnel is publishing something from this machine.</param>
/// <param name="FunnelTargetsPort">...and it proxies to localhost on Issun's port.</param>
/// <param name="Detail">What to tell the person when something is missing.</param>
public sealed record TailscaleStatus(
    bool Installed, bool Running, string? DnsName, bool FunnelOn, bool FunnelTargetsPort, string? Detail)
{
    public string? PublicBase => DnsName is { Length: > 0 } ? "https://" + DnsName.TrimEnd('.') : null;
}

public interface ITailscale
{
    Task<TailscaleStatus> ProbeAsync(int port, CancellationToken ct);

    /// <summary>
    /// <c>tailscale funnel --bg --https=443 localhost:&lt;port&gt;</c>. --bg is
    /// load-bearing: without it Funnel dies with the shell and does not resume
    /// after a reboot. Only ever run because the person clicked a button.
    /// </summary>
    Task<(bool Ok, string Output)> EnableFunnelAsync(int port, CancellationToken ct);
}

public interface IAutostart
{
    bool IsEnabled { get; }

    /// <summary>Registers the running .exe to start at logon, in the tray.</summary>
    void Enable();

    void Disable();

    /// <summary>If enabled but pointing at a different path (the .exe moved), repoint it at this one.</summary>
    void RepairIfMoved();
}

/// <param name="Settings">The imported settings, merged over the current ones. Not yet saved.</param>
/// <param name="Notes">One line per thing imported, skipped or worth knowing — shown to the person.</param>
/// <param name="UptimeLogImportedFrom">Where an existing ammy-uptime.log was copied from, if one was.</param>
public sealed record EnvImportResult(Settings Settings, IReadOnlyList<string> Notes, string? UptimeLogImportedFrom);

// ─────────────────────────────────── Host ──────────────────────────────────

/// <summary>Everything the window shows, as one immutable read.</summary>
public sealed record HostSnapshot
{
    /// <summary>The phone's current track; null when it isn't playing or has gone quiet past the idle timeout.</summary>
    public TrackInfo? Track { get; init; }

    /// <summary>When that reading arrived. Elapsed now ≈ Track.Elapsed + (now − this), capped at Duration.</summary>
    public double TrackObservedAt { get; init; }

    public string? ArtworkUrl { get; init; }

    /// <summary>What Discord is actually showing, per the worker.</summary>
    public DiscordActivity? Activity { get; init; }

    public double LastCheckinAt { get; init; }

    /// <summary>The phone had been checking in and has now been quiet for more than 90 s.</summary>
    public bool PhoneSilent { get; init; }

    public string PhoneVersion { get; init; } = "unknown";
    public string DiagSummary { get; init; } = "";

    public DiscordStatus Discord { get; init; } = new(DiscordLinkState.Disconnected, null, null);
    public ServerStatus Server { get; init; } = new(false, 8787, null);
    public TailscaleStatus? Tailscale { get; init; }

    /// <summary>The effective PUBLIC_BASE, "" when unknown.</summary>
    public string PublicBase { get; init; } = "";

    /// <summary>What goes in Ammy's Endpoint field.</summary>
    public string? EndpointUrl => PublicBase.Length > 0 ? PublicBase + "/now-playing" : null;
}

public interface IIssunHost : IAsyncDisposable
{
    /// <summary>Raised on any thread when anything in <see cref="Snapshot"/> may have changed.</summary>
    event Action? Changed;

    HostSnapshot Snapshot { get; }

    Settings Settings { get; }

    /// <summary>No settings existed before this run; a key was just generated.</summary>
    bool FirstRun { get; }

    Task StartAsync(CancellationToken ct = default);

    /// <summary>Saves, then applies: a new port restarts the server, a new client ID reconnects Discord.</summary>
    Task UpdateSettingsAsync(Settings updated);

    /// <summary>Replaces the key and returns it. Ammy stops being accepted until its Key field is updated.</summary>
    Task<string> RegenerateKeyAsync();

    /// <summary>Reads a relay.py .env and applies it, carrying ammy-uptime.log over if one sits beside it.</summary>
    Task<EnvImportResult> ImportEnvAsync(string envPath);

    Task RefreshTailscaleAsync();

    Task<(bool Ok, string Output)> EnableFunnelAsync();

    bool StartWithWindows { get; set; }

    string UptimeSummary();

    /// <summary>%LOCALAPPDATA%\Issun, for an "Open log folder" button.</summary>
    string DataFolder { get; }
}
