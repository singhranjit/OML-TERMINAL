using System.Text.RegularExpressions;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Ssh;

/// <summary>A side-channel SSH connection for running several read-only commands on one device - exec channel per
/// command, or one interactive shell (with enable handled) for CLIs that reject exec. Never touches a terminal tab.</summary>
public sealed partial class DeviceSession : IDisposable
{
    private readonly ConnectedSsh _ssh;
    private readonly Renci.SshNet.ShellStream? _shell;

    /// <summary>The device's CLI prompt as last seen in shell mode ("SW1#", "admin@srx1>"), or "" in exec mode.</summary>
    public string Prompt { get; }

    /// <summary>Hostname taken from the prompt, or null when it couldn't be determined (exec mode, unusual prompt).</summary>
    public string? Hostname => HostnameFromPrompt(Prompt);

    private DeviceSession(ConnectedSsh ssh, Renci.SshNet.ShellStream? shell, string prompt)
    {
        _ssh = ssh;
        _shell = shell;
        Prompt = prompt;
    }

    public static async Task<DeviceSession> OpenAsync(SessionProfile device, BackupMode mode, IReadOnlyList<string> prep, CancellationToken ct)
    {
        var ssh = await SshConnector.ConnectAsync(device, ct).ConfigureAwait(false);
        if (mode == BackupMode.Exec) return new DeviceSession(ssh, null, "");
        Renci.SshNet.ShellStream? shell = null;
        try
        {
            shell = ssh.Client.CreateShellStream("vt100", 512, 200, 0, 0, 1 << 16);
            var text = await ConfigBackup.ReadUntilQuietAsync(shell, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            text = await ConfigBackup.EnterEnableAsync(shell, text, device.EnablePassword, ct).ConfigureAwait(false);
            foreach (var p in prep)
            {
                shell.WriteLine(p);
                shell.Flush();
                text = await ConfigBackup.ReadUntilQuietAsync(shell, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            }
            return new DeviceSession(ssh, shell, LastLine(text));
        }
        catch
        {
            shell?.Dispose();
            ssh.Dispose();
            throw;
        }
    }

    public async Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? idle = null)
    {
        if (_shell is null)
        {
            using var cmd = _ssh.Client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(2);
            await cmd.ExecuteAsync(ct).ConfigureAwait(false);
            var result = cmd.Result;
            return string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(cmd.Error) ? cmd.Error : result;
        }
        _shell.WriteLine(command);
        _shell.Flush();
        var raw = await ConfigBackup.ReadUntilQuietAsync(_shell, idle ?? TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
        return ConfigBackup.StripEchoAndPrompt(raw, command);
    }

    private static string LastLine(string text)
    {
        var lines = TextLines.Split(text);
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].Trim().Length > 0) return lines[i].Trim();
        return "";
    }

    [GeneratedRegex(@"^(?:[\w.\-]+@)?(?<host>[A-Za-z0-9][\w.\-]{0,62})(?:\([^)]*\))?(?:/[\w.\-]+)?\s*[#>$%]\s*$")]
    private static partial Regex PromptPattern();

    /// <summary>"SW1#" → SW1, "admin@srx1>" → srx1, "asa/ctx1>" → asa, "FGT-01 #" → FGT-01, "SW1(config)#" → SW1.</summary>
    public static string? HostnameFromPrompt(string prompt)
    {
        var m = PromptPattern().Match(prompt.Trim());
        return m.Success ? m.Groups["host"].Value : null;
    }

    /// <summary>True when a command's output is the device saying it doesn't know that command, rather than real data.</summary>
    public static bool IsCommandError(string output)
    {
        var t = output.TrimStart();
        if (t.Length == 0) return false;
        var head = t.Length > 300 ? t[..300] : t;
        return head.Contains("% Invalid", StringComparison.OrdinalIgnoreCase)
            || head.Contains("Invalid input", StringComparison.OrdinalIgnoreCase)
            || head.Contains("Incomplete command", StringComparison.OrdinalIgnoreCase)
            || head.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
            || head.Contains("Unrecognized command", StringComparison.OrdinalIgnoreCase)
            || head.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
            || head.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || head.Contains("command parse error", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("Command fail", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _shell?.Dispose();
        _ssh.Dispose();
    }
}
