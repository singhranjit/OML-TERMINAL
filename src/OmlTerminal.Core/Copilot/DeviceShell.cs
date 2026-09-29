using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Core.Copilot;

/// <summary>An interactive shell on its own side-channel SSH connection to one device - the same mechanism
/// <see cref="ConfigBackup"/> uses, so the copilot never touches a terminal tab a person has open, and can't
/// interleave its commands with what someone is typing.</summary>
public sealed class DeviceShell : IDisposable
{
    private readonly ConnectedSsh _ssh;
    private readonly Renci.SshNet.ShellStream _shell;

    private DeviceShell(ConnectedSsh ssh, Renci.SshNet.ShellStream shell)
    {
        _ssh = ssh;
        _shell = shell;
    }

    public static async Task<DeviceShell> OpenAsync(SessionProfile device, CancellationToken ct)
    {
        var ssh = await SshConnector.ConnectAsync(device, ct);
        Renci.SshNet.ShellStream? shell = null;
        try
        {
            shell = ssh.Client.CreateShellStream("vt100", 512, 200, 0, 0, 1 << 16);
            await ConfigBackup.ReadUntilQuietAsync(shell, TimeSpan.FromSeconds(2), ct); // banner + first prompt
            return new DeviceShell(ssh, shell);
        }
        catch
        {
            shell?.Dispose();
            ssh.Dispose();
            throw;
        }
    }

    public async Task<string> RunAsync(string command, TimeSpan idle, CancellationToken ct)
    {
        _shell.WriteLine(command);
        _shell.Flush();
        var raw = await ConfigBackup.ReadUntilQuietAsync(_shell, idle, ct);
        return ConfigBackup.StripEchoAndPrompt(raw, command);
    }

    public void Dispose()
    {
        _shell.Dispose();
        _ssh.Dispose();
    }
}
