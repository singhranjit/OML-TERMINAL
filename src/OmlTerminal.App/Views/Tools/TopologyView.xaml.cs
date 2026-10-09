using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.NetTools;
using OmlTerminal.Core.Topology;
using Windows.Foundation;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class TopologyView : UserControl, IToolView
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
    private string? _selected;
    private string? _dragKey;
    private Point _dragOffset;

    public TopologyView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        // A redraw rebuilds every node, which would yank one out from under the mouse mid-drag - wait for the drop.
        _redrawTimer.Tick += (_, _) => { if (_dirty && _dragKey is null) { _dirty = false; Redraw(); } };
        Loaded += (_, _) => ToolUi.FillSessions(SeedBox, _ctx.SshSessions());
    }

    // ---------- crawl ----------

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        if (SeedBox.SelectedItem is not SessionProfile seed) { StatusText.Text = "Pick the device to start from."; return; }
        var scope = new List<Subnet>();
        foreach (var part in ScopeBox.Text.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Subnet.TryParse(part, out var net) || net.IsV6) { StatusText.Text = $"'{part}' isn't a valid IPv4 network - use CIDR like 10.0.0.0/8."; return; }
            scope.Add(net);
        }
        var options = new TopologyOptions
        {
            MaxDepth = (int)(double.IsNaN(DepthBox.Value) ? 2 : DepthBox.Value),
            MaxDevices = (int)(double.IsNaN(MaxBox.Value) ? 50 : MaxBox.Value),
            Scope = scope,
            CrawlEndpoints = EndpointsCheck.IsChecked == true,
        };

        _seed = seed;
        _crawler = new TopologyCrawler();
        _crawler.Changed += () => _dirty = true;
        _manual.Clear();
        _selected = null;
        ShowDetails();
        _cts = new CancellationTokenSource();
        DiscoverButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = $"Logging in to {seed.Name}…";
        _redrawTimer.Start();
        bool stopped = false;
        try
        {
            await Task.Run(() => _crawler.CrawlAsync(seed, ProfileFor, options, _cts.Token));
        }
        catch (OperationCanceledException) { stopped = true; }
        finally
        {
            _redrawTimer.Stop();
            _cts.Dispose();
            _cts = null;
            DiscoverButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
        Redraw();
        Fit_Click(this, new RoutedEventArgs());
        StatusText.Text = Summary() + (stopped ? " Stopped." : " Done.");
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

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
        int mapped = nodes.Count(n => n.Status == NodeStatus.Crawled), failed = nodes.Count(n => n.Status == NodeStatus.Failed);
        int busy = nodes.Count(n => n.Status == NodeStatus.Crawling);
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

        MapCanvas.Children.Clear();
        _nodeViews.Clear();
        _edgeViews.Clear();
        var lineBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x90, 0x38, 0xBD, 0xF8));
        foreach (var link in _view.Links)
        {
            if (!_pos.ContainsKey(link.A) || !_pos.ContainsKey(link.B)) continue;
            var line = new Line { Stroke = lineBrush, StrokeThickness = 1.6 };
            var a = PortLabel(link.APort);
            var b = PortLabel(link.BPort);
            MapCanvas.Children.Add(line);
            MapCanvas.Children.Add(a);
            MapCanvas.Children.Add(b);
            _edgeViews.Add((link, line, a, b));
        }
        foreach (var node in _view.Nodes.Values)
        {
            if (!_pos.TryGetValue(node.Key, out var p)) continue;
            var view = NodeView(node);
            Canvas.SetLeft(view, p.X);
            Canvas.SetTop(view, p.Y);
            MapCanvas.Children.Add(view);
            _nodeViews[node.Key] = view;
        }
        PositionEdges();
        MapCanvas.Width = Math.Max(800, _pos.Values.DefaultIfEmpty().Max(p => p.X) + TopologyLayout.NodeWidth + 60);
        MapCanvas.Height = Math.Max(500, _pos.Values.DefaultIfEmpty().Max(p => p.Y) + TopologyLayout.NodeHeight + 60);
        EmptyText.Visibility = _view.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_cts is not null) StatusText.Text = Summary();
        ShowDetails();
    }

    private static Border PortLabel(string port)
    {
        var label = new Border
        {
            Background = ToolUi.Brush("CardBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = port, FontSize = 10.5, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], Opacity = 0.8 },
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return label;
    }

    private static string Glyph(NodeKind kind) => kind switch
    {
        NodeKind.Router => "",
        NodeKind.Switch => "",
        NodeKind.Firewall => "",
        NodeKind.AccessPoint => "",
        NodeKind.Phone => "",
        NodeKind.Host => "",
        _ => "",
    };

    private static string StatusBrushKey(TopologyNode n) => n.Status switch
    {
        _ when n.IsSeed && n.Status != NodeStatus.Failed => "OmlOrangeBrush",
        NodeStatus.Crawled => "OmlMintBrush",
        NodeStatus.Crawling => "OmlSkyBrush",
        NodeStatus.Failed => "OmlRoseBrush",
        _ => "StatusIdleBrush",
    };

    private Border NodeView(TopologyNode n)
    {
        var accent = ToolUi.Brush(StatusBrushKey(n));
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = n.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = n.MgmtIp ?? "no address", FontSize = 11, Opacity = 0.7, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"] });
        text.Children.Add(new TextBlock { Text = n.Platform.Length > 0 ? n.Platform : n.StatusText, FontSize = 10.5, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis });
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = Glyph(n.Kind), FontSize = 18, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        bool selected = string.Equals(n.Key, _selected, StringComparison.OrdinalIgnoreCase);
        var border = new Border
        {
            Width = TopologyLayout.NodeWidth,
            Height = TopologyLayout.NodeHeight,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Background = ToolUi.Brush(selected ? "CardHoverBrush" : "CardBrush"),
            BorderBrush = accent,
            BorderThickness = new Thickness(selected || n.IsSeed ? 2.5 : 1.5),
            Opacity = n.Status is NodeStatus.Discovered or NodeStatus.Skipped ? 0.75 : 1,
            Child = grid,
            Tag = n.Key,
        };
        ToolTipService.SetToolTip(border, $"{n.Name}\n{n.StatusText}");
        border.PointerPressed += Node_PointerPressed;
        border.PointerMoved += Node_PointerMoved;
        border.PointerReleased += Node_PointerReleased;
        border.PointerCaptureLost += (_, _) => _dragKey = null;
        return border;
    }

    private void PositionEdges()
    {
        foreach (var (link, line, a, b) in _edgeViews)
        {
            var (ax, ay) = Center(link.A);
            var (bx, by) = Center(link.B);
            line.X1 = ax; line.Y1 = ay; line.X2 = bx; line.Y2 = by;
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

    // ---------- interaction ----------

    private void Node_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string key } border) return;
        e.Handled = true;
        var pt = e.GetCurrentPoint(MapCanvas).Position;
        _dragKey = key;
        _dragOffset = new Point(pt.X - _pos[key].X, pt.Y - _pos[key].Y);
        border.CapturePointer(e.Pointer);
        Select(key);
    }

    private void Node_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKey is null || sender is not Border { Tag: string key } border || key != _dragKey) return;
        var pt = e.GetCurrentPoint(MapCanvas).Position;
        var p = (Math.Max(0, pt.X - _dragOffset.X), Math.Max(0, pt.Y - _dragOffset.Y));
        _pos[key] = p;
        _manual[key] = p;
        Canvas.SetLeft(border, p.Item1);
        Canvas.SetTop(border, p.Item2);
        PositionEdges();
    }

    private void Node_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border) border.ReleasePointerCapture(e.Pointer);
        _dragKey = null;
    }

    private void MapCanvas_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MapCanvas)) Select(null);
    }

    private void Select(string? key)
    {
        var previous = _selected;
        _selected = key;
        foreach (var k in new[] { previous, key })
        {
            if (k is null || !_nodeViews.TryGetValue(k, out var v) || !_view.Nodes.TryGetValue(k, out var n)) continue;
            bool sel = k == key;
            v.Background = ToolUi.Brush(sel ? "CardHoverBrush" : "CardBrush");
            v.BorderThickness = new Thickness(sel || n.IsSeed ? 2.5 : 1.5);
        }
        ShowDetails();
    }

    private TopologyNode? SelectedNode => _selected is not null && _view.Nodes.TryGetValue(_selected, out var n) ? n : null;

    private void ShowDetails()
    {
        var n = SelectedNode;
        ConnectButton.IsEnabled = n?.MgmtIp is not null && _seed is not null;
        SaveOneButton.IsEnabled = n?.MgmtIp is not null && _seed is not null && !n.IsSeed && !AlreadySaved(n.MgmtIp);
        if (n is null)
        {
            NodeName.Text = "Nothing selected";
            NodeIp.Text = NodePlatform.Text = NodeStatusText.Text = "";
            LinkList.ItemsSource = null;
            return;
        }
        NodeName.Text = n.Name;
        NodeIp.Text = n.MgmtIp ?? "no management address";
        NodePlatform.Text = string.Join(" · ", new[] { n.Kind == NodeKind.Unknown ? "" : n.Kind.ToString(), n.Platform, n.Capabilities }.Where(s => s.Length > 0));
        NodeStatusText.Text = (n.IsSeed ? "Starting device · " : "") + n.Status switch
        {
            NodeStatus.Crawled => $"Mapped - {n.StatusText}",
            NodeStatus.Failed => $"Login failed: {n.StatusText}",
            NodeStatus.Crawling => n.StatusText,
            _ => $"Not logged in to: {n.StatusText}",
        };
        NodeStatusText.Foreground = ToolUi.Brush(StatusBrushKey(n));
        LinkList.ItemsSource = _view.LinksOf(n.Key).Select(l =>
        {
            bool a = l.A.Equals(n.Key, StringComparison.OrdinalIgnoreCase);
            var other = a ? l.B : l.A;
            var otherName = _view.Nodes.TryGetValue(other, out var o) ? o.Name : other;
            return $"{(a ? l.APort : l.BPort)}  →  {otherName} {(a ? l.BPort : l.APort)}";
        }).ToList();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => MapScroll.ChangeView(null, null, Math.Min(3f, MapScroll.ZoomFactor * 1.25f));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => MapScroll.ChangeView(null, null, Math.Max(0.2f, MapScroll.ZoomFactor / 1.25f));

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (MapScroll.ViewportWidth <= 0 || MapCanvas.Width <= 0) return;
        var factor = (float)Math.Clamp(Math.Min(MapScroll.ViewportWidth / MapCanvas.Width, MapScroll.ViewportHeight / MapCanvas.Height), 0.2, 1.0);
        MapScroll.ChangeView(0, 0, factor);
    }

    private void Relayout_Click(object sender, RoutedEventArgs e)
    {
        _manual.Clear();
        Redraw();
    }

    // ---------- sessions & export ----------

    private bool AlreadySaved(string host) => _ctx.Model.Sessions.Any(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { MgmtIp: { } ip } n || _seed is null) return;
        var saved = _ctx.Sessions().FirstOrDefault(s => s.IsSshBased && string.Equals(s.Host, ip, StringComparison.OrdinalIgnoreCase));
        _ctx.OpenSession(saved ?? ProfileFor(n.Name, ip));
    }

    /// <summary>A new saved session for a discovered device, copied from the stored (not credential-resolved) starting
    /// session - so a Password Manager link carries over instead of a plain-text copy of the password.</summary>
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

    private void SaveOne_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n) return;
        try { StatusText.Text = SaveSession(n) ? $"Saved {n.Name} under Discovered/{_seed?.Name}." : $"{n.Name} is already a saved session."; }
        catch (Exception ex) { StatusText.Text = $"Couldn't save: {ex.Message}"; }
        ShowDetails();
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_seed is null) { StatusText.Text = "Discover first."; return; }
        try
        {
            int saved = _view.Nodes.Values.Where(n => !n.IsSeed).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Count(SaveSession);
            StatusText.Text = saved == 0 ? "Nothing new to save - every device with an address is already a session." : $"Saved {saved} device(s) under Discovered/{_seed.Name}.";
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't save: {ex.Message}"; }
        ShowDetails();
    }

    private void CopyCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_crawler is null) return;
        ToolUi.Copy(_crawler.Graph.ToCsv());
        StatusText.Text = "Link list copied as CSV.";
    }

    private async void SaveDot_Click(object sender, RoutedEventArgs e)
    {
        if (_crawler is null) return;
        if (await ToolUi.SaveTextAsync($"topology-{DateTime.Now:yyyyMMdd-HHmm}", _crawler.Graph.ToDot(), ".dot", "Graphviz DOT") is { } path)
            StatusText.Text = $"Saved {System.IO.Path.GetFileName(path)}";
    }

    private async void SavePng_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Nodes.Count == 0) return;
        try
        {
            var path = await ToolUi.PickSavePathAsync($"topology-{DateTime.Now:yyyyMMdd-HHmm}", ".png", "PNG image");
            if (path is null) return;
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(MapCanvas);
            var pixels = await bitmap.GetPixelsAsync();
            using var file = System.IO.File.Create(path);
            using var stream = file.AsRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            StatusText.Text = $"Saved {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't save the image: {ex.Message}"; }
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _redrawTimer.Stop();
    }
}
