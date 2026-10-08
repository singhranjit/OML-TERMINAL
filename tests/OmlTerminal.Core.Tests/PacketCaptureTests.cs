using System.Buffers.Binary;
using System.Text;
using OmlTerminal.Core.Capture;

namespace OmlTerminal.Core.Tests;

/// <summary>Builds real frames byte by byte.</summary>
internal static class Frames
{
    public static readonly byte[] MacA = [0x00, 0x50, 0x56, 0xa1, 0x00, 0x01];
    public static readonly byte[] MacB = [0x00, 0x50, 0x56, 0xa1, 0x00, 0x02];
    public static readonly byte[] Bcast = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];

    public static byte[] Eth(byte[] dst, byte[] src, ushort type, byte[] payload, int? vlan = null)
    {
        var l = new List<byte>(dst);
        l.AddRange(src);
        if (vlan is { } v) { l.AddRange([0x81, 0x00, (byte)(v >> 8), (byte)v]); }
        l.Add((byte)(type >> 8)); l.Add((byte)type);
        l.AddRange(payload);
        return l.ToArray();
    }

    public static byte[] Ip(string src, string dst, byte proto, byte[] payload, byte ttl = 64)
    {
        var h = new byte[20 + payload.Length];
        h[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)h.Length);
        h[8] = ttl;
        h[9] = proto;
        System.Net.IPAddress.Parse(src).GetAddressBytes().CopyTo(h, 12);
        System.Net.IPAddress.Parse(dst).GetAddressBytes().CopyTo(h, 16);
        payload.CopyTo(h, 20);
        return h;
    }

    public static byte[] Tcp(int sp, int dp, uint seq, uint ack, TcpFlags flags, byte[]? payload = null, ushort window = 64240)
    {
        payload ??= [];
        var t = new byte[20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(t, (ushort)sp);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), (ushort)dp);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), ack);
        t[12] = 0x50;
        t[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(14), window);
        payload.CopyTo(t, 20);
        return t;
    }

    public static byte[] Udp(int sp, int dp, byte[] payload)
    {
        var u = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(u, (ushort)sp);
        BinaryPrimitives.WriteUInt16BigEndian(u.AsSpan(2), (ushort)dp);
        BinaryPrimitives.WriteUInt16BigEndian(u.AsSpan(4), (ushort)u.Length);
        payload.CopyTo(u, 8);
        return u;
    }

    public static byte[] DnsQuery(ushort id, string name, bool response = false, int rcode = 0, string? answerIp = null)
    {
        var l = new List<byte> { (byte)(id >> 8), (byte)id, (byte)(response ? 0x81 : 0x01), (byte)(response ? 0x80 | rcode : 0x00), 0, 1, 0, (byte)(answerIp is null ? 0 : 1), 0, 0, 0, 0 };
        foreach (var part in name.Split('.')) { l.Add((byte)part.Length); l.AddRange(Encoding.ASCII.GetBytes(part)); }
        l.AddRange([0, 0, 1, 0, 1]);
        if (answerIp is not null)
        {
            l.AddRange([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
            l.AddRange(System.Net.IPAddress.Parse(answerIp).GetAddressBytes());
        }
        return l.ToArray();
    }

    public static byte[] Arp(int op, byte[] sha, string spa, string tpa)
    {
        var a = new byte[28];
        a[1] = 1; a[2] = 8; a[4] = 6; a[5] = 4; a[7] = (byte)op;
        sha.CopyTo(a, 8);
        System.Net.IPAddress.Parse(spa).GetAddressBytes().CopyTo(a, 14);
        System.Net.IPAddress.Parse(tpa).GetAddressBytes().CopyTo(a, 24);
        return a;
    }

    public static byte[] Dhcp(int type, uint xid, string hostname = "")
    {
        var d = new byte[240];
        d[0] = (byte)(type is 1 or 3 ? 1 : 2);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), xid);
        MacA.CopyTo(d, 28);
        d[236] = 99; d[237] = 130; d[238] = 83; d[239] = 99;
        var l = new List<byte>(d) { 53, 1, (byte)type };
        if (hostname.Length > 0) { l.Add(12); l.Add((byte)hostname.Length); l.AddRange(Encoding.ASCII.GetBytes(hostname)); }
        l.Add(255);
        return l.ToArray();
    }

    public static byte[] ClientHello(string sni)
    {
        var name = Encoding.ASCII.GetBytes(sni);
        var ext = new List<byte> { 0, 0 };
        int listLen = name.Length + 3;
        ext.AddRange([(byte)((listLen + 2) >> 8), (byte)(listLen + 2), (byte)(listLen >> 8), (byte)listLen, 0, (byte)(name.Length >> 8), (byte)name.Length]);
        ext.AddRange(name);
        var body = new List<byte> { 3, 3 };
        body.AddRange(new byte[32]);
        body.Add(0);
        body.AddRange([0, 2, 0x13, 0x01]);
        body.AddRange([1, 0]);
        body.AddRange([(byte)(ext.Count >> 8), (byte)ext.Count]);
        body.AddRange(ext);
        var hs = new List<byte> { 1, 0, (byte)(body.Count >> 8), (byte)body.Count };
        hs.AddRange(body);
        var rec = new List<byte> { 0x16, 3, 1, (byte)(hs.Count >> 8), (byte)hs.Count };
        rec.AddRange(hs);
        return rec.ToArray();
    }

    public static DecodedPacket Decode(byte[] frame, int n = 1, DateTime? at = null) =>
        PacketDecoder.Decode(frame, LinkTypes.Ethernet, n, at ?? DateTime.Now, frame.Length);
}

public class PacketDecoderTests
{
    [Fact]
    public void DecodesTcpSynOverVlan()
    {
        var p = Frames.Decode(Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip("10.0.0.5", "10.0.0.9", 6, Frames.Tcp(51000, 443, 1000, 0, TcpFlags.Syn)), vlan: 20));
        Assert.Equal(20, p.Vlan);
        Assert.Equal("TCP", p.Protocol);
        Assert.Equal("10.0.0.5", p.Source);
        Assert.Equal(443, p.DstPort);
        Assert.Contains("[SYN]", p.Info);
        Assert.Contains(p.Layers, l => l.Name == "802.1Q VLAN");
    }

    [Fact]
    public void DecodesDnsQueryAndResponse()
    {
        var q = Frames.Decode(Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip("10.0.0.5", "10.0.0.53", 17, Frames.Udp(53001, 53, Frames.DnsQuery(0x1a2b, "intranet.corp.example")))));
        Assert.Equal("DNS", q.Protocol);
        Assert.Equal("Standard query 0x1a2b A intranet.corp.example", q.Info);

        var r = Frames.Decode(Frames.Eth(Frames.MacA, Frames.MacB, 0x0800, Frames.Ip("10.0.0.53", "10.0.0.5", 17, Frames.Udp(53, 53001, Frames.DnsQuery(0x1a2b, "intranet.corp.example", true, 0, "10.1.2.3")))));
        Assert.True(r.DnsResponse);
        Assert.EndsWith("A 10.1.2.3", r.Info);
    }

    [Fact]
    public void DecodesArpRequestReplyAndGratuitous()
    {
        Assert.Equal("Who has 10.0.0.1? Tell 10.0.0.5", Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacA, 0x0806, Frames.Arp(1, Frames.MacA, "10.0.0.5", "10.0.0.1"))).Info);
        Assert.Equal("10.0.0.1 is at 00:50:56:a1:00:02", Frames.Decode(Frames.Eth(Frames.MacA, Frames.MacB, 0x0806, Frames.Arp(2, Frames.MacB, "10.0.0.1", "10.0.0.5"))).Info);
        Assert.StartsWith("Gratuitous ARP", Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacA, 0x0806, Frames.Arp(1, Frames.MacA, "10.0.0.5", "10.0.0.5"))).Info);
    }

    [Fact]
    public void DecodesDhcpAndTlsSni()
    {
        var d = Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacA, 0x0800, Frames.Ip("0.0.0.0", "255.255.255.255", 17, Frames.Udp(68, 67, Frames.Dhcp(1, 0xdeadbeef, "LAPTOP-7")))));
        Assert.Equal("DHCP", d.Protocol);
        Assert.Equal(1, d.DhcpType);
        Assert.Contains("Discover", d.Info);
        Assert.Contains("LAPTOP-7", d.Info);

        var t = Frames.Decode(Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip("10.0.0.5", "93.184.216.34", 6, Frames.Tcp(51000, 443, 1, 1, TcpFlags.Psh | TcpFlags.Ack, Frames.ClientHello("www.example.com")))));
        Assert.Equal("TLS", t.Protocol);
        Assert.Equal("www.example.com", t.TlsSni);
        Assert.Equal("Client Hello (SNI=www.example.com)", t.Info);
    }

    [Fact]
    public void TruncatedFramesNeverThrow()
    {
        var full = Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip("10.0.0.5", "10.0.0.53", 17, Frames.Udp(53001, 53, Frames.DnsQuery(1, "a.b"))));
        for (int len = 0; len < full.Length; len++)
        {
            var p = PacketDecoder.Decode(full[..len], LinkTypes.Ethernet, 1, DateTime.Now, full.Length);
            Assert.NotNull(p.Protocol);
        }
        var junk = new Random(1);
        for (int i = 0; i < 500; i++)
        {
            var b = new byte[junk.Next(0, 200)];
            junk.NextBytes(b);
            PacketDecoder.Decode(b, LinkTypes.Ethernet, i, DateTime.Now, b.Length);
            PacketDecoder.Decode(b, LinkTypes.Radiotap, i, DateTime.Now, b.Length);
        }
    }

    [Fact]
    public void PcapRoundTrip()
    {
        var frame = Frames.Eth(Frames.Bcast, Frames.MacA, 0x0806, Frames.Arp(1, Frames.MacA, "10.0.0.5", "10.0.0.1"));
        var at = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Local).AddTicks(12340);
        using var ms = new MemoryStream();
        using (var w = new PcapWriter(ms, LinkTypes.Ethernet)) { w.Write(at, frame, frame.Length); w.Write(at.AddSeconds(1), frame, 60); }
        var frames = PcapReader.Read(ms.ToArray());
        Assert.Equal(2, frames.Count);
        Assert.Equal(frame, frames[0].Data);
        Assert.Equal(LinkTypes.Ethernet, frames[0].LinkType);
        Assert.Equal(at.Ticks / 10, frames[0].Timestamp.Ticks / 10);
        Assert.Equal(60, frames[1].OriginalLength);
    }
}

public class DisplayFilterTests
{
    private static readonly DecodedPacket Https = Frames.Decode(Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip("10.0.0.5", "93.184.216.34", 6, Frames.Tcp(51000, 443, 1, 0, TcpFlags.Syn))));
    private static readonly DecodedPacket Dns = Frames.Decode(Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip("10.0.0.5", "10.0.0.53", 17, Frames.Udp(53001, 53, Frames.DnsQuery(7, "files.corp.example")))));
    private static readonly DecodedPacket Arp = Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacA, 0x0806, Frames.Arp(1, Frames.MacA, "10.0.0.5", "10.0.0.1")));

    [Theory]
    [InlineData("tcp", true, false, false)]
    [InlineData("!arp", true, true, false)]
    [InlineData("ip.addr == 10.0.0.0/24", true, true, true)]
    [InlineData("ip.dst == 93.184.216.34 && tcp.port == 443", true, false, false)]
    [InlineData("tcp.flags.syn && !tcp.flags.ack", true, false, false)]
    [InlineData("dns.qry.name contains \"corp\"", false, true, false)]
    [InlineData("udp.port == 53 || arp", false, true, true)]
    [InlineData("eth.src == 00-50-56-A1-00-01", true, true, true)]
    [InlineData("broadcast", false, false, true)]
    [InlineData("10.0.0.53", false, true, false)]
    [InlineData("frame.len > 60", false, true, false)]
    [InlineData("(tcp or dns) and not ip.dst == 10.0.0.53", true, false, false)]
    [InlineData("\"Who has\"", false, false, true)]
    [InlineData("dns.qry.name", false, true, false)]
    [InlineData("tls.sni || http.host", false, false, false)]
    public void MatchesLikeWireshark(string filter, bool https, bool dns, bool arp)
    {
        var f = DisplayFilter.Parse(filter);
        Assert.Equal(https, f.Matches(Https));
        Assert.Equal(dns, f.Matches(Dns));
        Assert.Equal(arp, f.Matches(Arp));
    }

    [Theory]
    [InlineData("tcp.port ==")]
    [InlineData("ip.addr == banana")]
    [InlineData("(tcp")]
    [InlineData("nosuchproto")]
    public void RejectsBadFilters(string filter) => Assert.Throws<FilterSyntaxException>(() => DisplayFilter.Parse(filter));
}

public class TrafficAnalysisTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 10, 0, 0);

    private static DecodedPacket Tcp(int n, string s, int sp, string d, int dp, TcpFlags f, uint seq = 1, byte[]? data = null, double at = 0, ushort win = 64240) =>
        Frames.Decode(Frames.Eth(Frames.MacB, Frames.MacA, 0x0800, Frames.Ip(s, d, 6, Frames.Tcp(sp, dp, seq, 1, f, data, win))), n, T0.AddSeconds(at));

    [Fact]
    public void FindsDuplicateIpRefusedAndUnansweredConnectionsAndRetransmissions()
    {
        var data = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        var packets = new List<DecodedPacket>
        {
            Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacA, 0x0806, Frames.Arp(2, Frames.MacA, "10.0.0.50", "10.0.0.1")), 1, T0),
            Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacB, 0x0806, Frames.Arp(2, Frames.MacB, "10.0.0.50", "10.0.0.1")), 2, T0),
            Tcp(3, "10.0.0.5", 50001, "10.0.0.9", 8080, TcpFlags.Syn),
            Tcp(4, "10.0.0.9", 8080, "10.0.0.5", 50001, TcpFlags.Rst | TcpFlags.Ack),
            Tcp(5, "10.0.0.5", 50002, "10.0.0.77", 22, TcpFlags.Syn),
            Tcp(6, "10.0.0.5", 50003, "10.0.0.77", 22, TcpFlags.Syn, at: 1),
            Tcp(7, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Psh | TcpFlags.Ack, 100, data),
            Tcp(8, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Psh | TcpFlags.Ack, 100, data, 0.3),
            Tcp(10, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Psh | TcpFlags.Ack, 100, data, 0.9),
            Tcp(11, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Psh | TcpFlags.Ack, 100, data, 1.9),
            Tcp(9, "10.0.0.8", 80, "10.0.0.5", 50004, TcpFlags.Ack, 500, null, 0.4, win: 0),
        };
        var insights = TrafficAnalysis.Insights(packets);

        Assert.Contains(insights, i => i.Title == "Duplicate IP address 10.0.0.50" && i.Severity == InsightSeverity.Problem);
        Assert.Contains(insights, i => i.Title == "Connections refused by 10.0.0.9:8080");
        Assert.Contains(insights, i => i.Title == "No answer from 10.0.0.77:22");
        Assert.Contains(insights, i => i.Title.StartsWith("TCP retransmissions: 10.0.0.5:50004"));
        Assert.Contains(insights, i => i.Title == "TCP zero window");
        Assert.All(insights.Where(i => i.Filter.Length > 0), i => DisplayFilter.Parse(i.Filter));
    }

    [Fact]
    public void FindsDnsAndDhcpFailures()
    {
        var nx = Frames.Decode(Frames.Eth(Frames.MacA, Frames.MacB, 0x0800, Frames.Ip("10.0.0.53", "10.0.0.5", 17, Frames.Udp(53, 53001, Frames.DnsQuery(9, "typo.corp.example", true, 3)))), 1, T0);
        var discover = Frames.Decode(Frames.Eth(Frames.Bcast, Frames.MacA, 0x0800, Frames.Ip("0.0.0.0", "255.255.255.255", 17, Frames.Udp(68, 67, Frames.Dhcp(1, 42)))), 2, T0);
        var insights = TrafficAnalysis.Insights([nx, discover]);
        Assert.Contains(insights, i => i.Title == "DNS: typo.corp.example doesn't exist (NXDOMAIN)");
        Assert.Contains(insights, i => i.Title == "DHCP Discover with no Offer");
    }

    [Fact]
    public void FollowStreamSkipsRetransmissions()
    {
        var req = Encoding.ASCII.GetBytes("HELLO");
        var resp = Encoding.ASCII.GetBytes("WORLD");
        var packets = new List<DecodedPacket>
        {
            Tcp(1, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Psh | TcpFlags.Ack, 100, req),
            Tcp(2, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Psh | TcpFlags.Ack, 100, req),
            Tcp(3, "10.0.0.8", 80, "10.0.0.5", 50004, TcpFlags.Psh | TcpFlags.Ack, 900, resp),
        };
        var text = TrafficAnalysis.FollowTcpStream(packets, packets[0]);
        Assert.Equal(1, text.Split("HELLO").Length - 1);
        Assert.Contains("WORLD", text);
        Assert.Contains("10.0.0.8:80 → 10.0.0.5:50004", text);
    }

    [Fact]
    public void ConversationsAggregateBothDirections()
    {
        var packets = new List<DecodedPacket>
        {
            Tcp(1, "10.0.0.5", 50004, "10.0.0.8", 80, TcpFlags.Syn),
            Tcp(2, "10.0.0.8", 80, "10.0.0.5", 50004, TcpFlags.Syn | TcpFlags.Ack),
            Tcp(3, "10.0.0.5", 50005, "10.0.0.9", 80, TcpFlags.Syn),
        };
        var c = TrafficAnalysis.Conversations(packets);
        Assert.Equal(2, c.Count);
        Assert.Equal(2, c.Single(x => x.B == "10.0.0.8" || x.A == "10.0.0.8").Packets);
    }
}
