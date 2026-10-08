using System.Text.Json;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Core.ChangeGuard;

/// <summary>A change window: which devices and which checks. Saved so a POST capture hours later (or after an app
/// restart) compares against the same devices and commands as the PRE capture.</summary>
public sealed class GuardManifest
{
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string ProfileName { get; set; } = "";
    public BackupMode CustomMode { get; set; } = BackupMode.Shell;
    public string CustomCommandsText { get; set; } = "";
    public List<Guid> DeviceIds { get; set; } = new();

    public GuardProfile ResolveProfile() =>
        GuardProfiles.Find(ProfileName) ?? GuardProfiles.Custom(CustomMode, CustomCommandsText);

    public override string ToString() => Name;
}

public sealed class GuardCommandResult
{
    public string Title { get; set; } = "";
    public string Command { get; set; } = "";
    public CheckKind Kind { get; set; }
    public string File { get; set; } = "";
    /// <summary>Set when the command couldn't be run (connection dropped, timeout).</summary>
    public string? Error { get; set; }
    /// <summary>The device answered "invalid command" - this check doesn't apply to it.</summary>
    public bool Unsupported { get; set; }
}

public sealed class GuardSnapshot
{
    public DateTime TakenUtc { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Set when the device couldn't be reached at all.</summary>
    public string? Error { get; set; }
    public List<GuardCommandResult> Results { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory { get; set; } = "";

    public string? Output(GuardCommandResult r)
    {
        if (r.Error is not null || r.File.Length == 0) return null;
        try { return System.IO.File.ReadAllText(Path.Combine(Directory, r.File)); }
        catch (IOException) { return null; }
    }
}

public static class GuardStore
{
    public const string Pre = "pre";
    public const string Post = "post";

    public static string DefaultRoot => Path.Combine(AppPaths.DataDirectory, "changeguard");

    public static string ChangeDirectory(string root, string changeName) => Path.Combine(root, ConfigBackup.SafeFileName(changeName.Trim()));

    public static string DeviceFolder(SessionProfile device) =>
        $"{ConfigBackup.SafeFileName(string.IsNullOrWhiteSpace(device.Name) ? device.Host : device.Name)}_{device.Id.ToString("N")[..8]}";

    public static string SnapshotDirectory(string root, string changeName, SessionProfile device, string phase) =>
        Path.Combine(ChangeDirectory(root, changeName), DeviceFolder(device), phase);

    public static void SaveManifest(string root, GuardManifest manifest) =>
        JsonFile.WriteAtomic(Path.Combine(ChangeDirectory(root, manifest.Name), "manifest.json"), manifest);

    public static IReadOnlyList<GuardManifest> ListChanges(string root)
    {
        if (!System.IO.Directory.Exists(root)) return [];
        var list = new List<GuardManifest>();
        foreach (var dir in System.IO.Directory.GetDirectories(root))
        {
            try
            {
                var m = JsonSerializer.Deserialize<GuardManifest>(System.IO.File.ReadAllText(Path.Combine(dir, "manifest.json")), JsonFile.Options);
                if (m is not null) list.Add(m);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(m => m.CreatedUtc).ToList();
    }

    public static GuardSnapshot? Load(string root, string changeName, SessionProfile device, string phase)
    {
        var dir = SnapshotDirectory(root, changeName, device, phase);
        try
        {
            var snap = JsonSerializer.Deserialize<GuardSnapshot>(System.IO.File.ReadAllText(Path.Combine(dir, "meta.json")), JsonFile.Options);
            if (snap is not null) snap.Directory = dir;
            return snap;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Connects on a side channel, runs every check, and writes each output to its own text file (so Global
    /// Search can find a MAC or IP in a captured table later) plus meta.json. Never throws for device problems -
    /// they're recorded on the snapshot instead.</summary>
    public static async Task<GuardSnapshot> CaptureAsync(SessionProfile device, GuardProfile profile, string directory, CancellationToken ct)
    {
        var snap = new GuardSnapshot { TakenUtc = DateTime.UtcNow, DeviceId = device.Id, DeviceName = device.Name, Host = device.Host, Directory = directory };
        if (System.IO.Directory.Exists(directory))
            foreach (var old in System.IO.Directory.GetFiles(directory)) System.IO.File.Delete(old);
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var session = await DeviceSession.OpenAsync(device, profile.Mode, profile.Prep, ct).ConfigureAwait(false);
            for (int i = 0; i < profile.Checks.Count; i++)
            {
                var check = profile.Checks[i];
                var result = new GuardCommandResult { Title = check.Title, Command = check.Command, Kind = check.Kind };
                try
                {
                    var output = ConfigBackup.Normalize(await session.RunAsync(check.Command, ct).ConfigureAwait(false));
                    result.File = $"{i + 1:00}_{ConfigBackup.SafeFileName(check.Command)}.txt";
                    if (result.File.Length > 100) result.File = result.File[..96] + ".txt";
                    await System.IO.File.WriteAllTextAsync(Path.Combine(directory, result.File), output, ct).ConfigureAwait(false);
                    result.Unsupported = DeviceSession.IsCommandError(output);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Error = ex.Message;
                }
                snap.Results.Add(result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snap.Error = ex.Message;
        }
        JsonFile.WriteAtomic(Path.Combine(directory, "meta.json"), snap);
        return snap;
    }
}
