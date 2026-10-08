namespace OmlTerminal.Core.Snmp;

public sealed record SnmpInterface(int Index, string Name, string Description, string Alias, long SpeedBps, int OperStatus, int AdminStatus, int Type)
{
    public bool Up => OperStatus == 1;
    public string Display => Alias.Length > 0 ? $"{Name} - {Alias}" : Name;
    public string StatusText => OperStatus switch { 1 => "up", 2 => AdminStatus == 2 ? "admin down" : "down", 3 => "testing", 5 => "dormant", 6 => "not present", 7 => "lower layer down", _ => "unknown" };
    /// <summary>Physical-ish ports (ethernet, fibre, serial, LAG, tunnels) rather than loopbacks, nulls and internal stacks.</summary>
    public bool Interesting => Type is 6 or 7 or 22 or 23 or 32 or 37 or 49 or 53 or 62 or 69 or 117 or 131 or 135 or 136 or 161 or 166;
}

/// <summary>One poll of one interface's counters. Uses the 64-bit (HC) octet counters when the agent has them.</summary>
public sealed record IfCounters(DateTime At, ulong SysUpTime, ulong InOctets, ulong OutOctets, bool HighCapacity,
    ulong InErrors, ulong OutErrors, ulong InDiscards, ulong OutDiscards);

public sealed record SystemInfo(string Name, string Description, string Location, TimeSpan Uptime);

/// <summary>Bits per second and errors/discards per second between two polls.</summary>
public sealed record TrafficRate(DateTime At, double InBps, double OutBps, double ErrorsPerSec, double DiscardsPerSec);

/// <summary>IF-MIB (RFC 2863) and MIB-2 system group: discover interfaces, read their counters, turn two reads into rates.</summary>
public static class IfMib
{
    public const string SysDescr = "1.3.6.1.2.1.1.1.0", SysUpTime = "1.3.6.1.2.1.1.3.0", SysName = "1.3.6.1.2.1.1.5.0", SysLocation = "1.3.6.1.2.1.1.6.0";
    public const string IfDescr = "1.3.6.1.2.1.2.2.1.2", IfType = "1.3.6.1.2.1.2.2.1.3", IfSpeed = "1.3.6.1.2.1.2.2.1.5";
    public const string IfAdmin = "1.3.6.1.2.1.2.2.1.7", IfOper = "1.3.6.1.2.1.2.2.1.8";
    public const string IfInOctets = "1.3.6.1.2.1.2.2.1.10", IfInDiscards = "1.3.6.1.2.1.2.2.1.13", IfInErrors = "1.3.6.1.2.1.2.2.1.14";
    public const string IfOutOctets = "1.3.6.1.2.1.2.2.1.16", IfOutDiscards = "1.3.6.1.2.1.2.2.1.19", IfOutErrors = "1.3.6.1.2.1.2.2.1.20";
    public const string IfName = "1.3.6.1.2.1.31.1.1.1.1", IfHcIn = "1.3.6.1.2.1.31.1.1.1.6", IfHcOut = "1.3.6.1.2.1.31.1.1.1.10";
    public const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15", IfAlias = "1.3.6.1.2.1.31.1.1.1.18";

    public static async Task<SystemInfo> SystemAsync(SnmpClient c, CancellationToken ct)
    {
        var v = await c.GetAsync([SysName, SysDescr, SysLocation, SysUpTime], ct).ConfigureAwait(false);
        string S(int i) => i < v.Count && !v[i].Value.IsException ? v[i].Value.ToString() : "";
        ulong ticks = v.Count > 3 && !v[3].Value.IsException ? v[3].Value.ToUInt64() : 0;
        return new SystemInfo(S(0), S(1), S(2), TimeSpan.FromMilliseconds(ticks * 10.0));
    }

    public static async Task<IReadOnlyList<SnmpInterface>> InterfacesAsync(SnmpClient c, CancellationToken ct)
    {
        static Dictionary<int, SnmpValue> ByIndex(IReadOnlyList<SnmpVarbind> rows) =>
            rows.Where(r => !r.Value.IsException).GroupBy(r => int.Parse(r.Oid[(r.Oid.LastIndexOf('.') + 1)..])).ToDictionary(g => g.Key, g => g.First().Value);

        var descr = ByIndex(await c.WalkAsync(IfDescr, ct).ConfigureAwait(false));
        var type = ByIndex(await c.WalkAsync(IfType, ct).ConfigureAwait(false));
        var speed = ByIndex(await c.WalkAsync(IfSpeed, ct).ConfigureAwait(false));
        var admin = ByIndex(await c.WalkAsync(IfAdmin, ct).ConfigureAwait(false));
        var oper = ByIndex(await c.WalkAsync(IfOper, ct).ConfigureAwait(false));
        Dictionary<int, SnmpValue> name = [], alias = [], high = [];
        try
        {
            name = ByIndex(await c.WalkAsync(IfName, ct).ConfigureAwait(false));
            alias = ByIndex(await c.WalkAsync(IfAlias, ct).ConfigureAwait(false));
            high = ByIndex(await c.WalkAsync(IfHighSpeed, ct).ConfigureAwait(false));
        }
        catch (SnmpException) { } // ifXTable is optional (old agents, v1)
        var list = new List<SnmpInterface>();
        foreach (var (idx, d) in descr)
        {
            long bps = high.TryGetValue(idx, out var hs) && hs.ToUInt64() > 0 ? (long)hs.ToUInt64() * 1_000_000
                : speed.TryGetValue(idx, out var s) ? (long)s.ToUInt64() : 0;
            list.Add(new SnmpInterface(idx, name.TryGetValue(idx, out var n) && n.ToString().Length > 0 ? n.ToString() : d.ToString(), d.ToString(),
                alias.TryGetValue(idx, out var a) ? a.ToString() : "", bps,
                oper.TryGetValue(idx, out var o) ? (int)o.ToInt64() : 0, admin.TryGetValue(idx, out var ad) ? (int)ad.ToInt64() : 0,
                type.TryGetValue(idx, out var t) ? (int)t.ToInt64() : 0));
        }
        return list.OrderBy(i => i.Index).ToList();
    }

    /// <summary>Reads counters for the given interfaces in as few requests as possible (GET, batched).</summary>
    public static async Task<IReadOnlyDictionary<int, IfCounters>> CountersAsync(SnmpClient c, IReadOnlyList<int> indexes, bool useHc, CancellationToken ct)
    {
        var result = new Dictionary<int, IfCounters>();
        foreach (var chunk in indexes.Chunk(6))
        {
            var oids = new List<string> { SysUpTime };
            foreach (var i in chunk)
                oids.AddRange([useHc ? $"{IfHcIn}.{i}" : $"{IfInOctets}.{i}", useHc ? $"{IfHcOut}.{i}" : $"{IfOutOctets}.{i}",
                    $"{IfInErrors}.{i}", $"{IfOutErrors}.{i}", $"{IfInDiscards}.{i}", $"{IfOutDiscards}.{i}"]);
            var v = await c.GetAsync(oids, ct).ConfigureAwait(false);
            var at = DateTime.UtcNow;
            ulong up = v.Count > 0 && !v[0].Value.IsException ? v[0].Value.ToUInt64() : 0;
            for (int k = 0; k < chunk.Length; k++)
            {
                int b = 1 + k * 6;
                if (b + 5 >= v.Count) break;
                ulong U(int o) => v[b + o].Value.IsException ? 0 : v[b + o].Value.ToUInt64();
                if (v[b].Value.IsException) continue; // no such interface (any more)
                result[chunk[k]] = new IfCounters(at, up, U(0), U(1), useHc, U(2), U(3), U(4), U(5));
            }
        }
        return result;
    }

    /// <summary>Whether the agent has 64-bit counters (ifHCInOctets) - essential above ~100 Mbit/s, where 32-bit ones wrap in under 6 minutes.</summary>
    public static async Task<bool> HasHighCapacityAsync(SnmpClient c, int anyIndex, CancellationToken ct)
    {
        try
        {
            var v = await c.GetAsync([$"{IfHcIn}.{anyIndex}"], ct).ConfigureAwait(false);
            return v.Count == 1 && !v[0].Value.IsException && v[0].Value.Type == SnmpType.Counter64;
        }
        catch (SnmpException) { return false; }
    }

    /// <summary>Rate between two polls. Handles one counter wrap (32 or 64 bit); returns null after an agent reboot
    /// (sysUpTime went backwards) or a gap too short to measure.</summary>
    public static TrafficRate? Rate(IfCounters a, IfCounters b)
    {
        double secs = (b.At - a.At).TotalSeconds;
        if (b.SysUpTime > 0 && a.SysUpTime > 0)
        {
            if (b.SysUpTime < a.SysUpTime && a.SysUpTime - b.SysUpTime < uint.MaxValue / 2) return null; // rebooted
            double agent = (b.SysUpTime >= a.SysUpTime ? b.SysUpTime - a.SysUpTime : b.SysUpTime + ((ulong)uint.MaxValue + 1 - a.SysUpTime)) / 100.0;
            if (agent > 0.5 && Math.Abs(agent - secs) < secs * 0.5) secs = agent; // the agent's clock is the counters' clock
        }
        if (secs < 0.5) return null;
        double Delta(ulong x, ulong y, bool hc)
        {
            if (y >= x) return y - x;
            return hc ? (double)(ulong.MaxValue - x) + y + 1 : (double)(uint.MaxValue - x) + y + 1;
        }
        bool hc = a.HighCapacity && b.HighCapacity;
        double inB = Delta(a.InOctets, b.InOctets, hc) * 8 / secs, outB = Delta(a.OutOctets, b.OutOctets, hc) * 8 / secs;
        double err = (Delta(a.InErrors, b.InErrors, false) + Delta(a.OutErrors, b.OutErrors, false)) / secs;
        double disc = (Delta(a.InDiscards, b.InDiscards, false) + Delta(a.OutDiscards, b.OutDiscards, false)) / secs;
        return new TrafficRate(b.At, inB, outB, err, disc);
    }
}
