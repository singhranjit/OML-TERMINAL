using System.Globalization;
using System.Net;
using System.Text;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Capture;

public sealed class FilterSyntaxException(string message) : Exception(message);

/// <summary>Wireshark-style display filters over decoded packets:
/// <c>ip.addr == 10.0.0.0/24 &amp;&amp; tcp.port == 443</c>, <c>dns.qry.name contains "corp"</c>, <c>!arp</c>,
/// <c>tcp.flags.syn &amp;&amp; !tcp.flags.ack</c>, <c>frame.len &gt; 1000</c>, <c>vlan.id == 20</c>, a bare protocol name,
/// a bare IP (= ip.addr), and "quoted text" to search the summary line.</summary>
public sealed class DisplayFilter
{
    private readonly Func<DecodedPacket, bool> _match;

    public string Text { get; }

    private DisplayFilter(string text, Func<DecodedPacket, bool> match)
    {
        Text = text;
        _match = match;
    }

    public static readonly DisplayFilter All = new("", _ => true);

    public bool Matches(DecodedPacket p)
    {
        try { return _match(p); } catch { return false; }
    }

    public static DisplayFilter Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return All;
        var parser = new Parser(Tokenize(text));
        var expr = parser.Or();
        if (!parser.AtEnd) throw new FilterSyntaxException($"Unexpected '{parser.Peek}'");
        return new DisplayFilter(text, expr);
    }

    // ---------- tokenizer ----------

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) throw new FilterSyntaxException("Unclosed quote");
                tokens.Add(s[i..(end + 1)]);
                i = end + 1;
                continue;
            }
            string? two = i + 1 < s.Length ? s.Substring(i, 2) : null;
            if (two is "==" or "!=" or ">=" or "<=" or "&&" or "||") { tokens.Add(two); i += 2; continue; }
            if (c is '>' or '<' or '!') { tokens.Add(c.ToString()); i++; continue; }
            var sb = new StringBuilder();
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '"' or '=' or '>' or '<' or '&' or '|')
                   && !(s[i] == '!' && i + 1 < s.Length && s[i + 1] == '='))
                sb.Append(s[i++]);
            if (sb.Length == 0) throw new FilterSyntaxException($"Unexpected '{c}'");
            tokens.Add(sb.ToString());
        }
        return tokens;
    }

    // ---------- parser ----------

    private sealed class Parser(List<string> t)
    {
        private int _i;
        public bool AtEnd => _i >= t.Count;
        public string Peek => AtEnd ? "end of filter" : t[_i];
        private bool Accept(params string[] options)
        {
            if (!AtEnd && options.Any(o => string.Equals(o, t[_i], StringComparison.OrdinalIgnoreCase))) { _i++; return true; }
            return false;
        }

        public Func<DecodedPacket, bool> Or()
        {
            var left = And();
            while (Accept("||", "or")) { var l = left; var r = And(); left = p => l(p) || r(p); }
            return left;
        }

        private Func<DecodedPacket, bool> And()
        {
            var left = Not();
            while (Accept("&&", "and")) { var l = left; var r = Not(); left = p => l(p) && r(p); }
            return left;
        }

        private Func<DecodedPacket, bool> Not()
        {
            if (Accept("!", "not")) { var inner = Not(); return p => !inner(p); }
            return Primary();
        }

        private Func<DecodedPacket, bool> Primary()
        {
            if (AtEnd) throw new FilterSyntaxException("Filter ends too early");
            if (Accept("("))
            {
                var e = Or();
                if (!Accept(")")) throw new FilterSyntaxException("Missing ')'");
                return e;
            }
            var token = t[_i++];
            if (token.StartsWith('"')) { var text = token.Trim('"'); return p => p.Info.Contains(text, StringComparison.OrdinalIgnoreCase); }
            string field = token.ToLowerInvariant();
            if (!AtEnd && t[_i] is "==" or "!=" or ">" or "<" or ">=" or "<=" || !AtEnd && t[_i].Equals("contains", StringComparison.OrdinalIgnoreCase)
                || !AtEnd && t[_i] is "eq" or "ne" or "gt" or "lt")
            {
                var op = t[_i++].ToLowerInvariant() switch { "eq" => "==", "ne" => "!=", "gt" => ">", "lt" => "<", var o => o };
                if (AtEnd) throw new FilterSyntaxException($"'{field} {op}' needs a value");
                var value = t[_i++].Trim('"');
                return Compare(field, op, value);
            }
            // Bare IP or subnet = ip.addr; bare word = protocol / boolean field.
            if (Subnet.TryParse(token, out _) && token.Count(c => c == '.') == 3 || token.Contains(':') && IPAddress.TryParse(token, out _))
                return Compare("ip.addr", "==", token);
            return Truthy(field);
        }
    }

    // ---------- semantics ----------

    private static Func<DecodedPacket, bool> Truthy(string field) => field switch
    {
        "tcp.flags.syn" => p => p.Flags.HasFlag(TcpFlags.Syn),
        "tcp.flags.ack" => p => p.Flags.HasFlag(TcpFlags.Ack),
        "tcp.flags.fin" => p => p.Flags.HasFlag(TcpFlags.Fin),
        "tcp.flags.reset" or "tcp.flags.rst" => p => p.Flags.HasFlag(TcpFlags.Rst),
        "tcp.flags.push" or "tcp.flags.psh" => p => p.Flags.HasFlag(TcpFlags.Psh),
        "broadcast" or "eth.broadcast" => p => p.IsBroadcast,
        "multicast" => p => p.IsMulticast,
        "malformed" or "_ws.malformed" => p => p.Malformed,
        "dns.response" => p => p.DnsId is not null && p.DnsResponse,
        "dns.error" => p => p.DnsResponse && p.DnsRcode != 0,
        "stp.tc" => p => p.StpTopologyChange,
        "ip" => p => p.Protocols.Contains("ip"),
        _ when IsKnownProtocol(field) => p => p.Protocols.Contains(field) || p.Protocol.Equals(field, StringComparison.OrdinalIgnoreCase),
        _ => throw new FilterSyntaxException($"Unknown field or protocol '{field}'"),
    };

    private static readonly HashSet<string> KnownProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "eth", "vlan", "arp", "ipv6", "icmp", "icmpv6", "tcp", "udp", "dns", "mdns", "llmnr", "dhcp", "bootp", "tls", "http", "ntp", "syslog",
        "tftp", "hsrp", "bgp", "ospf", "cdp", "lldp", "stp", "pvst+", "ssh", "telnet", "snmp", "smb", "rdp", "quic", "igmp", "eapol", "lacp",
        "radius", "tacacs+", "kerberos", "ldap", "sip", "ssdp", "nbns", "gre", "esp", "vrrp", "eigrp", "pim", "wlan", "802.11", "dtp", "udld",
    };

    private static bool IsKnownProtocol(string f) => KnownProtocols.Contains(f);

    private static Func<DecodedPacket, bool> Compare(string field, string op, string value)
    {
        switch (field)
        {
            case "ip.addr" or "ipv6.addr" or "host": return Ip(value, op, p => [p.SrcIp, p.DstIp]);
            case "ip.src" or "ipv6.src" or "src": return Ip(value, op, p => [p.SrcIp]);
            case "ip.dst" or "ipv6.dst" or "dst": return Ip(value, op, p => [p.DstIp]);
            case "eth.addr" or "eth.src" or "eth.dst":
                {
                    var mac = NormalizeMac(value);
                    Func<DecodedPacket, string?[]> get = field switch
                    {
                        "eth.src" => p => [p.SrcMac],
                        "eth.dst" => p => [p.DstMac],
                        _ => p => [p.SrcMac, p.DstMac],
                    };
                    return p => Eq(op, get(p).Any(m => m is not null && NormalizeMac(m) == mac));
                }
            case "tcp.port": return Num(value, op, p => p.IpProtocol == 6 ? [p.SrcPort, p.DstPort] : []);
            case "udp.port": return Num(value, op, p => p.IpProtocol == 17 ? [p.SrcPort, p.DstPort] : []);
            case "port": return Num(value, op, p => [p.SrcPort, p.DstPort]);
            case "tcp.srcport": return Num(value, op, p => p.IpProtocol == 6 ? [p.SrcPort] : []);
            case "tcp.dstport": return Num(value, op, p => p.IpProtocol == 6 ? [p.DstPort] : []);
            case "udp.srcport": return Num(value, op, p => p.IpProtocol == 17 ? [p.SrcPort] : []);
            case "udp.dstport": return Num(value, op, p => p.IpProtocol == 17 ? [p.DstPort] : []);
            case "frame.len": return Num(value, op, p => [p.OriginalLength]);
            case "frame.number": return Num(value, op, p => [p.Number]);
            case "vlan.id": return Num(value, op, p => [p.Vlan]);
            case "ip.ttl": return Num(value, op, p => [p.Ttl]);
            case "tcp.window_size" or "tcp.window": return Num(value, op, p => p.IpProtocol == 6 ? [p.Window] : []);
            case "tcp.len": return Num(value, op, p => p.IpProtocol == 6 ? [p.PayloadLength] : []);
            case "ip.proto": return Num(value, op, p => [p.IpProtocol]);
            case "icmp.type": return Num(value, op, p => [p.IcmpType]);
            case "dns.flags.rcode": return Num(value, op, p => p.DnsId is null ? [] : [p.DnsRcode]);
            case "dhcp.option.dhcp" or "bootp.option.dhcp": return Num(value, op, p => [p.DhcpType]);
            case "arp.opcode": return Num(value, op, p => [p.ArpOp]);
            case "tcp.flags.syn" or "tcp.flags.ack" or "tcp.flags.fin" or "tcp.flags.reset" or "tcp.flags.rst" or "tcp.flags.push":
                {
                    var truthy = Truthy(field);
                    bool want = value is "1" or "true";
                    return p => Eq(op, truthy(p) == want);
                }
            case "dns.qry.name" or "dns.name": return Str(value, op, p => p.DnsName);
            case "tls.sni" or "tls.handshake.extensions_server_name": return Str(value, op, p => p.TlsSni);
            case "http.host": return Str(value, op, p => p.HttpHost);
            case "dhcp.hostname" or "dhcp.option.hostname": return Str(value, op, p => p.DhcpHostname);
            case "wlan.ssid": return Str(value, op, p => p.Ssid);
            case "_ws.col.info" or "info": return Str(value, op, p => p.Info);
            case "_ws.col.protocol" or "protocol": return Str(value, op, p => p.Protocol);
            case "frame":
                if (op != "contains") throw new FilterSyntaxException("Use 'frame contains \"text\"'");
                var needle = Encoding.ASCII.GetBytes(value);
                return p => p.Data.AsSpan().IndexOf(needle) >= 0;
            default: throw new FilterSyntaxException($"Unknown field '{field}'");
        }
    }

    private static bool Eq(string op, bool matched) => op switch
    {
        "==" or "contains" => matched,
        "!=" => !matched,
        _ => throw new FilterSyntaxException($"'{op}' isn't valid here"),
    };

    private static Func<DecodedPacket, bool> Ip(string value, string op, Func<DecodedPacket, string?[]> get)
    {
        if (!Subnet.TryParse(value, out var net)) throw new FilterSyntaxException($"'{value}' isn't an IP address or subnet");
        if (op is not ("==" or "!=")) throw new FilterSyntaxException($"Use == or != with addresses");
        // Wireshark semantics: "ip.addr != X" means "no address is X", so a negated multi-address match is "none match".
        return p =>
        {
            bool any = get(p).Any(a => a is not null && IPAddress.TryParse(a, out var ip) && net.Contains(ip));
            return op == "==" ? any : !any;
        };
    }

    private static Func<DecodedPacket, bool> Num(string value, string op, Func<DecodedPacket, int?[]> get)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) n = hex;
            else throw new FilterSyntaxException($"'{value}' isn't a number");
        }
        Func<int, bool> test = op switch
        {
            "==" => v => v == n, ">" => v => v > n, "<" => v => v < n, ">=" => v => v >= n, "<=" => v => v <= n,
            "!=" => v => v != n,
            _ => throw new FilterSyntaxException($"'{op}' isn't valid for numbers"),
        };
        if (op == "!=") return p => { var vals = get(p).Where(v => v is not null).ToList(); return vals.Count > 0 && vals.All(v => v != n); };
        return p => get(p).Any(v => v is { } x && test(x));
    }

    private static Func<DecodedPacket, bool> Str(string value, string op, Func<DecodedPacket, string?> get) => op switch
    {
        "==" => p => string.Equals(get(p), value, StringComparison.OrdinalIgnoreCase),
        "!=" => p => get(p) is { } s && !string.Equals(s, value, StringComparison.OrdinalIgnoreCase),
        "contains" => p => get(p)?.Contains(value, StringComparison.OrdinalIgnoreCase) == true,
        _ => throw new FilterSyntaxException($"'{op}' isn't valid for text"),
    };

    private static string NormalizeMac(string s) => new(s.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
}
