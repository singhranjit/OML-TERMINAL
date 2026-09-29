using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Firewall;

public enum AddressKind { Host, Subnet, Range, Fqdn, WildcardFqdn }

public sealed record AddressObject(string Name, AddressKind Kind, string Value, string Comment = "")
{
    /// <summary>Host/Subnet: the Subnet; Range: start; Fqdn: null.</summary>
    public Subnet? Prefix { get; init; }
    public IPAddress? RangeEnd { get; init; }
    public bool IsV6 => Prefix?.IsV6 == true;
}

public enum ServiceProtocol { Tcp, Udp, TcpUdp, Sctp, Icmp }

public sealed record ServiceObject(string Name, ServiceProtocol Protocol, string DestinationPorts, string SourcePorts = "", string Comment = "");

public sealed record ParseIssue(int Line, string Text, string Message)
{
    public override string ToString() => $"Line {Line}: {Message} ('{Text}')";
}

public enum NamingStyle
{
    /// <summary>H-10.1.1.1, N-10.1.0.0_24, R-10.1.1.1-10.1.1.9, FQDN-example.com</summary>
    Typed,
    /// <summary>10.1.1.1, 10.1.0.0/24, 10.1.1.1-10.1.1.9, example.com (the vendor-sanitized raw value)</summary>
    Plain,
}

public sealed class NamingOptions
{
    public NamingStyle Style { get; init; } = NamingStyle.Typed;
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
}

/// <summary>
/// Turns a pasted list into address objects. One entry per line (commas/semicolons also split bare values):
///   10.1.1.1  ·  10.1.0.0/24  ·  10.1.0.0 255.255.255.0  ·  10.1.1.1-10.1.1.50  ·  2001:db8::/64  ·  example.com  ·  *.example.com
/// or CSV with an explicit name:  WebServer,10.1.1.10[,comment]
/// </summary>
public static partial class AddressListParser
{
    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9\-_]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9\-]{0,62}\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FqdnRegex();

    public static (List<AddressObject> Objects, List<ParseIssue> Issues) Parse(string text, NamingOptions? naming = null)
    {
        naming ??= new NamingOptions();
        var objects = new List<AddressObject>();
        var issues = new List<ParseIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = TextLines.Split(text);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            foreach (var (name, value, comment) in SplitEntries(line))
            {
                var obj = TryParseValue(value, comment, out var error);
                if (obj is null) { issues.Add(new ParseIssue(i + 1, value, error)); continue; }
                obj = obj with { Name = string.IsNullOrWhiteSpace(name) ? AutoName(obj, naming) : name.Trim() };
                if (!seen.Add(obj.Name)) { issues.Add(new ParseIssue(i + 1, value, $"duplicate of '{obj.Name}', skipped")); continue; }
                objects.Add(obj);
            }
        }
        return (objects, issues);
    }

    /// <summary>"name,value[,comment]" is CSV when field 2 parses as an address and field 1 doesn't; otherwise a
    /// comma/semicolon separated line is just several bare values.</summary>
    private static IEnumerable<(string? Name, string Value, string Comment)> SplitEntries(string line)
    {
        var fields = line.Split([',', ';', '\t'], StringSplitOptions.TrimEntries);
        if (fields.Length >= 2 && TryParseValue(fields[0], "", out _) is null && TryParseValue(fields[1], "", out _) is not null)
        {
            yield return (fields[0], fields[1], fields.Length > 2 ? string.Join(", ", fields[2..]).Trim() : "");
            yield break;
        }
        foreach (var f in fields.Where(f => f.Length > 0)) yield return (null, f, "");
    }

    public static AddressObject? TryParseValue(string raw, string comment, out string error)
    {
        error = "";
        var v = raw.Trim().Trim('"');

        // Range: "a-b" or "a - b" (IPv4 only - FortiGate/PAN/ASA ranges are overwhelmingly v4 in practice)
        var dash = v.Split('-', StringSplitOptions.TrimEntries);
        if (dash.Length == 2 && Ipv4.IsIpv4(dash[0]))
        {
            var start = IPAddress.Parse(dash[0]);
            IPAddress end;
            if (Ipv4.IsIpv4(dash[1])) end = IPAddress.Parse(dash[1]);
            else if (int.TryParse(dash[1], NumberStyles.None, CultureInfo.InvariantCulture, out var lastOctet) && lastOctet <= 255)
            {
                var b = start.GetAddressBytes(); b[3] = (byte)lastOctet; end = new IPAddress(b); // 10.1.1.10-20 shorthand
            }
            else { error = "range end is not a valid IPv4 address"; return null; }
            if (Ipv4.ToUInt(start) > Ipv4.ToUInt(end)) (start, end) = (end, start);
            if (start.Equals(end))
                return new AddressObject("", AddressKind.Host, start.ToString(), comment) { Prefix = new Subnet(start, 32) };
            return new AddressObject("", AddressKind.Range, $"{start}-{end}", comment) { Prefix = new Subnet(start, 32), RangeEnd = end };
        }

        if (Subnet.TryParse(v, out var subnet))
        {
            if (!subnet.OriginalAddress.Equals(subnet.Network) && !subnet.IsHost)
            {
                error = $"host bits set - did you mean {subnet.Cidr}?";
                return null;
            }
            return subnet.IsHost
                ? new AddressObject("", AddressKind.Host, subnet.Network.ToString(), comment) { Prefix = subnet }
                : new AddressObject("", AddressKind.Subnet, subnet.Cidr, comment) { Prefix = subnet };
        }

        if (FqdnRegex().IsMatch(v) && !v.All(c => char.IsDigit(c) || c == '.'))
        {
            var fqdn = v.TrimEnd('.').ToLowerInvariant();
            return fqdn.StartsWith("*.")
                ? new AddressObject("", AddressKind.WildcardFqdn, fqdn, comment)
                : new AddressObject("", AddressKind.Fqdn, fqdn, comment);
        }

        error = "not an IP, subnet, range or FQDN";
        return null;
    }

    public static string AutoName(AddressObject o, NamingOptions naming)
    {
        string core = naming.Style == NamingStyle.Plain
            ? o.Value
            : o.Kind switch
            {
                AddressKind.Host => $"H-{o.Value}",
                AddressKind.Subnet => $"N-{o.Prefix!.Network}_{o.Prefix.PrefixLength}",
                AddressKind.Range => $"R-{o.Value}",
                AddressKind.Fqdn => $"FQDN-{o.Value}",
                _ => $"WFQDN-{o.Value[2..]}",
            };
        return naming.Prefix + core + naming.Suffix;
    }
}

/// <summary>
/// Turns a pasted list into service objects. One per line:
///   443 (TCP assumed)  ·  tcp/8443  ·  udp/500-501  ·  tcp-udp/53  ·  sctp/2905  ·  icmp  ·  tcp/443:1024-65535 (dst:src)
/// or with a name:  HTTPS-ALT,tcp/8443[,comment]   ·   HTTPS-ALT,tcp,8443[,comment]
/// </summary>
public static partial class ServiceListParser
{
    [GeneratedRegex(@"^(?:(?<proto>tcp|udp|tcp[-_/]?udp|sctp|icmp)\s*[/: ]?\s*)?(?<dst>\d{1,5}(?:\s*-\s*\d{1,5})?)?(?:\s*:\s*(?<src>\d{1,5}(?:\s*-\s*\d{1,5})?))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceRegex();

    public static (List<ServiceObject> Objects, List<ParseIssue> Issues) Parse(string text, string namePrefix = "")
    {
        var objects = new List<ServiceObject>();
        var issues = new List<ParseIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = TextLines.Split(text);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split([',', ';', '\t'], StringSplitOptions.TrimEntries);

            string? name = null, comment = "", spec;
            if (fields.Length >= 3 && IsProtocol(fields[1])) { name = fields[0]; spec = $"{fields[1]}/{fields[2]}"; comment = string.Join(", ", fields[3..]); }
            else if (fields.Length >= 2 && TryParse(fields[1], out _, out _) && !TryParse(fields[0], out _, out _)) { name = fields[0]; spec = fields[1]; comment = string.Join(", ", fields[2..]); }
            else if (fields.Length > 1)
            {
                // Several bare specs on one line: "tcp/80, tcp/443, udp/53"
                foreach (var f in fields.Where(f => f.Length > 0)) Add(null, f, "", i + 1);
                continue;
            }
            else spec = fields[0];
            Add(name, spec, comment, i + 1);
        }
        return (objects, issues);

        void Add(string? name, string spec, string comment, int lineNo)
        {
            if (!TryParse(spec, out var svc, out var error)) { issues.Add(new ParseIssue(lineNo, spec, error)); return; }
            svc = svc with { Name = string.IsNullOrWhiteSpace(name) ? namePrefix + AutoName(svc) : name.Trim(), Comment = comment };
            if (!seen.Add(svc.Name)) { issues.Add(new ParseIssue(lineNo, spec, $"duplicate of '{svc.Name}', skipped")); return; }
            objects.Add(svc);
        }
    }

    private static bool IsProtocol(string s) =>
        s.Trim().ToLowerInvariant() is "tcp" or "udp" or "sctp" or "icmp" or "tcp-udp" or "tcp_udp" or "tcpudp" or "tcp/udp";

    public static bool TryParse(string spec, out ServiceObject svc, out string error)
    {
        svc = null!;
        error = "";
        var m = ServiceRegex().Match(spec.Trim());
        if (!m.Success || spec.Trim().Length == 0) { error = "expected e.g. tcp/443, udp/500-501, icmp"; return false; }

        var protoText = m.Groups["proto"].Success ? m.Groups["proto"].Value.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace("/", "") : "tcp";
        var proto = protoText switch
        {
            "udp" => ServiceProtocol.Udp,
            "tcpudp" => ServiceProtocol.TcpUdp,
            "sctp" => ServiceProtocol.Sctp,
            "icmp" => ServiceProtocol.Icmp,
            _ => ServiceProtocol.Tcp,
        };
        var dst = m.Groups["dst"].Success ? m.Groups["dst"].Value.Replace(" ", "") : "";
        var src = m.Groups["src"].Success ? m.Groups["src"].Value.Replace(" ", "") : "";

        if (proto == ServiceProtocol.Icmp)
        {
            svc = new ServiceObject("", proto, "", "");
            return true;
        }
        if (dst.Length == 0) { error = "port number missing"; return false; }
        if (!ValidRange(dst, out var norm) || (src.Length > 0 && !ValidRange(src, out src))) { error = "ports must be 1-65535"; return false; }
        svc = new ServiceObject("", proto, norm, src);
        return true;
    }

    private static bool ValidRange(string r, out string normalized)
    {
        normalized = r;
        var parts = r.Split('-');
        if (parts.Any(p => !int.TryParse(p, out var n) || n is < 1 or > 65535)) return false;
        if (parts.Length == 2)
        {
            int a = int.Parse(parts[0]), b = int.Parse(parts[1]);
            if (a > b) (a, b) = (b, a);
            normalized = a == b ? $"{a}" : $"{a}-{b}";
        }
        return true;
    }

    public static string AutoName(ServiceObject s) => s.Protocol switch
    {
        ServiceProtocol.Icmp => "ICMP-ALL",
        ServiceProtocol.TcpUdp => $"TCP_UDP-{s.DestinationPorts}",
        _ => $"{s.Protocol.ToString().ToUpperInvariant()}-{s.DestinationPorts}",
    };
}
