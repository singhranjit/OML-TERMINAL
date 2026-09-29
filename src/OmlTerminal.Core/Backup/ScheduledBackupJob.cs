namespace OmlTerminal.Core.Backup;

/// <summary>A recurring backup job: which devices, which command preset, how often. Runs only while the app is
/// open - this is an in-process scheduler (see <see cref="BackupScheduler"/>), not a Windows Task Scheduler entry.</summary>
public sealed class ScheduledBackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Guid> DeviceIds { get; set; } = new();

    /// <summary>Matches a <see cref="ConfigBackup.Presets"/> entry's Name, or "Custom" to use CustomMode/CustomCommandsText.</summary>
    public string PresetName { get; set; } = "";
    public BackupMode CustomMode { get; set; } = BackupMode.Shell;
    public string CustomCommandsText { get; set; } = "";

    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Blank = use the app's default backup directory (<see cref="Models.AppSettings.BackupDirectory"/>).</summary>
    public string RootDirectory { get; set; } = "";

    public BackupPreset ResolvePreset() =>
        ConfigBackup.Presets.FirstOrDefault(p => p.Name == PresetName)
        ?? ConfigBackup.Custom(CustomMode, CustomCommandsText);

    public bool IsDue(DateTime nowUtc) =>
        Enabled && (LastRunUtc is null || nowUtc - LastRunUtc.Value >= TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes)));
}

/// <summary>One noteworthy event from a scheduled run: a config changed, or a backup failed. Kept around (capped)
/// so you can catch up on what happened while you weren't watching.</summary>
public sealed class BackupAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string JobName { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string Message { get; set; } = "";
    public bool Failed { get; set; }
    public bool Seen { get; set; }
}
