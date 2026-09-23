namespace Issun.Core.Discord;

/// <summary>
/// Lets a line through when it says something new, and otherwise at most once
/// per interval — with a count of how many identical ones were held back, so a
/// quiet log still shows the loop is alive and still failing.
///
/// relay.py logged "Discord not reachable" every 10 s for as long as Discord
/// was closed: a whole night of identical lines burying the few that mattered.
/// Silence is not the fix either — an unexplained silence is this project's
/// recurring bug — so the first occurrence and every change are always written.
///
/// Not thread-safe; each instance belongs to one loop.
/// </summary>
internal sealed class RepeatGate(double intervalSeconds)
{
    private string? _key;
    private double _loggedAt;
    private int _held;

    /// <param name="held">Identical lines held back since the last one written; only meaningful when this returns true.</param>
    /// <param name="previousAt">When that last one was written (unix seconds).</param>
    public bool ShouldLog(string key, double now, out int held, out double previousAt)
    {
        previousAt = _loggedAt;
        if (key == _key && now - _loggedAt < intervalSeconds)
        {
            _held++;
            held = 0;
            return false;
        }

        held = key == _key ? _held : 0;
        _key = key;
        _loggedAt = now;
        _held = 0;
        return true;
    }

    public void Reset()
    {
        _key = null;
        _held = 0;
    }
}
