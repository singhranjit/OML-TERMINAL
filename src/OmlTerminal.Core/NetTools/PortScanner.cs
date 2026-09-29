using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OmlTerminal.Core.NetTools;

public enum PortProtocol { Tcp, Udp }

public enum PortState { Open, Closed, Filtered, OpenOrFiltered }

public sealed record PortProbeResult(string Host, int Port, PortProtocol Protocol, PortState State, TimeSpan Latency, string Banner = "")
{
    public string Service => WellKnownPorts.NameFor(Port, Protocol);
    public string StateText => State switch
    {
        PortState.Open => "LISTENING",
        PortState.Closed => "NOT LISTENING",
        PortState.Filtered => "FILTERED",
        _ => "LISTENING or FILTERED",
    };
}

/// <summary>Parses port lists the way engineers type them: "22", "22,80,443", "8000-8010", "tcp/443, udp/161",
/// or a preset name from <see cref="WellKnownPorts.Presets"/> ("common", "web", "mgmt", "mail", "db", "windows").</summary>
public static class PortSpec
{
    public const int MaxPorts = 65535;

    public static IReadOnlyList<int> Parse(string text)
    {
        var ports = new SortedSet<int>();
        foreach (var raw in text.Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.StartsWith("tcp/") || token.StartsWith("udp/")) token = token[4..];
            if (WellKnownPorts.Presets.TryGetValue(token, out var preset)) { ports.UnionWith(preset); continue; }

            int dash = token.IndexOf('-');
            if (dash > 0)
            {
                if (!int.TryParse(token[..dash], out var a) || !int.TryParse(token[(dash + 1)..], out var b))
                    throw new FormatException($"'{raw}' is not a valid port range.");
                if (a > b) (a, b) = (b, a);
                Check(a, raw); Check(b, raw);
                for (int p = a; p <= b; p++) ports.Add(p);
            }
            else
            {
                if (!int.TryParse(token, out var p)) throw new FormatException($"'{raw}' is not a port number or preset.");
                Check(p, raw);
                ports.Add(p);
            }
        }
        return ports.ToList();
    }

    private static void Check(int p, string raw)
    {
        if (p is < 1 or > MaxPorts) throw new FormatException($"'{raw}': ports must be between 1 and 65535.");
    }
}

/// <summary>TCP connect / UDP probe scanner. Connect-based on purpose - no raw sockets, no admin rights, and it's
/// the same test PortQry and Test-NetConnection perform, so results match what firewall teams expect.</summary>
public sealed class PortScanner
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public int Concurrency { get; init; } = 64;
    public bool GrabBanner { get; init; }

    public async Task ScanAsync(string host, IReadOnlyList<int> ports, PortProtocol protocol,
        Action<PortProbeResult> onResult, CancellationToken ct = default)
    {
        var address = await ResolveAsync(host, ct).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = ports.Select(async port =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = protocol == PortProtocol.Tcp
                    ? await ProbeTcpAsync(host, address, port, ct).ConfigureAwait(false)
                    : await ProbeUdpAsync(host, address, port, ct).ConfigureAwait(false);
                onResult(r);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host.Trim(), out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host.Trim(), ct).ConfigureAwait(false);
        return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not resolve {host}.");
    }

    /// <summary>SIO_TCP_INITIAL_RTO. Windows re-sends the SYN for ~2s after a RST before failing the connect, so
    /// a closed port outlives any sane timeout and would be reported as "filtered".</summary>
    private const int SIO_TCP_INITIAL_RTO = unchecked((int)0x98000011);

    /// <summary>TCP_INITIAL_RTO_PARAMETERS { Rtt = unspecified, MaxSynRetransmissions = NO_SYN_RETRANSMISSIONS }:
    /// a RST now fails the connect immediately. A dropped SYN simply runs into Timeout, which is still "filtered".</summary>
    private static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

    private static void DisableSynRetries(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { socket.IOControl(SIO_TCP_INITIAL_RTO, NoSynRetransmissions, null); } catch (SocketException) { }
    }

    public async Task<PortProbeResult> ProbeTcpAsync(string host, IPAddress address, int port, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        DisableSynRetries(socket);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
            var latency = sw.Elapsed;
            var banner = GrabBanner ? await ReadBannerAsync(socket, ct).ConfigureAwait(false) : "";
            return new PortProbeResult(host, port, PortProtocol.Tcp, PortState.Open, latency, banner);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new PortProbeResult(host, port, PortProtocol.Tcp, PortState.Closed, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PortProbeResult(host, port, PortProtocol.Tcp, PortState.Filtered, sw.Elapsed);
        }
        catch (SocketException)
        {
            return new PortProbeResult(host, port, PortProtocol.Tcp, PortState.Filtered, sw.Elapsed);
        }
    }

    private static async Task<string> ReadBannerAsync(Socket socket, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(900);
        var buf = new byte[256];
        try
        {
            int n = await socket.ReceiveAsync(buf, SocketFlags.None, cts.Token).ConfigureAwait(false);
            return Printable(Encoding.ASCII.GetString(buf, 0, n));
        }
        catch { return ""; }
    }

    public static string Printable(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '\r' or '\n') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else if (c >= 32 && c < 127) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>UDP has no handshake: an ICMP port-unreachable (surfaced by Windows as ConnectionReset on a
    /// connected socket) means closed, a reply means open, silence means open-or-filtered - same verdicts as PortQry.
    /// Protocol-aware probes are sent for DNS/NTP/SNMP so those services actually answer.</summary>
    public async Task<PortProbeResult> ProbeUdpAsync(string host, IPAddress address, int port, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            await socket.SendAsync(UdpProbe(port), SocketFlags.None, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var buf = new byte[1500];
            int n = await socket.ReceiveAsync(buf, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            return new PortProbeResult(host, port, PortProtocol.Udp, PortState.Open, sw.Elapsed,
                GrabBanner ? $"{n} byte reply" : "");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return new PortProbeResult(host, port, PortProtocol.Udp, PortState.Closed, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PortProbeResult(host, port, PortProtocol.Udp, PortState.OpenOrFiltered, sw.Elapsed);
        }
        catch (SocketException)
        {
            return new PortProbeResult(host, port, PortProtocol.Udp, PortState.OpenOrFiltered, sw.Elapsed);
        }
    }

    public static byte[] UdpProbe(int port) => port switch
    {
        // DNS: standard query for "." NS
        53 => [0x13, 0x37, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01],
        // NTP v3 client request
        123 => [0x1b, .. new byte[47]],
        // SNMPv1 get-request sysDescr.0, community "public"
        161 => [0x30, 0x26, 0x02, 0x01, 0x00, 0x04, 0x06, 0x70, 0x75, 0x62, 0x6c, 0x69, 0x63, 0xa0, 0x19, 0x02, 0x01, 0x01,
                0x02, 0x01, 0x00, 0x02, 0x01, 0x00, 0x30, 0x0e, 0x30, 0x0c, 0x06, 0x08, 0x2b, 0x06, 0x01, 0x02, 0x01, 0x01,
                0x01, 0x00, 0x05, 0x00],
        _ => [0x00],
    };
}

public static class WellKnownPorts
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [20] = "ftp-data", [21] = "ftp", [22] = "ssh", [23] = "telnet", [25] = "smtp", [49] = "tacacs", [53] = "dns",
        [67] = "dhcp", [69] = "tftp", [80] = "http", [88] = "kerberos", [110] = "pop3", [111] = "rpcbind", [123] = "ntp",
        [135] = "msrpc", [137] = "netbios-ns", [139] = "netbios-ssn", [143] = "imap", [161] = "snmp", [162] = "snmptrap",
        [179] = "bgp", [389] = "ldap", [443] = "https", [445] = "smb", [465] = "smtps", [500] = "isakmp", [514] = "syslog",
        [515] = "lpd", [541] = "fortimanager", [587] = "submission", [636] = "ldaps", [830] = "netconf", [853] = "dns-over-tls",
        [993] = "imaps", [995] = "pop3s", [1433] = "mssql", [1521] = "oracle", [1645] = "radius", [1646] = "radius-acct",
        [1723] = "pptp", [1812] = "radius", [1813] = "radius-acct", [2049] = "nfs", [2222] = "ssh-alt", [3128] = "squid",
        [3306] = "mysql", [3389] = "rdp", [4500] = "ipsec-nat-t", [5060] = "sip", [5061] = "sips", [5432] = "postgres",
        [5900] = "vnc", [5985] = "winrm", [5986] = "winrm-https", [6379] = "redis", [6443] = "kube-api", [8000] = "http-alt",
        [8008] = "http-alt", [8080] = "http-proxy", [8443] = "https-alt", [8888] = "http-alt", [9000] = "http-alt",
        [9090] = "http-alt", [9200] = "elasticsearch", [9443] = "https-alt", [10443] = "fortigate-sslvpn", [27017] = "mongodb",
    };

    public static string NameFor(int port, PortProtocol protocol = PortProtocol.Tcp) => Names.GetValueOrDefault(port, "");

    public static readonly IReadOnlyDictionary<string, int[]> Presets = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["web"] = [80, 443, 8000, 8008, 8080, 8443, 8888, 9443],
        ["mgmt"] = [22, 23, 80, 161, 443, 830, 3389, 5900, 5985, 5986, 8443],
        ["mail"] = [25, 110, 143, 465, 587, 993, 995],
        ["db"] = [1433, 1521, 3306, 5432, 6379, 9200, 27017],
        ["windows"] = [53, 88, 135, 139, 389, 445, 464, 636, 3268, 3269, 3389, 5985, 5986],
        ["common"] = [21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 179, 389, 443, 445, 465, 514, 587, 636, 830, 993, 995,
                      1433, 1521, 1723, 3306, 3389, 5432, 5900, 5985, 6379, 8080, 8443, 9200, 10443],
    };
}
