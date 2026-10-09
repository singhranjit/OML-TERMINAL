using System.Text.Json;
using OmlTerminal.Core.Persistence;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OmlTerminal.Core.Ssh;

public sealed class KnownHost
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Algorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
}

public enum HostKeyStatus { New, Match, Mismatch }

/// <summary>Saved SSH host keys (known_hosts.json), trust-on-first-use like PuTTY/OpenSSH: a device's key is
/// remembered the first time, and a different key later blocks the connection instead of silently trusting it.
/// Keys are stored per host, port and key algorithm, so a device offering a second key type isn't a false alarm.</summary>
public sealed class KnownHostStore(string path)
{
    private readonly object _lock = new();
    private List<KnownHost>? _entries;

    public string Path { get; } = path;

    private static bool Same(KnownHost k, string host, int port) =>
        k.Port == port && string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase);

    private List<KnownHost> Entries()
    {
        if (_entries is not null) return _entries;
        try { _entries = File.Exists(Path) ? JsonSerializer.Deserialize<List<KnownHost>>(File.ReadAllText(Path), JsonFile.Options) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            if (ex is JsonException) Persistence.UnreadableFile.Keep(Path);
            _entries = [];
        }
        return _entries;
    }

    public (HostKeyStatus Status, KnownHost? Stored) Check(string host, int port, string algorithm, string fingerprint)
    {
        lock (_lock)
        {
            var stored = Entries().FirstOrDefault(k => Same(k, host, port) && k.Algorithm == algorithm);
            if (stored is null) return (HostKeyStatus.New, null);
            return (stored.Fingerprint == fingerprint ? HostKeyStatus.Match : HostKeyStatus.Mismatch, stored);
        }
    }

    public void Trust(string host, int port, string algorithm, string fingerprint)
    {
        lock (_lock)
        {
            var list = Entries();
            list.RemoveAll(k => Same(k, host, port) && k.Algorithm == algorithm);
            list.Add(new KnownHost { Host = host, Port = port, Algorithm = algorithm, Fingerprint = fingerprint });
            JsonFile.WriteAtomic(Path, list);
        }
    }

    /// <summary>Removes every saved key for the host (all algorithms). Returns how many were removed.</summary>
    public int Forget(string host, int port)
    {
        lock (_lock)
        {
            var list = Entries();
            int n = list.RemoveAll(k => Same(k, host, port));
            if (n > 0) JsonFile.WriteAtomic(Path, list);
            return n;
        }
    }
}

public sealed class HostKeyMismatchException(string host, int port, string expected, string actual)
    : Exception($"The SSH host key for {host}:{port} has CHANGED - the connection was blocked.\n\n" +
                $"Saved:   {expected}\nOffered: {actual}\n\n" +
                "Someone could be intercepting this connection. If the device was replaced, reinstalled or re-keyed, " +
                "right-click the session, choose \"Forget saved host key\", and connect again.")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
}

public static class HostKeyVerifier
{
    public static KnownHostStore Default { get; } = new(System.IO.Path.Combine(AppPaths.DataDirectory, "known_hosts.json"));

    /// <summary>Connects with host-key checking. <paramref name="host"/>/<paramref name="port"/> are the device's real
    /// address - not the 127.0.0.1 forward used when riding a jump host. <paramref name="onNotice"/> gets a one-line
    /// message when a key is saved for the first time (or re-trusted). <paramref name="retrustChangedKey"/> is only for
    /// throwaway lab nodes, which are rebuilt with fresh keys all the time.</summary>
    public static async Task ConnectAsync(BaseClient client, string host, int port, CancellationToken ct,
        Action<string>? onNotice = null, KnownHostStore? store = null, bool retrustChangedKey = false)
    {
        store ??= Default;
        HostKeyMismatchException? mismatch = null;
        string? notice = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            var (status, stored) = store.Check(host, port, e.HostKeyName, fp);
            switch (status)
            {
                case HostKeyStatus.Match:
                    e.CanTrust = true;
                    break;
                case HostKeyStatus.New:
                    store.Trust(host, port, e.HostKeyName, fp);
                    notice = $"First connection to {host}:{port} - saved its host key ({e.HostKeyName} {fp}).";
                    e.CanTrust = true;
                    break;
                case HostKeyStatus.Mismatch when retrustChangedKey:
                    store.Trust(host, port, e.HostKeyName, fp);
                    notice = $"Lab node {host}:{port} has a new host key ({e.HostKeyName} {fp}) - expected after a node rebuild; saved.";
                    e.CanTrust = true;
                    break;
                default:
                    mismatch = new HostKeyMismatchException(host, port, $"{stored!.Algorithm} {stored.Fingerprint}", $"{e.HostKeyName} {fp}");
                    e.CanTrust = false;
                    break;
            }
        };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (mismatch is not null)
        {
            throw mismatch;
        }
        if (notice is not null) onNotice?.Invoke(notice);
    }
}
