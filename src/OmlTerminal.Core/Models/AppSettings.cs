using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Models;

public sealed class AppSettings
{
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 14;
    public int ScrollbackLines { get; set; } = 5000;
    public string? PlinkPath { get; set; }
    public bool HighlightKeywords { get; set; } = true;
    public List<Guid> RecentSessionIds { get; set; } = new();
    /// <summary>Session IDs in the last open workspace; credentials are never stored here.</summary>
    public List<Guid> WorkspaceSessionIds { get; set; } = new();
    public bool RestoreWorkspaceOnLaunch { get; set; } = true;

    /// <summary>Explicitly created folder names (including empty ones with no sessions yet). Sessions also
    /// carry their own Folder string, so a folder can exist here from either path; "/" nests subfolders.</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>Where per-session transcript logs are written. Defaults to a "logs" folder next to the app's own data.</summary>
    public string LogDirectory { get; set; } = System.IO.Path.Combine(AppPaths.DataDirectory, "logs");

    /// <summary>When true, every new terminal session starts logging automatically instead of needing the toggle per tab.</summary>
    public bool AlwaysLogSessions { get; set; }

    /// <summary>When true, an SSH/Telnet session that drops unexpectedly retries with backoff instead of just
    /// showing "[Disconnected]". Doesn't apply to Serial (a dropped COM port usually means unplugged hardware).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Root folder for Config Backup; each device gets its own subfolder of timestamped files.</summary>
    public string BackupDirectory { get; set; } = System.IO.Path.Combine(AppPaths.DataDirectory, "backups");

    /// <summary>Explicit X server executable (vcxsrv.exe / Xming.exe / XWin.exe). Blank = auto-detect.</summary>
    public string XServerPath { get; set; } = "";

    /// <summary>X display number to use (DISPLAY=127.0.0.1:N.0).</summary>
    public int XDisplay { get; set; }

    /// <summary>Start the X server when the app starts, MobaXterm-style, instead of on the first X11 session.
    /// (Named differently from the earlier opt-in "AutoStartXServer" so existing settings files pick up the new default.)</summary>
    public bool StartXServerOnLaunch { get; set; } = true;

    /// <summary>Voice Command: the device-type grammar last used, and whether hands-free (auto-send) was on.</summary>
    public string VoiceGrammar { get; set; } = "";
    public bool VoiceHandsFree { get; set; }

    /// <summary>Set when the user declines the "install an X server?" offer, so it isn't shown on every launch.</summary>
    public bool XServerInstallDeclined { get; set; }

    /// <summary>Set together when a master password is enabled: base64 PBKDF2 salt and an encrypted check value.</summary>
    public string? MasterPasswordSalt { get; set; }
    public string? MasterPasswordVerifier { get; set; }

    /// <summary>Per-session SFTP directory history, most-recently-visited first. The SFTP Browser resumes at
    /// index 0 instead of the server's home directory, and offers the rest as a "Recent" quick-jump list.</summary>
    public Dictionary<Guid, List<string>> SftpPathHistory { get; set; } = new();

    /// <summary>Recurring config-backup jobs, run by an in-process scheduler while the app is open (see
    /// <see cref="OmlTerminal.Core.Backup.BackupScheduler"/>) - not a Windows Task Scheduler entry.</summary>
    public List<OmlTerminal.Core.Backup.ScheduledBackupJob> ScheduledBackups { get; set; } = new();

    /// <summary>Devices that changed or failed on a scheduled run, most recent first, capped at 100.</summary>
    public List<OmlTerminal.Core.Backup.BackupAlert> BackupAlerts { get; set; } = new();

    /// <summary>Local-LLM copilot: an OpenAI-compatible endpoint (LM Studio, Ollama's OpenAI-compat mode, etc).
    /// Off by default - it can run commands on real devices on its own, so turning it on is an explicit choice.</summary>
    public bool CopilotEnabled { get; set; }
    public string CopilotBaseUrl { get; set; } = "http://localhost:1234/v1";
    public string CopilotModel { get; set; } = "";
    public string CopilotApiKey { get; set; } = "";

    /// <summary>Opt-in daily check of omllabs.com for a newer release. Null until the user has been asked (first run).</summary>
    public bool? CheckForUpdates { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>A version the user said "skip this one" to - no banner for it again.</summary>
    public string SkippedUpdateVersion { get; set; } = "";

    /// <summary>Set once the first-run welcome has been shown.</summary>
    public bool WelcomeShown { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMasterPassword => !string.IsNullOrEmpty(MasterPasswordSalt) && !string.IsNullOrEmpty(MasterPasswordVerifier);
}
