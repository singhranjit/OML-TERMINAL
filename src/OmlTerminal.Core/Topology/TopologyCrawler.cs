using System.Net;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.NetTools;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Core.Topology;

public sealed class TopologyOptions
{
    /// <summary>How many hops from the seed to log in to. 0 = only the seed (its neighbors still appear).</summary>
    public int MaxDepth { get; init; } = 2;
    /// <summary>Most devices to log in to, seed included.</summary>
    public int MaxDevices { get; init; } = 50;
    public int Parallel { get; init; } = 4;
    /// <summary>Only log in to management addresses inside these networks. Empty = no restriction.</summary>
    public IReadOnlyList<Subnet> Scope { get; init; } = [];
    /// <summary>Also try to log in to phones, access points and hosts (normally just drawn as leaves).</summary>
    public bool CrawlEndpoints { get; init; }
}

/// <summary>Walks CDP/LLDP neighbors outward from a seed device, breadth first, logging in to each neighbor over SSH
/// with the credentials <c>profileFor</c> supplies. Only read-only "show" commands are sent. Hard limits (depth,
/// device count, address scope) keep it from wandering off across a network it wasn't asked to map.</summary>
public sealed class TopologyCrawler
{
    private readonly object _sync = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedHosts = new(StringComparer.OrdinalIgnoreCase);
    private int _loginBudget;

    public TopologyGraph Graph { get; } = new();

    /// <summary>Raised (from a background thread) whenever a node or link is added or a node's status changes.</summary>
    public event Action? Changed;

    public async Task CrawlAsync(SessionProfile seed, Func<string, string, SessionProfile> profileFor, TopologyOptions options, CancellationToken ct)
    {
        _loginBudget = Math.Max(1, options.MaxDevices) - 1;
        _queuedHosts.Add(seed.Host);
        var level = new List<(SessionProfile Profile, string? Key)> { (seed, null) };
        for (int depth = 0; level.Count > 0; depth++)
        {
            var next = new List<(SessionProfile, string?)>();
            using var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
            await Task.WhenAll(level.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var found = await CrawlOneAsync(item.Profile, item.Key, depth, profileFor, options, ct).ConfigureAwait(false);
                    lock (next) next.AddRange(found);
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
            level = next;
        }
    }

    private async Task<List<(SessionProfile, string?)>> CrawlOneAsync(SessionProfile profile, string? expectedKey, int depth,
        Func<string, string, SessionProfile> profileFor, TopologyOptions options, CancellationToken ct)
    {
        bool isSeed = expectedKey is null;
        TopologyNode? node = null;
        if (!isSeed)
        {
            node = Graph.Ensure(expectedKey!, profile.Name);
            Set(node, NodeStatus.Crawling, "logging in…");
        }
        var queue = new List<(SessionProfile, string?)>();
        try
        {
            using var session = await DeviceSession.OpenAsync(profile, BackupMode.Shell, ["terminal length 0"], ct).ConfigureAwait(false);
            if (isSeed)
            {
                var name = session.Hostname ?? profile.Name;
                node = Graph.Ensure(NeighborParser.NormalizeName(name), name);
                lock (_sync)
                {
                    node.IsSeed = true;
                    node.MgmtIp = profile.Host;
                    _queued.Add(node.Key);
                }
                Set(node, NodeStatus.Crawling, "reading neighbors…");
            }
            var neighbors = await NeighborsAsync(session, ct).ConfigureAwait(false);
            Set(node!, NodeStatus.Crawled, $"{neighbors.Count} neighbor entr{(neighbors.Count == 1 ? "y" : "ies")}");

            lock (_sync)
            {
                foreach (var n in neighbors)
                {
                    var key = NeighborParser.NormalizeName(n.Name);
                    if (key.Length == 0 || key == node!.Key) continue;
                    var display = NeighborParser.DisplayName(n.Name);
                    var peer = Graph.Ensure(key, display);
                    // Platform and capabilities only come from neighbors - even for devices already logged in to,
                    // the seed included - so fill them in whenever they're still missing.
                    if (peer.Platform.Length == 0) peer.Platform = n.Platform;
                    if (peer.Capabilities.Length == 0) peer.Capabilities = n.Capabilities;
                    if (peer.Kind == NodeKind.Unknown) peer.Kind = NeighborParser.Classify(peer.Platform, peer.Capabilities);
                    // First heard of via LLDP with no address, now CDP gives one: worth another look.
                    if (peer.Status == NodeStatus.Skipped && peer.MgmtIp is null && n.MgmtIp is not null) peer.Status = NodeStatus.Discovered;
                    if (peer.Status == NodeStatus.Discovered && !_queued.Contains(key))
                    {
                        peer.MgmtIp ??= n.MgmtIp;
                        peer.Depth = depth + 1;
                    }
                    Graph.AddLink(node.Key, NeighborParser.ShortInterface(n.LocalInterface), key, NeighborParser.ShortInterface(n.RemoteInterface), n.Protocol);

                    if (peer.Status != NodeStatus.Discovered || _queued.Contains(key)) continue;
                    var reason = WhyNotCrawl(peer, depth + 1, options);
                    if (reason is not null) { peer.Status = NodeStatus.Skipped; peer.StatusText = reason; continue; }
                    _queued.Add(key);
                    _queuedHosts.Add(peer.MgmtIp!);
                    _loginBudget--;
                    peer.StatusText = "queued";
                    queue.Add((profileFor(display, peer.MgmtIp!), key));
                }
            }
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            if (node is not null) Set(node, NodeStatus.Skipped, "stopped before it finished");
            throw;
        }
        catch (Exception ex)
        {
            if (node is null)
            {
                node = Graph.Ensure(NeighborParser.NormalizeName(profile.Name), profile.Name);
                node.IsSeed = true;
                node.MgmtIp = profile.Host;
            }
            Set(node, NodeStatus.Failed, ex.Message);
        }
        return queue;
    }

    private string? WhyNotCrawl(TopologyNode peer, int depth, TopologyOptions options)
    {
        if (depth > options.MaxDepth) return "beyond the depth limit";
        if (!options.CrawlEndpoints && !NeighborParser.IsInfrastructure(peer.Kind))
            return peer.Kind == NodeKind.Unknown ? "not identified as network gear" : $"{peer.Kind} - not logged in to";
        if (peer.MgmtIp is null) return "no management address advertised";
        if (_queuedHosts.Contains(peer.MgmtIp)) return $"{peer.MgmtIp} is already mapped under another name";
        if (options.Scope.Count > 0 && !(IPAddress.TryParse(peer.MgmtIp, out var ip) && options.Scope.Any(s => s.Contains(ip))))
            return $"{peer.MgmtIp} is outside the scope";
        if (_loginBudget <= 0) return "device limit reached";
        return null;
    }

    private void Set(TopologyNode node, NodeStatus status, string text)
    {
        lock (_sync)
        {
            node.Status = status;
            node.StatusText = text;
        }
        Changed?.Invoke();
    }

    private static async Task<List<NeighborInfo>> NeighborsAsync(DeviceSession session, CancellationToken ct)
    {
        var list = new List<NeighborInfo>();
        bool junos = session.Prompt.Contains('@') && session.Prompt.TrimEnd().EndsWith('>');
        if (junos)
        {
            list.AddRange(NeighborParser.ParseJunosLldp(await session.RunAsync("show lldp neighbors | no-more", ct).ConfigureAwait(false)));
            return list;
        }
        var cdp = await session.RunAsync("show cdp neighbors detail", ct).ConfigureAwait(false);
        if (!DeviceSession.IsCommandError(cdp)) list.AddRange(NeighborParser.ParseCdpDetail(cdp));
        var lldp = await session.RunAsync("show lldp neighbors detail", ct).ConfigureAwait(false);
        if (!DeviceSession.IsCommandError(lldp)) list.AddRange(NeighborParser.ParseLldpDetail(lldp));
        return list;
    }
}
