using System.Globalization;
using System.Text;
using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// The last few hundred log lines as the window's Log section shows them.
///
/// Built from Log.Recent() plus Log.Written. The window subscribes first and
/// reads Recent() second, so a line written in between arrives twice — once in
/// the snapshot and once as an event — and is dropped the second time by
/// identity. Missing it instead would be the worse failure: this view is where
/// the owner looks when something has gone quiet.
/// </summary>
public sealed class LogTail
{
    public const int Capacity = 300;

    // Trimming is batched: removing one line from the front means rewriting the
    // whole text box, so it happens once per this many lines rather than on
    // every line past the capacity.
    private const int TrimSlack = 60;

    private readonly Queue<string> _lines = new();
    private readonly HashSet<LogEntry> _seeded = new(ReferenceEqualityComparer.Instance);
    private DateTime _seededAt;

    public int Count => _lines.Count;

    public string Text => string.Join(Environment.NewLine, _lines);

    public void Seed(IEnumerable<LogEntry> entries)
    {
        _seededAt = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            _seeded.Add(entry);
            _lines.Enqueue(Format(entry));
        }
        while (_lines.Count > Capacity)
            _lines.Dequeue();
    }

    /// <summary>Adds lines; returns the text to append, and whether the view must instead reload <see cref="Text"/> whole.</summary>
    public (string Appended, bool Reload) Add(IEnumerable<LogEntry> entries)
    {
        // Log.Write raises its event after releasing its lock, so a duplicate can
        // trail the snapshot by a moment — but not by seconds. Past that, the
        // identities are only memory.
        if (_seeded.Count > 0 && DateTime.UtcNow - _seededAt > TimeSpan.FromSeconds(10))
            _seeded.Clear();

        var appended = new StringBuilder();
        foreach (var entry in entries)
        {
            if (_seeded.Count > 0 && _seeded.Remove(entry))
                continue;
            var line = Format(entry);
            if (_lines.Count > 0 || appended.Length > 0)
                appended.Append(Environment.NewLine);
            appended.Append(line);
            _lines.Enqueue(line);
        }

        if (_lines.Count > Capacity + TrimSlack)
        {
            while (_lines.Count > Capacity)
                _lines.Dequeue();
            return ("", true);
        }
        return (appended.ToString(), false);
    }

    /// <summary>"21:14:03  [rpc] connected to Discord" — the log file's line without its date.</summary>
    public static string Format(LogEntry e) =>
        e.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + e.Text;
}
