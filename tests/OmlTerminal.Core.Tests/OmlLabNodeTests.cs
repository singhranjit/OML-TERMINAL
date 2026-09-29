using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Tests;

public class OmlLabNodeReadyToConnectTests
{
    private static OmlLabNode Node(string status, OmlConsoleType type, int? consolePort = null, int? rdpPort = null, int? vncWsPort = null) =>
        new("id", "N", "router", "tmpl", status, type, consolePort, null, vncWsPort, rdpPort);

    [Fact]
    public void StoppedNode_IsNeverReadyRegardlessOfPorts()
        => Assert.False(Node("stopped", OmlConsoleType.Telnet, consolePort: 2001).IsReadyToConnect);

    [Fact]
    public void RunningTelnetWithoutConsolePort_IsNotReady()
        => Assert.False(Node("running", OmlConsoleType.Telnet).IsReadyToConnect);

    [Fact]
    public void RunningTelnetWithConsolePort_IsReady()
        => Assert.True(Node("running", OmlConsoleType.Telnet, consolePort: 2001).IsReadyToConnect);

    [Fact]
    public void RunningRdpNeedsRdpPortSpecifically()
    {
        Assert.False(Node("running", OmlConsoleType.Rdp, consolePort: 2001).IsReadyToConnect); // wrong port field
        Assert.True(Node("running", OmlConsoleType.Rdp, rdpPort: 13391).IsReadyToConnect);
    }

    [Fact]
    public void RunningVncNeedsVncWsPortSpecifically()
    {
        Assert.False(Node("running", OmlConsoleType.Vnc, consolePort: 2001).IsReadyToConnect);
        Assert.True(Node("running", OmlConsoleType.Vnc, vncWsPort: 6915).IsReadyToConnect);
    }

    [Fact]
    public void ConsoleTypeNone_IsNeverReady()
        => Assert.False(Node("running", OmlConsoleType.None).IsReadyToConnect);
}
