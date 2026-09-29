using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Tests;

public class QuickConnectTests
{
    [Theory]
    [InlineData("ssh admin@10.0.0.1:2222", ProtocolKind.Ssh, "10.0.0.1", 2222, "admin")]
    [InlineData("telnet 10.0.0.1 2001", ProtocolKind.Telnet, "10.0.0.1", 2001, "")]
    [InlineData("telnet sw1", ProtocolKind.Telnet, "sw1", 23, "")]
    [InlineData("admin@sw1", ProtocolKind.Ssh, "sw1", 22, "admin")]
    [InlineData("  sw1  ", ProtocolKind.Ssh, "sw1", 22, "")]
    public void Parses(string input, ProtocolKind proto, string host, int port, string user)
    {
        var p = QuickConnectParser.Parse(input)!;
        Assert.Equal((proto, host, port, user), (p.Protocol, p.Host, p.Port, p.Username));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ssh")]
    [InlineData("ssh @")]
    [InlineData("host:99999")]
    public void RejectsUnusable(string input) => Assert.Null(QuickConnectParser.Parse(input));
}
