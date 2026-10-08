using System.Net;
using System.Text.RegularExpressions;
using OmlTerminal.Core.NetTools;
using OmlTerminal.Core.Parsing;

namespace OmlTerminal.Core.PathTrace;

public sealed record NextHop(string Address, string Interface);

/// <summary>The forwarding decision one device makes for a destination: the matching prefix, how it was learned,
/// and every equal-cost next hop. <see cref="Connected"/> means the destination is on a directly attached network.</summary>
public sealed record RouteDecision(string Prefix, string Protocol, string Distance, IReadOnlyList<NextHop> Paths, bool Connected, bool Local)
{
    public string Text => Connected ? $"{Prefix} connected" + (Paths.Count > 0 ? $" on {Paths[0].Interface}" : "")
        : $"{Prefix} via {Protocol}{(Distance.Length > 0 ? $" [{Distance}]" : "")}";
}

public sealed record InterfaceHealth(string Name, string Status, string Protocol, string Description, long BandwidthKbps,
    long InBps, long OutBps, long InputErrors, long Crc, long OutputErrors, long InputDrops, long OutputDrops, long Resets)
{
    public bool Up => Status.Equals("up", StringComparison.OrdinalIgnoreCase) && Protocol.StartsWith("up", StringComparison.OrdinalIgnoreCase);
    public double InUtil => BandwidthKbps > 0 ? 100.0 * InBps / (BandwidthKbps * 1000.0) : double.NaN;
    public double OutUtil => BandwidthKbps > 0 ? 100.0 * OutBps / (BandwidthKbps * 1000.0) : double.NaN;
    public string SpeedText => BandwidthKbps >= 1_000_000 ? $"{BandwidthKbps / 1_000_000.0:0.#}G" : BandwidthKbps >= 1000 ? $"{BandwidthKbps / 1000.0:0.#}M" : $"{BandwidthKbps}K";
}

public sealed record ArpEntry(string Ip, string Mac, string Interface);

public sealed record MacEntry(string Vlan, string Mac, string Port);

public static partial class DeviceOutputParsers
{
    private const string Ip = @"\d{1,3}(?:\.\d{1,3}){3}";

    // ---------- route lookups ----------

    [GeneratedRegex(@"^Routing entry for (?<p>" + Ip + @"/\d{1,2})", RegexOptions.Multiline)]
    private static partial Regex IosEntry();

    [GeneratedRegex(@"Known via ""(?<proto>[^""]+)"", distance (?<ad>\d+), metric (?<metric>\d+)")]
    private static partial Regex IosKnownVia();

    [GeneratedRegex(@"^\s*\*?\s*(?<hop>" + Ip + @"|directly connected)(?:, from [^,]+)?(?:, [^,]*ago)?, via (?<if>\S+)", RegexOptions.Multiline)]
    private static partial Regex IosBlock();

    /// <summary>Cisco IOS/IOS-XE "show ip route &lt;ip&gt;" (Routing entry for … / Routing Descriptor Blocks). Null when the
    /// output isn't that format, or says "not in table".</summary>
    public static RouteDecision? ParseIosRouteEntry(string text)
    {
        var e = IosEntry().Match(text);
        if (!e.Success) return null;
        var known = IosKnownVia().Match(text);
        var proto = known.Success ? known.Groups["proto"].Value : "";
        var paths = IosBlock().Matches(text).Select(m => new NextHop(m.Groups["hop"].Value == "directly connected" ? "" : m.Groups["hop"].Value,
            m.Groups["if"].Value.TrimEnd(','))).Distinct().ToList();
        bool connected = proto.StartsWith("connected", StringComparison.OrdinalIgnoreCase) || paths.Any(p => p.Address.Length == 0);
        bool local = e.Groups["p"].Value.EndsWith("/32") && connected;
        // A recursive route (BGP, static to an address) lists its next hop with no "via interface".
        if (paths.Count == 0)
            foreach (Match m in Regex.Matches(text, @"^\s*\*?\s*(?<hop>" + Ip + @"), from ", RegexOptions.Multiline))
                paths.Add(new NextHop(m.Groups["hop"].Value, ""));
        return new RouteDecision(e.Groups["p"].Value, proto, known.Success ? $"{known.Groups["ad"].Value}/{known.Groups["metric"].Value}" : "",
            paths, connected, local);
    }

    public static bool NotInTable(string text) =>
        text.Contains("not in table", StringComparison.OrdinalIgnoreCase) || text.Contains("Route not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>Longest-prefix match of <paramref name="destination"/> against any routing table <see cref="RouteTableParser"/>
    /// reads (IOS full table, NX-OS, Junos terse, Linux) - also how NX-OS/Junos single-route output is read.</summary>
    public static RouteDecision? LongestMatch(string table, IPAddress destination)
    {
        var routes = RouteTableParser.Parse(table);
        RouteEntry? best = null;
        int bestLen = -1;
        foreach (var r in routes)
        {
            if (!Subnet.TryParse(r.Prefix, out var net) || net.IsV6 || !net.Contains(destination)) continue;
            if (net.PrefixLength > bestLen) { best = r; bestLen = net.PrefixLength; }
        }
        if (best is null) return null;
        var same = routes.Where(r => r.Prefix == best.Prefix && r.Code == best.Code).ToList();
        bool connected = same.Any(r => r.NextHop == "connected") || best.Code.StartsWith('C') || best.Code.StartsWith('L')
            || best.Code.Contains("direct", StringComparison.OrdinalIgnoreCase) || best.Code.Contains("local", StringComparison.OrdinalIgnoreCase)
            || best.Code is "D" && same.All(r => r.NextHop.Length == 0); // Junos "D" = Direct
        bool local = bestLen == 32 && (best.Code.StartsWith('L') || best.Code.Contains("local", StringComparison.OrdinalIgnoreCase));
        var paths = same.Select(r => new NextHop(r.NextHop is "connected" ? "" : r.NextHop, r.Interface)).Distinct().ToList();
        return new RouteDecision(best.Prefix, ProtocolName(best.Code), best.AdMetric, paths, connected, local);
    }

    private static string ProtocolName(string code) => code.Split(' ')[0].TrimEnd('*', '+', '%') switch
    {
        "O" => "ospf", "D" => "eigrp", "B" => "bgp", "S" => "static", "R" => "rip", "i" => "isis", "C" => "connected", "L" => "local",
        var other => other.Length == 0 ? "?" : other.ToLowerInvariant(),
    };

    // ---------- show interfaces ----------

    [GeneratedRegex(@"^(?<name>\S+) is (?<st>administratively down|up|down|deleted)(?:\s*\([^)]*\))?, line protocol is (?<pr>\S+)", RegexOptions.Multiline)]
    private static partial Regex IfHeader();

    [GeneratedRegex(@"^(?<name>(?:Ethernet|Eth|mgmt|Vlan|port-channel|loopback|Po)\S*) is (?<st>up|down|administratively down)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex NxosHeader();

    [GeneratedRegex(@"BW (?<bw>\d+) Kbit")]
    private static partial Regex Bandwidth();

    [GeneratedRegex(@"(?:\d+ (?:minute|second)s?|\d+ seconds) input rate (?<v>\d+) bits/sec", RegexOptions.IgnoreCase)]
    private static partial Regex InRate();

    [GeneratedRegex(@"(?:\d+ (?:minute|second)s?|\d+ seconds) output rate (?<v>\d+) bits/sec", RegexOptions.IgnoreCase)]
    private static partial Regex OutRate();

    private static long Num(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>Cisco IOS/IOS-XE/NX-OS "show interfaces X" - status, load, errors and drops.</summary>
    public static InterfaceHealth? ParseInterface(string text)
    {
        var h = IfHeader().Match(text);
        string name, st, pr;
        if (h.Success) { name = h.Groups["name"].Value; st = h.Groups["st"].Value; pr = h.Groups["pr"].Value; }
        else if (NxosHeader().Match(text) is { Success: true } n) { name = n.Groups["name"].Value; st = n.Groups["st"].Value; pr = st; }
        else return null;
        // Only the first interface's block, in case the device printed more.
        int next = text.IndexOf('\n', h.Success ? h.Index : 0);
        var rest = next < 0 ? "" : text[next..];
        var nextHeader = IfHeader().Match(rest);
        if (nextHeader.Success) rest = rest[..nextHeader.Index];
        var desc = Regex.Match(rest, @"^\s*Description: (.*)$", RegexOptions.Multiline);
        return new InterfaceHealth(name, st, pr, desc.Success ? desc.Groups[1].Value.Trim() : "",
            Bandwidth().Match(rest) is { Success: true } bw ? long.Parse(bw.Groups["bw"].Value) : 0,
            InRate().Match(rest) is { Success: true } i ? long.Parse(i.Groups["v"].Value) : 0,
            OutRate().Match(rest) is { Success: true } o ? long.Parse(o.Groups["v"].Value) : 0,
            Num(rest, @"(\d+) input errors?"), Num(rest, @"(\d+) CRC"), Num(rest, @"(\d+) output errors?"),
            Num(rest, @"Input queue: \d+/\d+/(\d+)/") + Num(rest, @"(\d+) input discard"),
            Num(rest, @"Total output drops: (\d+)") + Num(rest, @"(\d+) output discard"),
            Num(rest, @"(\d+) interface resets"));
    }

    // ---------- ARP and MAC tables ----------

    [GeneratedRegex(@"^Internet\s+(?<ip>" + Ip + @")\s+\S+\s+(?<mac>[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4})\s+\S+\s+(?<if>\S+)", RegexOptions.Multiline)]
    private static partial Regex IosArp();

    [GeneratedRegex(@"^(?<ip>" + Ip + @")\s+\S+\s+(?<mac>[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4})\s+(?<if>\S+)", RegexOptions.Multiline)]
    private static partial Regex NxosArp();

    public static ArpEntry? ParseArp(string text, string ip)
    {
        foreach (Match m in IosArp().Matches(text))
            if (m.Groups["ip"].Value == ip) return new ArpEntry(ip, m.Groups["mac"].Value.ToLowerInvariant(), m.Groups["if"].Value);
        foreach (Match m in NxosArp().Matches(text))
            if (m.Groups["ip"].Value == ip) return new ArpEntry(ip, m.Groups["mac"].Value.ToLowerInvariant(), m.Groups["if"].Value);
        return null;
    }

    [GeneratedRegex(@"^\*?\s*(?<vlan>\d+)\s+(?<mac>[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4})\s+\S+(?:\s+\S+){0,3}?\s+(?<port>(?:Gi|Te|Fa|Fo|Hu|Twe|Eth|Po|Port-channel|GigabitEthernet|TenGigabitEthernet|FastEthernet)\S*)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex MacLine();

    public static MacEntry? ParseMac(string text, string mac)
    {
        var want = mac.ToLowerInvariant();
        foreach (Match m in MacLine().Matches(text))
            if (m.Groups["mac"].Value.ToLowerInvariant() == want) return new MacEntry(m.Groups["vlan"].Value, want, m.Groups["port"].Value);
        return null;
    }

    /// <summary>"Vlan20" → true: the ARP entry points at an SVI, so the host is somewhere in that VLAN's L2 domain.</summary>
    public static bool IsSvi(string iface) => iface.StartsWith("Vlan", StringComparison.OrdinalIgnoreCase) || iface.StartsWith("BVI", StringComparison.OrdinalIgnoreCase)
        || iface.StartsWith("irb", StringComparison.OrdinalIgnoreCase);
}
