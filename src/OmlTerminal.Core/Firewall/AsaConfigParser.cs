namespace OmlTerminal.Core.Firewall;

/// <summary>Everything pulled out of a Cisco ASA "show running-config" (or "more system:running-config"): objects,
/// object-groups (flattened into their members, since the target-vendor generators build one flat group per call
/// rather than nested groups), extended ACLs turned into PolicyRule, and the interface each ACL is bound to via
/// "access-group ... in interface ...". NAT and VPN/crypto configuration is deliberately not modeled - those
/// differ too much between vendors to translate automatically - so any such lines are collected in ReviewItems
/// instead of being silently dropped or guessed at.</summary>
public sealed record AsaParseResult(
    List<AddressObject> Addresses,
    List<ServiceObject> Services,
    List<PolicyRule> Rules,
    IReadOnlyList<string> InterfacesFound,
    IReadOnlyList<string> ReviewItems,
    IReadOnlyList<ParseIssue> Issues);

public static class AsaConfigParser
{
    private static readonly Dictionary<string, string> PortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["www"] = "80", ["http"] = "80", ["https"] = "443", ["ssh"] = "22", ["telnet"] = "23",
        ["ftp"] = "21", ["ftp-data"] = "20", ["smtp"] = "25", ["domain"] = "53", ["pop3"] = "110",
        ["imap4"] = "143", ["ldap"] = "389", ["ldaps"] = "636", ["rdp"] = "3389", ["syslog"] = "514",
        ["snmp"] = "161", ["snmptrap"] = "162", ["ntp"] = "123", ["tftp"] = "69", ["nntp"] = "119",
        ["netbios-ns"] = "137", ["netbios-dgm"] = "138", ["netbios-ssn"] = "139", ["h323"] = "1720",
        ["sip"] = "5060", ["radius"] = "1645", ["radius-acct"] = "1646", ["citrix-ica"] = "1494",
        ["pptp"] = "1723", ["exec"] = "512", ["login"] = "513", ["shell"] = "514", ["kerberos"] = "750",
        ["lpd"] = "515", ["nfs"] = "2049", ["sqlnet"] = "1521", ["whois"] = "43", ["isakmp"] = "500",
        ["bgp"] = "179", ["finger"] = "79", ["ident"] = "113", ["irc"] = "194", ["pop2"] = "109",
        ["uucp"] = "540", ["xdmcp"] = "177", ["ike"] = "500", ["discard"] = "9", ["echo"] = "7",
    };

    private static readonly string[] AddressGroupKeywords = ["access-list", "access-group", "object", "object-group"];

    public static AsaParseResult Parse(string config)
    {
        var lines = TextLines.Split(config).Select(l => l.TrimEnd('\r')).ToList();
        var issues = new List<ParseIssue>();
        var reviewItems = new List<string>();

        var objectAddrs = new List<AddressObject>();
        var objectSvcs = new List<ServiceObject>();
        var addrGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var svcGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var aclLines = new List<(int LineNo, string[] Tokens)>();
        var aclInterfaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.StartsWith('!') || char.IsWhiteSpace(line, 0)) continue;
            var tokens = Tokenize(line);
            if (tokens.Length == 0) continue;

            switch (tokens[0].ToLowerInvariant())
            {
                case "object" when tokens.Length >= 3 && tokens[1].Equals("network", StringComparison.OrdinalIgnoreCase):
                    i = ParseObjectNetwork(lines, i, tokens[2], objectAddrs, reviewItems, issues);
                    break;
                case "object" when tokens.Length >= 3 && tokens[1].Equals("service", StringComparison.OrdinalIgnoreCase):
                    i = ParseObjectService(lines, i, tokens[2], objectSvcs, issues);
                    break;
                case "object-group" when tokens.Length >= 3 && tokens[1].Equals("network", StringComparison.OrdinalIgnoreCase):
                    i = ParseGroupNetwork(lines, i, tokens[2], addrGroups, issues);
                    break;
                case "object-group" when tokens.Length >= 3 && tokens[1].Equals("service", StringComparison.OrdinalIgnoreCase):
                    // "object-group service NAME [tcp|udp]" - the optional trailing protocol applies to every
                    // bare "port-object" member inside; "service-object" members carry their own protocol instead.
                    var headerProto = tokens.Length >= 4 && tokens[3] is "tcp" or "udp" ? tokens[3] : null;
                    i = ParseGroupService(lines, i, tokens[2], headerProto, svcGroups, issues);
                    break;
                case "object-group":
                    reviewItems.Add($"Line {i + 1}: object-group of type '{(tokens.Length > 1 ? tokens[1] : "?")}' isn't migrated (only network/service groups are) - recreate manually if referenced by a rule.");
                    i = SkipBlock(lines, i);
                    break;
                case "access-list":
                    if (tokens.Length >= 3 && tokens[2].Equals("extended", StringComparison.OrdinalIgnoreCase))
                        aclLines.Add((i + 1, tokens));
                    else if (tokens.Length >= 3 && tokens[2].Equals("remark", StringComparison.OrdinalIgnoreCase))
                    { /* comment line, nothing to migrate */ }
                    else
                        issues.Add(new ParseIssue(i + 1, line, "only 'access-list ... extended ...' ACEs are migrated - standard ACLs are skipped"));
                    break;
                case "access-group" when tokens.Length >= 5 && tokens[2].Equals("in", StringComparison.OrdinalIgnoreCase) && tokens[3].Equals("interface", StringComparison.OrdinalIgnoreCase):
                    aclInterfaces[tokens[1]] = tokens[4];
                    break;
                case "nat":
                case "global":
                    reviewItems.Add($"Line {i + 1}: NAT rule ('{Head(line)}') - NAT isn't automatically translated between vendors; recreate on the target firewall.");
                    break;
                case "crypto":
                case "tunnel-group":
                case "group-policy":
                    reviewItems.Add($"Line {i + 1}: VPN/crypto configuration ('{Head(line)}') - not migrated; recreate the tunnel on the target firewall.");
                    break;
                case "route":
                case "router":
                    reviewItems.Add($"Line {i + 1}: routing configuration ('{Head(line)}') - not migrated; recreate routes on the target firewall.");
                    break;
            }
        }

        var rules = new List<PolicyRule>();
        var svcObjects = objectSvcs.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var (lineNo, tokens) in aclLines)
        {
            var iface = aclInterfaces.GetValueOrDefault(tokens[1], "");
            var rule = ParseAce(lineNo, tokens, iface, addrGroups, svcGroups, svcObjects, issues);
            if (rule is not null) rules.Add(rule);
        }

        var interfaces = aclInterfaces.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        return new AsaParseResult(objectAddrs, objectSvcs, rules, interfaces, reviewItems, issues);
    }

    private static string Head(string line) => line.Trim().Length > 70 ? line.Trim()[..70] + "..." : line.Trim();

    private static string[] Tokenize(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Indented lines belong to the block that started at <paramref name="start"/>; returns the index of
    /// the block's last line (so the caller's loop, which increments, resumes after it).</summary>
    private static int SkipBlock(List<string> lines, int start)
    {
        int i = start + 1;
        while (i < lines.Count && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0])) i++;
        return i - 1;
    }

    private static int ParseObjectNetwork(List<string> lines, int start, string name, List<AddressObject> into, List<string> reviewItems, List<ParseIssue> issues)
    {
        int i = start + 1;
        AddressObject? found = null;
        string comment = "";
        for (; i < lines.Count && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]); i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length == 0) continue;
            switch (t[0].ToLowerInvariant())
            {
                case "host" when t.Length >= 2:
                    found = AddressListParser.TryParseValue(t[1], "", out _);
                    break;
                case "subnet" when t.Length >= 3:
                    found = AddressListParser.TryParseValue($"{t[1]} {t[2]}", "", out _);
                    break;
                case "range" when t.Length >= 3:
                    found = AddressListParser.TryParseValue($"{t[1]}-{t[2]}", "", out _);
                    break;
                case "fqdn":
                    // "fqdn v4 example.com" or "fqdn example.com"
                    var val = t.Length >= 3 && (t[1].Equals("v4", StringComparison.OrdinalIgnoreCase) || t[1].Equals("v6", StringComparison.OrdinalIgnoreCase)) ? t[2] : t.Length >= 2 ? t[1] : "";
                    if (val.Length > 0) found = AddressListParser.TryParseValue(val, "", out _);
                    break;
                case "description":
                    comment = string.Join(' ', t[1..]);
                    break;
                case "nat":
                    reviewItems.Add($"Line {i + 1}: object network '{name}' has a NAT rule attached - NAT isn't migrated; recreate it on the target firewall.");
                    break;
            }
        }
        if (found is not null) into.Add(found with { Name = name, Comment = comment });
        else issues.Add(new ParseIssue(start + 1, name, "object network has no recognized host/subnet/range/fqdn line - skipped"));
        return i - 1;
    }

    private static int ParseObjectService(List<string> lines, int start, string name, List<ServiceObject> into, List<ParseIssue> issues)
    {
        int i = start + 1;
        ServiceObject? found = null;
        for (; i < lines.Count && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]); i++)
        {
            var t = Tokenize(lines[i]);
            // "service tcp destination eq https" / "service tcp destination range 8000 8010" / "service icmp"
            if (t.Length == 0 || !t[0].Equals("service", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Length >= 2 && t[1].Equals("icmp", StringComparison.OrdinalIgnoreCase)) { found = new ServiceObject(name, ServiceProtocol.Icmp, ""); continue; }
            if (t.Length < 2 || t[1] is not ("tcp" or "udp")) continue;
            var proto = t[1].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? ServiceProtocol.Tcp : ServiceProtocol.Udp;
            string dst = "", src = "";
            for (int k = 2; k < t.Length - 1; k++)
            {
                if (t[k].Equals("destination", StringComparison.OrdinalIgnoreCase)) dst = ParsePortSpec(t, k + 1, out _);
                else if (t[k].Equals("source", StringComparison.OrdinalIgnoreCase)) src = ParsePortSpec(t, k + 1, out _);
            }
            if (dst.Length > 0) found = new ServiceObject(name, proto, dst, src);
        }
        if (found is not null) into.Add(found);
        else issues.Add(new ParseIssue(start + 1, name, "object service has no recognized tcp/udp/icmp line - skipped"));
        return i - 1;
    }

    private static int ParseGroupNetwork(List<string> lines, int start, string name, Dictionary<string, List<string>> into, List<ParseIssue> issues)
    {
        var members = new List<string>();
        int i = start + 1;
        for (; i < lines.Count && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]); i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length < 2 || !t[0].Equals("network-object", StringComparison.OrdinalIgnoreCase)) continue;
            if (t[1].Equals("host", StringComparison.OrdinalIgnoreCase) && t.Length >= 3) members.Add(t[2]);
            else if (t[1].Equals("object", StringComparison.OrdinalIgnoreCase) && t.Length >= 3) members.Add(t[2]); // reference by name
            else if (t.Length >= 3) members.Add($"{t[1]} {t[2]}"); // "network-object <ip> <mask>"
            else issues.Add(new ParseIssue(i + 1, lines[i].Trim(), "unrecognized network-object line - skipped"));
        }
        into[name] = members;
        return i - 1;
    }

    private static int ParseGroupService(List<string> lines, int start, string name, string? headerProto, Dictionary<string, List<string>> into, List<ParseIssue> issues)
    {
        var members = new List<string>();
        int i = start + 1;
        for (; i < lines.Count && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]); i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length < 2) continue;
            if (t[0].Equals("service-object", StringComparison.OrdinalIgnoreCase))
            {
                if (t[1].Equals("icmp", StringComparison.OrdinalIgnoreCase)) { members.Add("icmp"); continue; }
                if (t.Length >= 2 && t[1] is "tcp" or "udp")
                {
                    var protoTok = t[1];
                    int k = 2;
                    if (k < t.Length && t[k].Equals("destination", StringComparison.OrdinalIgnoreCase)) k++;
                    var port = ParsePortSpec(t, k, out _);
                    if (port.Length > 0) members.Add($"{protoTok}/{port}");
                    continue;
                }
                issues.Add(new ParseIssue(i + 1, lines[i].Trim(), "unrecognized service-object line - skipped"));
            }
            else if (t[0].Equals("port-object", StringComparison.OrdinalIgnoreCase))
            {
                var port = ParsePortSpec(t, 1, out _);
                if (port.Length == 0) continue;
                // "port-object" carries no protocol of its own - it comes from this group's own
                // "object-group service NAME tcp|udp" header line.
                if (headerProto is null) issues.Add(new ParseIssue(i + 1, lines[i].Trim(), "port-object with no protocol on the object-group's header line - skipped"));
                else members.Add($"{headerProto}/{port}");
            }
        }
        into[name] = members;
        return i - 1;
    }

    /// <summary>"eq https" -> "443", "range 8000 8010" -> "8000-8010". Returns "" (and sets ok=false) for
    /// operators with no direct equivalent (gt/lt/neq) - those need a human, not a guess.</summary>
    private static string ParsePortSpec(string[] t, int idx, out bool ok)
    {
        ok = false;
        if (idx >= t.Length) return "";
        var op = t[idx].ToLowerInvariant();
        string Resolve(string p) => PortNames.TryGetValue(p, out var n) ? n : p;
        if (op == "eq" && idx + 1 < t.Length) { ok = true; return Resolve(t[idx + 1]); }
        if (op == "range" && idx + 2 < t.Length) { ok = true; return $"{Resolve(t[idx + 1])}-{Resolve(t[idx + 2])}"; }
        return "";
    }

    private static PolicyRule? ParseAce(int lineNo, string[] t, string sourceInterface, Dictionary<string, List<string>> addrGroups,
        Dictionary<string, List<string>> svcGroups, Dictionary<string, ServiceObject> svcObjects, List<ParseIssue> issues)
    {
        // access-list <name> extended {permit|deny} <proto> <src> [<src-port>] <dst> [<dst-port>] [log] [inactive]
        // ASA 8.3+ also allows a service object/group in place of the protocol: "permit object HTTPS-8443 <src> <dst>".
        int idx = 3;
        if (idx >= t.Length) return null;
        var actionTok = t[idx++].ToLowerInvariant();
        if (actionTok is not ("permit" or "deny")) { issues.Add(new ParseIssue(lineNo, string.Join(' ', t), "expected 'permit' or 'deny'")); return null; }
        if (idx >= t.Length) return null;
        var proto = t[idx++].ToLowerInvariant();
        List<string>? namedServices = null;
        if (proto is "object" or "object-group" && idx < t.Length)
        {
            var svcName = t[idx++];
            namedServices = proto == "object" ? ServiceTokens(svcObjects, svcName, lineNo, t, issues) : svcGroups.GetValueOrDefault(svcName);
            if (namedServices is not { Count: > 0 })
            {
                issues.Add(new ParseIssue(lineNo, string.Join(' ', t), $"service {proto} '{svcName}' isn't defined earlier in the config (or has no destination port) - rule skipped"));
                return null;
            }
            proto = "service";
        }
        bool portCapable = proto is "tcp" or "udp";
        if (proto is not ("tcp" or "udp" or "icmp" or "ip" or "service"))
        {
            issues.Add(new ParseIssue(lineNo, string.Join(' ', t), $"protocol '{proto}' isn't tcp/udp/icmp/ip - rule skipped, recreate manually"));
            return null;
        }

        var src = ConsumeAddress(t, ref idx, addrGroups, out var srcOk);
        if (!srcOk) { issues.Add(new ParseIssue(lineNo, string.Join(' ', t), "couldn't parse the source address - rule skipped")); return null; }
        var srcPort = portCapable ? ConsumePort(t, ref idx, proto, svcGroups, addrGroups, out var srcPortOk) : [];
        if (portCapable && srcPort is null) { issues.Add(new ParseIssue(lineNo, string.Join(' ', t), "source port uses an operator with no direct equivalent (gt/lt/neq), or references a service-group not defined earlier in the config - rule skipped")); return null; }
        if (srcPort is { Count: > 0 } && !srcPort.Contains("any"))
            issues.Add(new ParseIssue(lineNo, string.Join(' ', t), $"source-port restriction ({string.Join(",", srcPort)}) has no equivalent on the destination-only Services model used here and was dropped - the migrated rule matches that service on any source port"));

        var dst = ConsumeAddress(t, ref idx, addrGroups, out var dstOk);
        if (!dstOk) { issues.Add(new ParseIssue(lineNo, string.Join(' ', t), "couldn't parse the destination address - rule skipped")); return null; }
        var dstPort = portCapable ? ConsumePort(t, ref idx, proto, svcGroups, addrGroups, out var dstPortOk) : [];
        if (portCapable && dstPort is null) { issues.Add(new ParseIssue(lineNo, string.Join(' ', t), "destination port uses an operator with no direct equivalent (gt/lt/neq), or references a service-group not defined earlier in the config - rule skipped")); return null; }

        bool log = t.Skip(idx).Any(x => x.Equals("log", StringComparison.OrdinalIgnoreCase));
        bool inactive = t.Skip(idx).Any(x => x.Equals("inactive", StringComparison.OrdinalIgnoreCase));

        List<string> services = proto switch
        {
            "icmp" => ["icmp"],
            "ip" => ["any"],
            "service" => namedServices!,
            _ => dstPort is { Count: > 0 } ? dstPort! : ["any"],
        };

        return new PolicyRule
        {
            Line = lineNo,
            Name = t[1],
            Action = actionTok == "permit" ? PolicyAction.Allow : PolicyAction.Deny,
            SourceInterface = sourceInterface,
            Sources = src,
            Destinations = dst,
            Services = services,
            Log = log,
            Enabled = !inactive,
        };
    }

    /// <summary>A named "object service" as rule service tokens ("tcp/8443"). Null when it isn't defined.</summary>
    private static List<string>? ServiceTokens(Dictionary<string, ServiceObject> objects, string name, int lineNo, string[] t, List<ParseIssue> issues)
    {
        if (!objects.TryGetValue(name, out var s)) return null;
        if (s.SourcePorts.Length > 0)
            issues.Add(new ParseIssue(lineNo, string.Join(' ', t), $"service object '{name}' restricts the source port ({s.SourcePorts}), which the destination-only Services model here drops - the migrated rule matches any source port"));
        return s.Protocol switch
        {
            ServiceProtocol.Icmp => ["icmp"],
            ServiceProtocol.Tcp => [$"tcp/{s.DestinationPorts}"],
            ServiceProtocol.Udp => [$"udp/{s.DestinationPorts}"],
            ServiceProtocol.TcpUdp => [$"tcp/{s.DestinationPorts}", $"udp/{s.DestinationPorts}"],
            _ => null,
        };
    }

    private static List<string> ConsumeAddress(string[] t, ref int idx, Dictionary<string, List<string>> groups, out bool ok)
    {
        ok = false;
        if (idx >= t.Length) return [];
        var tok = t[idx].ToLowerInvariant();
        switch (tok)
        {
            case "any" or "any4" or "any6":
                idx++; ok = true; return ["any"];
            case "host" when idx + 1 < t.Length:
                idx += 2; ok = true; return [t[idx - 1]];
            case "object" when idx + 1 < t.Length:
                idx += 2; ok = true; return [t[idx - 1]];
            case "object-group" when idx + 1 < t.Length:
                var name = t[idx + 1];
                idx += 2;
                if (groups.TryGetValue(name, out var members) && members.Count > 0) { ok = true; return members; }
                ok = true; return [name]; // not found locally (defined elsewhere / truncated capture) - pass the name through
            default:
                // "<ip> <mask>" - two bare tokens
                if (idx + 1 < t.Length) { var result = $"{t[idx]} {t[idx + 1]}"; idx += 2; ok = true; return [result]; }
                return [];
        }
    }

    /// <summary>Returns fully-qualified service tokens ("tcp/443", not bare "443") so a service-group reference
    /// can contribute its own members' protocols directly. Null (not an empty list) means "an unsupported
    /// operator was present" - the caller must distinguish that from "no port restriction" (empty list, i.e.
    /// match any port for the ACE's own protocol).</summary>
    private static List<string>? ConsumePort(string[] t, ref int idx, string proto, Dictionary<string, List<string>> svcGroups,
        Dictionary<string, List<string>> addrGroups, out bool ok)
    {
        ok = true;
        if (idx >= t.Length) return [];
        var tok = t[idx].ToLowerInvariant();
        if (tok is "gt" or "lt" or "neq") { ok = false; return null; }
        if (tok == "object-group" && idx + 1 < t.Length)
        {
            var name = t[idx + 1];
            // "permit tcp any object-group WEB-SERVERS eq https": after the source, a NETWORK group is the destination
            // address, not a source-port group - leave it for ConsumeAddress.
            if (!svcGroups.ContainsKey(name) && addrGroups.ContainsKey(name)) return [];
            idx += 2;
            // A group used as a port-spec must resolve to real members - defaulting to "any" here would
            // silently widen the rule to every port, which is exactly the kind of guess this parser avoids.
            if (svcGroups.TryGetValue(name, out var members) && members.Count > 0) return members;
            ok = false;
            return null;
        }
        var port = ParsePortSpec(t, idx, out var consumed);
        if (!consumed) return [];
        idx += tok == "range" ? 3 : 2;
        return [$"{proto}/{port}"];
    }
}
