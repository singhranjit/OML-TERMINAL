using OmlTerminal.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OmlTerminal.Core.Transports;

/// <summary>SSH via SSH.NET using its default algorithm set (deliberately not restricted).</summary>
public sealed class SshTransport(
    string host, int port, string username, string password, JumpHostGateway? gateway = null,
    SshAuthMethod authMethod = SshAuthMethod.Password, string privateKeyPath = "", string privateKeyPassphrase = "",
    bool retrustChangedKey = false)
    : ITerminalTransport, ISshTunnelHost
{
    private SshClient? _client;
    private ShellStream? _shell;
    private int _closing;
    private int _raised;

    private readonly Dictionary<Guid, ForwardedPort> _tunnelPorts = new();
    private readonly Dictionary<Guid, TunnelSpec> _tunnelSpecs = new();
    private readonly object _tunnelLock = new();

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;
    public event Action<TunnelSpec, string>? TunnelFailed;

    public async Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        string effectiveHost = host;
        int effectivePort = port;
        if (gateway is not null)
        {
            await gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
            effectiveHost = gateway.BoundHost;
            effectivePort = gateway.BoundPort;
        }

        AuthenticationMethod[] authMethods;
        if (authMethod == SshAuthMethod.PrivateKey)
        {
            // Thrown as-is (wraps a clear "file not found"/"bad passphrase" message from SSH.NET) so the caller's
            // existing connect-failure handling (e.g. MainWindow's "Could not launch"/"Cannot open session" dialogs)
            // surfaces it without any extra plumbing here.
            using var stream = File.OpenRead(privateKeyPath);
            var keyFile = string.IsNullOrEmpty(privateKeyPassphrase)
                ? new PrivateKeyFile(stream)
                : new PrivateKeyFile(stream, privateKeyPassphrase);
            authMethods = [new PrivateKeyAuthenticationMethod(username, keyFile)];
        }
        else
        {
            var passwordAuth = new PasswordAuthenticationMethod(username, password);
            var kbdAuth = new KeyboardInteractiveAuthenticationMethod(username);
            kbdAuth.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = password;
            };
            authMethods = [passwordAuth, kbdAuth];
        }
        var info = new ConnectionInfo(effectiveHost, effectivePort, username, authMethods)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _client = new SshClient(info);
        string? keyNotice = null;
        await Ssh.HostKeyVerifier.ConnectAsync(_client, host, port, cancellationToken, n => keyNotice = n,
            retrustChangedKey: retrustChangedKey).ConfigureAwait(false);
        _shell = _client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 8192);
        if (keyNotice is not null)
            DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes($"\x1b[33m{keyNotice}\x1b[0m\r\n"));
        _ = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        string? error = null;
        try
        {
            while (_shell is { } s)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _closing) == 0) { error = ex.Message; }
        catch { }
        RaiseClosed(error);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_shell is null || data.IsEmpty) return;
        var copy = data.ToArray();
        try { _shell.Write(copy, 0, copy.Length); _shell.Flush(); } catch { }
    }

    public void Resize(int cols, int rows)
    {
        try { _shell?.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); } catch { }
    }

    // ---------- ISshTunnelHost ----------

    public IReadOnlyList<TunnelSpec> ActiveTunnels { get { lock (_tunnelLock) return _tunnelSpecs.Values.ToList(); } }

    public TunnelSpec StartTunnel(TunnelSpec spec)
    {
        if (_client is not { IsConnected: true }) throw new InvalidOperationException("Session is not connected.");
        var errors = spec.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));

        ForwardedPort port = spec.Kind switch
        {
            TunnelKind.Local => new ForwardedPortLocal(spec.BoundHost, (uint)spec.BoundPort, spec.TargetHost, (uint)spec.TargetPort),
            TunnelKind.Remote => new ForwardedPortRemote(spec.BoundHost, (uint)spec.BoundPort, spec.TargetHost, (uint)spec.TargetPort),
            TunnelKind.Dynamic => new ForwardedPortDynamic(spec.BoundHost, (uint)spec.BoundPort),
            _ => throw new NotSupportedException($"Unknown tunnel kind {spec.Kind}."),
        };
        port.Exception += (_, e) => TunnelFailed?.Invoke(spec, e.Exception.Message);

        _client.AddForwardedPort(port);
        try { port.Start(); }
        catch { _client.RemoveForwardedPort(port); port.Dispose(); throw; }

        if (port is ForwardedPortLocal local) spec.BoundPort = (int)local.BoundPort;
        else if (port is ForwardedPortDynamic dyn) spec.BoundPort = (int)dyn.BoundPort;

        lock (_tunnelLock) { _tunnelPorts[spec.Id] = port; _tunnelSpecs[spec.Id] = spec; }
        return spec;
    }

    public void StopTunnel(Guid id)
    {
        ForwardedPort? port;
        lock (_tunnelLock)
        {
            if (!_tunnelPorts.Remove(id, out port)) return;
            _tunnelSpecs.Remove(id);
        }
        try { port.Stop(); } catch { }
        try { _client?.RemoveForwardedPort(port); } catch { }
        port.Dispose();
    }

    private void StopAllTunnels()
    {
        List<Guid> ids;
        lock (_tunnelLock) ids = _tunnelSpecs.Keys.ToList();
        foreach (var id in ids) StopTunnel(id);
    }

    // ---------- lifecycle ----------

    public void Close()
    {
        Interlocked.Exchange(ref _closing, 1);
        StopAllTunnels();
        try { _shell?.Dispose(); } catch { }
        try { _client?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _shell = null;
        _client = null;
        gateway?.Dispose();
        RaiseClosed(null);
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 0) Closed?.Invoke(error);
    }

    public void Dispose() => Close();
}
