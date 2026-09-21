namespace Issun.Core.Presence;

/// <summary>
/// "Log this once per value", safe across threads. The activity is rebuilt
/// every second against a reading the phone refreshes every thirty, so any
/// line written from inside a build is written thirty times a heartbeat unless
/// something remembers it already has been — relay.py 1.3.0 wrote 189
/// identical padding lines over one song before 1.3.1 added exactly this.
///
/// Never trimmed, as relay.py's sets never were. Entries are one per distinct
/// track or one-character title, so a year of listening is a few megabytes at
/// worst, and forgetting would re-log lines the owner has already seen.
/// </summary>
internal sealed class FirstSeen
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>True the first time <paramref name="value"/> is offered, false ever after.</summary>
    public bool Add(string value)
    {
        lock (_seen)
            return _seen.Add(value);
    }
}
