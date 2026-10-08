namespace OmlTerminal.Core.Wifi;

public sealed record ChannelScore(int Channel, int Networks, double Interference, bool Dfs);

public sealed record ChannelAdvice(WifiBand Band, int BestChannel, string Reason, IReadOnlyList<ChannelScore> Scores);

/// <summary>How crowded each channel is and which one to use. Interference weights each overlapping radio by how loud
/// it is here (a -45 dBm neighbour hurts far more than a -85 dBm one), and 2.4 GHz counts the adjacent-channel overlap
/// of 20 MHz radios (±4 channels).</summary>
public static class ChannelPlanner
{
    public static double Weight(int rssi) => Math.Pow(10, (Math.Clamp(rssi, -95, -20) + 95) / 20.0);

    public static IReadOnlyList<ChannelScore> Scores(IEnumerable<WifiNetwork> networks, WifiBand band, string? excludeSsid = null)
    {
        var nets = networks.Where(n => n.Band == band && (excludeSsid is null || n.Ssid != excludeSsid)).ToList();
        int[] channels = band switch { WifiBand.Band2_4 => WifiMath.Channels2_4, WifiBand.Band5 => WifiMath.Channels5, _ => WifiMath.Channels6 };
        return channels.Select(ch =>
        {
            var touching = nets.Where(n => band == WifiBand.Band2_4
                ? n.CoveredChannels.Any(c => Math.Abs(c - ch) < 5)
                : n.CoveredChannels.Contains(ch)).ToList();
            return new ChannelScore(ch, touching.Count, touching.Sum(n => Weight(n.Rssi)), band == WifiBand.Band5 && WifiMath.IsDfs(ch));
        }).ToList();
    }

    public static ChannelAdvice Advise(IEnumerable<WifiNetwork> networks, WifiBand band, bool allowDfs = false, string? mySsid = null)
    {
        var scores = Scores(networks, band, mySsid);
        IEnumerable<ChannelScore> candidates = band switch
        {
            WifiBand.Band2_4 => scores.Where(s => s.Channel is 1 or 6 or 11),
            WifiBand.Band5 => scores.Where(s => allowDfs || !s.Dfs),
            _ => scores.Where(s => (s.Channel - 5) % 16 == 0), // 6 GHz preferred scanning channels (5, 21, 37 ...)
        };
        var best = candidates.OrderBy(s => s.Interference).ThenBy(s => s.Networks).ThenBy(s => s.Channel).First();
        string reason = best.Networks == 0
            ? "no other networks heard on or overlapping it"
            : $"{best.Networks} network(s) overlap it, the quietest of the {(band == WifiBand.Band2_4 ? "non-overlapping 1/6/11" : allowDfs ? "available" : "non-DFS")} channels";
        return new ChannelAdvice(band, best.Channel, reason, scores);
    }

    /// <summary>Configuration smells a WLAN engineer would flag.</summary>
    public static IReadOnlyList<string> Warnings(IReadOnlyList<WifiNetwork> networks)
    {
        var w = new List<string>();
        var off = networks.Where(n => n.Band == WifiBand.Band2_4 && n.Channel is not (1 or 6 or 11) && n.Channel > 0).ToList();
        if (off.Count > 0)
            w.Add($"{off.Count} 2.4 GHz network(s) on overlapping channels ({string.Join(", ", off.Select(n => n.Channel).Distinct().Order())}) - only 1, 6 and 11 don't overlap.");
        var wide24 = networks.Where(n => n.Band == WifiBand.Band2_4 && n.ChannelWidth >= 40).ToList();
        if (wide24.Count > 0)
            w.Add($"{wide24.Count} 2.4 GHz network(s) use 40 MHz channels, which take two-thirds of the band: {string.Join(", ", wide24.Select(n => n.DisplaySsid).Distinct().Take(4))}.");
        var weak = networks.Where(n => n.Security is "Open" or "WEP" or "WPA" && !n.Hidden).ToList();
        if (weak.Count > 0)
            w.Add($"{weak.Count} network(s) with weak or no security ({string.Join(", ", weak.Select(n => $"{n.DisplaySsid}: {n.Security}").Distinct().Take(4))}).");
        var busy = networks.Where(n => n.ChannelUtilization >= 60).ToList();
        if (busy.Count > 0)
            w.Add($"{busy.Count} access point(s) report their channel over 60% busy: {string.Join(", ", busy.Select(n => $"{n.DisplaySsid} ch {n.Channel} ({n.ChannelUtilization}%)").Distinct().Take(4))}.");
        var crowded = networks.Where(n => n.StationCount >= 30).ToList();
        if (crowded.Count > 0)
            w.Add($"{crowded.Count} access point(s) with 30+ connected clients: {string.Join(", ", crowded.Select(n => $"{n.DisplaySsid} ({n.StationCount})").Distinct().Take(4))}.");
        return w;
    }
}

/// <summary>Signal readings over time per BSSID, for the live graph and for spotting drops.</summary>
public sealed class SignalHistory(int maxSamples = 300)
{
    private readonly Dictionary<string, List<(DateTime At, int Rssi)>> _series = new(StringComparer.OrdinalIgnoreCase);

    public void Add(IEnumerable<WifiNetwork> scan, DateTime at)
    {
        foreach (var n in scan)
        {
            if (!_series.TryGetValue(n.Bssid, out var list)) _series[n.Bssid] = list = new();
            list.Add((at, n.Rssi));
            if (list.Count > maxSamples) list.RemoveAt(0);
        }
    }

    public IReadOnlyList<(DateTime At, int Rssi)> For(string bssid) =>
        _series.TryGetValue(bssid, out var l) ? l : [];
}

/// <summary>Watches the current connection and records roams (BSSID changes) and drops - the events behind most
/// "Wi-Fi keeps cutting out" complaints.</summary>
public sealed class RoamTracker
{
    private WifiConnection? _last;
    public List<string> Events { get; } = new();

    public void Update(WifiConnection? now, DateTime at)
    {
        string t = at.ToString("HH:mm:ss");
        if (_last is null && now is not null) Events.Insert(0, $"{t}  Connected to {now.Ssid} via {now.Bssid} ({now.Rssi} dBm)");
        else if (_last is not null && now is null) Events.Insert(0, $"{t}  Disconnected from {_last.Ssid} (last signal {_last.Rssi} dBm)");
        else if (_last is not null && now is not null && !string.Equals(_last.Bssid, now.Bssid, StringComparison.OrdinalIgnoreCase))
            Events.Insert(0, $"{t}  Roamed {_last.Bssid} ({_last.Rssi} dBm) → {now.Bssid} ({now.Rssi} dBm)");
        if (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
        _last = now;
    }
}
