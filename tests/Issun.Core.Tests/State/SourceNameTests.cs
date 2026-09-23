using System.Text.Json.Nodes;
using Issun.Core.State;

namespace Issun.Core.Tests.State;

public class SourceNameTests
{
    [Theory]
    [InlineData("\"Ammy\"", "Ammy")]
    [InlineData("\"  Ammy  \"", "Ammy")]
    [InlineData("\"\"", null)]
    [InlineData("\"   \"", null)]
    [InlineData("12", null)]
    [InlineData("true", null)]
    [InlineData("null", null)]
    public void App_name_is_kept_only_when_it_is_a_real_string(string json, string? expected)
    {
        var diag = new PhoneDiagnostics();
        diag.RecordName(JsonNode.Parse(json));
        Assert.Equal(expected, diag.SourceName);
    }

    [Fact]
    public void A_push_without_a_name_keeps_the_last_one()
    {
        var diag = new PhoneDiagnostics();
        diag.RecordName(JsonValue.Create("Ammy"));
        diag.RecordName(null);
        Assert.Equal("Ammy", diag.SourceName);
    }
}
