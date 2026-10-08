using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OmlTerminal.Core.Snmp;
using Windows.Foundation;

namespace OmlTerminal.App.Views.Tools;

public sealed class DeviceRow
{
    public required TrafficTarget Target { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required Brush Brush { get; init; }
    public override string ToString() => $"{Name}, {Detail.Replace('\n', ' ')}"; // what screen readers announce
}

public sealed partial class TrafficGraphView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly TrafficGrapher _grapher = TrafficGrapher.Shared;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    private TrafficTarget? _target;
    private TrafficInterface? _iface;
    private bool _refreshingList;

    public TrafficGraphView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _refresh.Tick += (_, _) => Redraw();
        _grapher.Polled += OnPolled;
        _grapher.Alert += OnAlert;
        Loaded += (_, _) => { _grapher.Start(); RefreshDevices(); _refresh.Start(); };
        Unloaded += (_, _) => _refresh.Stop();
    }

    public void Shutdown()
    {
        _refresh.Stop();
        _grapher.Polled -= OnPolled;
        _grapher.Alert -= OnAlert;
        _grapher.Flush();
    }

    private void OnPolled(Guid id) => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshDevices();
        if (_target?.Id == id) Redraw();
    });

    private void OnAlert(TrafficAlert a) => DispatcherQueue.TryEnqueue(() =>
    {
        AlertBar.Title = $"{a.Device} {a.Interface} is {a.Percent:0}% busy ({a.Direction})";
        AlertBar.Message = $"{TrafficArchive.Bits(a.Bps)} at {a.At:HH:mm:ss}.";
        AlertBar.IsOpen = true;
    });

    // ---------- device list ----------

    private void RefreshDevices()
    {
        var targets = _grapher.Targets;
        _refreshingList = true;
        var rows = targets.Select(t =>
        {
            var st = _grapher.Status(t.Id);
            string brush = !t.Enabled ? "StatusIdleBrush" : st.Error is not null ? "OmlRoseBrush" : st.LastPoll is null ? "StatusIdleBrush" : "OmlMintBrush";
            var detail = $"{t.Host} · {t.Interfaces.Count} interface(s) · every {t.PollSeconds}s" +
                         (st.Error is not null ? $"\n{st.Error}" : st.LastPoll is { } lp ? $"\npolled {lp:HH:mm:ss}{(st.HighCapacity ? " · 64-bit counters" : "")}" : "\nwaiting for the first poll");
            return new DeviceRow { Target = t, Name = t.Name.Length > 0 ? t.Name : t.Host, Detail = detail, Brush = ToolUi.Brush(brush) };
        }).ToList();
        DeviceList.ItemsSource = rows;
        DeviceList.SelectedItem = rows.FirstOrDefault(r => r.Target.Id == _target?.Id);
        _refreshingList = false;
        if (_target is null && rows.Count > 0) DeviceList.SelectedIndex = 0;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingList || DeviceList.SelectedItem is not DeviceRow r) return;
        _target = r.Target;
        _iface = null;
        Redraw();
    }

    private void Back_Click(object sender, RoutedEventArgs e) { _iface = null; Redraw(); }

    private async void PollNow_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;
        await Task.Run(() => _grapher.PollAsync(_target, CancellationToken.None));
    }

    // ---------- graphs ----------

    private void Redraw()
    {
        if (_target is null) { HeadTitle.Text = ""; HeadDetail.Text = ""; Overview.Children.Clear(); DetailPanel.Children.Clear(); return; }
        var st = _grapher.Status(_target.Id);
        HeadTitle.Text = _iface is null ? $"{_target.Name}" : $"{_target.Name} · {_iface.Display}";
        HeadDetail.Text = st.System is { } sys
            ? $"{sys.Name} · up {(int)sys.Uptime.TotalDays}d {sys.Uptime.Hours}h · {FirstLine(sys.Description)}{(sys.Location.Length > 0 ? $" · {sys.Location}" : "")}"
            : st.Error ?? $"{_target.Host}:{_target.Port} · SNMP {_target.Credentials.Version}";
        BackButton.Visibility = _iface is null ? Visibility.Collapsed : Visibility.Visible;
        Overview.Visibility = _iface is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _iface is null ? Visibility.Collapsed : Visibility.Visible;
        if (_iface is null) DrawOverview(); else DrawDetail(_iface);
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private void DrawOverview()
    {
        Overview.Children.Clear();
        foreach (var i in _target!.Interfaces)
        {
            var (pts, latest) = _grapher.Query(_target.Id, i.Index, TrafficPeriod.Daily);
            var s = TrafficArchive.Stats(pts, latest);
            var canvas = new Canvas { Width = 336, Height = 120 };
            DrawGraph(canvas, pts, TrafficPeriod.Daily, i.SpeedBps, 336, 120, compact: true);
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = i.Display, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 70, 0) });
            head.Children.Add(new TextBlock { Text = Speed(i.SpeedBps), Opacity = 0.6, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
            var now = new TextBlock { FontFamily = Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            now.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"in {TrafficArchive.Bits(s.InNow)} ", Foreground = ToolUi.Brush("OmlMintBrush") });
            now.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"out {TrafficArchive.Bits(s.OutNow)} ", Foreground = ToolUi.Brush("OmlSkyBrush") });
            now.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"· peak {TrafficArchive.Bits(Math.Max(s.InMax, s.OutMax))}{Util(Math.Max(s.InMax, s.OutMax), i.SpeedBps)}" });
            var card = new Border
            {
                Width = 360, Height = 202, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 12, 12),
                Background = ToolUi.Brush("InputSurfaceBrush"), BorderBrush = ToolUi.Brush("HairlineBrush"), BorderThickness = new Thickness(1),
                Child = new StackPanel { Spacing = 6, Children = { head, canvas, now } },
            };
            ToolTipService.SetToolTip(card, "Daily graph · click for weekly, monthly and yearly");
            var iface = i;
            card.Tapped += (_, _) => { _iface = iface; Redraw(); };
            Overview.Children.Add(card);
        }
    }

    private void DrawDetail(TrafficInterface i)
    {
        DetailPanel.Children.Clear();
        foreach (var (period, title) in new[]
        {
            (TrafficPeriod.Live, "LIVE · LAST 15 MINUTES"), (TrafficPeriod.Daily, "DAILY · EACH POLL"), (TrafficPeriod.Weekly, "WEEKLY · 30-MINUTE AVERAGES"),
            (TrafficPeriod.Monthly, "MONTHLY · 2-HOUR AVERAGES"), (TrafficPeriod.Yearly, "YEARLY · 1-DAY AVERAGES"),
        })
        {
            var (pts, latest) = _grapher.Query(_target!.Id, i.Index, period);
            var s = TrafficArchive.Stats(pts, latest);
            var canvas = new Canvas { Height = 170 };
            var p = period;
            canvas.SizeChanged += (_, e) => DrawGraph(canvas, pts, p, i.SpeedBps, e.NewSize.Width, 170, compact: false);
            var stats = new TextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            void Line(string label, double a, double b, string brushKey, string suffix = "")
            {
                stats.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label, Foreground = ToolUi.Brush(brushKey) });
                stats.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"max {TrafficArchive.Bits(a)}{Util(a, i.SpeedBps)}  avg {TrafficArchive.Bits(b)}{suffix}\n" });
            }
            Line("In   ", s.InMax, s.InAvg, "OmlMintBrush", $"  now {TrafficArchive.Bits(s.InNow)}  95th {TrafficArchive.Bits(s.In95)}  total {TrafficArchive.Bytes(s.InBytes)}");
            Line("Out  ", s.OutMax, s.OutAvg, "OmlSkyBrush", $"  now {TrafficArchive.Bits(s.OutNow)}  95th {TrafficArchive.Bits(s.Out95)}  total {TrafficArchive.Bytes(s.OutBytes)}");
            if (s.ErrorsTotal > 0 || s.DiscardsTotal > 0)
                stats.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"Errors {s.ErrorsTotal:N0} · discards {s.DiscardsTotal:N0} in this period", Foreground = ToolUi.Brush("OmlAmberBrush") });
            DetailPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["ToolCardStyle"],
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionLabelStyle"] },
                        canvas,
                        pts.Count == 0 ? new TextBlock { Text = "No data for this period yet.", Opacity = 0.6 } : stats,
                    },
                },
            });
        }
    }

    private static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFont"];

    private static string Speed(long bps) => bps <= 0 ? "" : TrafficArchive.Bits(bps).Replace("/s", "");
    private static string Util(double bps, long speed) => speed > 0 ? $" ({100 * bps / speed:0}%)" : "";

    /// <summary>MRTG's look: incoming as a filled area, outgoing as a line, the busiest sample's level marked, time along the bottom.</summary>
    private static void DrawGraph(Canvas c, IReadOnlyList<TrafficPoint> pts, TrafficPeriod period, long speedBps, double w, double h, bool compact)
    {
        c.Children.Clear();
        if (w < 80) return;
        double left = compact ? 0 : 64, bottom = compact ? 0 : 18;
        double plotW = w - left, plotH = h - bottom;
        var end = DateTime.Now;
        var start = end - TrafficArchive.Span(period);
        double X(DateTime t) => left + plotW * (t - start).TotalSeconds / (end - start).TotalSeconds;
        double peak = pts.Count == 0 ? 1000 : pts.Max(p => Math.Max(p.In, p.Out));
        double max = NiceCeiling(Math.Max(peak * 1.1, 1000));
        double Y(double v) => plotH - plotH * Math.Min(v, max) / max;

        var axis = ToolUi.Brush("HairlineBrush");
        for (int k = 0; k <= 4; k++)
        {
            double y = plotH * k / 4.0;
            c.Children.Add(new Line { X1 = left, X2 = w, Y1 = y, Y2 = y, Stroke = axis, StrokeThickness = 1, Opacity = k == 4 ? 1 : 0.6 });
            if (!compact)
            {
                var t = new TextBlock { Text = TrafficArchive.Bits(max * (4 - k) / 4.0), FontSize = 10.5, Opacity = 0.55 };
                Canvas.SetTop(t, y - 7);
                c.Children.Add(t);
            }
        }
        if (!compact)
        {
            // time ticks: hours for daily, days for weekly/monthly, months for yearly
            var ticks = new List<(DateTime At, string Label)>();
            switch (period)
            {
                case TrafficPeriod.Live:
                    for (var t = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute / 5 * 5, 0).AddMinutes(5); t < end; t = t.AddMinutes(5)) ticks.Add((t, t.ToString("HH:mm")));
                    break;
                case TrafficPeriod.Daily:
                    for (var t = start.Date.AddHours(start.Hour + 1); t < end; t = t.AddHours(1)) if (t.Hour % 3 == 0) ticks.Add((t, t.ToString("HH:00")));
                    break;
                case TrafficPeriod.Weekly:
                    for (var t = start.Date.AddDays(1); t < end; t = t.AddDays(1)) ticks.Add((t, t.ToString("ddd")));
                    break;
                case TrafficPeriod.Monthly:
                    for (var t = start.Date.AddDays(1); t < end; t = t.AddDays(1)) if (t.DayOfWeek == DayOfWeek.Monday) ticks.Add((t, t.ToString("dd MMM")));
                    break;
                default:
                    for (var t = new DateTime(start.Year, start.Month, 1).AddMonths(1); t < end; t = t.AddMonths(1)) ticks.Add((t, t.ToString("MMM")));
                    break;
            }
            foreach (var (at, label) in ticks)
            {
                double x = X(at);
                c.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = plotH, Stroke = axis, Opacity = 0.35 });
                var t = new TextBlock { Text = label, FontSize = 10.5, Opacity = 0.55 };
                Canvas.SetLeft(t, x - 14);
                Canvas.SetTop(t, plotH + 2);
                c.Children.Add(t);
            }
        }
        if (pts.Count == 0) return;

        // Gaps (app closed, device unreachable) stay empty rather than being bridged.
        double gap = period switch { TrafficPeriod.Live or TrafficPeriod.Daily => 600, TrafficPeriod.Weekly => 3 * 1800, TrafficPeriod.Monthly => 3 * 7200, _ => 3 * 86400 };
        var inBrush = ToolUi.Brush("OmlMintBrush");
        var outBrush = ToolUi.Brush("OmlSkyBrush");
        foreach (var run in Runs(pts, gap))
        {
            var area = new Polygon { Fill = inBrush, Opacity = 0.45 };
            area.Points.Add(new Point(X(run[0].At), plotH));
            foreach (var p in run) area.Points.Add(new Point(X(p.At), Y(p.In)));
            area.Points.Add(new Point(X(run[^1].At), plotH));
            c.Children.Add(area);
            var inLine = new Polyline { Stroke = inBrush, StrokeThickness = 1.2 };
            var outLine = new Polyline { Stroke = outBrush, StrokeThickness = 1.8 };
            foreach (var p in run)
            {
                inLine.Points.Add(new Point(X(p.At), Y(p.In)));
                outLine.Points.Add(new Point(X(p.At), Y(p.Out)));
            }
            c.Children.Add(inLine);
            c.Children.Add(outLine);
        }
        if (speedBps > 0 && speedBps <= max)
        {
            double y = Y(speedBps);
            c.Children.Add(new Line { X1 = left, X2 = w, Y1 = y, Y2 = y, Stroke = ToolUi.Brush("OmlRoseBrush"), StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.7 });
        }
    }

    private static List<List<TrafficPoint>> Runs(IReadOnlyList<TrafficPoint> pts, double gapSeconds)
    {
        var runs = new List<List<TrafficPoint>>();
        foreach (var p in pts)
        {
            if (runs.Count == 0 || p.Time - runs[^1][^1].Time > gapSeconds) runs.Add([]);
            runs[^1].Add(p);
        }
        return runs;
    }

    private static double NiceCeiling(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 4, 5, 8, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }

    // ---------- add / edit ----------

    private async void AddDevice_Click(object sender, RoutedEventArgs e) => await EditAsync(null);
    private async void EditDevice_Click(object sender, RoutedEventArgs e) { if (_target is not null) await EditAsync(_target); }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Remove {_target.Name}?", Content = "Its traffic history is deleted too.",
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        _grapher.Remove(_target.Id);
        _target = null;
        _iface = null;
        RefreshDevices();
        Redraw();
    }

    private async Task EditAsync(TrafficTarget? existing)
    {
        var t = existing is null ? new TrafficTarget() : System.Text.Json.JsonSerializer.Deserialize<TrafficTarget>(System.Text.Json.JsonSerializer.Serialize(existing))!;
        var mono = Mono;
        var sessionBox = new ComboBox { Header = "Fill from a saved session (optional)", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Display", ItemsSource = _ctx.Sessions().Where(s => s.Host.Length > 0).ToList() };
        var name = new TextBox { Header = "Name", Text = t.Name };
        var host = new TextBox { Header = "Address", Text = t.Host, FontFamily = mono };
        var port = new NumberBox { Header = "Port", Value = t.Port, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, Width = 90 };
        var version = new ComboBox { Header = "Version", ItemsSource = new[] { "v1", "v2c", "v3" }, SelectedIndex = (int)t.Credentials.Version, Width = 90 };
        var community = new PasswordBox { Header = "Community", Password = t.Credentials.Community };
        var user = new TextBox { Header = "v3 user", Text = t.Credentials.User };
        var auth = new ComboBox { Header = "Auth", ItemsSource = Enum.GetNames<SnmpAuth>(), SelectedIndex = (int)t.Credentials.Auth, HorizontalAlignment = HorizontalAlignment.Stretch };
        var authPw = new PasswordBox { Header = "Auth password", Password = t.Credentials.AuthPassword };
        var priv = new ComboBox { Header = "Privacy", ItemsSource = Enum.GetNames<SnmpPriv>(), SelectedIndex = (int)t.Credentials.Priv, HorizontalAlignment = HorizontalAlignment.Stretch };
        var privPw = new PasswordBox { Header = "Privacy password", Password = t.Credentials.PrivPassword };
        var poll = new NumberBox { Header = "Poll every (s)", Value = t.PollSeconds, Minimum = 10, Maximum = 3600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var alert = new NumberBox { Header = "Alert at (% of speed, 0 = off)", Value = t.AlertPercent, Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var ifList = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 220, BorderBrush = ToolUi.Brush("HairlineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        var discoverStatus = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Text = t.Interfaces.Count > 0 ? $"{t.Interfaces.Count} interface(s) graphed. Discover to change the selection." : "Press Discover to list the device's interfaces." };
        var discover = new Button { Content = "Discover interfaces" };
        var found = new List<SnmpInterface>();

        void ShowV3()
        {
            bool v3 = version.SelectedIndex == 2;
            community.Visibility = v3 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var c in new FrameworkElement[] { user, auth, authPw, priv, privPw }) c.Visibility = v3 ? Visibility.Visible : Visibility.Collapsed;
        }
        version.SelectionChanged += (_, _) => ShowV3();
        ShowV3();
        sessionBox.SelectionChanged += (_, _) =>
        {
            if (sessionBox.SelectedItem is OmlTerminal.Core.Models.SessionProfile s) { name.Text = s.Name; host.Text = s.Host; }
        };

        SnmpCredentials Creds() => new()
        {
            Version = (SnmpVersion)Math.Max(0, version.SelectedIndex), Community = community.Password, User = user.Text.Trim(),
            Auth = (SnmpAuth)Math.Max(0, auth.SelectedIndex), AuthPassword = authPw.Password, Priv = (SnmpPriv)Math.Max(0, priv.SelectedIndex), PrivPassword = privPw.Password,
        };

        discover.Click += async (_, _) =>
        {
            discover.IsEnabled = false;
            discoverStatus.Text = $"Asking {host.Text.Trim()}…";
            try
            {
                var h = host.Text.Trim();
                int p = double.IsNaN(port.Value) ? 161 : (int)port.Value;
                var creds = Creds();
                var (sys, ifs) = await Task.Run(async () =>
                {
                    using var c = await SnmpClient.ConnectAsync(h, p, creds, 3000, 1);
                    return (await IfMib.SystemAsync(c, CancellationToken.None), await IfMib.InterfacesAsync(c, CancellationToken.None));
                });
                found = ifs.ToList();
                if (name.Text.Trim().Length == 0) name.Text = sys.Name;
                var items = found.Select(i => $"{i.Name,-24} {i.StatusText,-11} {Speed(i.SpeedBps),-9} {i.Alias}").ToList();
                ifList.ItemsSource = items;
                ifList.FontFamily = mono;
                var keep = t.Interfaces.Select(x => x.Index).ToHashSet();
                for (int k = 0; k < found.Count; k++)
                    if (keep.Count > 0 ? keep.Contains(found[k].Index) : found[k].Up && found[k].Interesting)
                        ifList.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(k, 1));
                discoverStatus.Text = $"{sys.Name}: {found.Count} interfaces, {found.Count(i => i.Up)} up. Pick the ones to graph (up ports are pre-selected).";
            }
            catch (Exception ex) { discoverStatus.Text = $"Couldn't reach it: {ex.Message}"; }
            finally { discover.IsEnabled = true; }
        };

        var row1 = new Grid { ColumnSpacing = 8 };
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.Children.Add(host);
        Grid.SetColumn(port, 1); row1.Children.Add(port);
        Grid.SetColumn(version, 2); row1.Children.Add(version);
        var v3a = new Grid { ColumnSpacing = 8 };
        v3a.ColumnDefinitions.Add(new ColumnDefinition()); v3a.ColumnDefinitions.Add(new ColumnDefinition());
        v3a.Children.Add(auth); Grid.SetColumn(authPw, 1); v3a.Children.Add(authPw);
        var v3b = new Grid { ColumnSpacing = 8 };
        v3b.ColumnDefinitions.Add(new ColumnDefinition()); v3b.ColumnDefinitions.Add(new ColumnDefinition());
        v3b.Children.Add(priv); Grid.SetColumn(privPw, 1); v3b.Children.Add(privPw);
        var row3 = new Grid { ColumnSpacing = 8 };
        row3.ColumnDefinitions.Add(new ColumnDefinition()); row3.ColumnDefinitions.Add(new ColumnDefinition());
        row3.Children.Add(poll); Grid.SetColumn(alert, 1); row3.Children.Add(alert);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = existing is null ? "Add a device to graph" : $"Edit {existing.Name}",
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                MaxHeight = 640,
                Content = new StackPanel
                {
                    Spacing = 10, Width = 520,
                    Children = { sessionBox, name, row1, community, user, v3a, v3b, row3, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { discover } }, discoverStatus, ifList },
                },
            },
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string? problem = host.Text.Trim().Length == 0 ? "Enter the device's address."
                : found.Count == 0 && t.Interfaces.Count == 0 ? "Discover the interfaces first, then pick the ones to graph."
                : found.Count > 0 && ifList.SelectedItems.Count == 0 ? "Pick at least one interface." : null;
            if (problem is null) break;
            discoverStatus.Text = problem;
        }
        t.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : host.Text.Trim();
        t.Host = host.Text.Trim();
        t.Port = double.IsNaN(port.Value) ? 161 : (int)port.Value;
        t.Credentials = Creds();
        t.PollSeconds = double.IsNaN(poll.Value) ? 60 : (int)poll.Value;
        t.AlertPercent = double.IsNaN(alert.Value) ? 90 : alert.Value;
        if (found.Count > 0)
        {
            var picked = ifList.SelectedRanges.SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length)).Select(k => found[k]);
            t.Interfaces = picked.Select(i => new TrafficInterface { Index = i.Index, Name = i.Name, Alias = i.Alias, SpeedBps = i.SpeedBps }).ToList();
        }
        _grapher.AddOrUpdate(t);
        _target = t;
        _iface = null;
        RefreshDevices();
        Redraw();
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;
        var sb = new StringBuilder("interface,period,time,in_bps,out_bps,in_peak_bps,out_peak_bps,errors_per_s,discards_per_s\n");
        foreach (var i in _iface is null ? _target.Interfaces : [_iface])
            foreach (var period in new[] { TrafficPeriod.Daily, TrafficPeriod.Weekly, TrafficPeriod.Monthly, TrafficPeriod.Yearly })
                foreach (var p in _grapher.Query(_target.Id, i.Index, period).Points)
                    sb.AppendLine(ToolUi.Csv(i.Display, period.ToString(), p.At.ToString("yyyy-MM-dd HH:mm:ss"), $"{p.In:0}", $"{p.Out:0}", $"{p.InMax:0}", $"{p.OutMax:0}", $"{p.Errors:0.###}", $"{p.Discards:0.###}"));
        var path = await ToolUi.SaveTextAsync($"traffic-{_target.Name}-{DateTime.Now:yyyyMMdd}", sb.ToString(), ".csv", "CSV");
        if (path is not null) HeadDetail.Text = $"Saved {path}";
    }
}
