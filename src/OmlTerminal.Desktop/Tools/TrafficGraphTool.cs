using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Snmp;
using OmlTerminal.Desktop.Controls;

namespace OmlTerminal.Desktop.Tools;

public sealed class TrafficGraphTool : UserControl, IToolView
{
    private sealed record DeviceRow(TrafficTarget Target, string Name, string Detail, IBrush Brush);

    private readonly ToolContext _ctx;
    private readonly TrafficGrapher _grapher = TrafficGrapher.Shared;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    private TrafficTarget? _target;
    private TrafficInterface? _iface;
    private bool _refreshingList;

    private readonly ListBox _devices = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly TextBlock _headTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _headDetail = new() { FontSize = 12, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _overview = new();
    private readonly StackPanel _detail = new() { Spacing = 12, IsVisible = false };
    private readonly TextBlock _empty = Ui.Text("No devices yet. Add a router, switch or firewall with SNMP enabled - its interfaces are polled in the background while the app is open, and graphs build up over days, weeks and months.", 13, color: Ui.Muted);
    private readonly Button _back;
    private readonly Border _alertBar;
    private readonly TextBlock _alertText;

    public TrafficGraphTool(ToolContext ctx)
    {
        _ctx = ctx;
        _alertBar = Ui.Banner(out _alertText, Ui.Amber);
        _back = Ui.Button("← All interfaces", () => { _iface = null; Redraw(); });
        _back.IsVisible = false;
        _devices.ItemTemplate = new FuncDataTemplate<DeviceRow>((r, _) => r is null ? new Panel() : new DockPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9, Fill = r.Brush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0), [DockPanel.DockProperty] = Dock.Left },
                Ui.Stack(1, new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = r.Detail, FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }),
            },
        });
        _devices.SelectionChanged += (_, _) =>
        {
            if (_refreshingList || _devices.SelectedItem is not DeviceRow r) return;
            _target = r.Target;
            _iface = null;
            Redraw();
        };

        var deviceCard = Ui.Card(new DockPanel
        {
            Children =
            {
                Ui.Stack(8, [Ui.Section("Devices"), Ui.Row(Ui.Button("Add device...", () => _ = EditAsync(null), accent: true))]) is var head ? WithDock(head, Dock.Top) : null!,
                WithDock(Ui.Row(Ui.Button("Edit", () => { if (_target is not null) _ = EditAsync(_target); }),
                                Ui.Button("Remove", () => _ = RemoveAsync()),
                                Ui.Button("Poll now", () => { if (_target is { } t) _ = Task.Run(() => _grapher.PollAsync(t, CancellationToken.None)); })), Dock.Bottom),
                new ScrollViewer { Content = _devices, Margin = new Thickness(0, 8) },
            },
        });
        var header = new DockPanel
        {
            Children =
            {
                WithDock(Ui.Row(Ui.Button("Export CSV...", () => _ = ExportAsync())), Dock.Right),
                WithDock(_back, Dock.Left),
                Ui.Stack(1, _headTitle, _headDetail),
            },
        };
        _back.Margin = new Thickness(0, 0, 12, 0);
        var graphs = new DockPanel
        {
            Children =
            {
                WithDock(header, Dock.Top),
                new ScrollViewer { Content = Ui.Stack(0, _empty, _overview, _detail), Margin = new Thickness(0, 12, 0, 0) },
            },
        };
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("290,*"), ColumnSpacing = 16 };
        body.Children.Add(deviceCard);
        Grid.SetColumn(graphs, 1);
        body.Children.Add(graphs);

        Content = Ui.Page("Traffic Graphs (MRTG)",
            "SNMP v1/v2c/v3 interface traffic with daily, weekly, monthly and yearly graphs, 95th percentile and utilisation alerts. Polling runs in the background while the app is open.",
            body, _alertBar);

        _refresh.Tick += (_, _) => Redraw();
        _grapher.Polled += OnPolled;
        _grapher.Alert += OnAlert;
        AttachedToVisualTree += (_, _) => { _grapher.Start(); RefreshDevices(); _refresh.Start(); };
        DetachedFromVisualTree += (_, _) => _refresh.Stop();
    }

    private static T WithDock<T>(T c, Dock dock) where T : Control { DockPanel.SetDock(c, dock); return c; }

    public void Shutdown()
    {
        _refresh.Stop();
        _grapher.Polled -= OnPolled;
        _grapher.Alert -= OnAlert;
        _grapher.Flush();
    }

    private void OnPolled(Guid id) => Dispatcher.UIThread.Post(() =>
    {
        RefreshDevices();
        if (_target?.Id == id) Redraw();
    });

    private void OnAlert(TrafficAlert a) => Dispatcher.UIThread.Post(() =>
        Ui.Show(_alertBar, _alertText, $"{a.Device} {a.Interface} is {a.Percent:0}% busy ({a.Direction}) - {TrafficArchive.Bits(a.Bps)} at {a.At:HH:mm:ss}."));

    // ---------- device list ----------

    private void RefreshDevices()
    {
        _refreshingList = true;
        var rows = _grapher.Targets.Select(t =>
        {
            var st = _grapher.Status(t.Id);
            IBrush brush = !t.Enabled ? Ui.Muted : st.Error is not null ? Ui.Rose : st.LastPoll is null ? Ui.Muted : Ui.Mint;
            var detail = $"{t.Host} · {t.Interfaces.Count} interface(s) · every {t.PollSeconds}s" +
                         (st.Error is not null ? $"\n{st.Error}" : st.LastPoll is { } lp ? $"\npolled {lp:HH:mm:ss}{(st.HighCapacity ? " · 64-bit counters" : "")}" : "\nwaiting for the first poll");
            return new DeviceRow(t, t.Name.Length > 0 ? t.Name : t.Host, detail, brush);
        }).ToList();
        _devices.ItemsSource = rows;
        _devices.SelectedItem = rows.FirstOrDefault(r => r.Target.Id == _target?.Id);
        _refreshingList = false;
        if (_target is null && rows.Count > 0) _devices.SelectedIndex = 0;
        _empty.IsVisible = rows.Count == 0;
    }

    // ---------- graphs ----------

    private void Redraw()
    {
        if (_target is null) { _headTitle.Text = ""; _headDetail.Text = ""; _overview.Children.Clear(); _detail.Children.Clear(); return; }
        var st = _grapher.Status(_target.Id);
        _headTitle.Text = _iface is null ? _target.Name : $"{_target.Name} · {_iface.Display}";
        _headDetail.Text = st.System is { } sys
            ? $"{sys.Name} · up {(int)sys.Uptime.TotalDays}d {sys.Uptime.Hours}h · {FirstLine(sys.Description)}{(sys.Location.Length > 0 ? $" · {sys.Location}" : "")}"
            : st.Error ?? $"{_target.Host}:{_target.Port} · SNMP {_target.Credentials.Version}";
        _back.IsVisible = _iface is not null;
        _overview.IsVisible = _iface is null;
        _detail.IsVisible = _iface is not null;
        if (_iface is null) DrawOverview(); else DrawDetail(_iface);
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private static string Speed(long bps) => bps <= 0 ? "" : TrafficArchive.Bits(bps).Replace("/s", "");
    private static string Util(double bps, long speed) => speed > 0 ? $" ({100 * bps / speed:0}%)" : "";

    private void DrawOverview()
    {
        _overview.Children.Clear();
        foreach (var i in _target!.Interfaces)
        {
            var (pts, latest) = _grapher.Query(_target.Id, i.Index, TrafficPeriod.Daily);
            var s = TrafficArchive.Stats(pts, latest);
            var chart = new TrafficChart { Compact = true, Height = 120 };
            chart.Set(pts, TrafficPeriod.Daily, i.SpeedBps);
            var head = new DockPanel
            {
                Children =
                {
                    WithDock(new TextBlock { Text = Speed(i.SpeedBps), Foreground = Ui.Muted, FontSize = 12 }, Dock.Right),
                    new TextBlock { Text = i.Display, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
            var now = new TextBlock { FontFamily = Ui.Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Inlines = new InlineCollection
            {
                new Run($"in {TrafficArchive.Bits(s.InNow)} ") { Foreground = Ui.Mint },
                new Run($"out {TrafficArchive.Bits(s.OutNow)} ") { Foreground = Ui.Sky },
                new Run($"· peak {TrafficArchive.Bits(Math.Max(s.InMax, s.OutMax))}{Util(Math.Max(s.InMax, s.OutMax), i.SpeedBps)}"),
            } };
            var card = new Border
            {
                Width = 360, Height = 202, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 12, 12),
                Background = Ui.CardBack, BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand),
                Child = Ui.Stack(6, head, chart, now),
            };
            ToolTip.SetTip(card, "Daily graph · click for weekly, monthly and yearly");
            var iface = i;
            card.PointerPressed += (_, _) => { _iface = iface; Redraw(); };
            _overview.Children.Add(card);
        }
    }

    private void DrawDetail(TrafficInterface i)
    {
        _detail.Children.Clear();
        foreach (var (period, title) in new[]
        {
            (TrafficPeriod.Live, "Live · last 15 minutes"), (TrafficPeriod.Daily, "Daily · each poll"), (TrafficPeriod.Weekly, "Weekly · 30-minute averages"),
            (TrafficPeriod.Monthly, "Monthly · 2-hour averages"), (TrafficPeriod.Yearly, "Yearly · 1-day averages"),
        })
        {
            var (pts, latest) = _grapher.Query(_target!.Id, i.Index, period);
            var s = TrafficArchive.Stats(pts, latest);
            var chart = new TrafficChart { Height = 170 };
            chart.Set(pts, period, i.SpeedBps);
            var stats = new SelectableTextBlock { FontFamily = Ui.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Inlines = new InlineCollection() };
            void Line(string label, double a, double b, IBrush brush, string suffix)
            {
                stats.Inlines!.Add(new Run(label) { Foreground = brush });
                stats.Inlines.Add(new Run($"max {TrafficArchive.Bits(a)}{Util(a, i.SpeedBps)}  avg {TrafficArchive.Bits(b)}{suffix}\n"));
            }
            Line("In   ", s.InMax, s.InAvg, Ui.Mint, $"  now {TrafficArchive.Bits(s.InNow)}  95th {TrafficArchive.Bits(s.In95)}  total {TrafficArchive.Bytes(s.InBytes)}");
            Line("Out  ", s.OutMax, s.OutAvg, Ui.Sky, $"  now {TrafficArchive.Bits(s.OutNow)}  95th {TrafficArchive.Bits(s.Out95)}  total {TrafficArchive.Bytes(s.OutBytes)}");
            if (s.ErrorsTotal > 0 || s.DiscardsTotal > 0)
                stats.Inlines!.Add(new Run($"Errors {s.ErrorsTotal:N0} · discards {s.DiscardsTotal:N0} in this period") { Foreground = Ui.Amber });
            _detail.Children.Add(Ui.Card(Ui.Stack(8, Ui.Section(title), chart,
                pts.Count == 0 ? Ui.Text("No data for this period yet.", color: Ui.Muted) : stats)));
        }
    }

    // ---------- add / edit ----------

    private async Task RemoveAsync()
    {
        if (_target is null || MainWindow.Current is not { } owner) return;
        if (!await Dialogs.ConfirmAsync(owner, $"Remove {_target.Name}?", "Its traffic history is deleted too.", "Remove")) return;
        _grapher.Remove(_target.Id);
        _target = null;
        _iface = null;
        RefreshDevices();
        Redraw();
    }

    private async Task EditAsync(TrafficTarget? existing)
    {
        if (MainWindow.Current is not { } owner) return;
        var t = existing is null ? new TrafficTarget() : JsonSerializer.Deserialize<TrafficTarget>(JsonSerializer.Serialize(existing))!;
        var saved = await new TrafficDeviceWindow(t, existing is null, _ctx.Sessions()).ShowDialog<bool>(owner);
        if (!saved) return;
        _grapher.AddOrUpdate(t);
        _target = t;
        _iface = null;
        RefreshDevices();
        Redraw();
    }

    private async Task ExportAsync()
    {
        if (_target is null) return;
        var sb = new StringBuilder("interface,period,time,in_bps,out_bps,in_peak_bps,out_peak_bps,errors_per_s,discards_per_s\n");
        foreach (var i in _iface is null ? _target.Interfaces : [_iface])
            foreach (var period in new[] { TrafficPeriod.Daily, TrafficPeriod.Weekly, TrafficPeriod.Monthly, TrafficPeriod.Yearly })
                foreach (var p in _grapher.Query(_target.Id, i.Index, period).Points)
                    sb.AppendLine(ToolUi.Csv(i.Display, period.ToString(), p.At.ToString("yyyy-MM-dd HH:mm:ss"), $"{p.In:0}", $"{p.Out:0}", $"{p.InMax:0}", $"{p.OutMax:0}", $"{p.Errors:0.###}", $"{p.Discards:0.###}"));
        var path = await ToolUi.SaveTextAsync($"traffic-{_target.Name}-{DateTime.Now:yyyyMMdd}", sb.ToString(), ".csv", "CSV");
        if (path is not null) _headDetail.Text = $"Saved {path}";
    }
}

/// <summary>Add or edit a graphed device: address, SNMP credentials (v1/v2c community or v3 user), and which interfaces to
/// graph, discovered from the device itself. Closes with true when saved (the target passed in is updated).</summary>
public sealed class TrafficDeviceWindow : Window
{
    public TrafficDeviceWindow(TrafficTarget t, bool isNew, IReadOnlyList<SessionProfile> sessions)
    {
        Title = isNew ? "Add a device to graph" : $"Edit {t.Name}";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var sessionBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = sessions.Where(s => s.Host.Length > 0).ToList(),
            ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
        var name = Ui.Input(t.Name);
        var host = Ui.Input(t.Host, "10.0.0.1", mono: true);
        var port = Ui.Number(t.Port, 1, 65535);
        var version = Ui.Combo(new[] { "v1", "v2c", "v3" }, (int)t.Credentials.Version);
        var community = new TextBox { Text = t.Credentials.Community, PasswordChar = '•' };
        var user = Ui.Input(t.Credentials.User);
        var auth = Ui.Combo(Enum.GetNames<SnmpAuth>(), (int)t.Credentials.Auth);
        var authPw = new TextBox { Text = t.Credentials.AuthPassword, PasswordChar = '•' };
        var priv = Ui.Combo(Enum.GetNames<SnmpPriv>(), (int)t.Credentials.Priv);
        var privPw = new TextBox { Text = t.Credentials.PrivPassword, PasswordChar = '•' };
        var poll = Ui.Number(t.PollSeconds, 10, 3600, 10);
        var alert = Ui.Number(t.AlertPercent, 0, 100, 5);
        var ifList = new ListBox { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, Height = 220, Background = Brushes.Transparent, FontFamily = Ui.Mono, FontSize = 12,
            BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1) };
        var status = Ui.Text(t.Interfaces.Count > 0 ? $"{t.Interfaces.Count} interface(s) graphed. Discover to change the selection." : "Press Discover to list the device's interfaces.", 12.5, color: Ui.Muted);
        var found = new List<SnmpInterface>();

        var communityField = Ui.Field("Community", community);
        var v3 = Ui.Stack(10, Ui.Field("v3 user", user), Ui.Columns("*,*", Ui.Field("Auth", auth), Ui.Field("Auth password", authPw)),
            Ui.Columns("*,*", Ui.Field("Privacy", priv), Ui.Field("Privacy password", privPw)));
        void ShowV3() { bool isV3 = version.SelectedIndex == 2; communityField.IsVisible = !isV3; v3.IsVisible = isV3; }
        version.SelectionChanged += (_, _) => ShowV3();
        ShowV3();
        sessionBox.SelectionChanged += (_, _) => { if (sessionBox.SelectedItem is SessionProfile s) { name.Text = s.Name; host.Text = s.Host; } };

        SnmpCredentials Creds() => new()
        {
            Version = (SnmpVersion)Math.Max(0, version.SelectedIndex), Community = community.Text ?? "", User = (user.Text ?? "").Trim(),
            Auth = (SnmpAuth)Math.Max(0, auth.SelectedIndex), AuthPassword = authPw.Text ?? "", Priv = (SnmpPriv)Math.Max(0, priv.SelectedIndex), PrivPassword = privPw.Text ?? "",
        };

        Button? discover = null;
        discover = Ui.Button("Discover interfaces", async () =>
        {
            discover!.IsEnabled = false;
            var h = (host.Text ?? "").Trim();
            status.Text = $"Asking {h}…";
            try
            {
                int p = Ui.IntValue(port, 161);
                var creds = Creds();
                var (sys, ifs) = await Task.Run(async () =>
                {
                    using var c = await SnmpClient.ConnectAsync(h, p, creds, 3000, 1);
                    return (await IfMib.SystemAsync(c, CancellationToken.None), await IfMib.InterfacesAsync(c, CancellationToken.None));
                });
                found = ifs.ToList();
                if ((name.Text ?? "").Trim().Length == 0) name.Text = sys.Name;
                ifList.ItemsSource = found.Select(i => $"{i.Name,-24} {i.StatusText,-11} {(i.SpeedBps <= 0 ? "" : TrafficArchive.Bits(i.SpeedBps).Replace("/s", "")),-9} {i.Alias}").ToList();
                var keep = t.Interfaces.Select(x => x.Index).ToHashSet();
                for (int k = 0; k < found.Count; k++)
                    if (keep.Count > 0 ? keep.Contains(found[k].Index) : found[k].Up && found[k].Interesting)
                        ifList.Selection.Select(k);
                status.Text = $"{sys.Name}: {found.Count} interfaces, {found.Count(i => i.Up)} up. Pick the ones to graph (up ports are pre-selected).";
            }
            catch (Exception ex) { status.Text = $"Couldn't reach it: {ex.Message}"; }
            finally { discover.IsEnabled = true; }
        });

        var save = Ui.Button("Save", () =>
        {
            string? problem = (host.Text ?? "").Trim().Length == 0 ? "Enter the device's address."
                : found.Count == 0 && t.Interfaces.Count == 0 ? "Discover the interfaces first, then pick the ones to graph."
                : found.Count > 0 && ifList.Selection.SelectedIndexes.Count == 0 ? "Pick at least one interface." : null;
            if (problem is not null) { status.Text = problem; status.Foreground = Ui.Rose; return; }
            t.Name = (name.Text ?? "").Trim().Length > 0 ? name.Text!.Trim() : host.Text!.Trim();
            t.Host = host.Text!.Trim();
            t.Port = Ui.IntValue(port, 161);
            t.Credentials = Creds();
            t.PollSeconds = Ui.IntValue(poll, 60);
            t.AlertPercent = (double)(alert.Value ?? 90);
            if (found.Count > 0)
                t.Interfaces = ifList.Selection.SelectedIndexes.OrderBy(k => k).Select(k => found[k])
                    .Select(i => new TrafficInterface { Index = i.Index, Name = i.Name, Alias = i.Alias, SpeedBps = i.SpeedBps }).ToList();
            Close(true);
        }, accent: true);
        var cancel = Ui.Button("Cancel", () => Close(false));
        cancel.IsCancel = true;

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
                    [DockPanel.DockProperty] = Dock.Bottom, Children = { cancel, save } },
                new ScrollViewer
                {
                    Content = Ui.Stack(10,
                        Ui.Field("Fill from a saved session (optional)", sessionBox),
                        Ui.Field("Name", name),
                        Ui.Columns("*,90,90", Ui.Field("Address", host), Ui.Field("Port", port), Ui.Field("Version", version)),
                        communityField, v3,
                        Ui.Columns("*,*", Ui.Field("Poll every (s)", poll), Ui.Field("Alert at (% of speed, 0 = off)", alert)),
                        Ui.Row(discover), status, ifList),
                },
            },
        };
    }
}
