using System.Net;
using System.Net.NetworkInformation;
using OmlTerminal.Core.PathTrace;

namespace OmlTerminal.Core.Monitoring;

public enum PingState { Unknown, Up, Degraded, Down }

/// <summary>A host to watch. Persisted in settings, so plain get/set properties.</summary>
public sealed class PingTarget
{
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
}

public sealed record PingOutage(string Host, string Name, DateTime Start, DateTime? End, string Reason)
{
    public TimeSpan Duration => (End ?? DateTime.Now) - Start;
}

/// <summary>Live state of one monitored host. Loss and latency are over a sliding window so the colour reflects now,
/// while the totals (and availability) cover the whole session.</summary>
public sealed class PingHostState
{
    public const int Window = 60;
    public const int History = 3600;

    public required PingTarget Target { get; init; }
    public IPAddress? Address { get; internal set; }
    public PingState State { get; internal set; } = PingState.Unknown;
    public DateTime StateSince { get; internal set; } = DateTime.Now;
    public long Sent { get; internal set; }
    public long Received { get; internal set; }
    public double? Last { get; internal set; }
    public double Best { get; internal set; } = double.NaN;
    public double Worst { get; internal set; } = double.NaN;
    public int ConsecutiveLost { get; internal set; }
    public string LastError { get; internal set; } = "";
    public List<(DateTime At, double? Rtt)> Samples { get; } = [];
    public int Outages { get; internal set; }

    public double TotalLoss => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public double Availability => 100 - TotalLoss;

    private IEnumerable<(DateTime At, double? Rtt)> Recent => Samples.Skip(Math.Max(0, Samples.Count - Window));
    public double RecentLoss { get { var r = Recent.ToList(); return r.Count == 0 ? 0 : 100.0 * r.Count(s => s.Rtt is null) / r.Count; } }
    public double RecentAvg { get { var r = Recent.Where(s => s.Rtt is not null).Select(s => s.Rtt!.Value).ToList(); return r.Count == 0 ? double.NaN : r.Average(); } }
    public double RecentJitter
    {
        get
        {
            var r = Recent.Where(s => s.Rtt is not null).Select(s => s.Rtt!.Value).ToList();
            if (r.Count < 2) return double.NaN;
            double sum = 0;
            for (int i = 1; i < r.Count; i++) sum += Math.Abs(r[i] - r[i - 1]);
            return sum / (r.Count - 1);
        }
    }
    public double Mos => PathDiagnosis.Mos(RecentAvg, double.IsNaN(RecentJitter) ? 0 : RecentJitter, RecentLoss);
}

public sealed record PingHostView(PingHostState Source, string Host, string Name, string Group, string? Address, PingState State, DateTime StateSince,
    long Sent, long Received, double? Last, double Best, double Worst, double Avg, double Jitter, double RecentLoss, double TotalLoss, double Mos,
    int Outages, string LastError, IReadOnlyList<(DateTime At, double? Rtt)> Samples)
{
    public double Availability => 100 - TotalLoss;
    public string Display => Name.Length > 0 ? Name : Host;
}

public sealed class PingMonitorOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public int TimeoutMs { get; set; } = 1000;
    public int PacketSize { get; set; } = 32;
    /// <summary>Lost replies in a row before a host counts as down.</summary>
    public int DownAfter { get; set; } = 3;
    /// <summary>Loss (over the window, once there are 10+ samples) or average latency above which an up host counts as degraded.</summary>
    public double DegradedLossPercent { get; set; } = 5;
    public double DegradedLatencyMs { get; set; } = 150;
    public int Parallel { get; set; } = 64;
}

/// <summary>
/// Pings many hosts continuously (each on its own schedule, so one slow host never delays the rest) and tracks
/// up/degraded/down with outage history - a PingPlotter/"Pinger" style multi-host monitor. No admin rights needed.
/// </summary>
public sealed class PingMonitor
{
    private readonly object _sync = new();
    private readonly List<PingHostState> _hosts = [];
    private readonly List<PingOutage> _outages = [];
    private readonly IProbeSender _sender;

    public PingMonitorOptions Options { get; }
    /// <summary>Raised (background thread) when a host changes state: (host, old state, new state).</summary>
    public event Action<PingHostState, PingState, PingState>? StateChanged;
    public event Action? Updated;

    public PingMonitor(PingMonitorOptions? options = null, IProbeSender? sender = null)
    {
        Options = options ?? new PingMonitorOptions();
        _sender = sender ?? new IcmpProbeSender();
    }

    public IReadOnlyList<PingHostState> Hosts { get { lock (_sync) return _hosts.ToList(); } }
    public IReadOnlyList<PingOutage> Outages { get { lock (_sync) return _outages.ToList(); } }

    /// <summary>A consistent copy of every host's numbers (the probe tasks keep writing while the UI reads).</summary>
    public IReadOnlyList<PingHostView> Snapshot(int samples = 300)
    {
        lock (_sync) return _hosts.Select(h => View(h, samples)).ToList();
    }

    /// <summary>One host with a longer history (the detail graph) - without copying every other host's samples.</summary>
    public PingHostView? Snapshot(PingHostState host, int samples)
    {
        lock (_sync) return _hosts.Contains(host) ? View(host, samples) : null;
    }

    private static PingHostView View(PingHostState h, int samples) => new(h, h.Target.Host, h.Target.Name, h.Target.Group, h.Address?.ToString(), h.State, h.StateSince,
                h.Sent, h.Received, h.Last, h.Best, h.Worst, h.RecentAvg, h.RecentJitter, h.RecentLoss, h.TotalLoss, h.Mos, h.Outages, h.LastError,
                h.Samples.Skip(Math.Max(0, h.Samples.Count - samples)).ToList());

    public PingHostState Add(PingTarget t)
    {
        lock (_sync)
        {
            var existing = _hosts.FirstOrDefault(h => h.Target.Host.Equals(t.Host, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            var s = new PingHostState { Target = t };
            _hosts.Add(s);
            return s;
        }
    }

    public void Remove(PingHostState s) { lock (_sync) _hosts.Remove(s); }

    public void ResetStats()
    {
        lock (_sync)
        {
            foreach (var h in _hosts)
            {
                h.Sent = h.Received = 0;
                h.Samples.Clear();
                h.Best = h.Worst = double.NaN;
                h.Outages = 0;
            }
            _outages.Clear();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, Options.Parallel));
        var running = new Dictionary<PingHostState, Task>();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.Now;
            foreach (var h in Hosts)
            {
                if (running.TryGetValue(h, out var t) && !t.IsCompleted) continue; // still waiting on the last probe
                running[h] = ProbeAsync(h, gate, ct);
            }
            foreach (var gone in running.Keys.Except(Hosts).ToList()) running.Remove(gone);
            Updated?.Invoke();
            var wait = Options.Interval - (DateTime.Now - started);
            try { await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One probe of one host - public so tests (and "ping now") can drive it without the loop.</summary>
    public async Task ProbeAsync(PingHostState h, SemaphoreSlim? gate = null, CancellationToken ct = default)
    {
        if (gate is not null) await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (h.Address is null && !await ResolveAsync(h, ct).ConfigureAwait(false))
            {
                Record(h, null, "can't resolve name");
                return;
            }
            var r = await _sender.SendAsync(h.Address!, 128, Options.TimeoutMs, Options.PacketSize, ct).ConfigureAwait(false);
            if (r.Status == IPStatus.Success) Record(h, Math.Round(r.RttMs, 2), "");
            else
            {
                Record(h, null, r.Status switch
                {
                    IPStatus.TimedOut => "timed out",
                    IPStatus.DestinationHostUnreachable => $"host unreachable{(r.From is null ? "" : $" (from {r.From})")}",
                    IPStatus.DestinationNetworkUnreachable => $"network unreachable{(r.From is null ? "" : $" (from {r.From})")}",
                    IPStatus.TtlExpired => "TTL expired in transit (routing loop?)",
                    var s => s.ToString(),
                });
                if (!IPAddress.TryParse(h.Target.Host, out _)) h.Address = null; // re-resolve names after a failure
            }
        }
        catch (OperationCanceledException) { }
        finally { gate?.Release(); }
    }

    private static async Task<bool> ResolveAsync(PingHostState h, CancellationToken ct)
    {
        if (IPAddress.TryParse(h.Target.Host, out var ip)) { h.Address = ip; return true; }
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(h.Target.Host, ct).ConfigureAwait(false);
            h.Address = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            return h.Address is not null;
        }
        catch { return false; }
    }

    internal void Record(PingHostState h, double? rtt, string error)
    {
        PingState old, now;
        var at = DateTime.Now;
        lock (_sync)
        {
            h.Sent++;
            h.Last = rtt;
            h.Samples.Add((at, rtt));
            if (h.Samples.Count > PingHostState.History) h.Samples.RemoveRange(0, h.Samples.Count - PingHostState.History);
            if (rtt is { } v)
            {
                h.Received++;
                h.ConsecutiveLost = 0;
                h.Best = double.IsNaN(h.Best) ? v : Math.Min(h.Best, v);
                h.Worst = double.IsNaN(h.Worst) ? v : Math.Max(h.Worst, v);
                h.LastError = "";
            }
            else
            {
                h.ConsecutiveLost++;
                h.LastError = error;
            }
            old = h.State;
            now = h.ConsecutiveLost >= Options.DownAfter ? PingState.Down
                : rtt is null && old is PingState.Down ? PingState.Down
                : rtt is null && old is PingState.Unknown ? PingState.Unknown
                : (h.Samples.Count >= 10 && h.RecentLoss >= Options.DegradedLossPercent) || h.RecentAvg >= Options.DegradedLatencyMs ? PingState.Degraded
                : PingState.Up;
            if (now != old)
            {
                h.State = now;
                h.StateSince = at;
                if (now == PingState.Down)
                {
                    h.Outages++;
                    // The outage began with the first of the lost replies, not when we decided it was down.
                    var start = h.Samples.Count >= Options.DownAfter ? h.Samples[^Options.DownAfter].At : at;
                    _outages.Add(new PingOutage(h.Target.Host, h.Target.Name, start, null, error));
                }
                else if (old == PingState.Down)
                {
                    int i = _outages.FindLastIndex(o => o.Host == h.Target.Host && o.End is null);
                    if (i >= 0) _outages[i] = _outages[i] with { End = at };
                }
            }
        }
        if (now != old) StateChanged?.Invoke(h, old, now);
    }

    /// <summary>Parses a pasted list: one host per line, optionally "host,name[,group]" or "host name" (tab/space).</summary>
    public static IReadOnlyList<PingTarget> ParseList(string text)
    {
        var list = new List<PingTarget>();
        foreach (var raw in TextLines.Split(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Contains(',') ? line.Split(',') : line.Split(['\t', ' '], 2, StringSplitOptions.RemoveEmptyEntries);
            var host = parts[0].Trim();
            if (host.Length == 0 || host.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new PingTarget
            {
                Host = host,
                Name = parts.Length > 1 ? parts[1].Trim() : "",
                Group = parts.Length > 2 ? parts[2].Trim() : "",
            });
        }
        return list;
    }
}
