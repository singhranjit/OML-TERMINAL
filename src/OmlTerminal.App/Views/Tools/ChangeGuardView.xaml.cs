using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.ChangeGuard;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views.Tools;

public sealed class GuardRow : INotifyPropertyChanged
{
    private GuardSnapshot? _pre, _post;
    private IReadOnlyList<GuardFinding>? _findings;
    private string? _busy;

    public required SessionProfile Device { get; init; }
    public GuardSnapshot? Pre { get => _pre; set { _pre = value; Changed(); } }
    public GuardSnapshot? Post { get => _post; set { _post = value; Changed(); } }
    public IReadOnlyList<GuardFinding>? Findings { get => _findings; set { _findings = value; Changed(); } }
    /// <summary>"Capturing PRE…" while a capture runs, else null.</summary>
    public string? Busy { get => _busy; set { _busy = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public string Name => Device.Name;
    public string Host => Device.Host;
    public string PreText => Describe(Pre);
    public string PostText => Describe(Post);

    private static string Describe(GuardSnapshot? s)
    {
        if (s is null) return "—";
        if (s.Error is not null) return $"failed: {s.Error}";
        int ok = s.Results.Count(r => r.Error is null && !r.Unsupported);
        return $"{s.TakenUtc.ToLocalTime():MMM d HH:mm} · {ok}/{s.Results.Count} checks";
    }

    private int Count(GuardSeverity sev) => Findings?.Count(f => f.Severity == sev) ?? 0;

    public string Verdict => Busy ?? (Findings is null
        ? (Pre is null ? "Not captured" : Pre.Error is not null ? "PRE failed" : "Waiting for POST")
        : Count(GuardSeverity.Critical) > 0 ? $"{Count(GuardSeverity.Critical)} critical, {Count(GuardSeverity.Warning)} warning(s)"
        : Count(GuardSeverity.Warning) > 0 ? $"{Count(GuardSeverity.Warning)} warning(s)"
        : "No problems found");

    public string VerdictGlyph => Busy is not null ? ""
        : Findings is null ? ""
        : Count(GuardSeverity.Critical) > 0 ? ""
        : Count(GuardSeverity.Warning) > 0 ? ""
        : "";

    public Brush VerdictBrush => ToolUi.Brush(Busy is not null ? "OmlSkyBrush"
        : Findings is null ? (Pre?.Error is not null ? "OmlRoseBrush" : "StatusIdleBrush")
        : Count(GuardSeverity.Critical) > 0 ? "OmlRoseBrush"
        : Count(GuardSeverity.Warning) > 0 ? "OmlAmberBrush"
        : "OmlMintBrush");
}

public sealed class FindingRow
{
    public required GuardFinding Finding { get; init; }
    public string Glyph => Finding.Severity switch { GuardSeverity.Critical => "", GuardSeverity.Warning => "", _ => "" };
    public Brush Brush => ToolUi.Brush(Finding.Severity switch
    {
        GuardSeverity.Critical => "OmlRoseBrush",
        GuardSeverity.Warning => "OmlAmberBrush",
        _ => Finding.Message.StartsWith("No change") || Finding.Message.StartsWith("No new") ? "StatusIdleBrush" : "OmlSkyBrush",
    });
    public string DetailText => Finding.Details is { Count: > 0 } d ? string.Join("\n", d) : "";
    public Visibility DetailVisibility => Finding.Details is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class ChangeGuardView : UserControl, IToolView
{
    private const string CustomLabel = "Custom commands...";
    private readonly ToolContext _ctx;
    private readonly HashSet<Guid> _selected = new();
    private readonly ObservableCollection<GuardRow> _rows = new();
    private List<SessionProfile> _devices = new();
    private GuardManifest? _manifest;
    private CancellationTokenSource? _cts;
    private bool _refreshingList;

    private static string Root => GuardStore.DefaultRoot;

    public ChangeGuardView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var p in GuardProfiles.All) ProfileBox.Items.Add(p);
        ProfileBox.Items.Add(CustomLabel);
        ProfileBox.SelectedIndex = 0;
        RowList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            _devices = _ctx.SshSessions().ToList();
            ApplyFilter();
            RefreshSaved();
        };
    }

    // ---------- setup ----------

    private void RefreshSaved()
    {
        SavedBox.SelectionChanged -= SavedBox_SelectionChanged;
        SavedBox.ItemsSource = GuardStore.ListChanges(Root);
        SavedBox.SelectedItem = null;
        SavedBox.SelectionChanged += SavedBox_SelectionChanged;
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = ProfileBox.SelectedItem as string == CustomLabel;
        CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ProfileHint.Text = ProfileBox.SelectedItem is GuardProfile p
            ? string.Join(" · ", p.Checks.Select(c => c.Title))
            : "Your own commands are compared line by line - fine for anything, but without the plain-English summary.";
    }

    private GuardProfile CurrentProfile() => ProfileBox.SelectedItem as GuardProfile
        ?? GuardProfiles.Custom(CustomMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell, CustomCommands.Text);

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

    /// <summary>Reopening a saved change restores its name, checks and devices, and whatever was captured so far.</summary>
    private void SavedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedBox.SelectedItem is not GuardManifest m) return;
        _manifest = m;
        ChangeNameBox.Text = m.Name;
        if (GuardProfiles.Find(m.ProfileName) is { } p) ProfileBox.SelectedItem = p;
        else
        {
            ProfileBox.SelectedItem = CustomLabel;
            CustomCommands.Text = m.CustomCommandsText;
            CustomMode.SelectedIndex = m.CustomMode == BackupMode.Exec ? 0 : 1;
        }
        _selected.Clear();
        foreach (var id in m.DeviceIds) _selected.Add(id);
        ApplyFilter();

        _rows.Clear();
        foreach (var device in ManifestDevices(m))
        {
            var row = new GuardRow { Device = device, Pre = GuardStore.Load(Root, m.Name, device, GuardStore.Pre), Post = GuardStore.Load(Root, m.Name, device, GuardStore.Post) };
            if (row.Pre is not null && row.Post is not null) row.Findings = GuardCompare.Compare(row.Pre, row.Post);
            _rows.Add(row);
        }
        StatusText.Text = $"Reopened '{m.Name}' ({m.CreatedUtc.ToLocalTime():MMM d HH:mm}). " +
            (_rows.All(r => r.Post is not null) ? "PRE and POST are both captured." : "Take POST when the change is done.");
        if (_rows.Count > 0) RowList.SelectedIndex = 0;
    }

    private List<SessionProfile> ManifestDevices(GuardManifest m) =>
        m.DeviceIds.Select(id => _devices.FirstOrDefault(d => d.Id == id)).OfType<SessionProfile>().ToList();

    // ---------- capture ----------

    private async void Pre_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var name = ChangeNameBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "Name the change first - a ticket number works well."; return; }
        var devices = _devices.Where(d => _selected.Contains(d.Id)).ToList();
        if (devices.Count == 0) { StatusText.Text = "Tick at least one device."; return; }
        var profile = CurrentProfile();
        if (profile.Checks.Count == 0) { StatusText.Text = "Enter at least one command."; return; }

        var existing = GuardStore.ListChanges(Root).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !await ConfirmAsync("Replace the PRE snapshot?",
                $"'{name}' already has a PRE snapshot from {existing.CreatedUtc.ToLocalTime():MMM d HH:mm}. Taking it again replaces it - do this only if the change hasn't started yet.", "Replace"))
            return;

        _manifest = new GuardManifest
        {
            Name = name,
            ProfileName = profile.Name,
            CustomMode = CustomMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell,
            CustomCommandsText = profile.Name == GuardProfiles.CustomName ? CustomCommands.Text : "",
            DeviceIds = devices.Select(d => d.Id).ToList(),
        };
        try { GuardStore.SaveManifest(Root, _manifest); }
        catch (Exception ex) { StatusText.Text = $"Couldn't save the change: {ex.Message}"; return; }

        _rows.Clear();
        foreach (var d in devices) _rows.Add(new GuardRow { Device = d });
        await RunAsync("PRE", async (row, ct) =>
        {
            var snap = await GuardStore.CaptureAsync(row.Device, profile, GuardStore.SnapshotDirectory(Root, name, row.Device, GuardStore.Pre), ct);
            Update(row, r => { r.Pre = snap; r.Post = null; r.Findings = null; });
        });
        int ok = _rows.Count(r => r.Pre is { Error: null });
        StatusText.Text = $"PRE captured for {ok} of {_rows.Count} device(s). Make your change, then come back and take POST - even after restarting the app.";
        RefreshSaved();
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var name = ChangeNameBox.Text.Trim();
        var manifest = _manifest is not null && string.Equals(_manifest.Name, name, StringComparison.OrdinalIgnoreCase)
            ? _manifest
            : GuardStore.ListChanges(Root).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (manifest is null) { StatusText.Text = "Take a PRE snapshot first (or reopen a saved change)."; return; }
        _manifest = manifest;
        var profile = manifest.ResolveProfile();
        var devices = ManifestDevices(manifest);
        if (devices.Count == 0) { StatusText.Text = "None of this change's devices exist as saved sessions any more."; return; }

        var byId = _rows.ToDictionary(r => r.Device.Id);
        _rows.Clear();
        foreach (var d in devices)
            _rows.Add(byId.TryGetValue(d.Id, out var r) ? r : new GuardRow { Device = d, Pre = GuardStore.Load(Root, manifest.Name, d, GuardStore.Pre) });

        await RunAsync("POST", async (row, ct) =>
        {
            var post = await GuardStore.CaptureAsync(row.Device, profile, GuardStore.SnapshotDirectory(Root, manifest.Name, row.Device, GuardStore.Post), ct);
            var pre = row.Pre ?? GuardStore.Load(Root, manifest.Name, row.Device, GuardStore.Pre);
            IReadOnlyList<GuardFinding> findings = pre is null
                ? [new GuardFinding(GuardSeverity.Warning, "Device", "No PRE snapshot for this device - nothing to compare against.")]
                : GuardCompare.Compare(pre, post);
            Update(row, r => { r.Pre = pre; r.Post = post; r.Findings = findings; });
        });

        int crit = _rows.Count(r => r.Findings?.Any(f => f.Severity == GuardSeverity.Critical) == true);
        int warn = _rows.Count(r => r.Findings?.Any(f => f.Severity == GuardSeverity.Warning) == true);
        StatusText.Text = crit + warn == 0
            ? $"POST done - no problems found on {_rows.Count} device(s)."
            : $"POST done - {crit} device(s) with critical findings, {warn} with warnings. Select a device for details.";
        var worst = _rows.OrderBy(r => r.Findings?.Min(f => (int?)f.Severity) ?? 9).FirstOrDefault();
        if (worst is not null) RowList.SelectedItem = worst;
    }

    private async Task RunAsync(string phase, Func<GuardRow, CancellationToken, Task> capture)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        PreButton.IsEnabled = PostButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = $"Capturing {phase} on {_rows.Count} device(s)…";
        using var gate = new SemaphoreSlim(5);
        try
        {
            await Task.WhenAll(_rows.ToList().Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    Update(row, r => r.Busy = $"Capturing {phase}…");
                    await Task.Run(() => capture(row, ct), ct);
                }
                finally
                {
                    Update(row, r => r.Busy = null);
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
            PreButton.IsEnabled = PostButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void Update(GuardRow row, Action<GuardRow> change)
    {
        void Apply()
        {
            change(row);
            if (ReferenceEquals(RowList.SelectedItem, row)) ShowFindings(row);
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- results ----------

    private void RowList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowFindings(RowList.SelectedItem as GuardRow);

    private void ShowFindings(GuardRow? row)
    {
        FindingsTitle.Text = row is null ? "WHAT CHANGED" : $"WHAT CHANGED · {row.Name.ToUpperInvariant()}";
        var items = new List<FindingRow>();
        if (row?.Findings is { } findings) items.AddRange(findings.Select(f => new FindingRow { Finding = f }));
        else if (row?.Pre?.Error is { } err) items.Add(new FindingRow { Finding = new GuardFinding(GuardSeverity.Critical, "Device", $"PRE capture failed: {err}") });
        FindingList.ItemsSource = items;
    }

    private string Report() => GuardCompare.Report(_manifest?.Name ?? ChangeNameBox.Text.Trim(),
        _rows.Where(r => r.Findings is not null).Select(r => (r.Device.Name, r.Findings!)));

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.All(r => r.Findings is null)) { StatusText.Text = "Nothing to report yet - take POST first."; return; }
        ToolUi.Copy(Report());
        StatusText.Text = "Report copied - paste it into the change ticket.";
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.All(r => r.Findings is null)) { StatusText.Text = "Nothing to report yet - take POST first."; return; }
        var name = ConfigBackup.SafeFileName($"change-guard-{_manifest?.Name ?? "report"}-{DateTime.Now:yyyyMMdd-HHmm}");
        if (await ToolUi.SaveTextAsync(name, Report()) is { } path) StatusText.Text = $"Saved {Path.GetFileName(path)}";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = _manifest?.Name ?? ChangeNameBox.Text.Trim();
        var dir = name.Length > 0 ? GuardStore.ChangeDirectory(Root, name) : Root;
        if (!Directory.Exists(dir)) dir = Root;
        Directory.CreateDirectory(dir);
        ToolUi.OpenInExplorer(dir);
    }

    private async Task<bool> ConfirmAsync(string title, string text, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void Shutdown() => _cts?.Cancel();
}
