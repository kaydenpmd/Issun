using System.Collections.Concurrent;
using System.Diagnostics;

namespace Issun.Core.Tests.Discord;

/// <summary>
/// Stands in for Discord across every connection the worker makes: it is the
/// client factory, and it records what each client was asked to do.
/// </summary>
internal sealed class FakeDiscord
{
    private readonly object _gate = new();
    private readonly List<FakeClient> _clients = new();
    private readonly List<(string ClientId, TimeSpan At)> _connects = new();
    private readonly List<DiscordActivity?> _sets = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    /// <summary>Decides each ConnectAsync: return a user, or throw.</summary>
    public Func<string, DiscordUser?> OnConnect { get; set; } = _ => new DiscordUser("1", "example", "Example Person");

    /// <summary>Decides each SetActivityAsync: return to accept, or throw.</summary>
    public Action<DiscordActivity?> OnSet { get; set; } = _ => { };

    public Func<IDiscordClient> Factory => () =>
    {
        var client = new FakeClient(this);
        lock (_gate) _clients.Add(client);
        return client;
    };

    public IReadOnlyList<FakeClient> Clients { get { lock (_gate) return _clients.ToArray(); } }
    public IReadOnlyList<(string ClientId, TimeSpan At)> Connects { get { lock (_gate) return _connects.ToArray(); } }
    public IReadOnlyList<DiscordActivity?> Sets { get { lock (_gate) return _sets.ToArray(); } }

    internal sealed class FakeClient(FakeDiscord hub) : IDiscordClient
    {
        private volatile bool _connected;

        public bool Disposed { get; private set; }

        /// <summary>Lets a test pull the plug without an exception, as a pipe that closes quietly would.</summary>
        public void Sever() => _connected = false;

        public bool IsConnected => _connected && !Disposed;

        public Task<DiscordUser?> ConnectAsync(string clientId, CancellationToken ct)
        {
            lock (hub._gate) hub._connects.Add((clientId, hub._watch.Elapsed));
            var user = hub.OnConnect(clientId);
            _connected = true;
            return Task.FromResult(user);
        }

        public Task SetActivityAsync(DiscordActivity? activity, CancellationToken ct)
        {
            if (!IsConnected)
                throw new DiscordConnectionLostException("Not connected to Discord.");
            lock (hub._gate) hub._sets.Add(activity);
            try
            {
                hub.OnSet(activity);
            }
            catch (DiscordConnectionLostException)
            {
                _connected = false;
                throw;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _connected = false;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakePhoneState : IPhoneState
{
    private readonly object _gate = new();
    private TrackInfo? _track;
    private double _updatedAt;
    private double _lastCheckin;

    public void Push(TrackInfo? track, double at)
    {
        lock (_gate)
        {
            _track = track;
            _updatedAt = at;
            _lastCheckin = at;
        }
    }

    public (TrackInfo? Track, double UpdatedAt) Get()
    {
        lock (_gate) return (_track, _updatedAt);
    }

    public double LastCheckinAt
    {
        get { lock (_gate) return _lastCheckin; }
    }
}

/// <summary>
/// A builder with just enough of the real one's behaviour to exercise the
/// worker: activity from title/artist/art/links, a playhead anchored to
/// observedAt, and relay.py's 2-second tolerance on start/end.
/// </summary>
internal sealed class FakeBuilder : IActivityBuilder
{
    private int _resets;
    private int _builds;

    public int Resets => Volatile.Read(ref _resets);
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>What the member list shows; null builds an activity with nothing for the fallback to shed.</summary>
    public int? DisplayType { get; set; } = 1;

    /// <summary>Thrown from the next Build, once.</summary>
    public Exception? FailNextBuild { get; set; }

    public ConcurrentQueue<double> ObservedAt { get; } = new();

    public DiscordActivity Build(TrackInfo track, double observedAt, ArtworkResult art, CatalogLinks? links)
    {
        Interlocked.Increment(ref _builds);
        if (FailNextBuild is { } failure)
        {
            FailNextBuild = null;
            throw failure;
        }
        ObservedAt.Enqueue(observedAt);

        long? start = track.Duration > 0 ? (long)(observedAt - (track.Elapsed ?? 0)) : null;
        return new DiscordActivity
        {
            Details = TrackText.Title(track),
            State = TrackText.Artist(track),
            Type = 2,
            StatusDisplayType = DisplayType,
            Start = start,
            End = start is null ? null : start + (long)track.Duration!.Value,
            LargeImage = art.Url,
            LargeUrl = art.Url is null ? null : links?.Album,
            DetailsUrl = links?.Song,
            StateUrl = links?.Artist,
        };
    }

    public void Reset() => Interlocked.Increment(ref _resets);

    public bool MateriallyDifferent(DiscordActivity? next, DiscordActivity? previous)
    {
        if (next is null || previous is null)
            return !ReferenceEquals(next, previous);
        if (next with { Start = null, End = null } != previous with { Start = null, End = null })
            return true;
        return Differs(next.Start, previous.Start) || Differs(next.End, previous.End);

        static bool Differs(long? a, long? b) => a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b!.Value) > 2);
    }
}

internal sealed class FakeResolver : IArtworkResolver
{
    private int _resolves;

    public int Resolves => Volatile.Read(ref _resolves);

    public Func<TrackInfo, ArtworkResult> Answer { get; set; } =
        t => new ArtworkResult($"https://art.example/{TrackText.Title(t)}.jpg", TrackText.Album(t), ArtworkSource.StoreId);

    public Dictionary<string, CatalogLinks> Links { get; } = new();

    public Task<ArtworkResult> ResolveAsync(TrackInfo track, CancellationToken ct)
    {
        Interlocked.Increment(ref _resolves);
        return Task.FromResult(Answer(track));
    }

    public CatalogLinks? CachedLinks(string storeId)
    {
        lock (Links) return Links.GetValueOrDefault(storeId);
    }

    public string? CachedArtwork(string storeId) => null;

    public bool? CachedExplicit(string storeId) => null;

    public string? UploadedArtPath(string name) => null;
}

internal sealed class FakeUptimeLog : IUptimeLog
{
    private int _silenceChecks;

    /// <summary>One per worker tick — the tests' metronome.</summary>
    public int SilenceChecks => Volatile.Read(ref _silenceChecks);

    public ConcurrentQueue<double> LastSeen { get; } = new();

    public string FilePath => "uptime-test.log";

    public void Line(string text) { }

    public void NoteSilence(double lastSeen)
    {
        LastSeen.Enqueue(lastSeen);
        Interlocked.Increment(ref _silenceChecks);
    }

    public string Summarize() => "";
}
