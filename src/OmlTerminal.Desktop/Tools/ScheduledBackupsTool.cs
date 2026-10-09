using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Desktop.Tools;

public sealed class ScheduledBackupsTool : UserControl, IToolView
{
    private sealed record JobRow(ScheduledBackupJob Job, int DeviceCount)
    {
        public string Interval => Job.IntervalMinutes >= 60 && Job.IntervalMinutes % 60 == 0 ? $"every {Job.IntervalMinutes / 60}h" : $"every {Job.IntervalMinutes}m";
        public string LastRun => Job.LastRunUtc is { } t ? $"last run {t.ToLocalTime():MMM d, HH:mm}" : "never run yet";
    }

    private readonly ToolContext _ctx;
    private readonly BackupScheduler _scheduler;
    private readonly CancellationTokenSource _cts = new();
    private readonly ListBox _jobs = new() { Background = Brushes.Transparent };
    private readonly ItemsControl _alerts = new();
    private readonly TextBlock _unseen = Ui.Text("", 12, color: Ui.Amber);
    private readonly Button _runNow;

    public ScheduledBackupsTool(ToolContext ctx)
    {
        _ctx = ctx;
        _scheduler = new BackupScheduler(ctx.Settings, ctx.SaveSettings, ctx.SshSessions);
        _runNow = Ui.Button("Run now", () => _ = RunNowAsync());
        _jobs.ItemTemplate = new FuncDataTemplate<JobRow>((r, _) =>
        {
            if (r is null) return new Panel();
            var toggle = new ToggleSwitch { IsChecked = r.Job.Enabled, OnContent = "On", OffContent = "Off", [DockPanel.DockProperty] = Dock.Right };
            toggle.IsCheckedChanged += (_, _) => BackupScheduler.WithSettingsLock(() => { r.Job.Enabled = toggle.IsChecked == true; _ctx.SaveSettings(); });
            return new DockPanel
            {
                Margin = new Thickness(0, 4),
                Children =
                {
                    toggle,
                    Ui.Stack(1, new TextBlock { Text = r.Job.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"{r.DeviceCount} device{(r.DeviceCount == 1 ? "" : "s")} · {r.Interval} · {r.LastRun}", FontSize = 12, Foreground = Ui.Muted }),
                },
            };
        });
        _alerts.ItemTemplate = new FuncDataTemplate<BackupAlert>((a, _) => a is null ? new Panel() : new Border
        {
            BorderBrush = a.Failed ? Ui.Rose : Ui.Amber, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8),
            Background = Ui.CardBack,
            Child = Ui.Stack(2,
                new DockPanel { Children = { new TextBlock { Text = a.AtUtc.ToLocalTime().ToString("MMM d, HH:mm"), FontSize = 11.5, Foreground = Ui.Muted, [DockPanel.DockProperty] = Dock.Right },
                                             new TextBlock { Text = $"{a.DeviceName} · {a.JobName}", FontWeight = a.Seen ? FontWeight.Normal : FontWeight.SemiBold } } },
                new TextBlock { Text = a.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = a.Failed ? Ui.Rose : Ui.Amber }),
        });

        var jobsCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Row(Ui.Section("Jobs"), Ui.Button("New job...", () => _ = EditAsync(null), accent: true)), Dock.Top),
                WithDock(Ui.Row(Ui.Button("Edit...", () => { if (_jobs.SelectedItem is JobRow r) _ = EditAsync(r.Job); }), Ui.Button("Delete", Delete), _runNow), Dock.Bottom),
                new ScrollViewer { Content = _jobs, Margin = new Thickness(0, 8) },
            },
        });
        var alertsCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(new DockPanel { Children = { WithDock(Ui.Button("Clear", ClearAlerts), Dock.Right), Ui.Row(Ui.Section("Drift & failure alerts"), _unseen) } }, Dock.Top),
                new ScrollViewer { Content = _alerts, Margin = new Thickness(0, 8, 0, 0) },
            },
        });
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*"), ColumnSpacing = 16 };
        body.Children.Add(jobsCard);
        Grid.SetColumn(alertsCard, 1);
        body.Children.Add(alertsCard);
        Content = Ui.Page("Scheduled Backups",
            "Recurring config backups that run in the background while the app is open. A config that changed between runs (drift), or a device that couldn't be backed up, raises an alert here.",
            body);
        AttachedToVisualTree += (_, _) => Refresh(markAlertsSeen: true);
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    /// <summary>Rebuilds both lists under the scheduler's lock (it edits the same settings lists from a background tick).
    /// Opening the tool counts as checking, so it marks the alerts on show as seen.</summary>
    private void Refresh(bool markAlertsSeen = false)
    {
        var devices = _ctx.SshSessions();
        BackupScheduler.WithSettingsLock(() =>
        {
            var keep = (_jobs.SelectedItem as JobRow)?.Job.Id;
            var rows = _ctx.Settings.ScheduledBackups.Select(j => new JobRow(j, j.DeviceIds.Count(id => devices.Any(d => d.Id == id)))).ToList();
            _jobs.ItemsSource = rows;
            _jobs.SelectedItem = rows.FirstOrDefault(r => r.Job.Id == keep);
            _alerts.ItemsSource = _ctx.Settings.BackupAlerts.ToList();
            int unseen = _ctx.Settings.BackupAlerts.Count(a => !a.Seen);
            _unseen.Text = unseen > 0 ? $"{unseen} new" : "";
            if (markAlertsSeen && unseen > 0)
            {
                foreach (var a in _ctx.Settings.BackupAlerts) a.Seen = true;
                _ctx.SaveSettings();
            }
        });
    }

    private async Task EditAsync(ScheduledBackupJob? existing)
    {
        if (MainWindow.Current is not { } owner) return;
        var updated = await new ScheduleJobWindow(_ctx.SshSessions(), existing).ShowDialog<ScheduledBackupJob?>(owner);
        if (updated is null) return;
        BackupScheduler.WithSettingsLock(() =>
        {
            var idx = _ctx.Settings.ScheduledBackups.FindIndex(j => j.Id == updated.Id);
            if (idx >= 0)
            {
                // The scheduler may have run this job while the window was open - keep its latest run time.
                updated.LastRunUtc = _ctx.Settings.ScheduledBackups[idx].LastRunUtc;
                _ctx.Settings.ScheduledBackups[idx] = updated;
            }
            else _ctx.Settings.ScheduledBackups.Add(updated);
            _ctx.SaveSettings();
        });
        Refresh();
    }

    private void Delete()
    {
        if (_jobs.SelectedItem is not JobRow row) return;
        BackupScheduler.WithSettingsLock(() =>
        {
            _ctx.Settings.ScheduledBackups.RemoveAll(j => j.Id == row.Job.Id);
            _ctx.SaveSettings();
        });
        Refresh();
    }

    private async Task RunNowAsync()
    {
        if (_jobs.SelectedItem is not JobRow row) return;
        _runNow.IsEnabled = false;
        try { await _scheduler.RunNowAsync(row.Job, _cts.Token); }
        finally { _runNow.IsEnabled = true; Refresh(); }
    }

    private void ClearAlerts()
    {
        BackupScheduler.WithSettingsLock(() =>
        {
            _ctx.Settings.BackupAlerts.Clear();
            _ctx.SaveSettings();
        });
        Refresh();
    }

    public void Shutdown() => _cts.Cancel();
}

/// <summary>Create or edit a scheduled backup job. Closes with the job, or null when cancelled.</summary>
public sealed class ScheduleJobWindow : Window
{
    private const string CustomLabel = "Custom commands...";

    public ScheduleJobWindow(IReadOnlyList<SessionProfile> devices, ScheduledBackupJob? existing)
    {
        Title = existing is null ? "New scheduled backup" : "Edit scheduled backup";
        Width = 640;
        Height = 700;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = Ui.Input(existing?.Name ?? "Scheduled backup", "Nightly config backup");
        var interval = Ui.Number(existing?.IntervalMinutes ?? 60, 1, 10080);
        var enabled = Ui.Check("Enabled", existing?.Enabled ?? true);
        var preset = Ui.Combo(ConfigBackup.Presets.Cast<object>().Append(CustomLabel).ToList());
        preset.ItemTemplate = new FuncDataTemplate<object>((o, _) => new TextBlock { Text = o is BackupPreset p ? p.Name : o?.ToString() });
        var customMode = Ui.Combo(new[] { "Exec", "Shell" }, existing?.CustomMode == BackupMode.Exec ? 0 : 1);
        var customCommands = Ui.MultiInput(existing?.CustomCommandsText ?? "", 90);
        var customPanel = Ui.Stack(8, Ui.Field("Mode", customMode), Ui.Field("Commands (one per line; the last one's output is saved)", customCommands));
        var hint = Ui.Text("", 12, mono: true, color: Ui.Muted);
        var root = Ui.Input(existing?.RootDirectory ?? "", mono: true);
        var error = Ui.Text("", 12, color: Ui.Rose);
        var picker = new DevicePicker { Height = 220 };
        picker.SetDevices(devices, existing?.DeviceIds);

        void PresetChanged()
        {
            bool custom = preset.SelectedItem as string == CustomLabel;
            customPanel.IsVisible = custom;
            hint.IsVisible = !custom;
            if (preset.SelectedItem is BackupPreset p) hint.Text = $"{(p.Mode == BackupMode.Exec ? "exec" : "shell")}:  {string.Join("  →  ", p.Commands)}";
        }
        preset.SelectionChanged += (_, _) => PresetChanged();
        preset.SelectedItem = existing is null ? ConfigBackup.Presets.FirstOrDefault()
            : (object?)ConfigBackup.Presets.FirstOrDefault(p => p.Name == existing.PresetName) ?? CustomLabel;
        PresetChanged();

        var save = Ui.Button("Save", () =>
        {
            var n = (name.Text ?? "").Trim();
            var selected = picker.Selected.Select(d => d.Id).ToList();
            bool custom = preset.SelectedItem as string == CustomLabel;
            string? problem = n.Length == 0 ? "Name is required."
                : selected.Count == 0 ? "Pick at least one device."
                : custom && ConfigBackup.Custom(BackupMode.Exec, customCommands.Text ?? "").Commands.Count == 0 ? "Enter at least one command."
                : !custom && preset.SelectedItem is not BackupPreset ? "Choose a vendor / command set." : null;
            if (problem is not null) { error.Text = problem; return; }
            Close(new ScheduledBackupJob
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                LastRunUtc = existing?.LastRunUtc,
                Name = n,
                DeviceIds = selected,
                PresetName = custom ? "Custom" : ((BackupPreset)preset.SelectedItem!).Name,
                CustomMode = customMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell,
                CustomCommandsText = customCommands.Text ?? "",
                IntervalMinutes = Ui.IntValue(interval, 60),
                Enabled = enabled.IsChecked == true,
                RootDirectory = (root.Text ?? "").Trim(),
            });
        }, accent: true);
        var cancel = Ui.Button("Cancel", () => Close(null));

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                    [DockPanel.DockProperty] = Dock.Bottom, Children = { cancel, save } },
                new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { error } },
                new ScrollViewer
                {
                    Content = Ui.Stack(10,
                        Ui.Columns("*,140,Auto", Ui.Field("Name", name), Ui.Field("Every (minutes)", interval), enabled),
                        Ui.Field("Vendor / command set", preset), hint, customPanel,
                        Ui.Field("Devices", picker),
                        Ui.Columns("*,Auto", Ui.Field("Save backups to (blank = default backup folder)", root),
                            Ui.Button("Browse...", async () => { if (await ToolUi.PickFolderAsync() is { } p) root.Text = p; }))),
                },
            },
        };
    }
}
