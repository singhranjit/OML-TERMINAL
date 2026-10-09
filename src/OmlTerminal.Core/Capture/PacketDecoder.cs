using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace OmlTerminal.Core.Capture;

/// <summary>pcap link-layer types this decoder understands.</summary>
public static class LinkTypes
{
    public const int Null = 0;
    public const int Ethernet = 1;
    public const int Raw = 101;
    public const int Ieee80211 = 105;
    public const int Radiotap = 127;
    public const int Ipv4 = 228;
    public const int Ipv6 = 229;
}

public sealed record PacketLayer(string Name, string Summary, int Offset, int Length, IReadOnlyList<(string Name, string Value)> Fields);

[Flags]
public enum TcpFlags : ushort { None = 0, Fin = 1, Syn = 2, Rst = 4, Psh = 8, Ack = 16, Urg = 32, Ece = 64, Cwr = 128 }

/// <summary>One captured frame, decoded. Everything is best-effort: a truncated or malformed frame decodes as far
/// as it safely can and is marked Malformed, it never throws.</summary>
public sealed class DecodedPacket
{
    public int Number { get; init; }
    public DateTime Timestamp { get; init; }
    public byte[] Data { get; init; } = [];
    public int OriginalLength { get; init; }
    public int LinkType { get; init; }

    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Info { get; set; } = "";
    public bool Malformed { get; set; }

    public string? SrcMac { get; set; }
    public string? DstMac { get; set; }
    public int? Vlan { get; set; }
    public string? SrcIp { get; set; }
    public string? DstIp { get; set; }
    public int? Ttl { get; set; }
    public int IpProtocol { get; set; } = -1;
    public int? SrcPort { get; set; }
    public int? DstPort { get; set; }
    public TcpFlags Flags { get; set; }
    public uint Seq { get; set; }
    public uint Ack { get; set; }
    public int Window { get; set; }
    public int PayloadOffset { get; set; }
    public int PayloadLength { get; set; }

    public string? DnsName { get; set; }
    public int? DnsId { get; set; }
    public bool DnsResponse { get; set; }
    public int DnsRcode { get; set; }
    public string? TlsSni { get; set; }
    public string? HttpHost { get; set; }
    public int? DhcpType { get; set; }
    public uint? DhcpXid { get; set; }
    public string? DhcpHostname { get; set; }
    public int? ArpOp { get; set; }
    public string? ArpSenderIp { get; set; }
    public string? ArpSenderMac { get; set; }
    public int? IcmpType { get; set; }
    public int? IcmpCode { get; set; }
    /// <summary>For an ICMP error: the original packet's destination it's about ("10.0.0.5:22", or just the address).</summary>
    public string? IcmpAbout { get; set; }
    public bool StpTopologyChange { get; set; }
    public string? Ssid { get; set; }
    public int? SignalDbm { get; set; }

    public HashSet<string> Protocols { get; } = new(StringComparer.OrdinalIgnoreCase);
    private List<PacketLayer>? _layers;

    /// <summary>False for packets decoded in bulk (live capture, opening a file): the per-field detail tree is then
    /// rebuilt on demand when someone looks at the packet - it's about half of each packet's memory.</summary>
    internal bool CollectLayers { get; init; } = true;

    /// <summary>The protocol tree for the details pane.</summary>
    public List<PacketLayer> Layers => _layers ??= CollectLayers
        ? new()
        : PacketDecoder.Decode(Data, LinkType, Number, Timestamp, OriginalLength).Layers;

    public bool IsBroadcast => DstMac == "ff:ff:ff:ff:ff:ff";
    public bool IsMulticast => DstMac is { Length: 17 } m && !IsBroadcast && (Convert.ToByte(m[..2], 16) & 1) == 1;
}

/// <summary>Turns raw frames into DecodedPacket. Covers what network engineers look at every day: Ethernet/802.1Q,
/// ARP, IPv4/IPv6, ICMP/ICMPv6, TCP/UDP, DNS/mDNS/LLMNR, DHCP, TLS SNI, HTTP, NTP, Syslog, TFTP, HSRP, BGP, OSPF,
/// CDP, LLDP, STP and basic 802.11 management frames.</summary>
public static class PacketDecoder
{
    /// <param name="details">False when decoding many packets at once: the detail tree is skipped and rebuilt on demand.</param>
    public static DecodedPacket Decode(byte[] data, int linkType, int number, DateTime timestamp, int originalLength, bool details = true)
    {
        var p = new DecodedPacket { Number = number, Timestamp = timestamp, Data = data, OriginalLength = originalLength, LinkType = linkType, CollectLayers = details };
        try
        {
            switch (linkType)
            {
                case LinkTypes.Ethernet: Ethernet(p, 0); break;
                case LinkTypes.Raw:
                    if (data.Length > 0 && data[0] >> 4 == 6) Ipv6(p, 0); else Ipv4(p, 0);
                    break;
                case LinkTypes.Ipv4: Ipv4(p, 0); break;
                case LinkTypes.Ipv6: Ipv6(p, 0); break;
                case LinkTypes.Null:
                    if (data.Length >= 4)
                    {
                        uint fam = BinaryPrimitives.ReadUInt32LittleEndian(data);
                        if (fam == 2) Ipv4(p, 4); else Ipv6(p, 4);
                    }
                    break;
                case LinkTypes.Radiotap: Radiotap(p); break;
                case LinkTypes.Ieee80211: Ieee80211(p, 0); break;
                default:
                    p.Protocol = $"LINK{linkType}";
                    p.Info = "Unsupported link type";
                    break;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
        {
            p.Malformed = true;
            p.Info = string.IsNullOrEmpty(p.Info) ? "[Malformed packet]" : p.Info + " [Malformed]";
        }
        if (p.Protocol.Length == 0) p.Protocol = "DATA";
        if (p.Source.Length == 0) p.Source = p.SrcIp ?? p.SrcMac ?? "";
        if (p.Destination.Length == 0) p.Destination = p.DstIp ?? p.DstMac ?? "";
        return p;
    }

    // ---------- helpers ----------

    private static string Mac(byte[] d, int o) =>
        o + 6 > d.Length ? "?" : $"{d[o]:x2}:{d[o + 1]:x2}:{d[o + 2]:x2}:{d[o + 3]:x2}:{d[o + 4]:x2}:{d[o + 5]:x2}";

    private static string Ip4(byte[] d, int o) => $"{d[o]}.{d[o + 1]}.{d[o + 2]}.{d[o + 3]}";

    private static string Ip6(byte[] d, int o) => new IPAddress(d.AsSpan(o, 16)).ToString();

    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));

    private static void Layer(DecodedPacket p, string name, string summary, int offset, int length, params (string, string)[] fields)
    {
        if (p.CollectLayers) p.Layers.Add(new PacketLayer(name, summary, offset, Math.Max(0, Math.Min(length, p.Data.Length - offset)), fields));
    }

    private static void Set(DecodedPacket p, string proto, string info)
    {
        p.Protocol = proto;
        p.Info = info;
        p.Protocols.Add(proto);
    }

    private static string Ascii(byte[] d, int o, int len)
    {
        len = Math.Max(0, Math.Min(len, d.Length - o));
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) { byte b = d[o + i]; sb.Append(b is >= 32 and < 127 ? (char)b : '.'); }
        return sb.ToString();
    }

    // ---------- link layer ----------

    private static void Ethernet(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (d.Length < o + 14) { Set(p, "ETH", "Truncated Ethernet frame"); p.Malformed = true; return; }
        p.DstMac = Mac(d, o);
        p.SrcMac = Mac(d, o + 6);
        p.Protocols.Add("eth");
        int type = U16(d, o + 12);
        int next = o + 14;
        Layer(p, "Ethernet II", $"{p.SrcMac} → {p.DstMac}", o, 14, ("Destination", p.DstMac), ("Source", p.SrcMac), ("Type", $"0x{type:x4}"));
        while (type is 0x8100 or 0x88a8 or 0x9100 && d.Length >= next + 4)
        {
            int tci = U16(d, next);
            p.Vlan ??= tci & 0x0fff;
            p.Protocols.Add("vlan");
            Layer(p, "802.1Q VLAN", $"VLAN {tci & 0x0fff}, priority {tci >> 13}", next, 4, ("ID", (tci & 0x0fff).ToString()), ("Priority (PCP)", (tci >> 13).ToString()));
            type = U16(d, next + 2);
            next += 4;
        }
        if (type < 0x0600) { Llc(p, next, type); return; }
        switch (type)
        {
            case 0x0800: Ipv4(p, next); break;
            case 0x86dd: Ipv6(p, next); break;
            case 0x0806: Arp(p, next); break;
            case 0x88cc: Lldp(p, next); break;
            case 0x888e: Set(p, "EAPOL", "802.1X authentication"); break;
            case 0x8809: Set(p, "LACP", "Link aggregation control"); break;
            case 0x8847 or 0x8848: Set(p, "MPLS", "MPLS labelled packet"); break;
            case 0x88f7: Set(p, "PTP", "Precision Time Protocol"); break;
            default: Set(p, $"0x{type:x4}", $"Ethertype 0x{type:x4}"); break;
        }
    }

    private static void Llc(DecodedPacket p, int o, int length)
    {
        var d = p.Data;
        if (d.Length < o + 3) { Set(p, "LLC", "802.3 frame"); return; }
        byte dsap = d[o], ssap = d[o + 1];
        if (dsap == 0x42 && ssap == 0x42) { Stp(p, o + 3); return; }
        if (dsap == 0xaa && ssap == 0xaa && d.Length >= o + 8)
        {
            int oui = d[o + 3] << 16 | d[o + 4] << 8 | d[o + 5];
            int pid = U16(d, o + 6);
            if (oui == 0x00000c && pid == 0x2000) { Cdp(p, o + 8); return; }
            if (oui == 0x00000c && pid == 0x2004) { Set(p, "DTP", "Dynamic Trunking Protocol"); return; }
            if (oui == 0x00000c && pid == 0x010b) { Set(p, "PVST+", "Cisco PVST+ BPDU"); Stp(p, o + 8); p.Protocol = "PVST+"; return; }
            if (oui == 0x00000c && pid == 0x0111) { Set(p, "UDLD", "Unidirectional Link Detection"); return; }
        }
        Set(p, "LLC", $"802.3 frame, DSAP 0x{dsap:x2} SSAP 0x{ssap:x2}, length {length}");
    }

    private static void Stp(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (d.Length < o + 4) { Set(p, "STP", "Spanning Tree"); return; }
        int version = d[o + 2], type = d[o + 3];
        if (type == 0x80) { Set(p, "STP", "Topology Change Notification"); p.StpTopologyChange = true; return; }
        if (d.Length < o + 35) { Set(p, "STP", "Spanning Tree BPDU"); return; }
        int flags = d[o + 4];
        int rootPri = U16(d, o + 5);
        string rootMac = Mac(d, o + 7);
        uint cost = U32(d, o + 13);
        int bridgePri = U16(d, o + 17);
        string bridgeMac = Mac(d, o + 19);
        int port = U16(d, o + 25);
        p.StpTopologyChange = (flags & 1) != 0;
        string kind = version switch { 0 => "Conf.", 2 => "RST.", 3 => "MST.", _ => "BPDU" };
        Set(p, "STP", $"{kind} Root = {rootPri}/{rootMac}  Cost = {cost}  Port = 0x{port:x4}{(p.StpTopologyChange ? "  [Topology change]" : "")}");
        Layer(p, "Spanning Tree Protocol", kind, o, 35, ("Root", $"{rootPri} / {rootMac}"), ("Root path cost", cost.ToString()),
            ("Bridge", $"{bridgePri} / {bridgeMac}"), ("Port", $"0x{port:x4}"), ("Topology change", p.StpTopologyChange ? "yes" : "no"));
    }

    private static void Cdp(DecodedPacket p, int o)
    {
        var d = p.Data;
        string device = "", port = "", platform = "";
        int i = o + 4;
        var fields = new List<(string, string)>();
        while (i + 4 <= d.Length)
        {
            int t = U16(d, i), len = U16(d, i + 2);
            if (len < 4 || i + len > d.Length) break;
            string val = Ascii(d, i + 4, len - 4);
            switch (t)
            {
                case 1: device = val; fields.Add(("Device ID", val)); break;
                case 3: port = val; fields.Add(("Port ID", val)); break;
                case 6: platform = val; fields.Add(("Platform", val)); break;
                case 5: fields.Add(("Software", val.Split('.', 2)[0])); break;
                case 0x0a when len >= 6: fields.Add(("Native VLAN", U16(d, i + 4).ToString())); break;
            }
            i += len;
        }
        Set(p, "CDP", $"Device ID: {device}  Port ID: {port}{(platform.Length > 0 ? $"  Platform: {platform}" : "")}");
        Layer(p, "Cisco Discovery Protocol", device, o, d.Length - o, fields.ToArray());
    }

    private static void Lldp(DecodedPacket p, int o)
    {
        var d = p.Data;
        string name = "", port = "", desc = "";
        var fields = new List<(string, string)>();
        int i = o;
        while (i + 2 <= d.Length)
        {
            int hdr = U16(d, i), t = hdr >> 9, len = hdr & 0x1ff;
            if (t == 0 || i + 2 + len > d.Length) break;
            switch (t)
            {
                case 2 when len > 1: port = Ascii(d, i + 3, len - 1); fields.Add(("Port ID", port)); break;
                case 4: desc = Ascii(d, i + 2, len); fields.Add(("Port description", desc)); break;
                case 5: name = Ascii(d, i + 2, len); fields.Add(("System name", name)); break;
                case 8 when len >= 6 && d[i + 3] == 1: fields.Add(("Management address", Ip4(d, i + 4))); break;
            }
            i += 2 + len;
        }
        Set(p, "LLDP", $"System name: {name}  Port: {(desc.Length > 0 ? desc : port)}");
        Layer(p, "Link Layer Discovery Protocol", name, o, d.Length - o, fields.ToArray());
    }

    private static void Arp(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (d.Length < o + 28) { Set(p, "ARP", "Truncated ARP"); p.Malformed = true; return; }
        int op = U16(d, o + 6);
        string sha = Mac(d, o + 8), spa = Ip4(d, o + 14), tha = Mac(d, o + 18), tpa = Ip4(d, o + 24);
        p.ArpOp = op;
        p.ArpSenderIp = spa;
        p.ArpSenderMac = sha;
        p.SrcIp = spa;
        p.DstIp = tpa;
        p.Source = sha;
        p.Destination = p.DstMac ?? tha;
        string info = op switch
        {
            1 when spa == tpa => $"Gratuitous ARP for {spa} (announcement)",
            1 when spa == "0.0.0.0" => $"ARP probe: who has {tpa}? (address conflict check)",
            1 => $"Who has {tpa}? Tell {spa}",
            2 => $"{spa} is at {sha}",
            _ => $"ARP opcode {op}",
        };
        Set(p, "ARP", info);
        Layer(p, "Address Resolution Protocol", op == 1 ? "request" : "reply", o, 28,
            ("Opcode", op == 1 ? "request (1)" : op == 2 ? "reply (2)" : op.ToString()), ("Sender MAC", sha), ("Sender IP", spa),
            ("Target MAC", tha), ("Target IP", tpa));
    }

    // ---------- network layer ----------

    private static readonly Dictionary<int, string> IpProtocols = new()
    {
        [1] = "ICMP", [2] = "IGMP", [6] = "TCP", [17] = "UDP", [41] = "IPv6", [47] = "GRE", [50] = "ESP", [51] = "AH",
        [58] = "ICMPv6", [88] = "EIGRP", [89] = "OSPF", [103] = "PIM", [112] = "VRRP", [132] = "SCTP",
    };

    private static void Ipv4(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (d.Length < o + 20 || d[o] >> 4 != 4) { Set(p, "IPv4", "Truncated or invalid IPv4 header"); p.Malformed = true; return; }
        int ihl = (d[o] & 0x0f) * 4;
        int total = U16(d, o + 2);
        int fragField = U16(d, o + 6);
        int ttl = d[o + 8], proto = d[o + 9];
        int dscp = d[o + 1] >> 2;
        p.SrcIp = Ip4(d, o + 12);
        p.DstIp = Ip4(d, o + 16);
        p.Ttl = ttl;
        p.IpProtocol = proto;
        p.Protocols.Add("ip");
        int fragOffset = (fragField & 0x1fff) * 8;
        bool moreFragments = (fragField & 0x2000) != 0;
        Layer(p, "Internet Protocol Version 4", $"{p.SrcIp} → {p.DstIp}", o, ihl,
            ("Source", p.SrcIp), ("Destination", p.DstIp), ("TTL", ttl.ToString()), ("Protocol", IpProtocols.GetValueOrDefault(proto, proto.ToString())),
            ("Total length", total.ToString()), ("DSCP", dscp.ToString()), ("Identification", $"0x{U16(d, o + 4):x4}"),
            ("Flags", $"{((fragField & 0x4000) != 0 ? "DF " : "")}{(moreFragments ? "MF" : "")}".Trim()), ("Fragment offset", fragOffset.ToString()));
        int end = Math.Min(d.Length, o + Math.Max(total, ihl));
        if (fragOffset > 0) { Set(p, IpProtocols.GetValueOrDefault(proto, "IPv4"), $"Fragmented IP packet (offset {fragOffset})"); return; }
        Transport(p, proto, o + ihl, end);
    }

    private static void Ipv6(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (d.Length < o + 40) { Set(p, "IPv6", "Truncated IPv6 header"); p.Malformed = true; return; }
        int payloadLen = U16(d, o + 4);
        int next = d[o + 6];
        p.Ttl = d[o + 7];
        p.SrcIp = Ip6(d, o + 8);
        p.DstIp = Ip6(d, o + 24);
        p.Protocols.Add("ipv6");
        Layer(p, "Internet Protocol Version 6", $"{p.SrcIp} → {p.DstIp}", o, 40,
            ("Source", p.SrcIp), ("Destination", p.DstIp), ("Hop limit", p.Ttl.ToString()!), ("Next header", IpProtocols.GetValueOrDefault(next, next.ToString())));
        int i = o + 40, end = Math.Min(d.Length, o + 40 + payloadLen);
        while (next is 0 or 43 or 60 or 44 && i + 8 <= end)
        {
            int nh = d[i];
            if (next == 44)
            {
                if ((U16(d, i + 2) & 0xfff8) != 0) { Set(p, "IPv6", "Fragmented IPv6 packet"); return; }
                i += 8;
            }
            else i += (d[i + 1] + 1) * 8;
            next = nh;
        }
        p.IpProtocol = next;
        Transport(p, next, i, end);
    }

    private static void Transport(DecodedPacket p, int proto, int o, int end)
    {
        switch (proto)
        {
            case 6: Tcp(p, o, end); break;
            case 17: Udp(p, o, end); break;
            case 1: Icmp(p, o, end); break;
            case 58: Icmp6(p, o, end); break;
            case 89: Ospf(p, o); break;
            case 2: Set(p, "IGMP", IgmpInfo(p.Data, o)); break;
            default: Set(p, IpProtocols.GetValueOrDefault(proto, $"IP/{proto}"), $"IP protocol {proto}"); break;
        }
    }

    private static string IgmpInfo(byte[] d, int o) => o >= d.Length ? "IGMP" : d[o] switch
    {
        0x11 => o + 8 <= d.Length ? $"Membership Query, group {Ip4(d, o + 4)}" : "Membership Query",
        0x16 or 0x12 => o + 8 <= d.Length ? $"Membership Report, group {Ip4(d, o + 4)}" : "Membership Report",
        0x22 => "Membership Report v3",
        0x17 => o + 8 <= d.Length ? $"Leave Group {Ip4(d, o + 4)}" : "Leave Group",
        _ => $"IGMP type 0x{d[o]:x2}",
    };

    private static void Ospf(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (d.Length < o + 24) { Set(p, "OSPF", "OSPF"); return; }
        string type = d[o + 1] switch { 1 => "Hello", 2 => "DB Description", 3 => "LS Request", 4 => "LS Update", 5 => "LS Acknowledge", _ => $"type {d[o + 1]}" };
        string router = Ip4(d, o + 4), area = Ip4(d, o + 8);
        Set(p, "OSPF", $"{type} Packet  Router {router}  Area {area}");
        Layer(p, "Open Shortest Path First", type, o, 24, ("Type", type), ("Router ID", router), ("Area", area));
    }

    private static readonly Dictionary<int, string> UnreachableCodes = new()
    {
        [0] = "network unreachable", [1] = "host unreachable", [2] = "protocol unreachable", [3] = "port unreachable",
        [4] = "fragmentation needed", [9] = "network prohibited", [10] = "host prohibited", [13] = "administratively prohibited",
    };

    private static void Icmp(DecodedPacket p, int o, int end)
    {
        var d = p.Data;
        if (o + 4 > d.Length) { Set(p, "ICMP", "Truncated ICMP"); return; }
        int type = d[o], code = d[o + 1];
        p.IcmpType = type;
        p.IcmpCode = code;
        string info = type switch
        {
            8 or 0 when o + 8 <= d.Length => $"Echo (ping) {(type == 8 ? "request" : "reply")}  id=0x{U16(d, o + 4):x4}, seq={U16(d, o + 6)}, ttl={p.Ttl}",
            3 => $"Destination unreachable ({UnreachableCodes.GetValueOrDefault(code, $"code {code}")})" + Embedded(p, o + 8),
            11 => (code == 0 ? "Time-to-live exceeded in transit" : "Fragment reassembly time exceeded") + Embedded(p, o + 8),
            5 => "Redirect",
            _ => $"ICMP type {type} code {code}",
        };
        Set(p, "ICMP", info);
        Layer(p, "Internet Control Message Protocol", info, o, end - o, ("Type", type.ToString()), ("Code", code.ToString()));
    }

    /// <summary>"for 10.0.0.5:443" - the packet an ICMP error is about, so the engineer sees what was blocked.</summary>
    private static string Embedded(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (o + 28 > d.Length || d[o] >> 4 != 4) return "";
        int ihl = (d[o] & 0x0f) * 4, proto = d[o + 9];
        string dst = Ip4(d, o + 16);
        if (proto is 6 or 17 && o + ihl + 4 <= d.Length)
        {
            p.IcmpAbout = $"{dst}:{U16(d, o + ihl + 2)}";
            return $" for {(proto == 6 ? "TCP" : "UDP")} {p.IcmpAbout}";
        }
        p.IcmpAbout = dst;
        return $" for {dst}";
    }

    private static void Icmp6(DecodedPacket p, int o, int end)
    {
        var d = p.Data;
        if (o + 4 > d.Length) { Set(p, "ICMPv6", "Truncated ICMPv6"); return; }
        int type = d[o], code = d[o + 1];
        p.IcmpType = type;
        p.IcmpCode = code;
        string target = o + 24 <= d.Length ? Ip6(d, o + 8) : "";
        string info = type switch
        {
            128 => "Echo (ping) request",
            129 => "Echo (ping) reply",
            1 => $"Destination unreachable (code {code})",
            3 => "Time exceeded",
            133 => "Router Solicitation",
            134 => "Router Advertisement",
            135 => $"Neighbor Solicitation for {target}",
            136 => $"Neighbor Advertisement {target}",
            137 => "Redirect",
            143 => "Multicast Listener Report v2",
            _ => $"ICMPv6 type {type}",
        };
        Set(p, "ICMPv6", info);
        Layer(p, "Internet Control Message Protocol v6", info, o, end - o, ("Type", type.ToString()), ("Code", code.ToString()));
    }

    // ---------- transport layer ----------

    private static readonly (TcpFlags Flag, string Name)[] FlagNames =
        [(TcpFlags.Syn, "SYN"), (TcpFlags.Fin, "FIN"), (TcpFlags.Rst, "RST"), (TcpFlags.Psh, "PSH"), (TcpFlags.Ack, "ACK"), (TcpFlags.Urg, "URG")];

    public static string FlagText(TcpFlags f) => string.Join(", ", FlagNames.Where(x => f.HasFlag(x.Flag)).Select(x => x.Name));

    private static void Tcp(DecodedPacket p, int o, int end)
    {
        var d = p.Data;
        if (o + 20 > d.Length) { Set(p, "TCP", "Truncated TCP header"); p.Malformed = true; return; }
        p.SrcPort = U16(d, o);
        p.DstPort = U16(d, o + 2);
        p.Seq = U32(d, o + 4);
        p.Ack = U32(d, o + 8);
        int hlen = (d[o + 12] >> 4) * 4;
        p.Flags = (TcpFlags)(d[o + 13] | (d[o + 12] & 1) << 8);
        p.Window = U16(d, o + 14);
        p.PayloadOffset = Math.Min(o + hlen, d.Length);
        p.PayloadLength = Math.Max(0, Math.Min(end, d.Length) - p.PayloadOffset);
        p.Protocols.Add("tcp");
        string flags = FlagText(p.Flags);
        Set(p, "TCP", $"{p.SrcPort} → {p.DstPort} [{flags}] Seq={p.Seq} Ack={p.Ack} Win={p.Window} Len={p.PayloadLength}");
        Layer(p, "Transmission Control Protocol", $"{p.SrcPort} → {p.DstPort} [{flags}]", o, hlen,
            ("Source port", p.SrcPort.ToString()!), ("Destination port", p.DstPort.ToString()!), ("Sequence", p.Seq.ToString()),
            ("Acknowledgment", p.Ack.ToString()), ("Flags", flags), ("Window", p.Window.ToString()), ("Payload", $"{p.PayloadLength} bytes"));
        if (p.PayloadLength > 0) Application(p, tcp: true);
    }

    private static void Udp(DecodedPacket p, int o, int end)
    {
        var d = p.Data;
        if (o + 8 > d.Length) { Set(p, "UDP", "Truncated UDP header"); p.Malformed = true; return; }
        p.SrcPort = U16(d, o);
        p.DstPort = U16(d, o + 2);
        int len = U16(d, o + 4);
        p.PayloadOffset = o + 8;
        p.PayloadLength = Math.Max(0, Math.Min(Math.Min(end, d.Length), o + Math.Max(8, len)) - p.PayloadOffset);
        p.Protocols.Add("udp");
        Set(p, "UDP", $"{p.SrcPort} → {p.DstPort} Len={p.PayloadLength}");
        Layer(p, "User Datagram Protocol", $"{p.SrcPort} → {p.DstPort}", o, 8,
            ("Source port", p.SrcPort.ToString()!), ("Destination port", p.DstPort.ToString()!), ("Length", len.ToString()));
        Application(p, tcp: false);
    }

    private static bool Port(DecodedPacket p, int port) => p.SrcPort == port || p.DstPort == port;

    private static void Application(DecodedPacket p, bool tcp)
    {
        var d = p.Data;
        int o = p.PayloadOffset, len = p.PayloadLength;
        if (len <= 0) return;
        if (Port(p, 53) || Port(p, 5353) || Port(p, 5355)) { Dns(p, tcp ? o + 2 : o, Port(p, 5353) ? "MDNS" : Port(p, 5355) ? "LLMNR" : "DNS"); return; }
        if (!tcp && (Port(p, 67) || Port(p, 68))) { Dhcp(p, o); return; }
        if (tcp && d[o] == 0x16 && len >= 6) { Tls(p, o); return; }
        if (tcp && (d[o] is 0x17 or 0x15 or 0x14) && len >= 5 && d[o + 1] == 3) { Set(p, "TLS", $"{(d[o] == 0x17 ? "Application Data" : d[o] == 0x15 ? "Alert" : "Change Cipher Spec")}"); p.Protocols.Add("tls"); return; }
        if (tcp && Http(p, o, len)) return;
        if (tcp && len >= 4 && d[o] == 'S' && d[o + 1] == 'S' && d[o + 2] == 'H' && d[o + 3] == '-') { Set(p, "SSH", Ascii(d, o, Math.Min(len, 60)).TrimEnd('.')); return; }
        if (Port(p, 22)) { Set(p, "SSH", $"Encrypted packet (len={len})"); return; }
        if (!tcp && Port(p, 123) && len >= 48) { Set(p, "NTP", $"NTP v{(d[o] >> 3) & 7} {((d[o] & 7) switch { 3 => "client", 4 => "server", 1 => "symmetric", _ => "mode " + (d[o] & 7) })}, stratum {d[o + 1]}"); return; }
        if (!tcp && Port(p, 514) && d[o] == '<') { Syslog(p, o, len); return; }
        if (!tcp && Port(p, 69) && len >= 4) { Tftp(p, o, len); return; }
        if (!tcp && Port(p, 1985) && len >= 20) { Hsrp(p, o); return; }
        if (tcp && Port(p, 179) && len >= 19) { Set(p, "BGP", $"{d[o + 18] switch { 1 => "OPEN", 2 => "UPDATE", 3 => "NOTIFICATION", 4 => "KEEPALIVE", _ => "message" }} Message"); return; }
        if (!tcp && Port(p, 443)) { Set(p, "QUIC", $"QUIC / HTTP3 (len={len})"); return; }
        string? named = (p.SrcPort < p.DstPort ? p.SrcPort : p.DstPort) switch
        {
            23 => "TELNET", 25 => "SMTP", 49 => "TACACS+", 88 => "KERBEROS", 110 => "POP3", 137 => "NBNS", 138 => "NBDS", 139 => "NETBIOS",
            143 => "IMAP", 161 or 162 => "SNMP", 389 => "LDAP", 445 => "SMB", 500 or 4500 => "ISAKMP", 636 => "LDAPS", 1812 or 1813 => "RADIUS",
            1900 => "SSDP", 3389 => "RDP", 5060 => "SIP", 3306 => "MYSQL", 1433 => "MSSQL", _ => null,
        };
        if (named is not null) { Set(p, named, $"{named} (len={len})"); return; }
        if (Port(p, 1812) || Port(p, 1813)) { Set(p, "RADIUS", "RADIUS"); return; }
        if (Port(p, 1900)) { Set(p, "SSDP", Ascii(d, o, Math.Min(60, len)).Split('.')[0]); }
    }

    private static string DnsType(int t) => t switch
    {
        1 => "A", 2 => "NS", 5 => "CNAME", 6 => "SOA", 12 => "PTR", 15 => "MX", 16 => "TXT", 28 => "AAAA", 33 => "SRV", 65 => "HTTPS", 255 => "ANY",
        _ => $"TYPE{t}",
    };

    private static string RcodeText(int r) => r switch { 0 => "", 1 => "Format error", 2 => "Server failure", 3 => "No such name", 5 => "Refused", _ => $"rcode {r}" };

    /// <summary>A DNS name at offset i, following compression pointers (with a hop limit against loops).</summary>
    private static string DnsName(byte[] d, int start, ref int i, int depth = 0)
    {
        var parts = new List<string>();
        int hops = 0;
        int pos = i;
        bool jumped = false;
        while (pos < d.Length && hops < 32)
        {
            int len = d[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xc0) == 0xc0)
            {
                if (pos + 1 >= d.Length) break;
                int ptr = start + ((len & 0x3f) << 8 | d[pos + 1]);
                if (!jumped) i = pos + 2;
                jumped = true;
                pos = ptr;
                hops++;
                continue;
            }
            if (pos + 1 + len > d.Length) break;
            parts.Add(Encoding.ASCII.GetString(d, pos + 1, len));
            pos += 1 + len;
        }
        if (!jumped) i = pos;
        return parts.Count == 0 ? "<Root>" : string.Join('.', parts);
    }

    private static void Dns(DecodedPacket p, int o, string proto)
    {
        var d = p.Data;
        if (o + 12 > d.Length) { Set(p, proto, "Truncated DNS"); return; }
        int id = U16(d, o), flags = U16(d, o + 2), qd = U16(d, o + 4), an = U16(d, o + 6);
        bool response = (flags & 0x8000) != 0;
        int rcode = flags & 0x0f;
        p.DnsId = id;
        p.DnsResponse = response;
        p.DnsRcode = rcode;
        p.Protocols.Add("dns");
        int i = o + 12;
        string name = "", qtype = "";
        if (qd > 0)
        {
            name = DnsName(d, o, ref i);
            if (i + 4 <= d.Length) { qtype = DnsType(U16(d, i)); i += 4; }
        }
        p.DnsName = name;
        var answers = new List<string>();
        for (int a = 0; a < an && a < 8 && i < d.Length; a++)
        {
            DnsName(d, o, ref i);
            if (i + 10 > d.Length) break;
            int type = U16(d, i), rdlen = U16(d, i + 8);
            int rd = i + 10;
            if (rd + rdlen > d.Length) break;
            if (type == 1 && rdlen == 4) answers.Add("A " + Ip4(d, rd));
            else if (type == 28 && rdlen == 16) answers.Add("AAAA " + Ip6(d, rd));
            else if (type is 5 or 12 or 2) { int j = rd; answers.Add($"{DnsType(type)} {DnsName(d, o, ref j)}"); }
            i = rd + rdlen;
        }
        string err = RcodeText(rcode);
        string info = response
            ? $"Standard query response 0x{id:x4} {qtype} {name}{(err.Length > 0 ? $" {err}" : "")}{(answers.Count > 0 ? " " + string.Join(" ", answers) : "")}"
            : $"Standard query 0x{id:x4} {qtype} {name}";
        Set(p, proto, info);
        Layer(p, "Domain Name System", response ? "response" : "query", o, d.Length - o,
            ("Transaction ID", $"0x{id:x4}"), ("Type", response ? "Response" : "Query"), ("Question", $"{name} {qtype}"),
            ("Answers", answers.Count > 0 ? string.Join(", ", answers) : "-"), ("Reply code", err.Length > 0 ? err : "No error"));
    }

    private static readonly string[] DhcpTypes = ["", "Discover", "Offer", "Request", "Decline", "ACK", "NAK", "Release", "Inform"];

    private static void Dhcp(DecodedPacket p, int o)
    {
        var d = p.Data;
        if (o + 240 > d.Length) { Set(p, "DHCP", "BOOTP"); return; }
        uint xid = U32(d, o + 4);
        string yiaddr = Ip4(d, o + 16), chaddr = Mac(d, o + 28);
        p.DhcpXid = xid;
        var fields = new List<(string, string)> { ("Transaction ID", $"0x{xid:x8}"), ("Client MAC", chaddr), ("Your IP", yiaddr) };
        int i = o + 240;
        string? requested = null, server = null;
        while (i < d.Length && d[i] != 255)
        {
            int opt = d[i];
            if (opt == 0) { i++; continue; }
            if (i + 1 >= d.Length) break;
            int len = d[i + 1];
            int v = i + 2;
            if (v + len > d.Length) break;
            switch (opt)
            {
                case 53 when len >= 1: p.DhcpType = d[v]; break;
                case 50 when len == 4: requested = Ip4(d, v); fields.Add(("Requested IP", requested)); break;
                case 54 when len == 4: server = Ip4(d, v); fields.Add(("DHCP server", server)); break;
                case 12: p.DhcpHostname = Ascii(d, v, len); fields.Add(("Hostname", p.DhcpHostname)); break;
                case 3 when len >= 4: fields.Add(("Router", Ip4(d, v))); break;
                case 6 when len >= 4: fields.Add(("DNS server", Ip4(d, v))); break;
                case 51 when len == 4: fields.Add(("Lease time", $"{U32(d, v)} s")); break;
            }
            i = v + len;
        }
        string type = p.DhcpType is { } t && t < DhcpTypes.Length ? DhcpTypes[t] : "BOOTP";
        string extra = p.DhcpType switch
        {
            2 or 5 => $" - {yiaddr}",
            3 when requested is not null => $" - requesting {requested}",
            _ => "",
        };
        Set(p, "DHCP", $"DHCP {type} - Transaction ID 0x{xid:x8}{extra}{(p.DhcpHostname is { } h ? $" ({h})" : "")}");
        p.Protocols.Add("bootp");
        Layer(p, "Dynamic Host Configuration Protocol", type, o, d.Length - o, fields.ToArray());
    }

    private static void Tls(DecodedPacket p, int o)
    {
        var d = p.Data;
        p.Protocols.Add("tls");
        if (o + 9 > d.Length) { Set(p, "TLS", "Handshake"); return; }
        int hsType = d[o + 5];
        string what = hsType switch { 1 => "Client Hello", 2 => "Server Hello", 11 => "Certificate", 4 => "New Session Ticket", _ => "Handshake" };
        if (hsType == 1) p.TlsSni = Sni(d, o + 9);
        Set(p, "TLS", p.TlsSni is { } sni ? $"{what} (SNI={sni})" : what);
        Layer(p, "Transport Layer Security", what, o, p.PayloadLength, ("Handshake", what), ("Server name (SNI)", p.TlsSni ?? "-"));
    }

    /// <summary>Server Name Indication from a ClientHello body - what site an encrypted connection is going to.</summary>
    private static string? Sni(byte[] d, int i)
    {
        i += 2 + 32;
        if (i >= d.Length) return null;
        i += 1 + d[i];
        if (i + 2 > d.Length) return null;
        i += 2 + U16(d, i);
        if (i >= d.Length) return null;
        i += 1 + d[i];
        if (i + 2 > d.Length) return null;
        int extEnd = Math.Min(d.Length, i + 2 + U16(d, i));
        i += 2;
        while (i + 4 <= extEnd)
        {
            int type = U16(d, i), len = U16(d, i + 2);
            if (type == 0 && i + 9 <= d.Length)
            {
                int nameLen = U16(d, i + 7);
                if (i + 9 + nameLen <= d.Length) return Encoding.ASCII.GetString(d, i + 9, nameLen);
                return null;
            }
            i += 4 + len;
        }
        return null;
    }

    private static readonly string[] HttpMethods = ["GET ", "POST ", "PUT ", "DELETE ", "HEAD ", "OPTIONS ", "PATCH ", "CONNECT ", "HTTP/1."];

    private static bool Http(DecodedPacket p, int o, int len)
    {
        var d = p.Data;
        var head = Encoding.ASCII.GetString(d, o, Math.Min(len, 8));
        if (!HttpMethods.Any(m => head.StartsWith(m, StringComparison.Ordinal))) return false;
        var text = Encoding.ASCII.GetString(d, o, Math.Min(len, 2048));
        var lines = text.Split("\r\n");
        var host = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        p.HttpHost = host?[5..].Trim();
        Set(p, "HTTP", lines[0]);
        Layer(p, "Hypertext Transfer Protocol", lines[0], o, len, ("Request/Status", lines[0]), ("Host", p.HttpHost ?? "-"));
        return true;
    }

    private static readonly string[] Severities = ["EMERG", "ALERT", "CRIT", "ERR", "WARNING", "NOTICE", "INFO", "DEBUG"];

    private static void Syslog(DecodedPacket p, int o, int len)
    {
        var d = p.Data;
        var text = Ascii(d, o, Math.Min(len, 512));
        int close = text.IndexOf('>');
        if (close > 1 && int.TryParse(text[1..close], out var pri))
            Set(p, "Syslog", $"{Severities[pri & 7]}: {text[(close + 1)..].Trim()}");
        else Set(p, "Syslog", text);
    }

    private static void Tftp(DecodedPacket p, int o, int len)
    {
        var d = p.Data;
        int op = U16(d, o);
        string info = op switch
        {
            1 or 2 => $"{(op == 1 ? "Read" : "Write")} Request, File: {Ascii(d, o + 2, len - 2).Split('.')[0]}",
            3 => $"Data Packet, Block: {U16(d, o + 2)}",
            4 => $"Acknowledgement, Block: {U16(d, o + 2)}",
            5 => $"Error: {Ascii(d, o + 4, len - 4).TrimEnd('.')}",
            _ => $"TFTP opcode {op}",
        };
        Set(p, "TFTP", info);
    }

    private static void Hsrp(DecodedPacket p, int o)
    {
        var d = p.Data;
        string state = d[o + 3] switch { 0 => "Initial", 1 => "Learn", 2 => "Listen", 4 => "Speak", 8 => "Standby", 16 => "Active", _ => d[o + 3].ToString() };
        Set(p, "HSRP", $"{(d[o + 2] == 0 ? "Hello" : d[o + 2] == 1 ? "Coup" : "Resign")} (state {state}), group {d[o + 6]}, priority {d[o + 7]}, VIP {Ip4(d, o + 16)}");
    }

    // ---------- 802.11 (monitor mode) ----------

    private static void Radiotap(DecodedPacket p)
    {
        var d = p.Data;
        if (d.Length < 8) { Set(p, "802.11", "Truncated radiotap"); return; }
        int len = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        uint present = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        int i = 8;
        uint word = present;
        while ((word & 0x80000000) != 0 && i + 4 <= len) { word = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i)); i += 4; }
        // Fields in bit order with their alignment and size: TSFT(8,8) Flags(1,1) Rate(1,1) Channel(2,4) FHSS(1,2) dBm signal(1,1)
        (int Align, int Size)[] layout = [(8, 8), (1, 1), (1, 1), (2, 4), (1, 2), (1, 1)];
        for (int bit = 0; bit < layout.Length; bit++)
        {
            if ((present & (1u << bit)) == 0) continue;
            var (align, size) = layout[bit];
            i = (i + align - 1) / align * align;
            if (i + size > len) break;
            if (bit == 5) p.SignalDbm = (sbyte)d[i];
            i += size;
        }
        Ieee80211(p, len);
    }

    private static void Ieee80211(DecodedPacket p, int o)
    {
        var d = p.Data;
        p.Protocols.Add("wlan");
        if (o + 10 > d.Length) { Set(p, "802.11", "Truncated 802.11 frame"); return; }
        int fc = d[o];
        int type = (fc >> 2) & 3, sub = fc >> 4;
        p.DstMac = Mac(d, o + 4);
        if (o + 16 <= d.Length) p.SrcMac = Mac(d, o + 10);
        string name = (type, sub) switch
        {
            (0, 8) => "Beacon", (0, 4) => "Probe Request", (0, 5) => "Probe Response", (0, 0) => "Association Request",
            (0, 1) => "Association Response", (0, 11) => "Authentication", (0, 12) => "Deauthentication", (0, 10) => "Disassociation",
            (0, 13) => "Action", (1, _) => "Control", (2, _) => "Data", _ => $"type {type}/{sub}",
        };
        if (type == 0 && sub is 8 or 5 or 4)
        {
            int ies = o + 24 + (sub == 4 ? 0 : 12);
            if (ies + 2 <= d.Length && d[ies] == 0) p.Ssid = Encoding.UTF8.GetString(d, ies + 2, Math.Min(d[ies + 1], d.Length - ies - 2));
        }
        Set(p, "802.11", $"{name}{(p.Ssid is { } s ? $", SSID=\"{(s.Length == 0 ? "<broadcast>" : s)}\"" : "")}{(p.SignalDbm is { } dbm ? $", {dbm} dBm" : "")}");
    }
}
