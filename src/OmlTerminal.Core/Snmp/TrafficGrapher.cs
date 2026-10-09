using System.Collections.Concurrent;
using System.Text.Json;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Snmp;

/// <summary>A device being graphed. Saved to traffic/targets.json with its SNMP secrets DPAPI-encrypted.</summary>
public sealed class TrafficTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 161;
    public SnmpCredentials Credentials { get; set; } = new();
    public int PollSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    /// <summary>Utilization (% of interface speed) that raises an alert.</summary>
    public double AlertPercent { get; set; } = 90;
    public List<TrafficInterface> Interfaces { get; set; } = [];
}

public sealed class TrafficInterface
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public long SpeedBps { get; set; }
    public string Display => Alias.Length > 0 ? $"{Name} - {Alias}" : Name;
}

public sealed record TargetStatus(DateTime? LastPoll, string? Error, SystemInfo? System, bool HighCapacity);

public sealed record TrafficAlert(DateTime At, Guid Target, string Device, string Interface, string Direction, double Percent, double Bps);

/// <summary>
/// The MRTG engine: polls every enabled target on its own interval in the background (whether or not the graph tab
/// is open), turns counters into rates, and keeps each interface's <see cref="TrafficArchive"/> on disk.
/// </summary>
public sealed class TrafficGrapher : IDisposable
{
    private static TrafficGrapher? _shared;
    /// <summary>The app-wide instance, started once at launch.</summary>
    public static TrafficGrapher Shared => _shared ??= new TrafficGrapher(Path.Combine(AppPaths.DataDirectory, "traffic"));

    private readonly string _root;
    private readonly object _sync = new();
    private readonly List<TrafficTarget> _targets;
    private readonly ConcurrentDictionary<(Guid, int), TrafficArchive> _archives = new();
    private readonly ConcurrentDictionary<(Guid, int), IfCounters> _last = new();
    private readonly ConcurrentDictionary<Guid, TargetStatus> _status = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _due = new();
    private readonly ConcurrentDictionary<Guid, bool> _hc = new();
    private readonly HashSet<(Guid, int)> _dirty = [];
    private readonly ConcurrentDictionary<Guid, byte> _polling = new();
    private readonly List<TrafficAlert> _alerts = [];
    private readonly HashSet<(Guid, int, string)> _alerting = [];
    private CancellationTokenSource? _cts;
    private DateTime _lastSave = DateTime.UtcNow;

    public event Action<Guid>? Polled;
    public event Action<TrafficAlert>? Alert;

    public TrafficGrapher(string root)
    {
        _root = root;
        _targets = LoadTargets();
    }

    private string TargetsFile => Path.Combine(_root, "targets.json");

    public IReadOnlyList<TrafficTarget> Targets { get { lock (_sync) return _targets.ToList(); } }
    public IReadOnlyList<TrafficAlert> Alerts { get { lock (_sync) return _alerts.ToList(); } }
    public TargetStatus Status(Guid id) => _status.GetValueOrDefault(id) ?? new TargetStatus(null, null, null, false);

    private List<TrafficTarget> LoadTargets()
    {
        try
        {
            if (!File.Exists(TargetsFile)) return [];
            var list = JsonSerializer.Deserialize<List<TrafficTarget>>(File.ReadAllText(TargetsFile), JsonFile.Options) ?? [];
            foreach (var t in list)
            {
                t.Credentials.Community = LocalSecret.Unprotect(t.Credentials.Community) ?? "";
                t.Credentials.AuthPassword = LocalSecret.Unprotect(t.Credentials.AuthPassword) ?? "";
                t.Credentials.PrivPassword = LocalSecret.Unprotect(t.Credentials.PrivPassword) ?? "";
            }
            return list;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            if (e is JsonException) UnreadableFile.Keep(TargetsFile);
            return [];
        }
    }

    public void SaveTargets()
    {
        List<TrafficTarget> copy;
        lock (_sync)
            copy = _targets.Select(t =>
            {
                var c = JsonSerializer.Deserialize<TrafficTarget>(JsonSerializer.Serialize(t, JsonFile.Options), JsonFile.Options)!;
                c.Credentials.Community = LocalSecret.Protect(c.Credentials.Community);
                c.Credentials.AuthPassword = LocalSecret.Protect(c.Credentials.AuthPassword);
                c.Credentials.PrivPassword = LocalSecret.Protect(c.Credentials.PrivPassword);
                return c;
            }).ToList();
        JsonFile.WriteAtomic(TargetsFile, copy);
    }

    public void AddOrUpdate(TrafficTarget t)
    {
        lock (_sync)
        {
            int i = _targets.FindIndex(x => x.Id == t.Id);
            if (i >= 0) _targets[i] = t; else _targets.Add(t);
        }
        _due[t.Id] = DateTime.UtcNow; // poll straight away
        SaveTargets();
    }

    private bool IsTarget(Guid id) { lock (_sync) return _targets.Any(t => t.Id == id); }

    public void Remove(Guid id)
    {
        lock (_sync) _targets.RemoveAll(t => t.Id == id);
        SaveTargets();
        try { Directory.Delete(Path.Combine(_root, id.ToString("N")), true); } catch { }
        foreach (var k in _archives.Keys.Where(k => k.Item1 == id).ToList()) _archives.TryRemove(k, out _);
    }

    public TrafficArchive Archive(Guid target, int ifIndex) => _archives.GetOrAdd((target, ifIndex), k =>
    {
        try
        {
            var file = ArchiveFile(k.Item1, k.Item2);
            if (File.Exists(file)) return JsonSerializer.Deserialize<TrafficArchive>(File.ReadAllText(file)) ?? new TrafficArchive();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            if (e is JsonException) UnreadableFile.Keep(ArchiveFile(k.Item1, k.Item2));
        }
        return new TrafficArchive();
    });

    /// <summary>A copy of an archive's points, safe to read while polling continues.</summary>
    public (IReadOnlyList<TrafficPoint> Points, IReadOnlyList<TrafficPoint> Latest) Query(Guid target, int ifIndex, TrafficPeriod period)
    {
        var a = Archive(target, ifIndex);
        lock (a) return (a.Points(period), a.Raw.TakeLast(1).ToList());
    }

    private string ArchiveFile(Guid target, int ifIndex) => Path.Combine(_root, target.ToString("N"), $"if{ifIndex}.json");

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var t in Targets.Where(t => t.Enabled && t.Interfaces.Count > 0))
                {
                    var due = _due.GetOrAdd(t.Id, DateTime.UtcNow);
                    if (DateTime.UtcNow < due) continue;
                    _due[t.Id] = DateTime.UtcNow.AddSeconds(Math.Max(10, t.PollSeconds));
                    _ = PollAsync(t, ct);
                }
                if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(2)) Flush();
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }, ct);
    }

    public async Task PollAsync(TrafficTarget t, CancellationToken ct)
    {
        // A slow or unreachable device can take longer than its interval - never run two polls of it at once.
        if (!_polling.TryAdd(t.Id, 0)) return;
        try
        {
            using var c = await SnmpClient.ConnectAsync(t.Host, t.Port, t.Credentials, 3000, 1, ct).ConfigureAwait(false);
            var sys = Status(t.Id).System;
            if (sys is null || DateTime.UtcNow.Minute % 10 == 0) sys = await IfMib.SystemAsync(c, ct).ConfigureAwait(false);
            if (!_hc.TryGetValue(t.Id, out var hc))
                _hc[t.Id] = hc = t.Credentials.Version != SnmpVersion.V1 && await IfMib.HasHighCapacityAsync(c, t.Interfaces[0].Index, ct).ConfigureAwait(false);
            var counters = await IfMib.CountersAsync(c, t.Interfaces.Select(i => i.Index).ToList(), hc, ct).ConfigureAwait(false);
            foreach (var (idx, cur) in counters)
            {
                if (!IsTarget(t.Id)) return; // removed while this poll was in flight
                if (_last.TryGetValue((t.Id, idx), out var prev) && IfMib.Rate(prev, cur) is { } rate)
                {
                    var a = Archive(t.Id, idx);
                    lock (a) a.Add(rate);
                    lock (_dirty) _dirty.Add((t.Id, idx));
                    CheckAlert(t, idx, rate);
                }
                _last[(t.Id, idx)] = cur;
            }
            _status[t.Id] = new TargetStatus(DateTime.Now, null, sys, hc);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            _status[t.Id] = Status(t.Id) with { LastPoll = DateTime.Now, Error = e.Message };
        }
        finally { _polling.TryRemove(t.Id, out _); }
        Polled?.Invoke(t.Id);
    }

    private void CheckAlert(TrafficTarget t, int idx, TrafficRate r)
    {
        var iface = t.Interfaces.FirstOrDefault(i => i.Index == idx);
        if (iface is null || iface.SpeedBps <= 0 || t.AlertPercent <= 0) return;
        foreach (var (dir, bps) in new[] { ("in", r.InBps), ("out", r.OutBps) })
        {
            double pct = 100 * bps / iface.SpeedBps;
            var key = (t.Id, idx, dir);
            TrafficAlert? raised = null;
            lock (_sync)
            {
                if (pct >= t.AlertPercent && _alerting.Add(key))
                {
                    raised = new TrafficAlert(DateTime.Now, t.Id, t.Name, iface.Display, dir, pct, bps);
                    _alerts.Add(raised);
                    if (_alerts.Count > 500) _alerts.RemoveAt(0);
                }
                else if (pct < t.AlertPercent * 0.9) _alerting.Remove(key); // hysteresis so it doesn't flap at the line
            }
            if (raised is not null) Alert?.Invoke(raised);
        }
    }

    /// <summary>Writes changed archives to disk (also every 2 minutes while polling, and at shutdown).</summary>
    public void Flush()
    {
        _lastSave = DateTime.UtcNow;
        List<(Guid, int)> dirty;
        lock (_dirty) { dirty = _dirty.ToList(); _dirty.Clear(); }
        foreach (var k in dirty)
        {
            if (!_archives.TryGetValue(k, out var a) || !IsTarget(k.Item1)) continue;
            string json;
            lock (a) json = JsonSerializer.Serialize(a);
            try
            {
                var file = ArchiveFile(k.Item1, k.Item2);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", json);
                File.Move(file + ".tmp", file, true);
            }
            catch (IOException) { lock (_dirty) _dirty.Add(k); }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Flush();
    }
}
