using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Desktop.Tools;

public sealed class BackupRow : LiveRow
{
    private BackupResult? _result;
    private bool _running;

    public required SessionProfile Device { get; init; }
    public BackupResult? Result { get => _result; set { _result = value; Touch(); } }
    public bool Running { get => _running; set { _running = value; Touch(); } }

    public string Name => Device.Name;
    public string Host => Device.Host;
    public string Lines => Result is { Success: true } r ? r.Lines.ToString("N0") : "";
    public string Message => Running ? "Backing up..." : Result?.Message ?? "Queued";
    public string State => Running ? "running" : Result switch { null => "queued", { Success: false } => "failed", { ChangedSincePrevious: true } => "changed", _ => "ok" };
    public IBrush Brush => Running ? Ui.Sky : Result switch
    {
        null => Ui.Muted, { Success: false } => Ui.Rose, { ChangedSincePrevious: true } => Ui.Amber, _ => Ui.Mint,
    };
}

/// <summary>Multi-select list of saved devices with a filter that doesn't lose the ticks.</summary>
internal sealed class DevicePicker : DockPanel
{
    private readonly HashSet<Guid> _selected = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, Background = Brushes.Transparent };
    private readonly TextBox _filter = Ui.Input("", "Filter by name, folder, host");
    private readonly TextBlock _count = Ui.Text("", 12, color: Ui.Muted);
    private List<SessionProfile> _devices = new();
    private bool _refreshing;

    public DevicePicker()
    {
        _list.ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => p is null ? new Panel() : Ui.Stack(0,
            new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = $"{p.Host}{(p.Folder.Length > 0 ? "  ·  " + p.Folder : "")}", FontSize = 11.5, Foreground = Ui.Muted, FontFamily = Ui.Mono }));
        _filter.TextChanged += (_, _) => Apply();
        _list.SelectionChanged += (_, e) =>
        {
            if (_refreshing) return;
            foreach (var d in e.AddedItems.OfType<SessionProfile>()) _selected.Add(d.Id);
            foreach (var d in e.RemovedItems.OfType<SessionProfile>()) _selected.Remove(d.Id);
            UpdateCount();
        };
        var buttons = Ui.Row(Ui.Button("All", () => { foreach (var d in Shown()) _selected.Add(d.Id); Apply(); }),
            Ui.Button("None", () => { _selected.Clear(); Apply(); }), _count);
        SetDock(_filter, Dock.Top);
        SetDock(buttons, Dock.Bottom);
        _filter.Margin = new Thickness(0, 0, 0, 8);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(_filter);
        Children.Add(buttons);
        Children.Add(_list);
    }

    /// <param name="preselect">Devices to tick, e.g. an existing job's.</param>
    public void SetDevices(IReadOnlyList<SessionProfile> devices, IEnumerable<Guid>? preselect = null)
    {
        _devices = devices.ToList();
        if (preselect is not null) foreach (var id in preselect) _selected.Add(id);
        Apply();
    }

    public IReadOnlyList<SessionProfile> Selected => _devices.Where(d => _selected.Contains(d.Id)).ToList();

    private List<SessionProfile> Shown()
    {
        var q = (_filter.Text ?? "").Trim();
        return q.Length == 0 ? _devices : _devices.Where(d => d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || d.Host.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Folder.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Apply()
    {
        var shown = Shown();
        _refreshing = true;
        _list.ItemsSource = shown;
        for (int i = 0; i < shown.Count; i++) if (_selected.Contains(shown[i].Id)) _list.Selection.Select(i);
        _refreshing = false;
        UpdateCount();
    }

    private void UpdateCount() => _count.Text = _devices.Count == 0 ? "No saved SSH sessions yet." : $"{_selected.Count} of {_devices.Count} selected";
}

public sealed class ConfigBackupTool : UserControl, IToolView
{
    private const string CustomLabel = "Custom commands...";
    private readonly ToolContext _ctx;
    private readonly ObservableCollection<BackupRow> _rows = new();
    private CancellationTokenSource? _cts;

    private readonly DevicePicker _devices = new();
    private readonly ComboBox _preset;
    private readonly NumericUpDown _parallel = Ui.Number(5, 1, 32);
    private readonly TextBox _folder;
    private readonly TextBox _customCommands = Ui.MultiInput("", 90, "terminal length 0\nshow running-config");
    private readonly ComboBox _customMode = Ui.Combo(new[] { "Exec (one command per channel)", "Shell (interactive)" }, 1);
    private readonly Control _customPanel;
    private readonly TextBlock _hint = Ui.Text("", 12, mono: true, color: Ui.Muted);
    private readonly DataGrid _results;
    private readonly TextBlock _resultHeader = Ui.Section("Result");
    private readonly ToggleButton _diffTab = new() { Content = "Changes", IsChecked = true }, _fileTab = new() { Content = "Saved config" };
    private readonly TextBox _preview = Ui.Output();
    private readonly Button _run, _cancel;

    public ConfigBackupTool(ToolContext ctx)
    {
        _ctx = ctx;
        _preset = Ui.Combo(ConfigBackup.Presets.Cast<object>().Append(CustomLabel).ToList());
        _preset.ItemTemplate = new FuncDataTemplate<object>((o, _) => new TextBlock { Text = o is BackupPreset p ? p.Name : o?.ToString() });
        _folder = Ui.Input(_ctx.Settings.BackupDirectory, mono: true);
        _customPanel = Ui.Columns("*,240", Ui.Field("Commands (one per line; the last one's output is saved)", _customCommands), Ui.Field("Mode", _customMode));
        _customPanel.IsVisible = false;
        _preset.SelectionChanged += (_, _) => PresetChanged();
        _run = Ui.Button("Back up now", () => _ = RunAsync(), accent: true);
        _cancel = Ui.Button("Cancel", () => _cts?.Cancel());
        _cancel.IsEnabled = false;

        _results = Ui.Table(
            Ui.Col<BackupRow>("", r => "●", 30, r => r.Brush),
            Ui.Col<BackupRow>("Device", r => r.Name, 170),
            Ui.Col<BackupRow>("Host", r => r.Host, 130, mono: true),
            Ui.Col<BackupRow>("Lines", r => r.Lines, 70, mono: true),
            Ui.Col<BackupRow>("Status", r => r.Message, fill: true, color: r => r.Brush));
        _results.ItemsSource = _rows;
        _results.SelectionMode = DataGridSelectionMode.Single;
        _results.SelectionChanged += (_, _) => ShowPreview();
        foreach (var t in new[] { _diffTab, _fileTab })
            t.Click += (s, _) => { _diffTab.IsChecked = s == _diffTab; _fileTab.IsChecked = s == _fileTab; ShowPreview(); };

        var left = Ui.Card(new DockPanel { Children = { WithDock(Ui.Section("Devices"), Dock.Top), _devices } });
        _devices.Margin = new Thickness(0, 8, 0, 0);
        var resultsPane = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto,*"), RowSpacing = 8 };
        void Put(Control c, int row) { Grid.SetRow(c, row); resultsPane.Children.Add(c); }
        Put(_resultHeader, 0);
        Put(_results, 1);
        Put(new DockPanel { Children = { WithDock(Ui.Row(Ui.Button("Show file", ShowFile)), Dock.Right), Ui.Row(_diffTab, _fileTab) } }, 2);
        Put(_preview, 3);

        var right = new DockPanel
        {
            Children =
            {
                WithDock(Ui.Card(Ui.Stack(10,
                    Ui.Columns("*,90,Auto,Auto", Ui.Field("Vendor / command set", _preset), Ui.Field("Parallel", _parallel), _run, _cancel),
                    _hint, _customPanel,
                    Ui.Columns("*,Auto,Auto", Ui.Field("Save backups to", _folder),
                        Ui.Button("Browse...", async () => { if (await ToolUi.PickFolderAsync() is { } p) _folder.Text = p; }),
                        Ui.Button("Open folder", () => { var f = (_folder.Text ?? "").Trim(); Directory.CreateDirectory(f); ToolUi.OpenInFileManager(f); })))), Dock.Top),
                resultsPane,
            },
        };
        resultsPane.Margin = new Thickness(0, 12, 0, 0);
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("300,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        Content = Ui.Page("Config Backup", "Pull running configs from many devices over SSH at once, keep every version, and see exactly what changed since the last backup.", body);
        PresetChanged();
        AttachedToVisualTree += (_, _) => _devices.SetDevices(_ctx.SshSessions());
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void PresetChanged()
    {
        bool custom = _preset.SelectedItem as string == CustomLabel;
        _customPanel.IsVisible = custom;
        _hint.IsVisible = !custom;
        if (_preset.SelectedItem is BackupPreset p)
            _hint.Text = $"{(p.Mode == BackupMode.Exec ? "exec" : "shell")}:  {string.Join("  →  ", p.Commands)}";
    }

    private BackupPreset CurrentPreset() => _preset.SelectedItem as BackupPreset
        ?? ConfigBackup.Custom(_customMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell, _customCommands.Text ?? "");

    private async Task RunAsync()
    {
        if (_cts is not null) return;
        var devices = _devices.Selected;
        if (devices.Count == 0) { _resultHeader.Text = "TICK AT LEAST ONE DEVICE"; return; }
        var preset = CurrentPreset();
        if (preset.Commands.Count == 0) { _hint.Text = "Enter at least one command."; _hint.IsVisible = true; return; }
        var root = (_folder.Text ?? "").Trim();
        if (root.Length == 0) root = ConfigBackup.DefaultDirectory;
        if (_ctx.Settings.BackupDirectory != root) { _ctx.Settings.BackupDirectory = root; _ctx.SaveSettings(); }

        _rows.Clear();
        var rows = devices.Select(d => new BackupRow { Device = d }).ToList();
        foreach (var r in rows) _rows.Add(r);

        _cts = new CancellationTokenSource();
        _run.IsEnabled = false;
        _cancel.IsEnabled = true;
        using var gate = new SemaphoreSlim(Ui.IntValue(_parallel, 5));
        var started = DateTime.Now;
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(_cts.Token);
                try
                {
                    Update(row, r => r.Running = true);
                    var result = await Task.Run(() => ConfigBackup.RunAsync(row.Device, preset, root, ct: _cts.Token));
                    Update(row, r => { r.Running = false; r.Result = result; });
                }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _run.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
        int ok = rows.Count(r => r.Result?.Success == true), changed = rows.Count(r => r.Result?.ChangedSincePrevious == true);
        _resultHeader.Text = $"RESULT · {ok}/{rows.Count} OK · {changed} changed · {(DateTime.Now - started).TotalSeconds:0}s";
    }

    /// <summary>Applied immediately on the UI thread so the run summary, counted right after the last device, sees it.</summary>
    private void Update(BackupRow row, Action<BackupRow> change)
    {
        void Apply()
        {
            change(row);
            if (ReferenceEquals(_results.SelectedItem, row)) ShowPreview();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void ShowPreview()
    {
        if (_results.SelectedItem is not BackupRow { Result: { } r }) { _preview.Text = ""; return; }
        if (!r.Success) { _preview.Text = $"Backup failed:\n{r.Message}"; return; }
        try
        {
            var current = File.ReadAllText(r.FilePath!);
            if (_fileTab.IsChecked == true) { _preview.Text = current; return; }
            if (r.PreviousFile is null) { _preview.Text = "First backup of this device - nothing to compare yet.\n\n" + string.Join('\n', current.Split('\n').Take(60)) + "\n…"; return; }
            var (added, removed) = ConfigBackup.Diff(File.ReadAllText(r.PreviousFile), current);
            _preview.Text = added.Count + removed.Count == 0
                ? $"No changes since {Path.GetFileName(r.PreviousFile)}."
                : $"vs {Path.GetFileName(r.PreviousFile)}: +{added.Count} / -{removed.Count} lines\n\n"
                  + string.Join('\n', removed.Select(l => "- " + l)) + (removed.Count > 0 ? "\n" : "")
                  + string.Join('\n', added.Select(l => "+ " + l));
        }
        catch (Exception ex) { _preview.Text = ex.Message; }
    }

    private void ShowFile()
    {
        if (_results.SelectedItem is BackupRow { Result.FilePath: { } path }) ToolUi.OpenInFileManager(path);
    }

    public void Shutdown() => _cts?.Cancel();
}
