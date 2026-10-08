using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OmlTerminal.Core.Wifi;
using Windows.Foundation;
using Windows.UI;

namespace OmlTerminal.App.Views.Tools;

public sealed class WifiRow(WifiNetwork n, Brush swatch, bool connected)
{
    public WifiNetwork Network { get; } = n;
    public string Ssid { get; } = n.DisplaySsid;
    public string Bssid { get; } = n.Bssid + (n.LocallyAdministeredBssid ? "  (virtual BSSID)" : "");
    public Brush Swatch { get; } = swatch;
    public string ConnectedTag { get; } = connected ? "● connected" : "";
    public double SignalWidth { get; } = Math.Clamp((n.Rssi + 95) / 65.0, 0.03, 1) * 150;
    public Brush SignalBrush { get; } = ToolUi.Brush(n.Rssi >= -67 ? "OmlMintBrush" : n.Rssi >= -75 ? "OmlAmberBrush" : "OmlRoseBrush");
    public string SignalText { get; } = $"{n.Rssi} dBm · {WifiMath.SignalWord(n.Rssi)}";
    public string ChannelText { get; } = $"{n.Channel}{(n.ChannelWidth > 20 ? $" ({n.ChannelWidth} MHz)" : "")}{(n.Band == WifiBand.Band5 && WifiMath.IsDfs(n.Channel) ? " DFS" : "")}";
    public string BandText { get; } = $"{n.BandText} · {n.FrequencyMHz} MHz";
    public string Standard { get; } = n.Standard;
    public string Security { get; } = n.Security;
    public Brush SecurityBrush { get; } = n.Security is "Open" or "WEP" or "WPA" ? ToolUi.Brush("OmlRoseBrush")
        : n.Security.Contains("WPA3") || n.Security.StartsWith("OWE") ? ToolUi.Brush("OmlMintBrush") : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    public string LoadText { get; } = n.StationCount is null && n.ChannelUtilization is null ? "—"
        : $"{(n.StationCount is { } s ? $"{s} client{(s == 1 ? "" : "s")}" : "")}{(n.StationCount is not null && n.ChannelUtilization is not null ? " · " : "")}{(n.ChannelUtilization is { } u ? $"{u}% busy" : "")}";
}

public sealed partial class WifiAnalyzerView : UserControl, IToolView
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 56, 189, 248), Color.FromArgb(255, 249, 115, 22), Color.FromArgb(255, 52, 211, 153), Color.FromArgb(255, 167, 139, 250),
        Color.FromArgb(255, 251, 191, 36), Color.FromArgb(255, 251, 113, 133), Color.FromArgb(255, 45, 212, 191), Color.FromArgb(255, 232, 121, 249),
        Color.FromArgb(255, 163, 230, 53), Color.FromArgb(255, 96, 165, 250), Color.FromArgb(255, 244, 114, 182), Color.FromArgb(255, 250, 204, 21),
    ];

    private static readonly JsonSerializerOptions ScanJson = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<string, WifiNetwork> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SignalHistory _history = new();
    private readonly RoamTracker _roams = new();
    private readonly HashSet<string> _graphed = new(StringComparer.OrdinalIgnoreCase);
    private IWifiSource? _source;
    private WifiAdapter? _adapter;
    private WifiConnection? _connection;
    private bool _busy, _refreshingList;

    private SurveyProject _survey = new();
    private (int W, int H) _planSize;
    private bool _surveying;

    public WifiAnalyzerView(ToolContext ctx)
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await TickAsync();
        Loaded += (_, _) => { OpenLiveSource(); UpdateLegend(); };
    }

    // ---------- sources ----------

    private void OpenLiveSource()
    {
        try
        {
            var live = new WlanSource();
            var adapters = live.Adapters();
            if (adapters.Count == 0)
            {
                live.Dispose();
                ShowUnavailable("No Wi-Fi adapter found on this PC. You can still open a saved scan or survey.");
                return;
            }
            UseSource(live, adapters);
        }
        catch (WifiUnavailableException ex) { ShowUnavailable(ex.Message + " You can still open a saved scan or survey."); }
    }

    private void ShowUnavailable(string message)
    {
        StatusText.Text = "Wi-Fi unavailable";
        NetEmpty.Text = message;
        NetEmpty.Visibility = Visibility.Visible;
        ScanToggle.IsEnabled = false;
    }

    private void UseSource(IWifiSource source, IReadOnlyList<WifiAdapter> adapters)
    {
        _timer.Stop();
        _source?.Dispose();
        _source = source;
        _seen.Clear();
        AdapterBox.ItemsSource = adapters;
        AdapterBox.SelectedIndex = 0;
        ScanToggle.IsEnabled = true;
        ScanToggle.IsChecked = true;
        NetEmpty.Visibility = Visibility.Collapsed;
        _source.RequestScan(adapters[0].Id);
        _ = TickAsync();
        _timer.Interval = TimeSpan.FromSeconds(double.IsNaN(IntervalBox.Value) ? 5 : IntervalBox.Value);
        _timer.Start();
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _adapter = AdapterBox.SelectedItem as WifiAdapter;
        _seen.Clear();
    }

    private void ScanToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ScanToggle.IsChecked == true) { _timer.Interval = TimeSpan.FromSeconds(double.IsNaN(IntervalBox.Value) ? 5 : IntervalBox.Value); _timer.Start(); _ = TickAsync(); }
        else _timer.Stop();
        ScanToggle.Content = ScanToggle.IsChecked == true ? "Scanning" : "Paused";
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
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _busy = false; }
    }

    // ---------- rendering ----------

    private Brush SwatchFor(WifiNetwork n) => new SolidColorBrush(ColorFor(n));

    private static Color ColorFor(WifiNetwork n)
    {
        int h = 0;
        foreach (char c in n.Ssid.Length > 0 ? n.Ssid : n.Bssid) h = h * 31 + c;
        return Palette[Math.Abs(h) % Palette.Length];
    }

    private IReadOnlyList<WifiNetwork> Current => _seen.Values.ToList();

    private void Render()
    {
        var all = Current;
        int bands = all.Select(n => n.Band).Distinct().Count();
        StatusText.Text = $"{all.Count} access point radios · {all.Select(n => n.Ssid).Distinct().Count()} networks · {bands} band(s) · updated {DateTime.Now:HH:mm:ss}";
        RenderConnection();
        RenderList();
        RenderGraph();
        if (ChannelsPage.Visibility == Visibility.Visible) RenderChannels();
        RoamList.ItemsSource = _roams.Events.ToList();
    }

    private void RenderConnection()
    {
        if (_connection is not { } c)
        {
            ConnTitle.Text = "Not connected";
            ConnDetail.Text = "";
            ConnIcon.Foreground = ToolUi.Brush("StatusIdleBrush");
            return;
        }
        var ap = _seen.GetValueOrDefault(c.Bssid);
        ConnTitle.Text = $"{c.Ssid}  ·  {c.Rssi} dBm {WifiMath.SignalWord(c.Rssi)}";
        ConnDetail.Text = $"{c.Bssid}{(ap is null ? "" : $" · ch {ap.Channel} {ap.BandText} {ap.ChannelWidth} MHz · {ap.Standard}")} · rx {c.RxRateMbps} / tx {c.TxRateMbps} Mbps";
        ConnIcon.Foreground = ToolUi.Brush(c.Rssi >= -67 ? "OmlMintBrush" : c.Rssi >= -75 ? "OmlAmberBrush" : "OmlRoseBrush");
    }

    private void RenderList()
    {
        IEnumerable<WifiNetwork> q = Current;
        var band = BandFilter.SelectedIndex switch { 1 => WifiBand.Band2_4, 2 => WifiBand.Band5, 3 => WifiBand.Band6, _ => (WifiBand?)null };
        if (band is { } b) q = q.Where(n => n.Band == b);
        var text = NetFilter.Text.Trim();
        if (text.Length > 0)
            q = q.Where(n => n.Ssid.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Bssid.Contains(text, StringComparison.OrdinalIgnoreCase)
                             || n.Security.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Standard.Contains(text, StringComparison.OrdinalIgnoreCase));
        q = SortBox.SelectedIndex switch
        {
            1 => q.OrderBy(n => n.Hidden).ThenBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase).ThenByDescending(n => n.Rssi),
            2 => q.OrderBy(n => n.Band).ThenBy(n => n.Channel).ThenByDescending(n => n.Rssi),
            _ => q.OrderByDescending(n => n.Rssi),
        };
        var rows = q.Select(n => new WifiRow(n, SwatchFor(n), string.Equals(n.Bssid, _connection?.Bssid, StringComparison.OrdinalIgnoreCase))).ToList();
        _refreshingList = true;
        NetList.ItemsSource = rows;
        foreach (var r in rows.Where(r => _graphed.Contains(r.Network.Bssid))) NetList.SelectedItems.Add(r);
        _refreshingList = false;
        NetEmpty.Visibility = rows.Count == 0 && _source is not null ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count == 0 && _source is not null) NetEmpty.Text = text.Length > 0 || band is not null ? "No networks match the filter." : "Scanning…";
    }

    private void NetFilter_TextChanged(object sender, TextChangedEventArgs e) => RenderList();
    private void BandFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RenderList(); }

    private void NetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingList) return;
        foreach (var r in e.AddedItems.OfType<WifiRow>()) _graphed.Add(r.Network.Bssid);
        foreach (var r in e.RemovedItems.OfType<WifiRow>()) _graphed.Remove(r.Network.Bssid);
        RenderGraph();
    }

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e) => RenderGraph();

    private void RenderGraph()
    {
        GraphCanvas.Children.Clear();
        double w = GraphCanvas.ActualWidth, h = GraphCanvas.ActualHeight;
        if (w < 220 || h < 50) return;
        const double left = 44, labelRoom = 120; // right margin keeps the SSID tags clear of the lines
        double right = w - labelRoom;
        var axis = ToolUi.Brush("HairlineBrush");
        foreach (var dbm in new[] { -30, -50, -67, -80, -95 })
        {
            double y = Y(dbm, h);
            var grid = new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = axis, StrokeThickness = dbm == -67 ? 1.5 : 1 };
            if (dbm == -67) grid.StrokeDashArray = new DoubleCollection { 4, 3 };
            GraphCanvas.Children.Add(grid);
            var label = new TextBlock { Text = $"{dbm}", FontSize = 10.5, Opacity = 0.55 };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            GraphCanvas.Children.Add(label);
        }
        var now = DateTime.Now;
        var window = TimeSpan.FromMinutes(5);
        var targets = _graphed.Count > 0
            ? Current.Where(n => _graphed.Contains(n.Bssid))
            : Current.OrderByDescending(n => n.Rssi).Take(6);
        var tags = new List<(TextBlock Tag, double X, double Y)>();
        foreach (var n in targets)
        {
            var pts = _history.For(n.Bssid).Where(s => now - s.At <= window).ToList();
            if (pts.Count == 0) continue;
            var line = new Polyline { Stroke = new SolidColorBrush(ColorFor(n)), StrokeThickness = 2 };
            foreach (var (at, rssi) in pts)
                line.Points.Add(new Point(left + (right - left) * (1 - (now - at).TotalSeconds / window.TotalSeconds), Y(rssi, h)));
            if (pts.Count == 1) line.Points.Add(new Point(line.Points[0].X + 2, line.Points[0].Y));
            GraphCanvas.Children.Add(line);
            var tag = new TextBlock { Text = $"{n.DisplaySsid} {pts[^1].Rssi}", FontSize = 10.5, Foreground = line.Stroke };
            tags.Add((tag, line.Points[^1].X, line.Points[^1].Y - 7));
        }
        // Lines from APs at similar levels end at the same spot - stack their labels instead of overprinting them.
        double nextFree = double.MinValue;
        foreach (var (tag, x, y) in tags.OrderBy(t => t.Y))
        {
            double top = Math.Min(Math.Max(y, nextFree), h - 14);
            nextFree = top + 13;
            Canvas.SetLeft(tag, x + 6);
            Canvas.SetTop(tag, top);
            GraphCanvas.Children.Add(tag);
        }

        static double Y(int dbm, double h) => 6 + (h - 12) * (-25 - Math.Clamp(dbm, -95, -25)) / 70.0;
    }

    // ---------- channels ----------

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (RoamPage is null) return; // first item's IsSelected fires this inside InitializeComponent
        int i = Tabs.Items.IndexOf(Tabs.SelectedItem);
        NetworksPage.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelsPage.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        SurveyPage.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        RoamPage.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (i == 1) RenderChannels();
        if (i == 2) RenderSurvey();
    }

    private void DfsCheck_Click(object sender, RoutedEventArgs e) => RenderChannels();
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChannels();

    private void RenderChannels()
    {
        var all = Current;
        var mine = _connection?.Ssid;
        var advice = new List<string>();
        foreach (var band in new[] { WifiBand.Band2_4, WifiBand.Band5, WifiBand.Band6 })
        {
            if (!all.Any(n => n.Band == band) && band == WifiBand.Band6) continue;
            var a = ChannelPlanner.Advise(all, band, DfsCheck.IsChecked == true, mine);
            advice.Add($"{(band == WifiBand.Band2_4 ? "2.4 GHz" : band == WifiBand.Band5 ? "5 GHz" : "6 GHz")}: use channel {a.BestChannel} - {a.Reason}.");
        }
        AdviceText.Text = string.Join("\n", advice) + (mine is null ? "" : $"\n(Your own network \"{mine}\" is left out of the count.)");
        WarningsList.ItemsSource = ChannelPlanner.Warnings(all).ToList();
        DrawSpectrum(Chart24, all.Where(n => n.Band == WifiBand.Band2_4), WifiMath.Channels2_4.Prepend(-1).Append(15).Append(14).Order().ToArray(), true);
        DrawSpectrum(Chart5, all.Where(n => n.Band == WifiBand.Band5), WifiMath.Channels5, false);
        DrawSpectrum(Chart6, all.Where(n => n.Band == WifiBand.Band6), WifiMath.Channels6, false);
    }

    /// <summary>Each radio drawn as a hump spanning the channels it occupies, as tall as its signal - overlap is visible at a glance.</summary>
    private static void DrawSpectrum(Canvas c, IEnumerable<WifiNetwork> nets, int[] channels, bool is24)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 100) return;
        const double bottom = 22, top = 8, left = 36;
        int min = channels.Min(), max = channels.Max();
        double X(double ch) => left + (w - left - 10) * (ch - min) / Math.Max(1, max - min);
        double Y(int dbm) => top + (h - bottom - top) * (-25 - Math.Clamp(dbm, -95, -25)) / 70.0;
        var axis = ToolUi.Brush("HairlineBrush");
        c.Children.Add(new Line { X1 = left, X2 = w - 10, Y1 = h - bottom, Y2 = h - bottom, Stroke = axis });
        foreach (var dbm in new[] { -30, -50, -70, -90 })
        {
            var t = new TextBlock { Text = dbm.ToString(), FontSize = 10, Opacity = 0.5 };
            Canvas.SetTop(t, Y(dbm) - 7);
            c.Children.Add(t);
        }
        int step = is24 ? 1 : channels.Length > 30 ? 8 : 1;
        foreach (var ch in channels.Where((x, i) => x >= 1 && (step == 1 || i % step == 0) && (!is24 || x <= 14)))
        {
            var t = new TextBlock { Text = ch.ToString(), FontSize = 10, Opacity = WifiMath.IsDfs(ch) && !is24 ? 0.35 : 0.6 };
            Canvas.SetLeft(t, X(ch) - 6);
            Canvas.SetTop(t, h - bottom + 3);
            c.Children.Add(t);
        }
        var labels = new List<(TextBlock Label, double X, double Y, double Width)>();
        foreach (var n in nets.OrderBy(n => n.Rssi))
        {
            double lo = is24 ? n.CoveredChannels.Min() - 2 : n.CoveredChannels.Min() - 2;
            double hi = is24 ? n.CoveredChannels.Max() + 2 : n.CoveredChannels.Max() + 2;
            var color = ColorFor(n);
            var shape = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.6,
            };
            double baseY = h - bottom, peak = Y(n.Rssi);
            shape.Points.Add(new Point(X(lo), baseY));
            shape.Points.Add(new Point(X(lo + (hi - lo) * 0.2), peak));
            shape.Points.Add(new Point(X(hi - (hi - lo) * 0.2), peak));
            shape.Points.Add(new Point(X(hi), baseY));
            ToolTipService.SetToolTip(shape, $"{n.DisplaySsid} ({n.Bssid})\nch {n.Channel}, {n.ChannelWidth} MHz, {n.Rssi} dBm");
            c.Children.Add(shape);
            var label = new TextBlock { Text = n.DisplaySsid, FontSize = 10.5, Foreground = new SolidColorBrush(color) };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            labels.Add((label, X((lo + hi) / 2) - label.DesiredSize.Width / 2, peak - 15, label.DesiredSize.Width));
        }
        // APs on the same channel at similar levels would print their names on top of each other - nudge later ones up.
        var placed = new List<Rect>();
        foreach (var (label, x, y, width) in labels.OrderByDescending(l => l.Y))
        {
            var r = new Rect(Math.Clamp(x, left, Math.Max(left, w - width - 4)), Math.Max(0, y), width, 13);
            while (placed.Any(p => p.X < r.X + r.Width && r.X < p.X + p.Width && p.Y < r.Y + r.Height && r.Y < p.Y + p.Height) && r.Y > 0)
                r.Y = Math.Max(0, r.Y - 13);
            placed.Add(r);
            Canvas.SetLeft(label, r.X);
            Canvas.SetTop(label, r.Y);
            c.Children.Add(label);
        }
        if (!nets.Any())
        {
            var t = new TextBlock { Text = "No networks heard in this band.", Opacity = 0.5 };
            Canvas.SetLeft(t, left + 10);
            Canvas.SetTop(t, h / 2 - 10);
            c.Children.Add(t);
        }
    }

    // ---------- scan files ----------

    private async void SaveScan_Click(object sender, RoutedEventArgs e)
    {
        var nets = Current.OrderByDescending(n => n.Rssi).ToList();
        if (nets.Count == 0) { StatusText.Text = "Nothing to save yet."; return; }
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"wifi-scan-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add("Wi-Fi scan (replayable)", new List<string> { ".json" });
        picker.FileTypeChoices.Add("CSV (Excel)", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        string content = file.FileType.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? "SSID,BSSID,RSSI dBm,Channel,Band,Width MHz,Standard,Security,Clients,Utilization %\n" + string.Join("\n", nets.Select(n =>
                $"\"{n.DisplaySsid.Replace("\"", "\"\"")}\",{n.Bssid},{n.Rssi},{n.Channel},{n.BandText},{n.ChannelWidth},{n.Standard},{n.Security},{n.StationCount},{n.ChannelUtilization}"))
            : JsonSerializer.Serialize(nets, ScanJson);
        await File.WriteAllTextAsync(file.Path, content);
        StatusText.Text = $"Saved {nets.Count} networks to {file.Name}";
    }

    private async void OpenScan_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var nets = JsonSerializer.Deserialize<List<WifiNetwork>>(await File.ReadAllTextAsync(file.Path), ScanJson) ?? [];
            if (nets.Count == 0) { StatusText.Text = "That file has no networks in it."; return; }
            var replay = new ReplayWifiSource(nets);
            UseSource(replay, replay.Adapters());
            StatusText.Text = $"Replaying {file.Name}";
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't open {file.Name}: {ex.Message}"; }
    }

    // ---------- site survey ----------

    private async void LoadPlan_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var bytes = await File.ReadAllBytesAsync(file.Path);
        _survey = new SurveyProject { Name = System.IO.Path.GetFileNameWithoutExtension(file.Name), FloorPlanBase64 = Convert.ToBase64String(bytes), FloorPlanFileName = file.Name };
        await ShowPlanAsync();
    }

    private async Task ShowPlanAsync()
    {
        if (_survey.FloorPlanBase64.Length == 0) { PlanEmpty.Visibility = Visibility.Visible; return; }
        var bytes = Convert.FromBase64String(_survey.FloorPlanBase64);
        var bmp = new BitmapImage();
        using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bmp.SetSourceAsync(stream);
        }
        _planSize = (bmp.PixelWidth, bmp.PixelHeight);
        PlanImage.Source = bmp;
        PlanSurface.Width = _planSize.W;
        PlanSurface.Height = _planSize.H;
        PlanEmpty.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (PlanScroll.ViewportWidth > 0 && _planSize.W > 0)
                PlanScroll.ChangeView(0, 0, (float)Math.Clamp(Math.Min(PlanScroll.ViewportWidth / _planSize.W, PlanScroll.ViewportHeight / _planSize.H), 0.3, 1.5));
        });
        RefreshSsids();
        RenderSurvey();
    }

    private async void PlanSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_surveying || _planSize.W == 0) return;
        if (_source is null || _adapter is null) { SurveyStats.Text = "A survey needs a Wi-Fi adapter to take readings (you can still open and review saved surveys)."; return; }
        var pos = e.GetPosition(PlanSurface);
        var point = new SurveyPoint { X = pos.X / _planSize.W, Y = pos.Y / _planSize.H };
        _surveying = true;
        var pending = Marker(point, "…", ToolUi.Brush("OmlSkyBrush"));
        SurveyStats.Text = "Scanning here - hold still for a few seconds…";
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
        catch (Exception ex) { SurveyStats.Text = $"Reading failed: {ex.Message}"; }
        finally
        {
            MarkerCanvas.Children.Remove(pending);
            _surveying = false;
            RenderSurvey();
        }
    }

    private void RefreshSsids()
    {
        var selected = SsidBox.SelectedItem as string;
        var ssids = _survey.Ssids.ToList();
        ssids.Insert(0, "All networks");
        SsidBox.SelectionChanged -= SurveyOption_Changed;
        SsidBox.ItemsSource = ssids;
        SsidBox.SelectedItem = selected is not null && ssids.Contains(selected) ? selected
            : _connection is not null && ssids.Contains(_connection.Ssid) ? _connection.Ssid
            : ssids.Count > 1 ? ssids[1] : ssids[0];
        SsidBox.SelectionChanged += SurveyOption_Changed;
    }

    // These fire while InitializeComponent is still building the page (SelectedIndex/Value set in XAML) - wait for Loaded.
    private void SurveyOption_Changed(object sender, SelectionChangedEventArgs e) { if (!IsLoaded) return; UpdateLegend(); RenderSurvey(); }
    private void TargetBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (IsLoaded) RenderSurvey(); }

    private HeatmapMetric Metric => (HeatmapMetric)Math.Max(0, MetricBox.SelectedIndex);
    private string? SurveySsid => SsidBox.SelectedItem is string s && s != "All networks" ? s : null;

    private Border Marker(SurveyPoint p, string label, Brush brush)
    {
        var dot = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = brush,
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White), BorderThickness = new Thickness(1.5),
            Child = new TextBlock { Text = label, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(255, 6, 7, 10)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Canvas.SetLeft(dot, p.X * _planSize.W - 11);
        Canvas.SetTop(dot, p.Y * _planSize.H - 11);
        MarkerCanvas.Children.Add(dot);
        return dot;
    }

    private async void RenderSurvey()
    {
        if (!IsLoaded || _planSize.W == 0) return;
        MarkerCanvas.Children.Clear();
        var metric = Metric;
        var ssid = SurveySsid;
        for (int i = 0; i < _survey.Points.Count; i++)
        {
            var v = Heatmap.Value(_survey.Points[i], metric, ssid);
            var brush = v is null ? ToolUi.Brush("StatusIdleBrush") : new SolidColorBrush(ToColor(Heatmap.Color(v.Value, metric) | 0xFF000000));
            var m = Marker(_survey.Points[i], (i + 1).ToString(), brush);
            ToolTipService.SetToolTip(m, $"Reading {i + 1} · {_survey.Points[i].Time:HH:mm:ss}\n" + string.Join("\n",
                _survey.Points[i].Readings.OrderByDescending(r => r.Rssi).Take(6).Select(r => $"{(r.Ssid.Length > 0 ? r.Ssid : "(hidden)")}  {r.Rssi} dBm  ch {r.Channel}")));
        }
        var samples = _survey.Points.Select(p => (p.X, p.Y, V: Heatmap.Value(p, metric, ssid))).Where(s => s.V is not null).Select(s => (s.X, s.Y, s.V!.Value)).ToList();
        if (samples.Count == 0)
        {
            HeatImage.Source = null;
            SurveyStats.Text = _survey.Points.Count == 0 ? "Click on the plan where you're standing to take the first reading." : "The chosen network wasn't heard at any reading yet.";
            return;
        }
        int gw = 220, gh = Math.Max(20, (int)(220.0 * _planSize.H / _planSize.W));
        double aspect = (double)_planSize.W / _planSize.H;
        double target = double.IsNaN(TargetBox.Value) ? -67 : TargetBox.Value;
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
        var wb = new WriteableBitmap(gw, gh);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
        wb.Invalidate();
        HeatImage.Source = wb;
        string what = metric switch
        {
            HeatmapMetric.NetworksHeard => "hears 6 or fewer radios above -80 dBm",
            HeatmapMetric.SignalToInterference => "has 25 dB or more signal-to-interference",
            HeatmapMetric.SecondaryCoverage => $"has a second access point at {target:0} dBm or better (seamless roaming)",
            _ => $"gets {target:0} dBm or better",
        };
        SurveyStats.Text = $"{_survey.Points.Count} reading(s){(ssid is null ? "" : $" · {ssid}")}\n{coverage:0}% of the walked area {what}.";
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void UpdateLegend()
    {
        var metric = Metric;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        (double lo, double hi, string loText, string hiText) = metric switch
        {
            HeatmapMetric.NetworksHeard => (15.0, 1.0, "15+ radios (crowded)", "1 radio"),
            HeatmapMetric.SignalToInterference => (0.0, 30.0, "0 dB SIR", "30+ dB"),
            _ => (-90.0, -45.0, "-90 dBm", "-45 dBm"),
        };
        for (int i = 0; i <= 10; i++)
            brush.GradientStops.Add(new GradientStop { Offset = i / 10.0, Color = ToColor(Heatmap.Color(lo + (hi - lo) * i / 10.0, metric) | 0xFF000000) });
        LegendBar.Background = brush;
        LegendLow.Text = loText;
        LegendHigh.Text = hiText;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_survey.Points.Count == 0) return;
        _survey.Points.RemoveAt(_survey.Points.Count - 1);
        RenderSurvey();
    }

    private async void SaveSurvey_Click(object sender, RoutedEventArgs e)
    {
        if (_survey.FloorPlanBase64.Length == 0) { SurveyStats.Text = "Load a floor plan first."; return; }
        var path = await ToolUi.PickSavePathAsync(_survey.Name, ".omlsurvey", "OML Terminal survey");
        if (path is null) return;
        _survey.Save(path);
        SurveyStats.Text = $"Saved {System.IO.Path.GetFileName(path)}";
    }

    private async void OpenSurvey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".omlsurvey");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            _survey = SurveyProject.Load(file.Path);
            await ShowPlanAsync();
        }
        catch (Exception ex) { SurveyStats.Text = $"Couldn't open {file.Name}: {ex.Message}"; }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_survey.Points.Count == 0) { SurveyStats.Text = "No readings to export yet."; return; }
        if (await ToolUi.SaveTextAsync($"{_survey.Name}-readings", _survey.ToCsv(), ".csv", "CSV") is { } path) SurveyStats.Text = $"Saved {System.IO.Path.GetFileName(path)}";
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (_planSize.W == 0) return;
        try
        {
            var path = await ToolUi.PickSavePathAsync($"{_survey.Name}-heatmap", ".png", "PNG image");
            if (path is null) return;
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(PlanSurface);
            var pixels = await bitmap.GetPixelsAsync();
            using var fs = File.Create(path);
            using var stream = fs.AsRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            SurveyStats.Text = $"Saved {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex) { SurveyStats.Text = $"Couldn't export: {ex.Message}"; }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _source?.Dispose();
        _source = null;
    }
}
