using OmlTerminal.Core.Parsing;

namespace OmlTerminal.Core.Tests;

public class ShowTableParserTests
{
    [Fact]
    public void ParsesIosIpInterfaceBriefIncludingTwoWordStatus()
    {
        var t = ShowTableParser.Parse(
            "SW1#show ip interface brief\n" +
            "Interface              IP-Address      OK? Method Status                Protocol\n" +
            "Vlan1                  unassigned      YES NVRAM  administratively down down    \n" +
            "Vlan10                 10.1.10.1       YES NVRAM  up                    up      \n" +
            "GigabitEthernet1/0/1   unassigned      YES unset  down                  down    \n" +
            "SW1#")!;

        Assert.Equal("IP interface brief", t.Title);
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal("administratively down", t.Cell(t.Rows[0], "Status"));
        Assert.Equal("down", t.Cell(t.Rows[0], "Protocol"));
        Assert.Equal("10.1.10.1", t.Cell(t.Rows[1], "IP-Address"));
    }

    [Fact]
    public void InterfaceStatusKeepsRightAlignedSpeedAndNamesWithSpaces()
    {
        var t = ShowTableParser.Parse(
            "Port      Name               Status       Vlan       Duplex  Speed Type\n" +
            "Gi1/0/1                      connected    1          a-full a-1000 10/100/1000BaseTX\n" +
            "Gi1/0/2   Uplink to core     notconnect   1            auto   auto 10/100/1000BaseTX\n")!;

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("a-1000", t.Cell(t.Rows[0], "Speed"));
        Assert.Equal("a-full", t.Cell(t.Rows[0], "Duplex"));
        Assert.Equal("Uplink to core", t.Cell(t.Rows[1], "Name"));
        Assert.Equal("notconnect", t.Cell(t.Rows[1], "Status"));
        Assert.Equal("10/100/1000BaseTX", t.Cell(t.Rows[1], "Type"));
    }

    [Fact]
    public void CdpNeighborsJoinsLongDeviceIdWrappedOntoItsOwnLine()
    {
        var t = ShowTableParser.Parse(
            "Capability Codes: R - Router, T - Trans Bridge, B - Source Route Bridge\n" +
            "                  S - Switch, H - Host, I - IGMP, r - Repeater, P - Phone\n\n" +
            "Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID\n" +
            "core-sw-01.corp.example.com\n" +
            "                 Gig 1/0/49        157             R S I  WS-C3850- Gig 1/0/1\n" +
            "SW3              Gig 1/0/2         131              S I   WS-C2960X Gig 0/1\n\n" +
            "Total cdp entries displayed : 2\n")!;

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("core-sw-01.corp.example.com", t.Cell(t.Rows[0], "Device ID"));
        Assert.Equal("Gig 1/0/49", t.Cell(t.Rows[0], "Local Intrfce"));
        Assert.Equal("Gig 1/0/1", t.Cell(t.Rows[0], "Port ID"));
        Assert.Equal("SW3", t.Cell(t.Rows[1], "Device ID"));
    }

    [Fact]
    public void BgpSummaryKeepsMultiWordStateAndWrappedIpv6Neighbor()
    {
        var t = ShowTableParser.Parse(
            "BGP router identifier 10.0.0.1, local AS number 65001\n" +
            "Neighbor        V           AS MsgRcvd MsgSent   TblVer  InQ OutQ Up/Down  State/PfxRcd\n" +
            "10.0.0.2        4        65002    1234    1230      100    0    0 1w2d          120\n" +
            "10.0.0.6        4        65003       0       0        1    0    0 never    Idle (Admin)\n" +
            "2001:DB8::2\n" +
            "                4        65004      55      50      100    0    0 00:10:01        7\n")!;

        Assert.Equal(3, t.Rows.Count);
        Assert.Equal("120", t.Cell(t.Rows[0], "State/PfxRcd"));
        Assert.Equal("Idle (Admin)", t.Cell(t.Rows[1], "State/PfxRcd"));
        Assert.Equal("2001:DB8::2", t.Cell(t.Rows[2], "Neighbor"));
        Assert.Equal("7", t.Cell(t.Rows[2], "State/PfxRcd"));
    }

    [Fact]
    public void OspfNeighborStateWithSpacesIsNormalized()
    {
        var t = ShowTableParser.Parse(
            "Neighbor ID     Pri   State           Dead Time   Address         Interface\n" +
            "10.0.0.2          1   FULL/DR         00:00:38    10.1.1.2        GigabitEthernet0/0\n" +
            "10.0.0.3          0   FULL/  -        00:00:31    10.1.2.2        GigabitEthernet0/1\n")!;

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("FULL/DR", t.Cell(t.Rows[0], "State"));
        Assert.Equal("FULL/-", t.Cell(t.Rows[1], "State"));
        Assert.Equal("GigabitEthernet0/1", t.Cell(t.Rows[1], "Interface"));
    }

    [Fact]
    public void MacTableWorksForIosAndNxos()
    {
        var ios = ShowTableParser.Parse(
            "          Mac Address Table\n-------------------------------------------\n\n" +
            "Vlan    Mac Address       Type        Ports\n----    -----------       --------    -----\n" +
            "  10    0050.56a1.0001    DYNAMIC     Gi1/0/1\n" +
            " All    0100.0ccc.cccc    STATIC      CPU\n" +
            "Total Mac Addresses for this criterion: 2\n")!;
        Assert.Equal("MAC address table", ios.Title);
        Assert.Equal(2, ios.Rows.Count);
        Assert.Equal(new[] { "10", "0050.56a1.0001", "DYNAMIC", "Gi1/0/1" }, ios.Rows[0]);

        var nxos = ShowTableParser.Parse(
            "   VLAN     MAC Address      Type      age     Secure NTFY Ports\n" +
            "---------+-----------------+--------+---------+------+----+------------------\n" +
            "*   10     0050.56a1.0002   dynamic  0         F      F    Eth1/1\n")!;
        Assert.Equal(new[] { "10", "0050.56a1.0002", "dynamic", "Eth1/1" }, nxos.Rows[0]);
    }

    [Theory]
    [InlineData("Protocol  Address          Age (min)  Hardware Addr   Type   Interface\nInternet  10.0.0.1                -   0050.56a1.0001  ARPA   Vlan10\n", "10.0.0.1", "Vlan10")]
    [InlineData("inside 10.0.0.1 0050.56a1.0001 12\n", "10.0.0.1", "inside")]
    [InlineData("Address           Age(min)   Hardware Addr      Interface\n10.0.0.1          0          00:50:56:a1:00:01 port1\n", "10.0.0.1", "port1")]
    [InlineData("10.0.0.1 dev eth0 lladdr 00:50:56:a1:00:01 REACHABLE\n", "10.0.0.1", "eth0")]
    [InlineData("MAC Address       Address         Interface         Flags\n00:50:56:a1:00:01 10.0.0.1        ge-0/0/0.0        none\n", "10.0.0.1", "ge-0/0/0.0")]
    public void ArpTableFindsAddressAndInterfaceAcrossVendors(string output, string ip, string iface)
    {
        var t = ShowTableParser.Parse(output)!;
        Assert.Equal("ARP table", t.Title);
        Assert.Equal(ip, t.Cell(t.Rows[0], "IP Address"));
        Assert.Equal(iface, t.Cell(t.Rows[0], "Interface"));
    }

    [Fact]
    public void VlanBriefAppendsWrappedPortLines()
    {
        var t = ShowTableParser.Parse(
            "VLAN Name                             Status    Ports\n" +
            "---- -------------------------------- --------- -------------------------------\n" +
            "1    default                          active    Gi1/0/1, Gi1/0/2, Gi1/0/3\n" +
            "                                                Gi1/0/4\n" +
            "10   USERS                            active    Gi1/0/5\n")!;

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Gi1/0/1, Gi1/0/2, Gi1/0/3, Gi1/0/4", t.Cell(t.Rows[0], "Ports"));
        Assert.Equal("USERS", t.Cell(t.Rows[1], "Name"));
    }

    [Fact]
    public void StandbyBriefHandlesBlankPreemptColumn()
    {
        var t = ShowTableParser.Parse(
            "                     P indicates configured to preempt.\n" +
            "Interface   Grp  Pri P State   Active          Standby         Virtual IP\n" +
            "Vl10        10   110 P Active  local           10.1.10.3       10.1.10.1\n" +
            "Vl20        20   100   Standby 10.1.20.2       local           10.1.20.1\n")!;

        Assert.Equal("P", t.Cell(t.Rows[0], "P"));
        Assert.Equal("", t.Cell(t.Rows[1], "P"));
        Assert.Equal("Standby", t.Cell(t.Rows[1], "State"));
        Assert.Equal("10.1.20.1", t.Cell(t.Rows[1], "Virtual IP"));
    }

    [Fact]
    public void JunosTerseAndBgpSummary()
    {
        var terse = ShowTableParser.Parse(
            "Interface               Admin Link Proto    Local                 Remote\n" +
            "ge-0/0/0                up    up\n" +
            "ge-0/0/0.0              up    up   inet     10.0.0.1/30\n" +
            "ge-0/0/1                up    down\n")!;
        Assert.Equal(3, terse.Rows.Count);
        Assert.Equal("down", terse.Cell(terse.Rows[2], "Link"));
        Assert.Equal("10.0.0.1/30", terse.Cell(terse.Rows[1], "Local"));

        var bgp = ShowTableParser.Parse(
            "Peer                     AS      InPkt     OutPkt    OutQ   Flaps Last Up/Dwn State|#Active/Received/Accepted/Damped...\n" +
            "10.0.0.2              65002      12345      12340       0       0 1w2d 3:04:05 10/12/12/0           0/0/0/0\n" +
            "10.0.0.6              65003          0          0       0       2       5:00 Active\n")!;
        Assert.Equal("BGP summary", bgp.Title);
        Assert.Equal("12", bgp.Cell(bgp.Rows[0], "State/PfxRcd"));
        Assert.Equal("1w2d 3:04:05", bgp.Cell(bgp.Rows[0], "Up/Down"));
        Assert.Equal("Active", bgp.Cell(bgp.Rows[1], "State/PfxRcd"));
        Assert.Equal("5:00", bgp.Cell(bgp.Rows[1], "Up/Down"));
    }

    [Fact]
    public void FortiGateInterfacesSkipGroupHeaders()
    {
        var t = ShowTableParser.Parse(
            "== [onboard]\n" +
            "        ==[port1]\n" +
            "                mode: static\n" +
            "                ip: 192.168.1.99 255.255.255.0\n" +
            "                status: up\n" +
            "                speed: 1000Mbps (Duplex: full)\n" +
            "        ==[port2]\n" +
            "                mode: static\n" +
            "                ip: 0.0.0.0 0.0.0.0\n" +
            "                status: down\n")!;

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("port1", t.Rows[0][0]);
        Assert.Equal("down", t.Cell(t.Rows[1], "Status"));
    }

    [Fact]
    public void GenericFallbackReadsAnyAlignedTable()
    {
        var t = ShowTableParser.Parse(
            "Name        Owner      Size\n" +
            "alpha       ops        10\n" +
            "beta        net        200\n")!;

        Assert.Equal("Table (auto-detected)", t.Title);
        Assert.Equal(new[] { "Name", "Owner", "Size" }, t.Columns);
        Assert.Equal("200", t.Cell(t.Rows[1], "Size"));
    }

    [Fact]
    public void PlainProseIsNotATable() => Assert.Null(ShowTableParser.Parse("Cisco IOS Software, Version 17.9.4\nuptime is 3 weeks"));

    [Fact]
    public void LastCommandOutputTakesTheMostRecentCommandBlock()
    {
        var (cmd, output) = ShowTableParser.LastCommandOutput(
            "SW1#show clock\n*10:00:01.123 UTC Mon Oct 6 2026\n" +
            "SW1#show ip interface brief\nInterface  IP-Address  OK? Method Status  Protocol\nVlan10  10.1.10.1  YES NVRAM  up  up\n" +
            "SW1#");

        Assert.Equal("show ip interface brief", cmd);
        Assert.StartsWith("Interface", output);
        Assert.DoesNotContain("SW1#", output);
    }

    [Fact]
    public void CsvAndMarkdownExportEscapeSpecialCharacters()
    {
        var t = new ShowTable("x", ["A", "B"], [["a,1", "pipe|here"]]);
        Assert.Contains("\"a,1\"", t.ToCsv());
        Assert.Contains("pipe\\|here", t.ToMarkdown());
    }
}

public class RouteTableParserTests
{
    [Fact]
    public void ParsesIosRoutesWithEcmpConnectedAndClassfulSubnetting()
    {
        var routes = RouteTableParser.Parse(
            "Codes: L - local, C - connected, S - static, R - RIP, M - mobile, B - BGP\n" +
            "       O - OSPF, IA - OSPF inter area\n\n" +
            "Gateway of last resort is 10.0.0.1 to network 0.0.0.0\n\n" +
            "S*    0.0.0.0/0 [1/0] via 10.0.0.1\n" +
            "      10.0.0.0/8 is variably subnetted, 4 subnets, 3 masks\n" +
            "C        10.0.0.0/30 is directly connected, GigabitEthernet0/0\n" +
            "L        10.0.0.2/32 is directly connected, GigabitEthernet0/0\n" +
            "O IA     10.2.0.0/16 [110/20] via 10.0.0.1, 00:01:02, GigabitEthernet0/0\n" +
            "                     [110/20] via 10.0.0.5, 00:01:02, GigabitEthernet0/1\n" +
            "      172.16.0.0/24 is subnetted, 1 subnets\n" +
            "B        172.16.5.0 [20/0] via 10.0.0.9, 1w2d\n");

        Assert.Equal(6, routes.Count);
        Assert.Equal(new RouteEntry("S*", "0.0.0.0/0", "1/0", "10.0.0.1", "", ""), routes[0]);
        Assert.Equal("connected", routes[1].NextHop);
        Assert.Equal("GigabitEthernet0/0", routes[1].Interface);
        Assert.Equal("O IA", routes[3].Code);
        Assert.Equal("00:01:02", routes[3].Age);
        Assert.Equal("10.0.0.5", routes[4].NextHop);
        Assert.Equal("10.2.0.0/16", routes[4].Prefix);
        Assert.Equal("172.16.5.0/24", routes[5].Prefix);
        Assert.Equal("1w2d", routes[5].Age);
    }

    [Fact]
    public void ParsesExternalCandidateDefaultWithoutSpaceInCode()
    {
        var r = Assert.Single(RouteTableParser.Parse("O*E2  0.0.0.0/0 [110/1] via 10.255.102.0, 3w2d, TenGigabitEthernet1/1/1\n"));
        Assert.Equal("O*E2", r.Code);
        Assert.Equal("0.0.0.0/0", r.Prefix);
        Assert.Equal("10.255.102.0", r.NextHop);
    }

    [Fact]
    public void ParsesAsaDottedMasksAndFortiOsWeights()
    {
        var asa = RouteTableParser.Parse(
            "S*       0.0.0.0 0.0.0.0 [1/0] via 203.0.113.1, outside\n" +
            "C        10.0.0.0 255.255.255.0 is directly connected, inside\n");
        Assert.Equal("0.0.0.0/0", asa[0].Prefix);
        Assert.Equal("outside", asa[0].Interface);
        Assert.Equal("10.0.0.0/24", asa[1].Prefix);

        var forti = RouteTableParser.Parse("S*      0.0.0.0/0 [10/0] via 192.168.1.1, port1, [1/0]\n");
        Assert.Equal("port1", forti[0].Interface);
        Assert.Equal("192.168.1.1", forti[0].NextHop);
    }

    [Fact]
    public void ParsesNxosRoutes()
    {
        var routes = RouteTableParser.Parse(
            "IP Route Table for VRF \"default\"\n" +
            "10.0.0.0/24, ubest/mbest: 1/0, attached\n" +
            "    *via 10.0.0.1, Vlan10, [0/0], 3w2d, direct\n" +
            "10.2.0.0/16, ubest/mbest: 2/0\n" +
            "    *via 10.0.0.2, Eth1/1, [110/41], 1d02h, ospf-1, intra\n" +
            "    *via 10.0.0.6, Eth1/2, [110/41], 1d02h, ospf-1, intra\n");

        Assert.Equal(3, routes.Count);
        Assert.Equal("direct", routes[0].Code);
        Assert.Equal("Vlan10", routes[0].Interface);
        Assert.Equal("ospf-1 intra", routes[1].Code);
        Assert.Equal("110/41", routes[2].AdMetric);
        Assert.Equal("10.0.0.6", routes[2].NextHop);
    }

    [Fact]
    public void ParsesLinuxRoutesButNotNeighborEntries()
    {
        var routes = RouteTableParser.Parse(
            "default via 192.168.1.1 dev eth0 proto dhcp metric 100\n" +
            "10.0.0.0/24 dev eth0 proto kernel scope link src 10.0.0.5\n");
        Assert.Equal("0.0.0.0/0", routes[0].Prefix);
        Assert.Equal("192.168.1.1", routes[0].NextHop);
        Assert.Equal("connected", routes[1].NextHop);

        Assert.Empty(RouteTableParser.Parse("10.0.0.1 dev eth0 lladdr 00:50:56:a1:00:01 REACHABLE\n"));
    }
}
