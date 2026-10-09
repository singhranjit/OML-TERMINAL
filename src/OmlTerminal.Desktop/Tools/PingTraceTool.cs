using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Desktop.Tools;

public sealed class PingTraceTool : UserControl, IToolView
{
    private const int MaxOutputChars = 400_000;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;

    private readonly ComboBox _mode = Ui.Combo(new[] { "Ping", "Traceroute", "Ping sweep (subnet)" });
    private readonly TextBox _target = Ui.Input("", "8.8.8.8 / host.example.com / 10.0.0.0/24", mono: true);
    private readonly NumericUpDown _count = Ui.Number(0, 0, 100000), _size = Ui.Number(32, 0, 65500);
    private readonly TextBox _output = Ui.Output();
    private readonly Button _start, _stop;
    private readonly TextBlock[] _statLabels = new TextBlock[4], _stats = new TextBlock[4];

    public PingTraceTool(ToolContext context)
    {
        _start = Ui.Button("Start", () => _ = StartAsync(), accent: true);
        _stop = Ui.Button("Stop", () => _cts?.Cancel());
        _stop.IsEnabled = false;
        var statGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,12,*,12,*,12,*") };
        for (int i = 0; i < 4; i++)
        {
            _statLabels[i] = Ui.Section("");
            _stats[i] = new TextBlock { Text = "-", FontSize = 22, FontWeight = FontWeight.SemiBold, FontFamily = Ui.Mono };
            var card = Ui.Card(Ui.Stack(4, _statLabels[i], _stats[i]));
            Grid.SetColumn(card, i * 2);
            statGrid.Children.Add(card);
        }
        _mode.SelectionChanged += (_, _) => ModeChanged();
        _target.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = StartAsync(); } };
        ModeChanged();

        var mode = OperatingSystem.IsWindows() ? "" : " " + UnixIcmp.ModeDescription + ".";
        Content = Ui.Page("Ping · Trace · Sweep", "ICMP echo through the operating system - no admin rights needed." + mode, _output,
            Ui.Columns("170,*,110,110,Auto,Auto",
                Ui.Field("Mode", _mode), Ui.Field("Target", _target), Ui.Field("Count (0 = ∞)", _count), Ui.Field("Size (bytes)", _size), _start, _stop),
            statGrid,
            Ui.Row(Ui.Button("Clear", () => { _log.Clear(); _output.Text = ""; }), Ui.Button("Copy", () => ToolUi.Copy(_log.ToString()))));
    }

    public void SetTarget(string host, int mode = 0)
    {
        _target.Text = host;
        _mode.SelectedIndex = mode;
    }

    private void ModeChanged()
    {
        bool ping = _mode.SelectedIndex == 0;
        _count.IsEnabled = ping;
        _size.IsEnabled = ping;
        var labels = _mode.SelectedIndex switch
        {
            1 => new[] { "Hops", "Target reached", "Last hop RTT", "Timed-out hops" },
            2 => new[] { "Hosts scanned", "Alive", "Fastest", "Elapsed" },
            _ => new[] { "Sent / received", "Loss", "Min / avg / max", "Jitter" },
        };
        for (int i = 0; i < 4; i++) { _statLabels[i].Text = labels[i].ToUpperInvariant(); _stats[i].Text = "-"; _stats[i].ClearValue(TextBlock.ForegroundProperty); }
    }

    private void Append(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > MaxOutputChars) _log.Remove(0, _log.Length - MaxOutputChars);
        _output.Text = _log.ToString();
        _output.CaretIndex = _output.Text.Length;
    }

    private static void OnUi(Action a) => Dispatcher.UIThread.Post(a);

    private async Task StartAsync()
    {
        if (_cts is not null) return;
        var target = (_target.Text ?? "").Trim();
        if (target.Length == 0) return;
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        try
        {
            switch (_mode.SelectedIndex)
            {
                case 1: await TraceAsync(target, _cts.Token); break;
                case 2: await SweepAsync(target, _cts.Token); break;
                default: await PingAsync(target, _cts.Token); break;
            }
        }
        catch (OperationCanceledException) { Append("-- stopped --"); }
        catch (Exception ex) { Append($"error: {ex.Message}"); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _start.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private async Task PingAsync(string target, CancellationToken ct)
    {
        var ip = await PortScanner.ResolveAsync(target, ct);
        int count = Ui.IntValue(_count, 0), size = Ui.IntValue(_size, 32);
        Append($"PING {target} ({ip}) {size} bytes of data");
        int sent = 0, received = 0;
        var rtts = new List<long>();
        long? previous = null;
        double jitter = 0;
        for (int i = 0; count == 0 || i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var started = DateTime.UtcNow;
            var r = await PingTools.PingOnceAsync(ip, 2000, size);
            sent++;
            if (r.Status == IPStatus.Success)
            {
                received++;
                rtts.Add(r.RoundTripMs);
                if (previous is { } p) jitter += (Math.Abs(r.RoundTripMs - p) - jitter) / 16.0; // RFC 3550 smoothing
                previous = r.RoundTripMs;
                Append($"{DateTime.Now:HH:mm:ss}  reply from {r.Address}: bytes={size} time={r.RoundTripMs}ms{(r.Ttl > 0 ? $" TTL={r.Ttl}" : "")}");
            }
            else Append($"{DateTime.Now:HH:mm:ss}  {Describe(r.Status)}");

            _stats[0].Text = $"{sent} / {received}";
            _stats[1].Text = $"{(sent - received) * 100.0 / sent:0.#}%";
            _stats[1].Foreground = received == sent ? Ui.Mint : received == 0 ? Ui.Rose : Ui.Amber;
            if (rtts.Count > 0) _stats[2].Text = $"{rtts.Min()}/{rtts.Average():0}/{rtts.Max()} ms";
            _stats[3].Text = $"{jitter:0.0} ms";

            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero && (count == 0 || i < count - 1)) await Task.Delay(wait, ct);
        }
        Append($"--- {target} ping statistics: {sent} sent, {received} received, {(sent - received) * 100.0 / Math.Max(1, sent):0.#}% loss ---");
    }

    private static string Describe(IPStatus s) => s switch
    {
        IPStatus.TimedOut => "request timed out",
        IPStatus.DestinationHostUnreachable => "destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "destination network unreachable",
        IPStatus.TtlExpired => "TTL expired in transit",
        IPStatus.PacketTooBig => "packet needs to be fragmented but DF set",
        _ => s.ToString(),
    };

    private async Task TraceAsync(string target, CancellationToken ct)
    {
        var ip = await PortScanner.ResolveAsync(target, ct);
        Append($"traceroute to {target} ({ip}), 30 hops max");
        int hops = 0, timedOut = 0;
        bool reached = false;
        await PingTools.TraceAsync(ip, hop => OnUi(() =>
        {
            hops = hop.Hop;
            if (hop.TimedOut) timedOut++;
            if (ip.Equals(hop.Address)) reached = true;
            var rtts = string.Join("  ", hop.RoundTrips.Select(r => r is { } v ? $"{v,4} ms" : "   *   "));
            var who = hop.Address is null ? "Request timed out." : string.IsNullOrEmpty(hop.HostName) ? hop.Address.ToString() : $"{hop.HostName} [{hop.Address}]";
            Append($"{hop.Hop,3}  {rtts}  {who}");
            _stats[0].Text = hops.ToString();
            _stats[1].Text = reached ? "yes" : "…";
            _stats[2].Text = hop.RoundTrips.LastOrDefault(r => r is not null) is { } last ? $"{last} ms" : "*";
            _stats[3].Text = timedOut.ToString();
        }), ct: ct);
        await Task.Delay(50, ct);
        _stats[1].Text = reached ? "yes" : "no";
        Append(reached ? "Trace complete." : "Target not reached within 30 hops.");
    }

    private async Task SweepAsync(string target, CancellationToken ct)
    {
        var subnet = Subnet.Parse(target);
        var alive = new List<(IPAddress Ip, long Rtt)>();
        var started = DateTime.Now;
        Append($"Sweeping {subnet.Cidr} ({subnet.UsableHosts} hosts)...");
        await PingTools.SweepAsync(subnet,
            (ip, r) => OnUi(() =>
            {
                alive.Add((ip, r.RoundTripMs));
                Append($"  alive  {ip,-16} {r.RoundTripMs,4} ms{(r.Ttl > 0 ? $"  TTL={r.Ttl}" : "")}");
                _stats[1].Text = alive.Count.ToString();
                _stats[2].Text = $"{alive.Min(a => a.Rtt)} ms";
            }),
            (done, total) => OnUi(() =>
            {
                _stats[0].Text = $"{done}/{total}";
                _stats[3].Text = $"{(DateTime.Now - started).TotalSeconds:0.0}s";
            }), ct: ct);
        await Task.Delay(100, ct);
        Append($"Done: {alive.Count} host(s) alive.");
        if (alive.Count > 0)
            Append("Sorted: " + string.Join(", ", alive.OrderBy(a => Ipv4.ToUInt(a.Ip)).Select(a => a.Ip.ToString())));
    }

    public void Shutdown() => _cts?.Cancel();
}
