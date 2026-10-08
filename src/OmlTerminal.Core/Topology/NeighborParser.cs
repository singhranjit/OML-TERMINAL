using System.Text.RegularExpressions;
using OmlTerminal.Core.Parsing;

namespace OmlTerminal.Core.Topology;

public enum NodeKind { Router, Switch, Firewall, AccessPoint, Phone, Host, Unknown }

/// <param name="MgmtIp">Management address if advertised, else the neighbor's interface address, else null.</param>
public sealed record NeighborInfo(string Name, string? MgmtIp, string LocalInterface, string RemoteInterface, string Platform, string Capabilities, string Protocol);

/// <summary>Parses "show cdp neighbors detail" and "show lldp neighbors detail" (IOS, IOS-XE, NX-OS) and Junos
/// "show lldp neighbors" into neighbor records.</summary>
public static partial class NeighborParser
{
    [GeneratedRegex(@"^\s*Device ID\s*:\s*(?<v>\S+)", RegexOptions.Multiline)]
    private static partial Regex CdpDeviceId();

    [GeneratedRegex(@"^\s*System Name\s*:\s*(?<v>\S+)", RegexOptions.Multiline)]
    private static partial Regex SystemName();

    [GeneratedRegex(@"^\s*Platform\s*:\s*(?:cisco\s+)?(?<p>[^,]+?)\s*,\s*Capabilities\s*:\s*(?<c>.*?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CdpPlatform();

    [GeneratedRegex(@"^\s*Interface\s*:\s*(?<l>[^,]+?)\s*,\s*Port ID \(outgoing port\)\s*:\s*(?<r>\S.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex CdpInterfaces();

    [GeneratedRegex(@"(?:IP address|IPv4 Address|IP)\s*:\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex AddressLine();

    [GeneratedRegex(@"(?:Management address\(es\)|Mgmt address\(es\)|Management Address(?:es)?)\s*:?(?<rest>[^\n]*)\n?(?<next>(?:[^\n]*\n?){0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex MgmtSection();

    [GeneratedRegex(@"\d{1,3}(?:\.\d{1,3}){3}")]
    private static partial Regex AnyIpv4();

    public static IReadOnlyList<NeighborInfo> ParseCdpDetail(string text)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var starts = CdpDeviceId().Matches(t).Select(m => m.Index).ToList();
        var list = new List<NeighborInfo>();
        for (int i = 0; i < starts.Count; i++)
        {
            var block = t[starts[i]..(i + 1 < starts.Count ? starts[i + 1] : t.Length)];
            var id = CdpDeviceId().Match(block).Groups["v"].Value;
            var name = SystemName().Match(block) is { Success: true } sn ? sn.Groups["v"].Value : id;
            var plat = CdpPlatform().Match(block);
            var intf = CdpInterfaces().Match(block);
            if (!intf.Success) continue;
            list.Add(new NeighborInfo(name, ManagementIp(block), intf.Groups["l"].Value.Trim(), intf.Groups["r"].Value.Trim(),
                plat.Success ? plat.Groups["p"].Value.Trim() : "", plat.Success ? plat.Groups["c"].Value.Trim() : "", "CDP"));
        }
        return list;
    }

    private static string? ManagementIp(string block)
    {
        if (MgmtSection().Match(block) is { Success: true } m && AnyIpv4().Match(m.Groups["rest"].Value + "\n" + m.Groups["next"].Value) is { Success: true } ip)
            return ip.Value;
        return AddressLine().Match(block) is { Success: true } a ? a.Groups["ip"].Value : null;
    }

    [GeneratedRegex(@"^-{10,}\s*$", RegexOptions.Multiline)]
    private static partial Regex Separator();

    [GeneratedRegex(@"^\s*Chassis id\s*:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ChassisStart();

    [GeneratedRegex(@"^\s*(?:Local Intf|Local Port id)\s*:\s*(?<v>\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LldpLocal();

    [GeneratedRegex(@"^\s*Port id\s*:\s*(?<v>\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LldpPort();

    [GeneratedRegex(@"^\s*Port Description\s*:\s*(?<v>\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LldpPortDescription();

    [GeneratedRegex(@"^\s*Chassis id\s*:\s*(?<v>\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LldpChassis();

    [GeneratedRegex(@"^\s*System Capabilities\s*:\s*(?<v>.*?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LldpCapabilities();

    [GeneratedRegex(@"^\s*System Description\s*:\s*\n?\s*(?<v>[^\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LldpDescription();

    [GeneratedRegex(@"^[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}$|^[0-9a-fA-F]{2}(?:[:\-][0-9a-fA-F]{2}){5}$")]
    private static partial Regex MacLike();

    public static IReadOnlyList<NeighborInfo> ParseLldpDetail(string text)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // IOS separates entries with a dashed line (and "Local Intf" comes before "Chassis id"); NX-OS just starts
        // each entry at "Chassis id".
        var blocks = Separator().IsMatch(t)
            ? Separator().Split(t)
            : SplitAt(t, ChassisStart().Matches(t).Select(m => m.Index).ToList());
        var list = new List<NeighborInfo>();
        foreach (var block in blocks)
        {
            if (LldpLocal().Match(block) is not { Success: true } local || LldpPort().Match(block) is not { Success: true } port) continue;
            var remote = port.Groups["v"].Value;
            if (MacLike().IsMatch(remote) && LldpPortDescription().Match(block) is { Success: true } desc) remote = desc.Groups["v"].Value;
            var name = SystemName().Match(block) is { Success: true } sn ? sn.Groups["v"].Value
                : LldpChassis().Match(block) is { Success: true } ch ? ch.Groups["v"].Value : "";
            if (name.Length == 0) continue;
            var platform = LldpDescription().Match(block) is { Success: true } d ? d.Groups["v"].Value.Trim() : "";
            if (platform.Length > 60) platform = platform[..60];
            list.Add(new NeighborInfo(name, ManagementIp(block), local.Groups["v"].Value, remote, platform,
                LldpCapabilities().Match(block) is { Success: true } c ? c.Groups["v"].Value : "", "LLDP"));
        }
        return list;
    }

    private static IEnumerable<string> SplitAt(string t, List<int> starts) =>
        starts.Select((s, i) => t[s..(i + 1 < starts.Count ? starts[i + 1] : t.Length)]);

    [GeneratedRegex(@"^\s*Local Interface\s+Parent Interface\s+Chassis Id\s+Port info\s+System Name\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex JunosLldpHeader();

    /// <summary>Junos "show lldp neighbors" - a table without management addresses, so these become map leaves.</summary>
    public static IReadOnlyList<NeighborInfo> ParseJunosLldp(string text)
    {
        var lines = TextLines.Split(text);
        int h = Array.FindIndex(lines, l => JunosLldpHeader().IsMatch(l));
        if (h < 0) return [];
        var header = lines[h];
        string[] columns = ["Local Interface", "Parent Interface", "Chassis Id", "Port info", "System Name"];
        var starts = columns.Select(n => header.IndexOf(n, StringComparison.OrdinalIgnoreCase)).ToArray();
        var list = new List<NeighborInfo>();
        foreach (var l in lines.Skip(h + 1))
        {
            if (l.Trim().Length == 0) break;
            var c = ShowTableParser.Slice(l.TrimEnd(), starts);
            var name = c[4].Length > 0 ? c[4] : c[2];
            if (name.Length > 0 && c[0].Length > 0) list.Add(new NeighborInfo(name, null, c[0], c[3], "", "", "LLDP"));
        }
        return list;
    }

    /// <summary>The key a device is known by across CDP/LLDP/prompts: lowercase, no domain, no "(serial)" suffix.</summary>
    public static string NormalizeName(string name) => DisplayName(name).ToLowerInvariant();

    /// <summary>"core-sw2.corp.example.com" → "core-sw2", "N9K-2(FDO123)" → "N9K-2"; IP addresses are left alone.</summary>
    public static string DisplayName(string name)
    {
        var n = name.Trim();
        int paren = n.IndexOf('(');
        if (paren > 0) n = n[..paren];
        if (!System.Net.IPAddress.TryParse(n, out _) && n.IndexOf('.') is > 0 and var dot) n = n[..dot];
        return n;
    }

    private static readonly (string Long, string Short)[] InterfacePrefixes =
    [
        ("TwentyFiveGigE", "Twe"), ("HundredGigE", "Hu"), ("FortyGigabitEthernet", "Fo"), ("TenGigabitEthernet", "Te"),
        ("GigabitEthernet", "Gi"), ("FastEthernet", "Fa"), ("Port-channel", "Po"), ("Ethernet", "Eth"),
        ("Gig", "Gi"), ("Ten", "Te"), ("Fas", "Fa"),
    ];

    /// <summary>"GigabitEthernet1/0/1", "Gig 1/0/1" and "Gi1/0/1" all → "Gi1/0/1", so both ends of a link line up.</summary>
    public static string ShortInterface(string name)
    {
        var n = name.Trim();
        foreach (var (longName, shortName) in InterfacePrefixes)
        {
            if (!n.StartsWith(longName, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = n[longName.Length..].TrimStart();
            if (rest.Length > 0 && char.IsDigit(rest[0])) return shortName + rest;
        }
        return n;
    }

    public static NodeKind Classify(string platform, string capabilities)
    {
        var p = platform.ToUpperInvariant();
        var c = capabilities.ToUpperInvariant();
        if (p.Contains("ASA") || p.Contains("FORTIGATE") || p.Contains("FGT") || p.StartsWith("PA-") || p.Contains("FIREPOWER") || p.Contains("FPR") || p.Contains("SRX"))
            return NodeKind.Firewall;
        if (c.Contains("PHONE") || Regex.IsMatch(c, @"(^|[\s,])T($|[\s,])") || p.Contains("IP PHONE") || p.StartsWith("CP-")) return NodeKind.Phone;
        if (Regex.IsMatch(c, @"(^|[\s,])W($|[\s,])") || p.StartsWith("AIR-") || p.Contains("ACCESS POINT")) return NodeKind.AccessPoint;
        bool router = c.Contains("ROUTER") || Regex.IsMatch(c, @"(^|[\s,])R($|[\s,])");
        bool sw = c.Contains("SWITCH") || Regex.IsMatch(c, @"(^|[\s,])B($|[\s,])");
        if (sw) return NodeKind.Switch;
        if (router) return NodeKind.Router;
        if (c.Contains("HOST") || Regex.IsMatch(c, @"(^|[\s,])S($|[\s,])")) return NodeKind.Host;
        return NodeKind.Unknown;
    }

    /// <summary>Worth logging in to and crawling further: network gear, not phones, APs or end hosts.</summary>
    public static bool IsInfrastructure(NodeKind kind) => kind is NodeKind.Router or NodeKind.Switch or NodeKind.Firewall;
}
