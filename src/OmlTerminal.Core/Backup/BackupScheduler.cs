using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Backup;

/// <summary>Drives <see cref="AppSettings.ScheduledBackups"/>: on each <see cref="TickAsync"/> (call this from a
/// once-a-minute timer), runs whatever jobs are due, and records <see cref="AppSettings.BackupAlerts"/> for any
/// device that changed or failed. Jobs only run while the app process is alive.</summary>
public sealed class BackupScheduler(AppSettings settings, Action saveSettings, Func<IReadOnlyList<SessionProfile>> resolveSessions)
{
    private const int MaxAlerts = 100;
    private readonly SemaphoreSlim _gate = new(3);

    /// <summary>Guards every read/write of AppSettings.ScheduledBackups and AppSettings.BackupAlerts, and every
    /// saveSettings() call. Static because MainWindow's background scheduler and a ScheduledBackupsView's own
    /// scheduler (built for its "Run now" button) are separate instances over the same AppSettings - without a
    /// shared lock, concurrent devices/jobs mutating a plain List&lt;T&gt; (and the UI thread reading it while a
    /// job is mid-run) could corrupt the list or crash with "Collection was modified".</summary>
    private static readonly object SettingsLock = new();

    /// <summary>Raised after a tick that ran at least one job, or after RunNowAsync - a hint to refresh the UI.</summary>
    public event Action? Changed;

    public async Task TickAsync(CancellationToken ct)
    {
        List<ScheduledBackupJob> due;
        lock (SettingsLock)
        {
            var now = DateTime.UtcNow;
            due = settings.ScheduledBackups.Where(j => j.IsDue(now)).ToList();
            if (due.Count == 0) return;
            foreach (var job in due) job.LastRunUtc = now;
            saveSettings();
        }
        await Task.WhenAll(due.Select(job => RunJobAsync(job, ct))).ConfigureAwait(false);
    }

    /// <summary>Runs one job immediately, ignoring its interval - for a "Run now" button.</summary>
    public Task RunNowAsync(ScheduledBackupJob job, CancellationToken ct)
    {
        lock (SettingsLock)
        {
            job.LastRunUtc = DateTime.UtcNow;
            saveSettings();
        }
        return RunJobAsync(job, ct);
    }

    private async Task RunJobAsync(ScheduledBackupJob job, CancellationToken ct)
    {
        IReadOnlyList<SessionProfile> devices;
        lock (SettingsLock) devices = resolveSessions().Where(s => job.DeviceIds.Contains(s.Id)).ToList();
        if (devices.Count == 0) { Changed?.Invoke(); return; }
        var preset = job.ResolvePreset();
        var root = string.IsNullOrWhiteSpace(job.RootDirectory) ? settings.BackupDirectory : job.RootDirectory;

        await Task.WhenAll(devices.Select(async device =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await ConfigBackup.RunAsync(device, preset, root, ct: ct).ConfigureAwait(false);
                if (result.ChangedSincePrevious == true || !result.Success)
                {
                    var alert = new BackupAlert
                    {
                        DeviceId = device.Id,
                        DeviceName = device.Name,
                        JobName = job.Name,
                        AtUtc = DateTime.UtcNow,
                        Failed = !result.Success,
                        Message = result.Success ? "Config changed since last backup" : $"Backup failed: {result.Message}",
                    };
                    lock (SettingsLock) settings.BackupAlerts.Insert(0, alert);
                }
            }
            finally { _gate.Release(); }
        })).ConfigureAwait(false);

        lock (SettingsLock)
        {
            if (settings.BackupAlerts.Count > MaxAlerts)
                settings.BackupAlerts.RemoveRange(MaxAlerts, settings.BackupAlerts.Count - MaxAlerts);
            saveSettings();
        }
        Changed?.Invoke();
    }

    /// <summary>The same lock BackupScheduler uses internally - take it before any other code reads or mutates
    /// AppSettings.ScheduledBackups or AppSettings.BackupAlerts directly (see ScheduledBackupsView), since those
    /// lists can be written from a background thread at any time while the scheduler is running.</summary>
    public static void WithSettingsLock(Action action) { lock (SettingsLock) action(); }
}
