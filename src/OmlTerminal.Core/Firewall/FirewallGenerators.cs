using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Firewall;

public enum FirewallVendor { FortiGate, PaloAlto, CiscoAsa, CheckPoint, CiscoFtd, JuniperSrx, Sophos, PfSense }

public static class FirewallVendors
{
    public static string DisplayName(FirewallVendor v) => v switch
    {
        FirewallVendor.FortiGate => "Fortinet FortiGate",
        FirewallVendor.PaloAlto => "Palo Alto Networks",
        FirewallVendor.CiscoAsa => "Cisco ASA",
        FirewallVendor.CheckPoint => "Check Point (R80+ mgmt_cli)",
        FirewallVendor.CiscoFtd => "Cisco Firepower (FMC REST API)",
        FirewallVendor.JuniperSrx => "Juniper SRX (Junos)",
        FirewallVendor.Sophos => "Sophos Firewall (XML API)",
        FirewallVendor.PfSense => "pfSense (config.xml)",
        _ => v.ToString(),
    };

    public static (string Extension, string TypeName) FileType(FirewallVendor v) => v switch
    {
        FirewallVendor.Sophos or FirewallVendor.PfSense => (".xml", "XML"),
        FirewallVendor.CiscoFtd => (".txt", "FMC API requests"),
        FirewallVendor.CheckPoint => (".sh", "mgmt_cli script"),
        _ => (".txt", "CLI script"),
    };

    /// <summary>What the output is and how to apply it - shown above the generated text.</summary>
    public static string HowToApply(FirewallVendor v) => v switch
    {
        FirewallVendor.FortiGate => "Paste into the FortiGate CLI (SSH or GUI CLI console).",
        FirewallVendor.PaloAlto => "Enter configure mode, paste the set commands, then commit.",
        FirewallVendor.CiscoAsa => "Enter configure terminal and paste; write memory when happy.",
        FirewallVendor.CheckPoint => "Run on the management server (expert mode) or any host with mgmt_cli; the script logs in, adds and publishes.",
        FirewallVendor.CiscoFtd => "POST each JSON block to the FMC REST API endpoint shown above it (API Explorer or curl). FTDs managed by FMC have no config CLI.",
        FirewallVendor.JuniperSrx => "Enter configure, paste the set commands, commit check, then commit.",
        FirewallVendor.Sophos => "POST the XML to https://<firewall>:4444/webconsole/APIController (enable API access for your IP first).",
        FirewallVendor.PfSense => "Paste the <alias>/<rule> blocks into config.xml (Diagnostics > Backup & Restore > download, edit, restore).",
        _ => "",
    };
}

public sealed class GeneratorOptions
{
    /// <summary>Address or service group to create containing every generated object. Blank = no group.</summary>
    public string GroupName { get; init; } = "";
    public string GroupComment { get; init; } = "";

    /// <summary>Also emit a script that deletes everything created (groups first, then members).</summary>
    public bool IncludeRollback { get; init; }

    // FortiGate
    public string Vdom { get; init; } = "";
    public string AssociatedInterface { get; init; } = "";
    /// <summary>FortiOS 6.2+ takes wildcards as "set type fqdn" + "*.x"; older builds need "firewall wildcard-fqdn custom".</summary>
    public bool LegacyWildcardFqdn { get; init; }
    public string ServiceCategory { get; init; } = "";

    // Palo Alto: "" = single-vsys firewall, "shared", "vsys:vsys2", "dg:Branches" (Panorama device-group)
    public string PanScope { get; init; } = "";
    public string PanTag { get; init; } = "";
}

public sealed record GeneratedConfig(string Script, string Rollback, IReadOnlyList<string> Warnings);

public static partial class FirewallGenerators
{
    public static int MaxNameLength(FirewallVendor v) => v switch
    {
        FirewallVendor.FortiGate => 79,
        FirewallVendor.PaloAlto => 63,
        FirewallVendor.JuniperSrx => 63,
        FirewallVendor.Sophos => 60,
        FirewallVendor.PfSense => 31,
        FirewallVendor.CheckPoint => 100,
        _ => 64,
    };

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex PfSenseIllegal();

    [GeneratedRegex(@"[^A-Za-z0-9_.\-]")]
    private static partial Regex StrictIllegal();

    [GeneratedRegex(@"[^A-Za-z0-9_.\- ]")]
    private static partial Regex PanIllegal();

    /// <summary>Makes a name legal for the vendor: PAN-OS forbids '/', '*', ':' etc.; ASA forbids spaces; all cap length.</summary>
    public static string SafeName(string name, FirewallVendor vendor)
    {
        var n = name.Trim();
        switch (vendor)
        {
            case FirewallVendor.PaloAlto:
                n = n.Replace("*.", "wildcard.").Replace('/', '_').Replace(':', '_');
                n = PanIllegal().Replace(n, "_");
                if (n.Length > 0 && !char.IsLetterOrDigit(n[0])) n = "x" + n;
                break;
            case FirewallVendor.CiscoAsa:
                n = n.Replace(' ', '_');
                break;
            case FirewallVendor.PfSense:
                // Alias names: letters, digits and underscore only, and they can't be purely numeric.
                n = PfSenseIllegal().Replace(n.Replace("*.", "wildcard_"), "_");
                if (n.Length == 0 || char.IsDigit(n[0])) n = "A_" + n;
                break;
            case FirewallVendor.CheckPoint or FirewallVendor.JuniperSrx or FirewallVendor.CiscoFtd:
                n = StrictIllegal().Replace(n.Replace("*.", "wildcard.").Replace('/', '_').Replace(':', '_'), "_");
                break;
            case FirewallVendor.Sophos:
                n = n.Replace("\"", "").Replace("<", "").Replace(">", "").Replace("&", "and");
                break;
            default:
                n = n.Replace("\"", "");
                break;
        }
        int max = MaxNameLength(vendor);
        return n.Length <= max ? n : n[..max];
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ------------------------------------------------------------------ addresses

    public static GeneratedConfig Addresses(FirewallVendor vendor, IReadOnlyList<AddressObject> objects, GeneratorOptions o)
    {
        var renamed = objects.Select(a => a with { Name = SafeName(a.Name, vendor) }).ToList();
        return vendor switch
        {
            FirewallVendor.FortiGate => FortiAddresses(renamed, o),
            FirewallVendor.PaloAlto => PanAddresses(renamed, o),
            FirewallVendor.CheckPoint => CheckPointAddresses(renamed, o),
            FirewallVendor.CiscoFtd => FtdAddresses(renamed, o),
            FirewallVendor.JuniperSrx => JunosAddresses(renamed, o),
            FirewallVendor.Sophos => SophosAddresses(renamed, o),
            FirewallVendor.PfSense => PfSenseAddresses(renamed, o),
            _ => AsaAddresses(renamed, o),
        };
    }

    public static GeneratedConfig Services(FirewallVendor vendor, IReadOnlyList<ServiceObject> objects, GeneratorOptions o)
    {
        var renamed = objects.Select(s => s with { Name = SafeName(s.Name, vendor) }).ToList();
        return vendor switch
        {
            FirewallVendor.FortiGate => FortiServices(renamed, o),
            FirewallVendor.PaloAlto => PanServices(renamed, o),
            FirewallVendor.CheckPoint => CheckPointServices(renamed, o),
            FirewallVendor.CiscoFtd => FtdServices(renamed, o),
            FirewallVendor.JuniperSrx => JunosServices(renamed, o),
            FirewallVendor.Sophos => SophosServices(renamed, o),
            FirewallVendor.PfSense => PfSenseServices(renamed, o),
            _ => AsaServices(renamed, o),
        };
    }

    // ---------- FortiGate

    private sealed class FortiWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly string _vdom;
        public FortiWriter(string vdom)
        {
            _vdom = vdom.Trim();
            if (_vdom.Length > 0) { _sb.AppendLine("config vdom"); _sb.AppendLine($"edit {_vdom}"); }
        }
        public FortiWriter Line(string s) { _sb.AppendLine(s); return this; }
        public override string ToString()
        {
            var s = new StringBuilder(_sb.ToString());
            if (_vdom.Length > 0) s.AppendLine("end");
            return s.ToString();
        }
    }

    private static GeneratedConfig FortiAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var warnings = new List<string>();
        var w = new FortiWriter(o.Vdom);
        var v4 = objs.Where(a => !a.IsV6 && !(a.Kind == AddressKind.WildcardFqdn && o.LegacyWildcardFqdn)).ToList();
        var v6 = objs.Where(a => a.IsV6).ToList();
        var legacyWild = objs.Where(a => a.Kind == AddressKind.WildcardFqdn && o.LegacyWildcardFqdn).ToList();

        if (v4.Count > 0)
        {
            w.Line("config firewall address");
            foreach (var a in v4)
            {
                w.Line($"    edit {Q(a.Name)}");
                switch (a.Kind)
                {
                    case AddressKind.Host:
                    case AddressKind.Subnet:
                        w.Line($"        set subnet {a.Prefix!.Network} {a.Prefix.SubnetMask}");
                        break;
                    case AddressKind.Range:
                        w.Line("        set type iprange");
                        w.Line($"        set start-ip {a.Prefix!.Network}");
                        w.Line($"        set end-ip {a.RangeEnd}");
                        break;
                    case AddressKind.Fqdn:
                    case AddressKind.WildcardFqdn:
                        w.Line("        set type fqdn");
                        w.Line($"        set fqdn {Q(a.Value)}");
                        break;
                }
                if (o.AssociatedInterface.Length > 0) w.Line($"        set associated-interface {Q(o.AssociatedInterface)}");
                if (a.Comment.Length > 0) w.Line($"        set comment {Q(a.Comment)}");
                w.Line("    next");
            }
            w.Line("end");
        }
        if (legacyWild.Count > 0)
        {
            w.Line("config firewall wildcard-fqdn custom");
            foreach (var a in legacyWild)
            {
                w.Line($"    edit {Q(a.Name)}");
                w.Line($"        set wildcard-fqdn {Q(a.Value)}");
                if (a.Comment.Length > 0) w.Line($"        set comment {Q(a.Comment)}");
                w.Line("    next");
            }
            w.Line("end");
            if (o.GroupName.Length > 0) warnings.Add("Legacy wildcard-FQDN objects can't join an address group; they were left out of it.");
        }
        if (v6.Count > 0)
        {
            w.Line("config firewall address6");
            foreach (var a in v6)
            {
                w.Line($"    edit {Q(a.Name)}");
                w.Line($"        set ip6 {a.Prefix!.Cidr}");
                if (a.Comment.Length > 0) w.Line($"        set comment {Q(a.Comment)}");
                w.Line("    next");
            }
            w.Line("end");
        }

        var rollback = new FortiWriter(o.Vdom);
        string v4Group = o.GroupName, v6Group = v4.Count > 0 ? o.GroupName + "_v6" : o.GroupName;
        if (o.GroupName.Length > 0)
        {
            if (v4.Count > 0) FortiGroup(w, "config firewall addrgrp", SafeName(v4Group, FirewallVendor.FortiGate), v4.Select(a => a.Name), o.GroupComment);
            if (v6.Count > 0)
            {
                FortiGroup(w, "config firewall addrgrp6", SafeName(v6Group, FirewallVendor.FortiGate), v6.Select(a => a.Name), o.GroupComment);
                if (v4.Count > 0) warnings.Add($"IPv6 objects went into a separate group '{v6Group}' (FortiOS keeps v4 and v6 groups apart).");
            }
            if (v4.Count > 0) rollback.Line("config firewall addrgrp").Line($"    delete {Q(SafeName(v4Group, FirewallVendor.FortiGate))}").Line("end");
            if (v6.Count > 0) rollback.Line("config firewall addrgrp6").Line($"    delete {Q(SafeName(v6Group, FirewallVendor.FortiGate))}").Line("end");
        }
        FortiDeletes(rollback, "config firewall address", v4);
        FortiDeletes(rollback, "config firewall wildcard-fqdn custom", legacyWild);
        FortiDeletes(rollback, "config firewall address6", v6);
        return new GeneratedConfig(w.ToString(), o.IncludeRollback ? rollback.ToString() : "", warnings);
    }

    private static void FortiGroup(FortiWriter w, string section, string name, IEnumerable<string> members, string comment)
    {
        w.Line(section);
        w.Line($"    edit {Q(name)}");
        w.Line($"        set member {string.Join(' ', members.Select(Q))}");
        if (comment.Length > 0) w.Line($"        set comment {Q(comment)}");
        w.Line("    next");
        w.Line("end");
    }

    private static void FortiDeletes(FortiWriter w, string section, IEnumerable<AddressObject> objs)
    {
        var list = objs.ToList();
        if (list.Count == 0) return;
        w.Line(section);
        foreach (var a in list) w.Line($"    delete {Q(a.Name)}");
        w.Line("end");
    }

    private static GeneratedConfig FortiServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var w = new FortiWriter(o.Vdom);
        var rollback = new FortiWriter(o.Vdom);
        if (objs.Count > 0)
        {
            w.Line("config firewall service custom");
            foreach (var s in objs)
            {
                w.Line($"    edit {Q(s.Name)}");
                string range = s.SourcePorts.Length > 0 ? $"{s.DestinationPorts}:{s.SourcePorts}" : s.DestinationPorts;
                switch (s.Protocol)
                {
                    case ServiceProtocol.Tcp: w.Line($"        set tcp-portrange {range}"); break;
                    case ServiceProtocol.Udp: w.Line($"        set udp-portrange {range}"); break;
                    case ServiceProtocol.Sctp: w.Line($"        set sctp-portrange {range}"); break;
                    case ServiceProtocol.TcpUdp:
                        w.Line($"        set tcp-portrange {range}");
                        w.Line($"        set udp-portrange {range}");
                        break;
                    case ServiceProtocol.Icmp: w.Line("        set protocol ICMP"); break;
                }
                if (o.ServiceCategory.Length > 0) w.Line($"        set category {Q(o.ServiceCategory)}");
                if (s.Comment.Length > 0) w.Line($"        set comment {Q(s.Comment)}");
                w.Line("    next");
            }
            w.Line("end");
        }
        if (o.GroupName.Length > 0 && objs.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.FortiGate);
            w.Line("config firewall service group");
            w.Line($"    edit {Q(g)}");
            w.Line($"        set member {string.Join(' ', objs.Select(s => Q(s.Name)))}");
            if (o.GroupComment.Length > 0) w.Line($"        set comment {Q(o.GroupComment)}");
            w.Line("    next");
            w.Line("end");
            rollback.Line("config firewall service group").Line($"    delete {Q(g)}").Line("end");
        }
        if (objs.Count > 0)
        {
            rollback.Line("config firewall service custom");
            foreach (var s in objs) rollback.Line($"    delete {Q(s.Name)}");
            rollback.Line("end");
        }
        return new GeneratedConfig(w.ToString(), o.IncludeRollback ? rollback.ToString() : "", []);
    }

    // ---------- Palo Alto (set commands)

    public static string PanPrefix(string scope)
    {
        scope = scope.Trim();
        if (scope.Length == 0) return "set ";
        if (scope.Equals("shared", StringComparison.OrdinalIgnoreCase)) return "set shared ";
        if (scope.StartsWith("vsys:", StringComparison.OrdinalIgnoreCase)) return $"set vsys {scope[5..].Trim()} ";
        if (scope.StartsWith("dg:", StringComparison.OrdinalIgnoreCase)) return $"set device-group {PanQuote(scope[3..].Trim())} ";
        return "set ";
    }

    private static string PanQuote(string s) => s.Contains(' ') ? Q(s) : s;

    /// <summary>Custom URL category holding wildcard FQDNs (PAN-OS address objects can't). Shared by the object
    /// builder and the policy builder so rules reference exactly the category that gets created.</summary>
    internal static string PanWildcardCategory(string groupName) =>
        SafeName((groupName.Length > 0 ? groupName : "OML") + "-wildcard-fqdn", FirewallVendor.PaloAlto);

    private static GeneratedConfig PanAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var warnings = new List<string>();
        var p = PanPrefix(o.PanScope);
        var del = p.Replace("set ", "delete ", StringComparison.Ordinal);
        var normal = objs.Where(a => a.Kind != AddressKind.WildcardFqdn).ToList();
        var wild = objs.Where(a => a.Kind == AddressKind.WildcardFqdn).ToList();

        if (o.PanTag.Length > 0 && normal.Count > 0) sb.AppendLine($"{p}tag {PanQuote(o.PanTag)}");
        foreach (var a in normal)
        {
            var n = PanQuote(a.Name);
            string body = a.Kind switch
            {
                AddressKind.Host => $"ip-netmask {a.Prefix!.Cidr}",
                AddressKind.Subnet => $"ip-netmask {a.Prefix!.Cidr}",
                AddressKind.Range => $"ip-range {a.Value}",
                _ => $"fqdn {a.Value}",
            };
            sb.AppendLine($"{p}address {n} {body}");
            if (a.Comment.Length > 0) sb.AppendLine($"{p}address {n} description {Q(a.Comment)}");
            if (o.PanTag.Length > 0) sb.AppendLine($"{p}address {n} tag {PanQuote(o.PanTag)}");
        }
        if (o.GroupName.Length > 0 && normal.Count > 0)
        {
            var g = PanQuote(SafeName(o.GroupName, FirewallVendor.PaloAlto));
            sb.AppendLine($"{p}address-group {g} static [ {string.Join(' ', normal.Select(a => PanQuote(a.Name)))} ]");
            if (o.GroupComment.Length > 0) sb.AppendLine($"{p}address-group {g} description {Q(o.GroupComment)}");
            rb.AppendLine($"{del}address-group {g}");
        }
        foreach (var a in normal) rb.AppendLine($"{del}address {PanQuote(a.Name)}");

        if (wild.Count > 0)
        {
            var cat = PanQuote(PanWildcardCategory(o.GroupName));
            sb.AppendLine($"{p}profiles custom-url-category {cat} type \"URL List\"");
            sb.AppendLine($"{p}profiles custom-url-category {cat} list [ {string.Join(' ', wild.Select(a => a.Value))} ]");
            rb.AppendLine($"{del}profiles custom-url-category {cat}");
            warnings.Add($"PAN-OS address objects can't hold wildcard FQDNs, so {wild.Count} wildcard(s) went into custom URL category {cat} - reference it in a security rule's URL Category.");
        }
        if (o.PanTag.Length > 0 && normal.Count > 0) rb.AppendLine($"{del}tag {PanQuote(o.PanTag)}");
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rb.ToString() : "", warnings);
    }

    private static GeneratedConfig PanServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var warnings = new List<string>();
        var p = PanPrefix(o.PanScope);
        var del = p.Replace("set ", "delete ", StringComparison.Ordinal);
        var members = new List<string>();

        foreach (var s in objs)
        {
            if (s.Protocol is ServiceProtocol.Icmp)
            {
                warnings.Add($"'{s.Name}': PAN-OS matches ICMP by App-ID (application 'icmp' / 'ping'), not a service object - skipped.");
                continue;
            }
            var protos = s.Protocol switch
            {
                ServiceProtocol.TcpUdp => new[] { ("tcp", s.Name.Replace("TCP_UDP", "TCP")), ("udp", s.Name.Replace("TCP_UDP", "UDP")) },
                ServiceProtocol.Udp => new[] { ("udp", s.Name) },
                ServiceProtocol.Sctp => new[] { ("sctp", s.Name) },
                _ => new[] { ("tcp", s.Name) },
            };
            if (s.Protocol == ServiceProtocol.TcpUdp && protos[0].Item2 == protos[1].Item2)
                protos = [("tcp", s.Name + "-tcp"), ("udp", s.Name + "-udp")];
            foreach (var (proto, rawName) in protos)
            {
                var n = PanQuote(SafeName(rawName, FirewallVendor.PaloAlto));
                sb.Append($"{p}service {n} protocol {proto} port {s.DestinationPorts}");
                if (s.SourcePorts.Length > 0) sb.Append($" source-port {s.SourcePorts}");
                sb.AppendLine();
                if (s.Comment.Length > 0) sb.AppendLine($"{p}service {n} description {Q(s.Comment)}");
                members.Add(n);
                rb.Insert(0, $"{del}service {n}\n");
            }
        }
        if (o.GroupName.Length > 0 && members.Count > 0)
        {
            var g = PanQuote(SafeName(o.GroupName, FirewallVendor.PaloAlto));
            sb.AppendLine($"{p}service-group {g} members [ {string.Join(' ', members)} ]");
            rb.Insert(0, $"{del}service-group {g}\n");
        }
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rb.ToString().Replace("\n", Environment.NewLine) : "", warnings);
    }

    // ---------- Cisco ASA

    private static GeneratedConfig AsaAddresses(List<AddressObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        var warnings = new List<string>();
        var usable = new List<AddressObject>();
        foreach (var a in objs)
        {
            if (a.Kind == AddressKind.WildcardFqdn)
            {
                warnings.Add($"'{a.Value}': ASA FQDN objects don't support wildcards - skipped.");
                continue;
            }
            sb.AppendLine($"object network {a.Name}");
            sb.AppendLine(a.Kind switch
            {
                AddressKind.Host => $" host {a.Prefix!.Network}",
                AddressKind.Subnet when a.IsV6 => $" subnet {a.Prefix!.Cidr}",
                AddressKind.Subnet => $" subnet {a.Prefix!.Network} {a.Prefix.SubnetMask}",
                AddressKind.Range => $" range {a.Prefix!.Network} {a.RangeEnd}",
                _ => $" fqdn v4 {a.Value}",
            });
            if (a.Comment.Length > 0) sb.AppendLine($" description {a.Comment}");
            usable.Add(a);
        }
        if (o.GroupName.Length > 0 && usable.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.CiscoAsa);
            sb.AppendLine($"object-group network {g}");
            if (o.GroupComment.Length > 0) sb.AppendLine($" description {o.GroupComment}");
            foreach (var a in usable) sb.AppendLine($" network-object object {a.Name}");
            rb.AppendLine($"no object-group network {g}");
        }
        foreach (var a in usable) rb.AppendLine($"no object network {a.Name}");
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rb.ToString() : "", warnings);
    }

    private static GeneratedConfig AsaServices(List<ServiceObject> objs, GeneratorOptions o)
    {
        var sb = new StringBuilder();
        var rb = new StringBuilder();
        foreach (var s in objs)
        {
            sb.AppendLine($"object service {s.Name}");
            string Ports(string r) => r.Contains('-') ? $"range {r.Replace('-', ' ')}" : $"eq {r}";
            string proto = s.Protocol switch
            {
                ServiceProtocol.Udp => "udp",
                ServiceProtocol.TcpUdp => "tcp-udp",
                ServiceProtocol.Sctp => "sctp",
                ServiceProtocol.Icmp => "icmp",
                _ => "tcp",
            };
            if (s.Protocol == ServiceProtocol.Icmp) sb.AppendLine(" service icmp");
            else
            {
                var src = s.SourcePorts.Length > 0 ? $" source {Ports(s.SourcePorts)}" : "";
                sb.AppendLine($" service {proto}{src} destination {Ports(s.DestinationPorts)}");
            }
            if (s.Comment.Length > 0) sb.AppendLine($" description {s.Comment}");
        }
        if (o.GroupName.Length > 0 && objs.Count > 0)
        {
            var g = SafeName(o.GroupName, FirewallVendor.CiscoAsa);
            sb.AppendLine($"object-group service {g}");
            if (o.GroupComment.Length > 0) sb.AppendLine($" description {o.GroupComment}");
            foreach (var s in objs) sb.AppendLine($" service-object object {s.Name}");
            rb.AppendLine($"no object-group service {g}");
        }
        foreach (var s in objs) rb.AppendLine($"no object service {s.Name}");
        return new GeneratedConfig(sb.ToString(), o.IncludeRollback ? rb.ToString() : "", []);
    }
}
