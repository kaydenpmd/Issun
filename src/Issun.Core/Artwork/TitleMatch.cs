using System.Globalization;
using System.Text;

namespace Issun.Core.Artwork;

/// <summary>
/// relay.py's <c>_normalize</c>, <c>_similarity</c> and <c>_score</c>: how an
/// iTunes Search result is judged against what the phone says is playing.
/// </summary>
internal static class TitleMatch
{
    /// <summary>
    /// Fold case, strip bracketed qualifiers like (feat. X) / [Remix] and
    /// punctuation, so "Song (Remastered 2011)" and "Song" compare closely.
    ///
    /// Also drops release-type suffixes: Apple Music's library metadata says
    /// "Album" where the catalog says "Album - Single", and that difference
    /// alone used to sink an otherwise perfect match.
    ///
    /// relay.py did this with four Python regexes. They are hand-ported below
    /// rather than handed to .NET's Regex, because the two engines disagree on
    /// exactly the characters that turn up in song titles: Python's \w is
    /// str.isalnum() plus "_", so it excludes combining marks — which NFKD has
    /// just split off every accented letter — while .NET's \w includes them.
    /// "with" followed by an accent is a word boundary to Python and not to
    /// .NET. Python's \s also includes U+001C–U+001F, and it walks code points
    /// where .NET walks UTF-16 units. Each disagreement changes whether a
    /// "feat."/"with" tail is cut, and therefore the score.
    /// </summary>
    public static string Normalize(string? text)
    {
        var cps = NfkdLower(text ?? "");
        cps = StripBrackets(cps);
        cps = StripReleaseSuffix(cps);
        cps = StripFeaturing(cps);

        // re.sub(r"[^a-z0-9]+", " ", text) then " ".join(text.split()): runs of
        // [a-z0-9] joined by single spaces.
        var sb = new StringBuilder(cps.Length);
        var pendingSpace = false;
        foreach (var cp in cps)
        {
            if (cp is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append((char)cp);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary><c>difflib.SequenceMatcher(None, _normalize(a), _normalize(b)).ratio()</c>. Argument order matters.</summary>
    public static double Similarity(string? a, string? b) => SequenceMatcher.Ratio(Normalize(a), Normalize(b));

    /// <summary>
    /// Album is weighted heavily because the artwork *is* the album cover — the
    /// right song off the wrong release still gives you the wrong image.
    /// The arithmetic is written in relay.py's order so the doubles come out
    /// bit-identical, not merely close.
    /// </summary>
    public static double Score(string? trackName, string? artistName, string? collectionName,
        string title, string artist, string album)
    {
        var titleS = Similarity(trackName, title);
        var artistS = Similarity(artistName, artist);
        if (album.Length == 0)
            return 0.6 * titleS + 0.4 * artistS;
        var albumS = Similarity(collectionName, album);
        return 0.40 * titleS + 0.25 * artistS + 0.35 * albumS;
    }

    // ── unicodedata.normalize("NFKD", text).lower() ─────────────────────────

    private static int[] NfkdLower(string text)
    {
        // .NET refuses to normalize a string holding a lone surrogate, which a
        // Python str (and a JSON "\ud800" escape) can carry. U+FFFD behaves
        // identically to a lone surrogate in everything below — neither is
        // alphanumeric, whitespace, or any character the patterns name — so
        // substituting it changes nothing but whether this throws.
        var decomposed = ReplaceLoneSurrogates(text).Normalize(NormalizationForm.FormKD);
        var cps = SequenceMatcher.CodePoints(decomposed);

        // Only ASCII is lowered. After NFKD this is exactly equivalent to
        // Python's full str.lower() for every purpose here: the only characters
        // whose lowercase is ASCII without being ASCII already (KELVIN SIGN,
        // LATIN CAPITAL I WITH DOT ABOVE) have been decomposed to ASCII by NFKD,
        // and lowering a non-ASCII letter yields another non-ASCII letter,
        // which every step below treats the same way. Culture-sensitive
        // lowering is how "TITLE" becomes "tıtle" on a Turkish machine.
        for (var i = 0; i < cps.Length; i++)
        {
            if (cps[i] is >= 'A' and <= 'Z')
                cps[i] += 'a' - 'A';
        }
        return cps;
    }

    /// <summary><paramref name="s"/> with every unpaired surrogate replaced by U+FFFD.</summary>
    internal static string ReplaceLoneSurrogates(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var paired = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            if (paired)
            {
                sb?.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append('�');
                continue;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }

    // ── re.sub(r"\(.*?\)|\[.*?\]", " ", text) ────────────────────────────────

    private static int[] StripBrackets(int[] t)
    {
        var output = new List<int>(t.Length);
        var i = 0;
        while (i < t.Length)
        {
            var close = t[i] switch { '(' => ')', '[' => ']', _ => -1 };
            if (close >= 0 && FindClose(t, i + 1, close) is var end and >= 0)
            {
                output.Add(' ');
                i = end + 1;
                continue;
            }
            output.Add(t[i]);
            i++;
        }
        return output.ToArray();
    }

    /// <summary>The first <paramref name="close"/> at or after <paramref name="from"/>, or -1 if a newline comes first — Python's "." stops at "\n".</summary>
    private static int FindClose(int[] t, int from, int close)
    {
        for (var k = from; k < t.Length; k++)
        {
            if (t[k] == close)
                return k;
            if (t[k] == '\n')
                return -1;
        }
        return -1;
    }

    // ── re.sub(r"\s*[-–—]\s*(single|ep|…)\b.*", " ", text) ───────────────────

    private static readonly string[] ReleaseSuffixes =
    [
        "single", "ep", "deluxe", "remastered", "remaster",
        "deluxe edition", "special edition", "expanded edition",
        "original motion picture soundtrack", "bonus track version",
    ];

    private static int[] StripReleaseSuffix(int[] t)
    {
        var output = new List<int>(t.Length);
        var i = 0;
        while (i < t.Length)
        {
            if (MatchReleaseSuffix(t, i) is var end and >= 0)
            {
                output.Add(' ');
                i = end;
                continue;
            }
            output.Add(t[i]);
            i++;
        }
        return output.ToArray();
    }

    /// <summary>
    /// Where a match starting at <paramref name="p"/> ends, or -1. Neither \s*
    /// can usefully backtrack — giving whitespace back only puts whitespace
    /// where a dash or a letter is required — so greedy scanning is exact.
    /// Which alternative matched doesn't matter: ".*" runs to the end of the
    /// line whichever it was.
    /// </summary>
    private static int MatchReleaseSuffix(int[] t, int p)
    {
        var q = SkipSpace(t, p);
        if (q >= t.Length || t[q] is not ('-' or '–' or '—'))
            return -1;
        var r = SkipSpace(t, q + 1);
        foreach (var word in ReleaseSuffixes)
        {
            if (WordAt(t, r, word))
                return EndOfLine(t, r + word.Length);
        }
        return -1;
    }

    // ── re.sub(r"\b(feat|ft|featuring|with)\b.*", " ", text) ─────────────────

    private static readonly string[] FeaturingWords = ["feat", "ft", "featuring", "with"];

    private static int[] StripFeaturing(int[] t)
    {
        var output = new List<int>(t.Length);
        var i = 0;
        while (i < t.Length)
        {
            // Every alternative starts with a word character, so the leading \b
            // holds exactly when the previous character isn't one.
            if (i == 0 || !IsWord(t[i - 1]))
            {
                var matched = -1;
                foreach (var word in FeaturingWords)
                {
                    if (WordAt(t, i, word))
                    {
                        matched = EndOfLine(t, i + word.Length);
                        break;
                    }
                }
                if (matched >= 0)
                {
                    output.Add(' ');
                    i = matched;
                    continue;
                }
            }
            output.Add(t[i]);
            i++;
        }
        return output.ToArray();
    }

    // ── Shared pieces ───────────────────────────────────────────────────────

    /// <summary><paramref name="word"/> (ASCII, ending in a letter) at <paramref name="at"/>, followed by \b.</summary>
    private static bool WordAt(int[] t, int at, string word)
    {
        if (at + word.Length > t.Length)
            return false;
        for (var k = 0; k < word.Length; k++)
        {
            if (t[at + k] != word[k])
                return false;
        }
        var next = at + word.Length;
        return next == t.Length || !IsWord(t[next]);
    }

    private static int SkipSpace(int[] t, int from)
    {
        while (from < t.Length && IsSpace(t[from]))
            from++;
        return from;
    }

    /// <summary>Where ".*" starting at <paramref name="from"/> stops: the next "\n", or the end.</summary>
    private static int EndOfLine(int[] t, int from)
    {
        while (from < t.Length && t[from] != '\n')
            from++;
        return from;
    }

    /// <summary>
    /// Python's \w for str patterns: str.isalnum() or "_". isalnum is every
    /// Letter category plus every Number category (Nd, Nl, No) — the CJK
    /// ideographs that carry a numeric value are letters (Lo) already. Marks
    /// (Mn, Mc, Me) are not word characters, unlike .NET's \w.
    /// </summary>
    internal static bool IsWord(int cp)
    {
        if (cp == '_')
            return true;
        if (cp < 0x80)
            return cp is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
        if (cp is >= 0xD800 and <= 0xDFFF)
            return false;
        return CharUnicodeInfo.GetUnicodeCategory(cp) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
            _ => false,
        };
    }

    /// <summary>
    /// Python's \s for str patterns, which is str.isspace(): bidi class WS, B
    /// or S, or category Zs. Listed explicitly because it is not .NET's \s —
    /// U+001C–U+001F are Python whitespace and .NET's \s omits them.
    /// </summary>
    internal static bool IsSpace(int cp) => cp switch
    {
        >= 0x09 and <= 0x0D => true,
        >= 0x1C and <= 0x20 => true,
        0x85 or 0xA0 or 0x1680 => true,
        >= 0x2000 and <= 0x200A => true,
        0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 => true,
        _ => false,
    };
}
