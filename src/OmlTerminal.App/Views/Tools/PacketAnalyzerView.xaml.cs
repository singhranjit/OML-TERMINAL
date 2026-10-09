using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Capture;

namespace OmlTerminal.App.Views.Tools;

public sealed class PacketRow(DecodedPacket p, DateTime start, Brush brush)
{
    public DecodedPacket Packet { get; } = p;
    public string Number { get; } = p.Number.ToString();
    public string Time { get; } = (p.Timestamp - start).TotalSeconds.ToString("0.000000");
    public string Source { get; } = p.Source;
    public string Destination { get; } = p.Destination;
    public string Protocol { get; } = p.Protocol;
    public string Length { get; } = p.OriginalLength.ToString();
    public string Info { get; } = p.Info;
    public Brush Brush { get; } = brush;
}

public sealed class InsightRow
{
    public required CaptureInsight Insight { get; init; }
    public string Glyph => Insight.Severity switch { InsightSeverity.Problem => "", InsightSeverity.Warning => "", _ => "" };
    public Brush Brush => ToolUi.Brush(Insight.Severity switch { InsightSeverity.Problem => "OmlRoseBrush", InsightSeverity.Warning => "OmlAmberBrush", _ => "OmlSkyBrush" });
    public string FilterHint => Insight.Filter.Length > 0 ? $"Click to filter: {Insight.Filter}" : "";
}

public sealed record StatRow(string Title, string Subtitle, string Value, string Filter);

public sealed partial class PacketAnalyzerView : UserControl, IToolView
{
    private const int MaxPackets = 500_000;
    private readonly List<DecodedPacket> _all = new();
    private readonly ObservableCollection<PacketRow> _shown = new();
    private readonly ConcurrentQueue<DecodedPacket> _incoming = new();
    private readonly DispatcherTimer _drain = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _filterDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DisplayFilter _filter = DisplayFilter.All;
    private ILiveCapture? _capture;
    private int _nextNumber;
    private DateTime _start;
    private bool _statsDirty;
    private readonly Dictionary<string, Brush> _brushes = new();

    public PacketAnalyzerView(ToolContext ctx)
    {
        InitializeComponent();
        PacketList.ItemsSource = _shown;
        _drain.Tick += (_, _) => Drain();
        _filterDelay.Tick += (_, _) => { _filterDelay.Stop(); ApplyFilter(); };
        _statsTimer.Tick += (_, _) => { if (_statsDirty) RefreshSide(); };
        Loaded += (_, _) => LoadInterfaces();
    }

    private void LoadInterfaces()
    {
        NpcapBar.IsOpen = !CaptureEngine.NpcapInstalled;
        try
        {
            var list = CaptureEngine.ListInterfaces();
            var keep = (InterfaceBox.SelectedItem as CaptureInterface)?.Id; // Loaded runs again on every tab switch
            InterfaceBox.ItemsSource = list;
            var again = list.FirstOrDefault(i => i.Id == keep);
            if (again is not null) InterfaceBox.SelectedItem = again;
            else if (list.Count > 0) InterfaceBox.SelectedIndex = 0;
            else StatusText.Text = "No capture-capable adapters found.";
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't list adapters: {ex.Message}"; }
    }

    private void InterfaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = InterfaceBox.SelectedItem as CaptureInterface;
        MonitorCheck.IsEnabled = i is { IsWireless: true, ViaNpcap: true };
        if (!MonitorCheck.IsEnabled) MonitorCheck.IsChecked = false;
        CaptureFilterBox.IsEnabled = i?.ViaNpcap != false;
    }

    // ---------- capture ----------

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is not null || InterfaceBox.SelectedItem is not CaptureInterface iface) return;
        try
        {
            var cap = CaptureEngine.Open(iface, CaptureFilterBox.Text.Trim(), PromiscuousCheck.IsChecked == true, MonitorCheck.IsChecked == true);
            cap.FrameArrived += OnFrame;
            cap.Failed += msg => DispatcherQueue.TryEnqueue(() => { StatusText.Text = msg; StopCapture(); });
            cap.Start();
            _capture = cap;
            if (_all.Count == 0) _start = DateTime.Now;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        InterfaceBox.IsEnabled = CaptureFilterBox.IsEnabled = false;
        StatusText.Text = $"Capturing on {iface.Name}…";
        _drain.Start();
        _statsTimer.Start();
    }

    /// <summary>Decoding happens here on the capture thread, so the UI only has to append finished rows.</summary>
    private void OnFrame(RawFrame f)
    {
        int n = Interlocked.Increment(ref _nextNumber);
        if (n > MaxPackets) return;
        _incoming.Enqueue(PacketDecoder.Decode(f.Data, f.LinkType, n, f.Timestamp, f.OriginalLength, details: false));
    }

    private void Drain()
    {
        if (_incoming.IsEmpty) { UpdateCounts(); return; }
        var batch = new List<DecodedPacket>();
        while (batch.Count < 20000 && _incoming.TryDequeue(out var p)) batch.Add(p);
        if (_all.Count == 0 && batch.Count > 0) _start = batch[0].Timestamp;
        _all.AddRange(batch);
        foreach (var p in batch.Where(_filter.Matches)) _shown.Add(Row(p));
        _statsDirty = true;
        if (AutoScrollToggle.IsChecked == true && _shown.Count > 0) PacketList.ScrollIntoView(_shown[^1]);
        if (_nextNumber > MaxPackets && _capture is not null)
        {
            StopCapture();
            StatusText.Text = $"Stopped at {MaxPackets:N0} packets to protect memory - save, clear, then capture again (a capture filter keeps it focused).";
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _filter == DisplayFilter.All ? $"{_all.Count:N0} packets" : $"{_shown.Count:N0} of {_all.Count:N0} shown";
        if (_capture is not null && InterfaceBox.SelectedItem is CaptureInterface iface)
        {
            var (recv, drop) = _capture.Statistics;
            StatusText.Text = $"Capturing on {iface.Name} · {_all.Count:N0} packets{(drop > 0 ? $" · {drop:N0} dropped by the driver" : "")}";
            _ = recv;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopCapture();
        StatusText.Text = $"Stopped · {_all.Count:N0} packets.";
    }

    private void StopCapture()
    {
        var cap = _capture;
        _capture = null;
        if (cap is not null)
        {
            cap.FrameArrived -= OnFrame;
            Task.Run(cap.Dispose);
        }
        Drain();
        _drain.Stop();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        InterfaceBox.IsEnabled = true;
        CaptureFilterBox.IsEnabled = (InterfaceBox.SelectedItem as CaptureInterface)?.ViaNpcap != false;
        RefreshSide();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        while (_incoming.TryDequeue(out _)) { }
        _all.Clear();
        _shown.Clear();
        _nextNumber = 0;
        _start = DateTime.Now;
        ShowDetails(null);
        RefreshSide();
        UpdateCounts();
        if (_capture is null) StatusText.Text = "Cleared.";
    }

    // ---------- files ----------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is not null) { StatusText.Text = "Stop the capture first."; return; }
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".pcap", ".pcapng", ".cap", ".dmp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        StatusText.Text = $"Reading {file.Name}…";
        try
        {
            bool truncated = false;
            var packets = await Task.Run(() =>
            {
                var frames = PcapReader.Read(file.Path, 512L << 20, out truncated);
                truncated |= frames.Count > MaxPackets;
                return frames.Take(MaxPackets)
                    .Select((f, i) => PacketDecoder.Decode(f.Data, f.LinkType, i + 1, f.Timestamp, f.OriginalLength, details: false)).ToList();
            });
            Clear_Click(sender, e);
            _all.AddRange(packets);
            _nextNumber = packets.Count;
            _start = packets.FirstOrDefault()?.Timestamp ?? DateTime.Now;
            ApplyFilter();
            RefreshSide();
            StatusText.Text = $"{file.Name} · {packets.Count:N0} packets" + (truncated ? " - the first part of a larger file (split big captures with editcap to see the rest)" : "");
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't open {file.Name}: {ex.Message}"; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var packets = _shown.Select(r => r.Packet).ToList();
        if (packets.Count == 0) { StatusText.Text = "Nothing to save."; return; }
        var path = await ToolUi.PickSavePathAsync($"capture-{DateTime.Now:yyyyMMdd-HHmmss}", ".pcap", "Packet capture");
        if (path is null) return;
        try
        {
            await Task.Run(() =>
            {
                using var fs = File.Create(path);
                using var w = new PcapWriter(fs, packets[0].LinkType);
                foreach (var p in packets) w.Write(p.Timestamp, p.Data, p.OriginalLength);
            });
            StatusText.Text = $"Saved {packets.Count:N0} packets to {Path.GetFileName(path)}";
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't save: {ex.Message}"; }
    }

    // ---------- filtering ----------

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterDelay.Stop();
        _filterDelay.Start();
    }

    private void FilterBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _filterDelay.Stop(); ApplyFilter(); }
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string f }) { FilterBox.Text = f; _filterDelay.Stop(); ApplyFilter(); }
    }

    private void ApplyFilter()
    {
        try
        {
            _filter = DisplayFilter.Parse(FilterBox.Text);
            FilterBox.BorderBrush = ToolUi.Brush(_filter == DisplayFilter.All ? "HairlineBrush" : "OmlMintBrush");
            ToolTipService.SetToolTip(FilterBox, null);
        }
        catch (FilterSyntaxException ex)
        {
            FilterBox.BorderBrush = ToolUi.Brush("OmlRoseBrush");
            ToolTipService.SetToolTip(FilterBox, ex.Message);
            CountText.Text = ex.Message;
            return;
        }
        var selected = (PacketList.SelectedItem as PacketRow)?.Packet;
        _shown.Clear();
        foreach (var p in _all.Where(_filter.Matches)) _shown.Add(Row(p));
        UpdateCounts();
        if (selected is not null && _shown.FirstOrDefault(r => r.Packet == selected) is { } row)
        {
            PacketList.SelectedItem = row;
            PacketList.ScrollIntoView(row);
        }
    }

    private PacketRow Row(DecodedPacket p) => new(p, _start, BrushFor(p));

    private Brush BrushFor(DecodedPacket p)
    {
        string key = p.Malformed || p.Flags.HasFlag(TcpFlags.Rst) || p.DnsResponse && p.DnsRcode != 0 || p.IcmpType is 3 or 11 && p.IpProtocol == 1 ? "OmlRoseBrush"
            : p.Protocol switch
            {
                "DNS" or "MDNS" or "LLMNR" => "OmlVioletBrush",
                "ARP" => "OmlAmberBrush",
                "DHCP" => "OmlMintBrush",
                "ICMP" or "ICMPv6" => "OmlSkyBrush",
                "STP" or "PVST+" or "CDP" or "LLDP" or "DTP" or "LACP" or "UDLD" => "StatusIdleBrush",
                "TCP" when p.Flags.HasFlag(TcpFlags.Syn) => "OmlOrangeBrush",
                _ => "TextFillColorPrimaryBrush",
            };
        if (!_brushes.TryGetValue(key, out var b))
            _brushes[key] = b = key == "TextFillColorPrimaryBrush" ? (Brush)Application.Current.Resources[key] : ToolUi.Brush(key);
        return b;
    }

    // ---------- details ----------

    private void PacketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = (PacketList.SelectedItem as PacketRow)?.Packet;
        ShowDetails(p);
        if (p is not null && AutoScrollToggle.IsChecked == true && _capture is not null && e.AddedItems.Count > 0 && PacketList.SelectedIndex < _shown.Count - 1)
            AutoScrollToggle.IsChecked = false; // looking at something - stop jumping to the newest packet
    }

    private void ShowDetails(DecodedPacket? p)
    {
        DetailTree.RootNodes.Clear();
        FollowButton.IsEnabled = p?.IpProtocol == 6;
        ConversationFilterButton.IsEnabled = p?.SrcIp is not null || p?.SrcMac is not null;
        if (p is null) { HexBox.Text = ""; DetailTitle.Text = "PACKET DETAILS"; return; }
        DetailTitle.Text = $"PACKET {p.Number} · {p.OriginalLength} BYTES · {p.Timestamp:HH:mm:ss.ffffff}";
        foreach (var layer in p.Layers)
        {
            var node = new TreeViewNode { Content = $"{layer.Name}  —  {layer.Summary}", IsExpanded = p.Layers.Count <= 3 || layer == p.Layers[^1] };
            foreach (var (name, value) in layer.Fields.Where(f => f.Value.Length > 0)) node.Children.Add(new TreeViewNode { Content = $"{name}: {value}" });
            DetailTree.RootNodes.Add(node);
        }
        if (p.Layers.Count == 0) DetailTree.RootNodes.Add(new TreeViewNode { Content = p.Info });
        HexBox.Text = Hex(p.Data);
    }

    private static string Hex(byte[] d)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(d.Length, 4096); i += 16)
        {
            sb.Append($"{i:x4}  ");
            for (int j = 0; j < 16; j++) sb.Append(i + j < d.Length ? $"{d[i + j]:x2} " : "   ").Append(j == 7 ? " " : "");
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < d.Length; j++) { byte b = d[i + j]; sb.Append(b is >= 32 and < 127 ? (char)b : '.'); }
            sb.Append('\n');
        }
        if (d.Length > 4096) sb.Append($"… {d.Length - 4096:N0} more bytes");
        return sb.ToString();
    }

    private async void Follow_Click(object sender, RoutedEventArgs e)
    {
        if ((PacketList.SelectedItem as PacketRow)?.Packet is not { IpProtocol: 6 } p) return;
        var snapshot = _all.ToList();
        var text = await Task.Run(() => TrafficAnalysis.FollowTcpStream(snapshot, p));
        var box = new TextBox { Text = text.TrimStart('\n'), Style = (Style)Application.Current.Resources["MonoOutputStyle"], Height = 520, Width = 900 };
        var dialog = new ContentDialog
        {
            Title = $"TCP stream {p.SrcIp}:{p.SrcPort} ↔ {p.DstIp}:{p.DstPort}",
            Content = box,
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ToolUi.Copy(text);
    }

    private void ConversationFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((PacketList.SelectedItem as PacketRow)?.Packet is not { } p) return;
        FilterBox.Text = p.SrcIp is not null
            ? p.SrcPort is not null
                ? $"ip.addr == {p.SrcIp} && ip.addr == {p.DstIp} && port == {p.SrcPort} && port == {p.DstPort}"
                : $"ip.addr == {p.SrcIp} && ip.addr == {p.DstIp}"
            : $"eth.addr == {p.SrcMac} && eth.addr == {p.DstMac}";
        _filterDelay.Stop();
        ApplyFilter();
    }

    // ---------- side panel ----------

    private void SideBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => RefreshSide();

    private async void RefreshSide()
    {
        _statsDirty = false;
        var snapshot = _all.ToList();
        int tab = SideBar.Items.IndexOf(SideBar.SelectedItem);
        InsightList.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsList.Visibility = tab == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot.Count == 0) { InsightList.ItemsSource = null; StatsList.ItemsSource = null; return; }
        if (tab == 0)
        {
            var insights = await Task.Run(() => TrafficAnalysis.Insights(snapshot));
            InsightList.ItemsSource = insights.Select(i => new InsightRow { Insight = i }).ToList();
        }
        else if (tab == 1)
        {
            var conv = await Task.Run(() => TrafficAnalysis.Conversations(snapshot));
            StatsList.ItemsSource = conv.Take(300).Select(c => new StatRow($"{c.A} ↔ {c.B}",
                $"{c.Packets:N0} pkts · {c.PacketsAtoB:N0} → / ← {c.PacketsBtoA:N0} · {c.Duration.TotalSeconds:0.0}s",
                Size(c.Bytes), c.A.Contains(':') && c.A.Count(ch => ch == ':') == 5 ? $"eth.addr == {c.A} && eth.addr == {c.B}" : $"ip.addr == {c.A} && ip.addr == {c.B}")).ToList();
        }
        else
        {
            var protos = await Task.Run(() => TrafficAnalysis.ProtocolBreakdown(snapshot));
            StatsList.ItemsSource = protos.Select(x => new StatRow(x.Protocol, $"{x.Packets:N0} packets · {x.Packets * 100.0 / snapshot.Count:0.0}%", Size(x.Bytes),
                $"protocol == \"{x.Protocol}\"")).ToList();
        }
    }

    private static string Size(long b) => b switch { >= 1 << 30 => $"{b / (double)(1 << 30):0.0} GB", >= 1 << 20 => $"{b / (double)(1 << 20):0.0} MB", >= 1 << 10 => $"{b / 1024.0:0.0} KB", _ => $"{b} B" };

    private void InsightList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InsightRow { Insight.Filter: { Length: > 0 } f }) { FilterBox.Text = f; _filterDelay.Stop(); ApplyFilter(); }
    }

    private void StatsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StatRow { Filter: { Length: > 0 } f })
        {
            FilterBox.Text = f;
            _filterDelay.Stop();
            ApplyFilter();
        }
    }

    public void Shutdown()
    {
        _drain.Stop();
        _statsTimer.Stop();
        var cap = _capture;
        _capture = null;
        if (cap is not null) { cap.FrameArrived -= OnFrame; Task.Run(cap.Dispose); }
    }
}
