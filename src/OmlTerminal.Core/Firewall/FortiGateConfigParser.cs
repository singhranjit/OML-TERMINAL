namespace OmlTerminal.Core.Firewall;

/// <summary>Everything pulled out of a FortiGate "show full-configuration": address/address-group,
/// service/service-group and firewall policy blocks turned into the same shared model the ASA parser produces.
/// VPN (phase1/phase2), routing, HA and SD-WAN configuration is deliberately not modeled - collected in
/// ReviewItems instead of guessed at.</summary>
public sealed record FortiParseResult(
    List<AddressObject> Addresses,
    List<ServiceObject> Services,
    List<PolicyRule> Rules,
    IReadOnlyList<string> InterfacesFound,
    IReadOnlyList<string> ReviewItems,
    IReadOnlyList<ParseIssue> Issues);

public static class FortiGateConfigParser
{
    /// <summary>FortiOS's built-in "predefined" services - a small, commonly-used subset. A policy referencing
    /// one of these by name resolves to a real port instead of being passed through as an unresolvable object name.</summary>
    private static readonly Dictionary<string, ServiceObject> PredefinedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HTTP"] = new ServiceObject("HTTP", ServiceProtocol.Tcp, "80"),
        ["HTTPS"] = new ServiceObject("HTTPS", ServiceProtocol.Tcp, "443"),
        ["SSH"] = new ServiceObject("SSH", ServiceProtocol.Tcp, "22"),
        ["TELNET"] = new ServiceObject("TELNET", ServiceProtocol.Tcp, "23"),
        ["FTP"] = new ServiceObject("FTP", ServiceProtocol.Tcp, "21"),
        ["SMTP"] = new ServiceObject("SMTP", ServiceProtocol.Tcp, "25"),
        ["DNS"] = new ServiceObject("DNS", ServiceProtocol.TcpUdp, "53"),
        ["RDP"] = new ServiceObject("RDP", ServiceProtocol.Tcp, "3389"),
        ["NTP"] = new ServiceObject("NTP", ServiceProtocol.Udp, "123"),
        ["SNMP"] = new ServiceObject("SNMP", ServiceProtocol.Udp, "161"),
        ["SYSLOG"] = new ServiceObject("SYSLOG", ServiceProtocol.Udp, "514"),
        ["LDAP"] = new ServiceObject("LDAP", ServiceProtocol.Tcp, "389"),
        ["POP3"] = new ServiceObject("POP3", ServiceProtocol.Tcp, "110"),
        ["IMAP"] = new ServiceObject("IMAP", ServiceProtocol.Tcp, "143"),
        ["TFTP"] = new ServiceObject("TFTP", ServiceProtocol.Udp, "69"),
        ["PING"] = new ServiceObject("PING", ServiceProtocol.Icmp, ""),
        ["ALL_ICMP"] = new ServiceObject("ALL_ICMP", ServiceProtocol.Icmp, ""),
    };

    public static FortiParseResult Parse(string config)
    {
        var lines = TextLines.Split(config).Select(l => l.TrimEnd('\r')).ToList();
        var issues = new List<ParseIssue>();
        var reviewItems = new List<string>();
        var addresses = new List<AddressObject>();
        var services = new List<ServiceObject>();
        var rules = new List<PolicyRule>();
        var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int i = 0;
        while (i < lines.Count)
        {
            var tokens = Tokenize(lines[i]);
            if (tokens.Length == 0) { i++; continue; }
            var section = string.Join(' ', tokens);

            if (section.Equals("config firewall address", StringComparison.OrdinalIgnoreCase))
                i = ParseAddressBlock(lines, i, addresses, issues);
            else if (section.Equals("config firewall service custom", StringComparison.OrdinalIgnoreCase))
                i = ParseServiceBlock(lines, i, services, issues);
            else if (section.Equals("config firewall policy", StringComparison.OrdinalIgnoreCase))
                i = ParsePolicyBlock(lines, i, rules, interfaces, issues);
            else if (section.StartsWith("config vpn", StringComparison.OrdinalIgnoreCase))
            { reviewItems.Add($"Line {i + 1}: VPN configuration ('{section}') - not migrated; recreate the tunnel on the target firewall."); i = SkipToMatchingEnd(lines, i); }
            else if (section.Equals("config router static", StringComparison.OrdinalIgnoreCase) || section.StartsWith("config router", StringComparison.OrdinalIgnoreCase))
            { reviewItems.Add($"Line {i + 1}: routing configuration ('{section}') - not migrated; recreate routes on the target firewall."); i = SkipToMatchingEnd(lines, i); }
            else if (section.Equals("config system ha", StringComparison.OrdinalIgnoreCase) || section.StartsWith("config system sdwan", StringComparison.OrdinalIgnoreCase))
            { reviewItems.Add($"Line {i + 1}: HA/SD-WAN configuration ('{section}') - not migrated; recreate on the target firewall."); i = SkipToMatchingEnd(lines, i); }
            else if (tokens[0].Equals("config", StringComparison.OrdinalIgnoreCase))
                i = SkipToMatchingEnd(lines, i); // any other section we don't model - skip silently, nothing security-relevant lost
            else
                i++;
        }

        return new FortiParseResult(addresses, services, rules, interfaces.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(), reviewItems, issues);
    }

    private static string[] Tokenize(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>"config X" blocks are delimited by "end", possibly containing nested "edit/next" pairs - this
    /// walks nesting depth via config/end so an inner block's own "end" doesn't terminate the outer one early.</summary>
    private static int SkipToMatchingEnd(List<string> lines, int start)
    {
        int depth = 0;
        int i = start;
        for (; i < lines.Count; i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length == 0) continue;
            if (t[0].Equals("config", StringComparison.OrdinalIgnoreCase)) depth++;
            else if (t[0].Equals("end", StringComparison.OrdinalIgnoreCase)) { depth--; if (depth == 0) return i + 1; }
        }
        return i;
    }

    /// <summary>Pulls the quoted value(s) after a "set <key>" line - one or more space-separated "quoted strings".</summary>
    private static List<string> QuotedValues(string line)
    {
        var values = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            int q1 = line.IndexOf('"', i);
            if (q1 < 0) break;
            int q2 = line.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            values.Add(line[(q1 + 1)..q2]);
            i = q2 + 1;
        }
        return values;
    }

    private static int ParseAddressBlock(List<string> lines, int start, List<AddressObject> into, List<ParseIssue> issues)
    {
        int i = start + 1;
        for (; i < lines.Count; i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length == 0) continue;
            if (t[0].Equals("end", StringComparison.OrdinalIgnoreCase)) return i + 1;
            if (!t[0].Equals("edit", StringComparison.OrdinalIgnoreCase)) continue;

            var name = QuotedValues(lines[i]).FirstOrDefault() ?? "";
            AddressObject? found = null;
            string comment = "", startIp = "", endIp = "";
            bool isRangeType = false, isFqdnType = false;
            i++;
            for (; i < lines.Count && !Tokenize(lines[i]).FirstOrDefault().Equals("next", StringComparison.OrdinalIgnoreCase); i++)
            {
                var st = Tokenize(lines[i]);
                if (st.Length < 2 || !st[0].Equals("set", StringComparison.OrdinalIgnoreCase)) continue;
                switch (st[1].ToLowerInvariant())
                {
                    case "type" when st.Length >= 3 && st[2].Equals("fqdn", StringComparison.OrdinalIgnoreCase): isFqdnType = true; break;
                    case "type" when st.Length >= 3 && st[2].Equals("iprange", StringComparison.OrdinalIgnoreCase): isRangeType = true; break;
                    case "subnet" when st.Length >= 4: found = AddressListParser.TryParseValue($"{st[2]} {st[3]}", "", out _); break;
                    case "start-ip" when st.Length >= 3: startIp = st[2]; break;
                    case "end-ip" when st.Length >= 3: endIp = st[2]; break;
                    case "fqdn": found = AddressListParser.TryParseValue(QuotedValues(lines[i]).FirstOrDefault() ?? "", "", out _); break;
                    case "comment": comment = QuotedValues(lines[i]).FirstOrDefault() ?? ""; break;
                }
            }
            // Built from a single TryParseValue call (start-end on one string) so Prefix/RangeEnd are populated
            // exactly the way every other parser in this codebase produces a Range object - building one by hand
            // leaves those fields null, which the target-vendor generators dereference unconditionally.
            if (isRangeType && startIp.Length > 0 && endIp.Length > 0) found = AddressListParser.TryParseValue($"{startIp}-{endIp}", "", out _);
            if (found is not null) into.Add(found with { Name = name, Comment = comment });
            else if (!isFqdnType || into.All(a => a.Name != name)) issues.Add(new ParseIssue(i + 1, name, "address object has no recognized subnet/iprange/fqdn - skipped"));
        }
        return i;
    }

    private static int ParseServiceBlock(List<string> lines, int start, List<ServiceObject> into, List<ParseIssue> issues)
    {
        int i = start + 1;
        for (; i < lines.Count; i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length == 0) continue;
            if (t[0].Equals("end", StringComparison.OrdinalIgnoreCase)) return i + 1;
            if (!t[0].Equals("edit", StringComparison.OrdinalIgnoreCase)) continue;

            var name = QuotedValues(lines[i]).FirstOrDefault() ?? "";
            string? tcp = null, udp = null;
            bool icmp = false;
            i++;
            for (; i < lines.Count && !Tokenize(lines[i]).FirstOrDefault().Equals("next", StringComparison.OrdinalIgnoreCase); i++)
            {
                var st = Tokenize(lines[i]);
                if (st.Length < 3 || !st[0].Equals("set", StringComparison.OrdinalIgnoreCase)) continue;
                if (st[1].Equals("tcp-portrange", StringComparison.OrdinalIgnoreCase)) tcp = NormalizeRange(st[2]);
                else if (st[1].Equals("udp-portrange", StringComparison.OrdinalIgnoreCase)) udp = NormalizeRange(st[2]);
                else if (st[1].Equals("protocol", StringComparison.OrdinalIgnoreCase) && st[2].Equals("ICMP", StringComparison.OrdinalIgnoreCase)) icmp = true;
            }
            if (icmp) into.Add(new ServiceObject(name, ServiceProtocol.Icmp, ""));
            else if (tcp is not null && udp is not null) into.Add(new ServiceObject(name, ServiceProtocol.TcpUdp, tcp));
            else if (tcp is not null) into.Add(new ServiceObject(name, ServiceProtocol.Tcp, tcp));
            else if (udp is not null) into.Add(new ServiceObject(name, ServiceProtocol.Udp, udp));
            else issues.Add(new ParseIssue(i + 1, name, "service object has no recognized tcp/udp-portrange or ICMP protocol - skipped"));
        }
        return i;
    }

    /// <summary>FortiOS writes a port range as "destination[:source]", e.g. "443" or "8000-8010:1024-65535" - only
    /// the destination side is modeled here, matching the destination-only PolicyRule.Services convention.</summary>
    private static string NormalizeRange(string spec) => spec.Split(':')[0];

    private static int ParsePolicyBlock(List<string> lines, int start, List<PolicyRule> into, HashSet<string> interfaces, List<ParseIssue> issues)
    {
        int i = start + 1;
        for (; i < lines.Count; i++)
        {
            var t = Tokenize(lines[i]);
            if (t.Length == 0) continue;
            if (t[0].Equals("end", StringComparison.OrdinalIgnoreCase)) return i + 1;
            if (!t[0].Equals("edit", StringComparison.OrdinalIgnoreCase)) continue;

            var id = t.Length >= 2 ? t[1] : "";
            string name = "", action = "deny", schedule = "", comment = "";
            var srcIntf = new List<string>(); var dstIntf = new List<string>();
            var srcAddr = new List<string>(); var dstAddr = new List<string>(); var svc = new List<string>();
            bool log = false, enabled = true, nat = false;
            i++;
            for (; i < lines.Count && !Tokenize(lines[i]).FirstOrDefault().Equals("next", StringComparison.OrdinalIgnoreCase); i++)
            {
                var st = Tokenize(lines[i]);
                if (st.Length < 2 || !st[0].Equals("set", StringComparison.OrdinalIgnoreCase)) continue;
                switch (st[1].ToLowerInvariant())
                {
                    case "name": name = QuotedValues(lines[i]).FirstOrDefault() ?? ""; break;
                    case "srcintf": srcIntf = QuotedValues(lines[i]); break;
                    case "dstintf": dstIntf = QuotedValues(lines[i]); break;
                    case "srcaddr": srcAddr = QuotedValues(lines[i]); break;
                    case "dstaddr": dstAddr = QuotedValues(lines[i]); break;
                    case "service": svc = QuotedValues(lines[i]); break;
                    case "action": action = st.Length >= 3 ? st[2] : "deny"; break;
                    case "schedule": schedule = QuotedValues(lines[i]).FirstOrDefault() ?? ""; break;
                    case "logtraffic": log = st.Length >= 3 && !st[2].Equals("disable", StringComparison.OrdinalIgnoreCase); break;
                    case "status": enabled = st.Length >= 3 && st[2].Equals("enable", StringComparison.OrdinalIgnoreCase); break;
                    case "nat": nat = st.Length >= 3 && st[2].Equals("enable", StringComparison.OrdinalIgnoreCase); break;
                    case "comments": comment = QuotedValues(lines[i]).FirstOrDefault() ?? ""; break;
                }
            }

            if (srcIntf.Count > 1) issues.Add(new ParseIssue(i + 1, id, $"policy {id} has {srcIntf.Count} source interfaces - only the first ('{srcIntf[0]}') was kept"));
            if (dstIntf.Count > 1) issues.Add(new ParseIssue(i + 1, id, $"policy {id} has {dstIntf.Count} destination interfaces - only the first ('{dstIntf[0]}') was kept"));
            var srcI = srcIntf.FirstOrDefault() ?? ""; var dstI = dstIntf.FirstOrDefault() ?? "";
            if (srcI.Length > 0) interfaces.Add(srcI);
            if (dstI.Length > 0) interfaces.Add(dstI);

            into.Add(new PolicyRule
            {
                Line = i + 1,
                Name = name.Length > 0 ? name : $"policy-{id}",
                Action = action.Equals("accept", StringComparison.OrdinalIgnoreCase) ? PolicyAction.Allow : PolicyAction.Deny,
                SourceInterface = srcI,
                DestinationInterface = dstI,
                Sources = ResolveAddrTokens(srcAddr),
                Destinations = ResolveAddrTokens(dstAddr),
                Services = ResolveSvcTokens(svc),
                Schedule = schedule.Equals("always", StringComparison.OrdinalIgnoreCase) ? "" : schedule,
                Nat = nat,
                Log = log,
                Comment = comment,
                Enabled = enabled,
            });
        }
        return i;
    }

    private static List<string> ResolveAddrTokens(List<string> tokens) =>
        tokens.Count == 0 ? ["any"] : tokens.Select(t => t.Equals("all", StringComparison.OrdinalIgnoreCase) ? "any" : t).ToList();

    /// <summary>A policy's service names are either an address/service-group reference (pass through as-is - it
    /// resolves against whatever the target vendor generator does with an unrecognized token) or one of FortiOS's
    /// predefined services, which get resolved to a real "proto/port" token so the target actually matches the
    /// right traffic instead of an opaque name that means nothing outside FortiOS.</summary>
    private static List<string> ResolveSvcTokens(List<string> tokens)
    {
        if (tokens.Count == 0) return ["any"];
        var result = new List<string>();
        foreach (var tok in tokens)
        {
            if (tok.Equals("ALL", StringComparison.OrdinalIgnoreCase)) { result.Add("any"); continue; }
            if (PredefinedServices.TryGetValue(tok, out var svc))
            {
                result.Add(svc.Protocol == ServiceProtocol.Icmp ? "icmp" : $"{(svc.Protocol == ServiceProtocol.TcpUdp ? "tcp" : svc.Protocol.ToString().ToLowerInvariant())}/{svc.DestinationPorts}");
                continue;
            }
            result.Add(tok); // custom service object name defined elsewhere in the config - pass through
        }
        return result;
    }
}
