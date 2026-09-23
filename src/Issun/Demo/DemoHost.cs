using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using Issun.Core;

namespace Issun.Demo;

/// <summary>
/// A pretend host for --demo and --screenshot: made-up tracks, a phone that
/// checks in every five seconds, a Discord connection that drops and comes
/// back, and a phone that goes quiet once per loop — every state the window
/// has to draw, cycling in about three minutes.
///
/// It receives nothing, sends nothing to Discord, runs no Tailscale commands,
/// touches no registry key and writes nothing to Issun's data folders. What it
/// does write is log lines, in the same "[tag] message" shapes the real host
/// uses, so the Log section has something realistic to show; the first one
/// says it is a demo.
///
/// Changed is raised from a timer thread, as the real host raises it from
/// Kestrel's and the presence worker's, so the window's marshalling onto the
/// dispatcher is exercised by the demo too.
/// </summary>
public sealed class DemoHost : IIssunHost
{
    private const double CheckinEvery = 5;
    private const string DemoDnsName = "issun-pc.example-tailnet.ts.net";
    private const string DemoUser = "demo-user";
    private const string PhoneBuild = "1.0 (80)";

    private enum Kind { Play, Paused, PhoneQuiet }

    private sealed record Phase(Kind Kind, double Length, int Track = -1, bool DiscordDrops = false);

    private sealed record DemoTrack(string Title, string Artist, string Album, double Duration, string? StoreId, string? Cover);

    private static readonly Phase[] Script =
    [
        new(Kind.Play, 26, 0),
        new(Kind.Play, 26, 1),
        new(Kind.Paused, 10),
        new(Kind.Play, 30, 2, DiscordDrops: true),
        new(Kind.Play, 26, 3),
        new(Kind.PhoneQuiet, 14),
        new(Kind.Play, 26, 4),
    ];

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly DemoTrack[] _tracks;
    private readonly double _createdAt;
    private readonly int _startPhase;
    private Timer? _timer;
    private bool _disposed;

    private Settings _settings;
    private bool _startWithWindows;
    private bool _funnelAsked;
    private TailscaleStatus _tailscale;
    private HostSnapshot _snapshot = new();

    private int _phase = -1;
    private double _phaseStart;
    private double _startElapsed;
    private double _lastCheckin;
    private double _observedAt;
    private double _observedElapsed;
    private int _appUptimeAtStart = 3554;
    private DiscordLinkState _discordWas = DiscordLinkState.Disconnected;
    private string? _activityWas;

    /// <param name="startPhase">Which step of the script to open on — --demo-phase, for screenshotting one state.</param>
    public DemoHost(IClock? clock = null, int startPhase = 0)
    {
        _clock = clock ?? SystemClock.Instance;
        _createdAt = _clock.Now;
        _startPhase = ((startPhase % Script.Length) + Script.Length) % Script.Length;
        _settings = new Settings { DiscordClientId = "123456789012345678", Key = NewKey() };
        _tailscale = new TailscaleStatus(true, true, DemoDnsName, FunnelOn: false, FunnelTargetsPort: false,
            "Funnel is off, so Ammy can't reach Issun from outside your tailnet. Turn it on from Issun, " +
            "or run: tailscale funnel --bg --https=443 localhost:8787");

        // Invented releases, except the one-character title: "i" by Kendrick
        // Lamar is the track that exposed Discord's two-character minimum, so
        // the demo keeps it to show a title that short still lays out.
        _tracks =
        [
            new("Paper Lanterns", "Kiri & the Brushstrokes", "Celestial Ink", 214, "1440000001",
                DemoCovers.Create(Color.FromRgb(242, 228, 200), Color.FromRgb(222, 196, 160), Color.FromRgb(196, 48, 44), 1)),
            new("i", "Kendrick Lamar", "i - Single", 231, "1440000002",
                DemoCovers.Create(Color.FromRgb(214, 226, 236), Color.FromRgb(160, 180, 200), Color.FromRgb(236, 238, 240), 2)),
            new("Brush Gods", "The Celestial Envoys", "Nippon Suite", 243, "1440000003",
                DemoCovers.Create(Color.FromRgb(232, 220, 236), Color.FromRgb(120, 92, 140), Color.FromRgb(250, 214, 120), 3)),
            // No cover and no catalog ID, with a title long enough to wrap: the
            // placeholder and the two-line clamp both need something to draw.
            new("A Title Long Enough to Need Two Lines, Because Some Releases Carry Every Remix Credit in It (Extended Mix)",
                "Several Artists & a Featured Guest", "", 412, null, null),
            new("Konohana", "Sakuya", "Guardian Saplings", 276, "1440000005",
                DemoCovers.Create(Color.FromRgb(236, 240, 222), Color.FromRgb(170, 200, 150), Color.FromRgb(236, 150, 170), 5)),
        ];
    }

    public event Action? Changed;

    public HostSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public Settings Settings { get { lock (_gate) return _settings; } }

    /// <summary>Always true, so the pairing banner can be seen.</summary>
    public bool FirstRun => true;

    /// <summary>A temp folder, never Issun's real one: the demo must not leave files among the owner's logs.</summary>
    public string DataFolder => Path.Combine(Path.GetTempPath(), "Issun demo");

    public bool StartWithWindows
    {
        get { lock (_gate) return _startWithWindows; }
        set
        {
            lock (_gate) _startWithWindows = value;
            Log.Write($"[demo] start with Windows {(value ? "on" : "off")} (nothing registered: this is the demo)");
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        Log.Write($"[demo] {IssunInfo.Wire} running on made-up data: nothing is received, and nothing is sent to Discord");
        Directory.CreateDirectory(DataFolder);
        // One tick now, so the first snapshot anyone reads already has a track
        // in it — --screenshot renders straight after this returns.
        Tick();
        lock (_gate)
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public async Task UpdateSettingsAsync(Settings updated)
    {
        await Task.Delay(250);
        lock (_gate) _settings = updated;
        Log.Write("[demo] settings saved (in memory only)");
        Tick();
    }

    public async Task<string> RegenerateKeyAsync()
    {
        await Task.Delay(150);
        string key;
        lock (_gate)
        {
            key = NewKey();
            _settings = _settings with { Key = key };
        }
        Log.Write("[demo] new key generated");
        Changed?.Invoke();
        return key;
    }

    public async Task<EnvImportResult> ImportEnvAsync(string envPath)
    {
        // Deliberately does not open the file: a relay .env holds the real key
        // and application ID, and the demo has no business reading them.
        await Task.Delay(300);
        Log.Write("[demo] import skipped: the demo reads nothing");
        return new EnvImportResult(Settings,
            [$"Demo: nothing was read from {Path.GetFileName(envPath)}. Run Issun without --demo to import it."],
            null);
    }

    public async Task RefreshTailscaleAsync()
    {
        await Task.Delay(700);
        Changed?.Invoke();
    }

    public async Task<(bool Ok, string Output)> EnableFunnelAsync()
    {
        await Task.Delay(1200);
        lock (_gate)
        {
            // The first press fails the way a tailnet that hasn't allowed Funnel
            // fails, link and all; the second succeeds.
            if (!_funnelAsked)
            {
                _funnelAsked = true;
                return (false,
                    "Funnel is not enabled on your tailnet.\n" +
                    "To enable, visit:\n\n" +
                    "         https://tailscale.com/kb/1223/funnel\n\n" +
                    "(Demo output. The real message links to your tailnet's admin page.)");
            }
            _tailscale = _tailscale with { FunnelOn = true, FunnelTargetsPort = true, Detail = null };
        }
        Log.Write("[demo] Funnel turned on (pretend)");
        Tick();
        var port = Settings.Port.ToString(CultureInfo.InvariantCulture);
        return (true,
            $"Available on the internet:\n\nhttps://{DemoDnsName}/\n|-- proxy http://127.0.0.1:{port}\n\n" +
            "Funnel started and running in the background.");
    }

    public string UptimeSummary() =>
        """
        relay starts            : 14
        silences detected live  : 9
        gaps including downtime : 3

        phone-only gaps, by app build
          1.0 (45)
            count    : 1
            longest  : 00:02:37
            median   : 00:02:37
            total    : 00:02:37
          1.0 (80)
            count    : 2
            longest  : 00:01:46
            median   : 00:01:46
            total    : 00:03:12

        (Demo figures.)
        """.Replace("\n", Environment.NewLine);

    public ValueTask DisposeAsync()
    {
        Timer? timer;
        lock (_gate)
        {
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ───────────────────────────────── the script ─────────────────────────────────

    private void Tick()
    {
        var lines = new List<string>();
        bool changed;
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = _clock.Now;
            var before = _snapshot;
            if (_phase < 0)
                EnterPhase(_startPhase, now, lines);
            else if (now - _phaseStart >= Script[_phase].Length)
                EnterPhase((_phase + 1) % Script.Length, now, lines);
            Advance(now, lines);
            changed = !Equals(before, _snapshot);
        }
        foreach (var line in lines)
            Log.Write(line);
        if (changed)
            Changed?.Invoke();
    }

    private void EnterPhase(int next, double now, List<string> lines)
    {
        var leaving = _phase >= 0 ? Script[_phase] : null;
        _phase = next;
        _phaseStart = now;
        var phase = Script[next];

        if (leaving?.Kind == Kind.PhoneQuiet)
        {
            var gap = now - _lastCheckin;
            var up = AppUptime(now);
            lines.Add(Uptime($"gap {Hms(gap)}  phone silent, relay up throughout, app stayed up " +
                             $"{up - (int)gap}s -> {up}s — the path failed, not the app, 3 pushes failed over {Hms(gap - 6)}"));
        }

        switch (phase.Kind)
        {
            case Kind.Play:
                // Start each song half a minute from its end, so the demo moves
                // through tracks quickly while showing real-looking lengths.
                _startElapsed = Math.Max(0, _tracks[phase.Track].Duration - phase.Length - 4);
                CheckIn(now, lines);
                break;
            case Kind.Paused:
                CheckIn(now, lines);
                break;
            case Kind.PhoneQuiet:
                // The real host only calls a phone quiet after 90 s without a
                // check-in; the demo skips the wait by backdating the last one.
                _lastCheckin = now - 92;
                lines.Add(Uptime($"phone stopped checking in  last seen {Clock(_lastCheckin)}  last state: {Diag(now)}"));
                break;
        }
    }

    private void Advance(double now, List<string> lines)
    {
        var phase = Script[_phase];
        var t = now - _phaseStart;

        if (phase.Kind != Kind.PhoneQuiet && now - _lastCheckin >= CheckinEvery)
            CheckIn(now, lines);

        var playing = phase.Kind == Kind.Play;
        var track = playing ? _tracks[phase.Track] : null;

        DiscordStatus discord;
        // Details worded as the presence worker words them.
        if (_settings.DiscordClientId.Length == 0)
            discord = new(DiscordLinkState.Disconnected, null, "Set the Discord application ID in Settings. Presence stays off until there is one.");
        else if (phase.DiscordDrops && t is >= 6 and < 16)
            discord = new(DiscordLinkState.Disconnected, null, "Discord isn't running. Open the Discord desktop app and Issun will connect by itself.");
        else if (phase.DiscordDrops && t is >= 16 and < 18)
            discord = new(DiscordLinkState.Connecting, null, null);
        else
            discord = new(DiscordLinkState.Connected, DemoUser, null);

        if (discord.State != _discordWas)
        {
            // The same lines the presence worker writes, word for word, so the
            // demo's Log section looks like the real one.
            if (_discordWas == DiscordLinkState.Connected && discord.State == DiscordLinkState.Disconnected)
            {
                lines.Add("[rpc] lost connection (the pipe closed)");
                lines.Add("[rpc] Discord not reachable (Could not find Discord installed and running on this machine.); retrying in 10s");
            }
            if (discord.State == DiscordLinkState.Connected)
                lines.Add("[rpc] connected to Discord");
            _discordWas = discord.State;
        }

        TrackInfo? info = null;
        DiscordActivity? activity = null;
        if (track is not null)
        {
            info = new TrackInfo
            {
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Duration = track.Duration,
                Elapsed = _observedElapsed,
                StoreId = track.StoreId,
            };
            if (discord.State == DiscordLinkState.Connected)
            {
                var start = (long)Math.Round(_observedAt - _observedElapsed);
                activity = new DiscordActivity
                {
                    Details = track.Title.Length < 2 ? track.Title + "⁠" : track.Title,
                    State = track.Artist,
                    Type = 2,
                    StatusDisplayType = 1,
                    Start = start,
                    End = start + (long)track.Duration,
                    LargeImage = track.Cover is null ? null : "demo-cover",
                };
            }
        }

        var label = activity?.Details;
        if (label != _activityWas)
        {
            lines.Add(label is null ? "[rpc] cleared" : $"[rpc] {label}");
            _activityWas = label;
        }

        _snapshot = new HostSnapshot
        {
            Track = info,
            TrackObservedAt = _observedAt,
            ArtworkUrl = track?.Cover,
            Activity = activity,
            LastCheckinAt = _lastCheckin,
            PhoneSilent = phase.Kind == Kind.PhoneQuiet,
            PhoneVersion = PhoneBuild,
            DiagSummary = Diag(_lastCheckin),
            Discord = discord,
            Server = new ServerStatus(true, _settings.Port, null),
            Tailscale = _tailscale,
            PublicBase = _settings.PublicBase.Length > 0 ? _settings.PublicBase.TrimEnd('/') : _tailscale.PublicBase ?? "",
        };
    }

    private void CheckIn(double now, List<string> lines)
    {
        _lastCheckin = now;
        var phase = Script[_phase];
        if (phase.Kind == Kind.Play)
        {
            _observedAt = now;
            _observedElapsed = _startElapsed + (now - _phaseStart);
            lines.Add($"[demo] phone checked in: playing {_tracks[phase.Track].Title}");
        }
        else
        {
            lines.Add("[demo] phone checked in: not playing");
        }
    }

    private int AppUptime(double at) => _appUptimeAtStart + (int)(at - _createdAt);

    private string Diag(double at) =>
        "engine=yes want=yes route=BluetoothA2DPOutput resumes=1 fails=0 heals=0 cfg=0 routechg=1 int=0/0 " +
        "mem=19MB avail=2071MB availmin=2068MB memwarn=0 state=background lpm=no thermal=fair " +
        $"appup={AppUptime(at).ToString(CultureInfo.InvariantCulture)}s devup=31067s pushfail=0/0";

    private static string Uptime(string text) => $"[uptime] {text}  [{IssunInfo.Wire} / app {PhoneBuild}]";

    private static string Hms(double seconds)
    {
        var total = (int)Math.Max(0, seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", total / 3600, total % 3600 / 60, total % 60);
    }

    private static string Clock(double unixSeconds) =>
        UnixTime.ToLocal(unixSeconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
}
