using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace OmlTerminal.Core.PathTrace;

/// <summary>One probe's answer: who replied (the router whose TTL expired, or the target), how, and how fast.</summary>
public sealed record ProbeResult(IPAddress? From, IPStatus Status, double RttMs)
{
    public bool Answered => From is not null && Status is IPStatus.Success or IPStatus.TtlExpired
        or IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
        or IPStatus.DestinationProhibited or IPStatus.DestinationPortUnreachable;

    public bool IsUnreachable => From is not null && Status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
        or IPStatus.DestinationProhibited or IPStatus.DestinationPortUnreachable;
}

/// <summary>Sends one TTL-limited probe. The ICMP implementation needs no admin rights; tests plug in a fake network.</summary>
public interface IProbeSender
{
    Task<ProbeResult> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct);
}

public sealed class IcmpProbeSender : IProbeSender
{
    public async Task<ProbeResult> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() && target.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return await NativeIcmp.SendAsync(target, ttl, timeoutMs, size).ConfigureAwait(false);
        using var ping = new Ping();
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), new byte[size], new PingOptions(ttl, true), ct)
                .ConfigureAwait(false);
            double elapsed = sw.Elapsed.TotalMilliseconds;
            // Windows reports RoundtripTime 0 for TTL-expired replies (and for sub-millisecond echoes) - time it ourselves.
            double rtt = reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? Math.Min(reply.RoundtripTime, elapsed) : elapsed;
            var from = reply.Status == IPStatus.TimedOut || reply.Address is null || reply.Address.Equals(IPAddress.Any) ? null : reply.Address;
            return new ProbeResult(from, reply.Status, rtt);
        }
        catch (PingException) { return new ProbeResult(null, IPStatus.Unknown, 0); }
    }
}

/// <summary>
/// IPv4 ICMP echo through Windows' own IcmpSendEcho2 (no admin rights). Unlike .NET's Ping it keeps the round-trip time
/// Windows measured for TTL-expired replies - the hop latencies tracert shows. Asynchronous: waiting for a reply (or a
/// timeout) holds no thread, so hundreds of probes to dead hosts can't starve the app's thread pool.
/// </summary>
internal static class NativeIcmp
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptionInformation
    {
        public byte Ttl, Tos, Flags, OptionsSize;
        public IntPtr OptionsData;
    }

    private const int ErrorIoPending = 997, IpReqTimedOut = 11010;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, uint destination,
        IntPtr request, ushort requestSize, IntPtr options, IntPtr reply, uint replySize, uint timeout);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpParseReplies(IntPtr reply, uint replySize);

    public static async Task<ProbeResult> SendAsync(IPAddress target, int ttl, int timeoutMs, int size)
    {
        var handle = IcmpCreateFile();
        if (handle == new IntPtr(-1)) return new ProbeResult(null, IPStatus.Unknown, 0);
        int replySize = 64 + size + 64;
        var reply = Marshal.AllocHGlobal(replySize);
        var request = Marshal.AllocHGlobal(Math.Max(1, size));
        var options = Marshal.AllocHGlobal(Marshal.SizeOf<IpOptionInformation>());
        using var done = new ManualResetEvent(false);
        try
        {
            Marshal.Copy(new byte[Math.Max(1, size)], 0, request, Math.Max(1, size));
            Marshal.StructureToPtr(new IpOptionInformation { Ttl = (byte)Math.Clamp(ttl, 1, 255), Flags = 0x02 }, options, false); // don't fragment
            uint dest = BitConverter.ToUInt32(target.GetAddressBytes(), 0);
            var sw = Stopwatch.StartNew();
            uint n = IcmpSendEcho2(handle, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero, dest, request, (ushort)size,
                options, reply, (uint)replySize, (uint)timeoutMs);
            if (n == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ErrorIoPending) return new ProbeResult(null, err == IpReqTimedOut ? IPStatus.TimedOut : (IPStatus)err, 0);
                await WaitAsync(done, timeoutMs + 2000).ConfigureAwait(false);
                double elapsed = sw.Elapsed.TotalMilliseconds;
                n = IcmpParseReplies(reply, (uint)replySize);
                if (n == 0)
                {
                    err = Marshal.GetLastWin32Error();
                    return new ProbeResult(null, err == IpReqTimedOut || err == 0 ? IPStatus.TimedOut : (IPStatus)err, 0);
                }
                return Read(reply, elapsed);
            }
            return Read(reply, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            Marshal.FreeHGlobal(options);
            Marshal.FreeHGlobal(request);
            Marshal.FreeHGlobal(reply);
            IcmpCloseHandle(handle);
        }
    }

    private static ProbeResult Read(IntPtr reply, double elapsed)
    {
        uint addr = (uint)Marshal.ReadInt32(reply, 0);
        var status = (IPStatus)Marshal.ReadInt32(reply, 4);
        uint rtt = (uint)Marshal.ReadInt32(reply, 8);
        // Windows reports whole milliseconds; under 1 ms reads as 0 - show the measured time, capped at 1 ms.
        double ms = rtt > 0 ? rtt : Math.Min(elapsed, 1.0);
        return new ProbeResult(status == IPStatus.TimedOut ? null : new IPAddress(addr), status, ms);
    }

    /// <summary>Completes when the event is signalled, without blocking a thread while waiting.</summary>
    private static Task WaitAsync(WaitHandle handle, int timeoutMs)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = ThreadPool.RegisterWaitForSingleObject(handle, (state, _) => ((TaskCompletionSource)state!).TrySetResult(), tcs, timeoutMs, true);
        return tcs.Task.ContinueWith(_ => reg.Unregister(null), TaskScheduler.Default);
    }
}

public sealed record HopSample(DateTime At, double? RttMs, string? From);

public sealed record PathChange(DateTime At, int Hop, string? From, string To);

/// <summary>Who an address belongs to: one of your own devices (from saved sessions or config backups), a private
/// range, or a public network with its AS number and owner.</summary>
public sealed record HopOwner(HopOwnerKind Kind, string Text, string? Device = null, string? Interface = null, int? Asn = null, string? AsName = null);

public enum HopOwnerKind { Device, Private, CarrierNat, Public, Unknown }

/// <summary>Running statistics for one TTL. Welford's method keeps mean/stdev exact without storing every sample.</summary>
public sealed class HopStats
{
    public const int MaxSamples = 3600;

    public int Hop { get; init; }
    public Dictionary<string, int> AddressCounts { get; } = new(StringComparer.Ordinal);
    public string? LastAddress { get; private set; }
    public string Name { get; internal set; } = "";
    public HopOwner? Owner { get; internal set; }
    public int Sent { get; private set; }
    public int Received { get; private set; }
    public double Last { get; private set; } = double.NaN;
    public double Best { get; private set; } = double.NaN;
    public double Worst { get; private set; } = double.NaN;
    public double Mean { get; private set; } = double.NaN;
    private double _m2, _jitterSum, _prev = double.NaN;
    private int _jitterCount;
    public int Flips { get; private set; }
    public bool IsDestination { get; internal set; }
    /// <summary>Set when this hop answered "destination unreachable" instead of passing the probe on.</summary>
    public IPStatus? Unreachable { get; private set; }
    public List<HopSample> Samples { get; } = [];

    /// <summary>The address that answered most often (ECMP/route changes can give a hop several).</summary>
    public string? Address => AddressCounts.Count == 0 ? null : AddressCounts.MaxBy(kv => kv.Value).Key;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public double StDev => Received < 2 ? double.NaN : Math.Sqrt(_m2 / (Received - 1));
    /// <summary>Mean absolute difference between consecutive replies (RFC 3550-style jitter, unsmoothed).</summary>
    public double Jitter => _jitterCount == 0 ? double.NaN : _jitterSum / _jitterCount;
    public bool EverAnswered => Received > 0;

    /// <summary>Loss over the most recent <paramref name="n"/> probes - the live view, rather than since the start.</summary>
    public double RecentLoss(int n)
    {
        int count = 0, lost = 0;
        for (int i = Samples.Count - 1; i >= 0 && count < n; i--, count++)
            if (Samples[i].RttMs is null) lost++;
        return count == 0 ? 0 : 100.0 * lost / count;
    }

    /// <returns>True when the answering address changed to one never seen at this hop before (a new path).</returns>
    internal bool Record(DateTime at, ProbeResult r, out string? previous)
    {
        previous = LastAddress;
        Sent++;
        if (!r.Answered)
        {
            Add(new HopSample(at, null, null));
            _prev = double.NaN;
            return false;
        }
        var from = r.From!.ToString();
        bool isNew = !AddressCounts.ContainsKey(from);
        bool changed = LastAddress is not null && LastAddress != from;
        if (changed && !isNew) Flips++;
        AddressCounts[from] = AddressCounts.GetValueOrDefault(from) + 1;
        LastAddress = from;
        if (r.IsUnreachable) Unreachable = r.Status;

        Received++;
        double rtt = Math.Round(r.RttMs, 2);
        Last = rtt;
        Best = double.IsNaN(Best) ? rtt : Math.Min(Best, rtt);
        Worst = double.IsNaN(Worst) ? rtt : Math.Max(Worst, rtt);
        if (double.IsNaN(Mean)) Mean = 0;
        double delta = rtt - Mean;
        Mean += delta / Received;
        _m2 += delta * (rtt - Mean);
        if (!double.IsNaN(_prev)) { _jitterSum += Math.Abs(rtt - _prev); _jitterCount++; }
        _prev = rtt;
        Add(new HopSample(at, rtt, from));
        return changed && isNew;
    }

    private void Add(HopSample s)
    {
        Samples.Add(s);
        if (Samples.Count > MaxSamples) Samples.RemoveRange(0, Samples.Count - MaxSamples);
    }
}

public sealed class TraceOptions
{
    public int MaxHops { get; init; } = 30;
    public int TimeoutMs { get; init; } = 2000;
    public int PacketSize { get; init; } = 32;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public bool ResolveNames { get; init; } = true;
}

/// <summary>
/// MTR-style continuous traceroute: every round probes every TTL at once, so each hop builds up loss, latency and
/// jitter statistics over time. Notices route changes (a hop answering from an address it never used before) and
/// load balancing (a hop alternating between known addresses). Names and owners are looked up in the background.
/// </summary>
public sealed class ContinuousTrace
{
    private static readonly ConcurrentDictionary<string, string> NameCache = new();

    private readonly object _sync = new();
    private readonly List<HopStats> _hops = [];
    private readonly List<PathChange> _changes = [];
    private readonly IProbeSender _sender;
    private readonly Func<string, Task<HopOwner?>>? _identify;
    private readonly ConcurrentDictionary<string, HopOwner?> _owners = new();
    private int _destinationHop; // 0 = not reached yet
    private int _lastAnswering;

    public IPAddress Target { get; }
    public TraceOptions Options { get; }
    public int Rounds { get; private set; }
    public DateTime Started { get; private set; }

    /// <summary>Raised from a background thread after every round, and when a name or owner arrives.</summary>
    public event Action? Updated;

    public ContinuousTrace(IPAddress target, TraceOptions? options = null, IProbeSender? sender = null, Func<string, Task<HopOwner?>>? identify = null)
    {
        Target = target;
        Options = options ?? new TraceOptions();
        _sender = sender ?? new IcmpProbeSender();
        _identify = identify;
    }

    public bool Reached { get { lock (_sync) return _destinationHop > 0; } }

    /// <summary>The hops worth showing: up to the destination, or one silent hop past the last router that answered.</summary>
    public IReadOnlyList<HopStats> Hops
    {
        get
        {
            lock (_sync)
            {
                int show = _destinationHop > 0 ? _destinationHop : Math.Min(_hops.Count, _lastAnswering + 1);
                return _hops.Take(show).ToList();
            }
        }
    }

    public IReadOnlyList<PathChange> Changes { get { lock (_sync) return _changes.ToList(); } }

    public async Task RunAsync(CancellationToken ct, int? maxRounds = null)
    {
        Started = DateTime.Now;
        while (!ct.IsCancellationRequested && (maxRounds is null || Rounds < maxRounds))
        {
            var sw = Stopwatch.StartNew();
            int probeTo;
            lock (_sync) probeTo = _destinationHop > 0 ? Math.Min(Options.MaxHops, _destinationHop) : Options.MaxHops;
            var at = DateTime.Now;
            // Windows doesn't time TTL-expired replies, so each probe is timed here - and 30 probes fired in the same
            // instant queue behind each other and read tens of ms slow. A few ms apart, each gets measured cleanly.
            var results = await Task.WhenAll(Enumerable.Range(1, probeTo).Select(async ttl =>
            {
                if (ttl > 1) await Task.Delay(TimeSpan.FromMilliseconds(4 * (ttl - 1)), ct).ConfigureAwait(false);
                return await _sender.SendAsync(Target, ttl, Options.TimeoutMs, Options.PacketSize, ct).ConfigureAwait(false);
            })).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;
            Apply(at, results);
            Updated?.Invoke();
            var wait = Options.Interval - sw.Elapsed;
            if (wait > TimeSpan.Zero && (maxRounds is null || Rounds < maxRounds))
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Apply(DateTime at, ProbeResult[] results)
    {
        var lookups = new List<string>();
        lock (_sync)
        {
            Rounds++;
            // The first TTL that reaches the target (or is refused outright) ends the path for this round.
            int end = Array.FindIndex(results, r => r.Status == IPStatus.Success || r.IsUnreachable);
            if (end >= 0 && results[end].Status == IPStatus.Success)
            {
                _destinationHop = end + 1;
                if (_hops.Count > _destinationHop) _hops.RemoveRange(_destinationHop, _hops.Count - _destinationHop);
            }
            int count = end >= 0 ? end + 1 : results.Length;
            for (int i = 0; i < count; i++)
            {
                while (_hops.Count <= i) _hops.Add(new HopStats { Hop = _hops.Count + 1 });
                var hop = _hops[i];
                hop.IsDestination = _destinationHop == i + 1;
                if (hop.Record(at, results[i], out var previous) && previous is not null)
                    _changes.Add(new PathChange(at, i + 1, previous, results[i].From!.ToString()));
                if (results[i].Answered)
                {
                    _lastAnswering = Math.Max(_lastAnswering, i + 1);
                    var ip = results[i].From!.ToString();
                    if (hop.AddressCounts[ip] == 1) lookups.Add(ip);
                }
            }
            if (_destinationHop > 0) _lastAnswering = Math.Min(_lastAnswering, _destinationHop);
        }
        foreach (var ip in lookups.Distinct()) _ = DescribeAsync(ip);
    }

    private async Task DescribeAsync(string ip)
    {
        bool changed = false;
        if (Options.ResolveNames && !NameCache.ContainsKey(ip))
        {
            string name = "";
            try
            {
                using var cts = new CancellationTokenSource(2500);
                name = (await Dns.GetHostEntryAsync(ip, cts.Token).ConfigureAwait(false)).HostName;
                if (name == ip) name = "";
            }
            catch { }
            NameCache[ip] = name;
            changed = name.Length > 0;
        }
        if (_identify is not null && !_owners.ContainsKey(ip))
        {
            _owners[ip] = null;
            try { _owners[ip] = await _identify(ip).ConfigureAwait(false); changed = true; }
            catch { }
        }
        lock (_sync)
        {
            foreach (var hop in _hops)
            {
                var addr = hop.Address;
                if (addr is null) continue;
                if (NameCache.TryGetValue(addr, out var n)) hop.Name = n;
                if (_owners.TryGetValue(addr, out var o) && o is not null) hop.Owner = o;
            }
        }
        if (changed) Updated?.Invoke();
    }

    /// <summary>A copy of the hops for display/analysis that won't change underneath the caller.</summary>
    public TraceSnapshot Snapshot()
    {
        lock (_sync)
        {
            int show = _destinationHop > 0 ? _destinationHop : Math.Min(_hops.Count, _lastAnswering + 1);
            var hops = _hops.Take(show).Select(h => new HopView(h.Hop, h.Address, h.AddressCounts.Keys.ToList(), h.Name, h.Owner, h.Sent, h.Received,
                h.LossPercent, h.Last, h.Best, h.Worst, h.Mean, h.StDev, h.Jitter, h.Flips, h.IsDestination, h.Unreachable, h.RecentLoss(60))).ToList();
            return new TraceSnapshot(Target.ToString(), Rounds, _destinationHop > 0, hops, _changes.ToList(), Started);
        }
    }

    /// <summary>Latency samples for one hop (for the timeline graph).</summary>
    public IReadOnlyList<HopSample> SamplesFor(int hop)
    {
        lock (_sync) return hop >= 1 && hop <= _hops.Count ? _hops[hop - 1].Samples.ToList() : [];
    }
}

public sealed record HopView(int Hop, string? Address, IReadOnlyList<string> Addresses, string Name, HopOwner? Owner, int Sent, int Received,
    double LossPercent, double Last, double Best, double Worst, double Mean, double StDev, double Jitter, int Flips, bool IsDestination,
    IPStatus? Unreachable, double RecentLoss)
{
    public bool Silent => Received == 0;
    public string Label => Owner?.Device is { } d ? d : Name.Length > 0 ? Name : Address ?? "no reply";
}

public sealed record TraceSnapshot(string Target, int Rounds, bool Reached, IReadOnlyList<HopView> Hops, IReadOnlyList<PathChange> Changes, DateTime Started);
