using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Tests;

public class OmlLabLinkParserTests
{
    // The exact payload OML Labs sent in practice (decoded): {"v":1,"kind":"lab","omlHost":"https://192.168.1.79",
    // "labId":"6652f202-...","labName":"Palo alto ","token":"<jwt>","source":"oml-labs","issuedAt":...,"expiresAt":...}
    // Its outer expiresAt was observed to be earlier than issuedAt on real traffic, so it must not be used to reject the link.
    private const string RealWorldLink =
        "oml-terminal://connect?data=eyJ2IjoxLCJraW5kIjoibGFiIiwib21sSG9zdCI6Imh0dHBzOi8vMTkyLjE2OC4xLjc5IiwibGFiSWQiOiI2NjUyZjIwMi04NDliLTQ4ZmQtOTMyZC0xYjk3OGUyMmUzNTEiLCJsYWJOYW1lIjoiUGFsbyBhbHRvICIsInRva2VuIjoiZXlKaGJHY2lPaUpJVXpJMU5pSXNJblI1Y0NJNklrcFhWQ0o5LmV5SnpkV0lpT2lJMU0yUXpZVFV3TVMxa09EYzNMVFJqTkdNdE9ESXhPUzFqTWprMk5qazVPVEkzTmpJaUxDSnNZV0pmYVdRaU9pSTJOalV5WmpJd01pMDRORGxpTFRRNFptUXRPVE15WkMweFlqazNPR1V5TW1Vek5URWlMQ0p6WTI5d1pTSTZJbXhoWWw5c1lYVnVZMmdpTENKbGVIQWlPakUzT1RBd05qRTBORFI5Lm1rclNQS19RQ2ZWdnFhakd5bWluUUxZY0xCcndFcGpGVXBtQm0zRVo3MVUiLCJzb3VyY2UiOiJvbWwtbGFicyIsImlzc3VlZEF0IjoxNzkwMDYwNTQzNjAyLCJleHBpcmVzQXQiOjE3OTAwNDE2NDQyMjB9";

    [Fact]
    public void ParsesTheRealOmlLabsPayload()
    {
        var r = OmlLabLinkParser.Parse(RealWorldLink);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("https://192.168.1.79", r.Link!.OmlHost);
        Assert.Equal("6652f202-849b-48fd-932d-1b978e22e351", r.Link.LabId);
        Assert.Equal("Palo alto", r.Link.LabName); // control/whitespace-cleaned, trailing space trimmed
        Assert.Equal("oml-labs", r.Link.Source);
        Assert.StartsWith("eyJ", r.Link.Token); // opaque JWT, not decoded/verified client-side
    }

    [Fact]
    public void TheOldSingleNodeParserCorrectlyRejectsALabLink()
        // This was the original bug report: a "kind":"lab" link doesn't have protocol/host fields, so the
        // single-node parser must reject it (not crash), leaving the caller to try lab parsing instead.
        => Assert.False(OmlNodeLink.Parse(RealWorldLink, DateTimeOffset.UtcNow).Ok);

    [Fact]
    public void TheLabParserRejectsASingleNodeLink()
    {
        var nodeLink = OmlNodeLink.Build(new { v = 1, protocol = "ssh", host = "10.0.0.1", source = "x", issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        Assert.False(OmlLabLinkParser.Parse(nodeLink).Ok);
    }

    private static string BuildLab(object overrides) => OmlNodeLink.Build(Merge(new Dictionary<string, object?>
    {
        ["v"] = 1, ["kind"] = "lab", ["omlHost"] = "https://192.168.1.79", ["labId"] = "lab-1",
        ["labName"] = "Test Lab", ["token"] = "tok", ["source"] = "oml-labs",
    }, overrides));

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> baseDict, object overrides)
    {
        foreach (var prop in overrides.GetType().GetProperties()) baseDict[prop.Name] = prop.GetValue(overrides);
        return baseDict;
    }

    [Fact]
    public void RejectsMissingOmlHost() => Assert.False(OmlLabLinkParser.Parse(BuildLab(new { omlHost = (string?)null })).Ok);

    [Fact]
    public void RejectsInvalidOmlHostUrl() => Assert.False(OmlLabLinkParser.Parse(BuildLab(new { omlHost = "not a url" })).Ok);

    [Fact]
    public void RejectsMissingLabId() => Assert.False(OmlLabLinkParser.Parse(BuildLab(new { labId = "" })).Ok);

    [Fact]
    public void RejectsMissingToken() => Assert.False(OmlLabLinkParser.Parse(BuildLab(new { token = "" })).Ok);

    [Fact]
    public void RejectsWrongVersion() => Assert.False(OmlLabLinkParser.Parse(BuildLab(new { v = 2 })).Ok);

    [Fact]
    public void RejectsNonLabKind() => Assert.False(OmlLabLinkParser.Parse(BuildLab(new { kind = "node" })).Ok);

    [Fact]
    public void MissingLabNameFallsBackToLabId() => Assert.Equal("lab-1", OmlLabLinkParser.Parse(BuildLab(new { labName = (string?)null })).Link!.LabName);

    [Fact]
    public void TrailingSlashOnOmlHostIsStripped() => Assert.Equal("https://192.168.1.79", OmlLabLinkParser.Parse(BuildLab(new { omlHost = "https://192.168.1.79/" })).Link!.OmlHost);

    [Theory]
    [InlineData("")]
    [InlineData("not a link at all")]
    [InlineData("oml-terminal://connect?data=***not-base64***")]
    public void RejectsGarbage(string link) => Assert.False(OmlLabLinkParser.Parse(link).Ok);
}

public class OmlLabNodeTests
{
    private static OmlLabNode Node(string status = "running", OmlConsoleType type = OmlConsoleType.Telnet, int? port = 2001) =>
        new("id-1", "R1", "router", "cisco-iosv", status, type, port, null, null, null);

    [Fact]
    public void IsRunning_IsCaseInsensitive()
    {
        Assert.True(Node(status: "running").IsRunning);
        Assert.True(Node(status: "RUNNING").IsRunning);
        Assert.False(Node(status: "stopped").IsRunning);
    }

    [Fact]
    public void Connectable_FollowsConsoleTypeNotStatus()
    {
        // Connectable reflects "do we know how to reach this console type at all"; a stopped node is still
        // Connectable=true, the port just won't be populated - the caller shows "start it first" instead.
        Assert.True(Node(status: "stopped", type: OmlConsoleType.Vnc).Connectable);
        Assert.False(Node(type: OmlConsoleType.None).Connectable);
    }
}
