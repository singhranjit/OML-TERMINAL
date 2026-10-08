using System.Text;

namespace OmlTerminal.Core.Capture;

public sealed class Conversation
{
    public required string A { get; init; }
    public required string B { get; init; }
    public string Protocol { get; init; } = "";
    public int PortA { get; init; }
    public int PortB { get; init; }
    public long PacketsAtoB { get; set; }
    public long PacketsBtoA { get; set; }
    public long BytesAtoB { get; set; }
    public long BytesBtoA { get; set; }
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public long Packets => PacketsAtoB + PacketsBtoA;
    public long Bytes => BytesAtoB + BytesBtoA;
    public TimeSpan Duration => Last - First;
}

public enum InsightSeverity { Problem, Warning, Info }

public sealed record CaptureInsight(InsightSeverity Severity, string Title, string Detail, string Filter);

/// <summary>Statistics and "what's wrong here" analysis over a set of decoded packets.</summary>
public static class TrafficAnalysis
{
    /// <summary>IP-to-IP conversations (or MAC-to-MAC for non-IP traffic), busiest first.</summary>
    public static List<Conversation> Conversations(IEnumerable<DecodedPacket> packets, bool byPort = false)
    {
        var map = new Dictionary<string, Conversation>();
        foreach (var p in packets)
        {
            string? s = p.SrcIp ?? p.SrcMac, d = p.DstIp ?? p.DstMac;
            if (s is null || d is null) continue;
            int sp = byPort ? p.SrcPort ?? 0 : 0, dp = byPort ? p.DstPort ?? 0 : 0;
            if (byPort && p.SrcPort is null) continue;
            bool forward = string.CompareOrdinal(s, d) < 0 || s == d && sp <= dp;
            string a = forward ? s : d, b = forward ? d : s;
            int pa = forward ? sp : dp, pb = forward ? dp : sp;
            string proto = byPort ? (p.IpProtocol == 6 ? "TCP" : p.IpProtocol == 17 ? "UDP" : "") : "";
            string key = $"{a}|{pa}|{b}|{pb}|{proto}";
            if (!map.TryGetValue(key, out var c))
            {
                c = new Conversation { A = a, B = b, PortA = pa, PortB = pb, Protocol = proto, First = p.Timestamp };
                map[key] = c;
            }
            if (forward) { c.PacketsAtoB++; c.BytesAtoB += p.OriginalLength; }
            else { c.PacketsBtoA++; c.BytesBtoA += p.OriginalLength; }
            if (p.Timestamp < c.First) c.First = p.Timestamp;
            if (p.Timestamp > c.Last) c.Last = p.Timestamp;
        }
        return map.Values.OrderByDescending(c => c.Bytes).ToList();
    }

    public static List<(string Protocol, long Packets, long Bytes)> ProtocolBreakdown(IEnumerable<DecodedPacket> packets) =>
        packets.GroupBy(p => p.Protocol)
            .Select(g => (g.Key, (long)g.Count(), g.Sum(p => (long)p.OriginalLength)))
            .OrderByDescending(x => x.Item2).ToList();

    /// <summary>The TCP conversation a packet belongs to, as both directions' payload in sequence order - duplicate and
    /// retransmitted segments are skipped. Bytes outside printable ASCII show as '.'.</summary>
    public static string FollowTcpStream(IReadOnlyList<DecodedPacket> packets, DecodedPacket seed, int maxBytes = 2_000_000)
    {
        if (seed.IpProtocol != 6 || seed.SrcIp is null) return "Not a TCP packet.";
        string a = $"{seed.SrcIp}:{seed.SrcPort}", b = $"{seed.DstIp}:{seed.DstPort}";
        var segments = packets.Where(p => p.IpProtocol == 6 && p.PayloadLength > 0 &&
                ($"{p.SrcIp}:{p.SrcPort}" == a && $"{p.DstIp}:{p.DstPort}" == b || $"{p.SrcIp}:{p.SrcPort}" == b && $"{p.DstIp}:{p.DstPort}" == a))
            .ToList();
        var nextSeq = new Dictionary<string, uint>();
        var sb = new StringBuilder();
        string? lastDir = null;
        foreach (var p in segments)
        {
            string dir = $"{p.SrcIp}:{p.SrcPort}";
            if (nextSeq.TryGetValue(dir, out var expected))
            {
                int delta = unchecked((int)(p.Seq - expected));
                if (delta < 0 && delta + p.PayloadLength <= 0) continue; // fully a retransmission
            }
            nextSeq[dir] = unchecked(p.Seq + (uint)p.PayloadLength);
            if (dir != lastDir) { sb.Append($"\n──── {dir} → {(dir == a ? b : a)} ────\n"); lastDir = dir; }
            for (int i = 0; i < p.PayloadLength && p.PayloadOffset + i < p.Data.Length; i++)
            {
                byte c = p.Data[p.PayloadOffset + i];
                if (c is >= 32 and < 127 or (byte)'\n' or (byte)'\t') sb.Append((char)c);
                else if (c != '\r') sb.Append('.');
            }
            if (sb.Length > maxBytes) { sb.Append("\n… (truncated)"); break; }
        }
        return sb.Length == 0 ? "No payload in this TCP conversation." : sb.ToString();
    }

    /// <summary>Problems a network engineer would want pointed out, each with a display filter that shows the evidence.</summary>
    public static List<CaptureInsight> Insights(IReadOnlyList<DecodedPacket> packets)
    {
        var list = new List<CaptureInsight>();
        if (packets.Count == 0) return list;

        // Duplicate IP: the same address claimed by more than one MAC in ARP.
        foreach (var g in packets.Where(p => p.ArpSenderIp is { } ip && ip != "0.0.0.0" && p.ArpSenderMac is not null)
                     .GroupBy(p => p.ArpSenderIp!))
        {
            var macs = g.Select(p => p.ArpSenderMac!).Distinct().ToList();
            if (macs.Count > 1)
                list.Add(new(InsightSeverity.Problem, $"Duplicate IP address {g.Key}", $"Claimed by {string.Join(", ", macs)} - an IP conflict.", $"arp && ip.addr == {g.Key}"));
        }

        // TCP: connection attempts that were refused or never answered.
        var tcp = packets.Where(p => p.IpProtocol == 6 && p.SrcIp is not null).ToList();
        var synAcked = tcp.Where(p => p.Flags.HasFlag(TcpFlags.Syn) && p.Flags.HasFlag(TcpFlags.Ack))
            .Select(p => $"{p.DstIp}:{p.DstPort}>{p.SrcIp}:{p.SrcPort}").ToHashSet();
        var resetBack = tcp.Where(p => p.Flags.HasFlag(TcpFlags.Rst)).Select(p => $"{p.DstIp}:{p.DstPort}>{p.SrcIp}:{p.SrcPort}").ToHashSet();
        var syns = tcp.Where(p => p.Flags == TcpFlags.Syn).ToList();
        foreach (var g in syns.GroupBy(p => $"{p.DstIp}:{p.DstPort}"))
        {
            var keys = g.Select(p => $"{p.SrcIp}:{p.SrcPort}>{p.DstIp}:{p.DstPort}").Distinct().ToList();
            int refused = keys.Count(k => resetBack.Contains(k) && !synAcked.Contains(k));
            int silent = keys.Count(k => !resetBack.Contains(k) && !synAcked.Contains(k));
            var dst = g.First();
            if (refused > 0)
                list.Add(new(InsightSeverity.Problem, $"Connections refused by {g.Key}", $"{refused} attempt(s) got a TCP reset - nothing is listening on port {dst.DstPort}, or a firewall rejects it.", $"ip.addr == {dst.DstIp} && tcp.port == {dst.DstPort} && (tcp.flags.syn || tcp.flags.reset)"));
            if (silent > 0 && g.Count() > 1)
                list.Add(new(InsightSeverity.Problem, $"No answer from {g.Key}", $"{silent} connection attempt(s) were never answered - the host is down, unroutable, or a firewall drops the traffic.", $"ip.addr == {dst.DstIp} && tcp.port == {dst.DstPort}"));
        }

        // Retransmissions: same direction, same sequence number, payload seen again.
        var seen = new HashSet<string>();
        var retrans = new Dictionary<string, int>();
        // 0-1 byte segments repeating a sequence number are keep-alives, sent again on purpose - not loss.
        foreach (var p in tcp.Where(p => p.PayloadLength > 1))
        {
            string flow = $"{p.SrcIp}:{p.SrcPort} → {p.DstIp}:{p.DstPort}";
            if (!seen.Add($"{flow}|{p.Seq}|{p.PayloadLength}")) retrans[flow] = retrans.GetValueOrDefault(flow) + 1;
        }
        // An odd retransmission is normal TCP; repeated ones on a flow point at loss.
        foreach (var (flow, n) in retrans.Where(x => x.Value >= 3).OrderByDescending(x => x.Value).Take(5))
        {
            var parts = flow.Split(" → ");
            var src = parts[0].Split(':');
            list.Add(new(n > 10 ? InsightSeverity.Problem : InsightSeverity.Warning, $"TCP retransmissions: {flow}",
                $"{n} segment(s) sent more than once - packet loss or congestion on the path.", $"ip.src == {string.Join(':', src[..^1])} && tcp.srcport == {src[^1]}"));
        }

        int zeroWin = tcp.Count(p => p.Window == 0 && !p.Flags.HasFlag(TcpFlags.Rst) && !p.Flags.HasFlag(TcpFlags.Syn));
        if (zeroWin > 0)
            list.Add(new(InsightSeverity.Warning, "TCP zero window", $"{zeroWin} packet(s) advertised a zero receive window - a receiver can't keep up (slow app or host).", "tcp.window_size == 0 && !tcp.flags.reset"));

        // DNS: failures and slow answers.
        var dns = packets.Where(p => p.DnsId is not null).ToList();
        foreach (var g in dns.Where(p => p.DnsResponse && p.DnsRcode != 0).GroupBy(p => (p.DnsName, p.DnsRcode)).Take(10))
        {
            string why = g.Key.DnsRcode switch { 3 => "doesn't exist (NXDOMAIN)", 2 => "server failure (SERVFAIL)", 5 => "refused", _ => $"error {g.Key.DnsRcode}" };
            list.Add(new(g.Key.DnsRcode == 3 ? InsightSeverity.Warning : InsightSeverity.Problem, $"DNS: {g.Key.DnsName} {why}",
                $"{g.Count()} failed response(s) from {g.First().SrcIp}.", $"dns.qry.name == \"{g.Key.DnsName}\""));
        }
        // Multicast name resolution (mDNS, LLMNR) has no one-to-one replies; only unicast DNS can be "unanswered" or "slow".
        var unicastDns = dns.Where(p => p.Protocol == "DNS").ToList();
        var queries = unicastDns.Where(p => !p.DnsResponse).GroupBy(p => (p.DnsId, p.SrcIp, p.SrcPort)).ToDictionary(g => g.Key, g => g.First());
        var slow = new List<(DecodedPacket Q, double Ms)>();
        var answered = new HashSet<(int?, string?, int?)>();
        foreach (var r in unicastDns.Where(p => p.DnsResponse))
        {
            var key = (r.DnsId, r.DstIp, r.DstPort);
            answered.Add(key);
            if (queries.TryGetValue(key, out var q))
            {
                double ms = (r.Timestamp - q.Timestamp).TotalMilliseconds;
                if (ms > 500) slow.Add((q, ms));
            }
        }
        if (slow.Count > 0)
            list.Add(new(InsightSeverity.Warning, "Slow DNS responses", $"{slow.Count} lookup(s) took over 500 ms (slowest {slow.Max(s => s.Ms):N0} ms for {slow.MaxBy(s => s.Ms).Q.DnsName}).", "dns"));
        var unanswered = queries.Where(q => !answered.Contains(q.Key) && q.Value.Timestamp < packets[^1].Timestamp.AddSeconds(-2)).ToList();
        if (unanswered.Count > 0)
            list.Add(new(InsightSeverity.Problem, "DNS queries with no answer", $"{unanswered.Count} lookup(s) never got a response - is the DNS server ({unanswered[0].Value.DstIp}) reachable?", $"dns && ip.dst == {unanswered[0].Value.DstIp}"));

        // DHCP.
        var dhcp = packets.Where(p => p.DhcpType is not null).ToList();
        var offered = dhcp.Where(p => p.DhcpType == 2).Select(p => p.DhcpXid).ToHashSet();
        var discovers = dhcp.Where(p => p.DhcpType == 1).GroupBy(p => p.DhcpXid).Where(g => !offered.Contains(g.Key)).ToList();
        if (discovers.Count > 0)
            list.Add(new(InsightSeverity.Problem, "DHCP Discover with no Offer", $"{discovers.Count} client request(s) got no offer - check the DHCP server, scope and ip helper-address.", "dhcp.option.dhcp == 1"));
        int naks = dhcp.Count(p => p.DhcpType == 6);
        if (naks > 0)
            list.Add(new(InsightSeverity.Warning, "DHCP NAK", $"{naks} request(s) were refused by the DHCP server (wrong subnet or expired lease).", "dhcp.option.dhcp == 6"));

        // ICMP errors.
        foreach (var g in packets.Where(p => p.IcmpType is 3 or 11 && p.IpProtocol == 1).GroupBy(p => (p.IcmpType, p.IcmpCode, p.SrcIp))
                     .OrderByDescending(g => g.Count()).Take(6))
        {
            string kind = g.Key.IcmpType == 11 ? "TTL exceeded" : g.Key.IcmpCode switch
            {
                0 => "Network unreachable", 1 => "Host unreachable", 3 => "Port unreachable", 4 => "Fragmentation needed",
                9 or 10 or 13 => "Administratively prohibited", _ => $"Unreachable (code {g.Key.IcmpCode})",
            };
            var about = g.Select(p => p.Info.Contains(" for ") ? p.Info[(p.Info.IndexOf(" for ") + 5)..] : "").Where(s => s.Length > 0).Distinct().ToList();
            list.Add(new(g.Key.IcmpCode is 9 or 10 or 13 ? InsightSeverity.Problem : InsightSeverity.Warning, $"ICMP {kind} from {g.Key.SrcIp}",
                $"{g.Count()} message(s){(about.Count > 0 ? $" about {string.Join(", ", about.Take(3))}{(about.Count > 3 ? $" and {about.Count - 3} more" : "")}" : "")}.",
                $"ip.src == {g.Key.SrcIp} && icmp.type == {g.Key.IcmpType}"));
        }

        // Layer 2.
        int tc = packets.Count(p => p.StpTopologyChange);
        if (tc > 0)
            list.Add(new(InsightSeverity.Warning, "Spanning-tree topology changes", $"{tc} BPDU(s) flagged a topology change - MAC tables flush and traffic floods each time.", "stp.tc"));
        int bcast = packets.Count(p => p.IsBroadcast);
        double secs = Math.Max(1, (packets[^1].Timestamp - packets[0].Timestamp).TotalSeconds);
        if (bcast / secs > 200)
            list.Add(new(InsightSeverity.Problem, "Broadcast storm", $"{bcast / secs:N0} broadcasts per second - look for a loop or a misbehaving host.", "broadcast"));
        else if (packets.Count > 100 && bcast * 100.0 / packets.Count > 30)
            list.Add(new(InsightSeverity.Warning, "High broadcast share", $"{bcast * 100.0 / packets.Count:N0}% of frames are broadcasts.", "broadcast"));
        int malformed = packets.Count(p => p.Malformed);
        if (malformed > 0)
            list.Add(new(InsightSeverity.Info, "Malformed or truncated packets", $"{malformed} frame(s) couldn't be fully decoded (often just a small snap length).", "malformed"));

        if (list.Count == 0)
            list.Add(new(InsightSeverity.Info, "Nothing suspicious found", $"Checked {packets.Count:N0} packets for IP conflicts, refused/unanswered connections, retransmissions, DNS/DHCP failures, ICMP errors, STP changes and broadcast storms.", ""));
        return list.OrderBy(i => i.Severity).ToList();
    }
}
