using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Ssh;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Core.Backup;

public enum BackupMode
{
    /// <summary>One SSH exec request per command - clean output, no prompts or paging. Best where supported.</summary>
    Exec,
    /// <summary>Interactive shell: type commands, read until the device goes quiet. For CLIs that reject exec (PAN-OS, ASA).</summary>
    Shell,
}

public sealed record BackupPreset(string Name, BackupMode Mode, IReadOnlyList<string> Commands, string FileExtension = ".cfg")
{
    public override string ToString() => Name;
}

public sealed record BackupResult(SessionProfile Device, bool Success, string? FilePath, int Lines, long Bytes,
    string Message, bool? ChangedSincePrevious, string? PreviousFile);

public static partial class ConfigBackup
{
    /// <summary>The last command in each list is the one whose output is saved; earlier ones prepare the CLI.</summary>
    public static readonly IReadOnlyList<BackupPreset> Presets =
    [
        new("FortiGate (full-configuration)", BackupMode.Exec, ["show full-configuration"], ".conf"),
        new("FortiGate (non-default only)", BackupMode.Exec, ["show"], ".conf"),
        new("Palo Alto PAN-OS (set format)", BackupMode.Shell, ["set cli pager off", "set cli config-output-format set", "show config running"], ".txt"),
        new("Palo Alto PAN-OS (XML)", BackupMode.Shell, ["set cli pager off", "show config running"], ".xml"),
        new("Cisco IOS / IOS-XE", BackupMode.Shell, ["terminal length 0", "show running-config"]),
        new("Cisco NX-OS", BackupMode.Exec, ["show running-config"]),
        new("Cisco ASA", BackupMode.Shell, ["terminal pager 0", "show running-config"]),
        new("Juniper Junos", BackupMode.Exec, ["show configuration | display set | no-more"], ".set"),
        new("Aruba AOS-CX / HPE ProCurve", BackupMode.Shell, ["no page", "show running-config"]),
        new("MikroTik RouterOS", BackupMode.Exec, ["/export"], ".rsc"),
        new("Check Point Gaia", BackupMode.Shell, ["set clienv rows 0", "show configuration"]),
        new("Linux (iptables + interfaces)", BackupMode.Exec, ["sh -c 'ip addr; ip route; sudo -n iptables-save 2>/dev/null'"], ".txt"),
    ];

    public static BackupPreset Custom(BackupMode mode, string commandsText) =>
        new("Custom", mode, TextLines.Split(commandsText).Select(l => l.Trim()).Where(l => l.Length > 0).ToList());

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "backups");

    public static async Task<BackupResult> RunAsync(SessionProfile device, BackupPreset preset, string rootDirectory,
        TimeSpan? idleTimeout = null, CancellationToken ct = default)
    {
        try
        {
            if (preset.Commands.Count == 0) throw new InvalidOperationException("No commands to run.");
            using var ssh = await SshConnector.ConnectAsync(device, ct).ConfigureAwait(false);
            string output = preset.Mode == BackupMode.Exec
                ? await RunExecAsync(ssh, preset.Commands, ct).ConfigureAwait(false)
                : await RunShellAsync(ssh, preset.Commands, device.EnablePassword, idleTimeout ?? TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);

            output = Normalize(output);
            if (output.Trim().Length == 0) throw new InvalidOperationException("Device returned no output - check the preset and user privileges.");
            // Saving "% Invalid input" as a backup would bury the real history behind a bogus "changed" entry.
            if (DeviceSession.IsCommandError(output))
                throw new InvalidOperationException($"The device rejected the command ({output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 1 && l != "^")}) - pick the command set for this vendor.");

            var (path, previous) = TargetPath(rootDirectory, device, preset.FileExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, output, ct).ConfigureAwait(false);

            bool? changed = previous is null ? null : !SameContent(previous, output);
            int lines = output.Count(c => c == '\n');
            return new BackupResult(device, true, path, lines, new FileInfo(path).Length,
                changed switch { null => "First backup", true => "Changed since last backup", false => "No change since last backup" },
                changed, previous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BackupResult(device, false, null, 0, 0, ex.Message, null, null);
        }
    }

    private static async Task<string> RunExecAsync(ConnectedSsh ssh, IReadOnlyList<string> commands, CancellationToken ct)
    {
        string last = "";
        foreach (var command in commands)
        {
            using var cmd = ssh.Client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(3);
            await cmd.ExecuteAsync(ct).ConfigureAwait(false);
            last = cmd.Result;
            if (string.IsNullOrEmpty(last) && !string.IsNullOrEmpty(cmd.Error)) last = cmd.Error;
        }
        return last;
    }

    [GeneratedRegex(@"(-+\s*More\s*-+|--More--|<--- More --->|Press any key to continue)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MorePrompt();

    /// <summary>Types each command and collects output until the device has been silent for idleTimeout. Paging
    /// prompts that slip through ("--More--") are answered with a space, so a failed "terminal length 0" still works.</summary>
    private static async Task<string> RunShellAsync(ConnectedSsh ssh, IReadOnlyList<string> commands, string enablePassword, TimeSpan idle, CancellationToken ct)
    {
        using var shell = ssh.Client.CreateShellStream("vt100", 512, 200, 0, 0, 1 << 16);
        var text = await ReadUntilQuietAsync(shell, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // banner + first prompt
        await EnterEnableAsync(shell, text, enablePassword, ct).ConfigureAwait(false);
        string last = "";
        for (int i = 0; i < commands.Count; i++)
        {
            shell.WriteLine(commands[i]);
            shell.Flush();
            var output = await ReadUntilQuietAsync(shell, i == commands.Count - 1 ? idle : TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            if (i == commands.Count - 1) last = StripEchoAndPrompt(output, commands[i]);
        }
        try { shell.WriteLine("exit"); } catch { }
        return last;
    }

    /// <summary>Landed at "sw1>"? Uses the saved enable password to reach privileged mode. Returns the last text read
    /// (ending in the current prompt).</summary>
    public static async Task<string> EnterEnableAsync(Renci.SshNet.ShellStream shell, string text, string enablePassword, CancellationToken ct)
    {
        if (enablePassword.Length == 0) return text;
        var enable = new LoginAutomator("", "", enablePassword, answerLogin: false);
        for (int step = 0; step < 3 && !enable.IsDone; step++)
        {
            var reply = enable.OnOutput(text);
            if (reply is null) break;
            shell.Write(reply);
            shell.Flush();
            text = await ReadUntilQuietAsync(shell, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        return text;
    }

    /// <summary>Reads from an interactive shell stream until it's been quiet for <paramref name="idle"/>, answering
    /// "--More--" style paging prompts along the way. Shared with <see cref="Copilot.DeviceShell"/>, which drives
    /// the same kind of shell to run commands a copilot chooses one at a time.</summary>
    public static async Task<string> ReadUntilQuietAsync(Renci.SshNet.ShellStream shell, TimeSpan idle, CancellationToken ct, string? prompt = null)
    {
        var sb = new StringBuilder();
        var buf = new byte[1 << 16];
        var lastData = DateTime.UtcNow;
        var hardStop = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow - lastData < idle && DateTime.UtcNow < hardStop)
        {
            ct.ThrowIfCancellationRequested();
            // The prompt is back and nothing more has arrived for a moment: the command is done - no need to sit out the idle time.
            if (prompt is { Length: > 0 } && DateTime.UtcNow - lastData > TimeSpan.FromMilliseconds(150) && sb.Length > 0
                && sb.ToString(Math.Max(0, sb.Length - prompt.Length - 4), Math.Min(sb.Length, prompt.Length + 4)).TrimEnd().EndsWith(prompt, StringComparison.Ordinal))
                break;
            if (shell.DataAvailable)
            {
                int n = shell.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(AnsiStripper.Strip(buf.AsSpan(0, n))));
                    lastData = DateTime.UtcNow;
                    var more = MorePrompt().Match(sb.ToString(Math.Max(0, sb.Length - 64), Math.Min(64, sb.Length)));
                    if (more.Success)
                    {
                        sb.Length -= more.Length;
                        shell.Write(" ");
                        shell.Flush();
                    }
                    continue;
                }
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return sb.ToString();
    }

    public static string StripEchoAndPrompt(string text, string command)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        int echo = lines.FindIndex(l => l.TrimEnd().EndsWith(command, StringComparison.Ordinal));
        if (echo >= 0) lines.RemoveRange(0, echo + 1);
        TrimBlankTail(lines);
        if (lines.Count > 0 && LooksLikePrompt(lines[^1])) lines.RemoveAt(lines.Count - 1);
        TrimBlankTail(lines);
        return string.Join('\n', lines);

        static void TrimBlankTail(List<string> l) { while (l.Count > 0 && l[^1].Trim().Length == 0) l.RemoveAt(l.Count - 1); }
    }

    public static bool LooksLikePrompt(string line)
    {
        var t = line.TrimEnd();
        return t.Length is > 0 and < 80 && !t.Contains("  ") && (t.EndsWith('#') || t.EndsWith('>') || t.EndsWith('$') || t.EndsWith("]"));
    }

    /// <summary>Unix line endings, no backspaces, no trailing whitespace - so identical configs hash identically.</summary>
    public static string Normalize(string s)
    {
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\b", "").Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).Trim('\n') + "\n";
    }

    public static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "device" : cleaned;
    }

    /// <summary>{root}/{device}/{device}_{yyyy-MM-dd_HHmmss}.cfg, plus the newest earlier backup of that device (for change detection).</summary>
    public static (string Path, string? Previous) TargetPath(string root, SessionProfile device, string extension)
    {
        var name = SafeFileName(string.IsNullOrWhiteSpace(device.Name) ? device.Host : device.Name);
        var dir = Path.Combine(root, name);
        string? previous = Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"{name}_*").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;
        return (Path.Combine(dir, $"{name}_{DateTime.Now:yyyy-MM-dd_HHmmss}{extension}"), previous);
    }

    private static bool SameContent(string previousPath, string current)
    {
        try
        {
            var prev = Normalize(File.ReadAllText(previousPath));
            return Hash(prev) == Hash(Normalize(current));
        }
        catch { return false; }
    }

    /// <summary>FortiOS stamps "#conf_file_ver=" and encrypted secrets change on every read; ignore such volatile lines.</summary>
    private static string Hash(string s)
    {
        var stable = string.Join('\n', s.Split('\n').Where(l => !IsVolatile(l)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
    }

    public static bool IsVolatile(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("#conf_file_ver=") || t.StartsWith("! Last configuration change") || t.StartsWith("! NVRAM config last updated")
            || t.StartsWith("Current configuration :") || t.StartsWith("Building configuration") || t.StartsWith("## Last commit:") || t.Contains(" ENC ");
    }

    /// <summary>Order-insensitive line diff (what was added / removed) - readable for configs, where a moved line
    /// usually isn't a real change.</summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Diff(string before, string after)
    {
        static Dictionary<string, int> Counts(string s)
        {
            var d = new Dictionary<string, int>();
            foreach (var l in TextLines.Split(s).Select(x => x.TrimEnd()).Where(x => x.Trim().Length > 0 && !IsVolatile(x)))
                d[l] = d.GetValueOrDefault(l) + 1;
            return d;
        }
        var a = Counts(before);
        var b = Counts(after);
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var (line, n) in b) for (int i = a.GetValueOrDefault(line); i < n; i++) added.Add(line);
        foreach (var (line, n) in a) for (int i = b.GetValueOrDefault(line); i < n; i++) removed.Add(line);
        return (added, removed);
    }
}
