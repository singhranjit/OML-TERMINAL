using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Capture;

namespace OmlTerminal.Desktop.Tools;

public sealed class PacketAnalyzerTool : UserControl, IToolView
{
    private sealed record PacketRow(DecodedPacket Packet, string Time, IBrush Brush);
    private sealed record StatRow(string Title, string Subtitle, string Value, string Filter);

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

    private readonly ComboBox _iface = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Pick an adapter" };
    private readonly TextBox _captureFilter = Ui.Input("", "host 10.0.0.5 and not port 22", mono: true);
    private readonly CheckBox _promisc = Ui.Check("Promiscuous", true), _monitor = Ui.Check("Monitor mode");
    private readonly Button _startButton, _stopButton;
    private readonly TextBlock _status = Ui.Text("", 12.5, color: Ui.Muted);
    private readonly Border _pcapBar;
    private readonly TextBlock _pcapText;
    private readonly TextBox _displayFilter = Ui.Input("", "Display filter: dns · tcp.port == 443 · ip.addr == 10.0.0.5 · tcp.flags.reset · arp", mono: true);
    private readonly TextBlock _count = Ui.Text("", 12, color: Ui.Muted);
    private readonly DataGrid _packets;
    private readonly CheckBox _followLive = Ui.Check("Follow live", true);
    private readonly TextBlock _empty = Ui.Text("Start a capture on an adapter, or open a .pcap/.pcapng file. Problems (retransmissions, resets, DNS failures, ICMP errors...) are pointed out on the right.", 13, color: Ui.Muted);
    private readonly TextBlock _detailTitle = Ui.Section("Packet details");
    private readonly TreeView _tree = new();
    private readonly TextBox _hex = Ui.Output();
    private readonly Button _followTcp, _conversationFilter;
    private readonly ToggleButton[] _sideTabs;
    private readonly ItemsControl _insights = new(), _stats = new();

    public PacketAnalyzerTool(ToolContext ctx)
    {
        _pcapBar = Ui.Banner(out _pcapText, Ui.Sky);
        _startButton = Ui.Button("Start", Start, accent: true);
        _stopButton = Ui.Button("Stop", () => { StopCapture(); _status.Text = $"Stopped · {_all.Count:N0} packets."; });
        _stopButton.IsEnabled = false;
        _iface.ItemTemplate = new FuncDataTemplate<CaptureInterface>((i, _) => new TextBlock { Text = i?.ToString() });
        _iface.SelectionChanged += (_, _) =>
        {
            var i = _iface.SelectedItem as CaptureInterface;
            _monitor.IsEnabled = i is { IsWireless: true, ViaNpcap: true };
            if (!_monitor.IsEnabled) _monitor.IsChecked = false;
        };
        _hex.FontSize = 11.5;
        _empty.Margin = new Thickness(12, 44, 12, 0); // below the table header

        _packets = Ui.Table(
            Ui.Col<PacketRow>("No.", r => r.Packet.Number.ToString(), 56, r => r.Brush, mono: true, sortKey: r => r.Packet.Number),
            Ui.Col<PacketRow>("Time", r => r.Time, 86, r => r.Brush, mono: true, sortKey: r => r.Packet.Timestamp),
            Ui.Col<PacketRow>("Source", r => r.Packet.Source, 128, r => r.Brush, mono: true),
            Ui.Col<PacketRow>("Destination", r => r.Packet.Destination, 128, r => r.Brush, mono: true),
            Ui.Col<PacketRow>("Protocol", r => r.Packet.Protocol, 66, r => r.Brush),
            Ui.Col<PacketRow>("Length", r => r.Packet.OriginalLength.ToString(), 56, r => r.Brush, mono: true, sortKey: r => r.Packet.OriginalLength),
            Ui.Col<PacketRow>("Info", r => r.Packet.Info, fill: true, color: r => r.Brush));
        _packets.ItemsSource = _shown;
        _packets.SelectionMode = DataGridSelectionMode.Single;
        _packets.RowHeight = 22;
        _packets.SelectionChanged += (_, e) =>
        {
            var p = (_packets.SelectedItem as PacketRow)?.Packet;
            ShowDetails(p);
            // Looking at an older packet: stop jumping to the newest one.
            if (p is not null && _capture is not null && e.AddedItems.Count > 0 && _packets.SelectedIndex < _shown.Count - 1) _followLive.IsChecked = false;
        };

        _displayFilter.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _filterDelay.Stop(); ApplyFilter(); } };
        _displayFilter.TextChanged += (_, _) => { _filterDelay.Stop(); _filterDelay.Start(); };
        _drain.Tick += (_, _) => Drain();
        _filterDelay.Tick += (_, _) => { _filterDelay.Stop(); ApplyFilter(); };
        _statsTimer.Tick += (_, _) => { if (_statsDirty) _ = RefreshSideAsync(); };

        _followTcp = Ui.Button("Follow TCP stream", () => _ = FollowAsync(), tip: "Show the whole TCP conversation's payload in order");
        _conversationFilter = Ui.Button("Filter conversation", ConversationFilter, tip: "Filter to this packet's conversation");
        _followTcp.IsEnabled = _conversationFilter.IsEnabled = false;

        _sideTabs = new[] { "Problems", "Conversations", "Protocols" }.Select((t, i) =>
        {
            var b = new ToggleButton { Content = t, IsChecked = i == 0 };
            b.Click += (_, _) => { for (int k = 0; k < 3; k++) _sideTabs![k].IsChecked = k == i; _ = RefreshSideAsync(); };
            return b;
        }).ToArray();
        _insights.ItemTemplate = new FuncDataTemplate<CaptureInsight>((x, _) =>
        {
            if (x is null) return new Panel();
            var brush = x.Severity switch { InsightSeverity.Problem => Ui.Rose, InsightSeverity.Warning => Ui.Amber, _ => Ui.Sky };
            var b = new Border
            {
                BorderBrush = brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8), Background = Ui.CardBack,
                Cursor = x.Filter.Length > 0 ? new Cursor(StandardCursorType.Hand) : null,
                Child = Ui.Stack(2, new TextBlock { Text = x.Title, Foreground = brush, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = x.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = x.Filter.Length > 0 ? $"Click to filter: {x.Filter}" : "", FontSize = 11, Foreground = Ui.Muted, FontFamily = Ui.Mono, IsVisible = x.Filter.Length > 0 }),
            };
            b.PointerPressed += (_, _) => SetFilter(x.Filter);
            return b;
        });
        _stats.ItemTemplate = new FuncDataTemplate<StatRow>((s, _) =>
        {
            if (s is null) return new Panel();
            var b = new Border
            {
                Padding = new Thickness(8, 5), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6), Background = Ui.CardBack, Cursor = new Cursor(StandardCursorType.Hand),
                Child = new DockPanel { Children = {
                    new TextBlock { Text = s.Value, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Sky, [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center },
                    Ui.Stack(1, new TextBlock { Text = s.Title, FontFamily = Ui.Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = s.Subtitle, FontSize = 11, Foreground = Ui.Muted }) } },
            };
            b.PointerPressed += (_, _) => SetFilter(s.Filter);
            return b;
        });
        _stats.IsVisible = false;

        var quick = Ui.Row(new[] { ("Problems", "tcp.analysis.flags || tcp.flags.reset || dns.flags.rcode != 0 || icmp.type == 3"), ("DNS", "dns"), ("DHCP", "dhcp"), ("ARP", "arp"),
            ("HTTP(S)", "tcp.port == 80 || tcp.port == 443"), ("Clear", "") }.Select(q => (Control)Ui.Button(q.Item1, () => SetFilter(q.Item2))).ToArray());
        var listCard = Ui.Card(new DockPanel { Children = {
            WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 6), Children = { WithDock(_followLive, Dock.Right), Ui.Section("Packets") } }, Dock.Top),
            new Panel { Children = { _packets, _empty } } } });
        var detailCard = Ui.Card(new DockPanel { Children = {
            WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 6), Children = { WithDock(Ui.Row(_followTcp, _conversationFilter), Dock.Right), _detailTitle } }, Dock.Top),
            Ui.Columns("*,*", new ScrollViewer { Content = _tree }, _hex) } });
        var left = new Grid { RowDefinitions = RowDefinitions.Parse("3*,2*"), RowSpacing = 12 };
        left.Children.Add(listCard);
        Grid.SetRow(detailCard, 1);
        left.Children.Add(detailCard);
        var side = Ui.Card(new DockPanel { Children = {
            WithDock(Ui.Row(_sideTabs), Dock.Top),
            new ScrollViewer { Content = new Panel { Children = { _insights, _stats } }, Margin = new Thickness(0, 8, 0, 0) } } });
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,300"), ColumnSpacing = 14 };
        body.Children.Add(left);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);

        Content = Ui.Page("Packet Analyzer",
            "Capture on this computer's Ethernet or Wi-Fi, or open a pcap - decode, filter, follow TCP streams and get problems pointed out in plain English.",
            body,
            Ui.Columns("*,*,Auto,Auto,Auto,Auto,Auto,Auto", Ui.Field("Adapter", _iface), Ui.Field("Capture filter (BPF, optional)", _captureFilter), _promisc, _monitor,
                _startButton, _stopButton, Ui.Button("Open...", () => _ = OpenAsync()), Ui.Button("Save...", () => _ = SaveAsync())),
            _pcapBar,
            new DockPanel { Children = { WithDock(Ui.Row(Ui.Button("Clear", Clear), _count), Dock.Right), _displayFilter } },
            Ui.Row(quick, _status));
        _displayFilter.Margin = new Thickness(0, 0, 8, 0);
        AttachedToVisualTree += (_, _) => LoadInterfaces();
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void LoadInterfaces()
    {
        try
        {
            var list = CaptureEngine.ListInterfaces();
            var keep = (_iface.SelectedItem as CaptureInterface)?.Id;
            _iface.ItemsSource = list;
            var again = list.FirstOrDefault(i => i.Id == keep);
            if (again is not null) _iface.SelectedItem = again;
            else if (list.Count > 0) _iface.SelectedIndex = 0;
            else _status.Text = "No capture-capable adapters found.";
            Ui.Show(_pcapBar, _pcapText, OperatingSystem.IsWindows() && !CaptureEngine.NpcapInstalled
                ? "Npcap isn't installed - capture is limited to IPv4 on one adapter (and needs administrator). Install Npcap (npcap.com) for full Ethernet and Wi-Fi capture."
                : null);
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't list adapters: {ex.Message}";
            Ui.Show(_pcapBar, _pcapText, ex.Message + " You can still open saved .pcap files.");
        }
    }

    // ---------- capture ----------

    private void Start()
    {
        if (_capture is not null || _iface.SelectedItem is not CaptureInterface iface) return;
        try
        {
            var cap = CaptureEngine.Open(iface, (_captureFilter.Text ?? "").Trim(), _promisc.IsChecked == true, _monitor.IsChecked == true);
            cap.FrameArrived += OnFrame;
            cap.Failed += msg => Dispatcher.UIThread.Post(() => { _status.Text = msg; StopCapture(); });
            cap.Start();
            _capture = cap;
            if (_all.Count == 0) _start = DateTime.Now;
        }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        _startButton.IsEnabled = false;
        _stopButton.IsEnabled = true;
        _iface.IsEnabled = _captureFilter.IsEnabled = false;
        _status.Text = $"Capturing on {iface.Name}…";
        _drain.Start();
        _statsTimer.Start();
    }

    /// <summary>Decoding happens on the capture thread, so the UI only appends finished rows.</summary>
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
        if (_followLive.IsChecked == true && _shown.Count > 0) _packets.ScrollIntoView(_shown[^1], null);
        if (_nextNumber > MaxPackets && _capture is not null)
        {
            StopCapture();
            _status.Text = $"Stopped at {MaxPackets:N0} packets to protect memory - save, clear, then capture again (a capture filter keeps it focused).";
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        _empty.IsVisible = _all.Count == 0;
        _count.Text = _filter == DisplayFilter.All ? $"{_all.Count:N0} packets" : $"{_shown.Count:N0} of {_all.Count:N0} shown";
        if (_capture is not null && _iface.SelectedItem is CaptureInterface iface)
        {
            var (_, drop) = _capture.Statistics;
            _status.Text = $"Capturing on {iface.Name} · {_all.Count:N0} packets{(drop > 0 ? $" · {drop:N0} dropped by the driver" : "")}";
        }
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
        _startButton.IsEnabled = true;
        _stopButton.IsEnabled = false;
        _iface.IsEnabled = _captureFilter.IsEnabled = true;
        _ = RefreshSideAsync();
    }

    private void Clear()
    {
        while (_incoming.TryDequeue(out _)) { }
        _all.Clear();
        _shown.Clear();
        _nextNumber = 0;
        _start = DateTime.Now;
        ShowDetails(null);
        _ = RefreshSideAsync();
        UpdateCounts();
        if (_capture is null) _status.Text = "Cleared.";
    }

    // ---------- files ----------

    /// <summary>Opens a capture file (also used by Packet Capture's "open" when Wireshark isn't installed).</summary>
    public void OpenFile(string path) => _ = LoadFileAsync(path);

    private async Task OpenAsync()
    {
        if (_capture is not null) { _status.Text = "Stop the capture first."; return; }
        if (await ToolUi.PickOpenPathAsync("Open a capture", ("Packet captures", ["*.pcap", "*.pcapng", "*.cap", "*.dmp"]), ("All files", ["*"])) is { } path)
            await LoadFileAsync(path);
    }

    private async Task LoadFileAsync(string path)
    {
        var name = Path.GetFileName(path);
        _status.Text = $"Reading {name}…";
        try
        {
            bool truncated = false;
            var packets = await Task.Run(() =>
            {
                var frames = PcapReader.Read(path, 512L << 20, out truncated);
                truncated |= frames.Count > MaxPackets;
                return frames.Take(MaxPackets).Select((f, i) => PacketDecoder.Decode(f.Data, f.LinkType, i + 1, f.Timestamp, f.OriginalLength, details: false)).ToList();
            });
            Clear();
            _all.AddRange(packets);
            _nextNumber = packets.Count;
            _start = packets.FirstOrDefault()?.Timestamp ?? DateTime.Now;
            ApplyFilter();
            await RefreshSideAsync();
            _status.Text = $"{name} · {packets.Count:N0} packets" + (truncated ? " - the first part of a larger file (split big captures with editcap to see the rest)" : "");
        }
        catch (Exception ex) { _status.Text = $"Couldn't open {name}: {ex.Message}"; }
    }

    private async Task SaveAsync()
    {
        var packets = _shown.Select(r => r.Packet).ToList();
        if (packets.Count == 0) { _status.Text = "Nothing to save."; return; }
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
            _status.Text = $"Saved {packets.Count:N0} packets to {Path.GetFileName(path)}";
        }
        catch (Exception ex) { _status.Text = $"Couldn't save: {ex.Message}"; }
    }

    // ---------- filtering ----------

    private void SetFilter(string filter)
    {
        _displayFilter.Text = filter;
        _filterDelay.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        try
        {
            _filter = DisplayFilter.Parse(_displayFilter.Text ?? "");
            _displayFilter.BorderBrush = _filter == DisplayFilter.All ? null : Ui.Mint;
            ToolTip.SetTip(_displayFilter, null);
        }
        catch (FilterSyntaxException ex)
        {
            _displayFilter.BorderBrush = Ui.Rose;
            ToolTip.SetTip(_displayFilter, ex.Message);
            _count.Text = ex.Message;
            return;
        }
        var selected = (_packets.SelectedItem as PacketRow)?.Packet;
        // A fresh collection rather than Clear()+Add per row: one reset instead of hundreds of thousands of events.
        var rows = new ObservableCollection<PacketRow>(_all.Where(_filter.Matches).Select(Row));
        _shown.Clear();
        _packets.ItemsSource = null;
        foreach (var r in rows) _shown.Add(r);
        _packets.ItemsSource = _shown;
        UpdateCounts();
        if (selected is not null && _shown.FirstOrDefault(r => r.Packet == selected) is { } row)
        {
            _packets.SelectedItem = row;
            _packets.ScrollIntoView(row, null);
        }
    }

    private PacketRow Row(DecodedPacket p) => new(p, (p.Timestamp - _start).TotalSeconds.ToString("0.000000"), BrushFor(p));

    private static readonly IBrush Normal = Ui.Solid(0xE6EDF3);

    private static IBrush BrushFor(DecodedPacket p) =>
        p.Malformed || p.Flags.HasFlag(TcpFlags.Rst) || p.DnsResponse && p.DnsRcode != 0 || p.IcmpType is 3 or 11 && p.IpProtocol == 1 ? Ui.Rose
        : p.Protocol switch
        {
            "DNS" or "MDNS" or "LLMNR" => Ui.Violet,
            "ARP" => Ui.Amber,
            "DHCP" => Ui.Mint,
            "ICMP" or "ICMPv6" => Ui.Sky,
            "STP" or "PVST+" or "CDP" or "LLDP" or "DTP" or "LACP" or "UDLD" => Ui.Muted,
            "TCP" when p.Flags.HasFlag(TcpFlags.Syn) => Ui.Orange,
            _ => Normal,
        };

    // ---------- details ----------

    private void ShowDetails(DecodedPacket? p)
    {
        _followTcp.IsEnabled = p?.IpProtocol == 6;
        _conversationFilter.IsEnabled = p?.SrcIp is not null || p?.SrcMac is not null;
        if (p is null) { _tree.ItemsSource = null; _hex.Text = ""; _detailTitle.Text = "PACKET DETAILS"; return; }
        _detailTitle.Text = $"PACKET {p.Number} · {p.OriginalLength} BYTES · {p.Timestamp:HH:mm:ss.ffffff}";
        var nodes = new List<TreeViewItem>();
        foreach (var layer in p.Layers)
            nodes.Add(new TreeViewItem
            {
                Header = new TextBlock { Text = $"{layer.Name}  —  {layer.Summary}", FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
                IsExpanded = p.Layers.Count <= 3 || layer == p.Layers[^1],
                ItemsSource = layer.Fields.Where(f => f.Value.Length > 0)
                    .Select(f => new TreeViewItem { Header = new SelectableTextBlock { Text = $"{f.Name}: {f.Value}", FontFamily = Ui.Mono, FontSize = 12 } }).ToList(),
            });
        if (p.Layers.Count == 0) nodes.Add(new TreeViewItem { Header = p.Info });
        _tree.ItemsSource = nodes;
        _hex.Text = Hex(p.Data);
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

    private async Task FollowAsync()
    {
        if ((_packets.SelectedItem as PacketRow)?.Packet is not { IpProtocol: 6 } p || MainWindow.Current is not { } owner) return;
        var snapshot = _all.ToList();
        var text = await Task.Run(() => TrafficAnalysis.FollowTcpStream(snapshot, p));
        var box = Ui.Output(text: text.TrimStart('\n'));
        var w = new Window
        {
            Title = $"TCP stream {p.SrcIp}:{p.SrcPort} ↔ {p.DstIp}:{p.DstPort}", Width = 900, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel { Margin = new Thickness(16), Children = {
                WithDock(Ui.Row(Ui.Button("Copy", () => ToolUi.Copy(text))), Dock.Bottom), box } },
        };
        await w.ShowDialog(owner);
    }

    private void ConversationFilter()
    {
        if ((_packets.SelectedItem as PacketRow)?.Packet is not { } p) return;
        SetFilter(p.SrcIp is not null
            ? p.SrcPort is not null
                ? $"ip.addr == {p.SrcIp} && ip.addr == {p.DstIp} && port == {p.SrcPort} && port == {p.DstPort}"
                : $"ip.addr == {p.SrcIp} && ip.addr == {p.DstIp}"
            : $"eth.addr == {p.SrcMac} && eth.addr == {p.DstMac}");
    }

    // ---------- side panel ----------

    private async Task RefreshSideAsync()
    {
        _statsDirty = false;
        var snapshot = _all.ToList();
        int tab = Array.FindIndex(_sideTabs, t => t.IsChecked == true);
        _insights.IsVisible = tab == 0;
        _stats.IsVisible = tab != 0;
        if (snapshot.Count == 0) { _insights.ItemsSource = null; _stats.ItemsSource = null; return; }
        if (tab == 0) _insights.ItemsSource = await Task.Run(() => TrafficAnalysis.Insights(snapshot));
        else if (tab == 1)
        {
            var conv = await Task.Run(() => TrafficAnalysis.Conversations(snapshot));
            _stats.ItemsSource = conv.Take(300).Select(c => new StatRow($"{c.A} ↔ {c.B}",
                $"{c.Packets:N0} pkts · {c.PacketsAtoB:N0} → / ← {c.PacketsBtoA:N0} · {c.Duration.TotalSeconds:0.0}s", Size(c.Bytes),
                c.A.Count(ch => ch == ':') == 5 ? $"eth.addr == {c.A} && eth.addr == {c.B}" : $"ip.addr == {c.A} && ip.addr == {c.B}")).ToList();
        }
        else
        {
            var protos = await Task.Run(() => TrafficAnalysis.ProtocolBreakdown(snapshot));
            _stats.ItemsSource = protos.Select(x => new StatRow(x.Protocol, $"{x.Packets:N0} packets · {x.Packets * 100.0 / snapshot.Count:0.0}%", Size(x.Bytes),
                $"protocol == \"{x.Protocol}\"")).ToList();
        }
    }

    private static string Size(long b) => b switch { >= 1 << 30 => $"{b / (double)(1 << 30):0.0} GB", >= 1 << 20 => $"{b / (double)(1 << 20):0.0} MB", >= 1 << 10 => $"{b / 1024.0:0.0} KB", _ => $"{b} B" };

    public void Shutdown()
    {
        _drain.Stop();
        _statsTimer.Stop();
        var cap = _capture;
        _capture = null;
        if (cap is not null) { cap.FrameArrived -= OnFrame; Task.Run(cap.Dispose); }
    }
}
