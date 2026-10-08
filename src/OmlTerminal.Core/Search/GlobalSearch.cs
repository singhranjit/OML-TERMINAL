using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Search;

public enum SearchSourceKind { Session, Backup, Log, Snapshot }

public enum QueryKind { Text, Ipv4, Network, Mac }

public sealed record SearchFile(string Path, string Source, SearchSourceKind Kind, DateTime Modified);

/// <param name="LineNumber">1-based; 0 for session matches (no file).</param>
/// <param name="Context">The config section the line sits in ("interface Vlan10"), or the command that produced a log line.</param>
/// <param name="Match">Why it matched: "exact", "in 10.20.30.0/24", "MAC", "text".</param>
public sealed record SearchHit(SearchSourceKind Kind, string Source, string? FilePath, int LineNumber, string Line, string Context, string Match);

/// <summary>What the user typed, classified: an IPv4 address or network is matched by containment (a host IP finds
/// the subnets, ACL wildcards and ranges that cover it), a MAC in any notation, otherwise case-insensitive text.</summary>
public sealed partial class SearchQuery
{
    public string Text { get; }
    public QueryKind Kind { get; }
    public IPAddress? Address { get; }
    public Subnet? Network { get; }
    public string? Mac { get; }

    private SearchQuery(string text, QueryKind kind, IPAddress? address = null, Subnet? network = null, string? mac = null)
    {
        Text = text;
        Kind = kind;
        Address = address;
        Network = network;
        Mac = mac;
    }

    public static SearchQuery Parse(string text)
    {
        var t = text.Trim();
        if (Ipv4.IsIpv4(t)) return new SearchQuery(t, QueryKind.Ipv4, IPAddress.Parse(t));
        if ((t.Contains('/') || t.Contains(' ')) && Subnet.TryParse(t, out var net) && !net.IsV6 && net.OriginalAddress.ToString().Count(c => c == '.') == 3)
            return net.IsHost ? new SearchQuery(t, QueryKind.Ipv4, net.Network) : new SearchQuery(t, QueryKind.Network, network: net);
        if (GlobalSearch.NormalizeMac(t) is { } mac) return new SearchQuery(t, QueryKind.Mac, mac: mac);
        return new SearchQuery(t, QueryKind.Text);
    }
}

public static partial class GlobalSearch
{
    [GeneratedRegex(@"(?<![\d.])(?<ip>\d{1,3}(?:\.\d{1,3}){3})(?:/(?<len>\d{1,2}))?(?![\d.]*\d)")]
    private static partial Regex Ipv4Occurrence();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:.\-])(?:[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}|[0-9A-Fa-f]{2}(?:[:\-][0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{12})(?![0-9A-Fa-f:.\-])")]
    private static partial Regex MacOccurrence();

    /// <summary>"0050.56a1.0001", "00:50:56:A1:00:01", "00-50-56-a1-00-01" and "005056a10001" all → "005056a10001".</summary>
    public static string? NormalizeMac(string s)
    {
        var t = s.Trim();
        if (!MacOccurrence().IsMatch(t) || MacOccurrence().Match(t).Length != t.Length) return null;
        return new string(t.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private sealed record IpSpec(IPAddress Address, int? PrefixLength, IPAddress? RangeEnd);

    /// <summary>Every IPv4 address in a line, pairing "addr mask" / "addr wildcard" / "addr/len" / "addr-addr" forms.</summary>
    private static List<IpSpec> IpSpecs(string line)
    {
        var occ = Ipv4Occurrence().Matches(line).Where(m => IPAddress.TryParse(m.Groups["ip"].Value, out _)).ToList();
        var specs = new List<IpSpec>();
        for (int i = 0; i < occ.Count; i++)
        {
            var m = occ[i];
            var ip = IPAddress.Parse(m.Groups["ip"].Value);
            if (m.Groups["len"].Success)
            {
                int len = int.Parse(m.Groups["len"].Value);
                specs.Add(new IpSpec(ip, len <= 32 ? len : null, null));
                continue;
            }
            if (i + 1 < occ.Count && !occ[i + 1].Groups["len"].Success)
            {
                var between = line[(m.Index + m.Length)..occ[i + 1].Index];
                var next = IPAddress.Parse(occ[i + 1].Groups["ip"].Value);
                if (between.Trim() == "-") { specs.Add(new IpSpec(ip, null, next)); i++; continue; }
                if (between.Trim().Length == 0 && MaskLength(ip, next) is { } len) { specs.Add(new IpSpec(ip, len, null)); i++; continue; }
            }
            specs.Add(new IpSpec(ip, null, null));
        }
        return specs;
    }

    /// <summary>The prefix length a dotted mask or Cisco wildcard means after this address, or null if it isn't one.
    /// "a.b.c.d 0.0.0.0" is the ACL wildcard for a single host (/32), not a /0 netmask - except for 0.0.0.0 itself.</summary>
    private static int? MaskLength(IPAddress ip, IPAddress mask)
    {
        uint m = Ipv4.ToUInt(mask);
        if (m == 0) return Ipv4.ToUInt(ip) == 0 ? 0 : 32;
        try { return Subnet.PrefixFromMask(mask); }
        catch (FormatException) { return null; }
    }

    /// <summary>Why this line matches the query, or null when it doesn't.</summary>
    public static string? MatchLine(string line, SearchQuery q)
    {
        switch (q.Kind)
        {
            case QueryKind.Text:
                return line.Contains(q.Text, StringComparison.OrdinalIgnoreCase) ? "text" : null;
            case QueryKind.Mac:
                if (line.Length < 12) return null;
                foreach (Match m in MacOccurrence().Matches(line))
                    if (NormalizeMac(m.Value) == q.Mac) return "MAC";
                return null;
        }
        if (line.Length < 7 || line.Count(c => c == '.') < 3) return null;
        string? best = null;
        foreach (var spec in IpSpecs(line))
        {
            var reason = q.Kind == QueryKind.Ipv4 ? MatchAddress(spec, q.Address!) : MatchNetwork(spec, q.Network!);
            if (reason == "exact") return reason;
            best ??= reason;
        }
        return best;
    }

    private static string? MatchAddress(IpSpec spec, IPAddress query)
    {
        if (spec.RangeEnd is { } end)
        {
            uint q = Ipv4.ToUInt(query), a = Ipv4.ToUInt(spec.Address), b = Ipv4.ToUInt(end);
            return q >= Math.Min(a, b) && q <= Math.Max(a, b) ? $"in range {spec.Address}-{end}" : null;
        }
        if (spec.Address.Equals(query) && (spec.PrefixLength is null or 32 || spec.PrefixLength < 32 && !IsNetworkAddress(spec)))
            return "exact";
        if (spec.PrefixLength is not { } len || len is 0 or 32) return null;
        var net = new Subnet(Subnet.Mask(spec.Address, len), len);
        return net.Contains(query) ? $"in {net.Cidr}" : null;
    }

    private static bool IsNetworkAddress(IpSpec spec) =>
        spec.PrefixLength is { } len && Subnet.Mask(spec.Address, len).Equals(spec.Address);

    private static string? MatchNetwork(IpSpec spec, Subnet query)
    {
        if (spec.RangeEnd is { } end)
        {
            uint a = Ipv4.ToUInt(spec.Address), b = Ipv4.ToUInt(end), qs = Ipv4.ToUInt(query.Network), qe = Ipv4.ToUInt(query.Broadcast);
            return Math.Min(a, b) <= qe && Math.Max(a, b) >= qs ? $"range {spec.Address}-{end} overlaps" : null;
        }
        if (spec.PrefixLength is not { } len || len == 32 || !IsNetworkAddress(spec) && len < 32)
        {
            // A host address, or an interface address with its mask ("ip address 10.1.1.1 255.255.255.0").
            if (query.Contains(spec.Address)) return spec.PrefixLength is null or 32 ? "address inside" : "interface inside";
            if (spec.PrefixLength is not { } ifLen || ifLen is 0 or 32) return null;
            var ifNet = new Subnet(Subnet.Mask(spec.Address, ifLen), ifLen);
            return ifNet.Contains(query.Network) ? $"in {ifNet.Cidr}" : null;
        }
        if (len == 0) return null;
        var net = new Subnet(spec.Address, len);
        if (net.PrefixLength == query.PrefixLength && net.Network.Equals(query.Network)) return "exact";
        if (net.Contains(query.Network) && net.PrefixLength < query.PrefixLength) return $"covered by {net.Cidr}";
        if (query.Contains(net.Network) && net.PrefixLength > query.PrefixLength) return $"subnet {net.Cidr}";
        return null;
    }

    /// <summary>Lower is more relevant: exact hits first, then the most specific network containing the query - so the
    /// /24 an address lives in outranks the /8 an OSPF "network" statement happens to cover.</summary>
    public static int Relevance(string match)
    {
        if (match is "exact" or "MAC" or "text") return 0;
        if (match.StartsWith("in range", StringComparison.Ordinal) || match.StartsWith("range ", StringComparison.Ordinal)) return 1;
        if (match is "address inside" or "interface inside" || match.StartsWith("subnet ", StringComparison.Ordinal)) return 2;
        int slash = match.LastIndexOf('/');
        return slash > 0 && int.TryParse(match[(slash + 1)..], out var len) && len is >= 0 and <= 32 ? 3 + (32 - len) : 40;
    }

    /// <summary>The config section a line belongs to - each less-indented line above it, nearest first, up to three
    /// levels ("config firewall address › edit "web01"" on FortiOS, "interface Vlan10" on IOS).</summary>
    public static string ConfigContext(IReadOnlyList<string> lines, int index)
    {
        var chain = new List<string>();
        int indent = Indent(lines[index]);
        for (int i = index - 1; i >= 0 && indent > 0 && chain.Count < 3; i--)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t == "!" || t.StartsWith('#') || t is "next" or "end" or "exit" or "}") continue;
            int ind = Indent(lines[i]);
            if (ind < indent) { chain.Insert(0, t); indent = ind; }
        }
        return string.Join(" › ", chain);
    }

    private static int Indent(string l)
    {
        int n = 0;
        while (n < l.Length && (l[n] == ' ' || l[n] == '\t')) n++;
        return n;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][\w.\-()/@:~]{0,62}\s?[#>$]\s?\S")]
    private static partial Regex PromptWithCommand();

    /// <summary>For a session log line: the prompt + command typed most recently above it ("SW1#show ip arp").</summary>
    public static string LogContext(IReadOnlyList<string> lines, int index)
    {
        for (int i = index; i >= 0 && index - i < 5000; i--)
            if (PromptWithCommand().IsMatch(lines[i])) return lines[i].Trim();
        return "";
    }

    public static IEnumerable<SearchHit> SearchLines(IReadOnlyList<string> lines, SearchFile file, SearchQuery q, int maxHits = 200)
    {
        int hits = 0;
        for (int i = 0; i < lines.Count && hits < maxHits; i++)
        {
            if (MatchLine(lines[i], q) is not { } why) continue;
            hits++;
            var context = file.Kind == SearchSourceKind.Log ? LogContext(lines, i) : ConfigContext(lines, i);
            yield return new SearchHit(file.Kind, file.Source, file.Path, i + 1, lines[i].Trim(), context, why);
        }
    }

    public static IReadOnlyList<SearchHit> SearchFile(SearchFile file, SearchQuery q, int maxHits = 200)
    {
        try
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists || info.Length > 64L * 1024 * 1024) return [];
            return SearchLines(ReadAllLinesShared(file.Path), file, q, maxHits).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Reads a file another process (or this app's own session logger) still has open for writing -
    /// File.ReadAllLines refuses those, which silently hid the log of any session still being recorded.</summary>
    public static string[] ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    public static IReadOnlyList<SearchHit> SearchSessions(IEnumerable<SessionProfile> sessions, SearchQuery q)
    {
        var hits = new List<SearchHit>();
        foreach (var s in sessions)
        {
            string? why = q.Kind switch
            {
                QueryKind.Ipv4 when IPAddress.TryParse(s.Host, out var ip) && ip.Equals(q.Address) => "exact",
                QueryKind.Network when IPAddress.TryParse(s.Host, out var ip) && ip.AddressFamily == q.Network!.Network.AddressFamily && q.Network.Contains(ip) => "address inside",
                QueryKind.Text when new[] { s.Name, s.Host, s.Folder, s.Tags, s.Notes }.Any(f => f.Contains(q.Text, StringComparison.OrdinalIgnoreCase)) => "text",
                _ => null,
            };
            if (why is not null)
                hits.Add(new SearchHit(SearchSourceKind.Session, s.Display, null, 0, $"{s.ProtocolLabel}  {s.Host}{(s.Port > 0 ? ":" + s.Port : "")}", s.Notes, why));
        }
        return hits;
    }

    /// <summary>Config backups under each root ({root}/{device}/{device}_{timestamp}.ext). By default only the newest
    /// file per device, since older versions mostly repeat it.</summary>
    public static IReadOnlyList<SearchFile> BackupFiles(IEnumerable<string> roots, bool allVersions)
    {
        var files = new List<SearchFile>();
        foreach (var root in roots.Where(r => r.Length > 0).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirectories(root))
            {
                var device = Path.GetFileName(dir);
                var versions = SafeFiles(dir, "*").Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
                foreach (var f in allVersions ? versions : versions.Take(1))
                    files.Add(new SearchFile(f, device, SearchSourceKind.Backup, File.GetLastWriteTime(f)));
            }
        }
        return files;
    }

    public static IReadOnlyList<SearchFile> LogFiles(string root) =>
        Directory.Exists(root)
            ? SafeFiles(root, "*.log").Select(f => new SearchFile(f, Path.GetFileNameWithoutExtension(f), SearchSourceKind.Log, File.GetLastWriteTime(f)))
                .OrderByDescending(f => f.Modified).ToList()
            : [];

    /// <summary>Change Guard captures: {root}/{change}/{device}/{pre|post}/NN_command.txt.</summary>
    public static IReadOnlyList<SearchFile> SnapshotFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        var files = new List<SearchFile>();
        foreach (var change in SafeDirectories(root))
            foreach (var device in SafeDirectories(change))
                foreach (var phase in SafeDirectories(device))
                {
                    var deviceName = ReadDeviceName(Path.Combine(phase, "meta.json")) ?? Path.GetFileName(device);
                    var label = $"{Path.GetFileName(change)} › {deviceName} ({Path.GetFileName(phase)})";
                    files.AddRange(SafeFiles(phase, "*.txt").Select(f => new SearchFile(f, label, SearchSourceKind.Snapshot, File.GetLastWriteTime(f))));
                }
        return files;
    }

    private static string? ReadDeviceName(string metaPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("DeviceName", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    private static string[] SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); } catch { return []; }
    }
}
