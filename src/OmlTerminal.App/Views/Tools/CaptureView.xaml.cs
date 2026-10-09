using System.Collections.Concurrent;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Capture;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class CaptureView : UserControl, IToolView
{
    private const int MaxLines = 5000;
    private readonly ToolContext _ctx;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly LinkedList<string> _lines = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cts;
    private string? _lastPcap;
    private long _lineCount, _pcapBytes;
    private bool _loaded;

    public CaptureView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _flush.Tick += (_, _) => Flush();
        Loaded += async (_, _) =>
        {
            ToolUi.FillSessions(DeviceBox, _ctx.SshSessions(), selectFirst: true);
            _loaded = true;
            await UpdatePlatformAsync();
        };
    }

    private CapturePlatform Platform => PlatformBox.SelectedIndex switch
    {
        1 => CapturePlatform.FortiGateSniffer,
        2 => CapturePlatform.LocalTshark,
        _ => CapturePlatform.RemoteTcpdump,
    };

    private async void Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (ReferenceEquals(sender, PlatformBox)) await UpdatePlatformAsync();
        UpdatePreview();
    }

    private void TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void Clicked(object sender, RoutedEventArgs e) => UpdatePreview();
    private void Count_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();
    private void Interface_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args) => DispatcherQueue.TryEnqueue(UpdatePreview);

    private async Task UpdatePlatformAsync()
    {
        var p = Platform;
        DeviceBox.IsEnabled = p != CapturePlatform.LocalTshark;
        VerbosityBox.IsEnabled = p == CapturePlatform.FortiGateSniffer;
        SudoBox.IsEnabled = p == CapturePlatform.RemoteTcpdump;
        PcapBox.IsEnabled = p != CapturePlatform.FortiGateSniffer;
        if (p == CapturePlatform.FortiGateSniffer) PcapBox.IsChecked = false;

        InterfaceBox.Items.Clear();
        if (p == CapturePlatform.LocalTshark)
        {
            var ifaces = await CaptureCommands.ListLocalInterfacesAsync();
            if (ifaces.Count == 0)
            {
                StatusText.Text = "tshark not found - install Wireshark (with Npcap) to capture on this PC.";
                InterfaceBox.Text = "1";
            }
            else
            {
                foreach (var i in ifaces) InterfaceBox.Items.Add(i);
                InterfaceBox.SelectedIndex = 0;
            }
        }
        else
        {
            foreach (var i in p == CapturePlatform.FortiGateSniffer ? new[] { "any", "wan1", "internal", "port1", "port2" } : new[] { "any", "eth0", "ens160", "ens192", "lo" })
                InterfaceBox.Items.Add(i);
            InterfaceBox.SelectedIndex = 0;
        }
        UpdatePreview();
    }

    private string InterfaceValue()
    {
        var text = (InterfaceBox.SelectedItem as string ?? InterfaceBox.Text ?? "").Trim();
        // tshark -D lines look like "3. \Device\NPF_{GUID} (Ethernet)" - tshark accepts the leading number.
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
        Filter = new CaptureFilter
        {
            Host = HostBox.Text,
            Port = PortBox.Text,
            Protocol = (ProtoBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
            Custom = CustomBox.Text,
        },
        Count = double.IsNaN(CountBox.Value) ? 0 : (int)CountBox.Value,
        UseSudo = SudoBox.IsChecked == true,
        FortiVerbosity = VerbosityBox.SelectedIndex + 1,
        PcapFile = pcapPath,
    };

    private void UpdatePreview()
    {
        if (!_loaded) return;
        CommandPreview.Text = CaptureCommands.Build(BuildRequest(PcapBox.IsChecked == true ? "capture.pcap" : null));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var device = DeviceBox.SelectedItem as SessionProfile;
        if (Platform != CapturePlatform.LocalTshark && device is null)
        {
            StatusText.Text = "Pick a saved SSH session to capture on (add one with New Session → SSH).";
            return;
        }

        string? pcap = null;
        if (PcapBox.IsChecked == true)
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
            await Task.Run(() => runner.RunAsync(request, device, _cts.Token));
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
            WiresharkButton.IsEnabled = _lastPcap is not null && File.Exists(_lastPcap);
            if (_lastPcap is not null && File.Exists(_lastPcap))
                StatusText.Text = $"Saved {new FileInfo(_lastPcap).Length:N0} bytes to {_lastPcap}";
        }
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        PlatformBox.IsEnabled = !running;
        LiveDot.Fill = ToolUi.Brush(running ? "StatusErrorBrush" : "StatusIdleBrush"); // red = recording
        if (running) StatusText.Text = "Capturing...";
    }

    private void Flush()
    {
        bool any = false;
        while (_pending.TryDequeue(out var line))
        {
            _lines.AddLast(line);
            if (_lines.Count > MaxLines) _lines.RemoveFirst();
            any = true;
        }
        if (_cts is not null)
            StatusText.Text = _lastPcap is not null
                ? $"Capturing → {Path.GetFileName(_lastPcap)} · {Interlocked.Read(ref _pcapBytes):N0} bytes"
                : $"Capturing · {Interlocked.Read(ref _lineCount):N0} lines";
        if (!any) return;
        Output.Text = string.Join('\n', _lines);
        if (AutoScroll.IsChecked == true) Output.SelectionStart = Output.Text.Length;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        Output.Text = "";
    }

    private async void SaveText_Click(object sender, RoutedEventArgs e) =>
        await ToolUi.SaveTextAsync($"capture-{DateTime.Now:yyyyMMdd-HHmm}", string.Join(Environment.NewLine, _lines));

    private void Wireshark_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPcap is null) return;
        var ws = CaptureCommands.FindWireshark();
        if (ws is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ws, $"\"{_lastPcap}\"") { UseShellExecute = false })?.Dispose();
        else ToolUi.OpenInExplorer(_lastPcap);
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _flush.Stop();
    }
}
