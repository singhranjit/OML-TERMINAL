using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OmlTerminal.Core.Wifi;
using OmlTerminal.Desktop.Controls;

namespace OmlTerminal.Desktop.Tools;

public sealed class WifiRow(WifiNetwork n) : LiveRow
{
    public WifiNetwork Network { get; set; } = n;
    public bool Connected { get; set; }
    public string Bssid => Network.Bssid + (Network.LocallyAdministeredBssid ? "  (virtual BSSID)" : "");
    public IBrush SignalBrush => Network.Rssi >= -67 ? Ui.Mint : Network.Rssi >= -75 ? Ui.Amber : Ui.Rose;
    public string SignalText => $"{Network.Rssi} dBm · {WifiMath.SignalWord(Network.Rssi)}";
    public string ChannelText => $"{Network.Channel}{(Network.ChannelWidth > 20 ? $" ({Network.ChannelWidth} MHz)" : "")}{(Network.Band == WifiBand.Band5 && WifiMath.IsDfs(Network.Channel) ? " DFS" : "")}";
    public string BandText => $"{Network.BandText} · {Network.FrequencyMHz} MHz";
    public IBrush? SecurityBrush => Network.Security is "Open" or "WEP" or "WPA" ? Ui.Rose
        : Network.Security.Contains("WPA3") || Network.Security.StartsWith("OWE") ? Ui.Mint : null;
    public string LoadText
    {
        get
        {
            var n = Network;
            if (n.StationCount is null && n.ChannelUtilization is null) return "—";
            var parts = new List<string>();
            if (n.StationCount is { } s) parts.Add($"{s} client{(s == 1 ? "" : "s")}");
            if (n.ChannelUtilization is { } u) parts.Add($"{u}% busy");
            return string.Join(" · ", parts);
        }
    }
}

public sealed class WifiAnalyzerTool : UserControl, IToolView
{
    private static readonly JsonSerializerOptions ScanJson = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<string, WifiNetwork> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SignalHistory _history = new();
    private readonly RoamTracker _roams = new();
    private readonly HashSet<string> _graphed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<WifiRow> _rows = new();
    private IWifiSource? _source;
    private WifiAdapter? _adapter;
    private WifiConnection? _connection;
    private bool _busy, _refreshingList, _started, _surveying, _surveyReady;

    private SurveyProject _survey = new();
    private (int W, int H) _planSize;
    private double _planZoom = 1;

    // top bar
    private readonly ComboBox _adapterBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemTemplate = new FuncDataTemplate<WifiAdapter>((a, _) => new TextBlock { Text = a?.Description }) };
    private readonly NumericUpDown _interval = Ui.Number(5, 3, 60);
    private readonly ToggleButton _scanToggle = new() { Content = "Scanning", IsChecked = true, MinWidth = 100 };
    private readonly Ellipse _connDot = new() { Width = 14, Height = 14, Fill = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connTitle = new() { Text = "Not connected", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _connDetail = new() { FontSize = 12, Foreground = Ui.Muted, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = Ui.Text("", 12.5, color: Ui.Muted);
    private readonly TabControl _tabs = new();

    // networks
    private readonly TextBox _filter = Ui.Input("", "Filter by SSID, BSSID, security, standard…");
    private readonly ComboBox _bandFilter = Ui.Combo(new[] { "All bands", "2.4 GHz", "5 GHz", "6 GHz" });
    private readonly ComboBox _sort = Ui.Combo(new[] { "Strongest first", "By SSID", "By channel" });
    private readonly DataGrid _list;
    private readonly TextBlock _netEmpty = Ui.Text("", 13, color: Ui.Muted);
    private readonly WifiSignalGraph _graph = new();

    // channels
    private readonly TextBlock _advice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
    private readonly CheckBox _dfs = Ui.Check("Allow DFS channels (52-144) for 5 GHz");
    private readonly ItemsControl _warnings = new();
    private readonly WifiSpectrum _chart24 = new() { Height = 230 }, _chart5 = new() { Height = 230 }, _chart6 = new() { Height = 230 };

    // survey
    private readonly Panel _planSurface = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Ui.Solid(0x0C0C0C) };
    private readonly Image _planImage = new() { Stretch = Stretch.Fill }, _heatImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _markers = new();
    private readonly LayoutTransformControl _planZoomHost;
    private readonly ScrollViewer _planScroll;
    private readonly StackPanel _planEmpty;
    private readonly ComboBox _metric = Ui.Combo(new[] { "Signal level", "Roaming overlap (2nd-best AP)", "Networks heard (interference)", "Signal-to-interference (SIR)" });
    private readonly ComboBox _ssidBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _target = Ui.Number(-67, -90, -40);
    private readonly TextBlock _surveyStats = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Border _legendBar = new() { Height = 12, CornerRadius = new CornerRadius(6) };
    private readonly TextBlock _legendLow = Ui.Text("", 11, color: Ui.Muted), _legendHigh = Ui.Text("", 11, color: Ui.Muted);

    // roaming
    private readonly ItemsControl _roamList = new();

    public WifiAnalyzerTool(ToolContext ctx)
    {
        _timer.Tick += async (_, _) => await TickAsync();
        _adapterBox.SelectionChanged += (_, _) => { _adapter = _adapterBox.SelectedItem as WifiAdapter; _seen.Clear(); };
        _scanToggle.IsCheckedChanged += (_, _) =>
        {
            if (_scanToggle.IsChecked == true) { _timer.Interval = Interval; _timer.Start(); _ = TickAsync(); }
            else _timer.Stop();
            _scanToggle.Content = _scanToggle.IsChecked == true ? "Scanning" : "Paused";
        };
        _interval.ValueChanged += (_, _) => _timer.Interval = Interval;

        var conn = new Border
        {
            Background = Ui.Solid(0x0F1216), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8),
            Child = new DockPanel { Children = { WithDock(_connDot, Dock.Left), new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _connTitle, _connDetail } } } },
        };
        var top = Ui.Card(Ui.Columns("300,110,Auto,*",
            Ui.Field("Wi-Fi adapter", _adapterBox), Ui.Field("Scan every (s)", _interval),
            Ui.Row(_scanToggle,
                Ui.Button("Save scan…", () => _ = SaveScanAsync(), tip: "Save this scan (JSON for replay, or CSV for Excel)"),
                Ui.Button("Open scan…", () => _ = OpenScanAsync(), tip: "Open a saved scan (review a site visit later, or analyse on a computer without Wi-Fi)")),
            conn));
        ((Grid)top.Child!).Children[2].VerticalAlignment = VerticalAlignment.Bottom;

        // ---- networks ----
        _list = Ui.Table(
            Ui.Custom<WifiRow, StackPanel>("Network", 0, () => new StackPanel { Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Children = {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                        new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = "● connected", Foreground = Ui.Mint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center } } },
                    new TextBlock { FontSize = 11, Foreground = Ui.Muted, FontFamily = Ui.Mono } } },
                (cell, r) =>
                {
                    var line = (StackPanel)cell.Children[0];
                    ((Ellipse)line.Children[0]).Fill = new SolidColorBrush(WifiColors.For(r.Network));
                    ((TextBlock)line.Children[1]).Text = r.Network.DisplaySsid;
                    line.Children[2].IsVisible = r.Connected;
                    ((TextBlock)cell.Children[1]).Text = r.Bssid;
                }),
            Ui.Custom<WifiRow, StackPanel>("Signal", 170, () => new StackPanel { Margin = new Thickness(8, 0), Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = {
                    new Panel { Children = {
                        new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.CardBorder, Width = 150, HorizontalAlignment = HorizontalAlignment.Left },
                        new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left } } },
                    new TextBlock { FontSize = 11.5, FontFamily = Ui.Mono } } },
                (cell, r) =>
                {
                    var bar = (Border)((Panel)cell.Children[0]).Children[1];
                    bar.Width = Math.Clamp((r.Network.Rssi + 95) / 65.0, 0.03, 1) * 150;
                    bar.Background = r.SignalBrush;
                    var t = (TextBlock)cell.Children[1];
                    t.Text = r.SignalText;
                    t.Foreground = r.SignalBrush;
                }),
            Ui.Custom<WifiRow, StackPanel>("Channel", 140, () => new StackPanel { Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Children = {
                    new TextBlock { FontFamily = Ui.Mono, FontSize = 12.5 }, new TextBlock { FontSize = 11, Foreground = Ui.Muted } } },
                (cell, r) => { ((TextBlock)cell.Children[0]).Text = r.ChannelText; ((TextBlock)cell.Children[1]).Text = r.BandText; }),
            Ui.Col<WifiRow>("Standard", r => r.Network.Standard, 130),
            Ui.Col<WifiRow>("Security", r => r.Network.Security, 170, r => r.SecurityBrush),
            Ui.Col<WifiRow>("Load", r => r.LoadText, 140));
        _list.RowHeight = 42;
        _list.CanUserSortColumns = false; // the Sort box decides the order
        _list.ItemsSource = _rows;
        _list.SelectionChanged += (_, e) =>
        {
            if (_refreshingList) return;
            foreach (var r in e.AddedItems.OfType<WifiRow>()) _graphed.Add(r.Network.Bssid);
            foreach (var r in e.RemovedItems.OfType<WifiRow>()) _graphed.Remove(r.Network.Bssid);
            RenderGraph();
        };
        _filter.TextChanged += (_, _) => RenderList();
        _bandFilter.SelectionChanged += (_, _) => RenderList();
        _sort.SelectionChanged += (_, _) => RenderList();
        _netEmpty.Margin = new Thickness(16, 50, 16, 16);
        _netEmpty.TextWrapping = TextWrapping.Wrap;
        _netEmpty.IsHitTestVisible = false;
        _graph.Margin = new Thickness(0, 6, 0, 0);
        var listCard = Ui.Card(new DockPanel { Children = {
            WithDock(Ui.Columns("*,140,160", _filter, _bandFilter, _sort), Dock.Top),
            new Panel { Margin = new Thickness(0, 10, 0, 0), Children = { _list, _netEmpty } } } });
        var graphCard = Ui.Card(new DockPanel { Children = {
            WithDock(Ui.Section("Signal over time · select networks above to graph them (strongest six shown by default)"), Dock.Top), _graph } });
        var networksPage = new Grid { RowDefinitions = RowDefinitions.Parse("3*,2*"), RowSpacing = 12 };
        networksPage.Children.Add(listCard);
        Grid.SetRow(graphCard, 1);
        networksPage.Children.Add(graphCard);

        // ---- channels ----
        _dfs.IsCheckedChanged += (_, _) => RenderChannels();
        _warnings.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = "⚠  " + s, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 2), Foreground = Ui.Amber });
        var channelsPage = new ScrollViewer { Content = Ui.Stack(12,
            Ui.Card(Ui.Stack(6, Ui.Section("Recommendations"), _advice, _dfs, _warnings)),
            Ui.Card(Ui.Stack(6, Ui.Section("2.4 GHz"), _chart24)),
            Ui.Card(Ui.Stack(6, Ui.Section("5 GHz"), _chart5)),
            Ui.Card(Ui.Stack(6, Ui.Section("6 GHz"), _chart6))) };

        // ---- survey ----
        RenderOptions.SetBitmapInterpolationMode(_heatImage, BitmapInterpolationMode.HighQuality);
        _planSurface.Children.Add(_planImage);
        _planSurface.Children.Add(_heatImage);
        _planSurface.Children.Add(_markers);
        _planSurface.PointerPressed += (_, e) => _ = TakeReadingAsync(e.GetPosition(_planSurface));
        _planSurface.Cursor = new Cursor(StandardCursorType.Cross);
        _planZoomHost = new LayoutTransformControl { Child = _planSurface, LayoutTransform = new ScaleTransform(1, 1) };
        _planScroll = new ScrollViewer { Content = _planZoomHost, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _planScroll.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            SetPlanZoom(_planZoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
            e.Handled = true;
        };
        _planEmpty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10, MaxWidth = 460, Children = {
            new TextBlock { Text = "Load a floor plan (PNG/JPG), then walk the site and click where you're standing - each click takes a Wi-Fi reading at that spot. Ctrl+wheel zooms.",
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Ui.Muted },
            Center(Ui.Button("Load floor plan…", () => _ = LoadPlanAsync(), accent: true)) } };
        _metric.SelectionChanged += (_, _) => { if (!_surveyReady) return; UpdateLegend(); _ = RenderSurveyAsync(); };
        _ssidBox.SelectionChanged += SsidChanged;
        _target.ValueChanged += (_, _) => { if (_surveyReady) _ = RenderSurveyAsync(); };
        ToolTip.SetTip(_target, "-67 dBm is the usual target for voice and video; -70 for data");
        var legendLabels = new DockPanel { Children = { WithDock(_legendHigh, Dock.Right), _legendLow } };
        var surveySide = Ui.Card(new ScrollViewer { Content = Ui.Stack(10,
            Ui.Section("Survey"),
            Wrap(Ui.Button("Plan…", () => _ = LoadPlanAsync(), tip: "Load a floor plan image"),
                Ui.Button("Open…", () => _ = OpenSurveyAsync(), tip: "Open a saved survey (.omlsurvey)"),
                Ui.Button("Save…", () => _ = SaveSurveyAsync(), tip: "Save the survey (.omlsurvey - plan and readings in one file)"),
                Ui.Button("Undo", Undo, tip: "Remove the last reading")),
            Ui.Field("Heatmap", _metric), Ui.Field("Network", _ssidBox), Ui.Field("Target signal (dBm)", _target),
            _surveyStats, _legendBar, legendLabels,
            Wrap(Ui.Button("Export PNG…", () => _ = ExportPngAsync()), Ui.Button("Export CSV…", () => _ = ExportCsvAsync())),
            new TextBlock { Text = "Tip: take a reading every few metres and in every room; areas nobody walked stay blank instead of being guessed.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Muted }) });
        var surveyPage = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,300"), ColumnSpacing = 12 };
        var planCard = Ui.Card(new Panel { Children = { _planScroll, _planEmpty } });
        planCard.Padding = new Thickness(0);
        surveyPage.Children.Add(planCard);
        Grid.SetColumn(surveySide, 1);
        surveyPage.Children.Add(surveySide);

        // ---- roaming ----
        _roamList.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, FontFamily = Ui.Mono, FontSize = 12.5, Margin = new Thickness(0, 3) });
        var roamPage = Ui.Card(new DockPanel { Children = {
            WithDock(Ui.Section("Connection events · roams, drops and reconnects while this tab is open"), Dock.Top),
            new ScrollViewer { Content = _roamList, Margin = new Thickness(0, 8, 0, 0) } } });

        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Networks", Content = networksPage },
            new TabItem { Header = "Channels", Content = channelsPage },
            new TabItem { Header = "Site survey", Content = surveyPage },
            new TabItem { Header = "Roaming log", Content = roamPage },
        };
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _tabs)) return;
            if (_tabs.SelectedIndex == 1) RenderChannels();
            if (_tabs.SelectedIndex == 2) _ = RenderSurveyAsync();
        };
        foreach (var t in (TabItem[])_tabs.ItemsSource) t.FontSize = 15;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.Margin = new Thickness(0, 12, 0, 0);

        Content = Ui.Page("Wi-Fi Analyzer",
            "Every nearby access point with its band, channel width, Wi-Fi generation, security and client load; live signal graphs; channel planning for 2.4, 5 and 6 GHz; roaming events on your own connection; and floor-plan site surveys with signal, roaming-overlap, interference and SIR heatmaps.",
            new Panel { Children = { _tabs, _status } },
            top);

        // Attached again on every tab switch: open the adapter once, or a replayed scan is thrown away.
        AttachedToVisualTree += (_, _) =>
        {
            if (_started) return;
            _started = true;
            _surveyReady = true;
            OpenLiveSource();
            UpdateLegend();
        };
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
    private static WrapPanel Wrap(params Control[] children)
    {
        var p = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private static Control Center(Control c) { c.HorizontalAlignment = HorizontalAlignment.Center; return c; }
    private TimeSpan Interval => TimeSpan.FromSeconds(Ui.IntValue(_interval, 5));

    // ---------- sources ----------

    private void OpenLiveSource()
    {
        try
        {
            var live = WifiSources.CreateLocal();
            var adapters = live.Adapters();
            if (adapters.Count == 0)
            {
                live.Dispose();
                ShowUnavailable("No Wi-Fi adapter found on this computer. You can still open a saved scan or survey.");
                return;
            }
            UseSource(live, adapters);
        }
        catch (WifiUnavailableException ex) { ShowUnavailable(ex.Message + " You can still open a saved scan or survey."); }
        catch (Exception ex) { ShowUnavailable($"Wi-Fi scanning isn't available: {ex.Message}. You can still open a saved scan or survey."); }
    }

    private void ShowUnavailable(string message)
    {
        _status.Text = "Wi-Fi unavailable";
        _netEmpty.Text = message;
        _netEmpty.IsVisible = true;
        _scanToggle.IsEnabled = false;
    }

    private void UseSource(IWifiSource source, IReadOnlyList<WifiAdapter> adapters)
    {
        _timer.Stop();
        _source?.Dispose();
        _source = source;
        _seen.Clear();
        _adapterBox.ItemsSource = adapters;
        _adapterBox.SelectedIndex = 0;
        _adapter = adapters[0];
        _scanToggle.IsEnabled = true;
        _scanToggle.IsChecked = true;
        _netEmpty.IsVisible = false;
        _source.RequestScan(adapters[0].Id);
        _ = TickAsync();
        _timer.Interval = Interval;
        _timer.Start();
    }

    private async Task TickAsync()
    {
        if (_busy || _source is null || _adapter is null) return;
        _busy = true;
        try
        {
            var src = _source;
            var id = _adapter.Id;
            var (nets, conn) = await Task.Run(() =>
            {
                var n = src.GetNetworks(id);
                var c = src.CurrentConnection(id);
                src.RequestScan(id);
                return (n, c);
            });
            var now = DateTime.Now;
            foreach (var n in nets) _seen[n.Bssid] = n;
            foreach (var stale in _seen.Values.Where(n => now - n.LastSeen > TimeSpan.FromSeconds(45)).Select(n => n.Bssid).ToList()) _seen.Remove(stale);
            _history.Add(nets, now);
            _connection = conn;
            _roams.Update(conn, now);
            Render();
        }
        catch (WifiUnavailableException ex) { _timer.Stop(); ShowUnavailable(ex.Message); }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; }
    }

    // ---------- rendering ----------

    private IReadOnlyList<WifiNetwork> Current => _seen.Values.ToList();

    private void Render()
    {
        var all = Current;
        int bands = all.Select(n => n.Band).Distinct().Count();
        _status.Text = $"{all.Count} access point radios · {all.Select(n => n.Ssid).Distinct().Count()} networks · {bands} band(s) · updated {DateTime.Now:HH:mm:ss}";
        RenderConnection();
        RenderList();
        RenderGraph();
        if (_tabs.SelectedIndex == 1) RenderChannels();
        _roamList.ItemsSource = _roams.Events.ToList();
    }

    private void RenderConnection()
    {
        if (_connection is not { } c)
        {
            _connTitle.Text = "Not connected";
            _connDetail.Text = "";
            _connDot.Fill = Ui.Muted;
            return;
        }
        var ap = _seen.GetValueOrDefault(c.Bssid);
        _connTitle.Text = $"{c.Ssid}  ·  {c.Rssi} dBm {WifiMath.SignalWord(c.Rssi)}";
        _connDetail.Text = $"{c.Bssid}{(ap is null ? "" : $" · ch {ap.Channel} {ap.BandText} {ap.ChannelWidth} MHz · {ap.Standard}")}" +
                           (c.RxRateMbps > 0 || c.TxRateMbps > 0 ? $" · rx {c.RxRateMbps} / tx {c.TxRateMbps} Mbps" : "");
        _connDot.Fill = c.Rssi >= -67 ? Ui.Mint : c.Rssi >= -75 ? Ui.Amber : Ui.Rose;
    }

    /// <summary>Updates the table in place (rows keep their identity), so selection and scroll survive each scan.</summary>
    private void RenderList()
    {
        IEnumerable<WifiNetwork> q = Current;
        var band = _bandFilter.SelectedIndex switch { 1 => WifiBand.Band2_4, 2 => WifiBand.Band5, 3 => WifiBand.Band6, _ => (WifiBand?)null };
        if (band is { } b) q = q.Where(n => n.Band == b);
        var text = (_filter.Text ?? "").Trim();
        if (text.Length > 0)
            q = q.Where(n => n.Ssid.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Bssid.Contains(text, StringComparison.OrdinalIgnoreCase)
                             || n.Security.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Standard.Contains(text, StringComparison.OrdinalIgnoreCase));
        q = _sort.SelectedIndex switch
        {
            1 => q.OrderBy(n => n.Hidden).ThenBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase).ThenByDescending(n => n.Rssi),
            2 => q.OrderBy(n => n.Band).ThenBy(n => n.Channel).ThenByDescending(n => n.Rssi),
            _ => q.OrderByDescending(n => n.Rssi),
        };
        var wanted = q.ToList();
        _refreshingList = true;
        try
        {
            var byBssid = _rows.ToDictionary(r => r.Network.Bssid, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < wanted.Count; i++)
            {
                var n = wanted[i];
                if (byBssid.TryGetValue(n.Bssid, out var row))
                {
                    int at = _rows.IndexOf(row);
                    if (at != i) _rows.Move(at, i);
                    row.Network = n;
                }
                else _rows.Insert(i, row = new WifiRow(n));
                row.Connected = string.Equals(n.Bssid, _connection?.Bssid, StringComparison.OrdinalIgnoreCase);
                row.Touch();
            }
            while (_rows.Count > wanted.Count) _rows.RemoveAt(_rows.Count - 1);
            foreach (var r in _rows)
            {
                bool want = _graphed.Contains(r.Network.Bssid);
                if (want && !_list.SelectedItems.Contains(r)) _list.SelectedItems.Add(r);
                else if (!want && _list.SelectedItems.Contains(r)) _list.SelectedItems.Remove(r);
            }
        }
        finally { _refreshingList = false; }
        _netEmpty.IsVisible = wanted.Count == 0 && _source is not null;
        if (_netEmpty.IsVisible) _netEmpty.Text = text.Length > 0 || band is not null ? "No networks match the filter." : "Scanning…";
    }

    private void RenderGraph()
    {
        var targets = _graphed.Count > 0
            ? Current.Where(n => _graphed.Contains(n.Bssid))
            : Current.OrderByDescending(n => n.Rssi).Take(6);
        _graph.SetSeries(targets.Select(n => (n, _history.For(n.Bssid))).ToList());
    }

    private void RenderChannels()
    {
        var all = Current;
        var mine = _connection?.Ssid;
        var advice = new List<string>();
        foreach (var band in new[] { WifiBand.Band2_4, WifiBand.Band5, WifiBand.Band6 })
        {
            if (!all.Any(n => n.Band == band) && band == WifiBand.Band6) continue;
            var a = ChannelPlanner.Advise(all, band, _dfs.IsChecked == true, mine);
            advice.Add($"{(band == WifiBand.Band2_4 ? "2.4 GHz" : band == WifiBand.Band5 ? "5 GHz" : "6 GHz")}: use channel {a.BestChannel} - {a.Reason}.");
        }
        _advice.Text = string.Join("\n", advice) + (mine is null ? "" : $"\n(Your own network \"{mine}\" is left out of the count.)");
        _warnings.ItemsSource = ChannelPlanner.Warnings(all).ToList();
        _chart24.Set(all.Where(n => n.Band == WifiBand.Band2_4), WifiMath.Channels2_4.Prepend(-1).Append(15).Append(14).Order().ToArray(), true);
        _chart5.Set(all.Where(n => n.Band == WifiBand.Band5), WifiMath.Channels5, false);
        _chart6.Set(all.Where(n => n.Band == WifiBand.Band6), WifiMath.Channels6, false);
    }

    // ---------- scan files ----------

    private async Task SaveScanAsync()
    {
        var nets = Current.OrderByDescending(n => n.Rssi).ToList();
        if (nets.Count == 0) { _status.Text = "Nothing to save yet."; return; }
        var path = await ToolUi.PickSavePathAsync($"wifi-scan-{DateTime.Now:yyyyMMdd-HHmm}", ".json", "Wi-Fi scan (replayable) - or name it .csv for Excel");
        if (path is null) return;
        string content = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? "SSID,BSSID,RSSI dBm,Channel,Band,Width MHz,Standard,Security,Clients,Utilization %\n" + string.Join("\n", nets.Select(n =>
                $"\"{n.DisplaySsid.Replace("\"", "\"\"")}\",{n.Bssid},{n.Rssi},{n.Channel},{n.BandText},{n.ChannelWidth},{n.Standard},{n.Security},{n.StationCount},{n.ChannelUtilization}"))
            : JsonSerializer.Serialize(nets, ScanJson);
        await File.WriteAllTextAsync(path, content);
        _status.Text = $"Saved {nets.Count} networks to {System.IO.Path.GetFileName(path)}";
    }

    private async Task OpenScanAsync()
    {
        var path = await ToolUi.PickOpenPathAsync("Open a saved Wi-Fi scan", ("Wi-Fi scan", ["*.json"]));
        if (path is null) return;
        var name = System.IO.Path.GetFileName(path);
        try
        {
            var nets = JsonSerializer.Deserialize<List<WifiNetwork>>(await File.ReadAllTextAsync(path), ScanJson) ?? [];
            if (nets.Count == 0) { _status.Text = "That file has no networks in it."; return; }
            var replay = new ReplayWifiSource(nets);
            UseSource(replay, replay.Adapters());
            _status.Text = $"Replaying {name}";
        }
        catch (Exception ex) { _status.Text = $"Couldn't open {name}: {ex.Message}"; }
    }

    // ---------- site survey ----------

    private void SetPlanZoom(double z)
    {
        _planZoom = Math.Clamp(z, 0.2, 4);
        _planZoomHost.LayoutTransform = new ScaleTransform(_planZoom, _planZoom);
    }

    private async Task LoadPlanAsync()
    {
        var path = await ToolUi.PickOpenPathAsync("Load a floor plan", ("Images", ["*.png", "*.jpg", "*.jpeg", "*.bmp"]));
        if (path is null) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            _survey = new SurveyProject { Name = System.IO.Path.GetFileNameWithoutExtension(path), FloorPlanBase64 = Convert.ToBase64String(bytes), FloorPlanFileName = System.IO.Path.GetFileName(path) };
            ShowPlan();
        }
        catch (Exception ex) { _surveyStats.Text = $"Couldn't load that image: {ex.Message}"; }
    }

    private void ShowPlan()
    {
        if (_survey.FloorPlanBase64.Length == 0) { _planEmpty.IsVisible = true; return; }
        using var ms = new MemoryStream(Convert.FromBase64String(_survey.FloorPlanBase64));
        var bmp = new Bitmap(ms);
        _planSize = (bmp.PixelSize.Width, bmp.PixelSize.Height);
        _planImage.Source = bmp;
        _planSurface.Width = _markers.Width = _planSize.W;
        _planSurface.Height = _markers.Height = _planSize.H;
        _planEmpty.IsVisible = false;
        Dispatcher.UIThread.Post(() =>
        {
            var vp = _planScroll.Bounds.Size;
            if (vp.Width > 0 && _planSize.W > 0) SetPlanZoom(Math.Clamp(Math.Min(vp.Width / _planSize.W, vp.Height / _planSize.H), 0.3, 1.5));
        }, DispatcherPriority.Background);
        RefreshSsids();
        _ = RenderSurveyAsync();
    }

    private async Task TakeReadingAsync(Point pos)
    {
        if (_surveying || _planSize.W == 0) return;
        if (_source is null || _adapter is null) { _surveyStats.Text = "A survey needs a Wi-Fi adapter to take readings (you can still open and review saved surveys)."; return; }
        var point = new SurveyPoint { X = pos.X / _planSize.W, Y = pos.Y / _planSize.H };
        _surveying = true;
        var pending = Marker(point, "…", Ui.Sky);
        _surveyStats.Text = "Scanning here - hold still for a few seconds…";
        try
        {
            var src = _source;
            var id = _adapter.Id;
            var nets = await Task.Run(async () =>
            {
                src.RequestScan(id);
                await Task.Delay(src is ReplayWifiSource ? 300 : 4000);
                return src.GetNetworks(id);
            });
            point.Readings = nets.Select(n => new SurveyReading(n.Ssid, n.Bssid, n.Rssi, n.Channel, n.Band)).ToList();
            point.Time = DateTime.Now;
            _survey.Points.Add(point);
            RefreshSsids();
        }
        catch (Exception ex) { _surveyStats.Text = $"Reading failed: {ex.Message}"; }
        finally
        {
            _markers.Children.Remove(pending);
            _surveying = false;
            await RenderSurveyAsync();
        }
    }

    private void RefreshSsids()
    {
        var selected = _ssidBox.SelectedItem as string;
        var ssids = _survey.Ssids.ToList();
        ssids.Insert(0, "All networks");
        _ssidBox.SelectionChanged -= SsidChanged;
        _ssidBox.ItemsSource = ssids;
        _ssidBox.SelectedItem = selected is not null && ssids.Contains(selected) ? selected
            : _connection is not null && ssids.Contains(_connection.Ssid) ? _connection.Ssid
            : ssids.Count > 1 ? ssids[1] : ssids[0];
        _ssidBox.SelectionChanged += SsidChanged;
    }

    private void SsidChanged(object? sender, SelectionChangedEventArgs e) { if (_surveyReady) _ = RenderSurveyAsync(); }

    private HeatmapMetric Metric => (HeatmapMetric)Math.Max(0, _metric.SelectedIndex);
    private string? SurveySsid => _ssidBox.SelectedItem is string s && s != "All networks" ? s : null;

    private Border Marker(SurveyPoint p, string label, IBrush brush)
    {
        var dot = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = brush, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
            Child = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Ui.Solid(0x06070A), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Canvas.SetLeft(dot, p.X * _planSize.W - 11);
        Canvas.SetTop(dot, p.Y * _planSize.H - 11);
        _markers.Children.Add(dot);
        return dot;
    }

    private async Task RenderSurveyAsync()
    {
        if (!_surveyReady || _planSize.W == 0) return;
        _markers.Children.Clear();
        var metric = Metric;
        var ssid = SurveySsid;
        for (int i = 0; i < _survey.Points.Count; i++)
        {
            var pt = _survey.Points[i];
            var v = Heatmap.Value(pt, metric, ssid);
            var brush = v is null ? Ui.Muted : new SolidColorBrush(ToColor(Heatmap.Color(v.Value, metric) | 0xFF000000));
            var m = Marker(pt, (i + 1).ToString(), brush);
            ToolTip.SetTip(m, $"Reading {i + 1} · {pt.Time:HH:mm:ss}\n" + string.Join("\n",
                pt.Readings.OrderByDescending(r => r.Rssi).Take(6).Select(r => $"{(r.Ssid.Length > 0 ? r.Ssid : "(hidden)")}  {r.Rssi} dBm  ch {r.Channel}")));
        }
        var samples = _survey.Points.Select(p => (p.X, p.Y, V: Heatmap.Value(p, metric, ssid))).Where(s => s.V is not null).Select(s => (s.X, s.Y, s.V!.Value)).ToList();
        if (samples.Count == 0)
        {
            _heatImage.Source = null;
            _surveyStats.Text = _survey.Points.Count == 0 ? "Click on the plan where you're standing to take the first reading." : "The chosen network wasn't heard at any reading yet.";
            return;
        }
        int gw = 220, gh = Math.Max(20, (int)(220.0 * _planSize.H / _planSize.W));
        double aspect = (double)_planSize.W / _planSize.H;
        double target = _target.Value is { } t ? (double)t : -67;
        var (pixels, coverage) = await Task.Run(() =>
        {
            var grid = Heatmap.Interpolate(samples, gw, gh, aspect);
            var px = new byte[gw * gh * 4];
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    uint c = Heatmap.Color(grid[y, x], metric);
                    byte a = (byte)(c >> 24);
                    int o = (y * gw + x) * 4;
                    px[o] = (byte)((c & 0xff) * a / 255);
                    px[o + 1] = (byte)((c >> 8 & 0xff) * a / 255);
                    px[o + 2] = (byte)((c >> 16 & 0xff) * a / 255);
                    px[o + 3] = a;
                }
            double cov = metric switch
            {
                HeatmapMetric.NetworksHeard => Heatmap.Coverage(grid, 6, higherIsBetter: false),
                HeatmapMetric.SignalToInterference => Heatmap.Coverage(grid, 25),
                _ => Heatmap.Coverage(grid, target),
            };
            return (px, cov);
        });
        var wb = new WriteableBitmap(new PixelSize(gw, gh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
            for (int y = 0; y < gh; y++) Marshal.Copy(pixels, y * gw * 4, fb.Address + y * fb.RowBytes, gw * 4);
        _heatImage.Source = wb;
        string what = metric switch
        {
            HeatmapMetric.NetworksHeard => "hears 6 or fewer radios above -80 dBm",
            HeatmapMetric.SignalToInterference => "has 25 dB or more signal-to-interference",
            HeatmapMetric.SecondaryCoverage => $"has a second access point at {target:0} dBm or better (seamless roaming)",
            _ => $"gets {target:0} dBm or better",
        };
        _surveyStats.Text = $"{_survey.Points.Count} reading(s){(ssid is null ? "" : $" · {ssid}")}\n{coverage:0}% of the walked area {what}.";
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void UpdateLegend()
    {
        var metric = Metric;
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
        (double lo, double hi, string loText, string hiText) = metric switch
        {
            HeatmapMetric.NetworksHeard => (15.0, 1.0, "15+ radios (crowded)", "1 radio"),
            HeatmapMetric.SignalToInterference => (0.0, 30.0, "0 dB SIR", "30+ dB"),
            _ => (-90.0, -45.0, "-90 dBm", "-45 dBm"),
        };
        for (int i = 0; i <= 10; i++)
            brush.GradientStops.Add(new GradientStop(ToColor(Heatmap.Color(lo + (hi - lo) * i / 10.0, metric) | 0xFF000000), i / 10.0));
        _legendBar.Background = brush;
        _legendLow.Text = loText;
        _legendHigh.Text = hiText;
    }

    private void Undo()
    {
        if (_survey.Points.Count == 0) return;
        _survey.Points.RemoveAt(_survey.Points.Count - 1);
        _ = RenderSurveyAsync();
    }

    private async Task SaveSurveyAsync()
    {
        if (_survey.FloorPlanBase64.Length == 0) { _surveyStats.Text = "Load a floor plan first."; return; }
        var path = await ToolUi.PickSavePathAsync(_survey.Name, ".omlsurvey", "OML Terminal survey");
        if (path is null) return;
        _survey.Save(path);
        _surveyStats.Text = $"Saved {System.IO.Path.GetFileName(path)}";
    }

    private async Task OpenSurveyAsync()
    {
        var path = await ToolUi.PickOpenPathAsync("Open a survey", ("OML Terminal survey", ["*.omlsurvey"]));
        if (path is null) return;
        try
        {
            _survey = SurveyProject.Load(path);
            ShowPlan();
        }
        catch (Exception ex) { _surveyStats.Text = $"Couldn't open {System.IO.Path.GetFileName(path)}: {ex.Message}"; }
    }

    private async Task ExportCsvAsync()
    {
        if (_survey.Points.Count == 0) { _surveyStats.Text = "No readings to export yet."; return; }
        if (await ToolUi.SaveTextAsync($"{_survey.Name}-readings", _survey.ToCsv(), ".csv", "CSV") is { } path) _surveyStats.Text = $"Saved {System.IO.Path.GetFileName(path)}";
    }

    private async Task ExportPngAsync()
    {
        if (_planSize.W == 0) return;
        try
        {
            var path = await ToolUi.PickSavePathAsync($"{_survey.Name}-heatmap", ".png", "PNG image");
            if (path is null) return;
            // Render at 100% - the zoom is a transform on the plan and would otherwise end up in the image.
            var zoom = _planZoom;
            SetPlanZoom(1);
            _planZoomHost.UpdateLayout();
            try
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize(_planSize.W, _planSize.H), new Vector(96, 96));
                bitmap.Render(_planSurface);
                bitmap.Save(path);
            }
            finally { SetPlanZoom(zoom); }
            _surveyStats.Text = $"Saved {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex) { _surveyStats.Text = $"Couldn't export: {ex.Message}"; }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _source?.Dispose();
        _source = null;
    }
}
