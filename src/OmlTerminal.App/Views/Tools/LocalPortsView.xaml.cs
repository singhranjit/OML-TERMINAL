using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.App.Views.Tools;

public sealed class LocalPortRow(LocalPortEntry e)
{
    public LocalPortEntry Entry { get; } = e;
    public string Pid => Entry.Pid.ToString();
    public Brush StateBrush => ToolUi.Brush(Entry.State switch
    {
        "LISTEN" or "" => "OmlMintBrush",
        "ESTABLISHED" => "OmlSkyBrush",
        "TIME_WAIT" or "CLOSE_WAIT" or "FIN_WAIT1" or "FIN_WAIT2" => "OmlAmberBrush",
        _ => "OmlVioletBrush",
    });
}

public sealed partial class LocalPortsView : UserControl, IToolView
{
    private IReadOnlyList<LocalPortEntry> _snapshot = [];
    private List<LocalPortRow> _shown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public LocalPortsView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _snapshot = await Task.Run(LocalPorts.Snapshot);
        Apply();
    }

    private void Apply()
    {
        var q = FilterBox.Text.Trim();
        var pidQuery = q.StartsWith("pid ", StringComparison.OrdinalIgnoreCase) ? q[4..].Trim() : null;
        IEnumerable<LocalPortEntry> rows = ScopeBox.SelectedIndex switch
        {
            0 => _snapshot.Where(e => e.State is "LISTEN" or ""),
            1 => _snapshot.Where(e => e.State == "ESTABLISHED"),
            _ => _snapshot,
        };
        if (pidQuery is not null) rows = rows.Where(e => e.Pid.ToString() == pidQuery);
        else if (q.Length > 0)
            rows = rows.Where(e => e.Local.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Remote.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || e.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Pid.ToString() == q);
        _shown = rows.Select(e => new LocalPortRow(e)).ToList();
        List.ItemsSource = _shown;
        CountText.Text = $"{_shown.Count}/{_snapshot.Count}";
        ToolTipService.SetToolTip(CountText, $"{_shown.Count} of {_snapshot.Count} sockets shown");
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Apply();
    private void Scope_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Apply(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void AutoRefresh_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsOn) _timer.Start(); else _timer.Stop();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var rows = List.SelectedItems.Count > 0 ? List.SelectedItems.OfType<LocalPortRow>() : _shown;
        var sb = new StringBuilder("proto,local,remote,state,pid,process\r\n");
        foreach (var r in rows)
            sb.Append(ToolUi.Csv(r.Entry.Protocol, r.Entry.Local, r.Entry.Remote, r.Entry.State, r.Pid, r.Entry.ProcessName)).Append("\r\n");
        ToolUi.Copy(sb.ToString());
    }

    public void Shutdown() => _timer.Stop();
}
