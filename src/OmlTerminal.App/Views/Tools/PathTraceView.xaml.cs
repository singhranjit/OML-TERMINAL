using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.PathTrace;
using OmlTerminal.Core.Topology;
using Windows.Foundation;

namespace OmlTerminal.App.Views.Tools;

public sealed class HopRow
{
    public required HopView Hop { get; init; }
    public required double Scale { get; init; } // ms per pixel-width of the range column
    public required bool RateLimited { get; init; }
    public required double EffectiveLoss { get; init; }
    public string HopText => Hop.Hop.ToString();
    public string Title => Hop.Silent ? "no reply" : Hop.Label + (Hop.Addresses.Count > 1 ? $"  (+{Hop.Addresses.Count - 1} more)" : "");
    public string Subtitle => Hop.Silent ? "this hop doesn't answer traceroute"
        : string.Join("  ·  ", new[] { Hop.Address, Hop.Name.Length > 0 && Hop.Name != Hop.Label ? Hop.Name : null, Hop.Owner?.Text }.Where(s => !string.IsNullOrEmpty(s)));
    public string LossText => Hop.Sent == 0 ? "" : $"{Hop.LossPercent:0.#}%" + (RateLimited ? "*" : "");
    public Brush LossBrush => ToolUi.Brush(EffectiveLoss >= 5 ? "OmlRoseBrush" : EffectiveLoss >= 1 ? "OmlAmberBrush" : RateLimited ? "OmlSkyBrush" : "OmlMintBrush");
    private static string Ms(double v) => double.IsNaN(v) ? "" : v >= 100 ? $"{v:0}" : $"{v:0.#}";
    public string LastText => Ms(Hop.Last);
    public string AvgText => Ms(Hop.Mean);
    public string BestText => Ms(Hop.Best);
    public string WorstText => Ms(Hop.Worst);
    public string JitterText => Ms(Hop.Jitter);
    public Brush BarBrush => ToolUi.Brush(Hop.Mean >= 150 ? "OmlRoseBrush" : Hop.Mean >= 60 ? "OmlAmberBrush" : "OmlSkyBrush");
    public double BarWidth => double.IsNaN(Hop.Best) ? 0 : Math.Max(4, (Hop.Worst - Hop.Best) / Scale);
    public Thickness BarMargin => new(double.IsNaN(Hop.Best) ? 0 : Hop.Best / Scale, 0, 0, 0);
    public Thickness AvgMargin => new(double.IsNaN(Hop.Mean) ? 0 : Math.Max(0, Hop.Mean / Scale - 1.5), 0, 0, 0);
    public Visibility MarkVisibility => double.IsNaN(Hop.Mean) ? Visibility.Collapsed : Visibility.Visible;
    public override string ToString() => $"Hop {HopText}, {Title}, loss {LossText}, average {AvgText} ms";
}

public sealed class NodeRow
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public required string Address { get; init; }
    public required string Status { get; init; }
    public required string Body { get; init; }
    public required string CommandsText { get; init; }
    public required Brush Brush { get; init; }
    public override string ToString() => $"{Title}, {Status}";
}

public sealed class TraceFindingRow
{
    public required TraceFinding Finding { get; init; }
    public string Glyph => Finding.Severity switch { InsightSeverity.Problem => "", InsightSeverity.Warning => "", _ => "" };
    public Brush Brush => ToolUi.Brush(Finding.Severity switch { InsightSeverity.Problem => "OmlRoseBrush", InsightSeverity.Warning => "OmlAmberBrush", _ => "OmlSkyBrush" });
    public override string ToString() => $"{Finding.Severity}: {Finding.Title}";
}

public sealed partial class PathTraceView : UserControl, IToolView
{
    private const double NodeW = 168, NodeH = 86, LanePitch = 112, Left = 18, Top = 22;
    private double ColPitch => Mode == 1 ? 258 : 240; // device mode carries interface labels between columns
    private const string GlyphPc = "", GlyphRouter = "", GlyphSwitch = "", GlyphHost = "", GlyphCloud = "", GlyphSilent = "", GlyphSource = "";

    private sealed record DNode(string Key, int Col, int Lane, string Glyph, string Title, string Line2, string Line3, string Accent, bool Dashed, string Tip, int? Hop);
    private sealed record DEdge(string From, string To, string Mid, string FromLabel, string ToLabel, string Accent, bool Dashed);

    private readonly ToolContext _ctx;
    private readonly HopIdentifier _identifier;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _cts;
    private ContinuousTrace? _trace;
    private DevicePathTracer? _dev;
    private IPAddress? _devDest;
    private MulticastTracer? _mc;
    private (IPAddress Source, IPAddress Group)? _mcTarget;
    private IReadOnlyList<MtraceHop>? _mtrace;
    private string _mtraceRouter = "";
    private volatile bool _dirty;
    private int _selectedHop;
    private bool _updatingList;
    private int Mode => Modes.Items.IndexOf(Modes.SelectedItem);

    public PathTraceView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _identifier = new HopIdentifier(() => _ctx.Sessions(), _ctx.Settings.BackupDirectory);
        _refresh.Tick += (_, _) => { if (_dirty) { _dirty = false; Render(); } };
        Loaded += (_, _) =>
        {
            var sessions = _ctx.SshSessions();
            StartDeviceBox.ItemsSource = sessions;
            McastStartBox.ItemsSource = sessions;
            if (sessions.Count > 0) { StartDeviceBox.SelectedIndex = 0; McastStartBox.SelectedIndex = 0; }
            _refresh.Start();
        };
        Unloaded += (_, _) => _refresh.Stop();
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _refresh.Stop();
    }

    private void Modes_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (McastControls is null) return; // fires inside InitializeComponent
        int m = Mode;
        PcControls.Visibility = m == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceControls.Visibility = m == 1 ? Visibility.Visible : Visibility.Collapsed;
        McastControls.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
        PcDetails.Visibility = m == 0 ? Visibility.Visible : Visibility.Collapsed;
        NodeDetails.Visibility = m == 0 ? Visibility.Collapsed : Visibility.Visible;
        TimelineCard.Visibility = m == 0 ? Visibility.Visible : Visibility.Collapsed;
        PathEmpty.Text = m switch
        {
            0 => "Enter a destination and press Start. Each hop appears as it answers; colours show where loss and delay really are - green clean, amber suspect, red the problem.",
            1 => "Pick the router nearest the source (a saved SSH session) and the destination. The tool logs in hop by hop - read-only show commands only - following the routing table, CDP/LLDP and finally ARP and MAC tables to the destination's switch port.",
            _ => "Pick the router nearest the receivers, the source and the group. RPF walk checks every router's RPF, (S,G) state, counters and PIM neighbors back to the source; mtrace runs the router's own multicast traceroute.",
        };
        _dirty = true;
    }

    // ======================= From this PC =======================

    private void TargetBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && PcStart.IsEnabled) PcStart_Click(sender, e);
    }

    private async void PcStart_Click(object sender, RoutedEventArgs e)
    {
        var text = TargetBox.Text.Trim();
        if (text.Length == 0) { StatusText.Text = "Enter a destination."; return; }
        IPAddress target;
        if (!IPAddress.TryParse(text, out target!))
        {
            StatusText.Text = $"Resolving {text}…";
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(text);
                target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.First();
            }
            catch (Exception ex) { StatusText.Text = $"Can't resolve {text}: {ex.Message}"; return; }
        }
        Stop();
        _cts = new CancellationTokenSource();
        _trace = new ContinuousTrace(target, new TraceOptions
        {
            MaxHops = (int)Num(MaxHopsBox, 30),
            PacketSize = (int)Num(SizeBox, 32),
            Interval = TimeSpan.FromSeconds(Num(IntervalBox, 1)),
            TimeoutMs = 2000,
        }, identify: _identifier.IdentifyAsync);
        _trace.Updated += () => _dirty = true;
        _selectedHop = 0;
        SetRunning(true);
        StatusText.Text = $"Tracing {text}{(text == target.ToString() ? "" : $" ({target})")}…";
        var trace = _trace;
        try { await Task.Run(() => trace.RunAsync(_cts.Token)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = ex.Message; }
        finally { if (_trace == trace) SetRunning(false); }
    }

    private static double Num(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetRunning(false);
    }

    private void SetRunning(bool running)
    {
        PcStart.IsEnabled = DevStart.IsEnabled = McStart.IsEnabled = !running;
        PcStop.IsEnabled = DevStop.IsEnabled = McStop.IsEnabled = running;
        _dirty = true;
    }

    private void RenderPc()
    {
        if (_trace is null) { ClearAll(); return; }
        var s = _trace.Snapshot();
        var findings = PathDiagnosis.Analyze(s);
        var hops = s.Hops;
        StatusText.Text = $"{s.Target} · round {s.Rounds}" + (s.Reached ? $" · {hops.Count} hops" : " · destination not reached yet") +
                          (_cts is null ? " · stopped" : "");

        // Effective loss: what a hop passes on. A hop can't lose more than the hops after it see.
        var eff = new double[hops.Count];
        double floor = double.MaxValue;
        for (int i = hops.Count - 1; i >= 0; i--)
        {
            if (!hops[i].Silent) floor = Math.Min(floor, hops[i].LossPercent);
            eff[i] = hops[i].Silent ? 0 : Math.Min(hops[i].LossPercent, floor);
        }
        var jumpHop = findings.FirstOrDefault(f => f.Title.StartsWith("Latency jumps") && f.Severity != InsightSeverity.Info)?.Hop;

        var nodes = new List<DNode> { new("pc", 0, 0, GlyphPc, "This PC", LocalAddressFor(_trace.Target), "", "OmlSkyBrush", false, "Where the probes start", null) };
        var edges = new List<DEdge>();
        string prev = "pc";
        double prevMean = 0;
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            bool limited = !h.Silent && h.LossPercent >= 5 && eff[i] < h.LossPercent / 2;
            string accent = h.Silent ? "StatusIdleBrush" : eff[i] >= 5 ? "OmlRoseBrush" : eff[i] >= 1 || jumpHop == h.Hop ? "OmlAmberBrush" : "OmlMintBrush";
            string glyph = h.Silent ? GlyphSilent : h.IsDestination ? GlyphCloud : h.Owner?.Kind == HopOwnerKind.Device ? GlyphSwitch : GlyphRouter;
            string line3 = h.Silent ? "* * *" : $"{(double.IsNaN(h.Mean) ? "-" : h.Mean.ToString("0.#"))} ms · {h.LossPercent:0.#}% loss{(limited ? " (ICMP limit)" : "")}";
            var tip = h.Silent ? $"Hop {h.Hop}: no reply" :
                $"Hop {h.Hop}: {h.Label}\n{string.Join(", ", h.Addresses)}{(h.Name.Length > 0 ? $"\n{h.Name}" : "")}{(h.Owner is null ? "" : $"\n{h.Owner.Text}")}\n" +
                $"loss {h.LossPercent:0.#}% ({h.Sent - h.Received}/{h.Sent}) · last {h.Last:0.#} · avg {h.Mean:0.#} · best {h.Best:0.#} · worst {h.Worst:0.#} · jitter {h.Jitter:0.#} ms";
            var key = $"h{h.Hop}";
            nodes.Add(new DNode(key, h.Hop, 0, glyph, h.Silent ? $"hop {h.Hop}" : Shorten(h.Label, 22), h.Silent ? "no reply" : h.Address ?? "", line3, accent, h.Silent, tip, h.Hop));
            double delta = h.Silent || double.IsNaN(h.Mean) ? double.NaN : h.Mean - prevMean;
            edges.Add(new DEdge(prev, key, double.IsNaN(delta) || delta < 1 ? "" : $"+{delta:0} ms", "", "", accent == "StatusIdleBrush" ? "HairlineBrush" : accent, h.Silent));
            prev = key;
            if (!h.Silent && !double.IsNaN(h.Mean)) prevMean = h.Mean;
        }
        if (!s.Reached && hops.Count > 0)
        {
            nodes.Add(new DNode("dest", hops.Count + 1, 0, GlyphCloud, s.Target, "not reached", "", "OmlRoseBrush", true, "The destination hasn't answered", null));
            edges.Add(new DEdge(prev, "dest", "?", "", "", "OmlRoseBrush", true));
        }
        DrawDiagram(nodes, edges);

        // Hop table
        double maxWorst = hops.Where(h => !double.IsNaN(h.Worst)).Select(h => h.Worst).DefaultIfEmpty(10).Max();
        double scale = Math.Max(maxWorst, 1) / 180.0;
        var rows = hops.Select((h, i) => new HopRow { Hop = h, Scale = scale, EffectiveLoss = eff[i], RateLimited = !h.Silent && h.LossPercent >= 5 && eff[i] < h.LossPercent / 2 }).ToList();
        _updatingList = true;
        HopList.ItemsSource = rows;
        if (_selectedHop == 0 && hops.Count > 0) _selectedHop = hops[^1].Hop;
        HopList.SelectedItem = rows.FirstOrDefault(r => r.Hop.Hop == _selectedHop);
        _updatingList = false;
        RenderTimeline();
        SetFindings(findings);
    }

    private static string LocalAddressFor(IPAddress target)
    {
        try
        {
            using var s = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, 9);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return ""; }
    }

    private void HopList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || HopList.SelectedItem is not HopRow r) return;
        _selectedHop = r.Hop.Hop;
        RenderTimeline();
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTimeline();

    private void RenderTimeline()
    {
        TimelineCanvas.Children.Clear();
        if (_trace is null || _selectedHop == 0) return;
        double w = TimelineCanvas.ActualWidth, h = TimelineCanvas.ActualHeight;
        if (w < 80 || h < 40) return;
        const double left = 40;
        int capacity = (int)((w - left) / 4);
        var all = _trace.SamplesFor(_selectedHop);
        var samples = all.Skip(Math.Max(0, all.Count - capacity)).ToList();
        var hop = _trace.Snapshot().Hops.FirstOrDefault(x => x.Hop == _selectedHop);
        TimelineTitle.Text = hop is null ? "LATENCY OVER TIME" :
            $"HOP {hop.Hop} · {hop.Label.ToUpperInvariant()} · LAST {samples.Count} PROBES";
        if (samples.Count == 0) return;
        double max = Math.Max(10, samples.Where(s => s.RttMs is not null).Select(s => s.RttMs!.Value).DefaultIfEmpty(10).Max() * 1.15);
        double Y(double v) => 4 + (h - 8) * (1 - v / max);
        var axis = ToolUi.Brush("HairlineBrush");
        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            double v = max * frac, y = Y(v);
            TimelineCanvas.Children.Add(new Line { X1 = left, X2 = w, Y1 = y, Y2 = y, Stroke = axis, StrokeThickness = 1 });
            var t = new TextBlock { Text = $"{v:0}", FontSize = 10.5, Opacity = 0.55 };
            Canvas.SetTop(t, y - 8);
            TimelineCanvas.Children.Add(t);
        }
        // Spread a short run across the width; once there are more samples than pixels allow, it scrolls.
        double step = (w - left) / Math.Max(samples.Count - 1, 30);
        var line = new Polyline { Stroke = ToolUi.Brush("OmlSkyBrush"), StrokeThickness = 1.6 };
        var lost = ToolUi.Brush("OmlRoseBrush");
        for (int i = 0; i < samples.Count; i++)
        {
            double x = left + i * step;
            if (samples[i].RttMs is { } v) line.Points.Add(new Point(x, Y(v)));
            else
            {
                TimelineCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 4, Y2 = h - 4, Stroke = lost, StrokeThickness = Math.Max(1.5, step * 0.8), Opacity = 0.7 });
                if (line.Points.Count > 0) { TimelineCanvas.Children.Add(line); line = new Polyline { Stroke = line.Stroke, StrokeThickness = 1.6 }; }
            }
        }
        if (line.Points.Count > 0) TimelineCanvas.Children.Add(line);
        if (hop is not null && !double.IsNaN(hop.Mean))
        {
            double y = Y(hop.Mean);
            TimelineCanvas.Children.Add(new Line { X1 = left, X2 = w, Y1 = y, Y2 = y, Stroke = ToolUi.Brush("OmlAmberBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.8 });
        }
    }

    // ======================= Through my devices =======================

    private void DeviceDestBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && DevStart.IsEnabled) DevStart_Click(sender, e);
    }

    private SessionProfile? KnownDevice(string ip)
    {
        var sessions = _ctx.SshSessions();
        var byHost = sessions.FirstOrDefault(s => s.Host == ip);
        if (byHost is not null) return byHost;
        var owner = _identifier.OwnerOf(ip);
        return owner is null ? null : sessions.FirstOrDefault(s => s.Name.Equals(owner.Value.Device, StringComparison.OrdinalIgnoreCase));
    }

    private static Func<string, string, SessionProfile> ProfileFrom(SessionProfile seed) => (name, ip) =>
    {
        var p = seed.Clone();
        p.Id = Guid.NewGuid();
        p.Name = name;
        p.Host = ip;
        p.Port = 22;
        p.Protocol = ProtocolKind.Ssh;
        return p;
    };

    private async void DevStart_Click(object sender, RoutedEventArgs e)
    {
        if (StartDeviceBox.SelectedItem is not SessionProfile start) { StatusText.Text = "Pick the starting device (a saved SSH session)."; return; }
        if (!IPAddress.TryParse(DeviceDestBox.Text.Trim(), out var dest)) { StatusText.Text = "Enter the destination as an IP address."; return; }
        Stop();
        var seed = _ctx.SshSessions().FirstOrDefault(s => s.Id == start.Id) ?? start;
        _cts = new CancellationTokenSource();
        _dev = new DevicePathTracer(ProfileFrom(seed), KnownDevice);
        _devDest = dest;
        _dev.Changed += () => _dirty = true;
        SetRunning(true);
        StatusText.Text = $"Tracing {start.Name} → {dest} hop by hop…";
        var options = new DevicePathOptions { FollowEcmp = EcmpCheck.IsChecked == true, LayerTwo = L2Check.IsChecked == true, InterfaceStats = StatsCheck.IsChecked == true };
        var tracer = _dev;
        var ct = _cts.Token;
        try
        {
            await Task.Run(() => tracer.TraceAsync(seed, dest, options, ct));
            StatusText.Text = $"{start.Name} → {dest}: done.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { if (_dev == tracer) { _cts = null; SetRunning(false); } }
    }

    private void RenderDevices()
    {
        if (_dev is null || _devDest is null) { ClearAll(); return; }
        var (pathNodes, links) = _dev.Snapshot();
        var lanes = new Dictionary<int, int>();
        var nodes = new List<DNode>();
        foreach (var n in pathNodes.OrderBy(n => n.Depth))
        {
            int lane = lanes.GetValueOrDefault(n.Depth);
            lanes[n.Depth] = lane + 1;
            var (accent, glyph) = NodeLook(n);
            var line3 = n.Status switch
            {
                PathNodeStatus.Working => "working…",
                PathNodeStatus.Pending => "queued",
                _ => n.Route is { } r && n.Kind != PathNodeKind.Host ? Shorten(r.Text, 30) : Shorten(n.StatusText, 30),
            };
            nodes.Add(new DNode(n.Key, n.Depth, lane, glyph, Shorten(n.Name, 22), n.MgmtIp ?? "", line3, accent, n.Status is PathNodeStatus.NotReached or PathNodeStatus.Pending,
                $"{n.Name}\n{n.StatusText}{(n.Route is null ? "" : $"\nroute: {n.Route.Text}")}", null));
        }
        var byKey = pathNodes.ToDictionary(n => n.Key);
        var edges = links.Select(l =>
        {
            var from = byKey.GetValueOrDefault(l.From);
            var to = byKey.GetValueOrDefault(l.To);
            var egress = from?.Interfaces.GetValueOrDefault(l.Egress);
            var ingress = to?.Interfaces.GetValueOrDefault(l.Ingress);
            string accent = "OmlMintBrush";
            foreach (var h in new[] { egress, ingress })
            {
                if (h is null) continue;
                double util = Math.Max(h.InUtil, h.OutUtil);
                if (!h.Up || util >= 95) accent = "OmlRoseBrush";
                else if ((util >= 80 || h.InputErrors >= 100 || h.Crc >= 50 || h.OutputDrops >= 1000) && accent != "OmlRoseBrush") accent = "OmlAmberBrush";
            }
            var fromLabel = NeighborParser.ShortInterface(l.Egress);
            if (egress is not null && !double.IsNaN(egress.OutUtil)) fromLabel += $" · {egress.OutUtil:0}%";
            if (to?.Status is PathNodeStatus.Failed or PathNodeStatus.NotReached) accent = "OmlAmberBrush";
            return new DEdge(l.From, l.To, "", fromLabel, NeighborParser.ShortInterface(l.Ingress), accent, l.Layer2);
        }).ToList();
        DrawDiagram(nodes, edges);

        NodeList.ItemsSource = pathNodes.OrderBy(n => n.Depth).Select(n =>
        {
            var (accent, glyph) = NodeLook(n);
            var body = new StringBuilder();
            if (n.Route is { } r)
            {
                body.AppendLine($"route   {r.Text}");
                foreach (var p in r.Paths) body.AppendLine($"        → {(p.Address.Length > 0 ? p.Address : "direct")} via {p.Interface}");
            }
            if (n.Attachment is not null) body.AppendLine($"port    {n.Attachment}");
            foreach (var (name, h) in n.Interfaces)
                body.AppendLine($"{NeighborParser.ShortInterface(name),-11}{h.Status}/{h.Protocol} {h.SpeedText}  in {Pct(h.InUtil)} out {Pct(h.OutUtil)}  err {h.InputErrors:N0} (crc {h.Crc:N0})  drops {h.OutputDrops:N0}" +
                                (h.Description.Length > 0 ? $"  \"{h.Description}\"" : ""));
            return new NodeRow
            {
                Glyph = glyph, Title = n.Name, Address = n.MgmtIp ?? "", Status = n.StatusText, Body = body.ToString().TrimEnd(),
                CommandsText = n.Commands.Count == 0 ? "" : "ran: " + string.Join(" · ", n.Commands.Distinct()), Brush = ToolUi.Brush(accent),
            };
        }).ToList();
        SetFindings(DevicePathTracer.Findings(pathNodes, links, _devDest));
        if (_cts is not null) StatusText.Text = $"{pathNodes.Count(n => n.Status is PathNodeStatus.Done or PathNodeStatus.Destination)} device(s) traced so far…";
    }

    private static string Pct(double v) => double.IsNaN(v) ? "-" : $"{v:0}%";

    private static (string Accent, string Glyph) NodeLook(PathNode n)
    {
        string glyph = n.Kind switch { PathNodeKind.Host => GlyphHost, PathNodeKind.Switch => GlyphSwitch, _ => GlyphRouter };
        bool sick = n.Interfaces.Values.Any(h => !h.Up || Math.Max(h.InUtil, h.OutUtil) >= 80 || h.InputErrors >= 100 || h.OutputDrops >= 1000);
        string accent = n.Status switch
        {
            PathNodeStatus.Failed or PathNodeStatus.NoRoute => "OmlRoseBrush",
            PathNodeStatus.NotReached => n.Kind == PathNodeKind.Host ? "OmlRoseBrush" : "OmlAmberBrush",
            PathNodeStatus.Working => "OmlSkyBrush",
            PathNodeStatus.Pending => "StatusIdleBrush",
            _ => sick ? "OmlAmberBrush" : "OmlMintBrush",
        };
        return (accent, glyph);
    }

    // ======================= Multicast =======================

    private async void McStart_Click(object sender, RoutedEventArgs e)
    {
        if (McastStartBox.SelectedItem is not SessionProfile start) { StatusText.Text = "Pick the router nearest the receivers."; return; }
        if (!IPAddress.TryParse(McastSourceBox.Text.Trim(), out var source)) { StatusText.Text = "Enter the multicast source's IP address."; return; }
        if (!IPAddress.TryParse(McastGroupBox.Text.Trim(), out var group) || group.GetAddressBytes()[0] is < 224 or > 239)
        { StatusText.Text = "Enter a multicast group (224.0.0.0 - 239.255.255.255)."; return; }
        Stop();
        var seed = _ctx.SshSessions().FirstOrDefault(s => s.Id == start.Id) ?? start;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _mcTarget = (source, group);
        _mc = null;
        _mtrace = null;
        SetRunning(true);
        try
        {
            if (McastMethodBox.SelectedIndex == 1)
            {
                StatusText.Text = $"Running mtrace on {start.Name}…";
                _mtraceRouter = start.Name;
                var text = await Task.Run(async () =>
                {
                    using var cli = await SshDeviceCli.OpenAsync(seed, ct);
                    return await ((SshDeviceCli)cli).RunSlowAsync($"mtrace {source} {seed.Host} {group}", TimeSpan.FromSeconds(8), ct);
                }, ct);
                _mtrace = MulticastParsers.ParseMtrace(text);
                StatusText.Text = _mtrace.Count == 0 ? $"{start.Name} didn't return an mtrace (unsupported command?) - try the RPF walk." : $"mtrace from {start.Name}: {_mtrace.Count} hops.";
            }
            else
            {
                var tracer = _mc = new MulticastTracer(ProfileFrom(seed), KnownDevice);
                tracer.Changed += () => _dirty = true;
                StatusText.Text = $"Walking the tree from {start.Name} back to {source}…";
                await Task.Run(() => tracer.TraceAsync(seed, source, group, 16, ct));
                StatusText.Text = $"({source}, {group}): {tracer.Hops.Count} hop(s) checked.";
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _cts = null; SetRunning(false); }
    }

    private void RenderMulticast()
    {
        if (_mcTarget is not { } target) { ClearAll(); return; }
        if (_mtrace is not null) { RenderMtrace(target); return; }
        if (_mc is null) { ClearAll(); return; }
        var hops = _mc.Hops;
        int n = hops.Count;
        var nodes = new List<DNode>();
        var edges = new List<DEdge>();
        for (int i = 0; i < n; i++)
        {
            var h = hops[i];
            bool isSource = h.Key.StartsWith("src:");
            int col = n - 1 - i;
            bool noState = h.Rpf is not null && h.SG is null && h.StarG is null;
            string accent = isSource ? "OmlSkyBrush" : h.Failed || noState || h.RpfNeighborIsPim == false ? "OmlRoseBrush"
                : h.Counters is { Pps: 0 } || h.Counters is { } rc && (rc.RpfFailed >= 1000 || rc.RpfFailed * 100 >= Math.Max(1, rc.Forwarded)) || h.SG is { Outgoing.Count: 0 } ? "OmlAmberBrush" : "OmlMintBrush";
            string line3 = isSource ? $"source of {target.Group}" : h.Counters is { } c ? $"{c.Pps} pps · {c.Kbps} kbps" : Shorten(h.Status, 30);
            string tip = isSource ? "Multicast source" :
                $"{h.Name}\n{h.Status}\nRPF: {h.Rpf?.Interface ?? "-"} → {h.Rpf?.Neighbor ?? "-"} ({h.Rpf?.Route})\n(S,G): {(h.SG is null ? "none" : $"in {h.SG.IncomingInterface}, out {string.Join(", ", h.SG.Outgoing)}, flags {h.SG.Flags}")}";
            nodes.Add(new DNode(h.Key, col, 0, isSource ? GlyphSource : GlyphRouter, Shorten(h.Name, 22), h.MgmtIp ?? "", line3, accent, false, tip, null));
            if (i > 0)
            {
                // Traffic flows from hop i (upstream) to hop i-1 (downstream), arriving on the downstream router's RPF interface.
                var down = hops[i - 1];
                edges.Add(new DEdge(h.Key, down.Key, "", "",
                    NeighborParser.ShortInterface(down.Rpf?.Interface ?? ""), down.Failed ? "OmlRoseBrush" : accent == "OmlSkyBrush" ? "OmlMintBrush" : accent, false));
            }
        }
        if (n > 0 && hops[0].IgmpInterfaces.Count > 0)
        {
            nodes.Add(new DNode("receivers", n, 0, GlyphHost, "Receivers", string.Join(", ", hops[0].IgmpInterfaces), $"joined {target.Group}", "OmlMintBrush", false, "IGMP members on the last-hop router", null));
            edges.Add(new DEdge(hops[0].Key, "receivers", "", NeighborParser.ShortInterface(hops[0].IgmpInterfaces[0]), "", "OmlMintBrush", true));
        }
        DrawDiagram(nodes, edges);
        NodeList.ItemsSource = hops.Select(h => new NodeRow
        {
            Glyph = h.Key.StartsWith("src:") ? GlyphSource : GlyphRouter,
            Title = h.Name,
            Address = h.MgmtIp ?? "",
            Status = h.Status,
            Body = h.Key.StartsWith("src:") ? "" : string.Join("\n", new[]
            {
                h.IgmpInterfaces.Count > 0 ? $"igmp    members on {string.Join(", ", h.IgmpInterfaces)}" : null,
                h.Rpf is { } r ? $"rpf     {r.Interface} → {(r.DirectlyConnected ? "directly connected" : r.Neighbor)}  ({r.Route}, {r.Type})" : "rpf     FAILED - no route to the source",
                h.SG is { } sg ? $"(S,G)   in {sg.IncomingInterface}  out {(sg.Outgoing.Count == 0 ? "Null" : string.Join(", ", sg.Outgoing))}  flags {sg.Flags}  up {sg.Uptime}" : "(S,G)   none",
                h.StarG is { } sgx ? $"(*,G)   in {sgx.IncomingInterface}  out {(sgx.Outgoing.Count == 0 ? "Null" : string.Join(", ", sgx.Outgoing))}  flags {sgx.Flags}" : null,
                h.Counters is { } c ? $"traffic {c.Pps} pps, {c.Kbps} kbps, {c.Forwarded:N0} forwarded, {c.RpfFailed:N0} RPF failures" : null,
                h.RpfNeighborIsPim is { } ok ? $"pim     RPF neighbor {(ok ? "is" : "is NOT")} a PIM neighbor" : null,
            }.Where(x => x is not null)),
            CommandsText = h.Commands.Count == 0 ? "" : "ran: " + string.Join(" · ", h.Commands),
            Brush = ToolUi.Brush(h.Key.StartsWith("src:") ? "OmlSkyBrush" : h.Failed ? "OmlRoseBrush" : "OmlMintBrush"),
        }).ToList();
        SetFindings(MulticastTracer.Findings(hops, target.Source, target.Group));
    }

    private void RenderMtrace((IPAddress Source, IPAddress Group) target)
    {
        var hops = _mtrace!.OrderByDescending(h => h.Index).ToList(); // source side first
        var nodes = new List<DNode>();
        var edges = new List<DEdge>();
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            bool err = MulticastParsers.IsMtraceError(h.Note);
            var title = h.Name.Length > 0 ? h.Name : i == 0 ? "source" : i == hops.Count - 1 ? "receiver" : h.Address;
            var line3 = string.Join(" · ", new[] { h.Protocol, h.Ms is { } ms ? $"{ms:0} ms" : null, h.Note.Length > 0 ? h.Note : null }.Where(s => !string.IsNullOrEmpty(s)));
            nodes.Add(new DNode($"m{i}", i, 0, i == 0 ? GlyphSource : i == hops.Count - 1 ? GlyphHost : GlyphRouter, Shorten(title, 22), h.Address, Shorten(line3, 30),
                err ? "OmlRoseBrush" : "OmlMintBrush", false, $"{title} {h.Address}\n{h.Prefix}\n{h.Note}", null));
            if (i > 0) edges.Add(new DEdge($"m{i - 1}", $"m{i}", "", "", "", err ? "OmlRoseBrush" : "OmlMintBrush", false));
        }
        DrawDiagram(nodes, edges);
        NodeList.ItemsSource = hops.Select(h => new NodeRow
        {
            Glyph = GlyphRouter, Title = h.Name.Length > 0 ? h.Name : h.Address, Address = h.Address,
            Status = h.Note.Length > 0 ? h.Note : "forwarding", Body = $"hop -{h.Index}  {h.Address}{(h.OutAddress.Length > 0 ? $" ==> {h.OutAddress}" : "")}  {h.Protocol}  [{h.Prefix}]",
            CommandsText = "", Brush = ToolUi.Brush(MulticastParsers.IsMtraceError(h.Note) ? "OmlRoseBrush" : "OmlMintBrush"),
        }).ToList();
        var f = new List<TraceFinding>();
        foreach (var h in hops.Where(h => MulticastParsers.IsMtraceError(h.Note)))
            f.Add(new TraceFinding(InsightSeverity.Problem, $"{(h.Name.Length > 0 ? h.Name : h.Address)}: {h.Note}", "mtrace stopped or reported an error at this router - check its RPF and PIM state (the RPF walk shows exactly which)."));
        if (f.Count == 0 && hops.Count > 0)
            f.Add(new TraceFinding(InsightSeverity.Info, "mtrace completed", $"{hops.Count} hops from {target.Source} to the receiver on {_mtraceRouter}."));
        SetFindings(f);
    }

    // ======================= shared rendering =======================

    private void Render()
    {
        switch (Mode)
        {
            case 0: RenderPc(); break;
            case 1: RenderDevices(); break;
            default: RenderMulticast(); break;
        }
    }

    private void ClearAll()
    {
        PathCanvas.Children.Clear();
        PathCanvas.Width = 0;
        PathEmpty.Visibility = Visibility.Visible;
        FindingList.ItemsSource = null;
        NodeList.ItemsSource = null;
        HopList.ItemsSource = null;
        TimelineCanvas.Children.Clear();
    }

    private void SetFindings(IReadOnlyList<TraceFinding> findings) =>
        FindingList.ItemsSource = findings.Select(f => new TraceFindingRow { Finding = f }).ToList();

    private void FindingList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TraceFindingRow { Finding.Hop: { } hop } && Mode == 0)
        {
            _selectedHop = hop;
            _dirty = true;
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void DrawDiagram(IReadOnlyList<DNode> nodes, IReadOnlyList<DEdge> edges)
    {
        PathCanvas.Children.Clear();
        _labels.Clear();
        PathEmpty.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (nodes.Count == 0) return;
        int maxCol = nodes.Max(n => n.Col), maxLane = nodes.Max(n => n.Lane);
        PathCanvas.Width = Left * 2 + maxCol * ColPitch + NodeW;
        PathCanvas.Height = Top + (maxLane + 1) * LanePitch;
        var pos = nodes.ToDictionary(n => n.Key, n => new Point(Left + n.Col * ColPitch, Top + n.Lane * LanePitch));

        // Several links leaving (or entering) one box are spread down its side, ordered by where they go,
        // so lines and interface labels don't sit on top of each other.
        var valid = edges.Where(e => pos.ContainsKey(e.From) && pos.ContainsKey(e.To)).ToList();
        var outSlot = new Dictionary<DEdge, double>();
        var inSlot = new Dictionary<DEdge, double>();
        foreach (var g in valid.GroupBy(e => e.From))
        {
            var list = g.OrderBy(e => pos[e.To].Y).ToList();
            for (int i = 0; i < list.Count; i++) outSlot[list[i]] = NodeH * (i + 1) / (list.Count + 1);
        }
        foreach (var g in valid.GroupBy(e => e.To))
        {
            var list = g.OrderBy(e => pos[e.From].Y).ToList();
            for (int i = 0; i < list.Count; i++) inSlot[list[i]] = NodeH * (i + 1) / (list.Count + 1);
        }
        foreach (var e in valid)
        {
            var a = pos[e.From];
            var b = pos[e.To];
            var start = new Point(a.X + NodeW, a.Y + outSlot[e]);
            var end = new Point(b.X, b.Y + inSlot[e]);
            var brush = ToolUi.Brush(e.Accent);
            var fig = new PathFigure { StartPoint = start };
            double mx = (start.X + end.X) / 2;
            fig.Segments.Add(new BezierSegment { Point1 = new Point(mx, start.Y), Point2 = new Point(mx, end.Y), Point3 = end });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var path = new Microsoft.UI.Xaml.Shapes.Path { Data = geo, Stroke = brush, StrokeThickness = 2.4, Opacity = 0.9 };
            if (e.Dashed) path.StrokeDashArray = new DoubleCollection { 3, 2 };
            PathCanvas.Children.Add(path);
            var arrow = new Polygon { Fill = brush, Points = { new Point(end.X, end.Y), new Point(end.X - 8, end.Y - 5), new Point(end.X - 8, end.Y + 5) } };
            PathCanvas.Children.Add(arrow);
            if (e.FromLabel.Length > 0) AddLabel(e.FromLabel, start.X + 6, start.Y - 17, false);
            if (e.ToLabel.Length > 0) AddLabel(e.ToLabel, end.X - 10, end.Y + 2, true);
            if (e.Mid.Length > 0)
            {
                var pill = new Border
                {
                    Background = ToolUi.Brush("InputSurfaceBrush"), BorderBrush = brush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 1, 6, 1),
                    Child = new TextBlock { Text = e.Mid, FontSize = 11, Foreground = brush, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"] },
                };
                pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(pill, mx - pill.DesiredSize.Width / 2);
                Canvas.SetTop(pill, (start.Y + end.Y) / 2 + 6);
                PathCanvas.Children.Add(pill);
            }
        }

        foreach (var n in nodes)
        {
            var p = pos[n.Key];
            var accent = ToolUi.Brush(n.Accent);
            var card = new Border
            {
                Width = NodeW, Height = NodeH, CornerRadius = new CornerRadius(12), Background = ToolUi.Brush("InputSurfaceBrush"),
                BorderBrush = accent, BorderThickness = new Thickness(n.Dashed ? 1 : 1.8), Padding = new Thickness(10, 7, 10, 7),
                Opacity = n.Dashed ? 0.75 : 1,
            };
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new FontIcon { Glyph = n.Glyph, FontSize = 20, Foreground = accent, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = n.Line2, FontSize = 11.5, Opacity = 0.65, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = n.Line3, FontSize = 11.5, Foreground = accent, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            card.Child = grid;
            if (n.Hop is { } hopNo && hopNo == _selectedHop && Mode == 0) card.BorderThickness = new Thickness(3);
            ToolTipService.SetToolTip(card, n.Tip);
            if (n.Hop is { } hop)
                card.Tapped += (_, _) => { _selectedHop = hop; _dirty = true; };
            if (n.Col > 0 || n.Key == "pc")
            {
                var badge = new TextBlock { Text = n.Hop is { } hh ? $"HOP {hh}" : "", FontSize = 10, Opacity = 0.5, FontWeight = FontWeights.SemiBold };
                Canvas.SetLeft(badge, p.X + 4);
                Canvas.SetTop(badge, p.Y - 16);
                PathCanvas.Children.Add(badge);
            }
            Canvas.SetLeft(card, p.X);
            Canvas.SetTop(card, p.Y);
            PathCanvas.Children.Add(card);
        }
    }

    private readonly List<Rect> _labels = [];

    /// <summary>Interface label at a link end. If it would overprint another label, it steps away from the line
    /// (egress labels upwards, ingress labels downwards) until it's clear.</summary>
    private void AddLabel(string text, double x, double y, bool alignRight)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, Opacity = 0.8, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"] };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var r = new Rect(alignRight ? x - t.DesiredSize.Width : x, y, t.DesiredSize.Width, 13);
        double step = alignRight ? 12 : -12;
        for (int i = 0; i < 6 && _labels.Any(p => p.X < r.X + r.Width && r.X < p.X + p.Width && p.Y < r.Y + r.Height && r.Y < p.Y + p.Height); i++)
            r.Y += step;
        _labels.Add(r);
        Canvas.SetLeft(t, r.X);
        Canvas.SetTop(t, r.Y);
        PathCanvas.Children.Add(t);
    }

    // ======================= export =======================

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OML Terminal - Visual Trace · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (Mode == 0 && _trace is not null)
        {
            var s = _trace.Snapshot();
            sb.AppendLine($"Destination {s.Target} · {s.Rounds} rounds · {(s.Reached ? "reached" : "NOT reached")}");
            sb.AppendLine();
            sb.AppendLine($"{"Hop",-4}{"Address",-17}{"Name / owner",-40}{"Loss",7}{"Sent",6}{"Last",8}{"Avg",8}{"Best",8}{"Worst",8}{"Jitter",8}");
            foreach (var h in s.Hops)
                sb.AppendLine($"{h.Hop,-4}{h.Address ?? "*",-17}{Shorten(h.Silent ? "(no reply)" : $"{h.Name} {h.Owner?.Text}".Trim(), 39),-40}{h.LossPercent,6:0.#}%{h.Sent,6}{F(h.Last)}{F(h.Mean)}{F(h.Best)}{F(h.Worst)}{F(h.Jitter)}");
            AppendFindings(sb, PathDiagnosis.Analyze(s));
        }
        else if (Mode == 1 && _dev is not null && _devDest is not null)
        {
            var (nodes, links) = _dev.Snapshot();
            sb.AppendLine($"Device path to {_devDest}");
            foreach (var n in nodes.OrderBy(n => n.Depth))
            {
                sb.AppendLine($"[{n.Depth}] {n.Name} {n.MgmtIp} - {n.StatusText}");
                if (n.Route is { } r) sb.AppendLine($"     route {r.Text}: {string.Join(", ", r.Paths.Select(p => $"{p.Address} via {p.Interface}"))}");
                foreach (var (name, h) in n.Interfaces)
                    sb.AppendLine($"     {name}: {h.Status}/{h.Protocol}, in {Pct(h.InUtil)} out {Pct(h.OutUtil)} of {h.SpeedText}, {h.InputErrors} in-errors ({h.Crc} CRC), {h.OutputDrops} out-drops");
            }
            foreach (var l in links) sb.AppendLine($"     {l.From} {l.Egress} → {l.To} {l.Ingress}{(l.Layer2 ? " (L2)" : "")}");
            AppendFindings(sb, DevicePathTracer.Findings(nodes, links, _devDest));
        }
        else if (Mode == 2 && _mcTarget is { } t)
        {
            sb.AppendLine($"Multicast ({t.Source}, {t.Group})");
            if (_mc is not null)
            {
                foreach (var h in _mc.Hops)
                    sb.AppendLine($"  {h.Name} {h.MgmtIp}: {h.Status}; RPF {h.Rpf?.Interface}→{h.Rpf?.Neighbor}; (S,G) {(h.SG is null ? "none" : $"in {h.SG.IncomingInterface} out {string.Join(",", h.SG.Outgoing)}")}; {(h.Counters is { } c ? $"{c.Pps} pps" : "")}");
                AppendFindings(sb, MulticastTracer.Findings(_mc.Hops, t.Source, t.Group));
            }
            else if (_mtrace is not null)
                foreach (var h in _mtrace) sb.AppendLine($"  -{h.Index} {h.Address} {h.Name} ==> {h.OutAddress} {h.Protocol} [{h.Prefix}] {h.Note}");
        }
        else { StatusText.Text = "Nothing to copy yet."; return; }
        ToolUi.Copy(sb.ToString());
        StatusText.Text = "Report copied to the clipboard.";
    }

    private static string F(double v) => double.IsNaN(v) ? $"{"-",8}" : $"{v,8:0.0}";

    private static void AppendFindings(StringBuilder sb, IReadOnlyList<TraceFinding> findings)
    {
        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var f in findings) sb.AppendLine($"  [{f.Severity}] {f.Title} - {f.Detail}");
    }

    private async void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_trace is null) { StatusText.Text = "Start a trace first."; return; }
        var s = _trace.Snapshot();
        var sb = new StringBuilder("hop,address,name,owner,sent,received,loss_pct,last_ms,avg_ms,best_ms,worst_ms,stdev_ms,jitter_ms\n");
        foreach (var h in s.Hops)
            sb.AppendLine(ToolUi.Csv(h.Hop.ToString(), h.Address ?? "", h.Name, h.Owner?.Text ?? "", h.Sent.ToString(), h.Received.ToString(), $"{h.LossPercent:0.##}",
                N(h.Last), N(h.Mean), N(h.Best), N(h.Worst), N(h.StDev), N(h.Jitter)));
        var path = await ToolUi.SaveTextAsync($"trace-{s.Target}-{DateTime.Now:yyyyMMdd-HHmm}", sb.ToString(), ".csv", "CSV");
        if (path is not null) StatusText.Text = $"Saved {path}";
    }

    private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
