using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Desktop.Tools;

public sealed class PortRow(PortProbeResult r)
{
    public PortProbeResult Result { get; } = r;
    public string Port => Result.Port.ToString();
    public string Protocol => Result.Protocol.ToString().ToUpperInvariant();
    public string State => Result.StateText;
    public string Service => Result.Service;
    public string Rtt => Result.State == PortState.Open ? $"{Result.Latency.TotalMilliseconds:0} ms" : "";
    public string Banner => Result.Banner;
    public IBrush StateBrush => Result.State switch
    {
        PortState.Open => Ui.Mint, PortState.Closed => Ui.Rose, PortState.Filtered => Ui.Amber, _ => Ui.Violet,
    };
}

public sealed class PortScanTool : UserControl, IToolView
{
    private readonly List<PortRow> _all = new();
    private readonly ObservableCollection<PortRow> _shown = new();
    private CancellationTokenSource? _cts;
    private string _lastHost = "";

    private readonly TextBox _host = Ui.Input("", "10.0.0.1 or fw01.corp.local", mono: true);
    private readonly TextBox _ports = Ui.Input("common", "22,443,8000-8010 or common", mono: true);
    private readonly ComboBox _protocol = Ui.Combo(new[] { "TCP", "UDP", "TCP + UDP" });
    private readonly NumericUpDown _timeout = Ui.Number(1500, 100, 30000, 100);
    private readonly CheckBox _banner = Ui.Check("Grab service banner", true);
    private readonly CheckBox _showClosed = Ui.Check("Show closed / filtered");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Width = 220 };
    private readonly TextBlock _summary = Ui.Text("Idle", 12, mono: true, color: Ui.Muted);
    private readonly Button _scan, _stop;

    public PortScanTool()
    {
        _scan = Ui.Button("Query", () => _ = ScanAsync(), accent: true);
        _stop = Ui.Button("Stop", () => _cts?.Cancel());
        _stop.IsEnabled = false;
        var table = Ui.Table(
            Ui.Col<PortRow>("Port", r => r.Port, 80, sortKey: r => r.Result.Port, mono: true),
            Ui.Col<PortRow>("Proto", r => r.Protocol, 70),
            Ui.Col<PortRow>("State", r => r.State, 130, r => r.StateBrush),
            Ui.Col<PortRow>("Service", r => r.Service, 140),
            Ui.Col<PortRow>("RTT", r => r.Rtt, 80, sortKey: r => r.Result.Latency),
            Ui.Col<PortRow>("Banner", r => r.Banner, mono: true, fill: true));
        table.ItemsSource = _shown;
        foreach (var box in new[] { _host, _ports })
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = ScanAsync(); } };
        _showClosed.IsCheckedChanged += (_, _) => Refilter();

        Content = Ui.Page("Port Query", "Check whether TCP/UDP ports answer, with service names and banners. 'common' scans the 100 most useful ports.", table,
            Ui.Columns("2*,2*,130,120,Auto,Auto",
                Ui.Field("Target host", _host), Ui.Field("Ports", _ports), Ui.Field("Protocol", _protocol), Ui.Field("Timeout (ms)", _timeout), _scan, _stop),
            Ui.Row(_banner, _showClosed, _progress, _summary,
                Ui.Button("Copy CSV", () => ToolUi.Copy(BuildCsv())),
                Ui.Button("Save CSV...", () => _ = ToolUi.SaveTextAsync($"portquery-{_lastHost}-{DateTime.Now:yyyyMMdd-HHmm}", BuildCsv(), ".csv", "CSV"))));
    }

    /// <summary>Prefills the target, e.g. from a session's context menu.</summary>
    public void SetTarget(string host) => _host.Text = host;

    private async Task ScanAsync()
    {
        if (_cts is not null) return;
        var host = (_host.Text ?? "").Trim();
        if (host.Length == 0) { _summary.Text = "Enter a target host."; return; }
        IReadOnlyList<int> ports;
        try { ports = PortSpec.Parse(_ports.Text ?? ""); }
        catch (FormatException ex) { _summary.Text = ex.Message; return; }
        if (ports.Count == 0) { _summary.Text = "Enter at least one port."; return; }

        _all.Clear();
        _shown.Clear();
        _lastHost = host;
        var protocols = _protocol.SelectedIndex switch
        {
            1 => new[] { PortProtocol.Udp },
            2 => new[] { PortProtocol.Tcp, PortProtocol.Udp },
            _ => new[] { PortProtocol.Tcp },
        };
        int total = ports.Count * protocols.Length, done = 0;
        _progress.Maximum = total;
        _progress.Value = 0;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var scanner = new PortScanner { Timeout = TimeSpan.FromMilliseconds(Ui.IntValue(_timeout, 1500)), GrabBanner = _banner.IsChecked == true };
        var started = DateTime.Now;
        try
        {
            foreach (var proto in protocols)
                await scanner.ScanAsync(host, ports, proto, r => Dispatcher.UIThread.Post(() =>
                {
                    Add(new PortRow(r));
                    _progress.Value = ++done;
                    UpdateSummary(done, total, started);
                }), _cts.Token);
            Dispatcher.UIThread.Post(() => UpdateSummary(total, total, started, finished: true));
        }
        catch (OperationCanceledException) { _summary.Text = $"Stopped · {Count(PortState.Open)} open"; }
        catch (Exception ex) { _summary.Text = ex.Message; }
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
        while (i < _shown.Count && Compare(_shown[i], row) < 0) i++;
        _shown.Insert(i, row);
    }

    private static int Compare(PortRow a, PortRow b) =>
        a.Result.Port != b.Result.Port ? a.Result.Port.CompareTo(b.Result.Port) : a.Result.Protocol.CompareTo(b.Result.Protocol);

    /// <summary>Open ports always show; UDP "open or filtered" too, since that's the only answer many UDP services give.</summary>
    private bool Visible(PortRow r) => _showClosed.IsChecked == true || r.Result.State is PortState.Open or PortState.OpenOrFiltered;

    private void Refilter()
    {
        _shown.Clear();
        foreach (var r in _all.Where(Visible).OrderBy(r => r.Result.Port).ThenBy(r => r.Result.Protocol)) _shown.Add(r);
    }

    private int Count(PortState s) => _all.Count(r => r.Result.State == s);

    private void UpdateSummary(int done, int total, DateTime started, bool finished = false) =>
        _summary.Text = $"{(finished ? "Done" : "Scanning")} {done}/{total} · {Count(PortState.Open)} open · {Count(PortState.Closed)} closed · " +
                        $"{Count(PortState.Filtered) + Count(PortState.OpenOrFiltered)} filtered · {(DateTime.Now - started).TotalSeconds:0.0}s";

    private void SetRunning(bool running)
    {
        _scan.IsEnabled = !running;
        _stop.IsEnabled = running;
    }

    private string BuildCsv()
    {
        var sb = new StringBuilder("host,port,protocol,state,service,rtt_ms,banner\r\n");
        foreach (var r in _all.OrderBy(r => r.Result.Port))
            sb.Append(ToolUi.Csv(_lastHost, r.Port, r.Protocol, r.State, r.Service, $"{r.Result.Latency.TotalMilliseconds:0}", r.Banner)).Append("\r\n");
        return sb.ToString();
    }

    public void Shutdown() => _cts?.Cancel();
}
