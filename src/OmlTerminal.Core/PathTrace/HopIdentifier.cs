using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.PathTrace;

/// <summary>
/// Puts a name on a traceroute hop: one of your own devices (a saved session's address, or an interface address in
/// a config backup - which also tells you the exact interface), a private or carrier-NAT range, or a public network's
/// AS number and owner (Team Cymru's DNS-based IP-to-ASN service - one TXT lookup, no API key).
/// </summary>
public sealed partial class HopIdentifier
{
    private readonly Func<IEnumerable<SessionProfile>> _sessions;
    private readonly string? _backupRoot;
    private readonly bool _lookupAsn;
    private Dictionary<string, (string Device, string Interface)>? _interfaceMap;
    private DateTime _mapBuilt;
    private readonly object _mapLock = new();
    private static readonly ConcurrentDictionary<string, HopOwner?> AsnCache = new();

    public HopIdentifier(Func<IEnumerable<SessionProfile>> sessions, string? backupRoot, bool lookupAsn = true)
    {
        _sessions = sessions;
        _backupRoot = backupRoot;
        _lookupAsn = lookupAsn;
    }

    public async Task<HopOwner?> IdentifyAsync(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        var map = InterfaceMap();
        if (map.TryGetValue(ip, out var hit))
            return new HopOwner(HopOwnerKind.Device, $"{hit.Device} {hit.Interface}", hit.Device, hit.Interface);
        var session = _sessions().FirstOrDefault(s => s.Host == ip);
        if (session is not null) return new HopOwner(HopOwnerKind.Device, session.Name, session.Name);

        var range = Classify(addr);
        if (range is not null) return range;
        if (!_lookupAsn) return new HopOwner(HopOwnerKind.Public, "public");
        return await AsnAsync(addr).ConfigureAwait(false);
    }

    /// <summary>The device and interface whose config (newest backup) carries this address, if any.</summary>
    public (string Device, string Interface)? OwnerOf(string ip) => InterfaceMap().TryGetValue(ip, out var hit) ? hit : null;

    /// <summary>Private, carrier-NAT, link-local etc. - or null for a public address.</summary>
    public static HopOwner? Classify(IPAddress a)
    {
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal ? new HopOwner(HopOwnerKind.Private, "private (IPv6)") : null;
        var b = a.GetAddressBytes();
        if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168))
            return new HopOwner(HopOwnerKind.Private, "private network");
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return new HopOwner(HopOwnerKind.CarrierNat, "ISP carrier-grade NAT");
        if (b[0] == 169 && b[1] == 254) return new HopOwner(HopOwnerKind.Private, "link-local");
        if (b[0] == 127) return new HopOwner(HopOwnerKind.Private, "loopback");
        if (b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113)
            return new HopOwner(HopOwnerKind.Private, "documentation range");
        return null;
    }

    private static async Task<HopOwner?> AsnAsync(IPAddress a)
    {
        var key = a.ToString();
        if (AsnCache.TryGetValue(key, out var cached)) return cached;
        HopOwner? owner = new(HopOwnerKind.Public, "public");
        try
        {
            var b = a.GetAddressBytes();
            var origin = await DnsLookup.QueryAsync($"{b[3]}.{b[2]}.{b[1]}.{b[0]}.origin.asn.cymru.com", DnsRecordType.TXT, timeoutMs: 2500).ConfigureAwait(false);
            var txt = origin.Records.FirstOrDefault(r => r.Type == DnsRecordType.TXT && r.Section == "ANSWER")?.Data;
            if (ParseOrigin(txt) is { } o)
            {
                string? asName = null;
                var asn = await DnsLookup.QueryAsync($"AS{o.Asn}.asn.cymru.com", DnsRecordType.TXT, timeoutMs: 2500).ConfigureAwait(false);
                asName = ParseAsName(asn.Records.FirstOrDefault(r => r.Type == DnsRecordType.TXT && r.Section == "ANSWER")?.Data);
                owner = new HopOwner(HopOwnerKind.Public, asName is null ? $"AS{o.Asn}" : $"AS{o.Asn} {asName}", Asn: o.Asn, AsName: asName);
            }
        }
        catch { }
        AsnCache[key] = owner;
        return owner;
    }

    /// <summary>"15169 | 8.8.8.0/24 | US | arin | 2023-12-28" → 15169 (the first AS when several originate it).</summary>
    public static (int Asn, string Prefix)? ParseOrigin(string? txt)
    {
        if (string.IsNullOrWhiteSpace(txt)) return null;
        var parts = txt.Trim('"', ' ').Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2) return null;
        var first = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var asn) ? (asn, parts[1]) : null;
    }

    /// <summary>"15169 | US | arin | 2000-03-30 | GOOGLE - Google LLC, US" → "GOOGLE - Google LLC".</summary>
    public static string? ParseAsName(string? txt)
    {
        if (string.IsNullOrWhiteSpace(txt)) return null;
        var parts = txt.Trim('"', ' ').Split('|');
        if (parts.Length < 5) return null;
        var name = parts[4].Trim();
        var comma = name.LastIndexOf(',');
        if (comma > 0 && name.Length - comma <= 4) name = name[..comma]; // drop the trailing ", US"
        return name.Length == 0 ? null : name;
    }

    // ---------- interface addresses from config backups ----------

    private Dictionary<string, (string Device, string Interface)> InterfaceMap()
    {
        lock (_mapLock)
        {
            // Re-read now and then so backups taken while a trace tab is open are picked up.
            if (_interfaceMap is not null && DateTime.UtcNow - _mapBuilt < TimeSpan.FromMinutes(5)) return _interfaceMap;
            _mapBuilt = DateTime.UtcNow;
            _interfaceMap = new(StringComparer.Ordinal);
            if (_backupRoot is null || !Directory.Exists(_backupRoot)) return _interfaceMap;
            foreach (var deviceDir in Directory.EnumerateDirectories(_backupRoot))
            {
                var newest = Directory.EnumerateFiles(deviceDir).OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
                if (newest is null) continue;
                try
                {
                    var device = Path.GetFileName(deviceDir);
                    foreach (var (ip, iface) in InterfaceAddresses(File.ReadAllText(newest)))
                        _interfaceMap.TryAdd(ip, (device, iface));
                }
                catch (IOException) { }
            }
            return _interfaceMap;
        }
    }

    [GeneratedRegex(@"^interface\s+(?<name>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex IosInterface();

    [GeneratedRegex(@"^\s+ip(?:v4)? address\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})(?:[/\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex IosAddress();

    [GeneratedRegex(@"^set interfaces (?<if>\S+) unit (?<unit>\d+) family inet address (?<ip>\d{1,3}(?:\.\d{1,3}){3})/")]
    private static partial Regex JunosAddress();

    [GeneratedRegex(@"^\s*edit ""(?<name>[^""]+)""")]
    private static partial Regex FortiEdit();

    [GeneratedRegex(@"^\s*set ip (?<ip>\d{1,3}(?:\.\d{1,3}){3})\s")]
    private static partial Regex FortiIp();

    /// <summary>Interface → address pairs from a Cisco-style (IOS/NX-OS/ASA), Junos set-format or FortiOS config.</summary>
    public static IEnumerable<(string Ip, string Interface)> InterfaceAddresses(string config)
    {
        string? current = null;
        bool fortiInterfaces = false;
        foreach (var line in TextLines.Split(config))
        {
            if (line.StartsWith("config system interface", StringComparison.Ordinal)) { fortiInterfaces = true; continue; }
            if (fortiInterfaces && line.StartsWith("end", StringComparison.Ordinal)) { fortiInterfaces = false; continue; }
            if (fortiInterfaces)
            {
                if (FortiEdit().Match(line) is { Success: true } fe) current = fe.Groups["name"].Value;
                else if (current is not null && FortiIp().Match(line) is { Success: true } fi) yield return (fi.Groups["ip"].Value, current);
                continue;
            }
            if (JunosAddress().Match(line) is { Success: true } j)
            {
                yield return (j.Groups["ip"].Value, $"{j.Groups["if"].Value}.{j.Groups["unit"].Value}");
                continue;
            }
            if (IosInterface().Match(line) is { Success: true } i) { current = i.Groups["name"].Value; continue; }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { current = null; continue; }
            if (current is not null && IosAddress().Match(line) is { Success: true } a && !line.Contains("secondary dhcp"))
                yield return (a.Groups["ip"].Value, current);
        }
    }
}
