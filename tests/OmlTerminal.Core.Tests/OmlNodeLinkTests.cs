using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Tests;

public class OmlNodeLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static object Payload(Action<Dictionary<string, object?>>? tweak = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["v"] = 1, ["protocol"] = "ssh", ["host"] = "10.20.30.40", ["port"] = 2222,
            ["username"] = "student", ["password"] = "pw", ["label"] = "Lab R1", ["source"] = "oml-labs",
            ["issuedAt"] = Now.ToUnixTimeSeconds() - 30,
        };
        tweak?.Invoke(d);
        return d;
    }

    private static OmlLinkResult Parse(Action<Dictionary<string, object?>>? tweak = null) =>
        OmlNodeLink.Parse(OmlNodeLink.Build(Payload(tweak)), Now);

    [Fact]
    public void ValidLink_BecomesProfile()
    {
        var r = Parse();
        Assert.True(r.Ok, r.Error);
        var p = r.Profile!;
        Assert.Equal((ProtocolKind.Ssh, "10.20.30.40", 2222, "student", "pw", "Lab R1"), (p.Protocol, p.Host, p.Port, p.Username, p.Password, p.Name));
        Assert.Equal("oml-labs", r.Source);
    }

    [Fact]
    public void PortDefaultsFromProtocol_AndLabelFallsBackToHost()
    {
        var r = Parse(d => { d.Remove("port"); d["protocol"] = "telnet"; d.Remove("label"); });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(23, r.Profile!.Port);
        Assert.Equal("10.20.30.40", r.Profile.Name);
    }

    [Theory]
    [InlineData("v", 2)]
    [InlineData("protocol", "rdp")]
    [InlineData("host", "bad host;calc")]
    [InlineData("host", "")]
    [InlineData("port", 70000)]
    [InlineData("port", "22")]
    [InlineData("source", "")]
    public void RejectsInvalidFields(string field, object value)
        => Assert.False(Parse(d => d[field] = value).Ok);

    [Theory]
    [InlineData("v")]
    [InlineData("protocol")]
    [InlineData("host")]
    [InlineData("source")]
    [InlineData("issuedAt")]
    public void RejectsMissingRequiredFields(string field)
        => Assert.False(Parse(d => d.Remove(field)).Ok);

    [Fact]
    public void ExpiredExplicitly_IsRejected()
    {
        var r = Parse(d => d["expiresAt"] = Now.ToUnixTimeSeconds() - 1);
        Assert.False(r.Ok);
        Assert.Contains("expired", r.Error);
    }

    [Fact]
    public void NoExpiry_MeansShortDefaultLifetime()
    {
        Assert.True(Parse(d => d["issuedAt"] = Now.ToUnixTimeSeconds() - 600).Ok);
        Assert.False(Parse(d => d["issuedAt"] = Now.ToUnixTimeSeconds() - 3600).Ok);
    }

    [Fact]
    public void FutureIssuedAt_IsRejected_ButSmallSkewIsFine()
    {
        Assert.False(Parse(d => d["issuedAt"] = Now.ToUnixTimeSeconds() + 3600).Ok);
        Assert.True(Parse(d => d["issuedAt"] = Now.ToUnixTimeSeconds() + 60).Ok);
    }

    [Fact]
    public void AcceptsIsoTimestampsAndMilliseconds()
    {
        Assert.True(Parse(d => { d["issuedAt"] = Now.AddSeconds(-5).ToString("O"); d["expiresAt"] = Now.AddMinutes(5).ToString("O"); }).Ok);
        Assert.True(Parse(d => d["issuedAt"] = Now.ToUnixTimeMilliseconds() - 1000).Ok);
    }

    [Fact]
    public void ControlCharactersAreStrippedFromLabel()
    {
        var r = Parse(d => d["label"] = "Lab\u001b[31m R1\r\n");
        Assert.True(r.Ok);
        Assert.DoesNotContain('\u001b', r.Profile!.Name);
        Assert.DoesNotContain('\n', r.Profile.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/connect?data=abc")]
    [InlineData("oml-terminal://open?data=abc")]
    [InlineData("oml-terminal://connect")]
    [InlineData("oml-terminal://connect?data=***not-base64***")]
    [InlineData("oml-terminal://connect?data=bm90IGpzb24")]
    public void RejectsGarbage(string? link) => Assert.False(OmlNodeLink.Parse(link, Now).Ok);

    [Fact]
    public void RejectsOversizedLink()
        => Assert.False(OmlNodeLink.Parse("oml-terminal://connect?data=" + new string('A', 5000), Now).Ok);

    [Fact]
    public void RejectsNonObjectJson()
        => Assert.False(OmlNodeLink.Parse(OmlNodeLink.Build(new[] { 1, 2, 3 }), Now).Ok);
}
