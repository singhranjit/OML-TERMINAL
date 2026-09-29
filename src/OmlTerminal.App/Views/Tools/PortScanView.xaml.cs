using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.App.Views.Tools;

public sealed class PortRow(PortProbeResult r)
{
    public PortProbeResult Result { get; } = r;
    public string Port => Result.Port.ToString();
    public string Protocol => Result.Protocol.ToString().ToUpperInvariant();
    public string State => Result.StateText;
    public string Service => Result.Service;
    public string Rtt => Result.State == PortState.Open ? $"{Result.Latency.TotalMilliseconds:0} ms" : "";
    public string Banner => Result.Banner;
    public Brush StateBrush => ToolUi.Brush(Result.State switch
    {
        PortState.Open => "OmlMintBrush",
        PortState.Closed => "OmlRoseBrush",
        PortState.Filtered => "OmlAmberBrush",
        _ => "OmlVioletBrush",
    });
}

public sealed partial class PortScanView : UserControl, IToolView
{
    private readonly List<PortRow> _all = new();
    private readonly ObservableCollection<PortRow> _shown = new();
    private CancellationTokenSource? _cts;
    private string _lastHost = "";

    public PortScanView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _shown;
    }

    /// <summary>Prefills the target, e.g. from a session's context menu.</summary>
    public void SetTarget(string host) => HostBox.Text = host;

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Scan_Click(sender, e); }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { SummaryText.Text = "Enter a target host."; return; }
        IReadOnlyList<int> ports;
        try { ports = PortSpec.Parse(PortsBox.Text); }
        catch (FormatException ex) { SummaryText.Text = ex.Message; return; }
        if (ports.Count == 0) { SummaryText.Text = "Enter at least one port."; return; }

        _all.Clear();
        _shown.Clear();
        _lastHost = host;
        var protocols = ProtocolBox.SelectedIndex switch
        {
            1 => new[] { PortProtocol.Udp },
            2 => new[] { PortProtocol.Tcp, PortProtocol.Udp },
            _ => new[] { PortProtocol.Tcp },
        };
        int total = ports.Count * protocols.Length, done = 0;
        Progress.Maximum = total;
        Progress.Value = 0;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var scanner = new PortScanner
        {
            Timeout = TimeSpan.FromMilliseconds(double.IsNaN(TimeoutBox.Value) ? 1500 : TimeoutBox.Value),
            GrabBanner = BannerBox.IsChecked == true,
        };
        var started = DateTime.Now;
        try
        {
            foreach (var proto in protocols)
                await scanner.ScanAsync(host, ports, proto, r => DispatcherQueue.TryEnqueue(() =>
                {
                    Add(new PortRow(r));
                    Progress.Value = ++done;
                    UpdateSummary(done, total, started);
                }), _cts.Token);
            // Queued UI updates can still be in flight; let them land before the final summary.
            DispatcherQueue.TryEnqueue(() => UpdateSummary(total, total, started, finished: true));
        }
        catch (OperationCanceledException) { SummaryText.Text = $"Stopped · {Count(PortState.Open)} open"; }
        catch (Exception ex) { SummaryText.Text = ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void Add(PortRow row)
    {
        _all.Add(row);
        if (!Visible(row)) return;
        int i = 0;
        while (i < _shown.Count && ComparePorts(_shown[i], row) < 0) i++;
        _shown.Insert(i, row);
    }

    private static int ComparePorts(PortRow a, PortRow b) =>
        a.Result.Port != b.Result.Port ? a.Result.Port.CompareTo(b.Result.Port) : a.Result.Protocol.CompareTo(b.Result.Protocol);

    /// <summary>Open ports always show; UDP "open or filtered" too, since that's the only answer many UDP services give.</summary>
    private bool Visible(PortRow r) => ShowClosedBox.IsChecked == true || r.Result.State is PortState.Open or PortState.OpenOrFiltered;

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _shown.Clear();
        foreach (var r in _all.Where(Visible).OrderBy(r => r.Result.Port).ThenBy(r => r.Result.Protocol)) _shown.Add(r);
    }

    private int Count(PortState s) => _all.Count(r => r.Result.State == s);

    private void UpdateSummary(int done, int total, DateTime started, bool finished = false)
    {
        var elapsed = DateTime.Now - started;
        SummaryText.Text = $"{(finished ? "Done" : "Scanning")} {done}/{total} · {Count(PortState.Open)} open · {Count(PortState.Closed)} closed · " +
                           $"{Count(PortState.Filtered) + Count(PortState.OpenOrFiltered)} filtered · {elapsed.TotalSeconds:0.0}s";
    }

    private void SetRunning(bool running)
    {
        ScanButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private string BuildCsv()
    {
        var sb = new StringBuilder("host,port,protocol,state,service,rtt_ms,banner\r\n");
        foreach (var r in _all.OrderBy(r => r.Result.Port))
            sb.Append(ToolUi.Csv(_lastHost, r.Port, r.Protocol, r.State, r.Service,
                $"{r.Result.Latency.TotalMilliseconds:0}", r.Banner)).Append("\r\n");
        return sb.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ToolUi.Copy(BuildCsv());

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        await ToolUi.SaveTextAsync($"portquery-{_lastHost}-{DateTime.Now:yyyyMMdd-HHmm}", BuildCsv(), ".csv", "CSV");

    public void Shutdown() => _cts?.Cancel();
}
