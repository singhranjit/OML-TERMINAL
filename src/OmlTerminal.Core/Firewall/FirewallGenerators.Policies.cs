using System.Text;
using System.Text.Json.Nodes;

namespace OmlTerminal.Core.Firewall;

public sealed class PolicyOptions
{
    /// <summary>Prepended to every object the builder creates from a literal (IP, FQDN, tcp/443...).</summary>
    public string ObjectPrefix { get; init; } = "";
    public bool IncludeRollback { get; init; }

    // FortiGate
    public string Vdom { get; init; } = "";
    /// <summary>First policy ID to use; 0 lets FortiOS pick ("edit 0"), which also means rollback can't delete by ID.</summary>
    public int FortiStartId { get; init; }

    // Palo Alto: "" = single-vsys firewall, "vsys:vsys2", "shared", "dg:Branches" (Panorama pre-rulebase)
    public string PanScope { get; init; } = "";

    // Check Point
    public string CheckPointLayer { get; init; } = "Network";
    public string CheckPointPosition { get; init; } = "bottom";

    // Cisco ASA: ACL named <SourceInterface><suffix>, bound with access-group ... in interface <SourceInterface>
    public string AsaAclSuffix { get; init; } = "_access_in";
    public bool AsaAccessGroup { get; init; } = true;
}

public static partial class FirewallGenerators
{
    private sealed record AddrRef(bool Any, string Name, AddressObject? Literal);
    private sealed record SvcRef(bool Any, string Name, ServiceObject? Literal);
    private sealed record ResolvedRule(PolicyRule Rule, List<AddrRef> Src, List<AddrRef> Dst, List<SvcRef> Svc)
    {
        public bool SrcAny => Src.Count == 0 || Src.Any(a => a.Any);
        public bool DstAny => Dst.Count == 0 || Dst.Any(a => a.Any);
        public bool SvcAny => Svc.Count == 0 || Svc.Any(s => s.Any);
        public bool HasV6 => Src.Concat(Dst).Any(a => a.Literal?.IsV6 == true);
    }

    private static bool IsAlways(string schedule) => schedule.Length == 0 || schedule.Equals("always", StringComparison.OrdinalIgnoreCase);

    public static GeneratedConfig Policies(FirewallVendor vendor, IReadOnlyList<PolicyRule> rules, PolicyOptions o)
    {
        var warnings = new List<string>();
        var addrs = new Dictionary<string, AddressObject>(StringComparer.OrdinalIgnoreCase);
        var svcs = new Dictionary<string, ServiceObject>(StringComparer.OrdinalIgnoreCase);
        var naming = new NamingOptions { Style = NamingStyle.Typed, Prefix = o.ObjectPrefix };

        AddrRef ResolveAddr(string token)
        {
            if (PolicySheet.IsAny(token)) return new AddrRef(true, "", null);
            var lit = AddressListParser.TryParseValue(token, "", out _);
            if (lit is null) return new AddrRef(false, SafeName(token, vendor), null); // an object that already exists on the firewall
            lit = lit with { Name = SafeName(AddressListParser.AutoName(lit, naming), vendor) };
            if (!addrs.TryGetValue(lit.Name, out var existing)) addrs[lit.Name] = existing = lit;
            return new AddrRef(false, ReferenceName(vendor, existing), existing);
        }

        SvcRef ResolveSvc(string token)
        {
            if (PolicySheet.IsAny(token)) return new SvcRef(true, "", null);
            if (!ServiceListParser.TryParse(token, out var lit, out _)) return new SvcRef(false, SafeName(token, vendor), null);
            lit = lit with { Name = SafeName(o.ObjectPrefix + ServiceListParser.AutoName(lit), vendor) };
            if (!svcs.TryGetValue(lit.Name, out var existing)) svcs[lit.Name] = existing = lit;
            return new SvcRef(false, existing.Name, existing);
        }

        var resolved = new List<ResolvedRule>();
        foreach (var r in rules)
        {
            var rr = new ResolvedRule(r, r.Sources.Select(ResolveAddr).ToList(), r.Destinations.Select(ResolveAddr).ToList(), r.Services.Select(ResolveSvc).ToList());
            if (WithoutUnmatchableWildcards(vendor, rr, warnings) is { } usable) resolved.Add(usable);
        }

        if (rules.Any(r => r.Nat) && vendor != FirewallVendor.FortiGate)
            warnings.Add($"NAT = yes only applies to FortiGate policies; configure NAT separately on {FirewallVendors.DisplayName(vendor)}.");
        if (rules.Any(r => r.Applications.Count > 0) && vendor != FirewallVendor.PaloAlto)
            warnings.Add("The Application column is Palo Alto App-ID and was ignored for this vendor.");

        var result = vendor switch
        {
            FirewallVendor.FortiGate => FortiPolicies(resolved, addrs.Values.ToList(), svcs.Values.ToList(), o, warnings),
            FirewallVendor.PaloAlto => PanPolicies(resolved, addrs.Values.ToList(), svcs.Values.ToList(), o, warnings),
            FirewallVendor.CiscoAsa => AsaPolicies(resolved, addrs.Values.ToList(), svcs.Values.ToList(), o, warnings),
            FirewallVendor.CheckPoint => CheckPointPolicies(resolved, addrs.Values.ToList(), svcs.Values.ToList(), o, warnings),
            FirewallVendor.CiscoFtd => FtdPolicies(resolved, addrs.Values.ToList(), o, warnings),
            FirewallVendor.JuniperSrx => JunosPolicies(resolved, addrs.Values.ToList(), svcs.Values.ToList(), o, warnings),
            FirewallVendor.Sophos => SophosPolicies(resolved, addrs.Values.ToList(), svcs.Values.ToList(), o, warnings),
            _ => PfSensePolicies(resolved, addrs.Values.ToList(), o, warnings),
        };
        return result with { Warnings = warnings.Concat(result.Warnings).Distinct().ToList() };
    }

    private static bool IsWildcard(AddrRef a) => a.Literal?.Kind == AddressKind.WildcardFqdn;

    /// <summary>
    /// Vendors whose address objects can't hold "*.domain" lose those entries. Dropping them from a rule that still has
    /// other entries narrows it, which is safe; dropping the *only* entry would leave that side empty, i.e. "any", and
    /// silently widen the rule - so such rules are skipped entirely. Palo Alto keeps destination wildcards: they become
    /// the rule's URL-category match (see PanPolicies).
    /// </summary>
    private static ResolvedRule? WithoutUnmatchableWildcards(FirewallVendor vendor, ResolvedRule r, List<string> warnings)
    {
        if (vendor is FirewallVendor.FortiGate or FirewallVendor.CheckPoint or FirewallVendor.Sophos) return r;
        if (!r.Src.Any(IsWildcard) && !r.Dst.Any(IsWildcard)) return r;
        bool keepDst = vendor == FirewallVendor.PaloAlto;
        var src = r.Src.Where(a => !IsWildcard(a)).ToList();
        var dst = keepDst ? r.Dst : r.Dst.Where(a => !IsWildcard(a)).ToList();
        var name = FirewallVendors.DisplayName(vendor);
        if ((r.Src.Count > 0 && src.Count == 0) || (dst.Count == 0 && r.Dst.Count > 0))
        {
            warnings.Add($"'{r.Rule.Name}' skipped: {name} can't match wildcard FQDNs there, and dropping them would widen the rule to 'any'.");
            return null;
        }
        if (src.Count < r.Src.Count || dst.Count < r.Dst.Count)
            warnings.Add($"'{r.Rule.Name}': wildcard FQDN entries dropped - {name} can't match them{(keepDst ? " as a source" : "")}.");
        return r with { Src = src, Dst = dst };
    }

    /// <summary>Objects section via the object builders, without a group (rules reference the objects directly).</summary>
    private static (GeneratedConfig Addr, GeneratedConfig Svc) Objects(FirewallVendor v, List<AddressObject> addrs, List<ServiceObject> svcs, GeneratorOptions o) =>
        (Addresses(v, addrs, o), Services(v, svcs, o));

    /// <summary>Service names a rule references: TCP+UDP literals become two objects on vendors without a combined type.</summary>
    private static IEnumerable<string> SvcNames(FirewallVendor v, SvcRef s)
    {
        if (s.Literal is not { } lit) return [s.Name];
        if (lit.Protocol == ServiceProtocol.TcpUdp && v is FirewallVendor.PaloAlto or FirewallVendor.CheckPoint or FirewallVendor.JuniperSrx)
            return SplitTcpUdp(lit, v).Select(x => x.Name);
        return [lit.Name];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------- FortiGate

    private static GeneratedConfig FortiPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, List<ServiceObject> svcs, PolicyOptions o, List<string> warnings)
    {
        var (objA, objS) = Objects(FirewallVendor.FortiGate, addrs, svcs, new GeneratorOptions { IncludeRollback = o.IncludeRollback });
        var w = new FortiWriter(o.Vdom);
        var rb = new FortiWriter(o.Vdom);
        w.Line("config firewall policy");
        int id = o.FortiStartId;
        var ids = new List<int>();
        foreach (var r in rules)
        {
            var rule = r.Rule;
            (List<string> V4, List<string> V6) Side(List<AddrRef> refs, bool any)
            {
                if (any) return (["all"], r.HasV6 ? ["all"] : []);
                return (refs.Where(a => a.Literal?.IsV6 != true).Select(a => a.Name).ToList(), refs.Where(a => a.Literal?.IsV6 == true).Select(a => a.Name).ToList());
            }
            var (s4, s6) = Side(r.Src, r.SrcAny);
            var (d4, d6) = Side(r.Dst, r.DstAny);

            w.Line($"    edit {(o.FortiStartId > 0 ? id : 0)}");
            if (o.FortiStartId > 0) ids.Add(id++);
            if (rule.Name.Length > 35) warnings.Add($"'{rule.Name}': FortiOS policy names are limited to 35 characters - truncated.");
            w.Line($"        set name {Q(Truncate(rule.Name, 35))}");
            w.Line($"        set srcintf {Q(rule.SourceInterface.Length > 0 ? rule.SourceInterface : "any")}");
            w.Line($"        set dstintf {Q(rule.DestinationInterface.Length > 0 ? rule.DestinationInterface : "any")}");
            w.Line($"        set action {(rule.Action == PolicyAction.Allow ? "accept" : "deny")}");
            if (s4.Count > 0) w.Line($"        set srcaddr {string.Join(' ', s4.Select(Q))}");
            if (d4.Count > 0) w.Line($"        set dstaddr {string.Join(' ', d4.Select(Q))}");
            if (s6.Count > 0) w.Line($"        set srcaddr6 {string.Join(' ', s6.Select(Q))}");
            if (d6.Count > 0) w.Line($"        set dstaddr6 {string.Join(' ', d6.Select(Q))}");
            w.Line($"        set schedule {Q(rule.Schedule.Length > 0 ? rule.Schedule : "always")}");
            w.Line($"        set service {(r.SvcAny ? Q("ALL") : string.Join(' ', r.Svc.Select(s => Q(s.Name))))}");
            w.Line($"        set logtraffic {(rule.Log ? "all" : "disable")}");
            if (rule.Nat && rule.Action == PolicyAction.Allow) w.Line("        set nat enable");
            if (rule.Action == PolicyAction.Reject) w.Line("        set send-deny-packet enable");
            if (rule.Comment.Length > 0) w.Line($"        set comments {Q(rule.Comment)}");
            if (!rule.Enabled) w.Line("        set status disable");
            w.Line("    next");
        }
        w.Line("end");

        string rollback = "";
        if (o.IncludeRollback)
        {
            if (ids.Count > 0)
            {
                rb.Line("config firewall policy");
                foreach (var i in ids) rb.Line($"    delete {i}");
                rb.Line("end");
                rollback = rb.ToString();
            }
            else
            {
                rollback = "# Policies were created with \"edit 0\" (next free ID), so delete them by name in the GUI or set a start ID:\n"
                           + string.Join('\n', rules.Select(r => $"#   {Truncate(r.Rule.Name, 35)}")) + "\n";
            }
            rollback += objS.Rollback + objA.Rollback;
        }
        return new GeneratedConfig(Section("Objects", objA.Script + objS.Script) + Section("Policies", w.ToString()), rollback, objA.Warnings.Concat(objS.Warnings).ToList());
    }

    private static string Section(string title, string body, string commentPrefix = "#") =>
        body.Trim().Length == 0 ? "" : $"{commentPrefix} ---- {title} ----\n{body.TrimEnd()}\n\n";

    // ---------- Palo Alto

    public static string PanRulebase(string scope)
    {
        scope = scope.Trim();
        if (scope.StartsWith("vsys:", StringComparison.OrdinalIgnoreCase)) return $"set vsys {scope[5..].Trim()} rulebase security rules";
        if (scope.StartsWith("dg:", StringComparison.OrdinalIgnoreCase)) return $"set device-group {PanQuote(scope[3..].Trim())} pre-rulebase security rules";
        if (scope.Equals("shared", StringComparison.OrdinalIgnoreCase)) return "set shared pre-rulebase security rules";
        return "set rulebase security rules";
    }

    private static string PanList(IReadOnlyList<string> items) =>
        items.Count == 0 ? "any" : items.Count == 1 ? PanQuote(items[0]) : $"[ {string.Join(' ', items.Select(PanQuote))} ]";

    private static GeneratedConfig PanPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, List<ServiceObject> svcs, PolicyOptions o, List<string> warnings)
    {
        var scope = o.PanScope.StartsWith("dg:", StringComparison.OrdinalIgnoreCase) || o.PanScope == "shared" || o.PanScope.StartsWith("vsys:") ? o.PanScope : "";
        var (objA, objS) = Objects(FirewallVendor.PaloAlto, addrs, svcs, new GeneratorOptions { IncludeRollback = o.IncludeRollback, PanScope = scope });
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var rulebase = PanRulebase(o.PanScope);
        foreach (var r in rules)
        {
            var rule = r.Rule;
            var name = PanQuote(Truncate(rule.Name, 63));
            var p = $"{rulebase} {name}";
            var apps = rule.Applications.Where(a => !PolicySheet.IsAny(a)).ToList();
            var services = r.SvcAny
                ? (apps.Count > 0 ? "application-default" : "any")
                : PanList(r.Svc.Where(s => s.Literal?.Protocol != ServiceProtocol.Icmp).SelectMany(s => SvcNames(FirewallVendor.PaloAlto, s)).ToList());
            if (r.Svc.Any(s => s.Literal?.Protocol == ServiceProtocol.Icmp))
            {
                apps.Add("icmp");
                warnings.Add($"'{rule.Name}': ICMP is matched by App-ID on PAN-OS - added application 'icmp'.");
                if (services == "any") services = "application-default"; // the row had only ICMP

            }
            sb.AppendLine($"{p} from {PanList(Zone(rule.SourceInterface))}");
            sb.AppendLine($"{p} to {PanList(Zone(rule.DestinationInterface))}");
            sb.AppendLine($"{p} source {(r.SrcAny ? "any" : PanList(r.Src.Select(a => a.Name).ToList()))}");
            // Wildcard FQDNs can't be address objects on PAN-OS; PanAddresses put them in a custom URL category, which
            // the rule matches through its "category" field instead of "destination".
            var dstAddrs = r.Dst.Where(a => !IsWildcard(a)).Select(a => a.Name).ToList();
            bool urlCategory = r.Dst.Any(IsWildcard);
            sb.AppendLine($"{p} destination {(r.DstAny || dstAddrs.Count == 0 ? "any" : PanList(dstAddrs))}");
            sb.AppendLine($"{p} source-user any");
            sb.AppendLine($"{p} category {(urlCategory ? PanQuote(PanWildcardCategory("")) : "any")}");
            sb.AppendLine($"{p} application {PanList(apps)}");
            sb.AppendLine($"{p} service {services}");
            sb.AppendLine($"{p} action {rule.Action switch { PolicyAction.Allow => "allow", PolicyAction.Drop => "drop", PolicyAction.Reject => "reset-both", _ => "deny" }}");
            sb.AppendLine($"{p} log-end {(rule.Log ? "yes" : "no")}");
            if (!IsAlways(rule.Schedule)) sb.AppendLine($"{p} schedule {PanQuote(rule.Schedule)}");
            if (rule.Comment.Length > 0) sb.AppendLine($"{p} description {Q(rule.Comment)}");
            if (!rule.Enabled) sb.AppendLine($"{p} disabled yes");
            rb.AppendLine($"{p.Replace("set ", "delete ", StringComparison.Ordinal)}");
        }
        var rollback = o.IncludeRollback ? rb + objS.Rollback + objA.Rollback : "";
        return new GeneratedConfig(Section("Objects", objA.Script + objS.Script) + Section("Security rules", sb.ToString()) + "# commit\n",
            rollback, objA.Warnings.Concat(objS.Warnings).ToList());
    }

    private static List<string> Zone(string z) => z.Length == 0 || PolicySheet.IsAny(z) ? [] : [z];

    // ---------- Cisco ASA

    private static GeneratedConfig AsaPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, List<ServiceObject> svcs, PolicyOptions o, List<string> warnings)
    {
        var (objA, objS) = Objects(FirewallVendor.CiscoAsa, addrs, svcs, new GeneratorOptions { IncludeRollback = o.IncludeRollback });
        var groups = new StringBuilder();
        var acl = new StringBuilder();
        var rb = new StringBuilder();
        var acls = new List<(string Acl, string Interface)>();
        foreach (var r in rules)
        {
            var rule = r.Rule;
            var tag = SafeName(rule.Name, FirewallVendor.CiscoAsa);
            var iface = rule.SourceInterface.Length > 0 && !PolicySheet.IsAny(rule.SourceInterface) ? rule.SourceInterface : "outside";
            if (rule.SourceInterface.Length == 0 || PolicySheet.IsAny(rule.SourceInterface))
                warnings.Add($"'{rule.Name}': no source interface - put it on the 'outside' ACL. ASA ACLs are applied per ingress interface.");
            var aclName = SafeName(iface + o.AsaAclSuffix, FirewallVendor.CiscoAsa);
            if (!acls.Any(a => a.Acl == aclName)) acls.Add((aclName, iface));

            string Net(List<AddrRef> refs, bool any, string suffix)
            {
                if (any) return "any";
                if (refs.Count == 1) return $"object {refs[0].Name}";
                var g = $"{tag}_{suffix}";
                groups.AppendLine($"object-group network {g}");
                foreach (var a in refs) groups.AppendLine($" network-object object {a.Name}");
                rb.Insert(0, $"no object-group network {g}\n");
                return $"object-group {g}";
            }
            var src = Net(r.Src, r.SrcAny, "SRC");
            var dst = Net(r.Dst, r.DstAny, "DST");
            string svc;
            if (r.SvcAny) svc = "ip";
            else if (r.Svc.Count == 1) svc = $"object {r.Svc[0].Name}";
            else
            {
                var g = $"{tag}_SVC";
                groups.AppendLine($"object-group service {g}");
                foreach (var s in r.Svc) groups.AppendLine($" service-object object {s.Name}");
                rb.Insert(0, $"no object-group service {g}\n");
                svc = $"object-group {g}";
            }
            var verb = rule.Action == PolicyAction.Allow ? "permit" : "deny";
            var tail = (rule.Log ? " log" : "") + (IsAlways(rule.Schedule) ? "" : $" time-range {rule.Schedule}") + (rule.Enabled ? "" : " inactive");
            var remark = rule.Comment.Length > 0 ? $"{rule.Name} - {rule.Comment}" : rule.Name;
            acl.AppendLine($"access-list {aclName} remark {remark}");
            var ace = $"access-list {aclName} extended {verb} {svc} {src} {dst}{tail}";
            acl.AppendLine(ace);
            rb.Insert(0, $"no {ace}\nno access-list {aclName} remark {remark}\n");
        }
        if (o.AsaAccessGroup)
            foreach (var (a, i) in acls) acl.AppendLine($"access-group {a} in interface {i}");
        warnings.Add("Existing object names are referenced as 'object <name>'; if one is an object-group, change it to 'object-group <name>'.");
        var rollback = o.IncludeRollback ? rb + objS.Rollback + objA.Rollback : "";
        return new GeneratedConfig(Section("Objects", objA.Script + objS.Script + groups, "!") + Section("Access lists", acl.ToString(), "!"),
            rollback, objA.Warnings.Concat(objS.Warnings).ToList());
    }

    // ---------- Check Point

    private static GeneratedConfig CheckPointPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, List<ServiceObject> svcs, PolicyOptions o, List<string> warnings)
    {
        var go = new GeneratorOptions { IncludeRollback = o.IncludeRollback };
        var objA = CheckPointAddresses(addrs, go, wrap: false);
        var objS = CheckPointServices(svcs, go, wrap: false);
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var layer = o.CheckPointLayer.Length > 0 ? o.CheckPointLayer : "Network";
        if (rules.Any(r => r.Rule.SourceInterface.Length > 0 || r.Rule.DestinationInterface.Length > 0))
            warnings.Add("Check Point access rules have no interface columns; the interface cells were ignored (use access layers or security zones).");

        string Members(string field, IEnumerable<string> names) => string.Join(' ', names.Select((n, i) => $"{field}.{i + 1} {Q(n)}"));
        foreach (var r in rules)
        {
            var rule = r.Rule;
            var svcNames = r.SvcAny ? ["Any"] : r.Svc.SelectMany(s => s.Literal?.Protocol == ServiceProtocol.Icmp ? ["icmp-proto"] : SvcNames(FirewallVendor.CheckPoint, s)).ToList();
            var cmd = new StringBuilder($"add access-rule layer {Q(layer)} position {Q(o.CheckPointPosition)} name {Q(rule.Name)} ");
            cmd.Append(Members("source", r.SrcAny ? ["Any"] : r.Src.Select(a => a.Name))).Append(' ');
            cmd.Append(Members("destination", r.DstAny ? ["Any"] : r.Dst.Select(a => a.Name))).Append(' ');
            cmd.Append(Members("service", svcNames)).Append(' ');
            cmd.Append($"action {Q(rule.Action switch { PolicyAction.Allow => "Accept", PolicyAction.Reject => "Reject", _ => "Drop" })} ");
            cmd.Append($"track.type {Q(rule.Log ? "Log" : "None")}");
            if (!IsAlways(rule.Schedule)) cmd.Append($" time.1 {Q(rule.Schedule)}");
            if (rule.Comment.Length > 0) cmd.Append($" comments {Q(rule.Comment)}");
            if (!rule.Enabled) cmd.Append(" enabled false");
            sb.AppendLine(Cp(cmd.ToString()));
            rb.AppendLine(Cp($"delete access-rule layer {Q(layer)} name {Q(rule.Name)}"));
        }
        var script = CheckPointHeader() + Section("Objects", objA.Script + objS.Script) + Section("Access rules", sb.ToString()) + CheckPointFooter();
        var rollback = "";
        if (o.IncludeRollback)
        {
            // The object rollbacks come with their own login/publish; strip those so the whole undo runs in one session.
            string Body(string s) => string.Join('\n', s.Split('\n').Where(l => l.StartsWith($"mgmt_cli -s {CheckPointSession} delete"))) + "\n";
            rollback = CheckPointHeader() + rb + Body(objS.Rollback) + Body(objA.Rollback) + CheckPointFooter();
        }
        return new GeneratedConfig(script, rollback, objA.Warnings.Concat(objS.Warnings).ToList());
    }

    // ---------- Cisco Firepower (FMC)

    private static GeneratedConfig FtdPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, PolicyOptions o, List<string> warnings)
    {
        // Host/network/range values go into the rules as literals (no object UUIDs needed); FQDNs must be objects.
        var fqdns = addrs.Where(a => a.Kind == AddressKind.Fqdn).ToList();
        var objA = FtdAddresses(fqdns, new GeneratorOptions());
        var arr = new JsonArray();
        bool placeholders = false;

        JsonObject? Networks(List<AddrRef> refs, bool any)
        {
            if (any) return null;
            var literals = new JsonArray();
            var objects = new JsonArray();
            foreach (var a in refs)
            {
                if (a.Literal is { Kind: AddressKind.Host or AddressKind.Subnet or AddressKind.Range } lit) literals.Add(FmcLiteral(lit));
                else if (a.Literal?.Kind == AddressKind.WildcardFqdn) warnings.Add($"'{a.Literal.Value}': FMC can't match wildcard FQDNs in access rules - skipped.");
                else { objects.Add(new JsonObject { ["name"] = a.Name, ["type"] = a.Literal is null ? "Network" : "FQDN", ["id"] = $"<id of {a.Name}>" }); placeholders = true; }
            }
            var node = new JsonObject();
            if (literals.Count > 0) node["literals"] = literals;
            if (objects.Count > 0) node["objects"] = objects;
            return node;
        }

        JsonObject Zones(string zone)
        {
            placeholders = true;
            return new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["name"] = zone, ["type"] = "SecurityZone", ["id"] = $"<id of zone {zone}>" }) };
        }

        foreach (var r in rules)
        {
            var rule = r.Rule;
            var node = new JsonObject
            {
                ["name"] = Truncate(rule.Name, 30),
                ["type"] = "AccessRule",
                ["action"] = rule.Action switch { PolicyAction.Allow => "ALLOW", PolicyAction.Reject => "BLOCK_RESET", _ => "BLOCK" },
                ["enabled"] = rule.Enabled,
                ["logEnd"] = rule.Log && rule.Action == PolicyAction.Allow,
                ["logBegin"] = rule.Log && rule.Action != PolicyAction.Allow,
                ["sendEventsToFMC"] = rule.Log,
            };
            if (Zone(rule.SourceInterface).Count > 0) node["sourceZones"] = Zones(rule.SourceInterface);
            if (Zone(rule.DestinationInterface).Count > 0) node["destinationZones"] = Zones(rule.DestinationInterface);
            if (Networks(r.Src, r.SrcAny) is { } src) node["sourceNetworks"] = src;
            if (Networks(r.Dst, r.DstAny) is { } dst) node["destinationNetworks"] = dst;
            if (!r.SvcAny)
            {
                var literals = new JsonArray();
                var objects = new JsonArray();
                foreach (var s in r.Svc)
                {
                    if (s.Literal is not { } lit)
                    {
                        objects.Add(new JsonObject { ["name"] = s.Name, ["type"] = "ProtocolPortObject", ["id"] = $"<id of {s.Name}>" });
                        placeholders = true;
                        continue;
                    }
                    switch (lit.Protocol)
                    {
                        case ServiceProtocol.Icmp:
                            literals.Add(new JsonObject { ["type"] = "ICMPv4PortLiteral", ["protocol"] = "1", ["icmpType"] = "Any" });
                            break;
                        case ServiceProtocol.TcpUdp:
                            literals.Add(new JsonObject { ["type"] = "PortLiteral", ["port"] = lit.DestinationPorts, ["protocol"] = "6" });
                            literals.Add(new JsonObject { ["type"] = "PortLiteral", ["port"] = lit.DestinationPorts, ["protocol"] = "17" });
                            break;
                        default:
                            literals.Add(new JsonObject
                            {
                                ["type"] = "PortLiteral", ["port"] = lit.DestinationPorts,
                                ["protocol"] = lit.Protocol switch { ServiceProtocol.Udp => "17", ServiceProtocol.Sctp => "132", _ => "6" },
                            });
                            break;
                    }
                }
                var ports = new JsonObject();
                if (literals.Count > 0) ports["literals"] = literals;
                if (objects.Count > 0) ports["objects"] = objects;
                node["destinationPorts"] = ports;
            }
            if (rule.Comment.Length > 0) node["newComments"] = new JsonArray(JsonValue.Create(rule.Comment));
            arr.Add(node);
        }
        if (placeholders) warnings.Add("Replace every <id of ...> with the object's UUID (GET the zone/object endpoint in FMC API Explorer) before posting.");
        if (rules.Any(r => r.Rule.Name.Length > 30)) warnings.Add("FMC rule names are limited to 30 characters - long names were truncated.");

        var sb = new StringBuilder(objA.Script);
        FmcBlock(sb, "Access rules (appended to the policy's default section)", "/policy/accesspolicies/{containerUUID}/accessrules?bulk=true", arr);
        var rollback = o.IncludeRollback
            ? "### Delete rules by UUID: GET .../accessrules?expanded=false, then DELETE .../accessrules/<id> for:\n" + string.Join('\n', rules.Select(r => $"#   {Truncate(r.Rule.Name, 30)}")) + "\n"
            : "";
        return new GeneratedConfig(sb.ToString(), rollback, objA.Warnings);
    }

    // ---------- Juniper SRX

    private static GeneratedConfig JunosPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, List<ServiceObject> svcs, PolicyOptions o, List<string> warnings)
    {
        var (objA, objS) = Objects(FirewallVendor.JuniperSrx, addrs, svcs, new GeneratorOptions { IncludeRollback = o.IncludeRollback });
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        foreach (var r in rules)
        {
            var rule = r.Rule;
            var name = SafeName(rule.Name, FirewallVendor.JuniperSrx);
            bool zoned = Zone(rule.SourceInterface).Count > 0 && Zone(rule.DestinationInterface).Count > 0;
            var path = zoned
                ? $"security policies from-zone {rule.SourceInterface} to-zone {rule.DestinationInterface} policy {name}"
                : $"security policies global policy {name}";
            var p = "set " + path;
            if (!zoned)
            {
                if (Zone(rule.SourceInterface).Count > 0) sb.AppendLine($"{p} match from-zone {rule.SourceInterface}");
                if (Zone(rule.DestinationInterface).Count > 0) sb.AppendLine($"{p} match to-zone {rule.DestinationInterface}");
            }
            foreach (var a in r.SrcAny ? ["any"] : r.Src.Select(x => x.Name)) sb.AppendLine($"{p} match source-address {a}");
            foreach (var a in r.DstAny ? ["any"] : r.Dst.Select(x => x.Name)) sb.AppendLine($"{p} match destination-address {a}");
            foreach (var s in r.SvcAny ? ["any"] : r.Svc.SelectMany(x => SvcNames(FirewallVendor.JuniperSrx, x))) sb.AppendLine($"{p} match application {s}");
            sb.AppendLine($"{p} then {rule.Action switch { PolicyAction.Allow => "permit", PolicyAction.Reject => "reject", _ => "deny" }}");
            if (rule.Log) sb.AppendLine($"{p} then log {(rule.Action == PolicyAction.Allow ? "session-close" : "session-init")}");
            if (!IsAlways(rule.Schedule)) sb.AppendLine($"{p} scheduler-name {rule.Schedule}");
            if (rule.Comment.Length > 0) sb.AppendLine($"{p} description {Q(rule.Comment)}");
            if (!rule.Enabled) sb.AppendLine($"deactivate {path}");
            rb.AppendLine($"delete {path}");
        }
        if (rules.Any(r => !(Zone(r.Rule.SourceInterface).Count > 0 && Zone(r.Rule.DestinationInterface).Count > 0)))
            warnings.Add("Rules without both zones became global policies (evaluated after zone-pair policies).");
        var rollback = o.IncludeRollback ? rb + objS.Rollback + objA.Rollback : "";
        return new GeneratedConfig(Section("Objects", objA.Script + objS.Script) + Section("Security policies", sb.ToString()) + "# commit check\n# commit\n",
            rollback, objA.Warnings.Concat(objS.Warnings).ToList());
    }

    // ---------- Sophos Firewall

    private static GeneratedConfig SophosPolicies(List<ResolvedRule> rules, List<AddressObject> addrs, List<ServiceObject> svcs, PolicyOptions o, List<string> warnings)
    {
        var (objA, objS) = Objects(FirewallVendor.Sophos, addrs, svcs, new GeneratorOptions { IncludeRollback = o.IncludeRollback });
        var body = new StringBuilder();
        var remove = new StringBuilder();
        foreach (var r in rules)
        {
            var rule = r.Rule;
            var name = SafeName(rule.Name, FirewallVendor.Sophos);
            if (r.Src.Concat(r.Dst).Any(a => a.Literal?.IsV6 == true) && r.Src.Concat(r.Dst).Any(a => a.Literal is { IsV6: false }))
                warnings.Add($"'{rule.Name}': mixes IPv4 and IPv6 literals - Sophos rules are one family; split the row.");
            string List(string outer, string inner, IEnumerable<string> items) =>
                $"<{outer}>{string.Concat(items.Select(i => $"<{inner}>{X(i)}</{inner}>"))}</{outer}>";
            var sb = new StringBuilder();
            sb.AppendLine("    <FirewallRule transactionid=\"\">");
            sb.AppendLine($"      <Name>{X(name)}</Name>");
            if (rule.Comment.Length > 0) sb.AppendLine($"      <Description>{X(rule.Comment)}</Description>");
            sb.AppendLine($"      <IPFamily>{(r.HasV6 ? "IPv6" : "IPv4")}</IPFamily>");
            sb.AppendLine($"      <Status>{(rule.Enabled ? "Enable" : "Disable")}</Status>");
            sb.AppendLine("      <Position>Bottom</Position>");
            sb.AppendLine("      <PolicyType>Network</PolicyType>");
            sb.AppendLine("      <NetworkPolicy>");
            sb.AppendLine($"        <Action>{rule.Action switch { PolicyAction.Allow => "Accept", PolicyAction.Reject => "Reject", _ => "Drop" }}</Action>");
            sb.AppendLine($"        <LogTraffic>{(rule.Log ? "Enable" : "Disable")}</LogTraffic>");
            sb.AppendLine("        <SkipLocalDestined>Disable</SkipLocalDestined>");
            if (Zone(rule.SourceInterface).Count > 0) sb.AppendLine("        " + List("SourceZones", "Zone", [rule.SourceInterface]));
            if (Zone(rule.DestinationInterface).Count > 0) sb.AppendLine("        " + List("DestinationZones", "Zone", [rule.DestinationInterface]));
            sb.AppendLine($"        <Schedule>{X(IsAlways(rule.Schedule) ? "All The Time" : rule.Schedule)}</Schedule>");
            if (!r.SrcAny) sb.AppendLine("        " + List("SourceNetworks", "Network", r.Src.Select(a => a.Name)));
            if (!r.DstAny) sb.AppendLine("        " + List("DestinationNetworks", "Network", r.Dst.Select(a => a.Name)));
            if (!r.SvcAny) sb.AppendLine("        " + List("Services", "Service", r.Svc.Select(s => s.Name)));
            sb.AppendLine("      </NetworkPolicy>");
            sb.AppendLine("    </FirewallRule>");
            body.Append(sb);
            remove.AppendLine($"    <FirewallRule><Name>{X(name)}</Name></FirewallRule>");
        }
        var script = new StringBuilder();
        if (objA.Script.Length + objS.Script.Length > 0)
        {
            script.AppendLine("<!-- Request 1: objects (POST first) -->");
            script.Append(objA.Script).Append(objS.Script).AppendLine();
        }
        script.AppendLine("<!-- Request 2: firewall rules -->");
        script.Append(SophosRequest("Set operation=\"add\"", body.ToString()));
        var rollback = o.IncludeRollback
            ? "<!-- Remove rules first, then objects -->\n" + SophosRequest("Remove", remove.ToString()) + objS.Rollback + objA.Rollback
            : "";
        return new GeneratedConfig(script.ToString(), rollback, objA.Warnings.Concat(objS.Warnings).ToList());
    }

    // ---------- pfSense

    private static GeneratedConfig PfSensePolicies(List<ResolvedRule> rules, List<AddressObject> addrs, PolicyOptions o, List<string> warnings)
    {
        var objA = Addresses(FirewallVendor.PfSense, addrs, new GeneratorOptions());
        var aliases = new StringBuilder(objA.Script.Replace("<!-- Paste inside <aliases> ... </aliases> in config.xml -->\n", ""));
        var filter = new StringBuilder();
        var created = new List<string>();

        string Alias(string name, string type, List<string> entries, string descr)
        {
            name = SafeName(name, FirewallVendor.PfSense);
            aliases.Append(PfAlias(name, type, entries, descr));
            created.Add(name);
            return name;
        }

        string Endpoint(string element, List<AddrRef> refs, bool any, string ruleName, string suffix, string? port)
        {
            var portXml = port is null ? "" : $"<port>{port}</port>";
            if (any) return $"<{element}><any/>{portXml}</{element}>";
            var names = refs.Where(a => a.Literal?.Kind != AddressKind.WildcardFqdn).Select(a => a.Name).ToList();
            var addr = names.Count == 1 ? names[0] : Alias($"{ruleName}_{suffix}", "network", names, $"{ruleName} {suffix.ToLowerInvariant()}");
            return $"<{element}><address>{addr}</address>{portXml}</{element}>";
        }

        foreach (var r in rules)
        {
            var rule = r.Rule;
            var tag = SafeName(rule.Name, FirewallVendor.PfSense);
            if (r.Src.Concat(r.Dst).Any(a => a.Literal?.Kind == AddressKind.WildcardFqdn))
                warnings.Add($"'{rule.Name}': pfSense can't match wildcard FQDNs - those entries were skipped.");
            var iface = rule.SourceInterface.Length > 0 && !PolicySheet.IsAny(rule.SourceInterface) ? rule.SourceInterface.ToLowerInvariant() : "lan";
            if (iface != rule.SourceInterface.ToLowerInvariant()) warnings.Add($"'{rule.Name}': no source interface - placed on 'lan'. pfSense rules live on the ingress interface.");

            // pfSense rules carry one protocol each: group the services by protocol and write one rule per group.
            var groups = new List<(string? Protocol, List<string> Ports)>();
            if (r.SvcAny) groups.Add((null, []));
            else
            {
                void Add(string proto, string? port)
                {
                    var g = groups.FirstOrDefault(x => x.Protocol == proto);
                    if (g.Protocol is null) { g = (proto, new List<string>()); groups.Add(g); }
                    if (port is not null) g.Ports.Add(port);
                }
                foreach (var s in r.Svc)
                {
                    if (s.Literal is not { } lit) { Add("tcp/udp", s.Name); continue; } // an existing port alias
                    switch (lit.Protocol)
                    {
                        case ServiceProtocol.Icmp: Add("icmp", null); break;
                        case ServiceProtocol.Udp: Add("udp", PfPorts(lit)); break;
                        case ServiceProtocol.TcpUdp: Add("tcp/udp", PfPorts(lit)); break;
                        case ServiceProtocol.Sctp: Add("sctp", PfPorts(lit)); break;
                        default: Add("tcp", PfPorts(lit)); break;
                    }
                }
            }

            foreach (var (proto, ports) in groups)
            {
                string? port = null;
                if (ports.Count == 1 && int.TryParse(ports[0], out _)) port = ports[0];
                else if (ports.Count > 0)
                    port = Alias($"{tag}_{(proto ?? "any").Replace("/", "")}", "port", ports.Distinct().ToList(), $"{rule.Name} ports");

                filter.AppendLine("<rule>");
                filter.AppendLine($"    <type>{rule.Action switch { PolicyAction.Allow => "pass", PolicyAction.Reject => "reject", _ => "block" }}</type>");
                filter.AppendLine($"    <interface>{X(iface)}</interface>");
                filter.AppendLine($"    <ipprotocol>{(r.HasV6 ? "inet46" : "inet")}</ipprotocol>");
                if (proto is not null) filter.AppendLine($"    <protocol>{proto}</protocol>");
                filter.AppendLine("    " + Endpoint("source", r.Src, r.SrcAny, tag, "SRC", null));
                filter.AppendLine("    " + Endpoint("destination", r.Dst, r.DstAny, tag, "DST", proto is "tcp" or "udp" or "tcp/udp" ? port : null));
                filter.AppendLine($"    <descr><![CDATA[{rule.Name}{(rule.Comment.Length > 0 ? " - " + rule.Comment : "")}]]></descr>");
                if (rule.Log) filter.AppendLine("    <log/>");
                if (!rule.Enabled) filter.AppendLine("    <disabled/>");
                filter.AppendLine("</rule>");
            }
        }
        var script = new StringBuilder();
        if (aliases.Length > 0) script.AppendLine("<!-- 1) Paste inside <aliases> ... </aliases> -->").Append(aliases).AppendLine();
        script.AppendLine("<!-- 2) Paste inside <filter> ... </filter>, then restore config.xml and reload the filter -->").Append(filter);
        var rollback = o.IncludeRollback
            ? "<!-- Delete these rules (by description) and aliases in the pfSense GUI: -->\n"
              + string.Join('\n', rules.Select(r => $"<!--   rule  {r.Rule.Name} -->").Concat(created.Concat(addrs.Select(a => a.Name)).Select(n => $"<!--   alias {n} -->"))) + "\n"
            : "";
        return new GeneratedConfig(script.ToString(), rollback, objA.Warnings);
    }
}
