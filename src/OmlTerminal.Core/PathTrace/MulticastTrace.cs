using System.Net;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Ssh;
using OmlTerminal.Core.Topology;

namespace OmlTerminal.Core.PathTrace;

public sealed record RpfInfo(string Interface, string Neighbor, string Route, string Type, bool DirectlyConnected);

public sealed record MrouteEntry(string Source, string Group, string Flags, string Uptime, string IncomingInterface, string RpfNeighbor, IReadOnlyList<string> Outgoing);

public sealed record MrouteCounters(long Forwarded, long Pps, long Kbps, long RpfFailed, long OtherDrops);

/// <summary>One router on the multicast tree, from the receiver's side back toward the source.</summary>
public sealed class McastHop
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string? MgmtIp { get; set; }
    public int Index { get; set; }
    public RpfInfo? Rpf { get; set; }
    public MrouteEntry? SG { get; set; }
    public MrouteEntry? StarG { get; set; }
    public MrouteCounters? Counters { get; set; }
    public IReadOnlyList<string> IgmpInterfaces { get; set; } = [];
    public bool? RpfNeighborIsPim { get; set; }
    public string Status { get; set; } = "";
    public bool Failed { get; set; }
    public List<string> Commands { get; } = [];
}

/// <summary>A hop of a router-run mtrace ("-2  10.255.102.1 ==> 10.10.50.2 PIM  [10.10.50.0/24]").</summary>
public sealed record MtraceHop(int Index, string Address, string Name, string OutAddress, string Protocol, string Prefix, string Note, double? Ms);

public static partial class MulticastParsers
{
    private const string Ip = @"\d{1,3}(?:\.\d{1,3}){3}";

    [GeneratedRegex(@"RPF interface:\s*(?<if>\S+)")] private static partial Regex RpfIf();
    [GeneratedRegex(@"RPF neighbor:\s*(?:\S+\s+)?\(?(?<nbr>" + Ip + @")\)?(?<direct>\s*-\s*directly connected)?")] private static partial Regex RpfNbr();
    [GeneratedRegex(@"RPF route/mask:\s*(?<r>\S+)")] private static partial Regex RpfRoute();
    [GeneratedRegex(@"RPF type:\s*(?<t>.+)$", RegexOptions.Multiline)] private static partial Regex RpfType();

    /// <summary>IOS/IOS-XE "show ip rpf &lt;source&gt;". Null when the lookup failed (no route to the source).</summary>
    public static RpfInfo? ParseRpf(string text)
    {
        if (text.Contains("failed", StringComparison.OrdinalIgnoreCase) && text.Contains("no route", StringComparison.OrdinalIgnoreCase)) return null;
        var i = RpfIf().Match(text);
        if (!i.Success) return null;
        var n = RpfNbr().Match(text);
        var nbr = n.Success ? n.Groups["nbr"].Value : "";
        bool direct = n.Success && (n.Groups["direct"].Success || nbr == "0.0.0.0");
        return new RpfInfo(i.Groups["if"].Value, direct ? "" : nbr, RpfRoute().Match(text) is { Success: true } r ? r.Groups["r"].Value : "",
            RpfType().Match(text) is { Success: true } t ? t.Groups["t"].Value.Trim() : "", direct);
    }

    [GeneratedRegex(@"^\((?<s>\*|" + Ip + @"), (?<g>" + Ip + @")\), (?<up>\S+?)(?:/\S+)?, (?:RP [^,]+, )?flags: (?<f>\S*)", RegexOptions.Multiline)]
    private static partial Regex MrouteHead();
    [GeneratedRegex(@"Incoming interface: (?<if>[^,\s]+), RPF nbr (?<nbr>\S+)")] private static partial Regex Incoming();
    [GeneratedRegex(@"^\s{4}(?<if>\S+), (?:Forward|Prune)", RegexOptions.Multiline)] private static partial Regex Oil();

    /// <summary>The (S,G) and (*,G) entries from "show ip mroute &lt;group&gt; [source]".</summary>
    public static IReadOnlyList<MrouteEntry> ParseMroute(string text)
    {
        var list = new List<MrouteEntry>();
        var heads = MrouteHead().Matches(text);
        for (int i = 0; i < heads.Count; i++)
        {
            var h = heads[i];
            int end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
            var block = text[h.Index..end];
            var inc = Incoming().Match(block);
            int oilStart = block.IndexOf("Outgoing interface list", StringComparison.Ordinal);
            var oil = new List<string>();
            if (oilStart >= 0 && !block[oilStart..].Contains("Outgoing interface list: Null"))
                oil.AddRange(Oil().Matches(block[oilStart..]).Select(m => m.Groups["if"].Value));
            list.Add(new MrouteEntry(h.Groups["s"].Value, h.Groups["g"].Value, h.Groups["f"].Value, h.Groups["up"].Value,
                inc.Success ? inc.Groups["if"].Value : "", inc.Success ? inc.Groups["nbr"].Value : "", oil));
        }
        return list;
    }

    [GeneratedRegex(@"Forwarding: (?<pk>\d+)/(?<pps>\d+)/\d+/(?<kbps>\d+), Other: \d+/(?<rpf>\d+)/(?<other>\d+)")]
    private static partial Regex Count();

    /// <summary>"show ip mroute &lt;group&gt; &lt;source&gt; count" - Forwarding: pkts/pps/avg-size/kbps, Other: total/RPF-failed/other-drops.</summary>
    public static MrouteCounters? ParseCount(string text)
    {
        var m = Count().Match(text);
        return m.Success ? new MrouteCounters(long.Parse(m.Groups["pk"].Value), long.Parse(m.Groups["pps"].Value), long.Parse(m.Groups["kbps"].Value),
            long.Parse(m.Groups["rpf"].Value), long.Parse(m.Groups["other"].Value)) : null;
    }

    [GeneratedRegex(@"^(?<g>" + Ip + @")\s+(?<if>\S+)\s+\S+\s+\S+\s+(?<rep>" + Ip + @")", RegexOptions.Multiline)]
    private static partial Regex Igmp();

    /// <summary>Interfaces with IGMP members of the group ("show ip igmp groups &lt;group&gt;").</summary>
    public static IReadOnlyList<string> ParseIgmp(string text, string group) =>
        Igmp().Matches(text).Where(m => m.Groups["g"].Value == group).Select(m => m.Groups["if"].Value).Distinct().ToList();

    /// <summary>Neighbor addresses from "show ip pim neighbor".</summary>
    public static IReadOnlySet<string> ParsePimNeighbors(string text) =>
        Regex.Matches(text, @"^(?<ip>" + Ip + @")\s+\S+", RegexOptions.Multiline).Select(m => m.Groups["ip"].Value).ToHashSet();

    [GeneratedRegex(@"^\s*(?<n>-?\d+)\s+(?<addr>" + Ip + @")(?:\s+\((?<name>[^)]*)\))?(?<rest>.*)$", RegexOptions.Multiline)]
    private static partial Regex MtraceHead();

    /// <summary>Cisco IOS/IOS-XE "mtrace &lt;source&gt; [dest] [group]" hop list, receiver side first.</summary>
    public static IReadOnlyList<MtraceHop> ParseMtrace(string text)
    {
        var hops = new List<MtraceHop>();
        foreach (Match m in MtraceHead().Matches(text))
        {
            var rest = m.Groups["rest"].Value;
            string outAddr = "", proto = "", prefix = "";
            double? ms = null;
            if (Regex.Match(rest, @"\[(?<p>[^\]]+)\]") is { Success: true } pm) { prefix = pm.Groups["p"].Value; rest = rest.Remove(pm.Index, pm.Length); }
            if (Regex.Match(rest, @"(?<ms>\d+(?:\.\d+)?) ms\b") is { Success: true } mm) { ms = double.Parse(mm.Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture); rest = rest.Remove(mm.Index, mm.Length); }
            if (Regex.Match(rest, @"==>\s*(?<o>" + Ip + @")(?:\s+(?<proto>[A-Z][A-Za-z_]*)\b)?") is { Success: true } om)
            {
                outAddr = om.Groups["o"].Value;
                proto = om.Groups["proto"].Value;
                rest = rest.Remove(om.Index, om.Length);
            }
            rest = Regex.Replace(rest, @"Thresh\^\s*\d+", "");
            var name = m.Groups["name"].Value;
            hops.Add(new MtraceHop(Math.Abs(int.Parse(m.Groups["n"].Value)), m.Groups["addr"].Value, name == "?" ? "" : name,
                outAddr, proto, prefix, Regex.Replace(rest, @"\s+", " ").Trim(), ms));
        }
        return hops;
    }

    /// <summary>mtrace forwarding codes that mean the tree is broken at that hop.</summary>
    public static bool IsMtraceError(string note) => note.Length > 0 && !note.StartsWith("Reached RP", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(note, "RPF|No route|Wrong|Prune|pruned|boundary|Prohibited|No space|Fatal|No multicast|not forwarding|Unknown", RegexOptions.IgnoreCase);
}

/// <summary>
/// Walks a multicast distribution tree hop by hop, from the router nearest the receivers back to the source - each
/// router's RPF check, its (S,G)/(*,G) state, packet counters and PIM neighbors. Works where mtrace isn't
/// supported, and says exactly which check fails where. Only "show" commands.
/// </summary>
public sealed class MulticastTracer
{
    private readonly object _sync = new();
    private readonly List<McastHop> _hops = [];
    private readonly Func<SessionProfile, CancellationToken, Task<IDeviceCli>> _connect;
    private readonly Func<string, string, SessionProfile> _profileFor;
    private readonly Func<string, SessionProfile?> _knownDevice;

    public event Action? Changed;

    public MulticastTracer(Func<string, string, SessionProfile> profileFor, Func<string, SessionProfile?>? knownDevice = null,
        Func<SessionProfile, CancellationToken, Task<IDeviceCli>>? connect = null)
    {
        _profileFor = profileFor;
        _knownDevice = knownDevice ?? (_ => null);
        _connect = connect ?? SshDeviceCli.OpenAsync;
    }

    public IReadOnlyList<McastHop> Hops { get { lock (_sync) return _hops.ToList(); } }

    public async Task TraceAsync(SessionProfile lastHop, IPAddress source, IPAddress group, int maxHops, CancellationToken ct)
    {
        var profile = lastHop;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < maxHops && profile is not null; i++)
        {
            ct.ThrowIfCancellationRequested();
            var hop = new McastHop { Key = profile.Host, Name = profile.Name, MgmtIp = profile.Host, Index = i, Status = "logging in…" };
            lock (_sync) _hops.Add(hop);
            Changed?.Invoke();
            IDeviceCli cli;
            try { cli = await _connect(profile, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Update(hop, h => { h.Failed = true; h.Status = $"couldn't log in: {ex.Message}"; }); return; }
            profile = null;
            using (cli)
            {
                Update(hop, h => h.Name = NeighborParser.DisplayName(cli.Hostname));
                if (!seen.Add(NeighborParser.NormalizeName(cli.Hostname)))
                {
                    Update(hop, h => { h.Failed = true; h.Status = "loop - this router was already on the path"; });
                    return;
                }
                if (i == 0)
                {
                    var igmp = await Run(cli, hop, $"show ip igmp groups {group}", ct).ConfigureAwait(false);
                    Update(hop, h => h.IgmpInterfaces = MulticastParsers.ParseIgmp(igmp, group.ToString()));
                }
                var rpfText = await Run(cli, hop, $"show ip rpf {source}", ct).ConfigureAwait(false);
                var rpf = DeviceSession.IsCommandError(rpfText) ? null : MulticastParsers.ParseRpf(rpfText);
                var mrText = await Run(cli, hop, $"show ip mroute {group} {source}", ct).ConfigureAwait(false);
                var sg = MulticastParsers.ParseMroute(mrText).FirstOrDefault(e => e.Source == source.ToString());
                var starText = await Run(cli, hop, $"show ip mroute {group}", ct).ConfigureAwait(false);
                var star = MulticastParsers.ParseMroute(starText).FirstOrDefault(e => e.Source == "*");
                var cnt = sg is null ? null : MulticastParsers.ParseCount(await Run(cli, hop, $"show ip mroute {group} {source} count", ct).ConfigureAwait(false));
                bool? pimOk = null;
                if (rpf is { DirectlyConnected: false, Neighbor.Length: > 0 })
                {
                    var pim = await Run(cli, hop, "show ip pim neighbor", ct).ConfigureAwait(false);
                    if (!DeviceSession.IsCommandError(pim)) pimOk = MulticastParsers.ParsePimNeighbors(pim).Contains(rpf.Neighbor);
                }
                Update(hop, h => { h.Rpf = rpf; h.SG = sg; h.StarG = star; h.Counters = cnt; h.RpfNeighborIsPim = pimOk; });

                if (rpf is null)
                {
                    Update(hop, h => { h.Failed = true; h.Status = $"RPF check fails - no route back to {source}"; });
                    return;
                }
                if (rpf.DirectlyConnected)
                {
                    Update(hop, h => h.Status = $"first-hop router - {source} is directly connected on {rpf.Interface}");
                    lock (_sync) _hops.Add(new McastHop { Key = $"src:{source}", Name = source.ToString(), MgmtIp = source.ToString(), Index = i + 1, Status = "multicast source" });
                    Changed?.Invoke();
                    return;
                }
                Update(hop, h => h.Status = $"RPF via {rpf.Interface} to {rpf.Neighbor}");

                // Upstream neighbor: CDP on the RPF interface, else a saved session for the RPF neighbor's address, else SSH to it.
                var cdp = await Run(cli, hop, $"show cdp neighbors {rpf.Interface} detail", ct).ConfigureAwait(false);
                var n = DeviceSession.IsCommandError(cdp) ? null : NeighborParser.ParseCdpDetail(cdp).FirstOrDefault(x => x.MgmtIp is not null);
                profile = n is not null
                    ? _knownDevice(n.MgmtIp!) ?? _profileFor(NeighborParser.DisplayName(n.Name), n.MgmtIp!)
                    : _knownDevice(rpf.Neighbor) ?? _profileFor(rpf.Neighbor, rpf.Neighbor);
            }
        }
    }

    private async Task<string> Run(IDeviceCli cli, McastHop hop, string command, CancellationToken ct)
    {
        lock (_sync) hop.Commands.Add(command);
        return await cli.RunAsync(command, ct).ConfigureAwait(false);
    }

    private void Update(McastHop hop, Action<McastHop> change)
    {
        lock (_sync) change(hop);
        Changed?.Invoke();
    }

    public static IReadOnlyList<TraceFinding> Findings(IReadOnlyList<McastHop> hops, IPAddress source, IPAddress group)
    {
        var f = new List<TraceFinding>();
        var routers = hops.Where(h => !h.Key.StartsWith("src:")).ToList();
        if (routers.Count > 0 && routers[0].IgmpInterfaces.Count == 0 && !routers[0].Failed)
            f.Add(new(InsightSeverity.Warning, $"No IGMP members of {group} on {routers[0].Name}",
                "Nobody on this router's interfaces has joined the group - check the receiver, IGMP snooping/querier on its VLAN, or start from the right router.", 0));
        foreach (var h in routers)
        {
            if (h.Failed)
                f.Add(new(InsightSeverity.Problem, $"{h.Name}: {h.Status}", h.Rpf is null && h.Status.StartsWith("RPF")
                    ? "Multicast is forwarded only along the unicast route back to the source. Add a route (or a static mroute) toward the source on this router."
                    : "The trace couldn't continue past this router.", h.Index));
            if (h.Rpf is not null && h.SG is null && h.StarG is null)
                f.Add(new(InsightSeverity.Problem, $"{h.Name} has no multicast state for {group}",
                    "No (S,G) or (*,G) entry - the join never reached this router. Check PIM is enabled on the interfaces toward the receivers.", h.Index));
            if (h.SG is { } sg)
            {
                if (sg.IncomingInterface.Equals("Null", StringComparison.OrdinalIgnoreCase))
                    f.Add(new(InsightSeverity.Problem, $"{h.Name}: incoming interface is Null", "The RPF lookup for the source failed when the state was built.", h.Index));
                else if (h.Rpf is not null && !sg.IncomingInterface.Equals(h.Rpf.Interface, StringComparison.OrdinalIgnoreCase))
                    f.Add(new(InsightSeverity.Warning, $"{h.Name}: (S,G) arrives on {sg.IncomingInterface} but RPF points to {h.Rpf.Interface}",
                        "The tree and the unicast route disagree - packets arriving on the 'wrong' interface fail the RPF check and are dropped.", h.Index));
                if (sg.Outgoing.Count == 0 || sg.Flags.Contains('P'))
                    f.Add(new(InsightSeverity.Warning, $"{h.Name} has pruned the (S,G)", "Its outgoing interface list is empty - downstream routers stopped asking for this stream.", h.Index));
            }
            if (h.Counters is { } c)
            {
                // A handful of RPF drops (during a convergence, say) is normal; a steady stream means a second path.
                if (c.RpfFailed >= 1000 || (c.RpfFailed > 0 && c.RpfFailed * 100 >= Math.Max(1, c.Forwarded)))
                    f.Add(new(InsightSeverity.Warning, $"{h.Name}: {c.RpfFailed:N0} RPF failures", "Packets for this stream arrive on an interface that isn't the RPF interface - asymmetric routing or a second path.", h.Index));
                if (c.Pps == 0)
                    f.Add(new(InsightSeverity.Warning, $"{h.Name} forwards 0 packets/s", "State exists but no traffic is flowing through this router right now - the problem is upstream of it, or the source isn't sending.", h.Index));
                else
                    f.Add(new(InsightSeverity.Info, $"{h.Name} forwards {c.Pps} pps ({c.Kbps} kbps)", "Traffic is flowing through this hop.", h.Index));
            }
            if (h.RpfNeighborIsPim == false)
                f.Add(new(InsightSeverity.Problem, $"{h.Name}: RPF neighbor {h.Rpf?.Neighbor} isn't a PIM neighbor",
                    $"PIM isn't running (or the adjacency is down) on {h.Rpf?.Interface} - joins can't go upstream. Enable 'ip pim sparse-mode' on both ends.", h.Index));
        }
        if (hops.Any(h => h.Key.StartsWith("src:")) && !f.Any(x => x.Severity == InsightSeverity.Problem))
            f.Insert(0, new(InsightSeverity.Info, "Complete tree from the source to the receivers", $"{routers.Count} router(s) - each one has a route back to the source and multicast state for the stream."));
        return f.OrderBy(x => x.Severity).ToList();
    }
}
