using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Monitoring;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Desktop.Controls;

namespace OmlTerminal.Desktop.Tools;

public sealed class PingMonitorTool : UserControl, IToolView
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
        public required Avalonia.Controls.Shapes.Ellipse Dot;
        public required TextBlock Name, Host, Now, Stats, State;
        public required LatencyChart Spark;
    }

    private sealed record OutageRow(string Title, string When, string Duration, bool Open);

    private static string ListFile => Path.Combine(AppPaths.DataDirectory, "ping-monitor.json"); // shared with the Windows app

    private readonly ToolContext _ctx;
    private readonly PingMonitor _monitor = new();
    private readonly Dictionary<PingHostState, Tile> _tiles = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _cts;
    private PingHostState? _selected;
    private string _order = "";

    private readonly TextBox _add = Ui.Input("", "Add hosts: 10.0.0.1 core-sw01 8.8.8.8 (Enter)", mono: true);
    private readonly NumericUpDown _interval = Ui.Number(1, 0.2, 60, 0.5), _timeout = Ui.Number(1000, 100, 10000, 100);
    private readonly CheckBox _sound = Ui.Check("Sound when a host goes down");
    private readonly TextBox _filter = Ui.Input("", "Filter");
    private readonly ComboBox _sort = Ui.Combo(new[] { "Problems first", "As added", "Name", "Slowest first" });
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _status = Ui.Text("", 12, color: Ui.Muted);
    private readonly WrapPanel _tilesPanel = new();
    private readonly TextBlock _empty = Ui.Text("No hosts yet - add some above, paste a list, or add your saved sessions.", 13, color: Ui.Muted);
    private readonly ItemsControl _outages = new();
    private readonly LatencyChart _detail = new() { ShowAxis = true, Height = 170 };
    private readonly TextBlock _detailTitle = Ui.Section("Latency · click a host to graph it");
    private readonly Button _start, _stop;

    public PingMonitorTool(ToolContext ctx)
    {
        _ctx = ctx;
        _interval.FormatString = "0.#";
        _add.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Add(); } };
        _start = Ui.Button("Start", () => _ = StartAsync(), accent: true);
        _stop = Ui.Button("Stop", Stop);
        _stop.IsEnabled = false;
        _filter.Width = 180;
        DockPanel.SetDock(_chips, Dock.Left);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(10, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _sort.Width = 160;
        _filter.TextChanged += (_, _) => { _order = ""; Refresh(); };
        _sort.SelectionChanged += (_, _) => { _order = ""; Refresh(); };
        _interval.ValueChanged += (_, _) => ApplyOptions();
        _timeout.ValueChanged += (_, _) => ApplyOptions();
        _monitor.StateChanged += (_, _, now) => { if (now == PingState.Down && _soundOn) Beep(); };

        _outages.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OutageRow>((o, _) => new Border
        {
            Padding = new Thickness(0, 6), BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = o?.Duration, [DockPanel.DockProperty] = Dock.Right, FontFamily = Ui.Mono, FontSize = 12, Foreground = o?.Open == true ? Ui.Rose : Ui.Amber },
                    Ui.Stack(1, new TextBlock { Text = o?.Title, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = o?.When, FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }),
                },
            },
        });

        var tilesScroll = new ScrollViewer { Content = Ui.Stack(0, _empty, _tilesPanel) };
        var outageCard = Ui.Card(new DockPanel
        {
            Children = { new TextBlock { Text = "OUTAGE LOG", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, [DockPanel.DockProperty] = Dock.Top, Margin = new Thickness(0, 0, 0, 6) },
                         new ScrollViewer { Content = _outages } },
        });
        var main = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,320"), ColumnSpacing = 14, RowDefinitions = RowDefinitions.Parse("*,Auto"), RowSpacing = 12 };
        main.Children.Add(tilesScroll);
        Grid.SetColumn(outageCard, 1);
        main.Children.Add(outageCard);
        var detailCard = Ui.Card(Ui.Stack(6, _detailTitle, _detail));
        Grid.SetRow(detailCard, 1);
        Grid.SetColumnSpan(detailCard, 2);
        main.Children.Add(detailCard);

        Content = Ui.Page("Ping Monitor", "Continuous ping to many hosts at once - latency, loss, jitter, MOS and an outage log. The list is saved and shared with the Windows app.", main,
            Ui.Columns("*,Auto,Auto,Auto,110,110,Auto,Auto,Auto",
                Ui.Field("Hosts", _add), Ui.Button("Add", Add), Ui.Button("Paste list...", () => _ = PasteAsync()), Ui.Button("Add sessions", AddSessions),
                Ui.Field("Every (s)", _interval), Ui.Field("Timeout (ms)", _timeout), _start, _stop, Ui.Button("Reset stats", () => { _monitor.ResetStats(); Refresh(); })),
            new DockPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Right,
                        Children = { _sound, _filter, _sort, Ui.Button("Export CSV...", () => _ = ExportAsync()) } },
                    new DockPanel { Children = { _chips, _status } },
                },
            });

        Load();
        _refresh.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { _refresh.Start(); Refresh(); };
        DetachedFromVisualTree += (_, _) => _refresh.Stop();
    }

    private bool _soundOn;

    /// <summary>The desktop's warning sound when one is available; silently nothing otherwise.</summary>
    private static void Beep()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) { Process.Start("afplay", "/System/Library/Sounds/Basso.aiff")?.Dispose(); return; }
            const string sound = "/usr/share/sounds/freedesktop/stereo/dialog-warning.oga";
            if (File.Exists(sound)) Process.Start(new ProcessStartInfo("paplay", [sound]) { UseShellExecute = false })?.Dispose();
        }
        catch { }
    }

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
            _interval.Value = (decimal)saved.IntervalSeconds;
            _timeout.Value = saved.TimeoutMs;
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
                IntervalSeconds = (double)(_interval.Value ?? 1),
                TimeoutMs = Ui.IntValue(_timeout, 1000),
            };
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(ListFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    private void Add()
    {
        var hosts = (_add.Text ?? "").Split([' ', ',', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var h in hosts) _monitor.Add(new PingTarget { Host = h });
        _add.Text = "";
        AfterAdd(hosts.Length);
    }

    private async Task PasteAsync()
    {
        if (MainWindow.Current is not { } owner) return;
        var text = await Dialogs.PromptMultilineAsync(owner, "Paste a host list",
            "One host per line - optionally host,name,group. Lines starting with # are ignored.",
            "10.10.0.1, Core switch, HQ\n10.10.0.254, Edge router, HQ\n8.8.8.8, Google DNS, Internet", "Add");
        if (text is null) return;
        var list = PingMonitor.ParseList(text);
        foreach (var t in list) _monitor.Add(t);
        AfterAdd(list.Count);
    }

    private void AddSessions()
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
        _status.Text = count == 0 ? "Nothing to add." : $"{_monitor.Hosts.Count} host(s) in the list.";
        Save();
        Refresh();
    }

    private void ApplyOptions()
    {
        _monitor.Options.Interval = TimeSpan.FromSeconds((double)(_interval.Value ?? 1));
        _monitor.Options.TimeoutMs = Ui.IntValue(_timeout, 1000);
    }

    // ---------- running ----------

    private async Task StartAsync()
    {
        if (_monitor.Hosts.Count == 0) { _status.Text = "Add some hosts first."; return; }
        ApplyOptions();
        _soundOn = _sound.IsChecked == true;
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        Save();
        try { await Task.Run(() => _monitor.RunAsync(_cts.Token)); }
        catch (OperationCanceledException) { }
        finally
        {
            _start.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ---------- drawing ----------

    private static IBrush StateBrush(PingState s) => s switch
    {
        PingState.Up => Ui.Mint, PingState.Degraded => Ui.Amber, PingState.Down => Ui.Rose, _ => Ui.Muted,
    };

    private static string Since(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";

    private void Refresh()
    {
        _soundOn = _sound.IsChecked == true;
        var views = _monitor.Snapshot(120);
        _empty.IsVisible = views.Count == 0;

        _chips.Children.Clear();
        foreach (var (state, label) in new[] { (PingState.Up, "up"), (PingState.Degraded, "degraded"), (PingState.Down, "down") })
        {
            int n = views.Count(v => v.State == state);
            var brush = StateBrush(state);
            _chips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3), BorderBrush = brush, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = $"{n} {label}", Foreground = brush, FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
            });
        }
        if (_cts is not null)
            _status.Text = $"{views.Count} host(s) · every {_monitor.Options.Interval.TotalSeconds:0.#} s · availability {(views.Count == 0 ? 100 : views.Average(v => v.Availability)):0.###}%";

        foreach (var gone in _tiles.Keys.Except(views.Select(v => v.Source)).ToList())
        {
            _tilesPanel.Children.Remove(_tiles[gone].Root);
            _tiles.Remove(gone);
        }
        var filter = (_filter.Text ?? "").Trim();
        IEnumerable<PingHostView> shown = views;
        if (filter.Length > 0)
            shown = shown.Where(v => v.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) || v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || v.Group.Contains(filter, StringComparison.OrdinalIgnoreCase));
        shown = _sort.SelectedIndex switch
        {
            1 => shown,
            2 => shown.OrderBy(v => v.Display, StringComparer.OrdinalIgnoreCase),
            3 => shown.OrderByDescending(v => double.IsNaN(v.Avg) ? -1 : v.Avg),
            _ => shown.OrderBy(v => v.State switch { PingState.Down => 0, PingState.Degraded => 1, PingState.Unknown => 3, _ => 2 })
                      .ThenBy(v => v.Display, StringComparer.OrdinalIgnoreCase),
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
            _tilesPanel.Children.Clear();
            foreach (var v in list) _tilesPanel.Children.Add(_tiles[v.Source].Root);
        }

        _outages.ItemsSource = _monitor.Outages.Reverse().Take(200).Select(o => new OutageRow(
            o.Name.Length > 0 ? $"{o.Name} ({o.Host})" : o.Host,
            $"{o.Start:dd MMM HH:mm:ss} → {(o.End is { } e ? e.ToString("HH:mm:ss") : "still down")} · {o.Reason}",
            Since(o.Duration), o.End is null)).ToList();
        DrawDetail();
    }

    private Tile MakeTile(PingHostState source)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var host = new TextBlock { FontSize = 11.5, Foreground = Ui.Muted, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis };
        var now = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, FontFamily = Ui.Mono, HorizontalAlignment = HorizontalAlignment.Right };
        var spark = new LatencyChart { Height = 40 };
        var stats = new TextBlock { FontSize = 11.5, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock { FontSize = 11.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis };

        var head = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"), ColumnSpacing = 8 };
        var names = Ui.Stack(0, name, host);
        Grid.SetColumn(names, 1);
        Grid.SetColumn(now, 2);
        head.Children.Add(dot);
        head.Children.Add(names);
        head.Children.Add(now);

        var root = new Border
        {
            Width = 258, Height = 156, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10),
            Background = Ui.CardBack, BorderThickness = new Thickness(1.5), Child = Ui.Stack(4, head, spark, stats, state), Cursor = new Cursor(StandardCursorType.Hand),
        };
        root.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
            _selected = source;
            Refresh();
        };
        var copy = new MenuItem { Header = "Copy address" };
        copy.Click += (_, _) => ToolUi.Copy(source.Target.Host);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) => { _monitor.Remove(source); if (_selected == source) _selected = null; Save(); _order = ""; Refresh(); };
        root.ContextMenu = new ContextMenu { ItemsSource = new[] { copy, remove } };
        ToolTip.SetTip(root, "Click to graph · right-click to copy or remove");
        return new Tile { Root = root, Dot = dot, Name = name, Host = host, Now = now, Stats = stats, State = state, Spark = spark };
    }

    private void UpdateTile(Tile t, PingHostView v)
    {
        var brush = StateBrush(v.State);
        bool selected = v.Source == _selected;
        t.Root.BorderBrush = selected ? Ui.Sky : brush;
        t.Root.BorderThickness = new Thickness(selected ? 2.5 : 1.5);
        t.Dot.Fill = brush;
        t.Name.Text = v.Display;
        t.Host.Text = v.Name.Length > 0 ? (v.Address is { } a && a != v.Host ? $"{v.Host} · {a}" : v.Host) : (v.Address is { } b && b != v.Host ? b : v.Group);
        t.Now.Text = v.Last is { } l ? $"{l:0.#} ms" : v.Sent == 0 ? "-" : "lost";
        t.Now.Foreground = v.Last is null && v.Sent > 0 ? Ui.Rose : brush;
        t.Stats.Text = $"loss {v.RecentLoss:0.#}%  avg {(double.IsNaN(v.Avg) ? "-" : v.Avg.ToString("0.#"))}  jit {(double.IsNaN(v.Jitter) ? "-" : v.Jitter.ToString("0.#"))}  MOS {v.Mos:0.0}";
        t.State.Text = v.State switch
        {
            PingState.Down => $"DOWN for {Since(DateTime.Now - v.StateSince)} · {v.LastError}",
            PingState.Unknown => v.Sent == 0 ? "waiting" : v.LastError,
            _ => $"{v.State.ToString().ToLowerInvariant()} {Since(DateTime.Now - v.StateSince)} · {v.Availability:0.##}% avail · {v.Outages} outage{(v.Outages == 1 ? "" : "s")}",
        };
        t.Spark.SetSamples(v.Samples, brush);
    }

    private void DrawDetail()
    {
        var v = _selected is null ? null : _monitor.Snapshot(_selected, 3600);
        if (v is null)
        {
            _detailTitle.Text = "LATENCY · CLICK A HOST TO GRAPH IT";
            _detail.SetSamples([]);
            return;
        }
        _detailTitle.Text = $"{v.Display.ToUpperInvariant()} · LAST {v.Samples.Count} PINGS · BEST {v.Best:0.#} · AVG {(double.IsNaN(v.Avg) ? 0 : v.Avg):0.#} · WORST {v.Worst:0.#} MS · {v.Sent - v.Received} LOST OF {v.Sent}";
        _detail.SetSamples(v.Samples, Ui.Sky);
    }

    private async Task ExportAsync()
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
        if (path is not null) _status.Text = $"Saved {path}";
    }

    private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
