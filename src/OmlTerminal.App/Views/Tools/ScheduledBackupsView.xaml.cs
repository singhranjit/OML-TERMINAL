using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Backup;

namespace OmlTerminal.App.Views.Tools;

public sealed class JobRow(ScheduledBackupJob job, int deviceCount)
{
    public ScheduledBackupJob Job { get; } = job;
    public string Name => Job.Name;
    public string DevicesText => $"{deviceCount} device{(deviceCount == 1 ? "" : "s")}";
    public string IntervalText => Job.IntervalMinutes >= 60 && Job.IntervalMinutes % 60 == 0
        ? $"every {Job.IntervalMinutes / 60}h" : $"every {Job.IntervalMinutes}m";
    public string LastRunText => Job.LastRunUtc is { } t ? $"last run {t.ToLocalTime():MMM d, HH:mm}" : "never run yet";
    public bool Enabled => Job.Enabled;
}

public sealed class AlertRow(BackupAlert alert)
{
    public BackupAlert Alert { get; } = alert;
    public string DeviceName => Alert.DeviceName;
    public string JobName => Alert.JobName;
    public string Message => Alert.Message;
    public string TimeText => Alert.AtUtc.ToLocalTime().ToString("MMM d, HH:mm");
    public Brush Brush => ToolUi.Brush(Alert.Failed ? "OmlRoseBrush" : "OmlAmberBrush");
    public Windows.UI.Text.FontWeight Weight => Alert.Seen ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
}

public sealed partial class ScheduledBackupsView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly BackupScheduler _scheduler;
    private readonly ObservableCollection<JobRow> _jobRows = new();
    private readonly ObservableCollection<AlertRow> _alertRows = new();
    private readonly CancellationTokenSource _cts = new();

    public ScheduledBackupsView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _scheduler = new BackupScheduler(ctx.Settings, ctx.SaveSettings, ctx.SshSessions);
        JobList.ItemsSource = _jobRows;
        AlertList.ItemsSource = _alertRows;
        Loaded += (_, _) => Refresh(markAlertsSeen: true);
    }

    /// <summary>Rebuilds both lists from settings. Opening this tool counts as "checking", so by default it also
    /// marks whatever alerts are currently showing as seen - the amber "new" styling is for what arrived since
    /// the last time you had this tab open.
    /// Takes BackupScheduler's shared lock because the background scheduler can be inserting/trimming
    /// AppSettings.BackupAlerts from another thread at any moment - enumerating it unlocked here could otherwise
    /// throw "Collection was modified" or read a half-updated list.</summary>
    private void Refresh(bool markAlertsSeen = false)
    {
        var devices = _ctx.SshSessions();
        BackupScheduler.WithSettingsLock(() =>
        {
            _jobRows.Clear();
            foreach (var job in _ctx.Settings.ScheduledBackups)
                _jobRows.Add(new JobRow(job, job.DeviceIds.Count(id => devices.Any(d => d.Id == id))));

            _alertRows.Clear();
            foreach (var alert in _ctx.Settings.BackupAlerts) _alertRows.Add(new AlertRow(alert));
            var unseen = _ctx.Settings.BackupAlerts.Count(a => !a.Seen);
            UnseenText.Text = unseen > 0 ? $"{unseen} new" : "";

            if (markAlertsSeen && unseen > 0)
            {
                foreach (var a in _ctx.Settings.BackupAlerts) a.Seen = true;
                _ctx.SaveSettings();
            }
        });
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScheduleJobDialog(XamlRoot, _ctx.SshSessions(), null);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } job)
        {
            BackupScheduler.WithSettingsLock(() =>
            {
                _ctx.Settings.ScheduledBackups.Add(job);
                _ctx.SaveSettings();
            });
            Refresh();
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobRow row) return;
        var dialog = new ScheduleJobDialog(XamlRoot, _ctx.SshSessions(), row.Job);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } updated)
        {
            // The scheduler can have run this job (and updated its LastRunUtc) while the dialog was open, since it
            // ticks once a minute independently of this view. Re-read that field right before replacing, instead
            // of trusting the value the dialog captured when it opened, or a completed run would appear undone.
            BackupScheduler.WithSettingsLock(() =>
            {
                var idx = _ctx.Settings.ScheduledBackups.FindIndex(j => j.Id == updated.Id);
                if (idx >= 0)
                {
                    updated.LastRunUtc = _ctx.Settings.ScheduledBackups[idx].LastRunUtc;
                    _ctx.Settings.ScheduledBackups[idx] = updated;
                }
                _ctx.SaveSettings();
            });
            Refresh();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobRow row) return;
        BackupScheduler.WithSettingsLock(() =>
        {
            _ctx.Settings.ScheduledBackups.RemoveAll(j => j.Id == row.Job.Id);
            _ctx.SaveSettings();
        });
        Refresh();
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobRow row) return;
        RunNowButton.IsEnabled = false;
        try { await _scheduler.RunNowAsync(row.Job, _cts.Token); }
        finally { RunNowButton.IsEnabled = true; Refresh(); }
    }

    private void Enabled_Toggled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not JobRow row) return;
        BackupScheduler.WithSettingsLock(() =>
        {
            row.Job.Enabled = ((ToggleSwitch)sender).IsOn;
            _ctx.SaveSettings();
        });
    }

    private void ClearAlerts_Click(object sender, RoutedEventArgs e)
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
