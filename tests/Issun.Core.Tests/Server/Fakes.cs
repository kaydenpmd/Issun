using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Issun.Core;

namespace Issun.Core.Tests.Server;

internal sealed class FakePhoneState : IPhoneState
{
    private readonly object _gate = new();
    private TrackInfo? _track;
    private double _updatedAt;
    private double _lastCheckin;

    public void Set(TrackInfo? track, double updatedAt)
    {
        lock (_gate)
        {
            _track = track;
            _updatedAt = updatedAt;
        }
    }

    public (TrackInfo? Track, double UpdatedAt) Get()
    {
        lock (_gate) return (_track, _updatedAt);
    }

    public double LastCheckinAt
    {
        get { lock (_gate) return _lastCheckin; }
        set { lock (_gate) _lastCheckin = value; }
    }
}

internal sealed class FakeCheckins : ICheckinProcessor
{
    public ConcurrentQueue<NowPlayingPush> Accepted { get; } = new();

    public Func<NowPlayingPush, CheckinResult>? OnAccept { get; set; }

    public CheckinResult Accept(NowPlayingPush push)
    {
        Accepted.Enqueue(push);
        return OnAccept?.Invoke(push) ?? new CheckinResult(true, null);
    }
}

internal sealed class FakeDiagnostics : IPhoneDiagnostics
{
    public string PhoneVersion { get; set; } = "unknown";
    public string? SourceName { get; set; }
    public JsonObject? Latest { get; set; }
    public double LatestAt { get; set; }
    public string SummaryText { get; set; } = "";

    public string Summary() => SummaryText;
}

internal sealed class FakeArtwork : IArtworkResolver
{
    private int _resolveCalls;

    public ConcurrentDictionary<string, string> Covers { get; } = new();
    public ConcurrentDictionary<string, CatalogLinks> Links { get; } = new();

    /// <summary>name → absolute path, for GET /art.</summary>
    public ConcurrentDictionary<string, string> Files { get; } = new();

    public ConcurrentQueue<string> ArtNamesAsked { get; } = new();

    public int ResolveCalls => Volatile.Read(ref _resolveCalls);

    public Task<ArtworkResult> ResolveAsync(TrackInfo track, CancellationToken ct)
    {
        Interlocked.Increment(ref _resolveCalls);
        return Task.FromResult(ArtworkResult.None);
    }

    public CatalogLinks? CachedLinks(string storeId) => Links.TryGetValue(storeId, out var links) ? links : null;

    public string? CachedArtwork(string storeId) => Covers.TryGetValue(storeId, out var url) ? url : null;

    public string? UploadedArtPath(string name)
    {
        ArtNamesAsked.Enqueue(name);
        return Files.TryGetValue(name, out var path) ? path : null;
    }
}
