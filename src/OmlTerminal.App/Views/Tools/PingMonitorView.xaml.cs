using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OmlTerminal.Core.Monitoring;
using OmlTerminal.Core.Persistence;
using Windows.Foundation;

namespace OmlTerminal.App.Views.Tools;

public sealed class OutageRow
{
    public required string Title { get; init; }
    public required string When { get; init; }
    public required string Duration { get; init; }
    public required Brush Brush { get; init; }
    public override string ToString() => $"{Title}, {When}, {Duration}";
}

public sealed partial class PingMonitorView : UserControl, IToolView
{
    private sealed class SavedList
    {
        public List<PingTarget> Targets { get; set; } = [];
        public double IntervalSeconds { get; set; } = 1;
        public int TimeoutMs { get; set; } = 1000;
    }

    /// <summary>The live controls of one host's tile, updated in place every refresh.</summary>
    private sealed class Tile
    {
        public required Border Root;
        public required Ellipse Dot;
        public required TextBlock Name, Host, Now, Stats, State;
        public required Canvas Spark;
    }

    private static string ListFile => System.IO.Path.Combine(AppPaths.DataDirectory, "ping-monitor.json");

    private readonly ToolContext _ctx;
    private readonly PingMonitor _monitor = new();
    private readonly Dictionary<PingHostState, Tile> _tiles = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _cts;
    private PingHostState? _selected;
    private string _order = "";

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    public PingMonitorView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _refresh.Tick += (_, _) => Refresh();
        _monitor.StateChanged += (h, old, now) =>
        {
            if (now == PingState.Down && SoundSoon()) MessageBeep(0x30);
        };
        Load();
        Loaded += (_, _) => { _refresh.Start(); Refresh(); };
        Unloaded += (_, _) => _refresh.Stop();
    }

    private bool _soundOn;
    private bool SoundSoon() => _soundOn;

    public void Shutdown()
    {
        _cts?.Cancel();
        _refresh.Stop();
        Save();
    }

    // ---------- the host list ----------

    private void Load()
    {
        try
        {
            if (!File.Exists(ListFile)) return;
            var saved = JsonSerializer.Deserialize<SavedList>(File.ReadAllText(ListFile));
            if (saved is null) return;
            IntervalBox.Value = saved.IntervalSeconds;
            TimeoutBox.Value = saved.TimeoutMs;
            foreach (var t in saved.Targets) _monitor.Add(t);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            if (e is JsonException) UnreadableFile.Keep(ListFile);
        }
    }

    private void Save()
    {
        try
        {
            var saved = new SavedList
            {
                Targets = _monitor.Hosts.Select(h => h.Target).ToList(),
                IntervalSeconds = double.IsNaN(IntervalBox.Value) ? 1 : IntervalBox.Value,
                TimeoutMs = double.IsNaN(TimeoutBox.Value) ? 1000 : (int)TimeoutBox.Value,
            };
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(ListFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    private void AddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Add_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var hosts = AddBox.Text.Split([' ', ',', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var h in hosts) _monitor.Add(new PingTarget { Host = h });
        AddBox.Text = "";
        AfterAdd(hosts.Length);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true, Height = 260, TextWrapping = TextWrapping.NoWrap, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            PlaceholderText = "10.10.0.1, Core switch, HQ\n10.10.0.254, Edge router, HQ\n8.8.8.8, Google DNS, Internet",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Paste a host list", PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "One host per line - optionally host,name,group. Lines starting with # are ignored.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 }, box } },
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var list = PingMonitor.ParseList(box.Text);
        foreach (var t in list) _monitor.Add(t);
        AfterAdd(list.Count);
    }

    private void AddSessions_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var s in _ctx.Sessions().Where(s => s.Host.Length > 0 && !s.Host.Contains('/')).DistinctBy(s => s.Host, StringComparer.OrdinalIgnoreCase))
        {
            _monitor.Add(new PingTarget { Host = s.Host, Name = s.Name, Group = s.Folder });
            n++;
        }
        AfterAdd(n);
    }

    private void AfterAdd(int count)
    {
        StatusText.Text = count == 0 ? "Nothing to add." : $"{_monitor.Hosts.Count} host(s) in the list.";
        Save();
        Refresh();
    }

    private void Options_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(sender.Value) || IntervalBox is null || TimeoutBox is null) return; // XAML sets Value during InitializeComponent
        _monitor.Options.Interval = TimeSpan.FromSeconds(double.IsNaN(IntervalBox.Value) ? 1 : IntervalBox.Value);
        _monitor.Options.TimeoutMs = double.IsNaN(TimeoutBox.Value) ? 1000 : (int)TimeoutBox.Value;
    }

    // ---------- running ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor.Hosts.Count == 0) { StatusText.Text = "Add some hosts first."; return; }
        _monitor.Options.Interval = TimeSpan.FromSeconds(double.IsNaN(IntervalBox.Value) ? 1 : IntervalBox.Value);
        _monitor.Options.TimeoutMs = double.IsNaN(TimeoutBox.Value) ? 1000 : (int)TimeoutBox.Value;
        _soundOn = SoundToggle.IsChecked == true;
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        Save();
        try { await Task.Run(() => _monitor.RunAsync(_cts.Token)); }
        catch (OperationCanceledException) { }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _monitor.ResetStats();
        Refresh();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) { _order = ""; Refresh(); }
    private void Sort_Changed(object sender, SelectionChangedEventArgs e) { _order = ""; if (IsLoaded) Refresh(); }

    // ---------- drawing ----------

    private static string StateBrush(PingState s) => s switch
    {
        PingState.Up => "OmlMintBrush", PingState.Degraded => "OmlAmberBrush", PingState.Down => "OmlRoseBrush", _ => "StatusIdleBrush",
    };

    private static string Since(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";

    private void Refresh()
    {
        _soundOn = SoundToggle.IsChecked == true;
        var views = _monitor.Snapshot(120);
        EmptyText.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // chips
        Chips.Children.Clear();
        foreach (var (state, label) in new[] { (PingState.Up, "up"), (PingState.Degraded, "degraded"), (PingState.Down, "down") })
        {
            int n = views.Count(v => v.State == state);
            var brush = ToolUi.Brush(StateBrush(state));
            Chips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), BorderBrush = brush, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = $"{n} {label}", Foreground = brush, FontWeight = FontWeights.SemiBold, FontSize = 12.5 },
            });
        }
        if (_cts is not null) StatusText.Text = $"{views.Count} host(s) · every {_monitor.Options.Interval.TotalSeconds:0.#} s · availability {(views.Count == 0 ? 100 : views.Average(v => v.Availability)):0.###}%";

        // tiles: create/remove, then order
        foreach (var gone in _tiles.Keys.Except(views.Select(v => v.Source)).ToList())
        {
            Tiles.Children.Remove(_tiles[gone].Root);
            _tiles.Remove(gone);
        }
        var filter = FilterBox.Text.Trim();
        IEnumerable<PingHostView> shown = views;
        if (filter.Length > 0)
            shown = shown.Where(v => v.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) || v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || v.Group.Contains(filter, StringComparison.OrdinalIgnoreCase));
        shown = SortBox.SelectedIndex switch
        {
            1 => shown,
            2 => shown.OrderBy(v => v.Display, StringComparer.OrdinalIgnoreCase),
            3 => shown.OrderByDescending(v => double.IsNaN(v.Avg) ? -1 : v.Avg),
            _ => shown.OrderBy(v => v.State switch { PingState.Down => 0, PingState.Degraded => 1, PingState.Unknown => 3, _ => 2 }).ThenBy(v => v.Display, StringComparer.OrdinalIgnoreCase),
        };
        var list = shown.ToList();
        foreach (var v in list)
        {
            if (!_tiles.TryGetValue(v.Source, out var tile)) _tiles[v.Source] = tile = MakeTile(v.Source);
            UpdateTile(tile, v);
        }
        var order = string.Join("|", list.Select(v => v.Host));
        if (order != _order)
        {
            _order = order;
            Tiles.Children.Clear();
            foreach (var v in list) Tiles.Children.Add(_tiles[v.Source].Root);
        }

        OutageList.ItemsSource = _monitor.Outages.Reverse().Take(200).Select(o => new OutageRow
        {
            Title = o.Name.Length > 0 ? $"{o.Name} ({o.Host})" : o.Host,
            When = $"{o.Start:dd MMM HH:mm:ss} → {(o.End is { } e ? e.ToString("HH:mm:ss") : "still down")} · {o.Reason}",
            Duration = Since(o.Duration),
            Brush = ToolUi.Brush(o.End is null ? "OmlRoseBrush" : "OmlAmberBrush"),
        }).ToList();
        DrawDetail();
    }

    private Tile MakeTile(PingHostState source)
    {
        var mono = (FontFamily)Application.Current.Resources["MonoFont"];
        var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var host = new TextBlock { FontSize = 11.5, Opacity = 0.6, FontFamily = mono, TextTrimming = TextTrimming.CharacterEllipsis };
        var now = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = mono, HorizontalAlignment = HorizontalAlignment.Right };
        var spark = new Canvas { Height = 44 };
        var stats = new TextBlock { FontSize = 11.5, FontFamily = mono, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock { FontSize = 11.5, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel { Children = { name, host } };
        Grid.SetColumn(names, 1);
        Grid.SetColumn(now, 2);
        head.Children.Add(dot);
        head.Children.Add(names);
        head.Children.Add(now);

        var body = new StackPanel { Spacing = 4, Children = { head, spark, stats, state } };
        var root = new Border
        {
            Width = 258, Height = 156, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10),
            Background = ToolUi.Brush("InputSurfaceBrush"), BorderThickness = new Thickness(1.5), Child = body,
        };
        root.Tapped += (_, _) => { _selected = source; DrawDetail(); };
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Copy address", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) => ToolUi.Copy(source.Target.Host);
        var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "" } };
        remove.Click += (_, _) => { _monitor.Remove(source); if (_selected == source) _selected = null; Save(); _order = ""; Refresh(); };
        menu.Items.Add(copy);
        menu.Items.Add(remove);
        root.ContextFlyout = menu;
        ToolTipService.SetToolTip(root, "Click to graph · right-click to copy or remove");
        return new Tile { Root = root, Dot = dot, Name = name, Host = host, Now = now, Stats = stats, State = state, Spark = spark };
    }

    private void UpdateTile(Tile t, PingHostView v)
    {
        var brush = ToolUi.Brush(StateBrush(v.State));
        t.Root.BorderBrush = v.Source == _selected ? ToolUi.Brush("OmlSkyBrush") : brush;
        t.Root.BorderThickness = new Thickness(v.Source == _selected ? 2.5 : 1.5);
        t.Dot.Fill = brush;
        t.Name.Text = v.Display;
        t.Host.Text = v.Name.Length > 0 ? (v.Address is { } a && a != v.Host ? $"{v.Host} · {a}" : v.Host) : (v.Address is { } b && b != v.Host ? b : v.Group);
        t.Now.Text = v.Last is { } l ? $"{l:0.#} ms" : v.Sent == 0 ? "-" : "lost";
        t.Now.Foreground = v.Last is null && v.Sent > 0 ? ToolUi.Brush("OmlRoseBrush") : brush;
        t.Stats.Text = $"loss {v.RecentLoss:0.#}%  avg {(double.IsNaN(v.Avg) ? "-" : v.Avg.ToString("0.#"))}  jit {(double.IsNaN(v.Jitter) ? "-" : v.Jitter.ToString("0.#"))}  MOS {v.Mos:0.0}";
        t.State.Text = v.State switch
        {
            PingState.Down => $"DOWN for {Since(DateTime.Now - v.StateSince)} · {v.LastError}",
            PingState.Unknown => v.Sent == 0 ? "waiting" : v.LastError,
            _ => $"{v.State.ToString().ToLowerInvariant()} {Since(DateTime.Now - v.StateSince)} · {v.Availability:0.##}% avail · {v.Outages} outage{(v.Outages == 1 ? "" : "s")}",
        };
        DrawSpark(t.Spark, v.Samples, brush);
    }

    private static void DrawSpark(Canvas c, IReadOnlyList<(DateTime At, double? Rtt)> samples, Brush brush)
    {
        c.Children.Clear();
        double w = 234, h = c.Height;
        if (samples.Count == 0) return;
        double max = Math.Max(5, samples.Where(s => s.Rtt is not null).Select(s => s.Rtt!.Value).DefaultIfEmpty(5).Max() * 1.15);
        double step = w / 120.0;
        double x0 = w - samples.Count * step;
        var line = new Polyline { Stroke = brush, StrokeThickness = 1.4 };
        var lost = ToolUi.Brush("OmlRoseBrush");
        for (int i = 0; i < samples.Count; i++)
        {
            double x = x0 + i * step;
            if (samples[i].Rtt is { } r) line.Points.Add(new Point(x, h - 2 - (h - 4) * r / max));
            else
            {
                c.Children.Add(new Rectangle { Width = Math.Max(1.5, step), Height = h, Fill = lost, Opacity = 0.55, Margin = new Thickness(x, 0, 0, 0) });
                if (line.Points.Count > 0) { c.Children.Add(line); line = new Polyline { Stroke = brush, StrokeThickness = 1.4 }; }
            }
        }
        if (line.Points.Count > 0) c.Children.Add(line);
    }

    private void Detail_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDetail();

    private void DrawDetail()
    {
        DetailCanvas.Children.Clear();
        var v = _selected is null ? null : _monitor.Snapshot(_selected, 3600);
        if (v is null) { DetailTitle.Text = "LATENCY · CLICK A HOST TO GRAPH IT"; return; }
        double w = DetailCanvas.ActualWidth, h = DetailCanvas.ActualHeight;
        if (w < 80 || h < 40) return;
        const double left = 44;
        int cap = (int)Math.Min(3600, (w - left) / 1.5);
        var s = v.Samples.Skip(Math.Max(0, v.Samples.Count - cap)).ToList();
        DetailTitle.Text = $"{v.Display.ToUpperInvariant()} · LAST {s.Count} PINGS · BEST {v.Best:0.#} · AVG {(double.IsNaN(v.Avg) ? 0 : v.Avg):0.#} · WORST {v.Worst:0.#} MS · {v.Sent - v.Received} LOST OF {v.Sent}";
        if (s.Count == 0) return;
        double max = Math.Max(5, s.Where(x => x.Rtt is not null).Select(x => x.Rtt!.Value).DefaultIfEmpty(5).Max() * 1.15);
        double Y(double val) => 4 + (h - 8) * (1 - val / max);
        var axis = ToolUi.Brush("HairlineBrush");
        foreach (var f in new[] { 0.0, 0.5, 1.0 })
        {
            double y = Y(max * f);
            DetailCanvas.Children.Add(new Line { X1 = left, X2 = w, Y1 = y, Y2 = y, Stroke = axis });
            var t = new TextBlock { Text = $"{max * f:0}", FontSize = 10.5, Opacity = 0.55 };
            Canvas.SetTop(t, y - 8);
            DetailCanvas.Children.Add(t);
        }
        double step = (w - left) / Math.Max(s.Count - 1, 60); // a short run fills the width, a long one scrolls
        var line = new Polyline { Stroke = ToolUi.Brush("OmlSkyBrush"), StrokeThickness = 1.4 };
        var lost = ToolUi.Brush("OmlRoseBrush");
        for (int i = 0; i < s.Count; i++)
        {
            double x = left + i * step;
            if (s[i].Rtt is { } r) line.Points.Add(new Point(x, Y(r)));
            else
            {
                DetailCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 4, Y2 = h - 4, Stroke = lost, StrokeThickness = Math.Max(1.2, step), Opacity = 0.6 });
                if (line.Points.Count > 0) { DetailCanvas.Children.Add(line); line = new Polyline { Stroke = line.Stroke, StrokeThickness = 1.4 }; }
            }
        }
        if (line.Points.Count > 0) DetailCanvas.Children.Add(line);
        var first = new TextBlock { Text = s[0].At.ToString("HH:mm:ss"), FontSize = 10.5, Opacity = 0.5 };
        Canvas.SetLeft(first, left);
        Canvas.SetTop(first, h - 14);
        DetailCanvas.Children.Add(first);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder("host,name,group,state,sent,received,loss_pct,availability_pct,best_ms,avg_ms_recent,worst_ms,jitter_ms,mos,outages\n");
        foreach (var v in _monitor.Snapshot(0))
            sb.AppendLine(ToolUi.Csv(v.Host, v.Name, v.Group, v.State.ToString(), v.Sent.ToString(), v.Received.ToString(), $"{v.TotalLoss:0.##}", $"{v.Availability:0.###}",
                N(v.Best), N(v.Avg), N(v.Worst), N(v.Jitter), $"{v.Mos:0.0}", v.Outages.ToString()));
        sb.AppendLine();
        sb.AppendLine("outage_host,outage_name,start,end,duration_s,reason");
        foreach (var o in _monitor.Outages)
            sb.AppendLine(ToolUi.Csv(o.Host, o.Name, o.Start.ToString("yyyy-MM-dd HH:mm:ss"), o.End?.ToString("yyyy-MM-dd HH:mm:ss") ?? "", $"{o.Duration.TotalSeconds:0}", o.Reason));
        var path = await ToolUi.SaveTextAsync($"ping-monitor-{DateTime.Now:yyyyMMdd-HHmm}", sb.ToString(), ".csv", "CSV");
        if (path is not null) StatusText.Text = $"Saved {path}";
    }

    private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
