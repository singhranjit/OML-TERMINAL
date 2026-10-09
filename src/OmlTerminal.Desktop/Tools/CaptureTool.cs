using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Desktop.Tools;

public sealed class CaptureTool : UserControl, IToolView
{
    private const int MaxLines = 5000;
    private static readonly string[] Protocols = ["", "tcp", "udp", "icmp", "arp", "icmp6"];

    private readonly ToolContext _ctx;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly LinkedList<string> _lines = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cts;
    private string? _lastPcap;
    private long _lineCount, _pcapBytes;
    private bool _loaded;

    private readonly ComboBox _platform = Ui.Combo(new[] { "Linux/Unix host (tcpdump over SSH)", "FortiGate (diagnose sniffer)", "This computer (tshark)" });
    private readonly ComboBox _device = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
    private readonly AutoCompleteBox _interface = new() { FilterMode = AutoCompleteFilterMode.None, MinimumPrefixLength = 0, FontFamily = Ui.Mono };
    private readonly NumericUpDown _count = Ui.Number(0, 0, 1_000_000);
    private readonly TextBox _host = Ui.Input("", "10.0.0.5, 10.1.0.0/16", mono: true), _port = Ui.Input("", "443, 500-501", mono: true), _custom = Ui.Input("", "not port 22", mono: true);
    private readonly ComboBox _proto = Ui.Combo(new[] { "any", "tcp", "udp", "icmp", "arp", "icmp6" });
    private readonly ComboBox _verbosity = Ui.Combo(new[] { "1 · headers", "2 · + IP payload", "3 · + ethernet", "4 · headers + intf", "5 · payload + intf", "6 · full + intf" }, 3);
    private readonly CheckBox _sudo = Ui.Check("sudo", true), _pcap = Ui.Check("Save to .pcap"), _follow = Ui.Check("Follow", true);
    private readonly TextBox _preview = new() { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 12 };
    private readonly Button _start, _stop, _wireshark;
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 10, Height = 10, Fill = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Text("Idle", 12, mono: true);
    private readonly TextBox _output = Ui.Output();

    public CaptureTool(ToolContext ctx)
    {
        _ctx = ctx;
        _start = Ui.Button("Start capture", () => _ = StartAsync(), accent: true);
        _stop = Ui.Button("Stop", () => _cts?.Cancel());
        _stop.IsEnabled = false;
        _wireshark = Ui.Button("Open in Wireshark", OpenWireshark, tip: "Open the saved capture in Wireshark");
        _wireshark.IsEnabled = false;
        _flush.Tick += (_, _) => Flush();
        _platform.SelectionChanged += async (_, _) => { if (_loaded) await UpdatePlatformAsync(); };
        foreach (var c in new SelectingItemsControl[] { _proto, _verbosity }) c.SelectionChanged += (_, _) => UpdatePreview();
        foreach (var t in new[] { _host, _port, _custom }) t.TextChanged += (_, _) => UpdatePreview();
        _interface.TextChanged += (_, _) => UpdatePreview();
        _count.ValueChanged += (_, _) => UpdatePreview();
        foreach (var c in new[] { _sudo, _pcap }) c.IsCheckedChanged += (_, _) => UpdatePreview();

        var statusRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = {
            WithDock(Ui.Row(_follow, Ui.Button("Clear", () => { _lines.Clear(); _output.Text = ""; }),
                Ui.Button("Save text...", () => _ = ToolUi.SaveTextAsync($"capture-{DateTime.Now:yyyyMMdd-HHmm}", string.Join(Environment.NewLine, _lines)))), Dock.Right),
            Ui.Row(_dot, _status) } };
        var body = new DockPanel { Children = { WithDock(statusRow, Dock.Top), _output } };
        Content = Ui.Page("Packet Capture",
            "Capture on a remote Linux host or FortiGate over SSH, or on this computer with tshark - with the filter built for you. Save to .pcap and open it in Wireshark or the Packet Analyzer.",
            body,
            Ui.Columns("260,*,200,110", Ui.Field("Capture on", _platform), Ui.Field("Device (saved SSH session)", _device), Ui.Field("Interface", _interface), Ui.Field("Packets (0 = ∞)", _count)),
            Ui.Columns("*,140,140,*,190,Auto", Ui.Field("Host / net", _host), Ui.Field("Port(s)", _port), Ui.Field("Protocol", _proto), Ui.Field("Extra BPF (ANDed)", _custom),
                Ui.Field("FortiGate verbosity", _verbosity), _sudo),
            Ui.Columns("Auto,*,Auto,Auto,Auto", _pcap, _preview, _start, _stop, _wireshark));
        AttachedToVisualTree += async (_, _) =>
        {
            ToolUi.FillSessions(_device, _ctx.SshSessions(), selectFirst: true);
            if (_loaded) return;
            _loaded = true;
            await UpdatePlatformAsync();
        };
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private CapturePlatform Platform => _platform.SelectedIndex switch
    {
        1 => CapturePlatform.FortiGateSniffer,
        2 => CapturePlatform.LocalTshark,
        _ => CapturePlatform.RemoteTcpdump,
    };

    private async Task UpdatePlatformAsync()
    {
        var p = Platform;
        _device.IsEnabled = p != CapturePlatform.LocalTshark;
        _verbosity.IsEnabled = p == CapturePlatform.FortiGateSniffer;
        _sudo.IsEnabled = p == CapturePlatform.RemoteTcpdump;
        _pcap.IsEnabled = p != CapturePlatform.FortiGateSniffer;
        if (p == CapturePlatform.FortiGateSniffer) _pcap.IsChecked = false;
        if (p == CapturePlatform.LocalTshark)
        {
            var ifaces = await CaptureCommands.ListLocalInterfacesAsync();
            _interface.ItemsSource = ifaces;
            _interface.Text = ifaces.Count > 0 ? ifaces[0] : "1";
            if (ifaces.Count == 0) _status.Text = OperatingSystem.IsWindows()
                ? "tshark not found - install Wireshark (with Npcap) to capture on this PC."
                : "tshark not found (or no capture permission) - install it (sudo apt install tshark) and add yourself to the wireshark group.";
        }
        else
        {
            var list = p == CapturePlatform.FortiGateSniffer ? new[] { "any", "wan1", "internal", "port1", "port2" } : new[] { "any", "eth0", "ens160", "ens192", "lo" };
            _interface.ItemsSource = list;
            _interface.Text = list[0];
        }
        UpdatePreview();
    }

    private string InterfaceValue()
    {
        var text = (_interface.Text ?? "").Trim();
        // tshark -D lines look like "3. eth0" - tshark accepts the leading number.
        if (Platform == CapturePlatform.LocalTshark)
        {
            int dot = text.IndexOf('.');
            if (dot > 0 && int.TryParse(text[..dot], out var n)) return n.ToString();
        }
        return text.Length == 0 ? "any" : text;
    }

    private CaptureRequest BuildRequest(string? pcapPath) => new()
    {
        Platform = Platform,
        Interface = InterfaceValue(),
        Filter = new CaptureFilter { Host = _host.Text ?? "", Port = _port.Text ?? "", Protocol = Protocols[Math.Max(0, _proto.SelectedIndex)], Custom = _custom.Text ?? "" },
        Count = Ui.IntValue(_count, 0),
        UseSudo = _sudo.IsChecked == true,
        FortiVerbosity = _verbosity.SelectedIndex + 1,
        PcapFile = pcapPath,
    };

    private void UpdatePreview()
    {
        if (!_loaded) return;
        _preview.Text = CaptureCommands.Build(BuildRequest(_pcap.IsChecked == true ? "capture.pcap" : null));
    }

    private async Task StartAsync()
    {
        if (_cts is not null) return;
        var device = _device.SelectedItem as SessionProfile;
        if (Platform != CapturePlatform.LocalTshark && device is null) { _status.Text = "Pick a saved SSH session to capture on."; return; }
        string? pcap = null;
        if (_pcap.IsChecked == true)
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "captures");
            Directory.CreateDirectory(dir);
            var who = device?.Name ?? "local";
            pcap = Path.Combine(dir, $"{string.Concat(who.Split(Path.GetInvalidFileNameChars()))}_{DateTime.Now:yyyyMMdd_HHmmss}.{(Platform == CapturePlatform.LocalTshark ? "pcapng" : "pcap")}");
        }
        var request = BuildRequest(pcap);
        var runner = new PacketCaptureRunner();
        runner.LineReceived += line => { _pending.Enqueue(line); Interlocked.Increment(ref _lineCount); };
        runner.BytesWritten += total => Interlocked.Exchange(ref _pcapBytes, total);
        _cts = new CancellationTokenSource();
        _lineCount = 0;
        _pcapBytes = 0;
        _lastPcap = pcap;
        SetRunning(true);
        _flush.Start();
        try
        {
            var ct = _cts.Token;
            await Task.Run(() => runner.RunAsync(request, device, ct));
            _pending.Enqueue("-- capture finished --");
        }
        catch (OperationCanceledException) { _pending.Enqueue("-- capture stopped --"); }
        catch (Exception ex) { _pending.Enqueue($"error: {ex.Message}"); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Flush();
            _flush.Stop();
            SetRunning(false);
            _wireshark.IsEnabled = _lastPcap is not null && File.Exists(_lastPcap);
            if (_lastPcap is not null && File.Exists(_lastPcap)) _status.Text = $"Saved {new FileInfo(_lastPcap).Length:N0} bytes to {_lastPcap}";
        }
    }

    private void SetRunning(bool running)
    {
        _start.IsEnabled = !running;
        _stop.IsEnabled = running;
        _platform.IsEnabled = !running;
        _dot.Fill = running ? Ui.Rose : Ui.Muted; // red = recording
        if (running) _status.Text = "Capturing...";
    }

    private void Flush()
    {
        bool any = false;
        string last = "";
        while (_pending.TryDequeue(out var line))
        {
            _lines.AddLast(line);
            if (_lines.Count > MaxLines) _lines.RemoveFirst();
            last = line;
            any = true;
        }
        if (_cts is not null)
            _status.Text = _lastPcap is not null
                ? $"Capturing → {Path.GetFileName(_lastPcap)} · {Interlocked.Read(ref _pcapBytes):N0} bytes"
                : $"Capturing · {Interlocked.Read(ref _lineCount):N0} lines";
        if (!any) return;
        _output.Text = string.Join('\n', _lines);
        if (_follow.IsChecked == true) _output.CaretIndex = _output.Text.Length - last.Length;
    }

    private void OpenWireshark()
    {
        if (_lastPcap is null) return;
        if (CaptureCommands.FindWireshark() is { } ws) Process.Start(new ProcessStartInfo(ws, [_lastPcap]) { UseShellExecute = false })?.Dispose();
        else _ctx.OpenTool("analyzer", c => (c as PacketAnalyzerTool)?.OpenFile(_lastPcap));
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _flush.Stop();
    }
}
