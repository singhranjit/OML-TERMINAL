using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Desktop.Tools;

public sealed class LocalPortsTool : UserControl, IToolView
{
    private IReadOnlyList<LocalPortEntry> _snapshot = [];
    private List<LocalPortEntry> _shown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBox _filter = Ui.Input("", "Filter: 443, sshd, 10.0.0.5, pid 1234...");
    private readonly ComboBox _scope = Ui.Combo(new[] { "Listening", "Established", "All sockets" });
    private readonly CheckBox _live = Ui.Check("Live (2s)");
    private readonly TextBlock _count = Ui.Text("", 12, mono: true, color: Ui.Muted);
    private readonly DataGrid _table;

    private static IBrush StateBrush(LocalPortEntry e) => e.State switch
    {
        "LISTEN" or "" => Ui.Mint,
        "ESTABLISHED" => Ui.Sky,
        "TIME_WAIT" or "CLOSE_WAIT" or "FIN_WAIT1" or "FIN_WAIT2" => Ui.Amber,
        _ => Ui.Violet,
    };

    public LocalPortsTool()
    {
        _filter.Width = 300;
        _table = Ui.Table(
            Ui.Col<LocalPortEntry>("Proto", e => e.Protocol, 84),
            Ui.Col<LocalPortEntry>("Local", e => e.Local, 220, mono: true, sortKey: e => e.LocalPort),
            Ui.Col<LocalPortEntry>("Remote", e => e.Remote, 220, mono: true),
            Ui.Col<LocalPortEntry>("State", e => e.State, 120, StateBrush),
            Ui.Col<LocalPortEntry>("PID", e => e.Pid == 0 ? "" : e.Pid.ToString(), 100, sortKey: e => e.Pid),
            Ui.Col<LocalPortEntry>("Process", e => e.ProcessName, fill: true));
        _timer.Tick += async (_, _) => await RefreshAsync();
        _filter.TextChanged += (_, _) => Apply();
        _scope.SelectionChanged += (_, _) => Apply();
        _live.IsCheckedChanged += (_, _) => { if (_live.IsChecked == true) _timer.Start(); else _timer.Stop(); };
        var note = OperatingSystem.IsLinux() && Environment.UserName != "root"
            ? " Processes owned by other users show as \"?\" (as with ss -p without sudo)." : "";
        Content = Ui.Page("Local Ports", "What's listening on this computer and who it's talking to - like netstat / ss, with the owning process." + note, _table,
            Ui.Row(_filter, _scope, _live, Ui.Button("Refresh", () => _ = RefreshAsync()), Ui.Button("Copy", Copy), _count));
        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _snapshot = await Task.Run(LocalPorts.Snapshot);
        Apply();
    }

    private void Apply()
    {
        var q = (_filter.Text ?? "").Trim();
        var pidQuery = q.StartsWith("pid ", StringComparison.OrdinalIgnoreCase) ? q[4..].Trim() : null;
        IEnumerable<LocalPortEntry> rows = _scope.SelectedIndex switch
        {
            0 => _snapshot.Where(e => e.State is "LISTEN" or ""),
            1 => _snapshot.Where(e => e.State == "ESTABLISHED"),
            _ => _snapshot,
        };
        if (pidQuery is not null) rows = rows.Where(e => e.Pid.ToString() == pidQuery);
        else if (q.Length > 0)
            rows = rows.Where(e => e.Local.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Remote.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || e.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Pid.ToString() == q);
        _shown = rows.ToList();
        _table.ItemsSource = _shown;
        _count.Text = $"{_shown.Count} of {_snapshot.Count} sockets";
    }

    private void Copy()
    {
        var rows = _table.SelectedItems.Count > 0 ? _table.SelectedItems.OfType<LocalPortEntry>() : _shown;
        var sb = new StringBuilder("proto,local,remote,state,pid,process\r\n");
        foreach (var e in rows) sb.Append(ToolUi.Csv(e.Protocol, e.Local, e.Remote, e.State, e.Pid.ToString(), e.ProcessName)).Append("\r\n");
        ToolUi.Copy(sb.ToString());
    }

    public void Shutdown() => _timer.Stop();
}
