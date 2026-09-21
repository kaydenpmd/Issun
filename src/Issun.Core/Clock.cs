namespace Issun.Core;

/// <summary>
/// Wall-clock seconds since the Unix epoch, as a double — the same unit as
/// Python's <c>time.time()</c>, so arithmetic ported from relay.py keeps its
/// shape. Injected everywhere time matters so tests can move it by hand.
/// </summary>
public interface IClock
{
    double Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

/// <summary>A clock tests can set and advance.</summary>
public sealed class ManualClock(double start = 1_750_000_000) : IClock
{
    private double _now = start;

    public double Now
    {
        get => Volatile.Read(ref _now);
        set => Volatile.Write(ref _now, value);
    }

    public void Advance(double seconds) => Now += seconds;
}

public static class UnixTime
{
    /// <summary>Unix seconds to local time, for log lines — relay.py used time.localtime.</summary>
    public static DateTime ToLocal(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)).LocalDateTime;
}
