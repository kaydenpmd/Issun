using Issun.Core.Platform;

namespace Issun.Core.Tests.Platform;

/// <summary>
/// Expected values come from CPython 3.14.7 — the interpreter relay.py runs
/// under on the owner's machine — generated once and committed here.
/// </summary>
public class PyTextTests
{
    /// <summary>[c for c in range(0x110000) if chr(c).isspace()]</summary>
    private static readonly int[] PythonWhitespace =
    [
        0x9, 0xa, 0xb, 0xc, 0xd, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x85, 0xa0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a,
        0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
    ];

    [Fact]
    public void Whitespace_is_exactly_what_python_strips()
    {
        var ours = Enumerable.Range(0, 0x10000).Where(c => PyText.IsSpace((char)c)).ToArray();
        Assert.Equal(PythonWhitespace, ours);
    }

    [Fact]
    public void Strip_removes_what_python_removes_and_dotnet_trim_would_not()
    {
        // U+001C..U+001F are Python whitespace; string.Trim() leaves them.
        Assert.Equal("key", PyText.Strip("\u001C\u001F key \u3000\u0085"));
        Assert.Equal("a b", PyText.Strip("\u00A0a b\u2003"));
        // U+200B (zero-width space) and U+FEFF are not whitespace to Python.
        Assert.Equal("\u200Bkey\uFEFF", PyText.Strip(" \u200Bkey\uFEFF "));
        Assert.Equal("", PyText.Strip(" \t "));
    }

    [Fact]
    public void Strip_of_a_character_removes_every_copy_at_both_ends()
    {
        Assert.Equal("quoted", PyText.Strip("\"\"quoted\"", '"'));
        Assert.Equal("'x'", PyText.Strip("\"'x'\"", '"'));
    }

    [Theory]
    // repr(s.splitlines()) for each input
    [InlineData("x\r\ny\n\nz\n", new[] { "x", "y", "", "z" })]
    [InlineData("", new string[0])]
    [InlineData("\n", new[] { "" })]
    [InlineData("a\r\r\nb", new[] { "a", "", "b" })]
    [InlineData("a\u000Bb\u000Cc\u001Cd\u001De\u001Ef\u0085g\u2028h\u2029i", new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" })]
    [InlineData("no break\u001F here", new[] { "no break\u001F here" })]
    public void SplitLines_breaks_where_python_does(string text, string[] expected)
    {
        Assert.Equal(expected, PyText.SplitLines(text));
    }

    [Theory]
    // int(s) from CPython 3.14.7; false where it raised ValueError.
    [InlineData("8787", true, 8787)]
    [InlineData(" 8787 ", true, 8787)]
    [InlineData("+8_788", true, 8788)]
    [InlineData("-1", true, -1)]
    [InlineData("08787", true, 8787)]
    [InlineData("8__787", false, 0)]
    [InlineData("_8787", false, 0)]
    [InlineData("8787_", false, 0)]
    [InlineData("87 87", false, 0)]
    [InlineData("8787.0", false, 0)]
    [InlineData("0x22", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("\uFF18\uFF17\uFF18\uFF17", true, 8787)]
    [InlineData("\u0668\u0667\u0668\u0667", true, 8787)]
    [InlineData("\U0001D7D6\U0001D7D5\U0001D7D6\U0001D7D5", true, 8787)]
    [InlineData("\u00B2", false, 0)]
    [InlineData("+-1", false, 0)]
    [InlineData("--1", false, 0)]
    [InlineData("1e3", false, 0)]
    [InlineData("\u20008787\u3000", true, 8787)]
    [InlineData("8\u0337", false, 0)]
    public void TryParseInt_matches_python_int(string text, bool ok, long expected)
    {
        Assert.Equal(ok, PyText.TryParseInt(text, out var value));
        if (ok)
            Assert.Equal(expected, value);
    }

    [Theory]
    // float(s) from CPython 3.14.7. nonFinite where Python returned inf or nan,
    // which Issun refuses rather than imports.
    [InlineData("0.35", true, false, 0.35)]
    [InlineData(" 0.5 ", true, false, 0.5)]
    [InlineData("1_0e-1", true, false, 1.0)]
    [InlineData(".5", true, false, 0.5)]
    [InlineData("5.", true, false, 5.0)]
    [InlineData("1e", false, false, 0.0)]
    [InlineData("e5", false, false, 0.0)]
    [InlineData("1e1_0", true, false, 10000000000.0)]
    [InlineData("1_.5", false, false, 0.0)]
    [InlineData("1._5", false, false, 0.0)]
    [InlineData("+.25", true, false, 0.25)]
    [InlineData("-0", true, false, -0.0)]
    [InlineData("0,35", false, false, 0.0)]
    [InlineData("", false, false, 0.0)]
    [InlineData(".", false, false, 0.0)]
    [InlineData("1.2.3", false, false, 0.0)]
    [InlineData("inf", false, true, 0.0)]
    [InlineData("-Infinity", false, true, 0.0)]
    [InlineData("nan", false, true, 0.0)]
    [InlineData("+NaN", false, true, 0.0)]
    [InlineData("1e400", false, true, 0.0)]
    [InlineData("-1e400", false, true, 0.0)]
    [InlineData("\uFF10.\uFF13\uFF15", true, false, 0.35)]
    [InlineData("0x1p3", false, false, 0.0)]
    [InlineData("1e-400", true, false, 0.0)]
    [InlineData("in_f", false, false, 0.0)]
    [InlineData("  -2.5e-3\t", true, false, -0.0025)]
    [InlineData("\u00B9", false, false, 0.0)]
    [InlineData("\U0001D7CE.\U0001D7D3", true, false, 0.5)]
    [InlineData("1E+2", true, false, 100.0)]
    [InlineData("1e+_2", false, false, 0.0)]
    public void TryParseFloat_matches_python_float(string text, bool ok, bool nonFinite, double expected)
    {
        Assert.Equal(ok, PyText.TryParseFloat(text, out var value, out var wasNonFinite));
        Assert.Equal(nonFinite, wasNonFinite);
        if (ok)
            Assert.Equal(expected, value);
    }

    [Fact]
    public void Float_parsing_ignores_the_machine_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma would read "0.35" as 35.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(PyText.TryParseFloat("0.35", out var value, out _));
            Assert.Equal(0.35, value);
            Assert.False(PyText.TryParseFloat("0,35", out _, out _));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Lower_ignores_the_machine_culture_and_keeps_pythons_dotted_capital_i()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("on", PyText.Lower("ON"));
            Assert.Equal("details", PyText.Lower("DETAILS"));
            // 'DETAİLS'.lower() == 'detai̇ls' in Python — not "details".
            Assert.Equal("detai\u0307ls", PyText.Lower("DETA\u0130LS"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }
}
