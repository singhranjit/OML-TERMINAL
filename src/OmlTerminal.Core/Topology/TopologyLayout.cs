namespace OmlTerminal.Core.Topology;

/// <summary>Layered placement: the seed on top, each hop one band further down, nodes within a band ordered by where
/// their upstream neighbors sit (fewer crossing lines). Wide bands wrap onto extra rows.</summary>
public static class TopologyLayout
{
    public const double NodeWidth = 176;
    public const double NodeHeight = 64;

    public static Dictionary<string, (double X, double Y)> Layered(TopologyGraph graph, double gapX = 44, double gapY = 96, int maxPerRow = 8, double margin = 40)
    {
        var adjacency = graph.Nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var l in graph.Links)
        {
            if (!adjacency.ContainsKey(l.A) || !adjacency.ContainsKey(l.B)) continue;
            adjacency[l.A].Add(l.B);
            adjacency[l.B].Add(l.A);
        }

        // BFS depth from the seed; any disconnected pieces start their own BFS one band below the deepest so far.
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var roots = graph.Nodes.Values.OrderByDescending(n => n.IsSeed).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Select(n => n.Key);
        foreach (var root in roots)
        {
            if (depth.ContainsKey(root)) continue;
            int start = depth.Count == 0 ? 0 : depth.Values.Max() + 1;
            var queue = new Queue<string>();
            depth[root] = start;
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                foreach (var n in adjacency[k].Where(n => !depth.ContainsKey(n)))
                {
                    depth[n] = depth[k] + 1;
                    queue.Enqueue(n);
                }
            }
        }

        var pos = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        double y = 0;
        foreach (var band in depth.GroupBy(d => d.Value).OrderBy(g => g.Key))
        {
            var ordered = band.Select(d => d.Key)
                .Select(k => (Key: k, Bary: adjacency[k].Where(pos.ContainsKey).Select(n => pos[n].X).DefaultIfEmpty(double.NaN).Average()))
                .OrderBy(t => double.IsNaN(t.Bary) ? double.MaxValue : t.Bary)
                .ThenBy(t => graph.Nodes[t.Key].Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.Key).ToList();
            foreach (var row in ordered.Chunk(maxPerRow))
            {
                double width = row.Length * NodeWidth + (row.Length - 1) * gapX;
                double x = -width / 2;
                foreach (var k in row)
                {
                    pos[k] = (x, y);
                    x += NodeWidth + gapX;
                }
                y += NodeHeight + gapY;
            }
        }

        if (pos.Count == 0) return pos;
        double minX = pos.Values.Min(p => p.X);
        return pos.ToDictionary(p => p.Key, p => (p.Value.X - minX + margin, p.Value.Y + margin), StringComparer.OrdinalIgnoreCase);
    }
}
