using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Monitoring;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.App.Views.Tools;

public sealed class MonitorRow : System.ComponentModel.INotifyPropertyChanged
{
    private HostStatsSample? _sample;
    private string? _error;
    private bool _connecting = true;

    public required SessionProfile Device { get; init; }
    public HostStatsSample? Sample { get => _sample; set { _sample = value; _error = null; Changed(); } }
    /// <summary>Setting this also clears Sample - otherwise the grid would keep showing a healthy-looking
    /// load/memory/disk reading from the last successful poll right next to (or instead of) the error text,
    /// which could read as the device still being fine when the connection has actually dropped.</summary>
    public string? Error { get => _error; set { _error = value; _sample = null; Changed(); } }
    public bool Connecting { get => _connecting; set { _connecting = value; Changed(); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every display property derives from Sample/Error/Connecting, so one notification covers them all.</summary>
    private void Changed() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    public string Name => Device.Name;
    public string Host => Device.Host;
    public string LoadText => Sample is { Load1: not null, Load5: not null, Load15: not null } s ? $"{s.Load1:0.00} / {s.Load5:0.00} / {s.Load15:0.00}" : "—";
    public string MemText => Sample?.MemUsedPercent is { } p ? $"{p:0}%  ({Sample!.MemUsedMb:N0}/{Sample!.MemTotalMb:N0} MB)" : "—";
    public string DiskText => Sample?.DiskUsedPercent is { } p ? $"{p:0}%" : "—";
    public string UptimeText => Sample?.UptimeText ?? "";
    public string StatusText => Connecting ? "Connecting…" : Error is { } e ? e : Sample is { AnyData: true } ? "OK" : Sample is not null ? "No stats - not a Linux/Unix host?" : "—";
    public Brush StatusBrush => ToolUi.Brush(Connecting ? "StatusIdleBrush" : Error is not null ? "OmlRoseBrush" : Sample is { AnyData: true } ? "OmlMintBrush" : "OmlAmberBrush");
}

public sealed partial class HostMonitorView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly HashSet<Guid> _selected = new();
    private readonly ObservableCollection<MonitorRow> _rows = new();
    private List<SessionProfile> _devices = new();
    private CancellationTokenSource? _cts;
    private bool _refreshingList;

    public HostMonitorView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        MonitorList.ItemsSource = _rows;
        Loaded += (_, _) => { _devices = _ctx.SshSessions().ToList(); ApplyFilter(); };
    }

    private void ApplyFilter()
    {
        var q = DeviceFilter.Text.Trim();
        var shown = q.Length == 0
            ? _devices
            : _devices.Where(d => d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.Folder.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _refreshingList = true;
        DeviceList.ItemsSource = shown;
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var d in shown.Where(d => _selected.Contains(d.Id))) DeviceList.SelectedItems.Add(d);
            _refreshingList = false;
            UpdateSelectedText();
        });
        if (_devices.Count == 0) SelectedText.Text = "No saved SSH sessions yet.";
    }

    private void DeviceFilter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingList) return;
        foreach (var d in e.AddedItems.OfType<SessionProfile>()) _selected.Add(d.Id);
        foreach (var d in e.RemovedItems.OfType<SessionProfile>()) _selected.Remove(d.Id);
        UpdateSelectedText();
    }

    private void UpdateSelectedText() => SelectedText.Text = $"{_selected.Count} of {_devices.Count} selected";

    private void SelectAll_Click(object sender, RoutedEventArgs e) => DeviceList.SelectAll();
    private void SelectNone_Click(object sender, RoutedEventArgs e) => DeviceList.SelectedItems.Clear();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var devices = _devices.Where(d => _selected.Contains(d.Id)).ToList();
        if (devices.Count == 0) { SelectedText.Text = "Tick at least one device."; return; }

        _rows.Clear();
        foreach (var d in devices) _rows.Add(new MonitorRow { Device = d });

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        var interval = TimeSpan.FromSeconds(double.IsNaN(IntervalBox.Value) ? 5 : Math.Max(2, IntervalBox.Value));
        foreach (var row in _rows) _ = PollDeviceAsync(row, interval, _cts.Token);
    }

    /// <summary>Owns one side-channel SSH connection per device, reconnecting on failure, polling <see cref="HostStats.Command"/>
    /// on its own exec channel - never the interactive shell, so it can't interleave with what the user is typing.</summary>
    private async Task PollDeviceAsync(MonitorRow row, TimeSpan interval, CancellationToken ct)
    {
        ConnectedSsh? ssh = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ssh is null)
                    {
                        Update(row, r => r.Connecting = true);
                        ssh = await SshConnector.ConnectAsync(row.Device, ct).ConfigureAwait(false);
                    }
                    using var cmd = ssh.Client.CreateCommand(HostStats.Command);
                    cmd.CommandTimeout = TimeSpan.FromSeconds(10);
                    await cmd.ExecuteAsync(ct).ConfigureAwait(false);
                    var sample = HostStats.Parse(cmd.Result);
                    Update(row, r => { r.Connecting = false; r.Sample = sample; });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ssh?.Dispose();
                    ssh = null;
                    Update(row, r => { r.Connecting = false; r.Error = ex.Message; });
                }
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { ssh?.Dispose(); }
    }

    private void Update(MonitorRow row, Action<MonitorRow> change) => DispatcherQueue.TryEnqueue(() => change(row));

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = null;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    public void Shutdown() => _cts?.Cancel();
}
