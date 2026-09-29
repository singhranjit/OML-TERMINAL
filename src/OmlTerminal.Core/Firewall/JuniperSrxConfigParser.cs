namespace OmlTerminal.Core.Firewall;

/// <summary>Everything pulled out of a Junos "set" format config (<c>show configuration | display set</c>):
/// address-book entries/address-sets, applications, and security policies turned into the shared model. NAT, IKE/
/// IPsec VPN and routing-options are surfaced as review items rather than translated - Junos models each as its
/// own configuration hierarchy with no equivalent in the shared object/policy model this migrates.</summary>
public sealed record SrxParseResult(
    List<AddressObject> Addresses,
    List<ServiceObject> Services,
    List<PolicyRule> Rules,
    IReadOnlyList<string> InterfacesFound,
    IReadOnlyList<string> ReviewItems,
    IReadOnlyList<ParseIssue> Issues);

public static class JuniperSrxConfigParser
{
    private sealed class RuleBuilder
    {
        public string FromZone = "", ToZone = "";
        public List<string> Sources = [], Destinations = [], Applications = [];
        public string Then = "permit";
        public bool Log;
        public bool SeenDisable;
    }

    public static SrxParseResult Parse(string config)
    {
        var lines = TextLines.Split(config);
        var issues = new List<ParseIssue>();
        var reviewItems = new List<string>();

        var addrValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addrOrder = new List<string>();
        var svcSpec = new Dictionary<string, (string Proto, string Port)>(StringComparer.OrdinalIgnoreCase);
        var svcOrder = new List<string>();
        var rules = new Dictionary<string, RuleBuilder>(StringComparer.OrdinalIgnoreCase);
        var ruleOrder = new List<string>();
        var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            var t = Tokenize(lines[lineNo]);
            if (t.Count < 2 || !t[0].Equals("set", StringComparison.OrdinalIgnoreCase)) continue;

            if (Matches(t, 1, "security", "address-book", "global", "address") && t.Count > 5 && !IsAddressSet(t))
            {
                var name = t[5];
                if (!addrOrder.Contains(name)) addrOrder.Add(name);
                if (t.Count > 7 && t[6] == "range-address" && t.Count > 8 && t[8] == "to" && t.Count > 9) addrValue[name] = $"{t[7]}-{t[9]}";
                else if (t.Count > 7 && t[6] == "dns-name") addrValue[name] = t[7];
                else if (t.Count > 6 && !(t[6] is "range-address" or "dns-name" or "description")) addrValue[name] = t[6];
            }
            else if (Matches(t, 1, "security", "address-book", "global", "address-set") && t.Count > 5)
            {
                reviewItems.Add($"Line {lineNo + 1}: address-set '{t[5]}' - not recreated as a named set; a policy referencing it gets its member names inline instead.");
            }
            else if (Matches(t, 1, "applications", "application") && t.Count > 3)
            {
                ParseApplication(t, svcSpec, svcOrder, lineNo, issues);
            }
            else if (Matches(t, 1, "security", "policies", "from-zone") && t.Count > 8 && t[5] == "to-zone" && t[7] == "policy")
            {
                ParsePolicy(t, rules, ruleOrder, interfaces);
            }
            else if (Matches(t, 1, "security", "nat")) reviewItems.Add($"Line {lineNo + 1}: NAT configuration - not migrated; recreate on the target firewall.");
            else if (Matches(t, 1, "security", "ike") || Matches(t, 1, "security", "ipsec")) reviewItems.Add($"Line {lineNo + 1}: IKE/IPsec VPN configuration - not migrated; recreate the tunnel on the target firewall.");
            else if (Matches(t, 1, "routing-options")) reviewItems.Add($"Line {lineNo + 1}: routing-options - not migrated; recreate routes on the target firewall.");
        }

        var addresses = new List<AddressObject>();
        foreach (var name in addrOrder)
        {
            if (!addrValue.TryGetValue(name, out var value)) { issues.Add(new ParseIssue(0, name, "address has no recognized value line - skipped")); continue; }
            var obj = AddressListParser.TryParseValue(value, "", out var error);
            if (obj is null) issues.Add(new ParseIssue(0, $"{name} = {value}", $"address '{name}': {error}"));
            else addresses.Add(obj with { Name = name });
        }

        var services = svcOrder.Select(name =>
        {
            var (proto, port) = svcSpec[name];
            return new ServiceObject(name, proto.Equals("udp", StringComparison.OrdinalIgnoreCase) ? ServiceProtocol.Udp : ServiceProtocol.Tcp, port);
        }).ToList();

        var policyRules = ruleOrder.Select(key =>
        {
            var b = rules[key];
            return new PolicyRule
            {
                Name = key[(key.IndexOf('\u0001') + 1)..],
                Action = b.Then switch { "permit" => PolicyAction.Allow, "reject" => PolicyAction.Reject, _ => PolicyAction.Deny },
                SourceInterface = b.FromZone,
                DestinationInterface = b.ToZone,
                Sources = b.Sources.Count > 0 ? b.Sources : ["any"],
                Destinations = b.Destinations.Count > 0 ? b.Destinations : ["any"],
                Services = b.Applications.Count > 0 ? b.Applications.Select(a => a.Equals("any", StringComparison.OrdinalIgnoreCase) ? "any" : a).ToList() : ["any"],
                Log = b.Log,
                Enabled = true, // Junos policies are removed rather than disabled in "set" output - "deactivate" lines aren't captured by "display set" the same way, out of scope for this pass
            };
        }).ToList();

        return new SrxParseResult(addresses, services, policyRules, interfaces.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(), reviewItems, issues);
    }

    private static bool IsAddressSet(List<string> t) => t.Count > 5 && t[5] == "address-set";

    private static bool Matches(List<string> t, int start, params string[] words)
    {
        if (t.Count < start + words.Length) return false;
        for (int i = 0; i < words.Length; i++) if (!t[start + i].Equals(words[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static void ParseApplication(List<string> t, Dictionary<string, (string, string)> spec, List<string> order, int lineNo, List<ParseIssue> issues)
    {
        var name = t[3];
        // "set applications application NAME protocol tcp destination-port 8443"
        var proto = IndexOf(t, "protocol") is { } pi && t.Count > pi + 1 ? t[pi + 1] : null;
        var port = IndexOf(t, "destination-port") is { } di && t.Count > di + 1 ? t[di + 1] : null;
        if (proto is null || port is null) { issues.Add(new ParseIssue(lineNo + 1, string.Join(' ', t), $"application '{name}': expected 'protocol tcp|udp destination-port <n>' - skipped")); return; }
        spec[name] = (proto, port.Replace("-", "-")); // Junos ranges are already "low-high"
        if (!order.Contains(name)) order.Add(name);
    }

    private static int? IndexOf(List<string> t, string token)
    {
        int i = t.IndexOf(token);
        return i < 0 ? null : i;
    }

    private static void ParsePolicy(List<string> t, Dictionary<string, RuleBuilder> rules, List<string> order, HashSet<string> interfaces)
    {
        // set security policies from-zone <fromZone> to-zone <toZone> policy <name> match|then ...
        //   t: 0=set 1=security 2=policies 3=from-zone 4=<fromZone> 5=to-zone 6=<toZone> 7=policy 8=<name> 9=match|then 10=field 11=value...
        var fromZone = t[4];
        var toZone = t[6];
        var name = t[8];
        var key = $"{fromZone}/{toZone}\u0001{name}";
        if (!rules.TryGetValue(key, out var b)) { b = new RuleBuilder { FromZone = fromZone, ToZone = toZone }; rules[key] = b; order.Add(key); }
        if (!fromZone.Equals("any", StringComparison.OrdinalIgnoreCase)) interfaces.Add(fromZone);
        if (!toZone.Equals("any", StringComparison.OrdinalIgnoreCase)) interfaces.Add(toZone);
        if (t.Count <= 9) return;

        if (t[9] == "match" && t.Count > 11 && t[10] == "source-address") { if (!b.Sources.Contains(t[11], StringComparer.OrdinalIgnoreCase)) b.Sources.Add(t[11]); }
        else if (t[9] == "match" && t.Count > 11 && t[10] == "destination-address") { if (!b.Destinations.Contains(t[11], StringComparer.OrdinalIgnoreCase)) b.Destinations.Add(t[11]); }
        else if (t[9] == "match" && t.Count > 11 && t[10] == "application") { if (!b.Applications.Contains(t[11], StringComparer.OrdinalIgnoreCase)) b.Applications.Add(t[11]); }
        else if (t[9] == "then" && t.Count > 10 && t[10] is "permit" or "deny" or "reject") b.Then = t[10];
        else if (t[9] == "then" && t.Count > 10 && t[10] == "log") b.Log = true;
    }

    private static List<string> Tokenize(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
}
