using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.ChangeGuard;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Desktop.Tools;

public sealed class GuardRow : LiveRow
{
    private GuardSnapshot? _pre, _post;
    private IReadOnlyList<GuardFinding>? _findings;
    private string? _busy;

    public required SessionProfile Device { get; init; }
    public GuardSnapshot? Pre { get => _pre; set { _pre = value; Touch(); } }
    public GuardSnapshot? Post { get => _post; set { _post = value; Touch(); } }
    public IReadOnlyList<GuardFinding>? Findings { get => _findings; set { _findings = value; Touch(); } }
    /// <summary>"Capturing PRE…" while a capture runs, else null.</summary>
    public string? Busy { get => _busy; set { _busy = value; Touch(); } }

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

    public IBrush VerdictBrush => Busy is not null ? Ui.Sky
        : Findings is null ? (Pre?.Error is not null ? Ui.Rose : Ui.Muted)
        : Count(GuardSeverity.Critical) > 0 ? Ui.Rose
        : Count(GuardSeverity.Warning) > 0 ? Ui.Amber
        : Ui.Mint;
}

public sealed class ChangeGuardTool : UserControl, IToolView
{
    private const string CustomLabel = "Custom commands...";
    private readonly ToolContext _ctx;
    private readonly ObservableCollection<GuardRow> _rows = new();
    private List<SessionProfile> _devices = new();
    private GuardManifest? _manifest;
    private CancellationTokenSource? _cts;
    private bool _loadingSaved;

    private static string Root => GuardStore.DefaultRoot;

    private readonly TextBox _name = Ui.Input("", "e.g. CHG0012345 core uplink swap");
    private readonly ComboBox _saved = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Saved changes" };
    private readonly ComboBox _profile;
    private readonly TextBox _customCommands = Ui.MultiInput("", 90);
    private readonly ComboBox _customMode = Ui.Combo(new[] { "Exec", "Shell" }, 1);
    private readonly Control _customPanel;
    private readonly TextBlock _hint = Ui.Text("", 12, color: Ui.Muted);
    private readonly DevicePicker _picker = new();
    private readonly TextBlock _status = Ui.Text("Name the change, tick the devices and take PRE before you start. Take POST when you're done.", 12.5);
    private readonly DataGrid _table;
    private readonly TextBlock _findingsTitle = Ui.Section("What changed");
    private readonly ItemsControl _findings = new();
    private readonly Button _pre, _post, _cancel;

    public ChangeGuardTool(ToolContext ctx)
    {
        _ctx = ctx;
        _profile = Ui.Combo(GuardProfiles.All.Cast<object>().Append(CustomLabel).ToList());
        _profile.ItemTemplate = new FuncDataTemplate<object>((o, _) => new TextBlock { Text = o is GuardProfile p ? p.Name : o?.ToString() });
        _saved.ItemTemplate = new FuncDataTemplate<GuardManifest>((m, _) => new TextBlock { Text = m is null ? "" : $"{m.Name}  ·  {m.CreatedUtc.ToLocalTime():MMM d HH:mm}" });
        _customPanel = Ui.Stack(6, Ui.Field("Commands (one per line, compared line by line)", _customCommands), _customMode);
        _customPanel.IsVisible = false;
        _profile.SelectionChanged += (_, _) => ProfileChanged();
        _saved.SelectionChanged += (_, _) => { if (!_loadingSaved) Reopen(); };
        _pre = Ui.Button("Take PRE snapshot", () => _ = PreAsync(), accent: true);
        _post = Ui.Button("Take POST and compare", () => _ = PostAsync(), accent: true);
        _cancel = Ui.Button("Stop", () => _cts?.Cancel());
        _cancel.IsEnabled = false;

        _table = Ui.Table(
            Ui.Col<GuardRow>("", r => "●", 30, r => r.VerdictBrush),
            Ui.Col<GuardRow>("Device", r => r.Device.Name, 150),
            Ui.Col<GuardRow>("PRE", r => r.PreText, 160),
            Ui.Col<GuardRow>("POST", r => r.PostText, 160),
            Ui.Col<GuardRow>("Verdict", r => r.Verdict, fill: true, color: r => r.VerdictBrush));
        _table.ItemsSource = _rows;
        _table.SelectionMode = DataGridSelectionMode.Single;
        _table.SelectionChanged += (_, _) => ShowFindings(_table.SelectedItem as GuardRow);
        _findings.ItemTemplate = new FuncDataTemplate<GuardFinding>((f, _) =>
        {
            if (f is null) return new Panel();
            var brush = f.Severity switch
            {
                GuardSeverity.Critical => Ui.Rose,
                GuardSeverity.Warning => Ui.Amber,
                _ => f.Message.StartsWith("No change") || f.Message.StartsWith("No new") ? Ui.Muted : Ui.Sky,
            };
            return new Border
            {
                BorderBrush = brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8), Background = Ui.CardBack,
                Child = Ui.Stack(2,
                    new TextBlock { Text = $"{f.Check}: {f.Message}", Foreground = brush, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new SelectableTextBlock { Text = f.Details is { Count: > 0 } d ? string.Join("\n", d) : "", FontFamily = Ui.Mono, FontSize = 11.5, IsVisible = f.Details is { Count: > 0 } }),
            };
        });

        var left = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(10, Ui.Field("Change name or ticket", _name), Ui.Field("Or reopen a saved change", _saved), Ui.Field("Checks for", _profile), _customPanel, _hint,
                    Ui.Section("Devices")), Dock.Top),
                _picker,
            },
        });
        _picker.Margin = new Thickness(0, 8, 0, 0);
        var resultsCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = {
                    WithDock(Ui.Row(Ui.Button("Copy report", CopyReport), Ui.Button("Save report...", () => _ = SaveReportAsync()), Ui.Button("Open folder", OpenFolder)), Dock.Right),
                    Ui.Section("Devices") } }, Dock.Top),
                _table,
            },
        });
        var findingsCard = Ui.Card(new DockPanel { Children = { WithDock(_findingsTitle, Dock.Top), new ScrollViewer { Content = _findings, Margin = new Thickness(0, 8, 0, 0) } } });
        var results = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,*"), RowSpacing = 12 };
        var actions = Ui.Card(new DockPanel { Children = { WithDock(Ui.Row(_pre, _post, _cancel), Dock.Left), _status } });
        _status.Margin = new Thickness(12, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        results.Children.Add(actions);
        Grid.SetRow(resultsCard, 1);
        results.Children.Add(resultsCard);
        Grid.SetRow(findingsCard, 2);
        results.Children.Add(findingsCard);

        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("320,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(results, 1);
        body.Children.Add(results);
        Content = Ui.Page("Change Guard",
            "Pre/post change checks in plain English: snapshot interfaces, routing, neighbors and more before a change, then again after - see what went down, rerouted or disappeared. Read-only show commands only.",
            body);
        ProfileChanged();
        AttachedToVisualTree += (_, _) =>
        {
            _devices = _ctx.SshSessions().ToList();
            _picker.SetDevices(_devices);
            RefreshSaved();
        };
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void RefreshSaved()
    {
        _loadingSaved = true;
        _saved.ItemsSource = GuardStore.ListChanges(Root);
        _saved.SelectedItem = null;
        _loadingSaved = false;
    }

    private void ProfileChanged()
    {
        bool custom = _profile.SelectedItem as string == CustomLabel;
        _customPanel.IsVisible = custom;
        _hint.Text = _profile.SelectedItem is GuardProfile p
            ? string.Join(" · ", p.Checks.Select(c => c.Title))
            : "Your own commands are compared line by line - fine for anything, but without the plain-English summary.";
    }

    private GuardProfile CurrentProfile() => _profile.SelectedItem as GuardProfile
        ?? GuardProfiles.Custom(_customMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell, _customCommands.Text ?? "");

    /// <summary>Reopening a saved change restores its name, checks and devices, and whatever was captured so far.</summary>
    private void Reopen()
    {
        if (_saved.SelectedItem is not GuardManifest m) return;
        _manifest = m;
        _name.Text = m.Name;
        if (GuardProfiles.Find(m.ProfileName) is { } p) _profile.SelectedItem = p;
        else
        {
            _profile.SelectedItem = CustomLabel;
            _customCommands.Text = m.CustomCommandsText;
            _customMode.SelectedIndex = m.CustomMode == BackupMode.Exec ? 0 : 1;
        }
        _picker.SetSelection(m.DeviceIds);
        _rows.Clear();
        foreach (var device in ManifestDevices(m))
        {
            var row = new GuardRow { Device = device, Pre = GuardStore.Load(Root, m.Name, device, GuardStore.Pre), Post = GuardStore.Load(Root, m.Name, device, GuardStore.Post) };
            if (row.Pre is not null && row.Post is not null) row.Findings = GuardCompare.Compare(row.Pre, row.Post);
            _rows.Add(row);
        }
        _status.Text = $"Reopened '{m.Name}' ({m.CreatedUtc.ToLocalTime():MMM d HH:mm}). " +
            (_rows.All(r => r.Post is not null) ? "PRE and POST are both captured." : "Take POST when the change is done.");
        if (_rows.Count > 0) _table.SelectedIndex = 0;
    }

    private List<SessionProfile> ManifestDevices(GuardManifest m) =>
        m.DeviceIds.Select(id => _devices.FirstOrDefault(d => d.Id == id)).OfType<SessionProfile>().ToList();

    private async Task PreAsync()
    {
        if (_cts is not null) return;
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) { _status.Text = "Name the change first - a ticket number works well."; return; }
        var devices = _picker.Selected;
        if (devices.Count == 0) { _status.Text = "Tick at least one device."; return; }
        var profile = CurrentProfile();
        if (profile.Checks.Count == 0) { _status.Text = "Enter at least one command."; return; }

        var existing = GuardStore.ListChanges(Root).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && MainWindow.Current is { } owner && !await Dialogs.ConfirmAsync(owner, "Replace the PRE snapshot?",
                $"'{name}' already has a PRE snapshot from {existing.CreatedUtc.ToLocalTime():MMM d HH:mm}. Taking it again replaces it - do this only if the change hasn't started yet.", "Replace"))
            return;

        _manifest = new GuardManifest
        {
            Name = name,
            ProfileName = profile.Name,
            CustomMode = _customMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell,
            CustomCommandsText = profile.Name == GuardProfiles.CustomName ? _customCommands.Text ?? "" : "",
            DeviceIds = devices.Select(d => d.Id).ToList(),
        };
        try { GuardStore.SaveManifest(Root, _manifest); }
        catch (Exception ex) { _status.Text = $"Couldn't save the change: {ex.Message}"; return; }

        _rows.Clear();
        foreach (var d in devices) _rows.Add(new GuardRow { Device = d });
        await RunAsync("PRE", async (row, ct) =>
        {
            var snap = await GuardStore.CaptureAsync(row.Device, profile, GuardStore.SnapshotDirectory(Root, name, row.Device, GuardStore.Pre), ct);
            await Update(row, r => { r.Pre = snap; r.Post = null; r.Findings = null; });
        });
        int ok = _rows.Count(r => r.Pre is { Error: null });
        _status.Text = $"PRE captured for {ok} of {_rows.Count} device(s). Make your change, then come back and take POST - even after restarting the app.";
        RefreshSaved();
    }

    private async Task PostAsync()
    {
        if (_cts is not null) return;
        var name = (_name.Text ?? "").Trim();
        var manifest = _manifest is not null && string.Equals(_manifest.Name, name, StringComparison.OrdinalIgnoreCase)
            ? _manifest
            : GuardStore.ListChanges(Root).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (manifest is null) { _status.Text = "Take a PRE snapshot first (or reopen a saved change)."; return; }
        _manifest = manifest;
        var profile = manifest.ResolveProfile();
        var devices = ManifestDevices(manifest);
        if (devices.Count == 0) { _status.Text = "None of this change's devices exist as saved sessions any more."; return; }

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
            await Update(row, r => { r.Pre = pre; r.Post = post; r.Findings = findings; });
        });

        int crit = _rows.Count(r => r.Findings?.Any(f => f.Severity == GuardSeverity.Critical) == true);
        int warn = _rows.Count(r => r.Findings?.Any(f => f.Severity == GuardSeverity.Warning) == true);
        _status.Text = crit + warn == 0
            ? $"POST done - no problems found on {_rows.Count} device(s)."
            : $"POST done - {crit} device(s) with critical findings, {warn} with warnings. Select a device for details.";
        if (_rows.OrderBy(r => r.Findings?.Min(f => (int?)f.Severity) ?? 9).FirstOrDefault() is { } worst) _table.SelectedItem = worst;
    }

    private async Task RunAsync(string phase, Func<GuardRow, CancellationToken, Task> capture)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _pre.IsEnabled = _post.IsEnabled = false;
        _cancel.IsEnabled = true;
        _status.Text = $"Capturing {phase} on {_rows.Count} device(s)…";
        using var gate = new SemaphoreSlim(5);
        try
        {
            await Task.WhenAll(_rows.ToList().Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await Update(row, r => r.Busy = $"Capturing {phase}…");
                    await Task.Run(() => capture(row, ct), ct);
                }
                finally
                {
                    await Update(row, r => r.Busy = null);
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _pre.IsEnabled = _post.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }

    /// <summary>Applies on the UI thread and completes once applied, so a run's summary (counted right after the last
    /// device) always sees every result.</summary>
    private Task Update(GuardRow row, Action<GuardRow> change)
    {
        void Apply()
        {
            change(row);
            if (ReferenceEquals(_table.SelectedItem, row)) ShowFindings(row);
        }
        if (Dispatcher.UIThread.CheckAccess()) { Apply(); return Task.CompletedTask; }
        return Dispatcher.UIThread.InvokeAsync(Apply).GetTask();
    }

    private void ShowFindings(GuardRow? row)
    {
        _findingsTitle.Text = row is null ? "WHAT CHANGED" : $"WHAT CHANGED · {row.Device.Name.ToUpperInvariant()}";
        var items = new List<GuardFinding>();
        if (row?.Findings is { } findings) items.AddRange(findings);
        else if (row?.Pre?.Error is { } err) items.Add(new GuardFinding(GuardSeverity.Critical, "Device", $"PRE capture failed: {err}"));
        _findings.ItemsSource = items;
    }

    private string Report() => GuardCompare.Report(_manifest?.Name ?? (_name.Text ?? "").Trim(),
        _rows.Where(r => r.Findings is not null).Select(r => (r.Device.Name, r.Findings!)));

    private void CopyReport()
    {
        if (_rows.All(r => r.Findings is null)) { _status.Text = "Nothing to report yet - take POST first."; return; }
        ToolUi.Copy(Report());
        _status.Text = "Report copied - paste it into the change ticket.";
    }

    private async Task SaveReportAsync()
    {
        if (_rows.All(r => r.Findings is null)) { _status.Text = "Nothing to report yet - take POST first."; return; }
        var name = ConfigBackup.SafeFileName($"change-guard-{_manifest?.Name ?? "report"}-{DateTime.Now:yyyyMMdd-HHmm}");
        if (await ToolUi.SaveTextAsync(name, Report()) is { } path) _status.Text = $"Saved {Path.GetFileName(path)}";
    }

    private void OpenFolder()
    {
        var name = _manifest?.Name ?? (_name.Text ?? "").Trim();
        var dir = name.Length > 0 ? GuardStore.ChangeDirectory(Root, name) : Root;
        if (!Directory.Exists(dir)) dir = Root;
        Directory.CreateDirectory(dir);
        ToolUi.OpenInFileManager(dir);
    }

    public void Shutdown() => _cts?.Cancel();
}
