using System.Text;

namespace Issun.Core.Platform;

/// <summary>
/// Carries relay.py's ammy-uptime.log into Issun's, so the record of when the
/// phone went quiet — the only evidence behind every "did iOS kill it?"
/// verdict this project has reached — continues across the switch instead of
/// starting over. The relay-era lines are copied verbatim, [relay 1.9.0 / app
/// 1.0 (83)] stamps included: the summary groups by the app build, and the
/// relay half of the stamp keeps saying which program wrote each line.
/// </summary>
internal static class UptimeHistory
{
    /// <param name="SourceLines">Non-blank lines in relay.py's log.</param>
    /// <param name="Added">How many of them were not already in Issun's log.</param>
    /// <param name="Lossy">The source wasn't valid UTF-8 and some characters were replaced.</param>
    internal sealed record Outcome(int SourceLines, int Added, bool Lossy);

    private const int Attempts = 3;

    /// <summary>
    /// Rewrites <paramref name="target"/> as every line of <paramref name="source"/>
    /// followed by the target's own lines. <paramref name="source"/> is opened
    /// read-only and never written.
    ///
    /// Idempotent by construction rather than by a marker: whatever lines the
    /// target already holds that also appear in the source — counted, not merely
    /// matched, because relay.py wrote the 14 Sept 14:59 gap line twice,
    /// identically, and both copies are history — are treated as a previous
    /// import and replaced by the source's current copy. So importing twice adds
    /// nothing, and importing again after relay.py ran a while longer adds only
    /// what it wrote since.
    /// </summary>
    public static Outcome Merge(string source, string target)
    {
        var (sourceLines, lossy) = ReadLines(source, strict: true);

        for (var attempt = 1; ; attempt++)
        {
            var before = Stat(target);
            var targetLines = before.Exists ? ReadLines(target, strict: false).Lines : [];

            var previouslyImported = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in sourceLines)
                previouslyImported[line] = previouslyImported.GetValueOrDefault(line) + 1;

            var own = new List<string>(targetLines.Count);
            var matched = 0;
            foreach (var line in targetLines)
            {
                if (previouslyImported.TryGetValue(line, out var left) && left > 0)
                {
                    previouslyImported[line] = left - 1;
                    matched++;
                }
                else
                {
                    own.Add(line);
                }
            }

            var merged = new List<string>(sourceLines.Count + own.Count);
            merged.AddRange(sourceLines);
            merged.AddRange(own);
            var outcome = new Outcome(sourceLines.Count, sourceLines.Count - matched, lossy);
            if (merged.SequenceEqual(targetLines, StringComparer.Ordinal))
                return outcome;

            // The uptime log is appended to by Issun's own worker loop, and a
            // line it wrote between the read above and the replace below would
            // otherwise vanish without a trace. The check runs after the merged
            // copy is on disk and just before the rename, so what is left
            // unguarded is the rename itself; if anything moved, start over.
            var text = string.Join(Environment.NewLine, merged) + Environment.NewLine;
            if (AtomicFile.WriteAllText(target, text, stillCurrent: () => Stat(target) == before))
                return outcome;
            if (attempt >= Attempts)
                throw new IOException($"{target} kept changing while its history was being merged");
        }
    }

    private readonly record struct FileStat(bool Exists, long Length, DateTime WrittenUtc);

    private static FileStat Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStat(true, info.Length, info.LastWriteTimeUtc) : default;
    }

    /// <summary>
    /// Non-blank lines, however they were terminated — relay.py wrote CRLF
    /// through Python's text mode, Issun writes Environment.NewLine, and a hand
    /// edit could leave anything. Shared read access, because relay.py may still
    /// be running and appending while this reads.
    /// </summary>
    private static (List<string> Lines, bool Lossy) ReadLines(string path, bool strict)
    {
        try
        {
            return (Read(path, new UTF8Encoding(false, throwOnInvalidBytes: strict)), false);
        }
        catch (DecoderFallbackException)
        {
            return (Read(path, new UTF8Encoding(false, throwOnInvalidBytes: false)), true);
        }
    }

    private static List<string> Read(string path, Encoding encoding)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }
        return lines;
    }
}
