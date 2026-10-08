using System.Text;

namespace OmlTerminal.Core.Topology;

public enum NodeStatus { Discovered, Crawling, Crawled, Failed, Skipped }

public sealed class TopologyNode
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string? MgmtIp { get; set; }
    public string Platform { get; set; } = "";
    public string Capabilities { get; set; } = "";
    public NodeKind Kind { get; set; } = NodeKind.Unknown;
    public NodeStatus Status { get; set; } = NodeStatus.Discovered;
    /// <summary>Why it failed or wasn't crawled ("out of scope", "auth failed: ...").</summary>
    public string StatusText { get; set; } = "";
    public int Depth { get; set; }
    public bool IsSeed { get; set; }
}

public sealed record TopologyLink(string A, string APort, string B, string BPort, string Protocol);

/// <summary>Devices and the links between them. The same cable is usually reported from both ends (and by both CDP
/// and LLDP); <see cref="AddLink"/> keeps one copy.</summary>
public sealed class TopologyGraph
{
    private readonly object _lock = new();

    public Dictionary<string, TopologyNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TopologyLink> Links { get; } = new();

    public TopologyNode Ensure(string key, string name)
    {
        lock (_lock)
        {
            if (!Nodes.TryGetValue(key, out var node))
            {
                node = new TopologyNode { Key = key, Name = name };
                Nodes[key] = node;
            }
            return node;
        }
    }

    public bool AddLink(string a, string aPort, string b, string bPort, string protocol)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_lock)
        {
            foreach (var l in Links)
            {
                bool same = Same(l.A, a) && Same(l.B, b) && (Same(l.APort, aPort) || Same(l.BPort, bPort))
                         || Same(l.A, b) && Same(l.B, a) && (Same(l.APort, bPort) || Same(l.BPort, aPort));
                if (same) return false;
            }
            Links.Add(new TopologyLink(a, aPort, b, bPort, protocol));
            return true;
        }
        static bool Same(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A copy that's safe to lay out and draw while a crawl is still adding to this graph.</summary>
    public TopologyGraph Snapshot()
    {
        var copy = new TopologyGraph();
        lock (_lock)
        {
            foreach (var (k, n) in Nodes)
                copy.Nodes[k] = new TopologyNode
                {
                    Key = n.Key, Name = n.Name, MgmtIp = n.MgmtIp, Platform = n.Platform, Capabilities = n.Capabilities,
                    Kind = n.Kind, Status = n.Status, StatusText = n.StatusText, Depth = n.Depth, IsSeed = n.IsSeed,
                };
            copy.Links.AddRange(Links);
        }
        return copy;
    }

    public IReadOnlyList<TopologyLink> LinksOf(string key)
    {
        lock (_lock) return Links.Where(l => l.A.Equals(key, StringComparison.OrdinalIgnoreCase) || l.B.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public string ToCsv()
    {
        var sb = new StringBuilder("Device A,Port A,Device B,Port B,Protocol\n");
        lock (_lock)
            foreach (var l in Links)
                sb.AppendLine(string.Join(",", new[] { Name(l.A), l.APort, Name(l.B), l.BPort, l.Protocol }.Select(Esc)));
        return sb.ToString();
        static string Esc(string f) => f.Contains(',') || f.Contains('"') ? $"\"{f.Replace("\"", "\"\"")}\"" : f;
    }

    /// <summary>Graphviz DOT, for anyone who wants to render or restyle the map elsewhere.</summary>
    public string ToDot()
    {
        var sb = new StringBuilder("graph topology {\n  node [shape=box, style=rounded];\n");
        lock (_lock)
        {
            foreach (var n in Nodes.Values)
                sb.AppendLine($"  \"{Q(n.Key)}\" [label=\"{Q(n.Name)}{(n.MgmtIp is { } ip ? "\\n" + ip : "")}\"];");
            foreach (var l in Links)
                sb.AppendLine($"  \"{Q(l.A)}\" -- \"{Q(l.B)}\" [taillabel=\"{Q(l.APort)}\", headlabel=\"{Q(l.BPort)}\"];");
        }
        sb.AppendLine("}");
        return sb.ToString();
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private string Name(string key) => Nodes.TryGetValue(key, out var n) ? n.Name : key;
}
