namespace OmlTerminal.Core.Firewall;

/// <summary>Everything pulled out of a PAN-OS "set format" config (<c>set cli config-output-format set</c> then
/// <c>show config running</c> - the same preset OML Terminal's own Config Backup tool already uses). Scoped to a
/// single-vsys firewall's default rulebase for this first pass: "set vsys ...", "set device-group ..." and
/// "set shared ..." lines (Panorama / multi-vsys) are recognized but not parsed, and surfaced as review items
/// instead of silently ignored. NAT rules, VPN (IKE/IPSec) and virtual routers are likewise left for manual
/// review - PAN-OS models them as entirely separate rulebases with no equivalent in the shared object/policy
/// model this migrates.</summary>
public sealed record PanParseResult(
    List<AddressObject> Addresses,
    List<ServiceObject> Services,
    List<PolicyRule> Rules,
    IReadOnlyList<string> InterfacesFound,
    IReadOnlyList<string> ReviewItems,
    IReadOnlyList<ParseIssue> Issues);

public static class PaloAltoConfigParser
{
    private sealed class RuleBuilder
    {
        public List<string> From = [], To = [], Source = [], Destination = [], Service = [], Application = [];
        public string Action = "deny", Comment = "", Schedule = "";
        public bool LogEnd, Disabled;
    }

    public static PanParseResult Parse(string config)
    {
        var lines = TextLines.Split(config);
        var issues = new List<ParseIssue>();
        var reviewItems = new List<string>();

        var addrValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addrComment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addrOrder = new List<string>();
        var svcSpec = new Dictionary<string, (string Proto, string Port)>(StringComparer.OrdinalIgnoreCase);
        var svcOrder = new List<string>();

        var rules = new Dictionary<string, RuleBuilder>(StringComparer.OrdinalIgnoreCase);
        var ruleOrder = new List<string>();
        var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            var t = Tokenize(lines[lineNo]);
            if (t.Count == 0 || !t[0].Equals("set", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Count < 2) continue;

            int idx = 1;
            if (t[idx] is "vsys" or "device-group" or "shared")
            {
                if (t[idx] != "shared" || t.Count <= idx + 1 || !(t[idx + 1] is "address" or "address-group" or "service" or "service-group"))
                {
                    reviewItems.Add($"Line {lineNo + 1}: scoped to {t[idx]} (Panorama / multi-vsys) - only the default single-vsys rulebase is migrated in this pass, recreate this line's object/rule manually.");
                    continue;
                }
                idx += 1; // "set shared address ..." - still a plain object, just shared scope; treat the rest normally
            }

            switch (t[idx])
            {
                case "address" when t.Count > idx + 1:
                    ParseAddressLine(t, idx + 1, addrValue, addrComment, addrOrder, lineNo, issues);
                    break;
                case "address-group":
                    reviewItems.Add($"Line {lineNo + 1}: address-group '{(t.Count > idx + 1 ? t[idx + 1] : "?")}' - groups aren't recreated as named groups; references to it in a rule are flattened to its members' names instead."); break;
                case "service" when t.Count > idx + 1:
                    ParseServiceLine(t, idx + 1, svcSpec, svcOrder, lineNo, issues);
                    break;
                case "service-group":
                    reviewItems.Add($"Line {lineNo + 1}: service-group '{(t.Count > idx + 1 ? t[idx + 1] : "?")}' - groups aren't recreated as named groups; references to it in a rule are passed through by name."); break;
                case "rulebase" when t.Count > idx + 3 && t[idx + 1] == "security" && t[idx + 2] == "rules":
                    ParseRuleLine(t, idx + 3, rules, ruleOrder, interfaces);
                    break;
                case "zone":
                    break; // zone/interface bindings are informational only here - the rule's own from/to already carries the zone name
                case "nat" or "vpn-ipsec" or "vpn-ike" or "network" when t.Count > idx + 1 && t[idx + 1] is "tunnel" or "ike" or "ipsec-crypto-profile":
                    reviewItems.Add($"Line {lineNo + 1}: NAT/VPN configuration - not migrated; recreate on the target firewall.");
                    break;
                case "network" when t.Count > idx + 1 && t[idx + 1] == "virtual-router":
                    reviewItems.Add($"Line {lineNo + 1}: routing (virtual-router) configuration - not migrated; recreate routes on the target firewall.");
                    break;
            }
        }

        var addresses = new List<AddressObject>();
        foreach (var name in addrOrder)
        {
            var value = addrValue[name];
            var obj = AddressListParser.TryParseValue(value, addrComment.GetValueOrDefault(name, ""), out var error);
            if (obj is null) issues.Add(new ParseIssue(0, $"{name} = {value}", $"address '{name}': {error}"));
            else addresses.Add(obj with { Name = name });
        }

        var services = new List<ServiceObject>();
        foreach (var name in svcOrder)
        {
            var (proto, port) = svcSpec[name];
            var kind = proto.Equals("udp", StringComparison.OrdinalIgnoreCase) ? ServiceProtocol.Udp : ServiceProtocol.Tcp;
            services.Add(new ServiceObject(name, kind, port));
        }

        var policyRules = new List<PolicyRule>();
        foreach (var name in ruleOrder)
        {
            var b = rules[name];
            policyRules.Add(new PolicyRule
            {
                Name = name,
                Action = b.Action switch { "allow" => PolicyAction.Allow, "drop" => PolicyAction.Drop, "reset-server" or "reset-client" or "reset-both" => PolicyAction.Reject, _ => PolicyAction.Deny },
                SourceInterface = b.From.FirstOrDefault(f => !f.Equals("any", StringComparison.OrdinalIgnoreCase)) ?? "",
                DestinationInterface = b.To.FirstOrDefault(f => !f.Equals("any", StringComparison.OrdinalIgnoreCase)) ?? "",
                Sources = b.Source.Count > 0 ? b.Source : ["any"],
                Destinations = b.Destination.Count > 0 ? b.Destination : ["any"],
                Services = ResolveServices(b),
                Applications = b.Application.Where(a => !a.Equals("any", StringComparison.OrdinalIgnoreCase)).ToList(),
                Schedule = b.Schedule,
                Log = b.LogEnd,
                Comment = b.Comment,
                Enabled = !b.Disabled,
            });
        }

        return new PanParseResult(addresses, services, policyRules, interfaces.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(), reviewItems, issues);
    }

    private static List<string> ResolveServices(RuleBuilder b)
    {
        if (b.Service.Count == 0) return ["any"];
        if (b.Service.Any(s => s.Equals("any", StringComparison.OrdinalIgnoreCase))) return ["any"];
        if (b.Service.Any(s => s.Equals("application-default", StringComparison.OrdinalIgnoreCase)))
            return b.Application.Count > 0 ? b.Application : ["any"]; // App-ID governs the actual port on PAN-OS; pass the app name(s) through
        return b.Service; // named service object references - the matching ServiceObject is emitted separately
    }

    private static void ParseAddressLine(List<string> t, int idx, Dictionary<string, string> value, Dictionary<string, string> comment, List<string> order, int lineNo, List<ParseIssue> issues)
    {
        var name = t[idx];
        if (!order.Contains(name)) order.Add(name);
        if (t.Count <= idx + 1) return;
        switch (t[idx + 1])
        {
            case "ip-netmask" when t.Count > idx + 2: value[name] = t[idx + 2]; break;
            case "ip-range" when t.Count > idx + 2: value[name] = t[idx + 2]; break;
            case "fqdn" when t.Count > idx + 2: value[name] = t[idx + 2]; break;
            case "description" when t.Count > idx + 2: comment[name] = t[idx + 2]; break;
            case "tag": break; // not modeled
            default: issues.Add(new ParseIssue(lineNo + 1, string.Join(' ', t), $"address '{name}': unrecognized attribute '{t[idx + 1]}'")); break;
        }
    }

    private static void ParseServiceLine(List<string> t, int idx, Dictionary<string, (string, string)> spec, List<string> order, int lineNo, List<ParseIssue> issues)
    {
        var name = t[idx];
        // "set service NAME protocol tcp port 8443"
        if (t.Count > idx + 4 && t[idx + 1] == "protocol" && t[idx + 3] == "port")
        {
            spec[name] = (t[idx + 2], t[idx + 4]);
            if (!order.Contains(name)) order.Add(name);
        }
        else if (t.Count > idx + 1 && t[idx + 1] == "description") { /* description on a service object - not modeled, harmless to skip */ }
        else issues.Add(new ParseIssue(lineNo + 1, string.Join(' ', t), $"service '{name}': expected 'protocol tcp|udp port <n>' - skipped"));
    }

    private static void ParseRuleLine(List<string> t, int idx, Dictionary<string, RuleBuilder> rules, List<string> order, HashSet<string> interfaces)
    {
        var name = t[idx];
        if (!rules.TryGetValue(name, out var b)) { b = new RuleBuilder(); rules[name] = b; order.Add(name); }
        if (t.Count <= idx + 1) return;
        var field = t[idx + 1];
        var rest = CollectList(t, idx + 2);
        // The PAN-OS "set" CLI appends to a list-type field on repeated invocations rather than replacing it, and
        // real device output sometimes emits multiple "source"/"service"/etc. lines for one rule instead of a
        // single bracketed list - so these accumulate (deduplicated) across every line seen for this rule.
        void Accumulate(List<string> target) { foreach (var v in rest) if (!target.Contains(v, StringComparer.OrdinalIgnoreCase)) target.Add(v); }
        switch (field)
        {
            case "from": Accumulate(b.From); foreach (var z in rest.Where(z => !z.Equals("any", StringComparison.OrdinalIgnoreCase))) interfaces.Add(z); break;
            case "to": Accumulate(b.To); foreach (var z in rest.Where(z => !z.Equals("any", StringComparison.OrdinalIgnoreCase))) interfaces.Add(z); break;
            case "source": Accumulate(b.Source); break;
            case "destination": Accumulate(b.Destination); break;
            case "service": Accumulate(b.Service); break;
            case "application": Accumulate(b.Application); break;
            case "action" when rest.Count > 0: b.Action = rest[0]; break;
            case "log-end" when rest.Count > 0: b.LogEnd = rest[0].Equals("yes", StringComparison.OrdinalIgnoreCase); break;
            case "disabled" when rest.Count > 0: b.Disabled = rest[0].Equals("yes", StringComparison.OrdinalIgnoreCase); break;
            case "description" when rest.Count > 0: b.Comment = rest[0]; break;
            case "schedule" when rest.Count > 0: b.Schedule = rest[0]; break;
        }
    }

    private static List<string> CollectList(List<string> t, int idx)
    {
        if (idx >= t.Count) return [];
        if (t[idx] == "[")
        {
            var items = new List<string>();
            int i = idx + 1;
            while (i < t.Count && t[i] != "]") { items.Add(t[i]); i++; }
            return items;
        }
        return [t[idx]];
    }

    /// <summary>Whitespace-splits, treating a double-quoted run as one token (dequoted) - PAN-OS quotes any value
    /// containing a space (names, descriptions), and always space-pads "[ ... ]" list brackets.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
