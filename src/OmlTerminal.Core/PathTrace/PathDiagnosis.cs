using System.Net.NetworkInformation;
using OmlTerminal.Core.Capture;

namespace OmlTerminal.Core.PathTrace;

public sealed record TraceFinding(InsightSeverity Severity, string Title, string Detail, int? Hop = null);

/// <summary>
/// Reads a traceroute the way an experienced engineer would. The classic trap it avoids: a router in the middle
/// that shows 40% loss while every hop after it shows 0% isn't losing traffic - it just answers traceroute probes
/// at low priority. Real loss starts at a hop and carries on all the way to the destination.
/// </summary>
public static class PathDiagnosis
{
    public const int MinRounds = 5;

    public static IReadOnlyList<TraceFinding> Analyze(TraceSnapshot t)
    {
        var f = new List<TraceFinding>();
        var hops = t.Hops;
        if (t.Rounds < MinRounds || hops.Count == 0)
        {
            f.Add(new(InsightSeverity.Info, "Collecting samples…", $"Findings appear after {MinRounds} rounds ({t.Rounds} so far)."));
            return f;
        }
        var answering = hops.Where(h => !h.Silent).ToList();
        var dest = t.Reached ? hops[^1] : null;

        // ---- unreachable / never reached
        var refused = hops.FirstOrDefault(h => h.Unreachable is not null && h.Received > 0);
        if (refused is not null)
        {
            var why = refused.Unreachable switch
            {
                IPStatus.DestinationProhibited => "administratively prohibited - an ACL or firewall rule is rejecting it",
                IPStatus.DestinationNetworkUnreachable => "network unreachable - it has no route to that network",
                IPStatus.DestinationPortUnreachable => "port unreachable",
                _ => "host unreachable - it has a route, but the host doesn't answer ARP (powered off, wrong VLAN, or the wrong IP)",
            };
            f.Add(new(InsightSeverity.Problem, $"Hop {refused.Hop} refuses the traffic",
                $"{Who(refused)} replies \"destination unreachable\": {why}.", refused.Hop));
        }
        else if (!t.Reached)
        {
            var last = answering.LastOrDefault();
            f.Add(new(InsightSeverity.Problem, "The destination never answers",
                last is null
                    ? "Not even the first hop answers - check this PC's connection, default gateway, and that ICMP isn't blocked locally."
                    : $"The last router to answer is hop {last.Hop}, {Who(last)}. Something after it drops the probes: a firewall/ACL, a missing return route, " +
                      $"or {t.Target} is down or ignores ping. Try a TCP port check to tell \"blocked\" from \"down\".", last?.Hop));
        }

        // ---- routing loop: the same address at two different TTLs
        var seen = new Dictionary<string, int>();
        foreach (var h in hops)
            foreach (var a in h.Addresses)
            {
                if (seen.TryGetValue(a, out var first) && h.Hop - first >= 2 && !t.Reached)
                {
                    f.Add(new(InsightSeverity.Problem, "Routing loop",
                        $"{a} answers at hop {first} and again at hop {h.Hop}: packets are going round in circles between the routers in between. " +
                        "Look for two routers pointing at each other (a static route, or a default route back the way it came).", first));
                    goto loopDone;
                }
                seen.TryAdd(a, h.Hop);
            }
        loopDone:

        // ---- loss: find where it starts and whether it carries through
        if (dest is not null && dest.LossPercent >= 2)
        {
            double threshold = Math.Max(1, dest.LossPercent * 0.5);
            int start = dest.Hop;
            for (int i = hops.Count - 1; i >= 0; i--)
            {
                var h = hops[i];
                if (h.Silent) continue;          // a silent hop says nothing either way
                if (h.LossPercent >= threshold) start = h.Hop;
                else break;
            }
            var origin = hops[start - 1];
            var before = hops.Take(start - 1).LastOrDefault(h => !h.Silent);
            if (start == dest.Hop)
                f.Add(new(InsightSeverity.Problem, $"{dest.LossPercent:0.#}% loss at the destination only",
                    $"Every router on the way is clean; only {t.Target} itself drops {dest.LossPercent:0.#}% of replies. " +
                    "Look at the host (CPU, NIC, its firewall's ICMP rate limit) and its access switch port (errors, duplex).", dest.Hop));
            else
                f.Add(new(InsightSeverity.Problem, $"{dest.LossPercent:0.#}% loss - it starts at hop {start}",
                    $"Loss begins at {Who(origin)} and continues all the way to the destination, so it's real. The fault is that router or the link " +
                    (before is null ? "into it." : $"between hop {before.Hop} ({Who(before)}) and hop {start}: check that link for errors, drops or saturation."), start));
        }

        // ---- loss at a hop that doesn't carry forward = ICMP rate limiting
        foreach (var h in answering.Where(h => !h.IsDestination && h.LossPercent >= 10))
        {
            var later = answering.Where(x => x.Hop > h.Hop).ToList();
            if (later.Count > 0 && later.Min(x => x.LossPercent) < h.LossPercent / 2)
                f.Add(new(InsightSeverity.Info, $"Hop {h.Hop} shows {h.LossPercent:0.#}% loss - not a real problem",
                    $"{Who(h)} drops traceroute replies but the hops after it don't lose anything, so traffic passes through it fine. " +
                    "Routers answer TTL-expired probes from their CPU at low priority (control-plane policing). Ignore this one.", h.Hop));
        }

        // ---- latency: the biggest jump that persists to the end
        var lat = answering.Where(h => !double.IsNaN(h.Mean)).ToList();
        if (lat.Count >= 2)
        {
            double endMean = lat[^1].Mean;
            (HopView A, HopView B, double D)? jump = null;
            for (int i = 1; i < lat.Count; i++)
            {
                double d = lat[i].Mean - lat[i - 1].Mean;
                bool persists = endMean >= lat[i].Mean - Math.Max(10, d * 0.3);
                if (d >= 40 && persists && (jump is null || d > jump.Value.D)) jump = (lat[i - 1], lat[i], d);
            }
            if (jump is { } j)
            {
                bool congested = !double.IsNaN(j.B.StDev) && j.B.StDev > Math.Max(15, j.B.Mean * 0.25);
                bool crossesOwner = j.A.Owner?.Asn != j.B.Owner?.Asn || j.A.Owner?.Kind != j.B.Owner?.Kind;
                f.Add(new(congested ? InsightSeverity.Warning : InsightSeverity.Info, $"Latency jumps {j.D:0} ms between hop {j.A.Hop} and {j.B.Hop}",
                    $"{Who(j.A)} → {Who(j.B)}. " + (congested
                        ? $"The jitter there ({j.B.StDev:0} ms) says the link is congested or queuing."
                        : crossesOwner ? "It's steady and crosses into another network - most likely a long-distance link, which is normal."
                                       : "It's steady, so most likely distance (a WAN or long-haul link) rather than congestion."), j.B.Hop));
            }
            foreach (var h in lat.Where(h => !h.IsDestination))
            {
                var after = lat.Where(x => x.Hop > h.Hop).ToList();
                if (after.Count > 0 && h.Mean - after.Max(x => x.Mean) > 50)
                {
                    f.Add(new(InsightSeverity.Info, $"Hop {h.Hop} answers slowly - not a real delay",
                        $"{Who(h)} averages {h.Mean:0} ms but later hops are faster, so packets aren't delayed there - it just takes its time replying to traceroute.", h.Hop));
                    break;
                }
            }
        }

        // ---- jitter and voice quality at the destination
        if (dest is not null && dest.Received >= MinRounds)
        {
            double jitter = double.IsNaN(dest.Jitter) ? 0 : dest.Jitter;
            if (jitter > 30)
                f.Add(new(InsightSeverity.Warning, $"Jitter {jitter:0} ms to the destination",
                    "Voice and video need under 30 ms. Find the first hop where jitter rises - that's where queues are building.", dest.Hop));
            var mos = Mos(dest.Mean, jitter, dest.LossPercent);
            f.Add(new(mos >= 4.0 ? InsightSeverity.Info : mos >= 3.6 ? InsightSeverity.Warning : InsightSeverity.Problem,
                $"Call quality estimate: MOS {mos:0.0} ({MosWord(mos)})",
                $"From {dest.Mean:0} ms latency, {jitter:0.#} ms jitter and {dest.LossPercent:0.#}% loss (ITU-T G.107 E-model, simplified). 4.0+ is toll quality.", dest.Hop));
        }

        // ---- route changes and load balancing
        if (t.Changes.Count > 0)
        {
            var last = t.Changes[^1];
            f.Add(new(InsightSeverity.Warning, $"The route changed {t.Changes.Count} time{(t.Changes.Count == 1 ? "" : "s")}",
                $"Most recently at {last.At:HH:mm:ss}: hop {last.Hop} moved from {last.From} to {last.To}. Frequent changes point to a flapping link or routing protocol.", last.Hop));
        }
        foreach (var h in answering.Where(h => h.Addresses.Count > 1 && h.Flips >= 3))
            f.Add(new(InsightSeverity.Info, $"Hop {h.Hop} is load-balanced",
                $"It alternates between {string.Join(", ", h.Addresses.Take(4))} - equal-cost paths. Different flows may take different routes.", h.Hop));

        // ---- silent middle hops
        var silent = hops.Where(h => h.Silent && !h.IsDestination && answering.Any(a => a.Hop > h.Hop)).Select(h => h.Hop).ToList();
        if (silent.Count > 0)
            f.Add(new(InsightSeverity.Info, $"Hop{(silent.Count == 1 ? "" : "s")} {string.Join(", ", silent)} never answer{(silent.Count == 1 ? "s" : "")}",
                "Traffic passes through (later hops answer), the device is just configured not to send TTL-expired replies - typical of firewalls and MPLS cores.", silent[0]));

        // ---- where traffic leaves your network
        var firstPublic = answering.FirstOrDefault(h => h.Owner?.Kind == HopOwnerKind.Public);
        if (firstPublic is not null && answering.Any(h => h.Hop < firstPublic.Hop && h.Owner?.Kind is HopOwnerKind.Private or HopOwnerKind.Device or HopOwnerKind.CarrierNat))
            f.Add(new(InsightSeverity.Info, $"Traffic leaves your network at hop {firstPublic.Hop}",
                $"{Who(firstPublic)} is the first public hop{(firstPublic.Owner?.AsName is { } n ? $" ({n})" : "")}. Problems from there on are your ISP's or beyond.", firstPublic.Hop));

        if (!f.Any(x => x.Severity != InsightSeverity.Info) && dest is not null)
            f.Insert(0, new(InsightSeverity.Info, "The path looks healthy",
                $"{dest.LossPercent:0.#}% loss, {dest.Mean:0.#} ms average, {(double.IsNaN(dest.Jitter) ? 0 : dest.Jitter):0.#} ms jitter to {t.Target} over {t.Rounds} rounds."));
        return f.OrderBy(x => x.Severity).ToList();
    }

    /// <summary>Mean opinion score from the E-model's R-factor, the usual simplification used by VoIP monitors.</summary>
    public static double Mos(double latencyMs, double jitterMs, double lossPercent)
    {
        if (double.IsNaN(latencyMs)) return 1;
        double effective = latencyMs + jitterMs * 2 + 10;
        double r = effective < 160 ? 93.2 - effective / 40 : 93.2 - (effective - 120) / 10;
        r -= lossPercent * 2.5;
        r = Math.Clamp(r, 0, 100);
        return Math.Round(1 + 0.035 * r + 0.000007 * r * (r - 60) * (100 - r), 2);
    }

    public static string MosWord(double mos) => mos >= 4.3 ? "excellent" : mos >= 4.0 ? "good" : mos >= 3.6 ? "fair" : mos >= 3.1 ? "poor" : "bad";

    private static string Who(HopView h)
    {
        var label = h.Owner?.Device ?? (h.Name.Length > 0 ? h.Name : null);
        return label is null ? h.Address ?? $"hop {h.Hop}" : $"{label} ({h.Address})";
    }
}
