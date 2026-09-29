using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Firewall;

/// <summary>Object generators for Check Point, Cisco Firepower (FMC), Juniper SRX, Sophos Firewall and pfSense.</summary>
public static partial class FirewallGenerators
{
    /// <summary>The name a rule must use to reference this object. Check Point DNS-domain objects are named after
    /// the domain itself with a leading dot; every other vendor uses the object's own name.</summary>
    public static string ReferenceName(FirewallVendor vendor, AddressObject a) =>
        vendor == FirewallVendor.CheckPoint && a.Kind is AddressKind.Fqdn or AddressKind.WildcardFqdn
            ? "." + (a.Kind == AddressKind.WildcardFqdn ? a.Value[2..] : a.Value)
            : a.Name;

    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };

    // ---------- Check Point (mgmt_cli)

    public const string CheckPointSession = "id.txt";
    private static string Cp(string command) => $"mgmt_cli -s {CheckPointSession} {command}";

    internal static string CheckPointHeader() =>
        $"# Log in once (prompts for the password) - every command below reuses this session\nmgmt_cli login user admin > {CheckPointSession}\n";

    internal static string CheckPointFooter() => $"{Cp("publish")}\nmgmt_cli -s {CheckPointSession} logout\n";

    private static string CpMembers(IEnumerable<string> names) =>
        string.Join(' ', names.Select((n, i) => $"members.{i + 1} {Q(n)}"));

    private static string CpComment(string c) => c.Length > 0 ? $" comments {Q(c)}" : "";

    private static GeneratedConfig CheckPointAddresses(List<AddressObject> objs, GeneratorOptions o, bool wrap = true)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        if (wrap) sb.Append(CheckPointHeader());
        foreach (var a in objs)
        {
            string name = ReferenceName(FirewallVendor.CheckPoint, a);
            switch (a.Kind)
            {
                case AddressKind.Host:
                    sb.AppendLine(Cp($"add host name {Q(name)} ip-address {Q(a.Prefix!.Network.ToString())}{CpComment(a.Comment)}"));
                    rb.Insert(0, Cp($"delete host name {Q(name)}") + "\n");
                    break;
                case AddressKind.Subnet:
                    var net = a.Prefix!;
                    sb.AppendLine(net.IsV6
                        ? Cp($"add network name {Q(name)} subnet6 {Q(net.Network.ToString())} mask-length6 {net.PrefixLength}{CpComment(a.Comment)}")
                        : Cp($"add network name {Q(name)} subnet {Q(net.Network.ToString())} mask-length {net.PrefixLength}{CpComment(a.Comment)}"));
                    rb.Insert(0, Cp($"delete network name {Q(name)}") + "\n");
                    break;
                case AddressKind.Range:
                    sb.AppendLine(Cp($"add address-range name {Q(name)} ip-address-first {Q(a.Prefix!.Network.ToString())} ip-address-last {Q(a.RangeEnd!.ToString())}{CpComment(a.Comment)}"));
                    rb.Insert(0, Cp($"delete address-range name {Q(name)}") + "\n");
                    break;
                default:
                    // DNS-domain objects: "is-sub-domain true" matches every host under the domain (a wildcard).
                    sb.AppendLine(Cp($"add dns-domain name {Q(name)} is-sub-domain {(a.Kind == AddressKind.WildcardFqdn ? "true" : "false")}{CpComment(a.Comment)}"));
                    rb.Insert(0, Cp($"delete dns-domain name {Q(name)}") + "\n");
                    break;
            }
        }
        if (o.GroupName.Length > 0 && objs.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.CheckPoint);
            sb.AppendLine(Cp($"add group name {Q(g)} {CpMembers(objs.Select(a => ReferenceName(FirewallVendor.CheckPoint, a)))}{CpComment(o.GroupComment)}"));
            rb.Insert(0, Cp($"delete group name {Q(g)}") + "\n");
        }
        if (wrap) sb.Append(CheckPointFooter());
        var rollback = rb.Length == 0 ? "" : CheckPointHeader() + rb + CheckPointFooter();
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rollback : "", []);
    }

    private static GeneratedConfig CheckPointServices(List<ServiceObject> objs, GeneratorOptions o) => CheckPointServices(objs, o, wrap: true);

    internal static GeneratedConfig CheckPointServices(List<ServiceObject> objs, GeneratorOptions o, bool wrap)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var warnings = new List<string>();
        var members = new List<string>();
        if (wrap) sb.Append(CheckPointHeader());
        foreach (var s in objs)
        {
            if (s.Protocol == ServiceProtocol.Icmp)
            {
                warnings.Add($"'{s.Name}': use Check Point's predefined ICMP services (e.g. echo-request) - skipped.");
                continue;
            }
            foreach (var (kind, name) in SplitTcpUdp(s, FirewallVendor.CheckPoint))
            {
                var src = s.SourcePorts.Length > 0 ? $" source-port {Q(s.SourcePorts)}" : "";
                sb.AppendLine(Cp($"add service-{kind} name {Q(name)} port {Q(s.DestinationPorts)}{src}{CpComment(s.Comment)}"));
                rb.Insert(0, Cp($"delete service-{kind} name {Q(name)}") + "\n");
                members.Add(name);
            }
        }
        if (o.GroupName.Length > 0 && members.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.CheckPoint);
            sb.AppendLine(Cp($"add service-group name {Q(g)} {CpMembers(members)}{CpComment(o.GroupComment)}"));
            rb.Insert(0, Cp($"delete service-group name {Q(g)}") + "\n");
        }
        if (wrap) sb.Append(CheckPointFooter());
        var rollback = rb.Length == 0 ? "" : CheckPointHeader() + rb + CheckPointFooter();
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rollback : "", warnings);
    }

    /// <summary>Vendors without a combined TCP+UDP service get one object per protocol ("TCP_UDP-53" → TCP-53 + UDP-53).
    /// Returns (protocol keyword in lower case, object name) pairs.</summary>
    internal static IEnumerable<(string Kind, string Name)> SplitTcpUdp(ServiceObject s, FirewallVendor vendor)
    {
        string proto = s.Protocol switch { ServiceProtocol.Udp => "udp", ServiceProtocol.Sctp => "sctp", _ => "tcp" };
        if (s.Protocol != ServiceProtocol.TcpUdp) { yield return (proto, s.Name); yield break; }
        var tcp = s.Name.Replace("TCP_UDP", "TCP");
        var udp = s.Name.Replace("TCP_UDP", "UDP");
        if (tcp == udp) { tcp = s.Name + "-tcp"; udp = s.Name + "-udp"; }
        yield return ("tcp", SafeName(tcp, vendor));
        yield return ("udp", SafeName(udp, vendor));
    }

    // ---------- Cisco Firepower (FMC REST API)

    public const string FmcBase = "/api/fmc_config/v1/domain/{domainUUID}";

    private static void FmcBlock(StringBuilder sb, string title, string endpoint, JsonNode body)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine($"### POST https://<fmc>{FmcBase}{endpoint}");
        sb.AppendLine(body.ToJsonString(JsonIndented));
        sb.AppendLine();
    }

    /// <summary>Host / Network / Range literal, as used inside FMC groups and access rules.</summary>
    internal static JsonObject FmcLiteral(AddressObject a) => a.Kind switch
    {
        AddressKind.Host => new JsonObject { ["type"] = "Host", ["value"] = a.Prefix!.Network.ToString() },
        AddressKind.Subnet => new JsonObject { ["type"] = "Network", ["value"] = a.Prefix!.Cidr },
        _ => new JsonObject { ["type"] = "Range", ["value"] = a.Value },
    };

    private static GeneratedConfig FtdAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var warnings = new List<string>();
        JsonArray Items(AddressKind kind, string type, Func<AddressObject, string> value, Action<JsonObject>? extra = null)
        {
            var arr = new JsonArray();
            foreach (var a in objs.Where(x => x.Kind == kind))
            {
                var node = new JsonObject { ["name"] = a.Name, ["type"] = type, ["value"] = value(a) };
                if (a.Comment.Length > 0) node["description"] = a.Comment;
                extra?.Invoke(node);
                arr.Add(node);
            }
            return arr;
        }
        var hosts = Items(AddressKind.Host, "Host", a => a.Prefix!.Network.ToString());
        var nets = Items(AddressKind.Subnet, "Network", a => a.Prefix!.Cidr);
        var ranges = Items(AddressKind.Range, "Range", a => a.Value);
        var fqdns = Items(AddressKind.Fqdn, "FQDN", a => a.Value, n => n["dnsResolution"] = "IPV4_AND_IPV6");
        if (hosts.Count > 0) FmcBlock(sb, "Host objects", "/object/hosts?bulk=true", hosts);
        if (nets.Count > 0) FmcBlock(sb, "Network objects", "/object/networks?bulk=true", nets);
        if (ranges.Count > 0) FmcBlock(sb, "Range objects", "/object/ranges?bulk=true", ranges);
        if (fqdns.Count > 0) FmcBlock(sb, "FQDN objects", "/object/fqdns?bulk=true", fqdns);
        int wild = objs.Count(a => a.Kind == AddressKind.WildcardFqdn);
        if (wild > 0) warnings.Add($"FMC FQDN objects can't hold wildcards - {wild} wildcard FQDN(s) skipped (use a URL object / DNS policy instead).");

        if (o.GroupName.Length > 0)
        {
            // Groups by literal value, so the payload doesn't depend on object UUIDs FMC hands out at creation time.
            var literals = new JsonArray();
            foreach (var a in objs.Where(x => x.Kind is AddressKind.Host or AddressKind.Subnet or AddressKind.Range)) literals.Add(FmcLiteral(a));
            if (literals.Count > 0)
            {
                var group = new JsonObject { ["name"] = SafeName(o.GroupName, FirewallVendor.CiscoFtd), ["type"] = "NetworkGroup", ["literals"] = literals };
                if (o.GroupComment.Length > 0) group["description"] = o.GroupComment;
                FmcBlock(sb, "Network group (members as literals)", "/object/networkgroups", group);
            }
            if (fqdns.Count > 0) warnings.Add("FQDN objects aren't added to the group: FMC group members need object IDs. Add them in the FMC UI after the POST.");
        }
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? FtdRollbackNote(objs.Select(a => a.Name)) : "", warnings);
    }

    private static string FtdRollbackNote(IEnumerable<string> names) =>
        "### FMC deletes objects by UUID: GET the endpoint with ?filter=nameOrValue:<name> to find each ID, then\n" +
        $"### DELETE https://<fmc>{FmcBase}/object/<type>/<id>\n" + string.Join('\n', names.Select(n => $"#   {n}")) + "\n";

    private static GeneratedConfig FtdServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var warnings = new List<string>();
        var arr = new JsonArray();
        var names = new List<string>();
        foreach (var s in objs)
        {
            if (s.Protocol is ServiceProtocol.Icmp or ServiceProtocol.Sctp)
            {
                warnings.Add($"'{s.Name}': create ICMP/SCTP objects in FMC (object/icmpv4objects) - skipped.");
                continue;
            }
            foreach (var (kind, name) in SplitTcpUdp(s, FirewallVendor.CiscoFtd))
            {
                var node = new JsonObject { ["name"] = name, ["type"] = "ProtocolPortObject", ["protocol"] = kind.ToUpperInvariant(), ["port"] = s.DestinationPorts };
                if (s.Comment.Length > 0) node["description"] = s.Comment;
                arr.Add(node);
                names.Add(name);
            }
        }
        if (arr.Count > 0) FmcBlock(sb, "Port objects", "/object/protocolportobjects?bulk=true", arr);
        if (o.GroupName.Length > 0 && names.Count > 0)
        {
            var members = new JsonArray();
            foreach (var n in names) members.Add(new JsonObject { ["name"] = n, ["type"] = "ProtocolPortObject", ["id"] = $"<id of {n}>" });
            FmcBlock(sb, "Port group - replace each <id of ...> with the id returned by the POST above", "/object/portobjectgroups",
                new JsonObject { ["name"] = SafeName(o.GroupName, FirewallVendor.CiscoFtd), ["type"] = "PortObjectGroup", ["objects"] = members });
            warnings.Add("FMC port groups reference members by UUID - fill in the <id of ...> placeholders from the first POST's response.");
        }
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? FtdRollbackNote(names) : "", warnings);
    }

    // ---------- Juniper SRX (Junos set commands)

    private const string JunosBook = "set security address-book global";

    private static GeneratedConfig JunosAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var warnings = new List<string>();
        var usable = new List<AddressObject>();
        foreach (var a in objs)
        {
            string body = a.Kind switch
            {
                AddressKind.Host or AddressKind.Subnet => a.Prefix!.Cidr,
                AddressKind.Range => $"range-address {a.Prefix!.Network} to {a.RangeEnd}",
                AddressKind.Fqdn => $"dns-name {a.Value}",
                _ => "",
            };
            if (body.Length == 0)
            {
                warnings.Add($"'{a.Value}': Junos address books have no wildcard-FQDN type - skipped.");
                continue;
            }
            sb.AppendLine($"{JunosBook} address {a.Name} {body}");
            if (a.Comment.Length > 0) sb.AppendLine($"{JunosBook} address {a.Name} description {Q(a.Comment)}");
            rb.AppendLine($"delete security address-book global address {a.Name}");
            usable.Add(a);
        }
        if (o.GroupName.Length > 0 && usable.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.JuniperSrx);
            foreach (var a in usable) sb.AppendLine($"{JunosBook} address-set {g} address {a.Name}");
            if (o.GroupComment.Length > 0) sb.AppendLine($"{JunosBook} address-set {g} description {Q(o.GroupComment)}");
            rb.Insert(0, $"delete security address-book global address-set {g}\n");
        }
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rb.ToString() : "", warnings);
    }

    private static GeneratedConfig JunosServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var names = new List<string>();
        foreach (var s in objs)
        {
            if (s.Protocol == ServiceProtocol.Icmp)
            {
                sb.AppendLine($"set applications application {s.Name} protocol icmp");
                names.Add(s.Name);
                rb.AppendLine($"delete applications application {s.Name}");
                continue;
            }
            foreach (var (kind, name) in SplitTcpUdp(s, FirewallVendor.JuniperSrx))
            {
                sb.Append($"set applications application {name} protocol {kind} destination-port {s.DestinationPorts}");
                if (s.SourcePorts.Length > 0) sb.Append($" source-port {s.SourcePorts}");
                sb.AppendLine();
                if (s.Comment.Length > 0) sb.AppendLine($"set applications application {name} description {Q(s.Comment)}");
                names.Add(name);
                rb.AppendLine($"delete applications application {name}");
            }
        }
        if (o.GroupName.Length > 0 && names.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.JuniperSrx);
            foreach (var n in names) sb.AppendLine($"set applications application-set {g} application {n}");
            rb.Insert(0, $"delete applications application-set {g}\n");
        }
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rb.ToString() : "", []);
    }

    // ---------- Sophos Firewall (XML API)

    internal static string X(string s) => SecurityElement.Escape(s) ?? "";

    internal static string SophosRequest(string operation, string inner) =>
        "<Request>\n  <Login><Username>admin</Username><Password>CHANGE_ME</Password></Login>\n" +
        $"  <{operation}>\n{inner}  </{operation.Split(' ')[0]}>\n</Request>\n";

    private static GeneratedConfig SophosAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var body = new StringBuilder();
        var remove = new StringBuilder();
        foreach (var a in objs)
        {
            string desc = a.Comment.Length > 0 ? $"<Description>{X(a.Comment)}</Description>" : "";
            switch (a.Kind)
            {
                case AddressKind.Host:
                    body.AppendLine($"    <IPHost><Name>{X(a.Name)}</Name><IPFamily>{Family(a)}</IPFamily><HostType>IP</HostType><IPAddress>{a.Prefix!.Network}</IPAddress>{desc}</IPHost>");
                    remove.AppendLine($"    <IPHost><Name>{X(a.Name)}</Name></IPHost>");
                    break;
                case AddressKind.Subnet:
                    var mask = a.IsV6 ? a.Prefix!.PrefixLength.ToString() : a.Prefix!.SubnetMask.ToString();
                    body.AppendLine($"    <IPHost><Name>{X(a.Name)}</Name><IPFamily>{Family(a)}</IPFamily><HostType>Network</HostType><IPAddress>{a.Prefix.Network}</IPAddress><Subnet>{mask}</Subnet>{desc}</IPHost>");
                    remove.AppendLine($"    <IPHost><Name>{X(a.Name)}</Name></IPHost>");
                    break;
                case AddressKind.Range:
                    body.AppendLine($"    <IPHost><Name>{X(a.Name)}</Name><IPFamily>IPv4</IPFamily><HostType>IPRange</HostType><StartIPAddress>{a.Prefix!.Network}</StartIPAddress><EndIPAddress>{a.RangeEnd}</EndIPAddress>{desc}</IPHost>");
                    remove.AppendLine($"    <IPHost><Name>{X(a.Name)}</Name></IPHost>");
                    break;
                default:
                    // SFOS 18.5+ accepts wildcards ("*.example.com") directly in FQDN host objects.
                    body.AppendLine($"    <FQDNHost><Name>{X(a.Name)}</Name><FQDN>{X(a.Value)}</FQDN>{desc}</FQDNHost>");
                    remove.AppendLine($"    <FQDNHost><Name>{X(a.Name)}</Name></FQDNHost>");
                    break;
            }
        }
        var warnings = new List<string>();
        if (o.GroupName.Length > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.Sophos);
            var desc = o.GroupComment.Length > 0 ? $"<Description>{X(o.GroupComment)}</Description>" : "";
            var ip = objs.Where(a => a.Kind is not (AddressKind.Fqdn or AddressKind.WildcardFqdn)).ToList();
            foreach (var family in ip.GroupBy(Family))
            {
                var name = family.Key == "IPv6" && ip.Any(a => !a.IsV6) ? g + "_v6" : g;
                body.AppendLine($"    <IPHostGroup><Name>{X(name)}</Name>{desc}<IPFamily>{family.Key}</IPFamily><HostList>" +
                                string.Concat(family.Select(a => $"<Host>{X(a.Name)}</Host>")) + "</HostList></IPHostGroup>");
                remove.Insert(0, $"    <IPHostGroup><Name>{X(name)}</Name></IPHostGroup>\n");
            }
            var fq = objs.Where(a => a.Kind is AddressKind.Fqdn or AddressKind.WildcardFqdn).ToList();
            if (fq.Count > 0)
            {
                var name = ip.Count > 0 ? g + "_FQDN" : g;
                body.AppendLine($"    <FQDNHostGroup><Name>{X(name)}</Name>{desc}<FQDNHostList>" +
                                string.Concat(fq.Select(a => $"<FQDNHost>{X(a.Name)}</FQDNHost>")) + "</FQDNHostList></FQDNHostGroup>");
                remove.Insert(0, $"    <FQDNHostGroup><Name>{X(name)}</Name></FQDNHostGroup>\n");
                if (ip.Count > 0) warnings.Add($"Sophos keeps IP and FQDN groups apart: FQDNs went into '{name}'.");
            }
        }
        var script = body.Length == 0 ? "" : SophosRequest("Set operation=\"add\"", body.ToString());
        var rollback = remove.Length == 0 ? "" : SophosRequest("Remove", remove.ToString());
        return new GeneratedConfig(script, o.IncludeRollback ? rollback : "", warnings);
    }

    private static string Family(AddressObject a) => a.IsV6 ? "IPv6" : "IPv4";

    internal static string SophosPorts(string range) => range.Replace('-', ':');

    private static GeneratedConfig SophosServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var body = new StringBuilder();
        var remove = new StringBuilder();
        var warnings = new List<string>();
        var names = new List<string>();
        foreach (var s in objs)
        {
            string details;
            string type = "TCPorUDP";
            string Detail(string proto) =>
                $"<ServiceDetail><SourcePort>{(s.SourcePorts.Length > 0 ? SophosPorts(s.SourcePorts) : "1:65535")}</SourcePort>" +
                $"<DestinationPort>{SophosPorts(s.DestinationPorts)}</DestinationPort><Protocol>{proto}</Protocol></ServiceDetail>";
            switch (s.Protocol)
            {
                case ServiceProtocol.Tcp: details = Detail("TCP"); break;
                case ServiceProtocol.Udp: details = Detail("UDP"); break;
                case ServiceProtocol.TcpUdp: details = Detail("TCP") + Detail("UDP"); break;
                case ServiceProtocol.Icmp:
                    type = "ICMP";
                    details = "<ServiceDetail><ICMPType>Any Type</ICMPType><ICMPCode>Any Code</ICMPCode></ServiceDetail>";
                    break;
                default:
                    warnings.Add($"'{s.Name}': Sophos service objects have no SCTP type - skipped.");
                    continue;
            }
            body.AppendLine($"    <Services><Name>{X(s.Name)}</Name><Type>{type}</Type><ServiceDetails>{details}</ServiceDetails></Services>");
            remove.AppendLine($"    <Services><Name>{X(s.Name)}</Name></Services>");
            names.Add(s.Name);
        }
        if (o.GroupName.Length > 0 && names.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.Sophos);
            var desc = o.GroupComment.Length > 0 ? $"<Description>{X(o.GroupComment)}</Description>" : "";
            body.AppendLine($"    <ServiceGroup><Name>{X(g)}</Name>{desc}<ServiceList>" + string.Concat(names.Select(n => $"<Service>{X(n)}</Service>")) + "</ServiceList></ServiceGroup>");
            remove.Insert(0, $"    <ServiceGroup><Name>{X(g)}</Name></ServiceGroup>\n");
        }
        var script = body.Length == 0 ? "" : SophosRequest("Set operation=\"add\"", body.ToString());
        var rollback = remove.Length == 0 ? "" : SophosRequest("Remove", remove.ToString());
        return new GeneratedConfig(script, o.IncludeRollback ? rollback : "", warnings);
    }

    // ---------- pfSense (config.xml aliases)

    internal static string PfAlias(string name, string type, IEnumerable<string> entries, string descr, IEnumerable<string>? details = null)
    {
        var list = entries.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<alias>");
        sb.AppendLine($"    <name>{name}</name>");
        sb.AppendLine($"    <type>{type}</type>");
        sb.AppendLine($"    <address>{X(string.Join(' ', list))}</address>");
        sb.AppendLine($"    <descr><![CDATA[{descr}]]></descr>");
        sb.AppendLine($"    <detail><![CDATA[{string.Join("||", details ?? list.Select(_ => ""))}]]></detail>");
        sb.AppendLine("</alias>");
        return sb.ToString();
    }

    /// <summary>What goes in a pfSense alias for this object: CIDRs (ranges expanded), or the FQDN itself.</summary>
    internal static IEnumerable<string> PfEntries(AddressObject a) => a.Kind switch
    {
        AddressKind.Host => [a.Prefix!.IsV6 ? a.Prefix.Network + "/128" : a.Prefix.Network + "/32"],
        AddressKind.Subnet => [a.Prefix!.Cidr],
        AddressKind.Range => Ipv4.RangeToCidrs(a.Prefix!.Network, a.RangeEnd!).Select(c => c.Cidr),
        _ => [a.Value],
    };

    private static GeneratedConfig PfSenseAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var warnings = new List<string>();
        var usable = new List<AddressObject>();
        foreach (var a in objs)
        {
            if (a.Kind == AddressKind.WildcardFqdn)
            {
                warnings.Add($"'{a.Value}': pfSense aliases can't resolve wildcard FQDNs - skipped.");
                continue;
            }
            var entries = PfEntries(a).ToList();
            sb.Append(PfAlias(a.Name, a.Kind == AddressKind.Host ? "host" : "network", entries, a.Comment.Length > 0 ? a.Comment : a.Value));
            usable.Add(a);
        }
        if (o.GroupName.Length > 0 && usable.Count > 0)
        {
            // pfSense aliases nest: the group alias simply lists the per-object alias names.
            sb.Append(PfAlias(SafeName(o.GroupName, FirewallVendor.PfSense), "network", usable.Select(a => a.Name),
                o.GroupComment.Length > 0 ? o.GroupComment : "OML Terminal group", usable.Select(a => a.Value)));
        }
        if (sb.Length > 0) sb.Insert(0, "<!-- Paste inside <aliases> ... </aliases> in config.xml -->\n");
        var rollback = usable.Count == 0 ? "" :
            "<!-- Remove these aliases in Firewall > Aliases (or delete their <alias> blocks from config.xml): -->\n" +
            string.Join('\n', (o.GroupName.Length > 0 ? [SafeName(o.GroupName, FirewallVendor.PfSense)] : Array.Empty<string>()).Concat(usable.Select(a => a.Name)).Select(n => $"<!--   {n} -->")) + "\n";
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rollback : "", warnings);
    }

    internal static string PfPorts(ServiceObject s) => s.DestinationPorts.Replace('-', ':');

    private static GeneratedConfig PfSenseServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var warnings = new List<string>();
        var usable = objs.Where(s => s.Protocol != ServiceProtocol.Icmp).ToList();
        if (usable.Count < objs.Count) warnings.Add("ICMP isn't a port - pick protocol ICMP on the pfSense rule instead. Skipped.");
        warnings.Add("pfSense port aliases hold only port numbers; the TCP/UDP protocol is chosen on each firewall rule.");
        if (o.GroupName.Length > 0)
        {
            if (usable.Count > 0)
                sb.Append(PfAlias(SafeName(o.GroupName, FirewallVendor.PfSense), "port", usable.Select(PfPorts),
                    o.GroupComment.Length > 0 ? o.GroupComment : "OML Terminal ports", usable.Select(s => s.Name)));
        }
        else
        {
            foreach (var s in usable) sb.Append(PfAlias(s.Name, "port", [PfPorts(s)], s.Comment.Length > 0 ? s.Comment : s.Name));
        }
        if (sb.Length > 0) sb.Insert(0, "<!-- Paste inside <aliases> ... </aliases> in config.xml -->\n");
        return new GeneratedConfig(sb.ToString(), "", warnings);
    }
}
