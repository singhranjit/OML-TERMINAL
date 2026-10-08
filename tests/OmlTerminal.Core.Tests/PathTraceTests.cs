using System.Net;
using System.Net.NetworkInformation;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.PathTrace;

namespace OmlTerminal.Core.Tests;

/// <summary>A pretend routed path for the continuous trace: hop i answers from Routers[i] with a latency and a loss rate.</summary>
internal sealed class FakeNetwork : IProbeSender
{
    public List<(string Ip, double Ms, double Loss)> Routers { get; } = [];
    public bool DestinationAnswers { get; set; } = true;
    /// <summary>Hop (1-based) that replies "unreachable" instead of forwarding.</summary>
    public (int Hop, IPStatus Status)? Refuse { get; set; }
    private int _n;

    public Task<ProbeResult> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct)
    {
        int seq = Interlocked.Increment(ref _n);
        bool Lost(double loss) => loss > 0 && (seq * 7919 % 1000) / 1000.0 < loss;
        if (Refuse is { } r && ttl >= r.Hop)
            return Task.FromResult(new ProbeResult(IPAddress.Parse(Routers[r.Hop - 1].Ip), r.Status, 2));
        if (ttl <= Routers.Count)
        {
            var (ip, ms, loss) = Routers[ttl - 1];
            if (Lost(loss) || ip == "*") return Task.FromResult(new ProbeResult(null, IPStatus.TimedOut, 0));
            return Task.FromResult(new ProbeResult(IPAddress.Parse(ip), IPStatus.TtlExpired, ms));
        }
        if (!DestinationAnswers) return Task.FromResult(new ProbeResult(null, IPStatus.TimedOut, 0));
        // Loss at a hop that really drops traffic also hits every probe that has to pass through it.
        double carried = Routers.Max(r => r.Loss >= 0.3 ? 0 : r.Loss);
        if (Lost(carried)) return Task.FromResult(new ProbeResult(null, IPStatus.TimedOut, 0));
        return Task.FromResult(new ProbeResult(target, IPStatus.Success, Routers.Count > 0 ? Routers[^1].Ms + 1 : 1));
    }
}

public class ContinuousTraceTests
{
    private static async Task<ContinuousTrace> Run(FakeNetwork net, int rounds = 40, string target = "8.8.8.8")
    {
        var t = new ContinuousTrace(IPAddress.Parse(target), new TraceOptions { MaxHops = 12, Interval = TimeSpan.Zero, ResolveNames = false }, net);
        await t.RunAsync(CancellationToken.None, rounds);
        return t;
    }

    [Fact]
    public async Task Builds_per_hop_statistics_and_stops_at_the_destination()
    {
        var net = new FakeNetwork();
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0), ("203.0.113.9", 12, 0)]);
        var t = await Run(net);
        var s = t.Snapshot();
        Assert.True(s.Reached);
        Assert.Equal(4, s.Hops.Count);
        Assert.Equal("8.8.8.8", s.Hops[^1].Address);
        Assert.True(s.Hops[^1].IsDestination);
        Assert.All(s.Hops, h => Assert.Equal(0, h.LossPercent));
        Assert.Equal(12, s.Hops[2].Mean, 3);
        Assert.Equal(40, s.Hops[0].Sent);
        Assert.Contains(PathDiagnosis.Analyze(s), f => f.Title.StartsWith("The path looks healthy"));
    }

    [Fact]
    public async Task Loss_at_a_middle_hop_that_does_not_carry_on_is_called_rate_limiting()
    {
        var net = new FakeNetwork();
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0.5), ("203.0.113.9", 12, 0)]);
        var s = (await Run(net)).Snapshot();
        Assert.True(s.Hops[1].LossPercent > 30);
        var f = PathDiagnosis.Analyze(s);
        Assert.Contains(f, x => x.Hop == 2 && x.Title.Contains("not a real problem"));
        Assert.DoesNotContain(f, x => x.Severity == InsightSeverity.Problem);
    }

    [Fact]
    public async Task Real_loss_is_traced_to_the_hop_where_it_starts()
    {
        var net = new FakeNetwork();
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0), ("203.0.113.9", 12, 0.2), ("203.0.113.20", 14, 0.2)]);
        var s = (await Run(net, 60)).Snapshot();
        var f = PathDiagnosis.Analyze(s);
        var loss = Assert.Single(f, x => x.Severity == InsightSeverity.Problem && x.Title.Contains("loss"));
        Assert.Equal(3, loss.Hop);
        Assert.Contains("hop 2", loss.Detail);
    }

    [Fact]
    public async Task Unreachable_destination_names_the_last_router_that_answered()
    {
        var net = new FakeNetwork { DestinationAnswers = false };
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0)]);
        var s = (await Run(net, 8)).Snapshot();
        Assert.False(s.Reached);
        Assert.Equal(3, s.Hops.Count); // two routers + one silent hop past the last answer
        Assert.True(s.Hops[^1].Silent);
        var p = Assert.Single(PathDiagnosis.Analyze(s), x => x.Severity == InsightSeverity.Problem);
        Assert.Contains("10.255.1.1", p.Detail);
    }

    [Fact]
    public async Task Admin_prohibited_reply_is_reported_as_a_firewall_block()
    {
        var net = new FakeNetwork { Refuse = (2, IPStatus.DestinationProhibited) };
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0)]);
        var s = (await Run(net, 8)).Snapshot();
        var p = Assert.Single(PathDiagnosis.Analyze(s), x => x.Severity == InsightSeverity.Problem);
        Assert.Contains("prohibited", p.Detail);
        Assert.Equal(2, p.Hop);
    }

    [Fact]
    public async Task A_new_router_at_a_hop_is_recorded_as_a_route_change()
    {
        var net = new FakeNetwork();
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0)]);
        var t = new ContinuousTrace(IPAddress.Parse("8.8.8.8"), new TraceOptions { Interval = TimeSpan.Zero, ResolveNames = false }, net);
        await t.RunAsync(CancellationToken.None, 10);
        net.Routers[1] = ("10.255.2.1", 4, 0);
        await t.RunAsync(CancellationToken.None, 20);
        var c = Assert.Single(t.Changes);
        Assert.Equal(2, c.Hop);
        Assert.Equal("10.255.1.1", c.From);
        Assert.Equal("10.255.2.1", c.To);
        Assert.Contains(PathDiagnosis.Analyze(t.Snapshot()), f => f.Title.Contains("route changed"));
    }

    [Fact]
    public async Task Persistent_latency_jump_is_located()
    {
        var net = new FakeNetwork();
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("10.255.1.1", 3, 0), ("203.0.113.9", 95, 0), ("203.0.113.20", 97, 0)]);
        var f = PathDiagnosis.Analyze((await Run(net)).Snapshot());
        Assert.Contains(f, x => x.Title == "Latency jumps 92 ms between hop 2 and 3");
    }

    [Fact]
    public async Task Silent_middle_hop_is_explained()
    {
        var net = new FakeNetwork();
        net.Routers.AddRange([("10.0.0.1", 1, 0), ("*", 0, 0), ("203.0.113.9", 12, 0)]);
        var f = PathDiagnosis.Analyze((await Run(net)).Snapshot());
        Assert.Contains(f, x => x.Title == "Hop 2 never answers");
    }

    [Theory]
    [InlineData(20, 1, 0, 4.3, 4.5)]
    [InlineData(150, 10, 0, 4.0, 4.4)]
    [InlineData(300, 40, 5, 1.0, 3.5)]
    public void Mos_follows_the_e_model(double latency, double jitter, double loss, double min, double max)
    {
        var m = PathDiagnosis.Mos(latency, jitter, loss);
        Assert.InRange(m, min, max);
    }
}

public class HopIdentifierTests
{
    [Fact]
    public void Parses_team_cymru_answers()
    {
        Assert.Equal((15169, "8.8.8.0/24"), HopIdentifier.ParseOrigin("\"15169 | 8.8.8.0/24 | US | arin | 2023-12-28\""));
        Assert.Equal(13335, HopIdentifier.ParseOrigin("\"13335 209242 | 1.1.1.0/24 | AU | apnic | 2011-08-11\"")!.Value.Asn);
        Assert.Equal("GOOGLE - Google LLC", HopIdentifier.ParseAsName("\"15169 | US | arin | 2000-03-30 | GOOGLE - Google LLC, US\""));
        Assert.Null(HopIdentifier.ParseOrigin(""));
    }

    [Fact]
    public void Classifies_private_ranges()
    {
        Assert.Equal(HopOwnerKind.Private, HopIdentifier.Classify(IPAddress.Parse("172.20.1.1"))!.Kind);
        Assert.Equal(HopOwnerKind.CarrierNat, HopIdentifier.Classify(IPAddress.Parse("100.72.0.1"))!.Kind);
        Assert.Null(HopIdentifier.Classify(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Reads_interface_addresses_from_ios_junos_and_fortios_configs()
    {
        var ios = "hostname R1\ninterface TenGigabitEthernet1/0/1\n description core\n ip address 10.255.101.0 255.255.255.254\n!\ninterface Vlan20\n ip address 10.10.20.2 255.255.255.0\n ip address 10.20.20.2 255.255.255.0 secondary\n!\nrouter ospf 1\n";
        var list = HopIdentifier.InterfaceAddresses(ios).ToList();
        Assert.Contains(("10.255.101.0", "TenGigabitEthernet1/0/1"), list);
        Assert.Contains(("10.10.20.2", "Vlan20"), list);
        Assert.Contains(("10.20.20.2", "Vlan20"), list);
        Assert.Contains(("10.1.1.1", "ge-0/0/1.0"), HopIdentifier.InterfaceAddresses("set interfaces ge-0/0/1 unit 0 family inet address 10.1.1.1/30"));
        var forti = "config system interface\n    edit \"port1\"\n        set ip 192.168.1.99 255.255.255.0\n    next\nend\n";
        Assert.Contains(("192.168.1.99", "port1"), HopIdentifier.InterfaceAddresses(forti));
    }

    [Fact]
    public async Task Identifies_own_devices_from_backups()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "CORE-SW1"));
        File.WriteAllText(Path.Combine(root, "CORE-SW1", "CORE-SW1_2026-10-08_010000.cfg"), "interface TenGigabitEthernet1/0/3\n ip address 10.255.102.0 255.255.255.254\n");
        var id = new HopIdentifier(() => [new SessionProfile { Name = "EDGE-R1", Host = "10.10.0.254" }], root, lookupAsn: false);
        var o = await id.IdentifyAsync("10.255.102.0");
        Assert.Equal(HopOwnerKind.Device, o!.Kind);
        Assert.Equal("CORE-SW1", o.Device);
        Assert.Equal("TenGigabitEthernet1/0/3", o.Interface);
        Assert.Equal("EDGE-R1", (await id.IdentifyAsync("10.10.0.254"))!.Device);
        Assert.Equal(HopOwnerKind.Private, (await id.IdentifyAsync("10.9.9.9"))!.Kind);
    }
}

public class DeviceOutputParserTests
{
    private const string IosEcmp = """
        Routing entry for 10.10.20.0/24
          Known via "ospf 1", distance 110, metric 2, type intra area
          Last update from 10.255.102.1 on TenGigabitEthernet1/0/3, 3w2d ago
          Routing Descriptor Blocks:
          * 10.255.102.1, from 10.255.0.11, 3w2d ago, via TenGigabitEthernet1/0/3
              Route metric is 2, traffic share count is 1
            10.255.104.1, from 10.255.0.12, 3w2d ago, via TenGigabitEthernet1/0/4
              Route metric is 2, traffic share count is 1
        """;

    [Fact]
    public void Ios_route_entry_with_equal_cost_paths()
    {
        var r = DeviceOutputParsers.ParseIosRouteEntry(IosEcmp)!;
        Assert.Equal("10.10.20.0/24", r.Prefix);
        Assert.Equal("ospf 1", r.Protocol);
        Assert.Equal("110/2", r.Distance);
        Assert.False(r.Connected);
        Assert.Equal([new NextHop("10.255.102.1", "TenGigabitEthernet1/0/3"), new NextHop("10.255.104.1", "TenGigabitEthernet1/0/4")], r.Paths);
    }

    [Fact]
    public void Ios_connected_route_and_not_in_table()
    {
        var c = DeviceOutputParsers.ParseIosRouteEntry("""
            Routing entry for 10.10.20.0/24
              Known via "connected", distance 0, metric 0 (connected, via interface)
              Routing Descriptor Blocks:
              * directly connected, via Vlan20
                  Route metric is 0, traffic share count is 1
            """)!;
        Assert.True(c.Connected);
        Assert.Equal("Vlan20", c.Paths[0].Interface);
        Assert.Null(DeviceOutputParsers.ParseIosRouteEntry("% Network not in table"));
        Assert.True(DeviceOutputParsers.NotInTable("% Subnet not in table"));
    }

    [Fact]
    public void Longest_match_over_a_full_table_and_nxos()
    {
        var table = """
            Gateway of last resort is 10.255.101.1 to network 0.0.0.0

            O*E2  0.0.0.0/0 [110/1] via 10.255.101.1, 3w2d, TenGigabitEthernet1/1/1
                  10.0.0.0/8 is variably subnetted, 6 subnets, 3 masks
            O        10.10.0.0/16 [110/3] via 10.255.101.1, 3w2d, TenGigabitEthernet1/1/1
            O        10.10.50.0/24 [110/2] via 10.255.101.1, 3w2d, TenGigabitEthernet1/1/1
                                   [110/2] via 10.255.103.1, 3w2d, TenGigabitEthernet1/1/2
            C        10.10.20.0/24 is directly connected, Vlan20
            """;
        var r = DeviceOutputParsers.LongestMatch(table, IPAddress.Parse("10.10.50.40"))!;
        Assert.Equal("10.10.50.0/24", r.Prefix);
        Assert.Equal(2, r.Paths.Count);
        Assert.True(DeviceOutputParsers.LongestMatch(table, IPAddress.Parse("10.10.20.9"))!.Connected);
        Assert.Equal("0.0.0.0/0", DeviceOutputParsers.LongestMatch(table, IPAddress.Parse("8.8.8.8"))!.Prefix);

        var nx = """
            IP Route Table for VRF "default"
            10.10.50.0/24, ubest/mbest: 1/0
                *via 10.255.101.1, Eth1/49, [110/41], 3w2d, ospf-1, intra
            """;
        var n = DeviceOutputParsers.LongestMatch(nx, IPAddress.Parse("10.10.50.1"))!;
        Assert.Equal(new NextHop("10.255.101.1", "Eth1/49"), n.Paths.Single());
    }

    [Fact]
    public void Interface_health()
    {
        var h = DeviceOutputParsers.ParseInterface("""
            GigabitEthernet1/0/2 is up, line protocol is up (connected)
              Hardware is Gigabit Ethernet, address is 0050.56a1.0102 (bia 0050.56a1.0102)
              Description: Link to ACC-SW2 Gi1/1/1
              MTU 1500 bytes, BW 1000000 Kbit/sec, DLY 10 usec,
              Input queue: 0/375/12/0 (size/max/drops/flushes); Total output drops: 4410
              5 minute input rate 912000000 bits/sec, 81234 packets/sec
              5 minute output rate 120000000 bits/sec, 12034 packets/sec
                 900 packets input, 12000 bytes, 0 no buffer
                 1291 input errors, 1284 CRC, 0 frame, 0 overrun, 0 ignored
                 0 output errors, 0 collisions, 2 interface resets
            """)!;
        Assert.True(h.Up);
        Assert.Equal(1_000_000, h.BandwidthKbps);
        Assert.Equal(91.2, h.InUtil, 1);
        Assert.Equal(1291, h.InputErrors);
        Assert.Equal(1284, h.Crc);
        Assert.Equal(4410, h.OutputDrops);
        Assert.Equal(12, h.InputDrops);
        Assert.Equal(2, h.Resets);
        Assert.Equal("Link to ACC-SW2 Gi1/1/1", h.Description);
    }

    [Fact]
    public void Arp_and_mac_lookups()
    {
        var arp = DeviceOutputParsers.ParseArp("Protocol  Address          Age (min)  Hardware Addr   Type   Interface\nInternet  10.10.20.57            12   0050.56A1.2057  ARPA   Vlan20\n", "10.10.20.57")!;
        Assert.Equal(("0050.56a1.2057", "Vlan20"), (arp.Mac, arp.Interface));
        var mac = DeviceOutputParsers.ParseMac("""
                      Mac Address Table
            -------------------------------------------
            Vlan    Mac Address       Type        Ports
            ----    -----------       --------    -----
              20    0050.56a1.2057    DYNAMIC     Gi1/0/14
            Total Mac Addresses for this criterion: 1
            """, "0050.56A1.2057")!;
        Assert.Equal(("20", "Gi1/0/14"), (mac.Vlan, mac.Port));
        Assert.True(DeviceOutputParsers.IsSvi("Vlan20"));
    }
}

/// <summary>A fake multi-device CLI network for the hop-by-hop tracer.</summary>
internal sealed class FakeCli(string host, Dictionary<string, string> outputs) : IDeviceCli
{
    public string Hostname => host;
    public bool IsJunos => false;
    public bool IsNxos => false;
    public Task<string> RunAsync(string command, CancellationToken ct) =>
        Task.FromResult(outputs.TryGetValue(command, out var o) ? o : "                ^\n% Invalid input detected at '^' marker.\n");
    public void Dispose() { }
}

public class DevicePathTracerTests
{
    private static string Cdp(string name, string ip, string local, string remote, string caps = "Router Switch IGMP") => $"""
        -------------------------
        Device ID: {name}
        Entry address(es):
          IP address: {ip}
        Platform: cisco C9300-48P,  Capabilities: {caps}
        Interface: {local},  Port ID (outgoing port): {remote}
        Holdtime : 150 sec
        """;

    private static string Route(string prefix, string proto, params (string Hop, string If)[] paths) =>
        $"Routing entry for {prefix}\n  Known via \"{proto}\", distance 110, metric 2\n  Routing Descriptor Blocks:\n" +
        string.Join("\n", paths.Select((p, i) => $"  {(i == 0 ? "*" : " ")} {(p.Hop.Length == 0 ? "directly connected" : p.Hop + ", from 1.1.1.1, 3w2d ago")}, via {p.If}\n      Route metric is 2, traffic share count is 1"));

    private static string Iface(string name, long inBps = 1000) =>
        $"{name} is up, line protocol is up\n  MTU 1500 bytes, BW 10000000 Kbit/sec, DLY 10 usec,\n  5 minute input rate {inBps} bits/sec, 1 packets/sec\n  5 minute output rate 0 bits/sec, 0 packets/sec\n     0 input errors, 0 CRC, 0 frame\n     0 output errors, 0 collisions, 0 interface resets\n";

    /// <summary>CORE1 → (ECMP) DIST1 / DIST2 → both: Vlan20 → ACC1 Gi1/0/14 → host 10.10.20.57.</summary>
    private static Dictionary<string, FakeCli> Network()
    {
        const string dst = "10.10.20.57";
        var core = new Dictionary<string, string>
        {
            [$"show ip route {dst}"] = Route("10.10.20.0/24", "ospf 1", ("10.255.102.1", "TenGigabitEthernet1/0/3"), ("10.255.104.1", "TenGigabitEthernet1/0/4")),
            ["show cdp neighbors TenGigabitEthernet1/0/3 detail"] = Cdp("DIST1.corp", "10.10.0.11", "TenGigabitEthernet1/0/3", "TenGigabitEthernet1/1/1"),
            ["show cdp neighbors TenGigabitEthernet1/0/4 detail"] = Cdp("DIST2.corp", "10.10.0.12", "TenGigabitEthernet1/0/4", "TenGigabitEthernet1/1/1"),
            ["show interfaces TenGigabitEthernet1/0/3"] = Iface("TenGigabitEthernet1/0/3"),
            ["show interfaces TenGigabitEthernet1/0/4"] = Iface("TenGigabitEthernet1/0/4", 9_600_000_000),
        };
        Dictionary<string, string> Dist() => new()
        {
            [$"show ip route {dst}"] = Route("10.10.20.0/24", "connected", ("", "Vlan20")),
            [$"show ip arp {dst}"] = $"Protocol  Address          Age (min)  Hardware Addr   Type   Interface\nInternet  {dst}            12   0050.56a1.2057  ARPA   Vlan20\n",
            ["show mac address-table address 0050.56a1.2057"] = "Vlan    Mac Address       Type        Ports\n----    -----------       --------    -----\n  20    0050.56a1.2057    DYNAMIC     Gi1/0/1\n",
            ["show cdp neighbors Gi1/0/1 detail"] = Cdp("ACC1", "10.10.0.21", "GigabitEthernet1/0/1", "GigabitEthernet1/1/1", "Switch IGMP"),
            ["show interfaces TenGigabitEthernet1/1/1"] = Iface("TenGigabitEthernet1/1/1"),
        };
        var acc = new Dictionary<string, string>
        {
            ["show mac address-table address 0050.56a1.2057"] = "Vlan    Mac Address       Type        Ports\n  20    0050.56a1.2057    DYNAMIC     Gi1/0/14\n",
            ["show cdp neighbors Gi1/0/14 detail"] = "",
        };
        return new()
        {
            ["10.10.0.1"] = new FakeCli("CORE1", core), ["10.10.0.11"] = new FakeCli("DIST1", Dist()),
            ["10.10.0.12"] = new FakeCli("DIST2", Dist()), ["10.10.0.21"] = new FakeCli("ACC1", acc),
        };
    }

    [Fact]
    public async Task Follows_ecmp_through_routers_and_mac_tables_to_the_access_port()
    {
        var net = Network();
        var tracer = new DevicePathTracer((name, ip) => new SessionProfile { Name = name, Host = ip },
            connect: (p, ct) => net.TryGetValue(p.Host, out var c) ? Task.FromResult<IDeviceCli>(c) : throw new IOException($"no route to {p.Host}"));
        await tracer.TraceAsync(new SessionProfile { Name = "core", Host = "10.10.0.1" }, IPAddress.Parse("10.10.20.57"), new DevicePathOptions(), CancellationToken.None);
        var (nodes, links) = tracer.Snapshot();

        Assert.Equal(["acc1", "core1", "dist1", "dist2", "host:10.10.20.57"], nodes.Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains(links, l => l is { From: "core1", Egress: "TenGigabitEthernet1/0/3", To: "dist1", Ingress: "TenGigabitEthernet1/1/1" });
        Assert.Contains(links, l => l is { From: "core1", To: "dist2" });
        Assert.Contains(links, l => l is { From: "dist1", Egress: "Gi1/0/1", To: "acc1", Layer2: true });
        var host = nodes.Single(n => n.Kind == PathNodeKind.Host);
        Assert.Equal(PathNodeStatus.Destination, host.Status);
        Assert.Equal("ACC1 Gi1/0/14 (VLAN 20)", host.Attachment);

        var findings = DevicePathTracer.Findings(nodes, links, IPAddress.Parse("10.10.20.57"));
        Assert.Contains(findings, f => f.Title == "CORE1 TenGigabitEthernet1/0/4 is 96% busy" && f.Severity == InsightSeverity.Problem);
        Assert.Contains(findings, f => f.Title == "CORE1 load-balances over 2 paths");
        Assert.Contains(findings, f => f.Title.StartsWith("10.10.20.57 found: ACC1 Gi1/0/14"));
    }

    [Fact]
    public async Task No_route_and_failed_logins_are_findings()
    {
        var net = new Dictionary<string, FakeCli>
        {
            ["10.0.0.1"] = new FakeCli("R1", new() { ["show ip route 192.0.2.1"] = "% Network not in table", ["show ip route 0.0.0.0"] = "% Network not in table" }),
        };
        var tracer = new DevicePathTracer((n, ip) => new SessionProfile { Name = n, Host = ip },
            connect: (p, ct) => net.TryGetValue(p.Host, out var c) ? Task.FromResult<IDeviceCli>(c) : throw new IOException("timeout"));
        await tracer.TraceAsync(new SessionProfile { Name = "r1", Host = "10.0.0.1" }, IPAddress.Parse("192.0.2.1"), new DevicePathOptions(), CancellationToken.None);
        var (nodes, links) = tracer.Snapshot();
        Assert.Equal(PathNodeStatus.NoRoute, nodes.Single().Status);
        Assert.Contains(DevicePathTracer.Findings(nodes, links, IPAddress.Parse("192.0.2.1")), f => f.Title == "R1 has no route to 192.0.2.1");
    }
}
