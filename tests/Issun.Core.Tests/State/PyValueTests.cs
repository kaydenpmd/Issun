using System.Numerics;
using System.Text.Json.Nodes;
using Issun.Core.State;

namespace Issun.Core.Tests.State;

public class PyValueTests
{
    public static TheoryData<ulong, string> FloatReprs()
    {
        var data = new TheoryData<ulong, string>();
        foreach (var (bits, repr) in RelayParityData.FloatReprs)
            data.Add(bits, repr);
        return data;
    }

    [Theory]
    [MemberData(nameof(FloatReprs))]
    public void Float_repr_matches_python(ulong bits, string expected)
    {
        Assert.Equal(expected, PyValue.FloatRepr(BitConverter.UInt64BitsToDouble(bits)));
    }

    [Fact]
    public void Str_repr_matches_python()
    {
        foreach (var (value, expected) in RelayParityData.StrReprs)
            Assert.Equal(expected, PyValue.StrRepr(value));
    }

    [Theory]
    [InlineData("3", "Int", "3")]
    [InlineData("-0", "Int", "0")]
    [InlineData("3.0", "Float", "3.0")]
    [InlineData("3e0", "Float", "3.0")]
    [InlineData("-0.0", "Float", "-0.0")]
    [InlineData("123456789012345678901234567890", "Int", "123456789012345678901234567890")]
    [InlineData("true", "Bool", "True")]
    [InlineData("null", "None", "None")]
    [InlineData("\"x\"", "Str", "'x'")]
    public void Json_numbers_keep_the_type_json_loads_gave_them(string json, string kind, string repr)
    {
        var value = PyValue.FromJson(JsonNode.Parse(json));
        Assert.Equal(kind, value.Kind.ToString());
        Assert.Equal(repr, value.Repr());
    }

    [Fact]
    public void A_node_built_in_code_keeps_its_clr_type()
    {
        // Serialising a double 5.0 writes "5", which would read back as an int.
        Assert.Equal(PyKind.Float, PyValue.FromJson(JsonValue.Create(5.0)).Kind);
        Assert.Equal(PyKind.Int, PyValue.FromJson(JsonValue.Create(5L)).Kind);
    }

    [Fact]
    public void Bools_are_not_ints_here()
    {
        // Python's isinstance(True, int) is True. Issun's deliberate deviation:
        // a JSON true is never an uptime or a count.
        Assert.False(PyValue.True.IsInt);
        Assert.False(PyValue.FromJson(JsonNode.Parse("1.0")).IsInt);
        Assert.True(PyValue.FromJson(JsonNode.Parse("1")).IsInt);
    }

    [Fact]
    public void Equality_follows_python_across_numeric_types()
    {
        Assert.True(PyValue.Equal(PyValue.True, PyValue.FromJson(JsonNode.Parse("1"))));
        Assert.True(PyValue.Equal(PyValue.FromJson(JsonNode.Parse("1")), PyValue.FromJson(JsonNode.Parse("1.0"))));
        Assert.False(PyValue.Equal(PyValue.FromJson(JsonNode.Parse("1")), PyValue.FromJson(JsonNode.Parse("\"1\""))));
        Assert.True(PyValue.Equal(PyValue.FromJson(JsonNode.Parse("{\"a\":1,\"b\":2}")),
                                  PyValue.FromJson(JsonNode.Parse("{\"b\":2,\"a\":1.0}"))));
    }

    [Theory]
    [InlineData("00", 0)]
    [InlineData("+01", 1)]
    [InlineData("-02", -2)]
    [InlineData(" 7 ", 7)]
    [InlineData("1_0", 10)]
    [InlineData("\x0663", 3)]           // ARABIC-INDIC DIGIT THREE
    [InlineData("\xFF11\xFF12", 12)]    // FULLWIDTH ONE, TWO
    public void Int_parses_what_python_int_accepts(string text, int expected)
    {
        Assert.True(PyText.TryParseInt(text, out var value));
        Assert.Equal(new BigInteger(expected), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("_1")]
    [InlineData("1_")]
    [InlineData("1__0")]
    [InlineData("+_1")]
    [InlineData("1.0")]
    [InlineData("aa")]
    [InlineData("1 2")]
    public void Int_rejects_what_python_int_rejects(string text)
    {
        Assert.False(PyText.TryParseInt(text, out _));
    }

    [Fact]
    public void Splitlines_breaks_where_python_does()
    {
        var text = "a\r\nb\rc\nd\ve\ff\x1cg\x1dh\x1ei\x85j\x2028k\x2029l\r\n";
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"], PyText.SplitLines(text));
        Assert.Equal(["", "x"], PyText.SplitLines("\nx"));
        Assert.Empty(PyText.SplitLines(""));
    }

    [Fact]
    public void Sorting_is_by_code_point()
    {
        // U+FF5E sorts before U+1F3B5 in Python; UTF-16 ordinal order puts the
        // emoji's surrogates (D83C) first.
        var emoji = char.ConvertFromUtf32(0x1F3B5);
        var sorted = new[] { emoji, "\xFF5E", "a", "Z" }.OrderBy(s => s, PyText.CodePointOrder).ToArray();
        Assert.Equal(["Z", "a", "\xFF5E", emoji], sorted);
    }

    [Fact]
    public void Clip_counts_code_points_and_keeps_lone_surrogates()
    {
        var emoji = char.ConvertFromUtf32(0x1F3B5);
        Assert.Equal(emoji + emoji, PyText.Clip(emoji + emoji + emoji, 2));
        Assert.Equal("\xD800x", PyText.Clip("\xD800xy", 2));
    }
}
