using System.Net;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Ssh;
using OmlTerminal.Core.Topology;

namespace OmlTerminal.Core.PathTrace;

/// <summary>A logged-in device the tracer can run read-only commands on.</summary>
public interface IDeviceCli : IDisposable
{
    string Hostname { get; }
    bool IsJunos { get; }
    bool IsNxos { get; }
    Task<string> RunAsync(string command, CancellationToken ct);
}

/// <summary>The real thing: an SSH <see cref="DeviceSession"/> (jump hosts, enable, known-host checks all handled).</summary>
public sealed class SshDeviceCli : IDeviceCli
{
    private readonly DeviceSession _session;
    private bool? _nxos;

    private SshDeviceCli(DeviceSession session, string fallbackName)
    {
        _session = session;
        Hostname = session.Hostname ?? fallbackName;
    }

    public static async Task<IDeviceCli> OpenAsync(SessionProfile profile, CancellationToken ct)
    {
        var s = await DeviceSession.OpenAsync(profile, BackupMode.Shell, ["terminal length 0"], ct).ConfigureAwait(false);
        return new SshDeviceCli(s, profile.Name);
    }

    public string Hostname { get; }
    public bool IsJunos => _session.Prompt.Contains('@') && _session.Prompt.TrimEnd().EndsWith('>');
    public bool IsNxos => _nxos ??= false;
    public Task<string> RunAsync(string command, CancellationToken ct) => _session.RunAsync(command, ct);

    /// <summary>For commands that pause between lines of output (mtrace waits on each hop's answer).</summary>
    public Task<string> RunSlowAsync(string command, TimeSpan quiet, CancellationToken ct) => _session.RunAsync(command, ct, quiet);
    public void Dispose() => _session.Dispose();
}

public enum PathNodeKind { Router, Switch, Host, Unknown }
public enum PathNodeStatus { Pending, Working, Done, Failed, NoRoute, Destination, NotReached }

/// <summary>One device on the path (or the destination host at the end).</summary>
public sealed class PathNode
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string? MgmtIp { get; set; }
    public PathNodeKind Kind { get; set; } = PathNodeKind.Router;
    public int Depth { get; set; }
    public PathNodeStatus Status { get; set; } = PathNodeStatus.Pending;
    public string StatusText { get; set; } = "";
    public RouteDecision? Route { get; set; }
    public Dictionary<string, InterfaceHealth> Interfaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Commands { get; } = [];
    /// <summary>For the destination host: which switch port it's plugged into.</summary>
    public string? Attachment { get; set; }
}

/// <summary>A hop between two nodes: leaves <see cref="From"/> on <see cref="Egress"/>, arrives at <see cref="To"/> on
/// <see cref="Ingress"/>. <see cref="Layer2"/> marks switch-to-switch hops inside the destination's VLAN.</summary>
public sealed record PathLink(string From, string Egress, string NextHop, string To, string Ingress, bool Layer2);

public sealed class DevicePathOptions
{
    public int MaxDevices { get; init; } = 25;
    public int MaxBranches { get; init; } = 4;
    public bool FollowEcmp { get; init; } = true;
    public bool LayerTwo { get; init; } = true;
    public bool InterfaceStats { get; init; } = true;
}

/// <summary>
/// Hop-by-hop path through your own network, the way you'd trace it by hand: on each router, look up the route to
/// the destination, take the egress interface, find who's on the other end (CDP/LLDP on that port, else a saved
/// session or config backup that owns the next-hop address, else SSH to the next hop itself), log in there and
/// repeat. Follows every equal-cost path. At the last router it resolves ARP and walks MAC tables switch to switch
/// down to the host's access port. Reads interface load, errors and drops at every hop. Only "show" commands.
/// </summary>
public sealed class DevicePathTracer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PathNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PathLink> _links = [];
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<SessionProfile, CancellationToken, Task<IDeviceCli>> _connect;
    private readonly Func<string, string, SessionProfile> _profileFor;
    private readonly Func<string, SessionProfile?> _knownDevice;
    private int _logins;

    public event Action? Changed;

    /// <param name="profileFor">(name, address) → a profile to log in with (normally the start device's credentials).</param>
    /// <param name="knownDevice">An address → a saved session for it (by host, or the backup that owns that address).</param>
    public DevicePathTracer(Func<string, string, SessionProfile> profileFor, Func<string, SessionProfile?>? knownDevice = null,
        Func<SessionProfile, CancellationToken, Task<IDeviceCli>>? connect = null)
    {
        _profileFor = profileFor;
        _knownDevice = knownDevice ?? (_ => null);
        _connect = connect ?? SshDeviceCli.OpenAsync;
    }

    public (IReadOnlyList<PathNode> Nodes, IReadOnlyList<PathLink> Links) Snapshot()
    {
        lock (_sync)
            return (_nodes.Values.OrderBy(n => n.Depth).ToList(),
                _links.Select(l => l with { From = Alias(l.From), To = Alias(l.To) }).Distinct().ToList());
    }

    public async Task TraceAsync(SessionProfile start, IPAddress destination, DevicePathOptions options, CancellationToken ct)
    {
        // Breadth first, a few devices at a time: equal-cost branches are traced side by side.
        var level = new List<(SessionProfile Profile, string? ExpectedKey, int Depth, string? Ingress, string? From, string? Egress, string? NextHop)>
            { (start, null, 0, null, null, null, null) };
        while (level.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<(SessionProfile, string?, int, string?, string?, string?, string?)>();
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(level.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _logins) >= options.MaxDevices)
                    {
                        var stub = Node(item.ExpectedKey ?? item.Profile.Host, item.Profile.Name, item.Depth);
                        Set(stub, PathNodeStatus.NotReached, "device limit reached");
                        return;
                    }
                    var found = await VisitAsync(item.Profile, item.ExpectedKey, item.Depth, item.Ingress, item.From, item.Egress, item.NextHop, destination, options, ct).ConfigureAwait(false);
                    lock (next) next.AddRange(found);
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
            level = next;
        }
        Changed?.Invoke();
    }

    private async Task<List<(SessionProfile, string?, int, string?, string?, string?, string?)>> VisitAsync(SessionProfile profile, string? expectedKey, int depth,
        string? ingress, string? from, string? egress, string? nextHop, IPAddress destination, DevicePathOptions options, CancellationToken ct)
    {
        var onward = new List<(SessionProfile, string?, int, string?, string?, string?, string?)>();
        var preKey = expectedKey ?? profile.Host;
        bool seen;
        lock (_sync) seen = !_visited.Add(preKey); // claim it, so a parallel branch to the same device just links to it
        if (seen)
        {
            // Equal-cost branches converging on a device that's already been traced from.
            if (from is not null) AddLink(new PathLink(from, egress ?? "", nextHop ?? "", Resolve(preKey), ingress ?? "", false));
            return onward;
        }
        var provisional = Node(preKey, profile.Name, depth);
        provisional.MgmtIp ??= profile.Host;
        Set(provisional, PathNodeStatus.Working, "logging in…");
        IDeviceCli cli;
        try
        {
            Interlocked.Increment(ref _logins);
            cli = await _connect(profile, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Set(provisional, PathNodeStatus.Failed, $"couldn't log in: {ex.Message}");
            if (from is not null) AddLink(new PathLink(from, egress ?? "", nextHop ?? "", provisional.Key, ingress ?? "", false));
            return onward;
        }
        using (cli)
        {
            // The device's own hostname is the real key - the name CDP gave, or an address, may differ from it.
            var key = NeighborParser.NormalizeName(cli.Hostname);
            PathNode node;
            bool already;
            lock (_sync)
            {
                already = key != preKey && !_visited.Add(key);
                if (key != preKey)
                {
                    _nodes.Remove(preKey);
                    _aliases[preKey] = key;
                    if (!_nodes.TryGetValue(key, out var real))
                    {
                        real = new PathNode { Key = key, Depth = provisional.Depth, MgmtIp = provisional.MgmtIp, Kind = provisional.Kind };
                        _nodes[key] = real;
                    }
                    provisional = real;
                }
                node = provisional;
                node.Name = NeighborParser.DisplayName(cli.Hostname);
            }
            if (from is not null) AddLink(new PathLink(from, egress ?? "", nextHop ?? "", node.Key, ingress ?? "", false));
            if (already) { Changed?.Invoke(); return onward; }

            if (ingress is { Length: > 0 } && options.InterfaceStats) await HealthAsync(cli, node, ingress, ct).ConfigureAwait(false);
            Set(node, PathNodeStatus.Working, "looking up the route…");
            var route = await RouteAsync(cli, node, destination, ct).ConfigureAwait(false);
            node.Route = route;
            if (route is null)
            {
                Set(node, PathNodeStatus.NoRoute, $"no route to {destination} - traffic is dropped here");
                return onward;
            }
            if (route.Local || route.Paths.Any(p => p.Address == destination.ToString() && route.Local))
            {
                Set(node, PathNodeStatus.Destination, $"{destination} is this device's own address");
                return onward;
            }
            if (route.Connected)
            {
                await FinishOnSegmentAsync(cli, node, route, destination, depth, options, ct).ConfigureAwait(false);
                return onward;
            }

            var paths = options.FollowEcmp ? route.Paths.Take(options.MaxBranches).ToList() : route.Paths.Take(1).ToList();
            foreach (var p in paths)
            {
                var hop = p;
                if (hop.Interface.Length == 0 && hop.Address.Length > 0 && IPAddress.TryParse(hop.Address, out var nhIp))
                {
                    // Recursive route (e.g. BGP): resolve the next hop's own route to find the exit interface.
                    var via = await RouteAsync(cli, node, nhIp, ct, quiet: true).ConfigureAwait(false);
                    if (via?.Paths.FirstOrDefault() is { } v) hop = new NextHop(v.Address.Length > 0 ? v.Address : hop.Address, v.Interface);
                }
                if (options.InterfaceStats && hop.Interface.Length > 0) await HealthAsync(cli, node, hop.Interface, ct).ConfigureAwait(false);
                var neighbor = await NeighborOnAsync(cli, node, hop, ct).ConfigureAwait(false);
                if (neighbor is null)
                {
                    var end = Node($"{hop.Address}@{node.Key}", hop.Address, depth + 1);
                    Set(end, PathNodeStatus.NotReached, "next hop not identified - no CDP/LLDP neighbor on that port and no saved session for that address");
                    end.MgmtIp = hop.Address;
                    AddLink(new PathLink(node.Key, hop.Interface, hop.Address, end.Key, "", false));
                    continue;
                }
                var (profileNext, nKey, nIngress) = neighbor.Value;
                var pending = Node(nKey, profileNext.Name, depth + 1);
                pending.MgmtIp ??= profileNext.Host;
                onward.Add((profileNext, nKey, depth + 1, nIngress, node.Key, hop.Interface, hop.Address));
            }
            Set(node, PathNodeStatus.Done, paths.Count > 1 ? $"{paths.Count} equal-cost paths" : $"→ {paths.FirstOrDefault()?.Address} via {paths.FirstOrDefault()?.Interface}");
        }
        return onward;
    }

    private async Task<RouteDecision?> RouteAsync(IDeviceCli cli, PathNode node, IPAddress dst, CancellationToken ct, bool quiet = false)
    {
        if (cli.IsJunos)
        {
            var j = await Run(cli, node, $"show route {dst} best terse | no-more", ct).ConfigureAwait(false);
            return DeviceOutputParsers.LongestMatch(j, dst);
        }
        var text = await Run(cli, node, $"show ip route {dst}", ct).ConfigureAwait(false);
        if (DeviceSession.IsCommandError(text))
        {
            var full = await Run(cli, node, "show ip route", ct).ConfigureAwait(false);
            return DeviceOutputParsers.LongestMatch(full, dst);
        }
        if (DeviceOutputParsers.ParseIosRouteEntry(text) is { } ios) return ios;
        if (DeviceOutputParsers.NotInTable(text))
        {
            // IOS leaves the default route out of "show ip route <ip>" - ask for it explicitly.
            if (quiet) return null;
            var def = await Run(cli, node, "show ip route 0.0.0.0", ct).ConfigureAwait(false);
            return DeviceOutputParsers.ParseIosRouteEntry(def) ?? DeviceOutputParsers.LongestMatch(def, dst);
        }
        return DeviceOutputParsers.LongestMatch(text, dst); // NX-OS style
    }

    private async Task HealthAsync(IDeviceCli cli, PathNode node, string iface, CancellationToken ct)
    {
        if (node.Interfaces.ContainsKey(iface) || cli.IsJunos) return;
        var text = await Run(cli, node, $"show interfaces {iface}", ct).ConfigureAwait(false);
        if (DeviceSession.IsCommandError(text)) return;
        if (DeviceOutputParsers.ParseInterface(text) is { } h) lock (_sync) node.Interfaces[iface] = h;
    }

    private async Task<(SessionProfile Profile, string Key, string Ingress)?> NeighborOnAsync(IDeviceCli cli, PathNode node, NextHop hop, CancellationToken ct)
    {
        if (hop.Interface.Length > 0 && !cli.IsJunos)
        {
            var cdp = await Run(cli, node, $"show cdp neighbors {hop.Interface} detail", ct).ConfigureAwait(false);
            var n = DeviceSession.IsCommandError(cdp) ? null : NeighborParser.ParseCdpDetail(cdp).FirstOrDefault(x => x.MgmtIp is not null);
            if (n is null)
            {
                var lldp = await Run(cli, node, $"show lldp neighbors {hop.Interface} detail", ct).ConfigureAwait(false);
                n = DeviceSession.IsCommandError(lldp) ? null : NeighborParser.ParseLldpDetail(lldp).FirstOrDefault(x => x.MgmtIp is not null);
            }
            if (n is not null)
            {
                var known = _knownDevice(n.MgmtIp!);
                return (known ?? _profileFor(NeighborParser.DisplayName(n.Name), n.MgmtIp!), NeighborParser.NormalizeName(n.Name), n.RemoteInterface);
            }
        }
        if (hop.Address.Length > 0)
        {
            var known = _knownDevice(hop.Address);
            if (known is not null) return (known, NeighborParser.NormalizeName(known.Name), "");
            return (_profileFor(hop.Address, hop.Address), hop.Address, "");
        }
        return null;
    }

    /// <summary>The destination is on a network this device is attached to: find its MAC, then walk the switches.</summary>
    private async Task FinishOnSegmentAsync(IDeviceCli cli, PathNode router, RouteDecision route, IPAddress dst, int depth, DevicePathOptions options, CancellationToken ct)
    {
        var iface = route.Paths.FirstOrDefault()?.Interface ?? "";
        var arpText = await Run(cli, router, $"show ip arp {dst}", ct).ConfigureAwait(false);
        if (DeviceSession.IsCommandError(arpText) || DeviceOutputParsers.ParseArp(arpText, dst.ToString()) is null)
            arpText = await Run(cli, router, "show ip arp", ct).ConfigureAwait(false);
        var arp = DeviceOutputParsers.ParseArp(arpText, dst.ToString());
        var host = Node($"host:{dst}", dst.ToString(), depth + 1);
        host.Kind = PathNodeKind.Host;
        host.MgmtIp = dst.ToString();
        if (options.InterfaceStats && iface.Length > 0) await HealthAsync(cli, router, iface, ct).ConfigureAwait(false);
        if (arp is null)
        {
            Set(router, PathNodeStatus.Done, $"{dst} is on {route.Prefix} ({iface})");
            Set(host, PathNodeStatus.NotReached, $"no ARP entry on {router.Name}: the host isn't answering (down, wrong VLAN/subnet, or it never sent traffic)");
            AddLink(new PathLink(router.Key, iface, "", host.Key, "", false));
            return;
        }
        Set(router, PathNodeStatus.Done, $"{dst} is on {route.Prefix} - ARP {arp.Mac} on {arp.Interface}");
        if (!options.LayerTwo || !DeviceOutputParsers.IsSvi(arp.Interface))
        {
            host.Attachment = $"{router.Name} {arp.Interface}";
            Set(host, PathNodeStatus.Destination, $"MAC {arp.Mac}, directly on {router.Name} {arp.Interface}");
            AddLink(new PathLink(router.Key, arp.Interface, "", host.Key, "", false));
            return;
        }

        // Layer 2: follow the MAC through the switches of that VLAN.
        IDeviceCli current = cli;
        PathNode at = router;
        var owned = new List<IDeviceCli>();
        try
        {
            for (int guard = 0; guard < 8; guard++)
            {
                var macText = await Run(current, at, $"show mac address-table address {arp.Mac}", ct).ConfigureAwait(false);
                var mac = DeviceOutputParsers.ParseMac(macText, arp.Mac);
                if (mac is null)
                {
                    host.Attachment = $"{at.Name} (VLAN {arp.Interface})";
                    Set(host, PathNodeStatus.Destination, $"MAC {arp.Mac} - not in {at.Name}'s MAC table (aged out?)");
                    AddLink(new PathLink(at.Key, arp.Interface, "", host.Key, "", at != router));
                    return;
                }
                if (options.InterfaceStats) await HealthAsync(current, at, mac.Port, ct).ConfigureAwait(false);
                var cdp = await Run(current, at, $"show cdp neighbors {mac.Port} detail", ct).ConfigureAwait(false);
                var n = DeviceSession.IsCommandError(cdp) ? null : NeighborParser.ParseCdpDetail(cdp).FirstOrDefault();
                var kind = n is null ? NodeKind.Unknown : NeighborParser.Classify(n.Platform, n.Capabilities);
                if (n is null || n.MgmtIp is null || kind is not (NodeKind.Switch or NodeKind.Router))
                {
                    host.Attachment = $"{at.Name} {mac.Port} (VLAN {mac.Vlan})";
                    Set(host, PathNodeStatus.Destination, $"MAC {arp.Mac} on {at.Name} {mac.Port}, VLAN {mac.Vlan}" + (n is null ? "" : $" - behind {NeighborParser.DisplayName(n.Name)}"));
                    AddLink(new PathLink(at.Key, mac.Port, "", host.Key, "", at != router));
                    return;
                }
                // Another switch: log in and keep following.
                var key = NeighborParser.NormalizeName(n.Name);
                var sw = Node(key, NeighborParser.DisplayName(n.Name), at.Depth + 1);
                sw.Kind = PathNodeKind.Switch;
                lock (_sync) host.Depth = sw.Depth + 1;
                sw.MgmtIp = n.MgmtIp;
                AddLink(new PathLink(at.Key, mac.Port, "", key, n.RemoteInterface, true));
                if (_logins >= options.MaxDevices) { Set(sw, PathNodeStatus.NotReached, "device limit reached"); return; }
                Set(sw, PathNodeStatus.Working, "logging in…");
                IDeviceCli next;
                try
                {
                    Interlocked.Increment(ref _logins);
                    next = await _connect(_knownDevice(n.MgmtIp) ?? _profileFor(sw.Name, n.MgmtIp), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Set(sw, PathNodeStatus.Failed, $"couldn't log in: {ex.Message}");
                    host.Attachment = $"somewhere behind {sw.Name}";
                    Set(host, PathNodeStatus.NotReached, $"MAC {arp.Mac} is behind {sw.Name}, which couldn't be logged in to");
                    AddLink(new PathLink(sw.Key, "", "", host.Key, "", true));
                    return;
                }
                owned.Add(next);
                if (options.InterfaceStats && n.RemoteInterface.Length > 0) await HealthAsync(next, sw, n.RemoteInterface, ct).ConfigureAwait(false);
                Set(sw, PathNodeStatus.Done, $"layer 2 - following MAC {arp.Mac}");
                current = next;
                at = sw;
            }
        }
        finally
        {
            foreach (var o in owned) o.Dispose();
        }
    }

    private async Task<string> Run(IDeviceCli cli, PathNode node, string command, CancellationToken ct)
    {
        lock (_sync) node.Commands.Add(command);
        return await cli.RunAsync(command, ct).ConfigureAwait(false);
    }

    private string Resolve(string key)
    {
        lock (_sync) return Alias(key);
    }

    private string Alias(string key) => _aliases.TryGetValue(key, out var real) ? real : key;

    private PathNode Node(string key, string name, int depth)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(key, out var n))
            {
                n = new PathNode { Key = key, Name = name, Depth = depth };
                _nodes[key] = n;
            }
            return n;
        }
    }

    private void Set(PathNode node, PathNodeStatus status, string text)
    {
        lock (_sync) { node.Status = status; node.StatusText = text; }
        Changed?.Invoke();
    }

    private void AddLink(PathLink link)
    {
        lock (_sync) if (!_links.Contains(link)) _links.Add(link);
        Changed?.Invoke();
    }

    /// <summary>What's wrong on the traced path, worst first: drops, no-route, errors, saturation, unknown hops.</summary>
    public static IReadOnlyList<TraceFinding> Findings(IReadOnlyList<PathNode> nodes, IReadOnlyList<PathLink> links, IPAddress destination)
    {
        var f = new List<TraceFinding>();
        foreach (var n in nodes)
        {
            if (n.Status == PathNodeStatus.NoRoute)
                f.Add(new(InsightSeverity.Problem, $"{n.Name} has no route to {destination}", "Traffic is dropped here. Check the routing protocol (missing adjacency or redistribution) or add a route."));
            if (n.Status == PathNodeStatus.Failed)
                f.Add(new(InsightSeverity.Warning, $"Couldn't log in to {n.Name}", $"{n.StatusText}. The path beyond it is unknown - save a session for it with working credentials."));
            if (n.Status == PathNodeStatus.NotReached && n.Kind == PathNodeKind.Host)
                f.Add(new(InsightSeverity.Problem, $"{n.Name}: {n.StatusText}", "The routers get traffic to the right subnet, but the last step to the host fails."));
            else if (n.Status == PathNodeStatus.NotReached)
                f.Add(new(InsightSeverity.Warning, $"Hop {n.Name} not identified", n.StatusText));
            foreach (var (name, h) in n.Interfaces)
            {
                if (!h.Up)
                    f.Add(new(InsightSeverity.Problem, $"{n.Name} {name} is {h.Status}/{h.Protocol}", "An interface on the path is down."));
                if (h.InputErrors >= 100 || h.Crc >= 50)
                    f.Add(new(InsightSeverity.Warning, $"{n.Name} {name}: {h.InputErrors:N0} input errors ({h.Crc:N0} CRC)",
                        "Physical-layer trouble - a bad cable, dirty/failing optic or a duplex mismatch. Clear the counters and watch whether they climb."));
                if (h.OutputDrops >= 1000)
                    f.Add(new(InsightSeverity.Warning, $"{n.Name} {name}: {h.OutputDrops:N0} output drops", "The interface is congested or a QoS policy is discarding traffic."));
                double util = Math.Max(h.InUtil, h.OutUtil);
                if (!double.IsNaN(util) && util >= 80)
                    f.Add(new(util >= 95 ? InsightSeverity.Problem : InsightSeverity.Warning, $"{n.Name} {name} is {util:0}% busy",
                        $"In {h.InUtil:0}%, out {h.OutUtil:0}% of {h.SpeedText}. Expect queuing delay and drops at this hop."));
            }
            if (n.Route is { Paths.Count: > 1 } r)
                f.Add(new(InsightSeverity.Info, $"{n.Name} load-balances over {r.Paths.Count} paths", string.Join(", ", r.Paths.Select(p => $"{p.Address} via {p.Interface}"))));
        }
        var host = nodes.FirstOrDefault(n => n.Kind == PathNodeKind.Host && n.Status == PathNodeStatus.Destination);
        if (host is not null)
            f.Add(new(InsightSeverity.Info, $"{destination} found: {host.Attachment}", host.StatusText));
        else if (nodes.Any(n => n.Status == PathNodeStatus.Destination))
            f.Add(new(InsightSeverity.Info, $"{destination} reached", "The destination is an address on one of the traced devices."));
        if (f.Count == 0)
            f.Add(new(InsightSeverity.Info, "Path traced", $"{nodes.Count} device(s), no problems found on the interfaces checked."));
        return f.OrderBy(x => x.Severity).ToList();
    }
}
