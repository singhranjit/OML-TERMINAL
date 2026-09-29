using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Tests;

public class BackupSchedulerTests
{
    [Fact]
    public void NeverRunJobIsAlwaysDue()
    {
        var job = new ScheduledBackupJob { IntervalMinutes = 60 };
        Assert.True(job.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void JobIsNotDueBeforeItsInterval()
    {
        var job = new ScheduledBackupJob { IntervalMinutes = 60, LastRunUtc = DateTime.UtcNow.AddMinutes(-30) };
        Assert.False(job.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void JobIsDueOnceIntervalElapses()
    {
        var job = new ScheduledBackupJob { IntervalMinutes = 60, LastRunUtc = DateTime.UtcNow.AddMinutes(-61) };
        Assert.True(job.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void DisabledJobIsNeverDue()
    {
        var job = new ScheduledBackupJob { Enabled = false };
        Assert.False(job.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void ResolvePresetFindsBuiltInPresetByName()
    {
        var job = new ScheduledBackupJob { PresetName = "Cisco IOS / IOS-XE" };
        var preset = job.ResolvePreset();
        Assert.Equal("Cisco IOS / IOS-XE", preset.Name);
        Assert.Equal(BackupMode.Shell, preset.Mode);
    }

    [Fact]
    public void ResolvePresetFallsBackToCustomCommands()
    {
        var job = new ScheduledBackupJob { PresetName = "Custom", CustomMode = BackupMode.Exec, CustomCommandsText = "show version\nshow running-config" };
        var preset = job.ResolvePreset();
        Assert.Equal(BackupMode.Exec, preset.Mode);
        Assert.Equal(new[] { "show version", "show running-config" }, preset.Commands);
    }

    [Fact]
    public async Task TickRunsDueJobsAndRecordsAlertOnChange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-sched-test-" + Guid.NewGuid());
        try
        {
            var settings = new AppSettings { BackupDirectory = dir };
            var device = new SessionProfile { Id = Guid.NewGuid(), Name = "test-dev", Host = "127.0.0.1", Protocol = ProtocolKind.Ssh };
            var job = new ScheduledBackupJob { Name = "Nightly", DeviceIds = [device.Id], PresetName = "Custom", CustomMode = BackupMode.Exec, CustomCommandsText = "echo hi" };
            settings.ScheduledBackups.Add(job);

            var scheduler = new BackupScheduler(settings, () => { }, () => [device]);

            // The device isn't reachable, so RunAsync will fail fast; we're only verifying the due/not-due bookkeeping
            // and that a failed run produces an alert, not real SSH connectivity.
            await scheduler.TickAsync(CancellationToken.None);

            Assert.NotNull(job.LastRunUtc);
            Assert.Single(settings.BackupAlerts);
            Assert.True(settings.BackupAlerts[0].Failed);
            Assert.Equal("test-dev", settings.BackupAlerts[0].DeviceName);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
