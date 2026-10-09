using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Parsing;
using OmlTerminal.Core.Ssh;
using Windows.ApplicationModel.DataTransfer;

namespace OmlTerminal.App.Views.Tools;

public sealed record TableRowItem(string Text, Brush Brush, IReadOnlyList<string> Cells);

public sealed partial class StructuredOutputView : UserControl, IToolView
{
    private static readonly string[] StateColumns = ["Status", "Protocol", "Link", "Admin", "State", "State/PfxRcd"];
    private static readonly string[] BadWords = ["down", "err-disabled", "notconnect", "idle", "init", "exstart", "absent"];

    private readonly ToolContext _ctx;
    private ShowTable? _table;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _parseDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public StructuredOutputView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _parseDelay.Tick += (_, _) => { _parseDelay.Stop(); ParseInput(); };
        Loaded += (_, _) => ToolUi.FillSessions(DeviceBox, _ctx.SshSessions());
    }

    private void Grab_Click(object sender, RoutedEventArgs e)
    {
        var buffer = _ctx.ActiveTerminalText();
        if (string.IsNullOrWhiteSpace(buffer)) { SourceStatus.Text = "No terminal tab is open - connect to a device and run a command first."; return; }
        var (command, output) = ShowTableParser.LastCommandOutput(buffer);
        InputBox.Text = output;
        SourceStatus.Text = command is null ? "Took the whole terminal buffer (no CLI prompt found)." : $"From the terminal: {command}";
        ParseInput();
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) { SourceStatus.Text = "The clipboard has no text."; return; }
            InputBox.Text = await content.GetTextAsync();
            SourceStatus.Text = "Pasted from the clipboard.";
            ParseInput();
        }
        catch (Exception ex) { SourceStatus.Text = ex.Message; }
    }

    private void CommandBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Run_Click(sender, e); }
    }

    /// <summary>Runs the command on its own side-channel SSH connection - never in a terminal tab someone is using.</summary>
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        if (DeviceBox.SelectedItem is not SessionProfile device) { SourceStatus.Text = "Pick a saved SSH session first."; return; }
        var command = CommandBox.Text.Trim();
        if (command.Length == 0) { SourceStatus.Text = "Type a command to run."; return; }

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        RunButton.IsEnabled = false;
        SourceStatus.Text = $"Running '{command}' on {device.Name}...";
        try
        {
            var output = await Task.Run(async () =>
            {
                using var session = await DeviceSession.OpenAsync(device, BackupMode.Shell, ["terminal length 0"], _cts.Token);
                return await session.RunAsync(command, _cts.Token);
            });
            InputBox.Text = output;
            SourceStatus.Text = $"{device.Name}: {command}";
            ParseInput();
        }
        catch (OperationCanceledException) { SourceStatus.Text = "Timed out."; }
        catch (Exception ex) { SourceStatus.Text = $"{device.Name}: {ex.Message}"; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _parseDelay.Stop();
        _parseDelay.Start();
    }

    private void ParseInput()
    {
        _parseDelay.Stop();
        _table = string.IsNullOrWhiteSpace(InputBox.Text) ? null : ShowTableParser.Parse(InputBox.Text);
        SortBox.Items.Clear();
        if (_table is not null) foreach (var c in _table.Columns) SortBox.Items.Add(c);
        Render();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Render();
    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Render();
    private void Descending_Click(object sender, RoutedEventArgs e) => Render();

    private IReadOnlyList<IReadOnlyList<string>> VisibleRows()
    {
        if (_table is null) return [];
        IEnumerable<IReadOnlyList<string>> rows = _table.Rows;
        var q = FilterBox.Text.Trim();
        if (q.Length > 0) rows = rows.Where(r => r.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)));
        if (SortBox.SelectedIndex >= 0)
        {
            int col = SortBox.SelectedIndex;
            var key = new Func<IReadOnlyList<string>, string>(r => col < r.Count ? r[col] : "");
            rows = DescendingToggle.IsChecked == true ? rows.OrderByDescending(key, NaturalComparer.Instance) : rows.OrderBy(key, NaturalComparer.Instance);
        }
        return rows.ToList();
    }

    private void Render()
    {
        if (_table is null)
        {
            TableTitle.Text = string.IsNullOrWhiteSpace(InputBox.Text) ? "NO TABLE YET" : "NOT RECOGNISED";
            TableInfo.Text = string.IsNullOrWhiteSpace(InputBox.Text) ? "" : "No table found in this output. Column-aligned output with a header line works best.";
            HeaderRow.Text = "";
            RowList.ItemsSource = null;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        var rows = VisibleRows();
        var widths = _table.Widths(_table.Rows);
        var stateCols = StateColumns.Select(_table.ColumnIndex).Where(i => i >= 0).ToList();
        var normal = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var bad = ToolUi.Brush("OmlRoseBrush");

        TableTitle.Text = _table.Title.ToUpperInvariant();
        TableInfo.Text = rows.Count == _table.Rows.Count ? $"{_table.Rows.Count:N0} rows · {_table.Columns.Count} columns" : $"{rows.Count:N0} of {_table.Rows.Count:N0} rows";
        HeaderRow.Text = ShowTable.Pad(_table.Columns, widths);
        RowList.ItemsSource = rows.Select(r => new TableRowItem(ShowTable.Pad(r, widths), IsProblem(r, stateCols) ? bad : normal, r)).ToList();
        EmptyText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Rows worth a second look in red: something down/err-disabled, or a BGP session that isn't Established
    /// (State/PfxRcd holds a state name instead of a prefix count).</summary>
    private bool IsProblem(IReadOnlyList<string> row, List<int> stateCols)
    {
        foreach (var i in stateCols)
        {
            if (i >= row.Count) continue;
            var v = row[i].ToLowerInvariant();
            if (v.Length == 0) continue;
            if (_table!.Columns[i] == "State/PfxRcd") { if (!v.All(char.IsDigit)) return true; continue; }
            if (v == "connected" || v.StartsWith("full") || v.StartsWith("2way") || v is "active" or "standby" or "up") continue;
            if (BadWords.Any(v.Contains)) return true;
        }
        return false;
    }

    private IReadOnlyList<IReadOnlyList<string>> ExportRows()
    {
        var selected = RowList.SelectedItems.OfType<TableRowItem>().Select(r => r.Cells).ToList();
        return selected.Count > 1 ? selected : VisibleRows();
    }

    private void CopyCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_table is null) return;
        ToolUi.Copy((_table with { Rows = ExportRows() }).ToCsv());
        TableInfo.Text = "Copied as CSV.";
    }

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_table is null) return;
        ToolUi.Copy((_table with { Rows = ExportRows() }).ToMarkdown());
        TableInfo.Text = "Copied as a Markdown table.";
    }

    private async void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_table is null) return;
        var name = $"{_table.Title.ToLowerInvariant().Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmm}";
        if (await ToolUi.SaveTextAsync(name, (_table with { Rows = ExportRows() }).ToCsv(), ".csv", "CSV") is { } path)
            TableInfo.Text = $"Saved {Path.GetFileName(path)}";
    }

    public void Shutdown() => _cts?.Cancel();
}

/// <summary>Sorts "Gi1/0/2" before "Gi1/0/10" and "9" before "120" - the way people expect interface and number columns.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }
            int d = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (d != 0) return d;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
