namespace OmlTerminal.Core.Sftp;

/// <summary>
/// Tracks a remote file downloaded to a local temp copy for editing (MobaXterm's "edit via SFTP" flow).
/// Core owns the bookkeeping; the App layer only needs to launch a local editor on LocalPath and call
/// SaveBackAsync when the editor closes or the user asks to save.
/// </summary>
public sealed class RemoteEditSession(SftpSession sftp, string remotePath, string localPath)
{
    public string RemotePath { get; } = remotePath;
    public string LocalPath { get; } = localPath;

    public async Task SaveBackAsync(CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(LocalPath);
        await sftp.UploadAsync(stream, RemotePath, overwrite: true, ct).ConfigureAwait(false);
    }

    public void DeleteLocalCopy()
    {
        try { File.Delete(LocalPath); } catch { }
    }
}
