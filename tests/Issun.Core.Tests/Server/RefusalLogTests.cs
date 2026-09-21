using Issun.Core;
using Issun.Core.Server;

namespace Issun.Core.Tests.Server;

public class RefusalLogTests
{
    [Fact]
    public void One_line_per_kind_per_minute_with_a_count_of_what_was_held()
    {
        var clock = new ManualClock();
        var refusals = new RefusalLog(clock);

        using var capture = Log.Capture();
        refusals.Note("401 wrong", "[http] 401 wrong");
        refusals.Note("401 wrong", "[http] 401 wrong");
        refusals.Note("404", "[http] 404 elsewhere");   // another kind is not held back
        clock.Advance(30);
        refusals.Note("401 wrong", "[http] 401 wrong");
        clock.Advance(RefusalLog.WindowSeconds - 30);   // exactly a minute after the first line
        refusals.Note("401 wrong", "[http] 401 wrong");
        refusals.Note("401 wrong", "[http] 401 wrong");
        clock.Advance(RefusalLog.WindowSeconds);
        refusals.Note("401 wrong", "[http] 401 wrong");

        Assert.Equal(
        [
            "[http] 401 wrong",
            "[http] 404 elsewhere",
            "[http] 401 wrong  (+2 more like it since the last line)",
            "[http] 401 wrong  (+1 more like it since the last line)",
        ], capture.Lines);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/now-playing", "/now-playing")]
    [InlineData("/a\r\n2026-09-21T00:00:00  [uptime] forged", "/a??2026-09-21T00:00:00  [uptime] forged")]
    [InlineData("/tab\there\u0000", "/tab?here?")]
    public void Request_text_cannot_forge_log_lines(string? text, string expected)
    {
        Assert.Equal(expected, RefusalLog.Printable(text));
    }

    [Fact]
    public void Request_text_is_capped()
    {
        var printable = RefusalLog.Printable("/" + new string('a', 500));
        Assert.Equal(121, printable.Length);
        Assert.EndsWith("a…", printable);
        Assert.Equal("GET…", RefusalLog.Printable("GETX", 3));
    }
}
