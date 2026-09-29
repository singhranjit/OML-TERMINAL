using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views.Tools;

public sealed class BackupRow : System.ComponentModel.INotifyPropertyChanged
{
    private BackupResult? _result;
    private bool _running;

    public required SessionProfile Device { get; init; }
    public BackupResult? Result { get => _result; set { _result = value; Changed(); } }
    public bool Running { get => _running; set { _running = value; Changed(); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every display property derives from Result/Running, so one "everything changed" notification covers them.</summary>
    private void Changed() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    public string Name => Device.Name;
    public string Host => Device.Host;
    public string Lines => Result is { Success: true } r ? r.Lines.ToString("N0") : "";
    public string Message => Running ? "Backing up..." : Result?.Message ?? "Queued";
    public string Glyph => Running ? "" : Result switch
    {
        null => "",
        { Success: false } => "",
        { ChangedSincePrevious: true } => "",
        _ => "",
    };
    public Brush Brush => ToolUi.Brush(Running ? "OmlSkyBrush" : Result switch
    {
        null => "StatusIdleBrush",
        { Success: false } => "OmlRoseBrush",
        { ChangedSincePrevious: true } => "OmlAmberBrush",
        _ => "OmlMintBrush",
    });
}

public sealed partial class ConfigBackupView : UserControl, IToolView
{
    private const string CustomLabel = "Custom commands...";
    private readonly ToolContext _ctx;
    private readonly HashSet<Guid> _selected = new();
    private readonly ObservableCollection<BackupRow> _rows = new();
    private List<SessionProfile> _devices = new();
    private CancellationTokenSource? _cts;
    private bool _refreshingList;

    public ConfigBackupView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var p in ConfigBackup.Presets) PresetBox.Items.Add(p);
        PresetBox.Items.Add(CustomLabel);
        PresetBox.SelectedIndex = 0;
        FolderBox.Text = _ctx.Settings.BackupDirectory;
        ResultList.ItemsSource = _rows;
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
        // ItemsSource swaps drop the selection; put back whatever the user had ticked.
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

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = PresetBox.SelectedItem as string == CustomLabel;
        CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        if (PresetBox.SelectedItem is BackupPreset p)
            PresetHint.Text = $"{(p.Mode == BackupMode.Exec ? "exec" : "shell")}:  {string.Join("  →  ", p.Commands)}";
    }

    private BackupPreset CurrentPreset() => PresetBox.SelectedItem as BackupPreset
        ?? ConfigBackup.Custom(CustomMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell, CustomCommands.Text);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (await ToolUi.PickFolderAsync() is { } path) FolderBox.Text = path;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(FolderBox.Text.Trim());
        ToolUi.OpenInExplorer(FolderBox.Text.Trim());
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var devices = _devices.Where(d => _selected.Contains(d.Id)).ToList();
        if (devices.Count == 0) { SelectedText.Text = "Tick at least one device."; return; }
        var preset = CurrentPreset();
        if (preset.Commands.Count == 0) { PresetHint.Text = "Enter at least one command."; return; }
        var root = FolderBox.Text.Trim();
        if (root.Length == 0) root = ConfigBackup.DefaultDirectory;
        if (_ctx.Settings.BackupDirectory != root) { _ctx.Settings.BackupDirectory = root; _ctx.SaveSettings(); }

        _rows.Clear();
        var rows = devices.Select(d => new BackupRow { Device = d }).ToList();
        foreach (var r in rows) _rows.Add(r);

        _cts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        int parallel = double.IsNaN(ParallelBox.Value) ? 5 : (int)ParallelBox.Value;
        using var gate = new SemaphoreSlim(parallel);
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
            RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
        int ok = rows.Count(r => r.Result?.Success == true), changed = rows.Count(r => r.Result?.ChangedSincePrevious == true);
        ResultHeader.Text = $"RESULT · {ok}/{rows.Count} OK · {changed} changed · {(DateTime.Now - started).TotalSeconds:0}s";
    }

    private void Update(BackupRow row, Action<BackupRow> change)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            change(row);
            if (ReferenceEquals(ResultList.SelectedItem, row)) ShowPreview();
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPreview();
    private void PreviewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowPreview();

    private void ShowPreview()
    {
        if (ResultList.SelectedItem is not BackupRow { Result: { } r })
        {
            PreviewBox.Text = "";
            return;
        }
        if (!r.Success) { PreviewBox.Text = $"Backup failed:\n{r.Message}"; return; }
        try
        {
            var current = File.ReadAllText(r.FilePath!);
            if (PreviewBar.SelectedItem == PreviewBar.Items[1]) { PreviewBox.Text = current; return; }
            if (r.PreviousFile is null) { PreviewBox.Text = "First backup of this device - nothing to compare yet.\n\n" + Head(current); return; }
            var (added, removed) = ConfigBackup.Diff(File.ReadAllText(r.PreviousFile), current);
            PreviewBox.Text = added.Count + removed.Count == 0
                ? $"No changes since {Path.GetFileName(r.PreviousFile)}."
                : $"vs {Path.GetFileName(r.PreviousFile)}: +{added.Count} / -{removed.Count} lines\n\n"
                  + string.Join('\n', removed.Select(l => "- " + l)) + (removed.Count > 0 ? "\n" : "")
                  + string.Join('\n', added.Select(l => "+ " + l));
        }
        catch (Exception ex) { PreviewBox.Text = ex.Message; }
    }

    private static string Head(string s) => string.Join('\n', s.Split('\n').Take(60)) + "\n…";

    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is BackupRow { Result.FilePath: { } path }) ToolUi.OpenInExplorer(path);
    }

    public void Shutdown() => _cts?.Cancel();
}
