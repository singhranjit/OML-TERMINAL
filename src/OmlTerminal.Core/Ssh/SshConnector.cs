using OmlTerminal.Core.Models;
using OmlTerminal.Core.Transports;
using Renci.SshNet;

namespace OmlTerminal.Core.Ssh;

/// <summary>An SSH.NET client plus the jump-host gateway (if any) it rides through; disposing tears down both.</summary>
public sealed class ConnectedSsh(SshClient client, JumpHostGateway? gateway) : IDisposable
{
    public SshClient Client { get; } = client;

    public void Dispose()
    {
        try { Client.Disconnect(); } catch { }
        Client.Dispose();
        gateway?.Dispose();
    }
}

/// <summary>Builds authenticated SSH.NET connections from a saved profile, for the tools that need a side channel
/// to a device (config backup, remote packet capture) rather than an interactive terminal tab.</summary>
public static class SshConnector
{
    public static AuthenticationMethod[] AuthFor(SessionProfile p)
    {
        if (p.AuthMethod == SshAuthMethod.PrivateKey)
        {
            using var stream = File.OpenRead(p.PrivateKeyPath);
            var key = string.IsNullOrEmpty(p.PrivateKeyPassphrase)
                ? new PrivateKeyFile(stream)
                : new PrivateKeyFile(stream, p.PrivateKeyPassphrase);
            return [new PrivateKeyAuthenticationMethod(p.Username, key)];
        }
        var kbd = new KeyboardInteractiveAuthenticationMethod(p.Username);
        kbd.AuthenticationPrompt += (_, e) => { foreach (var prompt in e.Prompts) prompt.Response = p.Password; };
        return [new PasswordAuthenticationMethod(p.Username, p.Password), kbd];
    }

    public static async Task<ConnectedSsh> ConnectAsync(SessionProfile p, CancellationToken ct = default)
    {
        if (!p.IsSshBased) throw new NotSupportedException($"{p.Name} is not an SSH session.");
        JumpHostGateway? gateway = null;
        string host = p.Host;
        int port = p.Port;
        if (p.UseJumpHost)
        {
            gateway = new JumpHostGateway(p.JumpHost, p.JumpPort, p.JumpUsername, p.JumpPassword, p.Host, p.Port);
            await gateway.ConnectAsync(ct).ConfigureAwait(false);
            host = gateway.BoundHost;
            port = gateway.BoundPort;
        }
        var client = new SshClient(new ConnectionInfo(host, port, p.Username, AuthFor(p)) { Timeout = TimeSpan.FromSeconds(15) });
        try
        {
            await HostKeyVerifier.ConnectAsync(client, p.Host, p.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            gateway?.Dispose();
            throw;
        }
        return new ConnectedSsh(client, gateway);
    }
}
