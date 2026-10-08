using Renci.SshNet;

namespace OmlTerminal.Core.Transports;

/// <summary>
/// Opens an SSH connection to a jump/gateway host and a local port forward to the real target, so any
/// transport can reach the target by connecting to BoundHost:BoundPort instead of its real address.
/// This is transport-agnostic: it works equally for an SSH or a Telnet target, since the forward is a
/// plain TCP relay once established.
/// </summary>
public sealed class JumpHostGateway(string jumpHost, int jumpPort, string jumpUsername, string jumpPassword, string targetHost, int targetPort)
    : IDisposable
{
    private SshClient? _client;
    private ForwardedPortLocal? _forward;
    private readonly object _sync = new();

    public string BoundHost => "127.0.0.1";
    public int BoundPort { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var info = new ConnectionInfo(jumpHost, jumpPort, jumpUsername,
            new PasswordAuthenticationMethod(jumpUsername, jumpPassword))
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        var client = new SshClient(info);
        try
        {
            await Ssh.HostKeyVerifier.ConnectAsync(client, jumpHost, jumpPort, cancellationToken).ConfigureAwait(false);
            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            lock (_sync) { _client = client; _forward = forward; }
            BoundPort = (int)forward.BoundPort;
        }
        catch
        {
            try { client.Disconnect(); } catch { }
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        SshClient? client;
        ForwardedPortLocal? forward;
        lock (_sync) { client = _client; forward = _forward; _client = null; _forward = null; }
        try { forward?.Stop(); } catch { }
        forward?.Dispose();
        try { client?.Disconnect(); } catch { }
        client?.Dispose();
    }
}
