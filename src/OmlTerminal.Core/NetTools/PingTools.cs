using System.Net;
using System.Net.NetworkInformation;

namespace OmlTerminal.Core.NetTools;

public sealed record PingReplyInfo(IPAddress? Address, IPStatus Status, long RoundTripMs, int Ttl);

public sealed record TraceHop(int Hop, IPAddress? Address, IReadOnlyList<long?> RoundTrips, string HostName)
{
    public bool TimedOut => Address is null;
}

/// <summary>ICMP echo via the OS stack (no raw sockets, no admin rights): continuous ping, subnet sweep, traceroute.</summary>
public static class PingTools
{
    public static async Task<PingReplyInfo> PingOnceAsync(IPAddress target, int timeoutMs = 1000, int size = 32, int ttl = 128, bool dontFragment = false)
    {
        if (!OperatingSystem.IsWindows() && target.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // .NET's Ping needs root on most Linux setups; UnixIcmp picks whatever this process is allowed to use.
            var r = await UnixIcmp.SendAsync(target, ttl, timeoutMs, size).ConfigureAwait(false);
            return new PingReplyInfo(r.From, r.Status, (long)Math.Round(r.RttMs), r.Ttl);
        }
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(target, timeoutMs, new byte[size], new PingOptions(ttl, dontFragment)).ConfigureAwait(false);
            return new PingReplyInfo(reply.Address, reply.Status, reply.RoundtripTime, reply.Options?.Ttl ?? 0);
        }
        catch (PingException) { return new PingReplyInfo(null, IPStatus.Unknown, 0, 0); }
    }

    /// <summary>Pings every host address in the subnet (capped at /20 = 4094 hosts), reporting each live one.</summary>
    public static async Task SweepAsync(Subnet subnet, Action<IPAddress, PingReplyInfo> onAlive, Action<int, int>? onProgress = null,
        int timeoutMs = 800, int concurrency = 128, CancellationToken ct = default)
    {
        if (subnet.IsV6) throw new NotSupportedException("Ping sweep is IPv4 only.");
        if (subnet.PrefixLength < 20) throw new ArgumentException("Sweeps are limited to /20 (4094 hosts) or smaller.");
        uint first = Ipv4.ToUInt(subnet.FirstHost), last = Ipv4.ToUInt(subnet.LastHost);
        int total = (int)(last - first + 1), done = 0;
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>();
        for (uint a = first; a <= last && a >= first; a++)
        {
            var ip = Ipv4.FromUInt(a);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var r = await PingOnceAsync(ip, timeoutMs).ConfigureAwait(false);
                    if (r.Status == IPStatus.Success) onAlive(ip, r);
                }
                finally
                {
                    gate.Release();
                    onProgress?.Invoke(Interlocked.Increment(ref done), total);
                }
            }, ct));
            if (a == uint.MaxValue) break;
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static async Task TraceAsync(IPAddress target, Action<TraceHop> onHop, int maxHops = 30, int timeoutMs = 1500,
        bool resolveNames = true, CancellationToken ct = default)
    {
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            IPAddress? hopAddress = null;
            var rtts = new List<long?>();
            bool reached = false;
            for (int probe = 0; probe < 3; probe++)
            {
                if (!OperatingSystem.IsWindows() && target.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var u = await UnixIcmp.SendAsync(target, ttl, timeoutMs, 32, ct).ConfigureAwait(false);
                    if (u.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        hopAddress ??= u.From;
                        rtts.Add((long)Math.Round(u.RttMs));
                        reached |= u.Status == IPStatus.Success;
                    }
                    else rtts.Add(null);
                    continue;
                }
                using var ping = new Ping();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var reply = await ping.SendPingAsync(target, timeoutMs, new byte[32], new PingOptions(ttl, true)).ConfigureAwait(false);
                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        hopAddress ??= reply.Address;
                        rtts.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : sw.ElapsedMilliseconds);
                        reached |= reply.Status == IPStatus.Success;
                    }
                    else rtts.Add(null);
                }
                catch (PingException) { rtts.Add(null); }
            }
            string name = "";
            if (resolveNames && hopAddress is not null)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(1500);
                    name = (await Dns.GetHostEntryAsync(hopAddress.ToString(), cts.Token).ConfigureAwait(false)).HostName;
                }
                catch { }
            }
            onHop(new TraceHop(ttl, hopAddress, rtts, name));
            if (reached) return;
        }
    }
}
