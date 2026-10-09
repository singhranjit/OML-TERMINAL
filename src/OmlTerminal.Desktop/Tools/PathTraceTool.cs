using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.PathTrace;
using OmlTerminal.Core.Topology;
using OmlTerminal.Desktop.Controls;
using Path = Avalonia.Controls.Shapes.Path;

namespace OmlTerminal.Desktop.Tools;

/// <summary>One hop in the live table - the same object for a hop number for the whole trace, updated in place.</summary>
public sealed class HopRow : LiveRow
{
    public HopView Hop { get; private set; } = null!;
    public double Scale { get; private set; }
    public bool RateLimited { get; private set; }
    public double EffectiveLoss { get; private set; }

    public void Update(HopView hop, double scale, bool rateLimited, double effectiveLoss)
    {
        (Hop, Scale, RateLimited, EffectiveLoss) = (hop, scale, rateLimited, effectiveLoss);
        Touch();
    }

    public string HopText => Hop.Hop.ToString();
    public string Title => Hop.Silent ? "no reply" : Hop.Label + (Hop.Addresses.Count > 1 ? $"  (+{Hop.Addresses.Count - 1} more)" : "");
    public string Subtitle => Hop.Silent ? "this hop doesn't answer traceroute"
        : string.Join("  ·  ", new[] { Hop.Address, Hop.Name.Length > 0 && Hop.Name != Hop.Label ? Hop.Name : null, Hop.Owner?.Text }.Where(s => !string.IsNullOrEmpty(s)));
    public string LossText => Hop.Sent == 0 ? "" : $"{Hop.LossPercent:0.#}%" + (RateLimited ? "*" : "");
    public IBrush LossBrush => EffectiveLoss >= 5 ? Ui.Rose : EffectiveLoss >= 1 ? Ui.Amber : RateLimited ? Ui.Sky : Ui.Mint;
    private static string Ms(double v) => double.IsNaN(v) ? "" : v >= 100 ? $"{v:0}" : $"{v:0.#}";
    public string LastText => Ms(Hop.Last);
    public string AvgText => Ms(Hop.Mean);
    public string BestText => Ms(Hop.Best);
    public string WorstText => Ms(Hop.Worst);
    public string JitterText => Ms(Hop.Jitter);
    public IBrush BarBrush => Hop.Mean >= 150 ? Ui.Rose : Hop.Mean >= 60 ? Ui.Amber : Ui.Sky;
}

/// <summary>Best-to-worst bar with the average marked - the RANGE column.</summary>
internal sealed class RangeBar : Control
{
    public HopRow? Row { get; set; }
    private static readonly IBrush Mark = Brushes.White;

    public override void Render(DrawingContext ctx)
    {
        if (Row is not { } r || double.IsNaN(r.Hop.Best)) return;
        double y = Bounds.Height / 2 - 4;
        double x = r.Hop.Best / r.Scale, w = Math.Max(4, (r.Hop.Worst - r.Hop.Best) / r.Scale);
        ctx.FillRectangle(r.BarBrush, new Rect(x + 4, y, w, 8), 3);
        if (!double.IsNaN(r.Hop.Mean)) ctx.FillRectangle(Mark, new Rect(4 + Math.Max(0, r.Hop.Mean / r.Scale - 1.5), y - 2, 3, 12));
    }
}

public sealed class PathTraceTool : UserControl, IToolView
{
    private const double NodeW = 168, NodeH = 86, LanePitch = 112, LeftPad = 18, TopPad = 22;
    private double ColPitch => _mode == 1 ? 258 : 240; // device mode carries interface labels between columns

    private sealed record DNode(string Key, int Col, int Lane, string Badge, string Title, string Line2, string Line3, IBrush Accent, bool Dashed, string Tip, int? Hop);
    private sealed record DEdge(string From, string To, string Mid, string FromLabel, string ToLabel, IBrush Accent, bool Dashed);
    private sealed record NodeRow(string Badge, string Title, string Address, string Status, string Body, string CommandsText, IBrush Brush);

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
    private int _mode;

    // controls
    private readonly ToggleButton[] _modeButtons;
    private readonly TextBlock _status = Ui.Text("", 12.5, color: Ui.Muted);
    private readonly Control _pcControls, _devControls, _mcControls;
    private readonly TextBox _target = Ui.Input("", "8.8.8.8 / server.example.com", mono: true);
    private readonly NumericUpDown _interval = Ui.Number(1, 0.2, 60, 0.5), _maxHops = Ui.Number(30, 1, 64), _size = Ui.Number(32, 0, 65000);
    private readonly ComboBox _startDevice = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, _mcStart = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _devDestBox = Ui.Input("", "10.10.20.57", mono: true);
    private readonly CheckBox _ecmp = Ui.Check("All equal-cost paths", true), _l2 = Ui.Check("Layer 2 to the port", true), _stats = Ui.Check("Interface health", true);
    private readonly TextBox _mcSource = Ui.Input("", "10.10.50.40", mono: true), _mcGroup = Ui.Input("", "239.1.1.10", mono: true);
    private readonly ComboBox _mcMethod = Ui.Combo(new[] { "RPF walk (every hop's state)", "Router's mtrace" });
    private readonly Button[] _startButtons, _stopButtons;
    private readonly Canvas _canvas = new() { Height = 150 };
    private readonly TextBlock _empty = Ui.Text("", 13, color: Ui.Muted);
    private readonly DataGrid _hopTable;
    private readonly ObservableCollection<HopRow> _hopRows = new();
    private readonly ItemsControl _nodeList = new(), _findings = new();
    private readonly Control _pcDetails, _nodeDetails, _timelineCard;
    private readonly LatencyChart _timeline = new() { ShowAxis = true, Height = 170 };
    private readonly TextBlock _timelineTitle = Ui.Section("Latency over time");
    private readonly List<Rect> _labels = [];

    public PathTraceTool(ToolContext ctx)
    {
        _ctx = ctx;
        _identifier = new HopIdentifier(() => _ctx.Sessions(), _ctx.Settings.BackupDirectory);
        foreach (var box in new[] { _startDevice, _mcStart })
            box.ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display });

        _modeButtons = new[] { "From this computer", "WireWalk", "Multicast" }.Select((t, i) =>
        {
            var b = new ToggleButton { Content = t, IsChecked = i == 0, Padding = new Thickness(14, 5) };
            if (i == 1) ToolTip.SetTip(b, "Hop by hop through your own routers and switches, down to the host's switch port");
            b.Click += (_, _) => SetMode(i);
            return b;
        }).ToArray();

        _startButtons = new Button[3];
        _stopButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            int mode = i;
            _startButtons[i] = Ui.Button(i == 0 ? "Start" : "Trace", () => _ = (mode switch { 0 => PcStartAsync(), 1 => DevStartAsync(), _ => McStartAsync() }), accent: true);
            _stopButtons[i] = Ui.Button("Stop", Stop);
            _stopButtons[i].IsEnabled = false;
        }
        _interval.FormatString = "0.#";
        _target.KeyDown += (_, e) => { if (e.Key == Key.Enter && _startButtons[0].IsEnabled) { e.Handled = true; _ = PcStartAsync(); } };
        _devDestBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && _startButtons[1].IsEnabled) { e.Handled = true; _ = DevStartAsync(); } };

        _pcControls = Ui.Columns("*,110,100,100,Auto,Auto",
            Ui.Field("Destination", _target), Ui.Field("Every (s)", _interval), Ui.Field("Max hops", _maxHops), Ui.Field("Size (bytes)", _size), _startButtons[0], _stopButtons[0]);
        _devControls = Ui.Stack(8,
            Ui.Columns("260,*,Auto,Auto", Ui.Field("Start at (the source's first router)", _startDevice), Ui.Field("Destination IP", _devDestBox), _startButtons[1], _stopButtons[1]),
            Ui.Row(_ecmp, _l2, _stats));
        _mcControls = Ui.Columns("240,*,*,220,Auto,Auto",
            Ui.Field("Receiver's router", _mcStart), Ui.Field("Source", _mcSource), Ui.Field("Group", _mcGroup), Ui.Field("Method", _mcMethod), _startButtons[2], _stopButtons[2]);
        _devControls.IsVisible = _mcControls.IsVisible = false;

        // Path diagram
        _empty.Margin = new Thickness(20, 24);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        var pathCard = Ui.Card(new Panel
        {
            Children =
            {
                new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330 },
                _empty,
            },
        });
        pathCard.Padding = new Thickness(8);

        // Hop table (from this computer)
        _hopTable = Ui.Table(
            Ui.Col<HopRow>("Hop", r => r.HopText, 40, sortKey: r => r.Hop.Hop),
            Ui.Custom<HopRow, StackPanel>("Address · name · owner", 0,
                () => new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Children = { new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, new TextBlock { FontSize = 11, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis } } },
                (p, r) => { ((TextBlock)p.Children[0]).Text = r.Title; ((TextBlock)p.Children[1]).Text = r.Subtitle; }),
            Ui.Col<HopRow>("Loss", r => r.LossText, 72, r => r.LossBrush, mono: true),
            Ui.Col<HopRow>("Last", r => r.LastText, 60, mono: true),
            Ui.Col<HopRow>("Avg", r => r.AvgText, 60, mono: true),
            Ui.Col<HopRow>("Best", r => r.BestText, 60, mono: true),
            Ui.Col<HopRow>("Worst", r => r.WorstText, 64, mono: true),
            Ui.Col<HopRow>("Jitter", r => r.JitterText, 64, mono: true),
            Ui.Custom<HopRow, RangeBar>("Range", 96, () => new RangeBar(), (bar, r) => { bar.Row = r; bar.InvalidateVisual(); }));
        _hopTable.RowHeight = 40;
        _hopTable.Columns[1].MinWidth = 130;
        _hopTable.ItemsSource = _hopRows;
        _hopTable.SelectionMode = DataGridSelectionMode.Single;
        _hopTable.SelectionChanged += (_, _) =>
        {
            if (_updatingList || _hopTable.SelectedItem is not HopRow r) return;
            _selectedHop = r.Hop.Hop;
            RenderTimeline();
        };
        _pcDetails = _hopTable;

        _nodeList.ItemTemplate = new FuncDataTemplate<NodeRow>((n, _) => n is null ? new Panel() : Ui.Card(Ui.Stack(4,
            new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = n.Status, Foreground = n.Brush, FontSize = 12, [DockPanel.DockProperty] = Dock.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 },
                    Ui.Row(Badge(n.Badge, n.Brush), new TextBlock { Text = n.Title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = n.Address, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Muted }),
                },
            },
            new SelectableTextBlock { Text = n.Body, FontFamily = Ui.Mono, FontSize = 11.5, IsVisible = n.Body.Length > 0 },
            new TextBlock { Text = n.CommandsText, FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = n.CommandsText.Length > 0 })) is var c ? c : null!);
        _nodeDetails = new ScrollViewer { Content = _nodeList, IsVisible = false };

        _findings.ItemTemplate = new FuncDataTemplate<TraceFinding>((f, _) =>
        {
            if (f is null) return new Panel();
            var brush = f.Severity switch { InsightSeverity.Problem => Ui.Rose, InsightSeverity.Warning => Ui.Amber, _ => Ui.Sky };
            var b = new Border
            {
                BorderBrush = brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8),
                Background = Ui.CardBack, Cursor = f.Hop is null ? null : new Cursor(StandardCursorType.Hand),
                Child = Ui.Stack(2, new TextBlock { Text = f.Title, FontWeight = FontWeight.SemiBold, Foreground = brush, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = f.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap }),
            };
            b.PointerPressed += (_, _) => { if (f.Hop is { } hop && _mode == 0) { _selectedHop = hop; _dirty = true; } };
            return b;
        });
        _timelineCard = Ui.Card(Ui.Stack(6, _timelineTitle, _timeline));
        var right = new DockPanel
        {
            Children =
            {
                new DockPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { _timelineCard } },
                Ui.Card(new DockPanel { Children = { new TextBlock { Text = "VERDICT", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, Margin = new Thickness(0, 0, 0, 8), [DockPanel.DockProperty] = Dock.Top },
                                                     new ScrollViewer { Content = _findings } } }),
            },
        };
        _timelineCard.Margin = new Thickness(0, 12, 0, 0);

        var lower = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,360"), ColumnSpacing = 14 };
        var leftPanel = new Panel { Children = { _pcDetails, _nodeDetails } };
        lower.Children.Add(leftPanel);
        Grid.SetColumn(right, 1);
        lower.Children.Add(right);

        var actions = Ui.Row(Ui.Button("Copy report", CopyReport), Ui.Button("Save CSV...", () => _ = SaveCsvAsync()));
        Content = Ui.Page("Visual Trace",
            "See the path, not just a list of hops: a live traceroute drawn hop by hop with loss, latency and jitter, a plain-English verdict on where the problem really is, the hop-by-hop path through your own routers and switches, and multicast trees.",
            lower,
            new DockPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Right, Children = { actions } },
                                         Ui.Row([.. _modeButtons, _status]) } },
            _pcControls, _devControls, _mcControls, pathCard);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.MaxWidth = 560;
        SetMode(0);

        _refresh.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            try { Render(); }
            catch (Exception ex) { App.Report(ex); } // keep refreshing; one bad frame mustn't freeze the view
        };
        AttachedToVisualTree += (_, _) =>
        {
            var sessions = _ctx.SshSessions();
            ToolUi.FillSessions(_startDevice, sessions, selectFirst: true);
            ToolUi.FillSessions(_mcStart, sessions, selectFirst: true);
            _refresh.Start();
        };
        DetachedFromVisualTree += (_, _) => _refresh.Stop();
    }

    /// <summary>Prefills the destination (e.g. "trace this host" from another tool).</summary>
    public void SetTarget(string host) { SetMode(0); _target.Text = host; }

    public void Shutdown()
    {
        _cts?.Cancel();
        _refresh.Stop();
    }

    private void SetMode(int m)
    {
        _mode = m;
        for (int i = 0; i < 3; i++) _modeButtons[i].IsChecked = i == m;
        _pcControls.IsVisible = m == 0;
        _devControls.IsVisible = m == 1;
        _mcControls.IsVisible = m == 2;
        _pcDetails.IsVisible = m == 0;
        _nodeDetails.IsVisible = m != 0;
        _timelineCard.IsVisible = m == 0;
        _empty.Text = m switch
        {
            0 => "Enter a destination and press Start. Each hop appears as it answers; colours show where loss and delay really are - green clean, amber suspect, red the problem.",
            1 => "WireWalk: pick the router nearest the source (a saved SSH session) and the destination. It logs in hop by hop - read-only show commands only - following the routing table, CDP/LLDP and finally ARP and MAC tables to the destination's switch port.",
            _ => "Pick the router nearest the receivers, the source and the group. RPF walk checks every router's RPF, (S,G) state, counters and PIM neighbors back to the source; mtrace runs the router's own multicast traceroute.",
        };
        _dirty = true;
    }

    // ======================= From this computer =======================

    private async Task PcStartAsync()
    {
        var text = (_target.Text ?? "").Trim();
        if (text.Length == 0) { _status.Text = "Enter a destination."; return; }
        IPAddress target;
        if (!IPAddress.TryParse(text, out target!))
        {
            _status.Text = $"Resolving {text}…";
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(text);
                target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.First();
            }
            catch (Exception ex) { _status.Text = $"Can't resolve {text}: {ex.Message}"; return; }
        }
        Stop();
        _cts = new CancellationTokenSource();
        _trace = new ContinuousTrace(target, new TraceOptions
        {
            MaxHops = Ui.IntValue(_maxHops, 30),
            PacketSize = Ui.IntValue(_size, 32),
            Interval = TimeSpan.FromSeconds((double)(_interval.Value ?? 1)),
            TimeoutMs = 2000,
        }, identify: _identifier.IdentifyAsync);
        _trace.Updated += () => _dirty = true;
        _selectedHop = 0;
        _hopRows.Clear();
        SetRunning(true);
        _status.Text = $"Tracing {text}{(text == target.ToString() ? "" : $" ({target})")}…";
        var trace = _trace;
        try { await Task.Run(() => trace.RunAsync(_cts.Token)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _status.Text = ex.Message; }
        finally { if (_trace == trace) SetRunning(false); }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetRunning(false);
    }

    private void SetRunning(bool running)
    {
        foreach (var b in _startButtons) b.IsEnabled = !running;
        foreach (var b in _stopButtons) b.IsEnabled = running;
        _dirty = true;
    }

    private void RenderPc()
    {
        if (_trace is null) { ClearAll(); return; }
        var s = _trace.Snapshot();
        var findings = PathDiagnosis.Analyze(s);
        var hops = s.Hops;
        _status.Text = $"{s.Target} · round {s.Rounds}" + (s.Reached ? $" · {hops.Count} hops" : " · destination not reached yet") + (_cts is null ? " · stopped" : "");

        // Effective loss: what a hop passes on. A hop can't lose more than the hops after it see.
        var eff = new double[hops.Count];
        double floor = double.MaxValue;
        for (int i = hops.Count - 1; i >= 0; i--)
        {
            if (!hops[i].Silent) floor = Math.Min(floor, hops[i].LossPercent);
            eff[i] = hops[i].Silent ? 0 : Math.Min(hops[i].LossPercent, floor);
        }
        var jumpHop = findings.FirstOrDefault(f => f.Title.StartsWith("Latency jumps") && f.Severity != InsightSeverity.Info)?.Hop;

        var nodes = new List<DNode> { new("pc", 0, 0, "PC", "This computer", LocalAddressFor(_trace.Target), "", Ui.Sky, false, "Where the probes start", null) };
        var edges = new List<DEdge>();
        string prev = "pc";
        double prevMean = 0;
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            bool limited = !h.Silent && h.LossPercent >= 5 && eff[i] < h.LossPercent / 2;
            IBrush accent = h.Silent ? Ui.Muted : eff[i] >= 5 ? Ui.Rose : eff[i] >= 1 || jumpHop == h.Hop ? Ui.Amber : Ui.Mint;
            string badge = h.Silent ? "?" : h.IsDestination ? "DST" : h.Owner?.Kind == HopOwnerKind.Device ? "SW" : "RTR";
            string line3 = h.Silent ? "* * *" : $"{(double.IsNaN(h.Mean) ? "-" : h.Mean.ToString("0.#"))} ms · {h.LossPercent:0.#}% loss{(limited ? " (ICMP limit)" : "")}";
            var tip = h.Silent ? $"Hop {h.Hop}: no reply" :
                $"Hop {h.Hop}: {h.Label}\n{string.Join(", ", h.Addresses)}{(h.Name.Length > 0 ? $"\n{h.Name}" : "")}{(h.Owner is null ? "" : $"\n{h.Owner.Text}")}\n" +
                $"loss {h.LossPercent:0.#}% ({h.Sent - h.Received}/{h.Sent}) · last {h.Last:0.#} · avg {h.Mean:0.#} · best {h.Best:0.#} · worst {h.Worst:0.#} · jitter {h.Jitter:0.#} ms";
            var key = $"h{h.Hop}";
            nodes.Add(new DNode(key, h.Hop, 0, badge, h.Silent ? $"hop {h.Hop}" : Shorten(h.Label, 22), h.Silent ? "no reply" : h.Address ?? "", line3, accent, h.Silent, tip, h.Hop));
            double delta = h.Silent || double.IsNaN(h.Mean) ? double.NaN : h.Mean - prevMean;
            edges.Add(new DEdge(prev, key, double.IsNaN(delta) || delta < 1 ? "" : $"+{delta:0} ms", "", "", h.Silent ? Ui.CardBorder : accent, h.Silent));
            prev = key;
            if (!h.Silent && !double.IsNaN(h.Mean)) prevMean = h.Mean;
        }
        if (!s.Reached && hops.Count > 0)
        {
            nodes.Add(new DNode("dest", hops.Count + 1, 0, "DST", s.Target, "not reached", "", Ui.Rose, true, "The destination hasn't answered", null));
            edges.Add(new DEdge(prev, "dest", "?", "", "", Ui.Rose, true));
        }
        DrawDiagram(nodes, edges);

        // Hop table, updated in place
        double maxWorst = hops.Where(h => !double.IsNaN(h.Worst)).Select(h => h.Worst).DefaultIfEmpty(10).Max();
        double scale = Math.Max(maxWorst, 1) / 88.0;
        _updatingList = true;
        while (_hopRows.Count > hops.Count) _hopRows.RemoveAt(_hopRows.Count - 1);
        for (int i = 0; i < hops.Count; i++)
        {
            // Fill a new row before adding it: the table renders it the moment it's added.
            var row = i < _hopRows.Count ? _hopRows[i] : new HopRow();
            row.Update(hops[i], scale, !hops[i].Silent && hops[i].LossPercent >= 5 && eff[i] < hops[i].LossPercent / 2, eff[i]);
            if (i >= _hopRows.Count) _hopRows.Add(row);
        }
        if (_selectedHop == 0 && hops.Count > 0) _selectedHop = hops[^1].Hop;
        var sel = _hopRows.FirstOrDefault(r => r.Hop.Hop == _selectedHop);
        if (_hopTable.SelectedItem != sel) _hopTable.SelectedItem = sel;
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

    private void RenderTimeline()
    {
        if (_trace is null || _selectedHop == 0) { _timeline.SetSamples([]); return; }
        var samples = _trace.SamplesFor(_selectedHop);
        var hop = _trace.Snapshot().Hops.FirstOrDefault(x => x.Hop == _selectedHop);
        _timelineTitle.Text = hop is null ? "LATENCY OVER TIME" : $"HOP {hop.Hop} · {hop.Label.ToUpperInvariant()} · LAST {samples.Count} PROBES";
        _timeline.Mean = hop?.Mean ?? double.NaN;
        _timeline.SetSamples(samples.Select(x => (x.At, x.RttMs)).ToList(), Ui.Sky);
    }

    // ======================= WireWalk (through my devices) =======================

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

    private async Task DevStartAsync()
    {
        if (_startDevice.SelectedItem is not SessionProfile start) { _status.Text = "Pick the starting device (a saved SSH session)."; return; }
        if (!IPAddress.TryParse((_devDestBox.Text ?? "").Trim(), out var dest)) { _status.Text = "Enter the destination as an IP address."; return; }
        Stop();
        var seed = _ctx.SshSessions().FirstOrDefault(s => s.Id == start.Id) ?? start;
        _cts = new CancellationTokenSource();
        _dev = new DevicePathTracer(ProfileFrom(seed), KnownDevice);
        _devDest = dest;
        _dev.Changed += () => _dirty = true;
        SetRunning(true);
        _status.Text = $"Tracing {start.Name} → {dest} hop by hop…";
        var options = new DevicePathOptions { FollowEcmp = _ecmp.IsChecked == true, LayerTwo = _l2.IsChecked == true, InterfaceStats = _stats.IsChecked == true };
        var tracer = _dev;
        var ct = _cts.Token;
        try
        {
            await Task.Run(() => tracer.TraceAsync(seed, dest, options, ct));
            _status.Text = $"{start.Name} → {dest}: done.";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex) { _status.Text = ex.Message; }
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
            var (accent, badge) = NodeLook(n);
            var line3 = n.Status switch
            {
                PathNodeStatus.Working => "working…",
                PathNodeStatus.Pending => "queued",
                _ => n.Route is { } r && n.Kind != PathNodeKind.Host ? Shorten(r.Text, 30) : Shorten(n.StatusText, 30),
            };
            nodes.Add(new DNode(n.Key, n.Depth, lane, badge, Shorten(n.Name, 22), n.MgmtIp ?? "", line3, accent, n.Status is PathNodeStatus.NotReached or PathNodeStatus.Pending,
                $"{n.Name}\n{n.StatusText}{(n.Route is null ? "" : $"\nroute: {n.Route.Text}")}", null));
        }
        var byKey = pathNodes.ToDictionary(n => n.Key);
        var edges = links.Select(l =>
        {
            var from = byKey.GetValueOrDefault(l.From);
            var to = byKey.GetValueOrDefault(l.To);
            var egress = from?.Interfaces.GetValueOrDefault(l.Egress);
            var ingress = to?.Interfaces.GetValueOrDefault(l.Ingress);
            IBrush accent = Ui.Mint;
            foreach (var h in new[] { egress, ingress })
            {
                if (h is null) continue;
                double util = Math.Max(h.InUtil, h.OutUtil);
                if (!h.Up || util >= 95) accent = Ui.Rose;
                else if ((util >= 80 || h.InputErrors >= 100 || h.Crc >= 50 || h.OutputDrops >= 1000) && accent != Ui.Rose) accent = Ui.Amber;
            }
            var fromLabel = NeighborParser.ShortInterface(l.Egress);
            if (egress is not null && !double.IsNaN(egress.OutUtil)) fromLabel += $" · {egress.OutUtil:0}%";
            if (to?.Status is PathNodeStatus.Failed or PathNodeStatus.NotReached) accent = Ui.Amber;
            return new DEdge(l.From, l.To, "", fromLabel, NeighborParser.ShortInterface(l.Ingress), accent, l.Layer2);
        }).ToList();
        DrawDiagram(nodes, edges);

        _nodeList.ItemsSource = pathNodes.OrderBy(n => n.Depth).Select(n =>
        {
            var (accent, badge) = NodeLook(n);
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
            return new NodeRow(badge, n.Name, n.MgmtIp ?? "", n.StatusText, body.ToString().TrimEnd(),
                n.Commands.Count == 0 ? "" : "ran: " + string.Join(" · ", n.Commands.Distinct()), accent);
        }).ToList();
        SetFindings(DevicePathTracer.Findings(pathNodes, links, _devDest));
        if (_cts is not null) _status.Text = $"{pathNodes.Count(n => n.Status is PathNodeStatus.Done or PathNodeStatus.Destination)} device(s) traced so far…";
    }

    private static string Pct(double v) => double.IsNaN(v) ? "-" : $"{v:0}%";

    private static (IBrush Accent, string Badge) NodeLook(PathNode n)
    {
        string badge = n.Kind switch { PathNodeKind.Host => "HOST", PathNodeKind.Switch => "SW", _ => "RTR" };
        bool sick = n.Interfaces.Values.Any(h => !h.Up || Math.Max(h.InUtil, h.OutUtil) >= 80 || h.InputErrors >= 100 || h.OutputDrops >= 1000);
        IBrush accent = n.Status switch
        {
            PathNodeStatus.Failed or PathNodeStatus.NoRoute => Ui.Rose,
            PathNodeStatus.NotReached => n.Kind == PathNodeKind.Host ? Ui.Rose : Ui.Amber,
            PathNodeStatus.Working => Ui.Sky,
            PathNodeStatus.Pending => Ui.Muted,
            _ => sick ? Ui.Amber : Ui.Mint,
        };
        return (accent, badge);
    }

    // ======================= Multicast =======================

    private async Task McStartAsync()
    {
        if (_mcStart.SelectedItem is not SessionProfile start) { _status.Text = "Pick the router nearest the receivers."; return; }
        if (!IPAddress.TryParse((_mcSource.Text ?? "").Trim(), out var source)) { _status.Text = "Enter the multicast source's IP address."; return; }
        if (!IPAddress.TryParse((_mcGroup.Text ?? "").Trim(), out var group) || group.GetAddressBytes()[0] is < 224 or > 239)
        { _status.Text = "Enter a multicast group (224.0.0.0 - 239.255.255.255)."; return; }
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
            if (_mcMethod.SelectedIndex == 1)
            {
                _status.Text = $"Running mtrace on {start.Name}…";
                _mtraceRouter = start.Name;
                var text = await Task.Run(async () =>
                {
                    using var cli = await SshDeviceCli.OpenAsync(seed, ct);
                    return await ((SshDeviceCli)cli).RunSlowAsync($"mtrace {source} {seed.Host} {group}", TimeSpan.FromSeconds(8), ct);
                }, ct);
                _mtrace = MulticastParsers.ParseMtrace(text);
                _status.Text = _mtrace.Count == 0 ? $"{start.Name} didn't return an mtrace (unsupported command?) - try the RPF walk." : $"mtrace from {start.Name}: {_mtrace.Count} hops.";
            }
            else
            {
                var tracer = _mc = new MulticastTracer(ProfileFrom(seed), KnownDevice);
                tracer.Changed += () => _dirty = true;
                _status.Text = $"Walking the tree from {start.Name} back to {source}…";
                await Task.Run(() => tracer.TraceAsync(seed, source, group, 16, ct));
                _status.Text = $"({source}, {group}): {tracer.Hops.Count} hop(s) checked.";
            }
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex) { _status.Text = ex.Message; }
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
            IBrush accent = isSource ? Ui.Sky : h.Failed || noState || h.RpfNeighborIsPim == false ? Ui.Rose
                : h.Counters is { Pps: 0 } || h.Counters is { } rc && (rc.RpfFailed >= 1000 || rc.RpfFailed * 100 >= Math.Max(1, rc.Forwarded)) || h.SG is { Outgoing.Count: 0 } ? Ui.Amber : Ui.Mint;
            string line3 = isSource ? $"source of {target.Group}" : h.Counters is { } c ? $"{c.Pps} pps · {c.Kbps} kbps" : Shorten(h.Status, 30);
            string tip = isSource ? "Multicast source" :
                $"{h.Name}\n{h.Status}\nRPF: {h.Rpf?.Interface ?? "-"} → {h.Rpf?.Neighbor ?? "-"} ({h.Rpf?.Route})\n(S,G): {(h.SG is null ? "none" : $"in {h.SG.IncomingInterface}, out {string.Join(", ", h.SG.Outgoing)}, flags {h.SG.Flags}")}";
            nodes.Add(new DNode(h.Key, col, 0, isSource ? "SRC" : "RTR", Shorten(h.Name, 22), h.MgmtIp ?? "", line3, accent, false, tip, null));
            if (i > 0)
            {
                // Traffic flows from hop i (upstream) to hop i-1 (downstream), arriving on the downstream router's RPF interface.
                var down = hops[i - 1];
                edges.Add(new DEdge(h.Key, down.Key, "", "", NeighborParser.ShortInterface(down.Rpf?.Interface ?? ""), down.Failed ? Ui.Rose : accent == Ui.Sky ? Ui.Mint : accent, false));
            }
        }
        if (n > 0 && hops[0].IgmpInterfaces.Count > 0)
        {
            nodes.Add(new DNode("receivers", n, 0, "RCV", "Receivers", string.Join(", ", hops[0].IgmpInterfaces), $"joined {target.Group}", Ui.Mint, false, "IGMP members on the last-hop router", null));
            edges.Add(new DEdge(hops[0].Key, "receivers", "", NeighborParser.ShortInterface(hops[0].IgmpInterfaces[0]), "", Ui.Mint, true));
        }
        DrawDiagram(nodes, edges);
        _nodeList.ItemsSource = hops.Select(h => new NodeRow(
            h.Key.StartsWith("src:") ? "SRC" : "RTR", h.Name, h.MgmtIp ?? "", h.Status,
            h.Key.StartsWith("src:") ? "" : string.Join("\n", new[]
            {
                h.IgmpInterfaces.Count > 0 ? $"igmp    members on {string.Join(", ", h.IgmpInterfaces)}" : null,
                h.Rpf is { } r ? $"rpf     {r.Interface} → {(r.DirectlyConnected ? "directly connected" : r.Neighbor)}  ({r.Route}, {r.Type})" : "rpf     FAILED - no route to the source",
                h.SG is { } sg ? $"(S,G)   in {sg.IncomingInterface}  out {(sg.Outgoing.Count == 0 ? "Null" : string.Join(", ", sg.Outgoing))}  flags {sg.Flags}  up {sg.Uptime}" : "(S,G)   none",
                h.StarG is { } sgx ? $"(*,G)   in {sgx.IncomingInterface}  out {(sgx.Outgoing.Count == 0 ? "Null" : string.Join(", ", sgx.Outgoing))}  flags {sgx.Flags}" : null,
                h.Counters is { } c ? $"traffic {c.Pps} pps, {c.Kbps} kbps, {c.Forwarded:N0} forwarded, {c.RpfFailed:N0} RPF failures" : null,
                h.RpfNeighborIsPim is { } ok ? $"pim     RPF neighbor {(ok ? "is" : "is NOT")} a PIM neighbor" : null,
            }.Where(x => x is not null)),
            h.Commands.Count == 0 ? "" : "ran: " + string.Join(" · ", h.Commands),
            h.Key.StartsWith("src:") ? Ui.Sky : h.Failed ? Ui.Rose : Ui.Mint)).ToList();
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
            nodes.Add(new DNode($"m{i}", i, 0, i == 0 ? "SRC" : i == hops.Count - 1 ? "RCV" : "RTR", Shorten(title, 22), h.Address, Shorten(line3, 30),
                err ? Ui.Rose : Ui.Mint, false, $"{title} {h.Address}\n{h.Prefix}\n{h.Note}", null));
            if (i > 0) edges.Add(new DEdge($"m{i - 1}", $"m{i}", "", "", "", err ? Ui.Rose : Ui.Mint, false));
        }
        DrawDiagram(nodes, edges);
        _nodeList.ItemsSource = hops.Select(h => new NodeRow("RTR", h.Name.Length > 0 ? h.Name : h.Address, h.Address,
            h.Note.Length > 0 ? h.Note : "forwarding", $"hop -{h.Index}  {h.Address}{(h.OutAddress.Length > 0 ? $" ==> {h.OutAddress}" : "")}  {h.Protocol}  [{h.Prefix}]",
            "", MulticastParsers.IsMtraceError(h.Note) ? Ui.Rose : Ui.Mint)).ToList();
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
        switch (_mode)
        {
            case 0: RenderPc(); break;
            case 1: RenderDevices(); break;
            default: RenderMulticast(); break;
        }
    }

    private void ClearAll()
    {
        _canvas.Children.Clear();
        _canvas.Width = 0;
        _empty.IsVisible = true;
        _findings.ItemsSource = null;
        _nodeList.ItemsSource = null;
        if (_mode == 0) _hopRows.Clear();
        _timeline.SetSamples([]);
    }

    private void SetFindings(IReadOnlyList<TraceFinding> findings) => _findings.ItemsSource = findings;

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Border Badge(string text, IBrush accent) => new()
    {
        BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1),
        VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = accent },
    };

    private void DrawDiagram(IReadOnlyList<DNode> nodes, IReadOnlyList<DEdge> edges)
    {
        _canvas.Children.Clear();
        _labels.Clear();
        _empty.IsVisible = nodes.Count == 0;
        if (nodes.Count == 0) return;
        int maxCol = nodes.Max(n => n.Col), maxLane = nodes.Max(n => n.Lane);
        _canvas.Width = LeftPad * 2 + maxCol * ColPitch + NodeW;
        _canvas.Height = TopPad + (maxLane + 1) * LanePitch;
        var pos = nodes.ToDictionary(n => n.Key, n => new Point(LeftPad + n.Col * ColPitch, TopPad + n.Lane * LanePitch));

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
            double mx = (start.X + end.X) / 2;
            var geo = new PathGeometry
            {
                Figures = new PathFigures
                {
                    new PathFigure { StartPoint = start, IsClosed = false, Segments = new PathSegments { new BezierSegment { Point1 = new Point(mx, start.Y), Point2 = new Point(mx, end.Y), Point3 = end } } },
                },
            };
            var path = new Path { Data = geo, Stroke = e.Accent, StrokeThickness = 2.4, Opacity = 0.9 };
            if (e.Dashed) path.StrokeDashArray = new AvaloniaList<double> { 3, 2 };
            _canvas.Children.Add(path);
            _canvas.Children.Add(new Polygon { Fill = e.Accent, Points = [new Point(end.X, end.Y), new Point(end.X - 8, end.Y - 5), new Point(end.X - 8, end.Y + 5)] });
            if (e.FromLabel.Length > 0) AddLabel(e.FromLabel, start.X + 6, start.Y - 17, false);
            if (e.ToLabel.Length > 0) AddLabel(e.ToLabel, end.X - 10, end.Y + 2, true);
            if (e.Mid.Length > 0)
            {
                var pill = new Border
                {
                    Background = Ui.CardBack, BorderBrush = e.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1),
                    Child = new TextBlock { Text = e.Mid, FontSize = 11, Foreground = e.Accent, FontFamily = Ui.Mono },
                };
                pill.Measure(Size.Infinity);
                Canvas.SetLeft(pill, mx - pill.DesiredSize.Width / 2);
                Canvas.SetTop(pill, (start.Y + end.Y) / 2 + 6);
                _canvas.Children.Add(pill);
            }
        }

        foreach (var n in nodes)
        {
            var p = pos[n.Key];
            bool selected = n.Hop is { } hopNo && hopNo == _selectedHop && _mode == 0;
            var card = new Border
            {
                Width = NodeW, Height = NodeH, CornerRadius = new CornerRadius(12), Background = Ui.CardBack,
                BorderBrush = n.Accent, BorderThickness = new Thickness(selected ? 3 : n.Dashed ? 1 : 1.8), Padding = new Thickness(10, 7),
                Opacity = n.Dashed ? 0.75 : 1,
            };
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"), ColumnSpacing = 8 };
            grid.Children.Add(Badge(n.Badge, n.Accent));
            var text = Ui.Stack(1,
                new TextBlock { Text = n.Title, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = n.Line2, FontSize = 11.5, Foreground = Ui.Muted, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = n.Line3, FontSize = 11.5, Foreground = n.Accent, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            card.Child = grid;
            ToolTip.SetTip(card, n.Tip);
            if (n.Hop is { } hop)
            {
                card.Cursor = new Cursor(StandardCursorType.Hand);
                card.PointerPressed += (_, _) => { _selectedHop = hop; _dirty = true; };
                var badge = new TextBlock { Text = $"HOP {hop}", FontSize = 10, Foreground = Ui.Muted, FontWeight = FontWeight.SemiBold };
                Canvas.SetLeft(badge, p.X + 4);
                Canvas.SetTop(badge, p.Y - 16);
                _canvas.Children.Add(badge);
            }
            Canvas.SetLeft(card, p.X);
            Canvas.SetTop(card, p.Y);
            _canvas.Children.Add(card);
        }
    }

    /// <summary>Interface label at a link end. If it would overprint another label, it steps away from the line
    /// (egress labels upwards, ingress labels downwards) until it's clear.</summary>
    private void AddLabel(string text, double x, double y, bool alignRight)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, Foreground = Ui.Muted, FontFamily = Ui.Mono };
        t.Measure(Size.Infinity);
        var r = new Rect(alignRight ? x - t.DesiredSize.Width : x, y, t.DesiredSize.Width, 13);
        double step = alignRight ? 12 : -12;
        for (int i = 0; i < 6 && _labels.Any(p => p.Intersects(r)); i++) r = r.Translate(new Vector(0, step));
        _labels.Add(r);
        Canvas.SetLeft(t, r.X);
        Canvas.SetTop(t, r.Y);
        _canvas.Children.Add(t);
    }

    // ======================= export =======================

    private void CopyReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OML Terminal - Visual Trace · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (_mode == 0 && _trace is not null)
        {
            var s = _trace.Snapshot();
            sb.AppendLine($"Destination {s.Target} · {s.Rounds} rounds · {(s.Reached ? "reached" : "NOT reached")}");
            sb.AppendLine();
            sb.AppendLine($"{"Hop",-4}{"Address",-17}{"Name / owner",-40}{"Loss",7}{"Sent",6}{"Last",8}{"Avg",8}{"Best",8}{"Worst",8}{"Jitter",8}");
            foreach (var h in s.Hops)
                sb.AppendLine($"{h.Hop,-4}{h.Address ?? "*",-17}{Shorten(h.Silent ? "(no reply)" : $"{h.Name} {h.Owner?.Text}".Trim(), 39),-40}{h.LossPercent,6:0.#}%{h.Sent,6}{F(h.Last)}{F(h.Mean)}{F(h.Best)}{F(h.Worst)}{F(h.Jitter)}");
            AppendFindings(sb, PathDiagnosis.Analyze(s));
        }
        else if (_mode == 1 && _dev is not null && _devDest is not null)
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
        else if (_mode == 2 && _mcTarget is { } t)
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
        else { _status.Text = "Nothing to copy yet."; return; }
        ToolUi.Copy(sb.ToString());
        _status.Text = "Report copied to the clipboard.";
    }

    private static string F(double v) => double.IsNaN(v) ? $"{"-",8}" : $"{v,8:0.0}";

    private static void AppendFindings(StringBuilder sb, IReadOnlyList<TraceFinding> findings)
    {
        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var f in findings) sb.AppendLine($"  [{f.Severity}] {f.Title} - {f.Detail}");
    }

    private async Task SaveCsvAsync()
    {
        if (_trace is null) { _status.Text = "Start a trace first."; return; }
        var s = _trace.Snapshot();
        var sb = new StringBuilder("hop,address,name,owner,sent,received,loss_pct,last_ms,avg_ms,best_ms,worst_ms,stdev_ms,jitter_ms\n");
        foreach (var h in s.Hops)
            sb.AppendLine(ToolUi.Csv(h.Hop.ToString(), h.Address ?? "", h.Name, h.Owner?.Text ?? "", h.Sent.ToString(), h.Received.ToString(), $"{h.LossPercent:0.##}",
                N(h.Last), N(h.Mean), N(h.Best), N(h.Worst), N(h.StDev), N(h.Jitter)));
        var path = await ToolUi.SaveTextAsync($"trace-{s.Target}-{DateTime.Now:yyyyMMdd-HHmm}", sb.ToString(), ".csv", "CSV");
        if (path is not null) _status.Text = $"Saved {path}";
    }

    private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
