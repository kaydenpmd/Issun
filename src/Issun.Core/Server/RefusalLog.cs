using System.Globalization;
using System.Text;

namespace Issun.Core.Server;

/// <summary>
/// One log line per kind of refused request per minute.
///
/// relay.py logged nothing for a 401 or a 404, so a phone with the wrong key
/// or an Endpoint missing its /now-playing sat in the log looking exactly like
/// a phone that had stopped pushing — the silent failure this project keeps
/// paying for. Every refusal is written down now. But this endpoint is on the
/// public internet through Funnel, and a misconfigured phone repeats itself
/// every 30 seconds, so each kind is written at most once a minute and says
/// how many like it went unwritten in between.
/// </summary>
internal sealed class RefusalLog(IClock clock)
{
    public const double WindowSeconds = 60.0;

    private readonly object _gate = new();
    private readonly Dictionary<string, (double LoggedAt, int Held)> _kinds = new(StringComparer.Ordinal);

    /// <param name="kind">A fixed label, never request text, so the table stays small whatever strangers send.</param>
    public void Note(string kind, string line)
    {
        string text;
        lock (_gate)
        {
            var now = clock.Now;
            if (_kinds.TryGetValue(kind, out var last) && now - last.LoggedAt < WindowSeconds)
            {
                _kinds[kind] = (last.LoggedAt, last.Held + 1);
                return;
            }
            _kinds[kind] = (now, 0);
            text = last.Held > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{line}  (+{last.Held} more like it since the last line)")
                : line;
        }
        Log.Write(text);
    }

    /// <summary>
    /// Request text made safe to put in a log line: control characters would
    /// let a stranger forge whole lines of relay history, and length is capped.
    /// </summary>
    public static string Printable(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text))
            return "/";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        foreach (var c in text)
        {
            if (sb.Length == max)
            {
                sb.Append('…');
                break;
            }
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return sb.ToString();
    }
}
