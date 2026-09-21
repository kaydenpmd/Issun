namespace Issun.Core.Presence;

/// <summary>
/// relay.py's <c>pad_for_discord()</c>.
///
/// Discord requires <c>details</c> and <c>state</c> to be at least two
/// characters and refuses the whole activity otherwise — as a server error,
/// not a validation warning. One-character titles are ordinary ("4", "T",
/// "Ø"); the one that found this was "i" by Kendrick Lamar, hit on shuffle.
/// relay.py's worker read the refusal as a dropped socket, reconnected,
/// re-sent the identical payload and looped until the song changed, with
/// presence empty for about a minute. Padding is the first of the three fixes
/// from relay 1.3.0; the other two live in the presence worker, which must
/// keep a refused payload (<see cref="DiscordRejectedException"/>) apart from a
/// lost connection.
///
/// U+2060 WORD JOINER is invisible when rendered but counts toward the length.
/// </summary>
public sealed class DiscordText
{
    public const int MinField = 2;
    public const char InvisiblePad = '\u2060';

    // Keyed by the text alone, as relay.py's _padded_seen was: "i" as a title
    // and then "i" as an artist is one line, not two.
    private readonly FirstSeen _paddedSeen = new();

    /// <summary>
    /// "Unknown" when <paramref name="text"/> is blank after Python's
    /// <c>strip()</c>; otherwise padded to <see cref="MinField"/> code points.
    /// Logs the first time each distinct value is padded.
    /// </summary>
    public string Pad(string text, string label)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PyText.IsBlank(text) ? "Unknown" : PadToMinimum(text, label);
    }

    /// <summary>
    /// The padding half on its own, for fields where "Unknown" would be a false
    /// statement rather than a placeholder — an album tooltip.
    /// </summary>
    internal string PadToMinimum(string text, string label)
    {
        // Code points, as Python's len() counts: a single emoji is two UTF-16
        // units but one character, and relay.py padded it.
        var length = PyText.CodePoints(text);
        if (length >= MinField)
            return text;

        // Once per value, not once per build. build_payload runs every second,
        // so the first short title ("i", Kendrick Lamar) wrote 189 identical
        // lines over one song.
        if (_paddedSeen.Add(text))
            Log.Write($"[rpc] padded {label} {PyText.Repr(text)} — Discord requires 2+ characters");

        return text + new string(InvisiblePad, MinField - length);
    }
}
