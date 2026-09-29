using OmlTerminal.Core.Rdp;

namespace OmlTerminal.Core.Tests;

public class RdpLauncherTests
{
    [Theory]
    [InlineData("10.0.0.1", 3389, "10.0.0.1")]
    [InlineData("10.0.0.1", 0, "10.0.0.1")]
    [InlineData("10.0.0.1", 13389, "10.0.0.1:13389")]
    [InlineData("host.example.net", 3390, "host.example.net:3390")]
    public void BuildTarget(string host, int port, string expected) => Assert.Equal(expected, RdpLauncher.BuildTarget(host, port));

    [Fact]
    public void CredentialTargetUsesTermsrvPrefix() => Assert.Equal("TERMSRV/10.0.0.1", RdpLauncher.CredentialTarget("10.0.0.1"));
}
