using OmlTerminal.Core.Topology;

namespace OmlTerminal.Core.Tests;

public class TopologyTests
{
    private const string IosCdpDetail =
        "-------------------------\n" +
        "Device ID: dist-sw1.corp.example.com\n" +
        "Entry address(es): \n" +
        "  IP address: 10.0.0.2\n" +
        "Platform: cisco WS-C3850-48P,  Capabilities: Router Switch IGMP \n" +
        "Interface: GigabitEthernet1/0/49,  Port ID (outgoing port): GigabitEthernet1/0/1\n" +
        "Holdtime : 155 sec\n\n" +
        "Version :\nCisco IOS Software, IOS-XE Software\n\n" +
        "Management address(es): \n" +
        "  IP address: 192.168.0.2\n\n" +
        "-------------------------\n" +
        "Device ID: SEP001122334455\n" +
        "Entry address(es): \n" +
        "  IP address: 10.9.9.50\n" +
        "Platform: Cisco IP Phone 8845,  Capabilities: Host Phone Two-port Mac Relay \n" +
        "Interface: GigabitEthernet1/0/5,  Port ID (outgoing port): Port 1\n" +
        "Holdtime : 170 sec\n\n" +
        "Management address(es): \n\n";

    [Fact]
    public void ParsesIosCdpDetailPreferringTheManagementAddress()
    {
        var n = NeighborParser.ParseCdpDetail(IosCdpDetail);

        Assert.Equal(2, n.Count);
        Assert.Equal("dist-sw1.corp.example.com", n[0].Name);
        Assert.Equal("192.168.0.2", n[0].MgmtIp);
        Assert.Equal("GigabitEthernet1/0/49", n[0].LocalInterface);
        Assert.Equal("GigabitEthernet1/0/1", n[0].RemoteInterface);
        Assert.Equal("WS-C3850-48P", n[0].Platform);
        Assert.Equal(NodeKind.Switch, NeighborParser.Classify(n[0].Platform, n[0].Capabilities));

        Assert.Equal("10.9.9.50", n[1].MgmtIp); // no management address advertised - falls back to the entry address
        Assert.Equal(NodeKind.Phone, NeighborParser.Classify(n[1].Platform, n[1].Capabilities));
    }

    [Fact]
    public void ParsesNxosCdpDetailUsingSystemName()
    {
        var n = Assert.Single(NeighborParser.ParseCdpDetail(
            "Device ID:N9K-2(FDO21234567)\n" +
            "System Name: N9K-2\n\n" +
            "Interface address(es): 2\n" +
            "    IPv4 Address: 10.0.0.6\n" +
            "Platform: N9K-C93180YC-EX, Capabilities: Router Switch IGMP Filtering Supports-STP-Dispute\n" +
            "Interface: Ethernet1/49, Port ID (outgoing port): Ethernet1/49\n" +
            "Holdtime: 136 sec\n\n" +
            "Mgmt address(es):\n" +
            "    IPv4 Address: 192.168.0.12\n"));

        Assert.Equal("N9K-2", n.Name);
        Assert.Equal("192.168.0.12", n.MgmtIp);
        Assert.Equal("Ethernet1/49", n.LocalInterface);
    }

    [Fact]
    public void ParsesIosLldpDetailBlocks()
    {
        var n = NeighborParser.ParseLldpDetail(
            "------------------------------------------------\n" +
            "Local Intf: Gi1/0/48\n" +
            "Chassis id: 0050.56a1.0001\n" +
            "Port id: Gi0/1\n" +
            "Port Description: GigabitEthernet0/1\n" +
            "System Name: acc-sw7.corp.example.com\n\n" +
            "System Description: \n" +
            "Cisco IOS Software, C2960X Software\n\n" +
            "Time remaining: 99 seconds\n" +
            "System Capabilities: B,R\n" +
            "Enabled Capabilities: B\n" +
            "Management Addresses:\n" +
            "    IP: 192.168.0.7\n" +
            "------------------------------------------------\n" +
            "Local Intf: Gi1/0/10\n" +
            "Chassis id: 0011.2233.4455\n" +
            "Port id: 0011.2233.4455\n" +
            "Port Description: eth0\n" +
            "System Name: printer-3\n" +
            "System Capabilities: S\n" +
            "Management Addresses - not advertised\n" +
            "------------------------------------------------\n" +
            "\nTotal entries displayed: 2\n");

        Assert.Equal(2, n.Count);
        Assert.Equal("acc-sw7.corp.example.com", n[0].Name);
        Assert.Equal("192.168.0.7", n[0].MgmtIp);
        Assert.Equal("Gi1/0/48", n[0].LocalInterface);
        Assert.Equal("Gi0/1", n[0].RemoteInterface);
        Assert.StartsWith("Cisco IOS Software", n[0].Platform);
        Assert.Equal(NodeKind.Switch, NeighborParser.Classify(n[0].Platform, n[0].Capabilities));

        Assert.Equal("eth0", n[1].RemoteInterface); // port id was a MAC - the description says more
        Assert.Null(n[1].MgmtIp);
        Assert.Equal(NodeKind.Host, NeighborParser.Classify(n[1].Platform, n[1].Capabilities));
    }

    [Fact]
    public void ParsesNxosLldpDetailWithoutSeparators()
    {
        var n = Assert.Single(NeighborParser.ParseLldpDetail(
            "Chassis id: 0050.56a1.0009\n" +
            "Port id: Ethernet1/50\n" +
            "Local Port id: Eth1/50\n" +
            "Port Description: to-n9k-1\n" +
            "System Name: n9k-2\n" +
            "System Description: Cisco Nexus Operating System (NX-OS) Software\n" +
            "Time remaining: 107 seconds\n" +
            "System Capabilities: B, R\n" +
            "Enabled Capabilities: B, R\n" +
            "Management Address: 192.168.0.12\n"));

        Assert.Equal("n9k-2", n.Name);
        Assert.Equal("Eth1/50", n.LocalInterface);
        Assert.Equal("Ethernet1/50", n.RemoteInterface);
        Assert.Equal("192.168.0.12", n.MgmtIp);
    }

    [Fact]
    public void ParsesJunosLldpTable()
    {
        var n = Assert.Single(NeighborParser.ParseJunosLldp(
            "Local Interface    Parent Interface    Chassis Id          Port info          System Name\n" +
            "ge-0/0/0           -                   00:50:56:a1:00:01   Gi0/1              core-sw1\n"));
        Assert.Equal("core-sw1", n.Name);
        Assert.Equal("ge-0/0/0", n.LocalInterface);
        Assert.Equal("Gi0/1", n.RemoteInterface);
    }

    [Theory]
    [InlineData("dist-sw1.corp.example.com", "dist-sw1")]
    [InlineData("N9K-2(FDO21234567)", "n9k-2")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("  CORE1 ", "core1")]
    public void NormalizesNames(string input, string expected) => Assert.Equal(expected, NeighborParser.NormalizeName(input));

    [Theory]
    [InlineData("CORE-SW2.corp.example.com", "CORE-SW2")]
    [InlineData("N9K-2(FDO21234567)", "N9K-2")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void DisplayNamesDropDomainButKeepCase(string input, string expected) => Assert.Equal(expected, NeighborParser.DisplayName(input));

    [Theory]
    [InlineData("GigabitEthernet1/0/1", "Gi1/0/1")]
    [InlineData("Gig 1/0/1", "Gi1/0/1")]
    [InlineData("TenGigabitEthernet1/1/1", "Te1/1/1")]
    [InlineData("Ethernet1/49", "Eth1/49")]
    [InlineData("Eth1/49", "Eth1/49")]
    [InlineData("Port-channel10", "Po10")]
    [InlineData("ge-0/0/0", "ge-0/0/0")]
    [InlineData("Port 1", "Port 1")]
    public void ShortensInterfaceNames(string input, string expected) => Assert.Equal(expected, NeighborParser.ShortInterface(input));

    [Theory]
    [InlineData("ASA5516", "Router", NodeKind.Firewall)]
    [InlineData("AIR-AP2802I-B-K9", "Trans-Bridge Source-Route-Bridge IGMP", NodeKind.AccessPoint)]
    [InlineData("ISR4331/K9", "Router Source-Route-Bridge IGMP", NodeKind.Router)]
    [InlineData("", "W", NodeKind.AccessPoint)]
    [InlineData("", "", NodeKind.Unknown)]
    public void ClassifiesDevices(string platform, string caps, NodeKind kind) => Assert.Equal(kind, NeighborParser.Classify(platform, caps));

    [Fact]
    public void TheSameCableReportedFromBothEndsOrByBothProtocolsIsOneLink()
    {
        var g = new TopologyGraph();
        Assert.True(g.AddLink("core", "Gi1/0/1", "dist", "Gi0/1", "CDP"));
        Assert.False(g.AddLink("dist", "Gi0/1", "core", "Gi1/0/1", "CDP"));
        Assert.False(g.AddLink("core", "Gi1/0/1", "dist", "0050.56a1.0001", "LLDP"));
        Assert.True(g.AddLink("core", "Gi1/0/2", "dist", "Gi0/2", "CDP")); // a second cable between the same pair
        Assert.False(g.AddLink("core", "Gi1/0/3", "core", "Gi1/0/4", "CDP"));
        Assert.Equal(2, g.Links.Count);
    }

    [Fact]
    public void ExportsCsvAndDot()
    {
        var g = new TopologyGraph();
        g.Ensure("core", "CORE").MgmtIp = "10.0.0.1";
        g.Ensure("dist", "DIST, east");
        g.AddLink("core", "Gi1/0/1", "dist", "Gi0/1", "CDP");

        Assert.Contains("CORE,Gi1/0/1,\"DIST, east\",Gi0/1,CDP", g.ToCsv());
        var dot = g.ToDot();
        Assert.Contains("\"core\" -- \"dist\"", dot);
        Assert.Contains("label=\"CORE\\n10.0.0.1\"", dot);
    }

    [Fact]
    public void LayoutPutsTheSeedOnTopAndNeighborsInBandsWithoutOverlap()
    {
        var g = new TopologyGraph();
        g.Ensure("core", "core").IsSeed = true;
        foreach (var d in new[] { "d1", "d2" }) { g.Ensure(d, d); g.AddLink("core", "x" + d, d, "up", "CDP"); }
        foreach (var a in new[] { "a1", "a2", "a3" }) { g.Ensure(a, a); g.AddLink("d1", "x" + a, a, "up", "CDP"); }
        g.Ensure("island", "island");

        var pos = TopologyLayout.Layered(g);

        Assert.Equal(7, pos.Count);
        Assert.True(pos["core"].Y < pos["d1"].Y);
        Assert.Equal(pos["d1"].Y, pos["d2"].Y);
        Assert.True(pos["d1"].Y < pos["a1"].Y);
        Assert.True(pos["a3"].Y < pos["island"].Y);
        var access = new[] { pos["a1"], pos["a2"], pos["a3"] }.OrderBy(p => p.X).ToArray();
        Assert.True(access[1].X - access[0].X >= TopologyLayout.NodeWidth);
        Assert.All(pos.Values, p => Assert.True(p.X >= 0 && p.Y >= 0));
    }
}
