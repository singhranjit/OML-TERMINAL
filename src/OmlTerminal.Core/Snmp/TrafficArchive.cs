using System.Text.Json.Serialization;

namespace OmlTerminal.Core.Snmp;

/// <summary>One graph point: average bits/s in and out over the period, the peak sample inside it, errors and discards per second.</summary>
public sealed record TrafficPoint(
    [property: JsonPropertyName("t")] long Time,
    [property: JsonPropertyName("i")] double In,
    [property: JsonPropertyName("o")] double Out,
    [property: JsonPropertyName("I")] double InMax,
    [property: JsonPropertyName("O")] double OutMax,
    [property: JsonPropertyName("e")] double Errors,
    [property: JsonPropertyName("d")] double Discards)
{
    [JsonIgnore] public DateTime At => DateTimeOffset.FromUnixTimeSeconds(Time).LocalDateTime;
}

public enum TrafficPeriod { Live, Daily, Weekly, Monthly, Yearly }

public sealed record TrafficStats(double InMax, double OutMax, double InAvg, double OutAvg, double InNow, double OutNow,
    double In95, double Out95, double InBytes, double OutBytes, double ErrorsTotal, double DiscardsTotal);

/// <summary>
/// MRTG/RRD-style history for one interface: every sample for 2 days, then 30-minute, 2-hour and 1-day averages
/// (with the peak inside each) for 2 weeks, 2 months and 2 years. Fixed size however long it runs.
/// </summary>
public sealed class TrafficArchive
{
    public static readonly (int Seconds, TimeSpan Keep)[] Tiers =
    [
        (0, TimeSpan.FromDays(2)),           // raw polls
        (1800, TimeSpan.FromDays(14)),       // 30 min
        (7200, TimeSpan.FromDays(62)),       // 2 h
        (86400, TimeSpan.FromDays(800)),     // 1 day
    ];

    public List<TrafficPoint> Raw { get; set; } = [];
    public List<TrafficPoint> HalfHour { get; set; } = [];
    public List<TrafficPoint> TwoHour { get; set; } = [];
    public List<TrafficPoint> Day { get; set; } = [];

    private List<TrafficPoint> Tier(int i) => i switch { 0 => Raw, 1 => HalfHour, 2 => TwoHour, _ => Day };

    public void Add(TrafficRate r)
    {
        long t = new DateTimeOffset(r.At).ToUnixTimeSeconds();
        Raw.Add(new TrafficPoint(t, r.InBps, r.OutBps, r.InBps, r.OutBps, r.ErrorsPerSec, r.DiscardsPerSec));
        Trim(Raw, t, Tiers[0].Keep);
        // Roll completed buckets up into the coarser tiers.
        // Bucket sizes nest (30 min | 2 h | 1 day), so each tier is built from the finished buckets of the one below.
        for (int i = 1; i < Tiers.Length; i++)
        {
            var tier = Tier(i);
            int size = Tiers[i].Seconds;
            long current = Bucket(t, size);
            long next = tier.Count > 0 ? tier[^1].Time + size : long.MinValue;
            foreach (var g in Tier(i - 1).Where(p => p.Time >= next && p.Time < current).GroupBy(p => Bucket(p.Time, size)).OrderBy(g => g.Key))
                tier.Add(Merge(g.Key, g.ToList()));
            Trim(tier, t, Tiers[i].Keep);
        }
    }

    /// <summary>Start of the bucket holding <paramref name="t"/>, aligned to local time - a "day" is your day, not UTC's.</summary>
    public static long Bucket(long t, int size)
    {
        long offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime).TotalSeconds;
        return (t + offset) / size * size - offset;
    }

    private static TrafficPoint Merge(long time, List<TrafficPoint> pts) => new(time,
        pts.Average(p => p.In), pts.Average(p => p.Out), pts.Max(p => p.InMax), pts.Max(p => p.OutMax), pts.Average(p => p.Errors), pts.Average(p => p.Discards));

    private static void Trim(List<TrafficPoint> list, long now, TimeSpan keep)
    {
        long cutoff = now - (long)keep.TotalSeconds;
        int n = 0;
        while (n < list.Count && list[n].Time < cutoff) n++;
        if (n > 0) list.RemoveRange(0, n);
    }

    public static TimeSpan Span(TrafficPeriod p) => p switch
    {
        TrafficPeriod.Live => TimeSpan.FromMinutes(15), TrafficPeriod.Daily => TimeSpan.FromHours(24), TrafficPeriod.Weekly => TimeSpan.FromDays(7),
        TrafficPeriod.Monthly => TimeSpan.FromDays(31), _ => TimeSpan.FromDays(365),
    };

    /// <summary>The points for a graph - the MRTG "daily/weekly/monthly/yearly" views - including the current, unfinished bucket.</summary>
    public IReadOnlyList<TrafficPoint> Points(TrafficPeriod p, DateTime? now = null)
    {
        long end = new DateTimeOffset(now ?? DateTime.Now).ToUnixTimeSeconds();
        long start = end - (long)Span(p).TotalSeconds;
        int tier = p switch { TrafficPeriod.Live or TrafficPeriod.Daily => 0, TrafficPeriod.Weekly => 1, TrafficPeriod.Monthly => 2, _ => 3 };
        var list = Tier(tier).Where(x => x.Time >= start).ToList();
        if (tier > 0)
        {
            int size = Tiers[tier].Seconds;
            long from = list.Count > 0 ? list[^1].Time + size : Bucket(start, size);
            var partial = Tier(tier - 1).Where(x => x.Time >= from).ToList();
            if (partial.Count > 0) list.Add(Merge(from, partial));
        }
        return list;
    }

    public static TrafficStats Stats(IReadOnlyList<TrafficPoint> pts, IReadOnlyList<TrafficPoint>? latest = null)
    {
        if (pts.Count == 0) return new TrafficStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        double Pct(Func<TrafficPoint, double> f)
        {
            var sorted = pts.Select(f).OrderBy(x => x).ToList();
            return sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1)];
        }
        double bytesIn = 0, bytesOut = 0, err = 0, disc = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double dt = i + 1 < pts.Count ? pts[i + 1].Time - pts[i].Time : (i > 0 ? pts[i].Time - pts[i - 1].Time : 60);
            dt = Math.Min(dt, 86400);
            bytesIn += pts[i].In * dt / 8;
            bytesOut += pts[i].Out * dt / 8;
            err += pts[i].Errors * dt;
            disc += pts[i].Discards * dt;
        }
        var now = (latest ?? pts)[^1];
        return new TrafficStats(pts.Max(p => p.InMax), pts.Max(p => p.OutMax), pts.Average(p => p.In), pts.Average(p => p.Out),
            now.In, now.Out, Pct(p => p.In), Pct(p => p.Out), bytesIn, bytesOut, err, disc);
    }

    public static string Bits(double bps) => bps >= 1e9 ? $"{bps / 1e9:0.##} Gb/s" : bps >= 1e6 ? $"{bps / 1e6:0.##} Mb/s" : bps >= 1e3 ? $"{bps / 1e3:0.#} kb/s" : $"{bps:0} b/s";
    public static string Bytes(double b) => b >= 1e12 ? $"{b / 1e12:0.##} TB" : b >= 1e9 ? $"{b / 1e9:0.##} GB" : b >= 1e6 ? $"{b / 1e6:0.#} MB" : b >= 1e3 ? $"{b / 1e3:0.#} kB" : $"{b:0} B";
}
