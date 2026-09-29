using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class PingTraceView : UserControl, IToolView
{
    private const int MaxOutputChars = 400_000;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;

    public PingTraceView()
    {
        InitializeComponent();
    }

    public void SetTarget(string host, int mode = 0)
    {
        TargetBox.Text = host;
        ModeBox.SelectedIndex = mode;
    }

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CountBox is null) return;
        bool ping = ModeBox.SelectedIndex == 0;
        CountBox.IsEnabled = ping;
        SizeBox.IsEnabled = ping;
        (Stat1Label.Text, Stat2Label.Text, Stat3Label.Text, Stat4Label.Text) = ModeBox.SelectedIndex switch
        {
            1 => ("HOPS", "TARGET REACHED", "LAST HOP RTT", "TIMED-OUT HOPS"),
            2 => ("HOSTS SCANNED", "ALIVE", "FASTEST", "ELAPSED"),
            _ => ("SENT / RECEIVED", "LOSS", "MIN / AVG / MAX", "JITTER"),
        };
        Stat1.Text = Stat2.Text = Stat3.Text = Stat4.Text = "-";
    }

    private void TargetBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Start_Click(sender, e); }
    }

    private void Append(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > MaxOutputChars) _log.Remove(0, _log.Length - MaxOutputChars);
        Output.Text = _log.ToString();
        Output.SelectionStart = Output.Text.Length;
    }

    private void Ui(Action a) => DispatcherQueue.TryEnqueue(() => a());

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var target = TargetBox.Text.Trim();
        if (target.Length == 0) return;
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        try
        {
            switch (ModeBox.SelectedIndex)
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
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private async Task PingAsync(string target, CancellationToken ct)
    {
        var ip = await PortScanner.ResolveAsync(target, ct);
        int count = double.IsNaN(CountBox.Value) ? 0 : (int)CountBox.Value;
        int size = double.IsNaN(SizeBox.Value) ? 32 : (int)SizeBox.Value;
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
                Append($"{DateTime.Now:HH:mm:ss}  reply from {r.Address}: bytes={size} time={r.RoundTripMs}ms TTL={r.Ttl}");
            }
            else Append($"{DateTime.Now:HH:mm:ss}  {Describe(r.Status)}");

            Stat1.Text = $"{sent} / {received}";
            Stat2.Text = $"{(sent - received) * 100.0 / sent:0.#}%";
            Stat2.Foreground = ToolUi.Brush(received == sent ? "OmlMintBrush" : received == 0 ? "OmlRoseBrush" : "OmlAmberBrush");
            if (rtts.Count > 0) Stat3.Text = $"{rtts.Min()}/{rtts.Average():0}/{rtts.Max()} ms";
            Stat4.Text = $"{jitter:0.0} ms";

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
        await PingTools.TraceAsync(ip, hop => Ui(() =>
        {
            hops = hop.Hop;
            if (hop.TimedOut) timedOut++;
            if (ip.Equals(hop.Address)) reached = true;
            var rtts = string.Join("  ", hop.RoundTrips.Select(r => r is { } v ? $"{v,4} ms" : "   *   "));
            var who = hop.Address is null ? "Request timed out." : string.IsNullOrEmpty(hop.HostName) ? hop.Address.ToString() : $"{hop.HostName} [{hop.Address}]";
            Append($"{hop.Hop,3}  {rtts}  {who}");
            Stat1.Text = hops.ToString();
            Stat2.Text = reached ? "yes" : "…";
            Stat3.Text = hop.RoundTrips.LastOrDefault(r => r is not null) is { } last ? $"{last} ms" : "*";
            Stat4.Text = timedOut.ToString();
        }), ct: ct);
        await Task.Delay(50, ct); // let the last queued hop render
        Stat2.Text = reached ? "yes" : "no";
        Append(reached ? "Trace complete." : "Target not reached within 30 hops.");
    }

    private async Task SweepAsync(string target, CancellationToken ct)
    {
        var subnet = Subnet.Parse(target);
        var alive = new List<(IPAddress Ip, long Rtt)>();
        var started = DateTime.Now;
        Append($"Sweeping {subnet.Cidr} ({subnet.UsableHosts} hosts)...");
        await PingTools.SweepAsync(subnet,
            (ip, r) => Ui(() =>
            {
                alive.Add((ip, r.RoundTripMs));
                Append($"  alive  {ip,-16} {r.RoundTripMs,4} ms  TTL={r.Ttl}");
                Stat2.Text = alive.Count.ToString();
                Stat3.Text = $"{alive.Min(a => a.Rtt)} ms";
            }),
            (done, total) => Ui(() =>
            {
                Stat1.Text = $"{done}/{total}";
                Stat4.Text = $"{(DateTime.Now - started).TotalSeconds:0.0}s";
            }), ct: ct);
        await Task.Delay(100, ct);
        Append($"Done: {alive.Count} host(s) alive.");
        if (alive.Count > 0)
            Append("Sorted: " + string.Join(", ", alive.OrderBy(a => Ipv4.ToUInt(a.Ip)).Select(a => a.Ip.ToString())));
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        Output.Text = "";
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ToolUi.Copy(_log.ToString());

    public void Shutdown() => _cts?.Cancel();
}
