using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Monitoring;
using OmlTerminal.Core.PathTrace;
using OmlTerminal.Core.Snmp;

namespace OmlTerminal.Core.Tests;

public class MulticastTests
{
    private const string Rpf = """
        RPF information for ? (10.10.50.40)
          RPF interface: TenGigabitEthernet1/1/1
          RPF neighbor: ? (10.255.102.0)
          RPF route/mask: 10.10.50.0/24
          RPF type: unicast (ospf 1)
          Doing distance-preferred lookups across tables
        """;

    private const string Mroute = """
        IP Multicast Routing Table
        Flags: D - Dense, S - Sparse, B - Bidir Group, s - SSM Group, C - Connected,
        Outgoing interface flags: H - Hardware switched, A - Assert winner, p - PIM Join
         Timers: Uptime/Expires
         Interface state: Interface, Next-Hop or VCD, State/Mode

        (*, 239.1.1.10), 00:12:31/stopped, RP 10.255.0.1, flags: SJC
          Incoming interface: TenGigabitEthernet1/1/1, RPF nbr 10.255.102.0
          Outgoing interface list:
            Vlan20, Forward/Sparse, 00:12:31/00:02:21

        (10.10.50.40, 239.1.1.10), 00:12:31/00:02:56, flags: JT
          Incoming interface: TenGigabitEthernet1/1/1, RPF nbr 10.255.102.0
          Outgoing interface list:
            Vlan20, Forward/Sparse, 00:12:31/00:02:21
        """;

    [Fact]
    public void Parses_rpf_mroute_count_and_igmp()
    {
        var r = MulticastParsers.ParseRpf(Rpf)!;
        Assert.Equal(("TenGigabitEthernet1/1/1", "10.255.102.0", "10.10.50.0/24", false), (r.Interface, r.Neighbor, r.Route, r.DirectlyConnected));
        var direct = MulticastParsers.ParseRpf("RPF information for ? (10.10.50.40)\n  RPF interface: Vlan50\n  RPF neighbor: ? (0.0.0.0) - directly connected\n")!;
        Assert.True(direct.DirectlyConnected);
        Assert.Null(MulticastParsers.ParseRpf("RPF information for ? (192.0.2.9) failed, no route exists"));

        var m = MulticastParsers.ParseMroute(Mroute);
        Assert.Equal(2, m.Count);
        var sg = m.Single(e => e.Source == "10.10.50.40");
        Assert.Equal(("TenGigabitEthernet1/1/1", "10.255.102.0", "JT"), (sg.IncomingInterface, sg.RpfNeighbor, sg.Flags));
        Assert.Equal(["Vlan20"], sg.Outgoing);

        var c = MulticastParsers.ParseCount("Group: 239.1.1.10, Source count: 1, Packets forwarded: 9988, Packets received: 10011\n  Source: 10.10.50.40/32, Forwarding: 9988/250/1316/2632, Other: 10011/23/0\n")!;
        Assert.Equal((9988L, 250L, 2632L, 23L), (c.Forwarded, c.Pps, c.Kbps, c.RpfFailed));
        Assert.Equal(["Vlan20"], MulticastParsers.ParseIgmp("IGMP Connected Group Membership\nGroup Address    Interface                Uptime    Expires   Last Reporter   Group Accounted\n239.1.1.10       Vlan20                   00:12:44  00:02:13  10.10.20.57\n", "239.1.1.10"));
    }

    [Fact]
    public void Parses_router_mtrace_output()
    {
        var hops = MulticastParsers.ParseMtrace("""
            Type escape sequence to abort.
            Mtrace from 10.10.50.40 to 10.10.20.2 via group 239.1.1.10
            From source (?) to destination (?)
            Querying full reverse path...
             0  10.10.20.2
            -1  10.10.20.2 (DIST-SW1) ==> 10.255.102.1 PIM  [10.10.50.0/24]
            -2  10.255.102.0 (CORE-SW1) ==> 10.10.50.2 PIM_MT  Reached RP/Core [10.10.50.0/24]
            -3  10.10.50.40
            """);
        Assert.Equal(4, hops.Count);
        Assert.Equal(("DIST-SW1", "10.255.102.1", "PIM", "10.10.50.0/24"), (hops[1].Name, hops[1].OutAddress, hops[1].Protocol, hops[1].Prefix));
        Assert.Contains("Reached RP", hops[2].Note);
        Assert.False(MulticastParsers.IsMtraceError(hops[2].Note));
        Assert.True(MulticastParsers.IsMtraceError("!RPF Interface"));
    }

    [Fact]
    public async Task Walks_the_tree_back_to_the_source_and_flags_a_missing_pim_neighbor()
    {
        var dist = new Dictionary<string, string>
        {
            ["show ip igmp groups 239.1.1.10"] = "239.1.1.10       Vlan20                   00:12:44  00:02:13  10.10.20.57\n",
            ["show ip rpf 10.10.50.40"] = Rpf,
            ["show ip mroute 239.1.1.10 10.10.50.40"] = Mroute,
            ["show ip mroute 239.1.1.10"] = Mroute,
            ["show ip mroute 239.1.1.10 10.10.50.40 count"] = "  Source: 10.10.50.40/32, Forwarding: 9988/250/1316/2632, Other: 10011/0/0\n",
            ["show ip pim neighbor"] = "Neighbor          Interface                Uptime/Expires    Ver   DR\nAddress                                                            Prio/Mode\n10.255.103.0      TenGigabitEthernet1/1/2  3w2d/00:01:22     v2    1 / S P G\n",
            ["show cdp neighbors TenGigabitEthernet1/1/1 detail"] = "Device ID: CORE1\nEntry address(es):\n  IP address: 10.10.0.1\nPlatform: cisco C9500,  Capabilities: Router Switch\nInterface: TenGigabitEthernet1/1/1,  Port ID (outgoing port): TenGigabitEthernet1/0/3\n",
        };
        var core = new Dictionary<string, string>
        {
            ["show ip rpf 10.10.50.40"] = "RPF information for ? (10.10.50.40)\n  RPF interface: Vlan50\n  RPF neighbor: ? (0.0.0.0) - directly connected\n",
            ["show ip mroute 239.1.1.10 10.10.50.40"] = "(10.10.50.40, 239.1.1.10), 00:12:31/00:02:56, flags: T\n  Incoming interface: Vlan50, RPF nbr 0.0.0.0\n  Outgoing interface list:\n    TenGigabitEthernet1/0/3, Forward/Sparse, 00:12:31/00:02:21\n",
            ["show ip mroute 239.1.1.10 10.10.50.40 count"] = "  Source: 10.10.50.40/32, Forwarding: 9988/250/1316/2632, Other: 9988/0/0\n",
        };
        var net = new Dictionary<string, FakeCli> { ["10.10.0.11"] = new("DIST1", dist), ["10.10.0.1"] = new("CORE1", core) };
        var t = new MulticastTracer((n, ip) => new SessionProfile { Name = n, Host = ip },
            connect: (p, ct) => Task.FromResult<IDeviceCli>(net[p.Host]));
        await t.TraceAsync(new SessionProfile { Name = "dist1", Host = "10.10.0.11" }, IPAddress.Parse("10.10.50.40"), IPAddress.Parse("239.1.1.10"), 10, CancellationToken.None);
        var hops = t.Hops;
        Assert.Equal(["DIST1", "CORE1", "10.10.50.40"], hops.Select(h => h.Name));
        Assert.Equal(["Vlan20"], hops[0].IgmpInterfaces);
        Assert.False(hops[0].RpfNeighborIsPim);
        var f = MulticastTracer.Findings(hops, IPAddress.Parse("10.10.50.40"), IPAddress.Parse("239.1.1.10"));
        Assert.Contains(f, x => x.Severity == InsightSeverity.Problem && x.Title == "DIST1: RPF neighbor 10.255.102.0 isn't a PIM neighbor");
        Assert.Contains(f, x => x.Title == "CORE1 forwards 250 pps (2632 kbps)");
    }
}

/// <summary>Answers pings by a script: true = reply in 5 ms, false = time out.</summary>
internal sealed class ScriptedPinger(params bool[] script) : IProbeSender
{
    private int _i;
    public Task<ProbeResult> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct)
    {
        bool ok = script[Math.Min(_i++, script.Length - 1)];
        return Task.FromResult(ok ? new ProbeResult(target, IPStatus.Success, 5) : new ProbeResult(null, IPStatus.TimedOut, 0));
    }
}

public class PingMonitorTests
{
    [Fact]
    public async Task Goes_down_after_consecutive_losses_and_records_the_outage()
    {
        var m = new PingMonitor(new PingMonitorOptions { DownAfter = 3 }, new ScriptedPinger(true, true, false, false, false, false, true, true));
        var h = m.Add(new PingTarget { Host = "10.0.0.1", Name = "gw" });
        var changes = new List<(PingState, PingState)>();
        m.StateChanged += (_, a, b) => changes.Add((a, b));
        for (int i = 0; i < 8; i++) await m.ProbeAsync(h);
        Assert.Equal([(PingState.Unknown, PingState.Up), (PingState.Up, PingState.Down), (PingState.Down, PingState.Up)], changes);
        var o = Assert.Single(m.Outages);
        Assert.NotNull(o.End);
        Assert.Equal("timed out", o.Reason);
        Assert.Equal(50, h.TotalLoss);
        Assert.Equal(1, h.Outages);
    }

    [Fact]
    public void Parses_pasted_host_lists()
    {
        var list = PingMonitor.ParseList("# core\n10.0.0.1, Core switch, HQ\nhost\n8.8.8.8 Google DNS\nfw.example.com\n");
        Assert.Equal(["10.0.0.1", "8.8.8.8", "fw.example.com"], list.Select(t => t.Host));
        Assert.Equal(("Core switch", "HQ"), (list[0].Name, list[0].Group));
        Assert.Equal("Google DNS", list[1].Name);
    }
}

public class SnmpTests
{
    [Fact]
    public void Ber_round_trips_oids_and_integers()
    {
        foreach (var oid in new[] { "1.3.6.1.2.1.1.3.0", "1.3.6.1.4.1.9.9.42.1.2.10.1.1.4294967295", "2.25.300" })
        {
            var enc = Ber.Oid(oid);
            Assert.Equal(oid, Ber.DecodeOid(enc.AsSpan(2)));
        }
        Assert.Equal([0x02, 0x02, 0x00, 0x80], Ber.Integer(128));
        Assert.Equal([0x02, 0x01, 0xFF], Ber.Integer(-1));
        Assert.Equal(-1, new SnmpValue(SnmpType.Integer, [0xFF]).ToInt64());
        Assert.Equal(4294967295UL, new SnmpValue(SnmpType.Counter32, [0x00, 0xFF, 0xFF, 0xFF, 0xFF]).ToUInt64());
        Assert.True(Ber.IsUnder("1.3.6.1.2.1.2.2.1.10.5", "1.3.6.1.2.1.2.2.1.10"));
        Assert.False(Ber.IsUnder("1.3.6.1.2.1.2.2.1.100", "1.3.6.1.2.1.2.2.1.10"));
        Assert.True(Ber.CompareOid("1.3.6.1.2.1.2.2.1.10.10", "1.3.6.1.2.1.2.2.1.10.9") > 0);
    }

    [Fact]
    public void Long_ber_lengths()
    {
        var big = Ber.Octets(new byte[300]);
        Assert.Equal([0x04, 0x82, 0x01, 0x2C], big[..4]);
        var r = new Ber.Reader(big);
        Assert.Equal(300, r.ReadOctets().Length);
    }

    [Fact]
    public void Key_localization_matches_rfc3414_appendix_a3()
    {
        var engine = Convert.FromHexString("000000000000000000000002");
        Assert.Equal("526F5EED9FCCE26F8964C2930787D82B", Convert.ToHexString(SnmpClient.LocalizedKey(SnmpAuth.Md5, "maplesyrup", engine)));
        Assert.Equal("6695FEBC9288E36282235FC7151F128497B38F3F", Convert.ToHexString(SnmpClient.LocalizedKey(SnmpAuth.Sha1, "maplesyrup", engine)));
        Assert.Equal(16, SnmpClient.PrivKey(SnmpAuth.Sha1, SnmpPriv.Aes128, "maplesyrup", engine).Length);
        Assert.Equal(32, SnmpClient.PrivKey(SnmpAuth.Sha1, SnmpPriv.Aes256, "maplesyrup", engine).Length);
        Assert.Throws<SnmpException>(() => SnmpClient.LocalizedKey(SnmpAuth.Sha1, "short", engine));
    }

    [Fact]
    public void Sha224_matches_fips_vectors()
    {
        var hash = typeof(SnmpClient).Assembly.GetType("OmlTerminal.Core.Snmp.Sha224Impl")!.GetMethod("Hash")!;
        string H(string s) => Convert.ToHexString((byte[])hash.Invoke(null, [Encoding.ASCII.GetBytes(s)])!).ToLowerInvariant();
        Assert.Equal("23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7", H("abc"));
        Assert.Equal("d14a028c2a3a2bc9476102bb288234c415a2b01f828ea62ac5b3e42f", H(""));
        Assert.Equal("75388b16512776cc5dba5da1fd890150b0c6455cb4f58b1952522525", H("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"));
    }

    [Fact]
    public void Rates_handle_wraps_and_reboots()
    {
        var t0 = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);
        var a = new IfCounters(t0, 100_000, 1_000, 2_000, false, 0, 0, 0, 0);
        var b = a with { At = t0.AddSeconds(60), SysUpTime = 106_000, InOctets = 751_000, OutOctets = 2_000 };
        var r = IfMib.Rate(a, b)!;
        Assert.Equal(100_000, r.InBps, 0); // 750,000 bytes * 8 / 60 s
        var wrapped = IfMib.Rate(a with { InOctets = uint.MaxValue - 99 }, b with { InOctets = 650 })!;
        Assert.Equal(100.0, wrapped.InBps, 0); // 750 bytes across the wrap, over 60 s
        Assert.Null(IfMib.Rate(a, b with { SysUpTime = 500 })); // agent rebooted
    }

    [Fact]
    public void Archive_rolls_up_and_reports_mrtg_stats()
    {
        var arc = new TrafficArchive();
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local);
        for (int m = 0; m <= 3 * 24 * 60; m += 5)
            arc.Add(new TrafficRate(start.AddMinutes(m), m % 60 == 0 ? 9e6 : 1e6, 2e6, 0, 0));
        Assert.All(arc.Raw, p => Assert.True(p.At >= start.AddDays(1)));            // raw keeps 2 days
        Assert.True(arc.HalfHour.Count >= 140);                                      // 3 days of 30-min buckets
        var day = arc.Points(TrafficPeriod.Daily, start.AddDays(3));
        var s = TrafficArchive.Stats(day);
        Assert.Equal(9e6, s.InMax);
        Assert.InRange(s.InAvg, 1.6e6, 1.7e6);
        Assert.Equal(9e6, s.In95);
        Assert.Equal(2e6, s.Out95);
        var week = arc.Points(TrafficPeriod.Weekly, start.AddDays(3));
        Assert.All(week.Where(p => p.At.Minute == 0), p => Assert.Equal(9e6, p.InMax)); // the on-the-hour peak survives averaging
        Assert.All(week.Where(p => p.At.Minute == 30), p => Assert.Equal(1e6, p.InMax));
        Assert.Equal("1.5 Gb/s", TrafficArchive.Bits(1.5e9));
    }

    [Fact]
    public async Task Talks_to_a_v2c_agent()
    {
        using var agent = new MiniAgent();
        using var c = new SnmpClient(agent.Endpoint, new SnmpCredentials { Community = "lab" });
        var v = await c.GetAsync([IfMib.SysName]);
        Assert.Equal("CORE-SW1", v.Single().Value.ToString());
        var walk = await c.WalkAsync(IfMib.IfDescr);
        Assert.Equal(["Gi1/0/1", "Gi1/0/2", "Te1/1/1"], walk.Select(x => x.Value.ToString()));
        var ifs = await IfMib.InterfacesAsync(c, CancellationToken.None);
        Assert.Equal(3, ifs.Count);
        Assert.Equal(10_000_000_000, ifs[2].SpeedBps);
        await Assert.ThrowsAsync<SnmpException>(async () =>
        {
            using var bad = new SnmpClient(agent.Endpoint, new SnmpCredentials { Community = "wrong" }, 300, 0);
            await bad.GetAsync([IfMib.SysName]);
        });
    }
}

/// <summary>An in-process SNMP v2c agent with a tiny MIB, to test the client end to end over real UDP.</summary>
internal sealed class MiniAgent : IDisposable
{
    private readonly System.Net.Sockets.UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly SortedDictionary<string, byte[]> _mib = new(Comparer<string>.Create(Ber.CompareOid));

    public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public MiniAgent()
    {
        _mib[IfMib.SysName] = Ber.Octets("CORE-SW1");
        string[] names = ["Gi1/0/1", "Gi1/0/2", "Te1/1/1"];
        for (int i = 1; i <= 3; i++)
        {
            _mib[$"{IfMib.IfDescr}.{i}"] = Ber.Octets(names[i - 1]);
            _mib[$"{IfMib.IfType}.{i}"] = Ber.Integer(6);
            _mib[$"{IfMib.IfSpeed}.{i}"] = Ber.Tlv(SnmpType.Gauge32, i == 3 ? [0xFF, 0xFF, 0xFF, 0xFF] : [0x3B, 0x9A, 0xCA, 0x00]);
            _mib[$"{IfMib.IfAdmin}.{i}"] = Ber.Integer(1);
            _mib[$"{IfMib.IfOper}.{i}"] = Ber.Integer(i == 2 ? 2 : 1);
            _mib[$"{IfMib.IfHighSpeed}.{i}"] = Ber.Tlv(SnmpType.Gauge32, i == 3 ? [0x27, 0x10] : [0x03, 0xE8]);
        }
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_cts.IsCancellationRequested)
        {
            System.Net.Sockets.UdpReceiveResult req;
            try { req = await _udp.ReceiveAsync(_cts.Token); } catch { return; }
            var r = new Ber.Reader(req.Buffer);
            r.Enter();
            long version = r.ReadInteger();
            var community = Encoding.UTF8.GetString(r.ReadOctets());
            if (community != "lab") continue;
            var (type, _, _) = r.Enter();
            long id = r.ReadInteger(), nonRep = r.ReadInteger(), maxRep = r.ReadInteger();
            r.Enter();
            var oids = new List<string>();
            while (r.More) { r.Enter(); var (_, s, l) = r.Next(); oids.Add(Ber.DecodeOid(req.Buffer.AsSpan(s, l))); r.Next(); }
            var vbs = new List<byte[]>();
            foreach (var oid in oids)
            {
                if (type == SnmpType.GetRequest)
                    vbs.Add(Ber.Sequence(Ber.Oid(oid), _mib.TryGetValue(oid, out var v) ? v : [SnmpType.NoSuchInstance, 0]));
                else
                {
                    var cur = oid;
                    int reps = type == SnmpType.GetBulkRequest ? (int)maxRep : 1;
                    for (int k = 0; k < reps; k++)
                    {
                        var next = _mib.Keys.FirstOrDefault(x => Ber.CompareOid(x, cur) > 0);
                        if (next is null) { vbs.Add(Ber.Sequence(Ber.Oid(cur), [SnmpType.EndOfMibView, 0])); break; }
                        vbs.Add(Ber.Sequence(Ber.Oid(next), _mib[next]));
                        cur = next;
                    }
                }
            }
            var resp = Ber.Sequence(Ber.Integer(version), Ber.Octets(community),
                Ber.Seq(SnmpType.Response, Ber.Integer(id), Ber.Integer(0), Ber.Integer(0), Ber.Sequence(vbs.ToArray())));
            await _udp.SendAsync(resp, req.RemoteEndPoint);
        }
    }

    public void Dispose() { _cts.Cancel(); _udp.Dispose(); }
}
