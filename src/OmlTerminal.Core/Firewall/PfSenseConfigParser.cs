using System.Xml.Linq;

namespace OmlTerminal.Core.Firewall;

/// <summary>Everything pulled out of a pfSense <c>config.xml</c> backup (Diagnostics &gt; Backup &amp; Restore &gt;
/// Download configuration): host/network aliases, port aliases, and filter rules turned into the shared model.
/// A rule using negated source/destination match ("not") is skipped rather than migrated inverted - creating a
/// rule with the opposite meaning of the original would be worse than not migrating it at all. NAT, IPsec/OpenVPN
/// and gateways/routing are surfaced as review items instead.</summary>
public sealed record PfSenseParseResult(
    List<AddressObject> Addresses,
    List<ServiceObject> Services,
    List<PolicyRule> Rules,
    IReadOnlyList<string> InterfacesFound,
    IReadOnlyList<string> ReviewItems,
    IReadOnlyList<ParseIssue> Issues);

public static class PfSenseConfigParser
{
    public static PfSenseParseResult Parse(string xml)
    {
        var issues = new List<ParseIssue>();
        var reviewItems = new List<string>();
        var addresses = new List<AddressObject>();
        var services = new List<ServiceObject>();
        var rules = new List<PolicyRule>();
        var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addrAliasMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var portAliasMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            issues.Add(new ParseIssue(0, "", $"not valid XML: {ex.Message}"));
            return new PfSenseParseResult(addresses, services, rules, [], reviewItems, issues);
        }

        foreach (var alias in doc.Descendants("alias"))
        {
            var name = (string?)alias.Element("name") ?? "";
            var type = (string?)alias.Element("type") ?? "";
            var raw = (string?)alias.Element("address") ?? "";
            var descr = (string?)alias.Element("descr") ?? "";
            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length == 0 || tokens.Length == 0) continue;

            if (type is "host" or "network")
            {
                var members = new List<string>();
                for (int n = 0; n < tokens.Length; n++)
                {
                    var objName = tokens.Length == 1 ? name : $"{name}_{n + 1}";
                    var obj = AddressListParser.TryParseValue(tokens[n], descr, out var err);
                    if (obj is null) { issues.Add(new ParseIssue(0, tokens[n], $"alias '{name}': {err}")); continue; }
                    addresses.Add(obj with { Name = objName });
                    members.Add(objName);
                }
                addrAliasMembers[name] = members;
            }
            else if (type == "port")
            {
                var members = new List<string>();
                foreach (var tok in tokens) members.Add(tok.Replace(':', '-'));
                portAliasMembers[name] = members;
            }
            else
            {
                reviewItems.Add($"Alias '{name}': type '{type}' (URL/table-based) can't be represented as a static address list - recreate manually.");
            }
        }

        foreach (var rule in doc.Descendants("filter").Elements("rule"))
        {
            var type = (string?)rule.Element("type") ?? "block";
            var iface = (string?)rule.Element("interface") ?? "";
            var proto = (string?)rule.Element("protocol");
            var descr = (string?)rule.Element("descr") ?? "";
            bool disabled = rule.Element("disabled") != null;
            bool log = rule.Element("log") != null;

            var srcEl = rule.Element("source");
            var dstEl = rule.Element("destination");
            if (srcEl?.Element("not") != null || dstEl?.Element("not") != null)
            {
                issues.Add(new ParseIssue(0, descr.Length > 0 ? descr : iface, "rule uses a negated (\"not\") source or destination match, which has no safe equivalent here - migrating it non-negated would invert its meaning, so it was skipped"));
                continue;
            }

            var sources = ResolveEndpoint(srcEl, addrAliasMembers);
            var destinations = ResolveEndpoint(dstEl, addrAliasMembers);
            if (iface.Length > 0) interfaces.Add(iface);

            List<string> svcTokens;
            var dstPort = (string?)dstEl?.Element("port");
            if (proto is null) svcTokens = ["any"];
            else if (proto.Equals("icmp", StringComparison.OrdinalIgnoreCase)) svcTokens = ["icmp"];
            else if (dstPort is null)
            {
                svcTokens = ["any"];
                reviewItems.Add($"Rule '{(descr.Length > 0 ? descr : iface)}': restricted to protocol '{proto}' with no destination port - migrated as service 'any' (protocol-only restriction has no direct equivalent), narrow it manually if needed.");
            }
            else if (portAliasMembers.TryGetValue(dstPort, out var members))
                svcTokens = members.Select(p => $"{proto}/{p}").ToList();
            else
                svcTokens = [$"{proto}/{dstPort.Replace(':', '-')}"];

            rules.Add(new PolicyRule
            {
                Name = descr.Length > 0 ? descr : $"{iface}-rule-{rules.Count + 1}",
                Action = type.Equals("pass", StringComparison.OrdinalIgnoreCase) ? PolicyAction.Allow
                       : type.Equals("reject", StringComparison.OrdinalIgnoreCase) ? PolicyAction.Reject
                       : PolicyAction.Deny,
                SourceInterface = iface,
                Sources = sources,
                Destinations = destinations,
                Services = svcTokens,
                Log = log,
                Comment = descr,
                Enabled = !disabled,
            });
        }

        foreach (var section in new[] { "nat", "openvpn", "ipsec" })
            if (doc.Descendants(section).Any(e => e.HasElements))
                reviewItems.Add($"'{section}' configuration found - NAT/VPN aren't migrated; recreate manually on the target firewall.");
        if (doc.Descendants("gateways").Any(e => e.HasElements) || doc.Descendants("staticroutes").Any(e => e.HasElements))
            reviewItems.Add("Gateway/static route configuration found - routing isn't migrated; recreate manually on the target firewall.");

        return new PfSenseParseResult(addresses, services, rules, interfaces.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(), reviewItems, issues);
    }

    private static List<string> ResolveEndpoint(XElement? endpoint, Dictionary<string, List<string>> addrAliasMembers)
    {
        if (endpoint is null) return ["any"];
        if (endpoint.Element("any") is not null) return ["any"];
        var addr = (string?)endpoint.Element("address");
        if (addr is null || addr.Length == 0) return ["any"];
        if (addrAliasMembers.TryGetValue(addr, out var members) && members.Count > 0) return members;
        return [addr]; // a literal IP/CIDR (resolved as a literal downstream) or a name defined elsewhere
    }
}
