using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.NetTools;
using OmlTerminal.Core.Topology;

namespace OmlTerminal.Desktop.Tools;

public sealed class TopologyTool : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly DispatcherTimer _redrawTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<string, (double X, double Y)> _manual = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _nodeViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(TopologyLink Link, Line Line, Border A, Border B)> _edgeViews = new();
    private Dictionary<string, (double X, double Y)> _pos = new(StringComparer.OrdinalIgnoreCase);
    private TopologyCrawler? _crawler;
    private TopologyGraph _view = new();
    private CancellationTokenSource? _cts;
    private SessionProfile? _seed;
    private volatile bool _dirty;
    private string? _selected, _dragKey;
    private Point _dragOffset;
    private double _zoom = 1;

    private readonly ComboBox _seedBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Pick a saved SSH session",
        ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
    private readonly NumericUpDown _depth = Ui.Number(2, 0, 6), _max = Ui.Number(50, 1, 500);
    private readonly TextBox _scope = Ui.Input("", "10.0.0.0/8 (blank = anywhere)", mono: true);
    private readonly CheckBox _endpoints = Ui.Check("Phones, APs, hosts too");
    private readonly Button _discover, _stop, _connect, _saveOne;
    private readonly TextBlock _status = Ui.Text("", 12.5, color: Ui.Muted);
    private readonly Canvas _canvas = new() { Width = 800, Height = 500, Background = Ui.Solid(0x0C0C0C) };
    private readonly LayoutTransformControl _zoomHost;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty = Ui.Text("Pick a device to start from and press Discover. It logs in over SSH, reads CDP/LLDP neighbors, and follows them hop by hop - read-only show commands only.", 13, color: Ui.Muted);
    private readonly TextBlock _nodeName = new() { Text = "Nothing selected", FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _nodeIp = new() { FontFamily = Ui.Mono, FontSize = 12.5 };
    private readonly TextBlock _nodePlatform = Ui.Text("", 12, color: Ui.Muted), _nodeStatus = Ui.Text("", 12);
    private readonly ItemsControl _links = new();

    public TopologyTool(ToolContext ctx)
    {
        _ctx = ctx;
        _discover = Ui.Button("Discover", () => _ = DiscoverAsync(), accent: true);
        _stop = Ui.Button("Stop", () => _cts?.Cancel());
        _stop.IsEnabled = false;
        _connect = Ui.Button("Connect", Connect);
        _saveOne = Ui.Button("Save as session", SaveOne, tip: "Add to your sessions under Discovered/");
        _connect.IsEnabled = _saveOne.IsEnabled = false;
        _links.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, FontFamily = Ui.Mono, FontSize = 12, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap });
        // A redraw rebuilds every node, which would yank one out from under the mouse mid-drag - wait for the drop.
        _redrawTimer.Tick += (_, _) => { if (_dirty && _dragKey is null) { _dirty = false; Redraw(); } };
        _canvas.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, _canvas)) Select(null); };

        _zoomHost = new LayoutTransformControl { Child = _canvas, LayoutTransform = new ScaleTransform(1, 1) };
        _scroll = new ScrollViewer { Content = _zoomHost, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _scroll.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            SetZoom(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
            e.Handled = true;
        };
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.MaxWidth = 440;
        _empty.TextAlignment = TextAlignment.Center;

        var mapCard = Ui.Card(new DockPanel { Children = {
            WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = {
                WithDock(Ui.Row(Ui.Button("−", () => SetZoom(_zoom / 1.25), tip: "Zoom out (Ctrl+wheel)"), Ui.Button("+", () => SetZoom(_zoom * 1.25), tip: "Zoom in"),
                    Ui.Button("Fit", Fit), Ui.Button("Re-layout", () => { _manual.Clear(); Redraw(); })), Dock.Right),
                Ui.Section("Map · drag devices to arrange") } }, Dock.Top),
            new Panel { Children = { _scroll, _empty } } } });
        var details = Ui.Card(new DockPanel { Children = {
            WithDock(Ui.Stack(6, Ui.Section("Device"), _nodeName, _nodeIp, _nodePlatform, _nodeStatus, Ui.Row(_connect, _saveOne), Ui.Section("Links")), Dock.Top),
            WithDock(Ui.Stack(6, Ui.Section("Export"), Ui.Row(Ui.Button("Save all as sessions", SaveAll), Ui.Button("Copy CSV", CopyCsv)),
                Ui.Row(Ui.Button("Save .dot...", () => _ = SaveDotAsync()), Ui.Button("Save PNG...", () => _ = SavePngAsync()))), Dock.Bottom),
            new ScrollViewer { Content = _links, Margin = new Thickness(0, 6) } } });
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,320"), ColumnSpacing = 14 };
        body.Children.Add(mapCard);
        Grid.SetColumn(details, 1);
        body.Children.Add(details);
        Content = Ui.Page("Topology Mapper", "Discover the network from one device over CDP/LLDP and draw a live, clickable map. Save what it finds as sessions.", body,
            Ui.Columns("260,90,110,*,Auto,Auto,Auto", Ui.Field("Start from", _seedBox), Ui.Field("Hops", _depth), Ui.Field("Max logins", _max),
                Ui.Field("Only log in to addresses in", _scope), _endpoints, _discover, _stop),
            _status);
        AttachedToVisualTree += (_, _) => ToolUi.FillSessions(_seedBox, _ctx.SshSessions());
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.2, 3);
        _zoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
    }

    private void Fit()
    {
        var vp = _scroll.Bounds.Size;
        if (vp.Width <= 0 || _canvas.Width <= 0) return;
        SetZoom(Math.Clamp(Math.Min(vp.Width / _canvas.Width, vp.Height / _canvas.Height), 0.2, 1.0));
        _scroll.Offset = new Vector(0, 0);
    }

    // ---------- crawl ----------

    private async Task DiscoverAsync()
    {
        if (_cts is not null) return;
        if (_seedBox.SelectedItem is not SessionProfile seed) { _status.Text = "Pick the device to start from."; return; }
        var scope = new List<Subnet>();
        foreach (var part in (_scope.Text ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Subnet.TryParse(part, out var net) || net.IsV6) { _status.Text = $"'{part}' isn't a valid IPv4 network - use CIDR like 10.0.0.0/8."; return; }
            scope.Add(net);
        }
        var options = new TopologyOptions
        {
            MaxDepth = Ui.IntValue(_depth, 2), MaxDevices = Ui.IntValue(_max, 50), Scope = scope, CrawlEndpoints = _endpoints.IsChecked == true,
        };
        _seed = seed;
        _crawler = new TopologyCrawler();
        _crawler.Changed += () => _dirty = true;
        _manual.Clear();
        _selected = null;
        ShowDetails();
        _cts = new CancellationTokenSource();
        _discover.IsEnabled = false;
        _stop.IsEnabled = true;
        _status.Text = $"Logging in to {seed.Name}…";
        _redrawTimer.Start();
        bool stopped = false;
        try
        {
            var crawler = _crawler;
            var ct = _cts.Token;
            await Task.Run(() => crawler.CrawlAsync(seed, ProfileFor, options, ct));
        }
        catch (OperationCanceledException) { stopped = true; }
        finally
        {
            _redrawTimer.Stop();
            _cts.Dispose();
            _cts = null;
            _discover.IsEnabled = true;
            _stop.IsEnabled = false;
        }
        Redraw();
        Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        _status.Text = Summary() + (stopped ? " Stopped." : " Done.");
    }

    /// <summary>A neighbor gets the starting session's credentials and jump host, pointed at its own address.</summary>
    private SessionProfile ProfileFor(string name, string ip)
    {
        var p = _seed!.Clone();
        p.Id = Guid.NewGuid();
        p.Name = name;
        p.Host = ip;
        p.Port = 22;
        p.Protocol = ProtocolKind.Ssh;
        return p;
    }

    private string Summary()
    {
        var nodes = _view.Nodes.Values;
        int mapped = nodes.Count(n => n.Status == NodeStatus.Crawled), failed = nodes.Count(n => n.Status == NodeStatus.Failed), busy = nodes.Count(n => n.Status == NodeStatus.Crawling);
        return $"{nodes.Count} device(s), {_view.Links.Count} link(s) · logged in to {mapped}" +
               (failed > 0 ? $" · {failed} login(s) failed" : "") + (busy > 0 ? $" · {busy} in progress" : "") + ".";
    }

    // ---------- drawing ----------

    private void Redraw()
    {
        if (_crawler is null) return;
        _view = _crawler.Graph.Snapshot();
        _pos = TopologyLayout.Layered(_view);
        foreach (var (k, p) in _manual) if (_pos.ContainsKey(k)) _pos[k] = p;
        _canvas.Children.Clear();
        _nodeViews.Clear();
        _edgeViews.Clear();
        var lineBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x38, 0xBD, 0xF8));
        foreach (var link in _view.Links)
        {
            if (!_pos.ContainsKey(link.A) || !_pos.ContainsKey(link.B)) continue;
            var line = new Line { Stroke = lineBrush, StrokeThickness = 1.6 };
            var a = PortLabel(link.APort);
            var b = PortLabel(link.BPort);
            _canvas.Children.Add(line);
            _canvas.Children.Add(a);
            _canvas.Children.Add(b);
            _edgeViews.Add((link, line, a, b));
        }
        foreach (var node in _view.Nodes.Values)
        {
            if (!_pos.TryGetValue(node.Key, out var p)) continue;
            var view = NodeView(node);
            Canvas.SetLeft(view, p.X);
            Canvas.SetTop(view, p.Y);
            _canvas.Children.Add(view);
            _nodeViews[node.Key] = view;
        }
        PositionEdges();
        SizeCanvas();
        _empty.IsVisible = _view.Nodes.Count == 0;
        if (_cts is not null) _status.Text = Summary();
        ShowDetails();
    }

    private void SizeCanvas()
    {
        _canvas.Width = Math.Max(800, _pos.Values.DefaultIfEmpty().Max(p => p.X) + TopologyLayout.NodeWidth + 60);
        _canvas.Height = Math.Max(500, _pos.Values.DefaultIfEmpty().Max(p => p.Y) + TopologyLayout.NodeHeight + 60);
    }

    private static Border PortLabel(string port)
    {
        var label = new Border
        {
            Background = Ui.CardBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1), IsHitTestVisible = false,
            Child = new TextBlock { Text = port, FontSize = 10.5, FontFamily = Ui.Mono, Foreground = Ui.Muted },
        };
        label.Measure(Size.Infinity);
        return label;
    }

    private static string Badge(NodeKind kind) => kind switch
    {
        NodeKind.Router => "RTR", NodeKind.Switch => "SW", NodeKind.Firewall => "FW", NodeKind.AccessPoint => "AP", NodeKind.Phone => "PH", NodeKind.Host => "HOST", _ => "?",
    };

    private static IBrush StatusBrush(TopologyNode n) => n.Status switch
    {
        _ when n.IsSeed && n.Status != NodeStatus.Failed => Ui.Orange,
        NodeStatus.Crawled => Ui.Mint,
        NodeStatus.Crawling => Ui.Sky,
        NodeStatus.Failed => Ui.Rose,
        _ => Ui.Muted,
    };

    private Border NodeView(TopologyNode n)
    {
        var accent = StatusBrush(n);
        var text = Ui.Stack(1,
            new TextBlock { Text = n.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = n.MgmtIp ?? "no address", FontSize = 11, Foreground = Ui.Muted, FontFamily = Ui.Mono },
            new TextBlock { Text = n.Platform.Length > 0 ? n.Platform : n.StatusText, FontSize = 10.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        text.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = Badge(n.Kind), FontSize = 9.5, FontWeight = FontWeight.Bold, Foreground = accent },
        };
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"), ColumnSpacing = 8 };
        grid.Children.Add(badge);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        bool selected = string.Equals(n.Key, _selected, StringComparison.OrdinalIgnoreCase);
        var border = new Border
        {
            Width = TopologyLayout.NodeWidth, Height = TopologyLayout.NodeHeight, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6),
            Background = selected ? Ui.Solid(0x1E2A33) : Ui.CardBack, BorderBrush = accent, BorderThickness = new Thickness(selected || n.IsSeed ? 2.5 : 1.5),
            Opacity = n.Status is NodeStatus.Discovered or NodeStatus.Skipped ? 0.75 : 1, Child = grid, Tag = n.Key, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(border, $"{n.Name}\n{n.StatusText}");
        border.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var pt = e.GetPosition(_canvas);
            _dragKey = n.Key;
            _dragOffset = new Point(pt.X - _pos[n.Key].X, pt.Y - _pos[n.Key].Y);
            e.Pointer.Capture(border);
            Select(n.Key);
        };
        border.PointerMoved += (_, e) =>
        {
            if (_dragKey != n.Key) return;
            var pt = e.GetPosition(_canvas);
            var p = (Math.Max(0, pt.X - _dragOffset.X), Math.Max(0, pt.Y - _dragOffset.Y));
            _pos[n.Key] = p;
            _manual[n.Key] = p;
            Canvas.SetLeft(border, p.Item1);
            Canvas.SetTop(border, p.Item2);
            PositionEdges();
        };
        border.PointerReleased += (_, e) => { e.Pointer.Capture(null); _dragKey = null; SizeCanvas(); };
        border.PointerCaptureLost += (_, _) => _dragKey = null;
        return border;
    }

    private void PositionEdges()
    {
        foreach (var (link, line, a, b) in _edgeViews)
        {
            var (ax, ay) = Center(link.A);
            var (bx, by) = Center(link.B);
            line.StartPoint = new Point(ax, ay);
            line.EndPoint = new Point(bx, by);
            Place(a, ax + (bx - ax) * 0.26, ay + (by - ay) * 0.26);
            Place(b, ax + (bx - ax) * 0.74, ay + (by - ay) * 0.74);
        }

        static void Place(Border label, double x, double y)
        {
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
        }
    }

    private (double X, double Y) Center(string key)
    {
        var p = _pos[key];
        return (p.X + TopologyLayout.NodeWidth / 2, p.Y + TopologyLayout.NodeHeight / 2);
    }

    // ---------- selection ----------

    private void Select(string? key)
    {
        var previous = _selected;
        _selected = key;
        foreach (var k in new[] { previous, key })
        {
            if (k is null || !_nodeViews.TryGetValue(k, out var v) || !_view.Nodes.TryGetValue(k, out var n)) continue;
            bool sel = k == key;
            v.Background = sel ? Ui.Solid(0x1E2A33) : Ui.CardBack;
            v.BorderThickness = new Thickness(sel || n.IsSeed ? 2.5 : 1.5);
        }
        ShowDetails();
    }

    private TopologyNode? SelectedNode => _selected is not null && _view.Nodes.TryGetValue(_selected, out var n) ? n : null;

    private void ShowDetails()
    {
        var n = SelectedNode;
        _connect.IsEnabled = n?.MgmtIp is not null && _seed is not null;
        _saveOne.IsEnabled = n?.MgmtIp is not null && _seed is not null && !n.IsSeed && !AlreadySaved(n.MgmtIp);
        if (n is null)
        {
            _nodeName.Text = "Nothing selected";
            _nodeIp.Text = _nodePlatform.Text = _nodeStatus.Text = "";
            _links.ItemsSource = null;
            return;
        }
        _nodeName.Text = n.Name;
        _nodeIp.Text = n.MgmtIp ?? "no management address";
        _nodePlatform.Text = string.Join(" · ", new[] { n.Kind == NodeKind.Unknown ? "" : n.Kind.ToString(), n.Platform, n.Capabilities }.Where(s => s.Length > 0));
        _nodeStatus.Text = (n.IsSeed ? "Starting device · " : "") + n.Status switch
        {
            NodeStatus.Crawled => $"Mapped - {n.StatusText}",
            NodeStatus.Failed => $"Login failed: {n.StatusText}",
            NodeStatus.Crawling => n.StatusText,
            _ => $"Not logged in to: {n.StatusText}",
        };
        _nodeStatus.Foreground = StatusBrush(n);
        _links.ItemsSource = _view.LinksOf(n.Key).Select(l =>
        {
            bool a = l.A.Equals(n.Key, StringComparison.OrdinalIgnoreCase);
            var other = a ? l.B : l.A;
            var otherName = _view.Nodes.TryGetValue(other, out var o) ? o.Name : other;
            return $"{(a ? l.APort : l.BPort)}  →  {otherName} {(a ? l.BPort : l.APort)}";
        }).ToList();
    }

    // ---------- sessions & export ----------

    private bool AlreadySaved(string host) => _ctx.Model.Sessions.Any(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

    private void Connect()
    {
        if (SelectedNode is not { MgmtIp: { } ip } n || _seed is null) return;
        var saved = _ctx.Sessions().FirstOrDefault(s => s.IsSshBased && string.Equals(s.Host, ip, StringComparison.OrdinalIgnoreCase));
        _ctx.OpenSession(saved ?? ProfileFor(n.Name, ip));
    }

    /// <summary>A new saved session copied from the stored (not credential-resolved) starting session, so a Password
    /// Manager link carries over instead of a copy of the password.</summary>
    private bool SaveSession(TopologyNode n)
    {
        if (n.MgmtIp is null || _seed is null || AlreadySaved(n.MgmtIp)) return false;
        var stored = _ctx.Model.Sessions.FirstOrDefault(s => s.Id == _seed.Id) ?? _seed;
        var p = stored.Clone();
        p.Id = Guid.NewGuid();
        p.Name = n.Name;
        p.Host = n.MgmtIp;
        p.Port = 22;
        p.Protocol = ProtocolKind.Ssh;
        p.Folder = $"Discovered/{stored.Name}";
        p.Tags = n.Kind == NodeKind.Unknown ? "discovered" : $"discovered, {n.Kind.ToString().ToLowerInvariant()}";
        p.Notes = $"{n.Platform} - found by Topology Mapper from {stored.Name} on {DateTime.Now:yyyy-MM-dd}".Trim(' ', '-');
        p.ColorTag = "";
        if (p.Validate().Count > 0) return false;
        _ctx.Model.Add(p);
        return true;
    }

    private void SaveOne()
    {
        if (SelectedNode is not { } n) return;
        try { _status.Text = SaveSession(n) ? $"Saved {n.Name} under Discovered/{_seed?.Name}." : $"{n.Name} is already a saved session."; }
        catch (Exception ex) { _status.Text = $"Couldn't save: {ex.Message}"; }
        ShowDetails();
        MainWindow.Current?.RefreshSessions();
    }

    private void SaveAll()
    {
        if (_seed is null) { _status.Text = "Discover first."; return; }
        try
        {
            int saved = _view.Nodes.Values.Where(n => !n.IsSeed).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Count(SaveSession);
            _status.Text = saved == 0 ? "Nothing new to save - every device with an address is already a session." : $"Saved {saved} device(s) under Discovered/{_seed.Name}.";
        }
        catch (Exception ex) { _status.Text = $"Couldn't save: {ex.Message}"; }
        ShowDetails();
        MainWindow.Current?.RefreshSessions();
    }

    private void CopyCsv()
    {
        if (_crawler is null) return;
        ToolUi.Copy(_crawler.Graph.ToCsv());
        _status.Text = "Link list copied as CSV.";
    }

    private async Task SaveDotAsync()
    {
        if (_crawler is null) return;
        if (await ToolUi.SaveTextAsync($"topology-{DateTime.Now:yyyyMMdd-HHmm}", _crawler.Graph.ToDot(), ".dot", "Graphviz DOT") is { } path)
            _status.Text = $"Saved {System.IO.Path.GetFileName(path)}";
    }

    private async Task SavePngAsync()
    {
        if (_view.Nodes.Count == 0) return;
        try
        {
            var path = await ToolUi.PickSavePathAsync($"topology-{DateTime.Now:yyyyMMdd-HHmm}", ".png", "PNG image");
            if (path is null) return;
            // The zoom is a transform on the canvas and would end up in the image - render at 100% and put it back.
            var zoom = _zoom;
            SetZoom(1);
            _zoomHost.UpdateLayout();
            try
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)_canvas.Width, (int)_canvas.Height), new Vector(96, 96));
                bitmap.Render(_canvas);
                bitmap.Save(path);
            }
            finally { SetZoom(zoom); }
            _status.Text = $"Saved {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex) { _status.Text = $"Couldn't save the image: {ex.Message}"; }
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _redrawTimer.Stop();
    }
}
