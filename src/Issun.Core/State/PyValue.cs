using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Issun.Core.State;

internal enum PyKind { None, Bool, Int, Float, Str, List, Dict }

/// <summary>
/// A JSON value as relay.py held it after <c>json.loads</c> — None, bool, int,
/// float, str, list or dict — with Python's meaning for the few operations
/// relay.py applied to the phone's diag block: truthiness, <c>==</c>,
/// <c>str()</c> and <c>repr()</c>.
///
/// It exists for the logs. relay.py wrote diag values with f-strings and
/// <c>!r</c>, so relay.log says <c>thermal 'nominal' -&gt; 'fair'</c> and
/// <c>low_power False -&gt; True</c>, and months of that history only stay
/// greppable if Issun spells values the way Python did. It also keeps the type
/// distinctions relay.py's checks rest on: JSON <c>3</c> is an int and
/// <c>3.0</c> is a float, and only the first is an uptime or a counter.
///
/// Immutable, so a stored snapshot can be read from any thread.
/// </summary>
internal sealed class PyValue
{
    public static readonly PyValue None = new(PyKind.None);
    public static readonly PyValue True = new(PyKind.Bool, flag: true);
    public static readonly PyValue False = new(PyKind.Bool, flag: false);
    public static readonly PyValue Zero = Int(BigInteger.Zero);

    private readonly bool _flag;
    private readonly BigInteger _int;
    private readonly double _float;
    private readonly string? _str;
    private readonly PyValue[]? _list;
    private readonly PyDict? _dict;

    private PyValue(PyKind kind, bool flag = false, BigInteger integer = default, double number = 0,
        string? text = null, PyValue[]? list = null, PyDict? dict = null)
    {
        Kind = kind;
        _flag = flag;
        _int = integer;
        _float = number;
        _str = text;
        _list = list;
        _dict = dict;
    }

    public PyKind Kind { get; }

    public static PyValue Int(BigInteger value) => new(PyKind.Int, integer: value);
    public static PyValue Float(double value) => new(PyKind.Float, number: value);
    public static PyValue Str(string value) => new(PyKind.Str, text: value);
    public static PyValue Bool(bool value) => value ? True : False;
    public static PyValue List(IEnumerable<PyValue> items) => new(PyKind.List, list: items.ToArray());
    public static PyValue Dict(PyDict dict) => new(PyKind.Dict, dict: dict);

    /// <summary>
    /// <c>isinstance(x, int)</c>, except that a bool is not an int here. Python
    /// says <c>isinstance(True, int)</c>; Issun doesn't count a JSON true as an
    /// uptime of 1 second. Ammy never sends a bool in those fields, so the
    /// difference only matters to a malformed push.
    /// </summary>
    public bool IsInt => Kind == PyKind.Int;

    /// <summary>The integer value. Only meaningful when <see cref="IsInt"/>.</summary>
    public BigInteger IntValue => _int;

    public PyDict? DictValue => _dict;

    /// <summary>Python's <c>bool(x)</c>.</summary>
    public bool Truthy => Kind switch
    {
        PyKind.None => false,
        PyKind.Bool => _flag,
        PyKind.Int => !_int.IsZero,
        PyKind.Float => _float != 0,        // NaN is truthy in Python too
        PyKind.Str => _str!.Length > 0,
        PyKind.List => _list!.Length > 0,
        PyKind.Dict => _dict!.Count > 0,
        _ => false,
    };

    /// <summary>
    /// What <c>json.loads</c> would have produced. Only the spelling of a number
    /// decides int or float: <c>3</c> is an int, <c>3.0</c> and <c>3e0</c> are floats.
    /// </summary>
    public static PyValue FromJson(JsonNode? node) => node switch
    {
        null => None,
        JsonObject obj => Dict(PyDict.FromJson(obj)),
        JsonArray array => List(array.Select(FromJson)),
        JsonValue value => FromJsonValue(value),
        _ => None,
    };

    private static PyValue FromJsonValue(JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                try
                {
                    return Str(value.GetValue<string>());
                }
                catch (InvalidOperationException)
                {
                    // A lone surrogate written as an escape ("\ud800"): legal
                    // JSON, kept by json.loads, but System.Text.Json refuses to
                    // produce a string holding one. Decoding the literal by hand
                    // keeps a malformed diag from throwing out of a check-in.
                    var raw = value.TryGetValue<JsonElement>(out var element) ? element.GetRawText() : value.ToJsonString();
                    return Str(UnescapeJsonString(raw));
                }
            case JsonValueKind.True:
                return True;
            case JsonValueKind.False:
                return False;
            case JsonValueKind.Number:
                return FromJsonNumber(value);
            default:
                return None;
        }
    }

    /// <summary>A JSON string literal, quotes included, decoded unit by unit so lone surrogates survive.</summary>
    private static string UnescapeJsonString(string raw)
    {
        var body = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\\' || i + 1 >= body.Length)
            {
                sb.Append(c);
                continue;
            }

            var e = body[++i];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when i + 4 < body.Length
                              && ushort.TryParse(body.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier,
                                                 CultureInfo.InvariantCulture, out var unit):
                    sb.Append((char)unit);
                    i += 4;
                    break;
                default: sb.Append(e); break;   // \" \\ \/
            }
        }
        return sb.ToString();
    }

    private static PyValue FromJsonNumber(JsonValue value)
    {
        // A node parsed from text wraps a JsonElement, and the text is what
        // json.loads saw. A node built in code wraps the CLR value itself, and
        // its type is the only honest answer: serialising a double 5.0 writes
        // "5", which would read back as an int.
        value.TryGetValue<object>(out var boxed);
        switch (boxed)
        {
            case double d: return Float(d);
            case float f: return Float(f);
            case decimal m: return Float((double)m);
            case Half h: return Float((double)h);
            case long or int or short or sbyte or ulong or uint or ushort or byte:
                return Int(BigInteger.Parse(Convert.ToString(boxed, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture));
            case BigInteger big: return Int(big);
        }

        var raw = boxed is JsonElement element ? element.GetRawText() : value.ToJsonString();
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
            // "1e400" is inf in Python and in .NET alike; neither throws.
            return Float(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
        return BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? Int(n)
            : Str(raw);
    }

    /// <summary>Python's <c>str(x)</c>, which is also what an f-string's <c>{x}</c> produces for these types.</summary>
    public override string ToString() => Kind == PyKind.Str ? _str! : Repr();

    /// <summary>Python's <c>repr(x)</c>, which is what <c>{x!r}</c> produces.</summary>
    public string Repr() => Kind switch
    {
        PyKind.None => "None",
        PyKind.Bool => _flag ? "True" : "False",
        PyKind.Int => _int.ToString(CultureInfo.InvariantCulture),
        PyKind.Float => FloatRepr(_float),
        PyKind.Str => StrRepr(_str!),
        PyKind.List => "[" + string.Join(", ", _list!.Select(v => v.Repr())) + "]",
        PyKind.Dict => "{" + string.Join(", ", _dict!.Items.Select(kv => StrRepr(kv.Key) + ": " + kv.Value.Repr())) + "}",
        _ => "None",
    };

    /// <summary>
    /// Python's <c>a == b</c>. Numbers compare by value across bool, int and
    /// float — <c>True == 1 == 1.0</c> — which is why a flag moving from
    /// <c>True</c> to <c>1</c> was never logged as a change, and still isn't.
    /// </summary>
    public static bool Equal(PyValue a, PyValue b)
    {
        if (a.IsNumber && b.IsNumber)
            return NumbersEqual(a, b);
        if (a.Kind != b.Kind)
            return false;

        switch (a.Kind)
        {
            case PyKind.None:
                return true;
            case PyKind.Str:
                return string.Equals(a._str, b._str, StringComparison.Ordinal);
            case PyKind.List:
                if (a._list!.Length != b._list!.Length)
                    return false;
                for (var i = 0; i < a._list.Length; i++)
                    if (!Equal(a._list[i], b._list[i]))
                        return false;
                return true;
            case PyKind.Dict:
                if (a._dict!.Count != b._dict!.Count)
                    return false;
                foreach (var (key, value) in a._dict.Items)
                    if (!b._dict.TryGet(key, out var other) || !Equal(value, other))
                        return false;
                return true;
            default:
                return false;
        }
    }

    private bool IsNumber => Kind is PyKind.Bool or PyKind.Int or PyKind.Float;

    private BigInteger AsInteger => Kind == PyKind.Bool ? (_flag ? BigInteger.One : BigInteger.Zero) : _int;

    private static bool NumbersEqual(PyValue a, PyValue b)
    {
        if (a.Kind != PyKind.Float && b.Kind != PyKind.Float)
            return a.AsInteger == b.AsInteger;
        if (a.Kind == PyKind.Float && b.Kind == PyKind.Float)
            return a._float == b._float;

        // int against float: exact, the way Python compares them.
        var (f, n) = a.Kind == PyKind.Float ? (a._float, b.AsInteger) : (b._float, a.AsInteger);
        return double.IsFinite(f) && Math.Floor(f) == f && new BigInteger(f) == n;
    }

    /// <summary>
    /// Python's <c>repr(float)</c>: the shortest digits that round-trip — the
    /// same digits .NET's "R" picks — laid out Python's way. Fixed notation
    /// from 1e-4 up to 1e16, exponent outside it with at least two exponent
    /// digits ("1e-05", "1e+16"), and ".0" on anything integral in fixed notation.
    /// </summary>
    internal static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var sign = "";
        if (text.StartsWith('-'))
        {
            sign = "-";
            text = text[1..];
        }

        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        // Normalise to digits "d1d2d3..." with the decimal point after `point` of them.
        var dot = text.IndexOf('.');
        var digits = dot < 0 ? text : text.Remove(dot, 1);
        var point = (dot < 0 ? text.Length : dot) + exponent;
        var leading = 0;
        while (leading < digits.Length && digits[leading] == '0')
            leading++;
        digits = digits[leading..].TrimEnd('0');
        point -= leading;

        if (digits.Length == 0)
            return sign + "0.0";

        if (point <= -4 || point > 16)
        {
            var exp = point - 1;
            var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            return sign + mantissa + "e" + (exp < 0 ? "-" : "+")
                + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        if (point <= 0)
            return sign + "0." + new string('0', -point) + digits;
        if (point >= digits.Length)
            return sign + digits + new string('0', point - digits.Length) + ".0";
        return sign + digits[..point] + "." + digits[point..];
    }

    /// <summary>
    /// Python's <c>repr(str)</c>: single quotes unless the text has a single
    /// quote and no double one; backslash escapes for the quote, backslash,
    /// \t \n \r; \xhh for other control characters and for non-printable
    /// characters up to U+00FF; \uhhhh and \Uhhhhhhhh above that. Printable
    /// non-ASCII text is left as it is. Lone surrogates, which JSON can carry
    /// and Python keeps, are escaped rather than mangled.
    /// </summary>
    internal static string StrRepr(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);

        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }

            if (cp == quote || cp == '\\')
                sb.Append('\\').Append((char)cp);
            else if (cp == '\t')
                sb.Append("\\t");
            else if (cp == '\n')
                sb.Append("\\n");
            else if (cp == '\r')
                sb.Append("\\r");
            else if (cp < 0x20 || cp == 0x7F)
                sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            else if (cp < 0x7F)
                sb.Append((char)cp);
            else if (IsPrintable(cp))
                sb.Append(char.ConvertFromUtf32(cp));
            else if (cp <= 0xFF)
                sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            else if (cp <= 0xFFFF)
                sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
            else
                sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
        }

        return sb.Append(quote).ToString();
    }

    /// <summary>str.isprintable() for one code point: everything but Unicode's "Other" and "Separator" categories, space excepted.</summary>
    private static bool IsPrintable(int cp)
    {
        if (cp is >= 0xD800 and <= 0xDFFF)
            return false;
        return CharUnicodeInfo.GetUnicodeCategory(cp) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => false,
            UnicodeCategory.SpaceSeparator => cp == ' ',
            _ => true,
        };
    }
}

/// <summary>
/// A JSON object as a Python dict: insertion-ordered, with <c>d.get()</c>
/// semantics. <see cref="TryGet"/> distinguishes an absent key from one present
/// with a JSON null, which relay.py's <c>d.get(key, '?')</c> did — absent reads
/// "?", null reads "None".
/// </summary>
internal sealed class PyDict
{
    public static readonly PyDict Empty = new([]);

    private readonly KeyValuePair<string, PyValue>[] _items;
    private readonly Dictionary<string, PyValue> _lookup;

    private PyDict(KeyValuePair<string, PyValue>[] items)
    {
        _items = items;
        _lookup = new Dictionary<string, PyValue>(items.Length, StringComparer.Ordinal);
        foreach (var (key, value) in items)
            _lookup[key] = value;
    }

    /// <summary>
    /// A repeated key keeps its first position and takes its last value, which
    /// is what json.loads does with <c>{"a": 1, "a": 2}</c>.
    /// </summary>
    public static PyDict FromJson(JsonObject obj)
    {
        var items = new List<KeyValuePair<string, PyValue>>(obj.Count);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, node) in obj)
        {
            var value = PyValue.FromJson(node);
            if (index.TryGetValue(key, out var at))
                items[at] = new(key, value);
            else
            {
                index[key] = items.Count;
                items.Add(new(key, value));
            }
        }
        return new PyDict(items.ToArray());
    }

    public int Count => _items.Length;

    public IReadOnlyList<KeyValuePair<string, PyValue>> Items => _items;

    public bool ContainsKey(string key) => _lookup.ContainsKey(key);

    public bool TryGet(string key, out PyValue value) => _lookup.TryGetValue(key, out value!);

    /// <summary><c>d.get(key)</c>: None when absent.</summary>
    public PyValue Get(string key) => Get(key, PyValue.None);

    /// <summary><c>d.get(key, fallback)</c>.</summary>
    public PyValue Get(string key, PyValue fallback) => _lookup.TryGetValue(key, out var v) ? v : fallback;
}
