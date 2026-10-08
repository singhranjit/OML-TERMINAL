using Renci.SshNet;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Sftp;

/// <summary>
/// A remote-file browser and transfer session over SFTP, reusing the same SSH.NET stack the terminal transport
/// uses. Can optionally ride through a jump host, exactly like SshTransport. Independent of any open terminal tab -
/// a session can browse files without a shell being open, matching MobaXterm's split terminal/SFTP panes.
/// </summary>
public sealed class SftpSession(string host, int port, string username, string password, JumpHostGateway? gateway = null,
    AuthenticationMethod[]? auth = null) : IDisposable
{
    private SftpClient? _client;

    public bool IsConnected => _client?.IsConnected == true;
    public string WorkingDirectory => _client?.WorkingDirectory ?? "/";

    public static SftpSession For(SessionProfile p, string? plinkPath = null)
    {
        if (!p.IsSshBased) throw new NotSupportedException("SFTP requires an SSH session.");
        var gw = p.UseJumpHost ? new JumpHostGateway(p.JumpHost, p.JumpPort, p.JumpUsername, p.JumpPassword, p.Host, p.Port) : null;
        return new SftpSession(p.Host, p.Port, p.Username, p.Password, gw, Ssh.SshConnector.AuthFor(p));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        string effectiveHost = host;
        int effectivePort = port;
        if (gateway is not null)
        {
            await gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
            effectiveHost = gateway.BoundHost;
            effectivePort = gateway.BoundPort;
        }
        var info = new ConnectionInfo(effectiveHost, effectivePort, username, auth ?? [new PasswordAuthenticationMethod(username, password)])
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        var client = new SftpClient(info);
        try
        {
            await Ssh.HostKeyVerifier.ConnectAsync(client, host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
    }

    private SftpClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    public async Task<List<SftpEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var entries = new List<SftpEntry>();
        await foreach (var f in Client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (f.Name is "." or "..") continue;
            entries.Add(new SftpEntry(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTimeUtc, f.IsSymbolicLink));
        }
        entries.Sort((a, b) => a.IsDirectory != b.IsDirectory ? b.IsDirectory.CompareTo(a.IsDirectory) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    public Task DownloadAsync(string remotePath, Stream target, CancellationToken ct = default) =>
        Client.DownloadFileAsync(remotePath, target, ct);

    public Task UploadAsync(Stream source, string remotePath, bool overwrite = true, CancellationToken ct = default) =>
        Client.UploadFileAsync(source, remotePath, overwrite, (IProgress<Renci.SshNet.UploadFileProgressReport>?)null, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Client.CreateDirectoryAsync(path, ct);
    public Task DeleteFileAsync(string path, CancellationToken ct = default) => Client.DeleteFileAsync(path, ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) => Client.DeleteDirectoryAsync(path, ct);
    public Task RenameAsync(string from, string to, CancellationToken ct = default) => Client.RenameFileAsync(from, to, ct);

    public static string Combine(string dir, string name) => dir.TrimEnd('/') is "" ? "/" + name : dir.TrimEnd('/') + "/" + name;

    public static string Parent(string path)
    {
        path = path.TrimEnd('/');
        if (path.Length == 0) return "/";
        int i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public void Close()
    {
        try { _client?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        gateway?.Dispose();
    }

    public void Dispose() => Close();
}
