using System.Net;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Tests;

public class NetToolsTests
{
    [Fact]
    public void PortSpec_ParsesListsRangesAndPresets()
    {
        Assert.Equal([22, 80, 443], PortSpec.Parse("443, 22 80"));
        Assert.Equal([8000, 8001, 8002], PortSpec.Parse("8002-8000"));
        Assert.Equal([53, 443], PortSpec.Parse("tcp/443,udp/53"));
        Assert.Contains(8443, PortSpec.Parse("web"));
        Assert.Throws<FormatException>(() => PortSpec.Parse("70000"));
        Assert.Throws<FormatException>(() => PortSpec.Parse("http"));
    }

    [Theory]
    [InlineData("10.1.2.3/24", "10.1.2.0", 24)]
    [InlineData("10.1.2.3 255.255.255.0", "10.1.2.0", 24)]
    [InlineData("10.1.2.3/255.255.254.0", "10.1.2.0", 23)]
    [InlineData("10.1.2.3 0.0.0.255", "10.1.2.0", 24)] // Cisco wildcard mask
    [InlineData("10.1.2.3", "10.1.2.3", 32)]
    [InlineData("2001:db8:abcd::1/48", "2001:db8:abcd::", 48)]
    public void Subnet_ParsesEveryCommonNotation(string text, string network, int prefix)
    {
        var s = Subnet.Parse(text);
        Assert.Equal(IPAddress.Parse(network), s.Network);
        Assert.Equal(prefix, s.PrefixLength);
    }

    [Fact]
    public void Subnet_ComputesHostRangeMaskAndWildcard()
    {
        var s = Subnet.Parse("192.168.10.77/26");
        Assert.Equal("192.168.10.64", s.Network.ToString());
        Assert.Equal("192.168.10.127", s.Broadcast.ToString());
        Assert.Equal("192.168.10.65", s.FirstHost.ToString());
        Assert.Equal("192.168.10.126", s.LastHost.ToString());
        Assert.Equal("255.255.255.192", s.SubnetMask.ToString());
        Assert.Equal("0.0.0.63", s.Wildcard.ToString());
        Assert.Equal(62, (int)s.UsableHosts);
        Assert.Equal("Private (RFC 1918)", s.Classification);
    }

    [Fact]
    public void Subnet_PointToPointLinksCountBothAddresses()
    {
        Assert.Equal(2, (int)Subnet.Parse("10.0.0.0/31").UsableHosts);
        Assert.Equal(1, (int)Subnet.Parse("10.0.0.1/32").UsableHosts);
    }

    [Fact]
    public void Subnet_RejectsGarbageAndNonContiguousMasks()
    {
        Assert.False(Subnet.TryParse("10.1.1", out _));
        Assert.False(Subnet.TryParse("10.1.1.1/33", out _));
        Assert.False(Subnet.TryParse("10.1.1.1 255.0.255.0", out _));
        Assert.False(Subnet.TryParse("example.com", out _));
    }

    [Fact]
    public void RangeToCidrs_CoversRangeExactly()
    {
        var cidrs = Ipv4.RangeToCidrs(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.20")).Select(c => c.Cidr);
        Assert.Equal(["10.0.0.5/32", "10.0.0.6/31", "10.0.0.8/29", "10.0.0.16/30", "10.0.0.20/32"], cidrs);
    }

    [Fact]
    public void Summarize_MergesAdjacentPrefixes()
    {
        var merged = Ipv4.Summarize(new[] { "10.0.0.0/25", "10.0.0.128/25", "10.0.1.0/24", "10.0.1.7/32" }.Select(Subnet.Parse));
        Assert.Equal(["10.0.0.0/23"], merged.Select(m => m.Cidr));
    }

    [Fact]
    public void Split_ProducesEqualSubnets()
    {
        var parts = Subnet.Parse("10.0.0.0/24").Split(26);
        Assert.Equal(["10.0.0.0/26", "10.0.0.64/26", "10.0.0.128/26", "10.0.0.192/26"], parts.Select(p => p.Cidr));
    }

    [Fact]
    public void Dns_QueryAndReplyRoundTripWithCompression()
    {
        var query = DnsLookup.BuildQuery(0x1234, "example.com", DnsRecordType.A);
        // Reply = query header with QR/RA set + answer "example.com A 93.184.216.34" using a pointer to offset 12.
        var reply = query.ToList();
        reply[2] = 0x81; reply[3] = 0x80;
        reply[7] = 1; // ANCOUNT
        reply.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x0E, 0x10, 0x00, 0x04, 93, 184, 216, 34 });
        var parsed = DnsLookup.Parse(reply.ToArray(), 0x1234);
        Assert.Equal("NOERROR", parsed.ResponseCode);
        var rec = Assert.Single(parsed.Records);
        Assert.Equal(("example.com.", DnsRecordType.A, 3600u, "93.184.216.34"), (rec.Name, rec.Type, rec.Ttl, rec.Data));
    }

    [Fact]
    public void Dns_ReverseNamesForPtr()
    {
        Assert.Equal("4.3.2.1.in-addr.arpa", DnsLookup.ReverseName(IPAddress.Parse("1.2.3.4")));
        Assert.EndsWith(".8.b.d.0.1.0.0.2.ip6.arpa", DnsLookup.ReverseName(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void LocalPorts_DecodesNetworkOrderPort()
    {
        Assert.Equal(443, LocalPorts.PortFromDword(0xBB01)); // 443 = 0x01BB, stored byte-swapped
        Assert.Equal("LISTEN", LocalPorts.TcpStateName(2));
    }

    [Fact]
    public void UdpProbe_UsesProtocolPayloadForKnownPorts()
    {
        Assert.Equal(48, PortScanner.UdpProbe(123).Length);
        Assert.Equal(0x30, PortScanner.UdpProbe(161)[0]);
    }

    [Fact]
    public void PortScanner_PrintableCollapsesBanner()
    {
        Assert.Equal("SSH-2.0-OpenSSH_9.6", PortScanner.Printable("SSH-2.0-OpenSSH_9.6\r\n\0"));
    }

    [Fact]
    public async Task PortScanner_FindsLocalListener()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var results = new List<PortProbeResult>();
            await new PortScanner { Timeout = TimeSpan.FromSeconds(2) }
                .ScanAsync("127.0.0.1", [port], PortProtocol.Tcp, r => { lock (results) results.Add(r); });
            Assert.Equal(PortState.Open, Assert.Single(results).State);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task PortScanner_ReportsRefusedPortAsClosedNotFiltered()
    {
        // Find a port nothing listens on: bind, note the port, release it.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new PortScanner { Timeout = TimeSpan.FromMilliseconds(1500) }
            .ProbeTcpAsync("127.0.0.1", IPAddress.Loopback, port, CancellationToken.None);
        Assert.Equal(PortState.Closed, r.State);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms - Windows' RST retries weren't disabled");
    }

    [Fact]
    public void CaptureFilter_BuildsBpf()
    {
        var f = new CaptureFilter { Host = "10.1.1.1, 10.2.0.0/16", Port = "443,8000-8010", Protocol = "tcp" };
        Assert.Equal("tcp and host 10.1.1.1 and net 10.2.0.0/16 and (port 443 or portrange 8000-8010)", f.ToBpf());
        Assert.Equal("", new CaptureFilter().ToBpf());
    }

    [Fact]
    public void CaptureCommands_FortiGateAndTcpdump()
    {
        var filter = new CaptureFilter { Host = "8.8.8.8", Protocol = "icmp" };
        Assert.Equal("diagnose sniffer packet wan1 'icmp and host 8.8.8.8' 4 50 l",
            CaptureCommands.Build(new CaptureRequest { Platform = CapturePlatform.FortiGateSniffer, Interface = "wan1", Filter = filter, Count = 50 }));
        Assert.Equal("diagnose sniffer packet any none 4 0 l",
            CaptureCommands.Build(new CaptureRequest { Platform = CapturePlatform.FortiGateSniffer }));
        Assert.Equal("sudo -S -p '' tcpdump -i eth0 -nn -U -w - 'icmp and host 8.8.8.8'",
            CaptureCommands.Build(new CaptureRequest { Platform = CapturePlatform.RemoteTcpdump, Interface = "eth0", Filter = filter, PcapFile = "x.pcap" }));
        Assert.Equal("tcpdump -i any -nn -c 10 -l",
            CaptureCommands.Build(new CaptureRequest { Platform = CapturePlatform.RemoteTcpdump, UseSudo = false, Count = 10 }));
    }
}
