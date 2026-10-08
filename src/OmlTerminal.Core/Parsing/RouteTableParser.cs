using System.Text.RegularExpressions;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Parsing;

/// <summary>One next hop for one prefix. ECMP routes produce one entry per next hop.</summary>
public sealed record RouteEntry(string Code, string Prefix, string AdMetric, string NextHop, string Interface, string Age);

/// <summary>Routing tables from Cisco IOS/IOS-XE/ASA and FortiOS (the same "code prefix [AD/metric] via ..." layout),
/// NX-OS ("prefix, ubest/mbest" + "*via" lines), Linux "ip route" and Junos "show route terse".</summary>
public static partial class RouteTableParser
{
    public static IReadOnlyList<RouteEntry> Parse(string text)
    {
        var lines = TextLines.Split(text);
        var ios = ParseIosStyle(lines);
        if (ios.Count > 0) return ios;
        var nxos = ParseNxos(lines);
        if (nxos.Count > 0) return nxos;
        var junos = ParseJunosTerse(lines);
        if (junos.Count > 0) return junos;
        return ParseLinux(lines);
    }

    public static ShowTable? ToTable(string text)
    {
        var routes = Parse(text);
        if (routes.Count == 0) return null;
        return new ShowTable("Routing table", ["Code", "Prefix", "AD/Metric", "Next hop", "Interface", "Age"],
            routes.Select(r => (IReadOnlyList<string>)[r.Code, r.Prefix, r.AdMetric, r.NextHop, r.Interface, r.Age]).ToList());
    }

    private const string Ip = @"\d{1,3}(?:\.\d{1,3}){3}";

    // "O*E2" (candidate default, external) has no space before the suffix; "O IA" / "O E2" do.
    [GeneratedRegex(@"^(?<code>[A-Za-z]{1,2}[*+%&]?(?:\s{0,3}(?:IA|EX|E1|E2|N1|N2|L1|L2|ia|su))?[*+]?)\s+(?<net>" + Ip + @")(?:/(?<len>\d{1,2})|\s+(?<mask>" + Ip + @"))?(?<rest>.*)$")]
    private static partial Regex IosRoute();

    [GeneratedRegex(@"^\s+(?<rest>\[\d+/\d+\].*)$")]
    private static partial Regex IosContinuation();

    [GeneratedRegex(@"^\s*(?<net>" + Ip + @")/(?<len>\d{1,2}) is (?:variably )?subnetted")]
    private static partial Regex IosSubnettedHeader();

    [GeneratedRegex(@"\[(?<ad>\d+/\d+)\]")]
    private static partial Regex AdMetricPattern();

    [GeneratedRegex(@"^(?:\d+[ywdhms])+(?:\d+)?$|^\d{1,2}:\d{2}:\d{2}$|^never$")]
    private static partial Regex AgePattern();

    private static List<RouteEntry> ParseIosStyle(string[] lines)
    {
        var routes = new List<RouteEntry>();
        int? classfulLen = null;
        RouteEntry? pending = null; // prefix seen, details on the next line(s)
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (IosSubnettedHeader().Match(line) is { Success: true } sh) { classfulLen = int.Parse(sh.Groups["len"].Value); continue; }

            if (IosContinuation().Match(line) is { Success: true } cont && (pending is not null || routes.Count > 0))
            {
                var basis = pending ?? routes[^1];
                routes.Add(Details(basis.Code, basis.Prefix, cont.Groups["rest"].Value));
                pending = null;
                continue;
            }

            var m = IosRoute().Match(line);
            if (!m.Success) continue;
            var code = Regex.Replace(m.Groups["code"].Value.Trim(), @"\s+", " ");
            if (code.Equals("via", StringComparison.OrdinalIgnoreCase)) continue;
            int len;
            if (m.Groups["len"].Success) len = int.Parse(m.Groups["len"].Value);
            else if (m.Groups["mask"].Success)
            {
                try { len = Subnet.PrefixFromMask(System.Net.IPAddress.Parse(m.Groups["mask"].Value)); }
                catch (FormatException) { continue; }
            }
            else len = classfulLen ?? 32;
            if (len > 32) continue;
            var prefix = $"{m.Groups["net"].Value}/{len}";
            var rest = m.Groups["rest"].Value.Trim();
            if (rest.Length == 0) { pending = new RouteEntry(code, prefix, "", "", "", ""); continue; }
            pending = null;
            routes.Add(Details(code, prefix, rest));
        }
        return routes;
    }

    private static RouteEntry Details(string code, string prefix, string rest)
    {
        var ad = AdMetricPattern().Match(rest) is { Success: true } a ? a.Groups["ad"].Value : "";
        if (rest.Contains("directly connected", StringComparison.OrdinalIgnoreCase))
        {
            var iface = rest.Split(',').Select(p => p.Trim()).LastOrDefault(p => p.Length > 0 && !p.Contains("directly connected") && !AgePattern().IsMatch(p)) ?? "";
            return new RouteEntry(code, prefix, ad, "connected", iface, "");
        }
        string nextHop = "", ifaceName = "", age = "";
        var parts = rest.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        foreach (var part in parts)
        {
            var viaIdx = part.IndexOf("via ", StringComparison.OrdinalIgnoreCase);
            if (viaIdx >= 0) { nextHop = part[(viaIdx + 4)..].Trim().Split(' ')[0]; continue; }
            if (part.StartsWith('[')) continue;
            if (AgePattern().IsMatch(part)) { age = part; continue; }
            if (char.IsLetter(part[0]) && !part.Contains(' ')) ifaceName = part;
        }
        return new RouteEntry(code, prefix, ad, nextHop, ifaceName, age);
    }

    [GeneratedRegex(@"^(?<net>" + Ip + @"/\d{1,2}), ubest/mbest:")]
    private static partial Regex NxosPrefix();

    [GeneratedRegex(@"^\s+\*?via\s+(?<hop>[^,]+),\s*(?<rest>.*)$")]
    private static partial Regex NxosVia();

    private static List<RouteEntry> ParseNxos(string[] lines)
    {
        var routes = new List<RouteEntry>();
        string? prefix = null;
        foreach (var line in lines)
        {
            if (NxosPrefix().Match(line) is { Success: true } p) { prefix = p.Groups["net"].Value; continue; }
            if (prefix is null || NxosVia().Match(line) is not { Success: true } v) continue;
            var parts = v.Groups["rest"].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            string iface = "", ad = "", age = "";
            var proto = new List<string>();
            foreach (var part in parts)
            {
                if (part.StartsWith('[')) { ad = part.Trim('[', ']'); continue; }
                if (ad.Length == 0 && iface.Length == 0) { iface = part; continue; }
                if (age.Length == 0 && AgePattern().IsMatch(part)) { age = part; continue; }
                if (ad.Length > 0) proto.Add(part);
            }
            var hop = v.Groups["hop"].Value.Trim();
            routes.Add(new RouteEntry(string.Join(" ", proto), prefix, ad, hop, iface, age));
        }
        return routes;
    }

    [GeneratedRegex(@"^\s*A\s+(?:V\s+)?Destination\s+P\s+Prf")]
    private static partial Regex JunosHeader();

    [GeneratedRegex(@"^\s*[*+\-]?\s*(?:[?VIN]\s+)?(?<net>" + Ip + @"/\d{1,2})\s+(?<proto>[A-Z])\s+(?<pref>\d+)\s*(?<rest>.*)$")]
    private static partial Regex JunosRoute();

    private static List<RouteEntry> ParseJunosTerse(string[] lines)
    {
        var routes = new List<RouteEntry>();
        if (!lines.Any(l => JunosHeader().IsMatch(l))) return routes;
        foreach (var line in lines)
        {
            if (JunosRoute().Match(line) is not { Success: true } m) continue;
            var tokens = m.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hop = tokens.FirstOrDefault(t => t.StartsWith('>'))?.TrimStart('>') ?? tokens.LastOrDefault(t => !t.All(char.IsDigit)) ?? "";
            bool hopIsIface = hop.Length > 0 && char.IsLetter(hop[0]);
            routes.Add(new RouteEntry(m.Groups["proto"].Value, m.Groups["net"].Value, m.Groups["pref"].Value,
                hopIsIface ? "" : hop, hopIsIface ? hop : "", ""));
        }
        return routes;
    }

    private static readonly HashSet<string> NeighborStates = ["REACHABLE", "STALE", "DELAY", "PROBE", "FAILED", "INCOMPLETE", "PERMANENT", "NOARP"];

    [GeneratedRegex(@"^(?<net>default|" + Ip + @"(?:/\d{1,2})?)\s+(?<rest>.*)$")]
    private static partial Regex LinuxRoute();

    private static List<RouteEntry> ParseLinux(string[] lines)
    {
        var routes = new List<RouteEntry>();
        foreach (var line in lines)
        {
            if (LinuxRoute().Match(line.Trim()) is not { Success: true } m) continue;
            var t = m.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Contains("lladdr") || (t.Length > 0 && NeighborStates.Contains(t[^1]))) continue; // "ip neigh", not a route
            string Arg(string key) { int i = Array.IndexOf(t, key); return i >= 0 && i + 1 < t.Length ? t[i + 1] : ""; }
            var via = Arg("via");
            var dev = Arg("dev");
            if (via.Length == 0 && dev.Length == 0) continue;
            var net = m.Groups["net"].Value;
            if (net == "default") net = "0.0.0.0/0";
            else if (!net.Contains('/')) net += "/32";
            var proto = Arg("proto");
            routes.Add(new RouteEntry(proto.Length > 0 ? proto : "static", net, Arg("metric"), via.Length > 0 ? via : "connected", dev, ""));
        }
        return routes;
    }
}
