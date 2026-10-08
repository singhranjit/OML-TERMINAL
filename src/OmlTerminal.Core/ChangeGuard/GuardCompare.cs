using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Parsing;

namespace OmlTerminal.Core.ChangeGuard;

public enum GuardSeverity { Critical, Warning, Info }

public sealed record GuardFinding(GuardSeverity Severity, string Check, string Message, IReadOnlyList<string>? Details = null);

/// <summary>Compares a PRE and POST capture check by check and says, in plain words, what changed: interfaces that
/// went down, routes lost or rerouted, BGP/OSPF adjacencies dropped, error counters climbing.</summary>
public static partial class GuardCompare
{
    private const int MaxListed = 25;

    public static IReadOnlyList<GuardFinding> Compare(GuardSnapshot pre, GuardSnapshot post)
    {
        var findings = new List<GuardFinding>();
        if (post.Error is not null) { findings.Add(new(GuardSeverity.Critical, "Device", $"Couldn't connect for the post-check: {post.Error}")); return findings; }
        if (pre.Error is not null) { findings.Add(new(GuardSeverity.Warning, "Device", $"The pre-check never connected ({pre.Error}), so there's nothing to compare against.")); return findings; }

        foreach (var before in pre.Results)
        {
            var after = post.Results.FirstOrDefault(r => r.Command == before.Command);
            if (before.Unsupported || before.Error is not null)
            {
                if (after is { Unsupported: false, Error: null } && before.Error is not null)
                    findings.Add(new(GuardSeverity.Info, before.Title, $"Couldn't capture before ({before.Error}) - no comparison."));
                continue;
            }
            if (after is null || after.Error is not null)
            {
                findings.Add(new(GuardSeverity.Warning, before.Title, $"Couldn't capture after{(after?.Error is { } e ? $" ({e})" : "")} - no comparison."));
                continue;
            }
            var a = pre.Output(before) ?? "";
            var b = post.Output(after) ?? "";
            findings.AddRange(CompareOutputs(before.Title, before.Kind, a, after.Unsupported ? "" : b));
        }
        return findings.OrderBy(f => f.Severity).ToList();
    }

    public static IReadOnlyList<GuardFinding> CompareOutputs(string title, CheckKind kind, string before, string after) => kind switch
    {
        CheckKind.Interfaces => CompareStates(title, InterfaceStates(before), InterfaceStates(after), InterfaceSeverity, "Interface"),
        CheckKind.Routes => CompareRoutes(title, before, after),
        CheckKind.BgpPeers => CompareBgp(title, TableMap(before, "Neighbor", "State/PfxRcd"), TableMap(after, "Neighbor", "State/PfxRcd")),
        CheckKind.OspfNeighbors => CompareStates(title, OspfStates(before), OspfStates(after), OspfSeverity, "OSPF neighbor"),
        CheckKind.InterfaceErrors => CompareErrors(title, ErrorCounters(before), ErrorCounters(after)),
        CheckKind.CdpNeighbors => CompareStates(title, CdpLinks(before), CdpLinks(after), (_, _) => GuardSeverity.Warning, "CDP neighbor"),
        CheckKind.Hsrp => CompareStates(title, HsrpStates(before), HsrpStates(after), (_, _) => GuardSeverity.Warning, "HSRP group"),
        CheckKind.MacCount => CompareCount(title, "MAC entries", ShowTableParser.Parse(before)?.Rows.Count, ShowTableParser.Parse(after)?.Rows.Count),
        CheckKind.ArpCount => CompareCount(title, "ARP entries", ShowTableParser.Parse(before)?.Rows.Count, ShowTableParser.Parse(after)?.Rows.Count),
        _ => CompareRaw(title, before, after),
    };

    // ---------- extractors ----------

    private static readonly string[] StatusColumns = ["Status", "Protocol", "Admin", "Link", "State"];

    /// <summary>Interface → "status/protocol" (IOS), "admin/link" (Junos), "connected" (switchport status), etc.</summary>
    public static Dictionary<string, string> InterfaceStates(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShowTableParser.Parse(output) is not { } t) return map;
        var cols = StatusColumns.Select(t.ColumnIndex).Where(i => i >= 0).ToList();
        if (cols.Count == 0) return map;
        foreach (var r in t.Rows)
            if (r.Count > 0 && r[0].Length > 0)
                map[r[0]] = string.Join("/", cols.Select(i => i < r.Count ? r[i] : "").Where(v => v.Length > 0));
        return map;
    }

    public static bool IsUp(string state)
    {
        var s = state.ToLowerInvariant();
        if (s.Contains("down") || s.Contains("notconnect") || s.Contains("disabled") || s.Contains("err") || s.Contains("absent") || s.Contains("notpresent")) return false;
        return s.Contains("up") || s == "connected";
    }

    private static GuardSeverity InterfaceSeverity(string? before, string? after)
    {
        if (after is not null && after.Contains("err-disabled", StringComparison.OrdinalIgnoreCase)) return GuardSeverity.Critical;
        bool wasUp = before is not null && IsUp(before), isUp = after is not null && IsUp(after);
        if (wasUp && !isUp) return after is null ? GuardSeverity.Warning : GuardSeverity.Critical;
        if (!wasUp && isUp) return GuardSeverity.Info;
        return before is null ? GuardSeverity.Info : GuardSeverity.Warning;
    }

    private static Dictionary<string, string> TableMap(string output, string keyColumn, string valueColumn)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShowTableParser.Parse(output) is not { } t || t.ColumnIndex(keyColumn) < 0) return map;
        foreach (var r in t.Rows) map[t.Cell(r, keyColumn)] = t.Cell(r, valueColumn);
        return map;
    }

    public static Dictionary<string, string> OspfStates(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShowTableParser.Parse(output) is not { } t || t.ColumnIndex("Neighbor ID") < 0) return map;
        foreach (var r in t.Rows) map[$"{t.Cell(r, "Neighbor ID")} on {t.Cell(r, "Interface")}"] = t.Cell(r, "State");
        return map;
    }

    /// <summary>FULL is a working adjacency; 2WAY is normal between two DROTHERs on a shared segment.</summary>
    private static bool OspfHealthy(string? state) =>
        state is not null && (state.StartsWith("FULL", StringComparison.OrdinalIgnoreCase) || state.StartsWith("2WAY", StringComparison.OrdinalIgnoreCase));

    private static GuardSeverity OspfSeverity(string? before, string? after)
    {
        if (OspfHealthy(before) && !OspfHealthy(after)) return GuardSeverity.Critical;
        return GuardSeverity.Info;
    }

    private static Dictionary<string, string> CdpLinks(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShowTableParser.Parse(output) is not { } t || t.ColumnIndex("Local Intrfce") < 0) return map;
        foreach (var r in t.Rows) map[$"{t.Cell(r, "Device ID")} via {t.Cell(r, "Local Intrfce")}"] = t.Cell(r, "Port ID");
        return map;
    }

    private static Dictionary<string, string> HsrpStates(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShowTableParser.Parse(output) is not { } t || t.ColumnIndex("Grp") < 0) return map;
        foreach (var r in t.Rows) map[$"{t.Cell(r, "Interface")} group {t.Cell(r, "Grp")}"] = t.Cell(r, "State");
        return map;
    }

    [GeneratedRegex(@"^(?<if>\S+) is (?:administratively down|up|down|deleted)")]
    private static partial Regex InterfaceHeader();

    [GeneratedRegex(@"(?<in>\d+) input errors, (?<crc>\d+) CRC")]
    private static partial Regex InputErrors();

    [GeneratedRegex(@"(?<out>\d+) output errors")]
    private static partial Regex OutputErrors();

    /// <summary>IOS "show interfaces": per-interface input errors, CRC errors and output errors.</summary>
    public static Dictionary<string, (long In, long Crc, long Out)> ErrorCounters(string output)
    {
        var map = new Dictionary<string, (long In, long Crc, long Out)>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in TextLines.Split(output))
        {
            if (InterfaceHeader().Match(line) is { Success: true } h) { current = h.Groups["if"].Value; map[current] = (0, 0, 0); continue; }
            if (current is null) continue;
            if (InputErrors().Match(line) is { Success: true } i)
                map[current] = (long.Parse(i.Groups["in"].Value), long.Parse(i.Groups["crc"].Value), map[current].Out);
            if (OutputErrors().Match(line) is { Success: true } o)
                map[current] = (map[current].In, map[current].Crc, long.Parse(o.Groups["out"].Value));
        }
        return map;
    }

    /// <summary>Prefix → "code via next-hop(s)". Ages are left out - they change on every read.</summary>
    public static Dictionary<string, string> RouteMap(string output) =>
        RouteTableParser.Parse(output)
            .GroupBy(r => r.Prefix)
            .ToDictionary(g => g.Key,
                g => $"{CleanCode(g.First().Code)} via {string.Join(", ", g.Select(r => r.NextHop == "connected" ? $"connected {r.Interface}".Trim() : r.NextHop).Distinct().Order(StringComparer.Ordinal))}",
                StringComparer.Ordinal);

    /// <summary>Drops the candidate-default marker: "S*" → "S", "O*E2" → "O E2".</summary>
    private static string CleanCode(string code) => Regex.Replace(code.Replace('*', ' '), @"\s+", " ").Trim();

    // ---------- comparisons ----------

    private static IReadOnlyList<GuardFinding> CompareStates(string title, Dictionary<string, string> before, Dictionary<string, string> after,
        Func<string?, string?, GuardSeverity> severity, string noun)
    {
        var findings = new List<GuardFinding>();
        if (before.Count == 0 && after.Count == 0) return findings;
        foreach (var (key, was) in before)
        {
            if (!after.TryGetValue(key, out var now))
                findings.Add(new(severity(was, null) == GuardSeverity.Info ? GuardSeverity.Warning : severity(was, null), title, $"{noun} {key} is gone (was {Show(was)})"));
            else if (!string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                findings.Add(new(severity(was, now), title, $"{noun} {key}: {Show(was)} → {Show(now)}"));
        }
        foreach (var (key, now) in after)
            if (!before.ContainsKey(key))
                findings.Add(new(GuardSeverity.Info, title, $"New {noun.ToLowerInvariant()} {key} ({Show(now)})"));
        if (findings.Count == 0) findings.Add(new(GuardSeverity.Info, title, $"No change ({before.Count} {Plural(noun, before.Count)})"));
        return findings;
    }

    private static string Show(string v) => v.Length == 0 ? "blank" : v;

    private static string Plural(string noun, int n) => n == 1 ? noun.ToLowerInvariant() : noun.ToLowerInvariant() + "s";

    private static IReadOnlyList<GuardFinding> CompareRoutes(string title, string beforeText, string afterText)
    {
        var before = RouteMap(beforeText);
        var after = RouteMap(afterText);
        var findings = new List<GuardFinding>();
        if (before.Count == 0 && after.Count == 0) return findings;

        var removed = before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(PrefixSortKey).ToList();
        var added = after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(PrefixSortKey).ToList();
        var changed = before.Keys.Where(k => after.TryGetValue(k, out var v) && v != before[k]).OrderBy(PrefixSortKey).ToList();

        if (removed.Contains("0.0.0.0/0")) findings.Add(new(GuardSeverity.Critical, title, $"Default route lost (was {before["0.0.0.0/0"]})"));
        if (changed.Contains("0.0.0.0/0")) findings.Add(new(GuardSeverity.Warning, title, $"Default route changed: {before["0.0.0.0/0"]} → {after["0.0.0.0/0"]}"));
        var otherRemoved = removed.Where(k => k != "0.0.0.0/0").ToList();
        var otherChanged = changed.Where(k => k != "0.0.0.0/0").ToList();
        if (otherRemoved.Count > 0)
            findings.Add(new(GuardSeverity.Warning, title, $"{otherRemoved.Count} route(s) lost", Listed(otherRemoved.Select(k => $"{k}  ({before[k]})"))));
        if (otherChanged.Count > 0)
            findings.Add(new(GuardSeverity.Warning, title, $"{otherChanged.Count} route(s) now take a different path", Listed(otherChanged.Select(k => $"{k}  {before[k]}  →  {after[k]}"))));
        if (added.Count > 0)
            findings.Add(new(GuardSeverity.Info, title, $"{added.Count} new route(s)", Listed(added.Select(k => $"{k}  ({after[k]})"))));
        findings.Add(new(GuardSeverity.Info, title, removed.Count + added.Count + changed.Count == 0
            ? $"No change ({before.Count:N0} prefixes)"
            : $"Prefixes: {before.Count:N0} → {after.Count:N0}"));
        return findings;
    }

    private static IReadOnlyList<string> Listed(IEnumerable<string> items)
    {
        var all = items.ToList();
        return all.Count <= MaxListed ? all : [.. all.Take(MaxListed), $"… and {all.Count - MaxListed:N0} more"];
    }

    private static (uint, int) PrefixSortKey(string prefix)
    {
        var parts = prefix.Split('/');
        return System.Net.IPAddress.TryParse(parts[0], out var ip) && ip.GetAddressBytes().Length == 4
            ? (NetTools.Ipv4.ToUInt(ip), parts.Length > 1 && int.TryParse(parts[1], out var l) ? l : 32)
            : (uint.MaxValue, 0);
    }

    /// <summary>Cisco/FortiOS show a prefix count in State/PfxRcd when the session is Established, otherwise the state name.</summary>
    private static IReadOnlyList<GuardFinding> CompareBgp(string title, Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var findings = new List<GuardFinding>();
        if (before.Count == 0 && after.Count == 0) return findings;
        static bool Up(string? s, out long prefixes) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out prefixes);
        static string Describe(string s) => Up(s, out var n) ? $"Established, {n:N0} prefixes" : s;

        foreach (var (peer, was) in before)
        {
            after.TryGetValue(peer, out var now);
            bool wasUp = Up(was, out var p1), isUp = Up(now, out var p2);
            if (now is null)
                findings.Add(new(wasUp ? GuardSeverity.Critical : GuardSeverity.Warning, title, $"BGP neighbor {peer} is gone (was {Describe(was)})"));
            else if (wasUp && !isUp)
                findings.Add(new(GuardSeverity.Critical, title, $"BGP neighbor {peer} went down: Established → {now}"));
            else if (!wasUp && isUp)
                findings.Add(new(GuardSeverity.Info, title, $"BGP neighbor {peer} came up ({p2:N0} prefixes)"));
            else if (wasUp && isUp && p1 != p2)
            {
                bool bigDrop = p2 < p1 && p1 - p2 >= 5 && p2 < p1 * 0.8;
                findings.Add(new(bigDrop ? GuardSeverity.Warning : GuardSeverity.Info, title, $"BGP neighbor {peer}: prefixes {p1:N0} → {p2:N0}"));
            }
            else if (!wasUp && !isUp && was != now)
                findings.Add(new(GuardSeverity.Info, title, $"BGP neighbor {peer}: {was} → {now}"));
        }
        foreach (var (peer, now) in after)
            if (!before.ContainsKey(peer)) findings.Add(new(GuardSeverity.Info, title, $"New BGP neighbor {peer} ({Describe(now)})"));
        if (findings.Count == 0)
            findings.Add(new(GuardSeverity.Info, title, $"No change ({before.Count} neighbor(s), {before.Values.Count(v => Up(v, out _))} established)"));
        return findings;
    }

    private static IReadOnlyList<GuardFinding> CompareErrors(string title, Dictionary<string, (long In, long Crc, long Out)> before, Dictionary<string, (long In, long Crc, long Out)> after)
    {
        var findings = new List<GuardFinding>();
        if (before.Count == 0 && after.Count == 0) return findings;
        var rising = new List<string>();
        foreach (var (iface, b) in before)
        {
            if (!after.TryGetValue(iface, out var a)) continue;
            var parts = new List<string>();
            // A counter that went down was cleared ("clear counters") - not new errors.
            if (a.Crc > b.Crc) parts.Add($"CRC +{a.Crc - b.Crc:N0}");
            if (a.In > b.In) parts.Add($"input errors +{a.In - b.In:N0}");
            if (a.Out > b.Out) parts.Add($"output errors +{a.Out - b.Out:N0}");
            if (parts.Count > 0) rising.Add($"{iface}: {string.Join(", ", parts)}");
        }
        findings.Add(rising.Count > 0
            ? new(GuardSeverity.Warning, title, $"Errors increasing on {rising.Count} interface(s)", Listed(rising))
            : new(GuardSeverity.Info, title, $"No new interface errors ({before.Count} interfaces)"));
        return findings;
    }

    private static IReadOnlyList<GuardFinding> CompareCount(string title, string noun, int? before, int? after)
    {
        if (before is null && after is null) return [];
        int b = before ?? 0, a = after ?? 0;
        bool shrank = a < b && b - a >= 10 && a < b * 0.8;
        var change = b == 0 ? "" : $" ({(a - b) * 100.0 / b:+0;-0}%)";
        return [new(shrank ? GuardSeverity.Warning : GuardSeverity.Info, title, a == b ? $"No change ({b:N0} {noun})" : $"{noun}: {b:N0} → {a:N0}{change}")];
    }

    private static IReadOnlyList<GuardFinding> CompareRaw(string title, string before, string after)
    {
        var (added, removed) = ConfigBackup.Diff(before, after);
        if (added.Count + removed.Count == 0) return [new(GuardSeverity.Info, title, "No change")];
        var details = removed.Select(l => "- " + l.Trim()).Concat(added.Select(l => "+ " + l.Trim()));
        return [new(GuardSeverity.Info, title, $"Output changed: +{added.Count} / -{removed.Count} lines", Listed(details))];
    }

    // ---------- report ----------

    public static string Report(string changeName, IEnumerable<(string Device, IReadOnlyList<GuardFinding> Findings)> devices)
    {
        var list = devices.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Change Guard report - {changeName}");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        int crit = list.Sum(d => d.Findings.Count(f => f.Severity == GuardSeverity.Critical));
        int warn = list.Sum(d => d.Findings.Count(f => f.Severity == GuardSeverity.Warning));
        sb.AppendLine($"{list.Count} device(s) · {crit} critical · {warn} warning(s)");
        foreach (var (device, findings) in list)
        {
            sb.AppendLine();
            sb.AppendLine($"=== {device} ===");
            foreach (var f in findings)
            {
                var tag = f.Severity switch { GuardSeverity.Critical => "[CRITICAL]", GuardSeverity.Warning => "[WARNING] ", _ => "[info]    " };
                sb.AppendLine($"{tag} {f.Check}: {f.Message}");
                if (f.Details is { } d) foreach (var line in d) sb.AppendLine($"             {line}");
            }
        }
        return sb.ToString();
    }
}
