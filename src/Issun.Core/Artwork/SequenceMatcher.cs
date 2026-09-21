namespace Issun.Core.Artwork;

/// <summary>
/// CPython's <c>difflib.SequenceMatcher(None, a, b)</c>, ported line for line.
///
/// relay.py ranked iTunes Search results with <c>SequenceMatcher.ratio()</c>,
/// and ART_MIN_SCORE (0.35) and the 0.6 "weak match" line are thresholds on
/// that exact number. An approximation — Levenshtein, or Ratcliff/Obershelp
/// without CPython's tie-breaking — would move scores near a threshold to the
/// other side of it, and a cover that relay.py accepted would silently vanish.
/// So this reproduces CPython's algorithm, including the parts that look like
/// details: which of several equally long matches wins (earliest in a, then
/// earliest in b), and the "autojunk" rule that ignores popular elements of b
/// once b reaches 200 elements.
///
/// Elements are Unicode code points, as they are in a Python str — a surrogate
/// pair is one element, not two.
/// </summary>
internal sealed class SequenceMatcher
{
    private readonly int[] _a;
    private readonly int[] _b;

    // difflib's b2j: for each element of b, the ascending indices where it
    // occurs. Popular elements are removed from here but are *not* junk —
    // bjunk stays empty with isjunk=None — so the extension loops in
    // FindLongestMatch still grow a match across them. Porting that
    // distinction wrongly changes ratios for long strings only, which is where
    // nobody would think to look.
    private readonly Dictionary<int, List<int>> _b2j = new();

    private List<(int A, int B, int Size)>? _matchingBlocks;

    public SequenceMatcher(string a, string b, bool autojunk = true)
        : this(CodePoints(a), CodePoints(b), autojunk)
    {
    }

    public SequenceMatcher(int[] a, int[] b, bool autojunk = true)
    {
        _a = a;
        _b = b;

        for (var i = 0; i < b.Length; i++)
        {
            if (!_b2j.TryGetValue(b[i], out var indices))
                _b2j[b[i]] = indices = new List<int>();
            indices.Add(i);
        }

        var n = b.Length;
        if (autojunk && n >= 200)
        {
            var ntest = n / 100 + 1;
            var popular = _b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList();
            foreach (var elt in popular)
                _b2j.Remove(elt);
        }
    }

    /// <summary>
    /// Longest block with a[i:i+size] == b[j:j+size] inside the given bounds;
    /// of equal lengths, the one starting earliest in a, then earliest in b.
    /// (alo, blo, 0) when nothing matches.
    /// </summary>
    public (int A, int B, int Size) FindLongestMatch(int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;

        // j2len[j] = length of the longest match ending with a[i-1] and b[j].
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (_b2j.TryGetValue(_a[i], out var indices))
            {
                foreach (var j in indices)
                {
                    if (j < blo)
                        continue;
                    if (j >= bhi)
                        break;
                    var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }

        // Extend across elements b2j doesn't index (the popular ones). With
        // isjunk=None nothing is junk, so difflib's second pair of loops — the
        // ones that extend across junk — can never run and are omitted.
        while (besti > alo && bestj > blo && _a[besti - 1] == _b[bestj - 1])
        {
            besti--;
            bestj--;
            bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && _a[besti + bestsize] == _b[bestj + bestsize])
            bestsize++;

        return (besti, bestj, bestsize);
    }

    public IReadOnlyList<(int A, int B, int Size)> GetMatchingBlocks()
    {
        if (_matchingBlocks is not null)
            return _matchingBlocks;

        int la = _a.Length, lb = _b.Length;
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, la, 0, lb));
        var blocks = new List<(int A, int B, int Size)>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var x = FindLongestMatch(alo, ahi, blo, bhi);
            var (i, j, k) = x;
            if (k > 0)
            {
                blocks.Add(x);
                if (alo < i && blo < j)
                    queue.Push((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi)
                    queue.Push((i + k, ahi, j + k, bhi));
            }
        }
        blocks.Sort();

        // Collapse adjacent blocks, as difflib does. The ratio only needs the
        // sum of sizes, but the blocks themselves are compared in the tests.
        var nonAdjacent = new List<(int A, int B, int Size)>();
        int i1 = 0, j1 = 0, k1 = 0;
        foreach (var (i2, j2, k2) in blocks)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
            {
                k1 += k2;
            }
            else
            {
                if (k1 > 0)
                    nonAdjacent.Add((i1, j1, k1));
                (i1, j1, k1) = (i2, j2, k2);
            }
        }
        if (k1 > 0)
            nonAdjacent.Add((i1, j1, k1));
        nonAdjacent.Add((la, lb, 0));

        return _matchingBlocks = nonAdjacent;
    }

    /// <summary>2.0 * matches / (len(a) + len(b)); 1.0 when both are empty.</summary>
    public double Ratio()
    {
        var matches = 0;
        foreach (var block in GetMatchingBlocks())
            matches += block.Size;
        var length = _a.Length + _b.Length;
        return length > 0 ? 2.0 * matches / length : 1.0;
    }

    public static double Ratio(string a, string b) => new SequenceMatcher(a, b).Ratio();

    /// <summary>
    /// A string as Python sees it: one element per code point. A lone surrogate
    /// stays a single element, as it does in a Python str.
    /// </summary>
    public static int[] CodePoints(string s)
    {
        var result = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                result.Add(char.ConvertToUtf32(c, s[i + 1]));
                i++;
            }
            else
            {
                result.Add(c);
            }
        }
        return result.ToArray();
    }
}
