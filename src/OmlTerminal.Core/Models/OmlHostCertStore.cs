using System.Text.Json;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Models;

/// <summary>
/// Trust-on-first-use pinning for an OML Labs host's TLS certificate (SHA-256 thumbprint keyed by host), mirroring
/// PlinkHostKeyStore's approach for SSH host keys. These platforms are typically reached over a bare LAN IP with a
/// self-signed certificate, so ordinary CA validation isn't meaningful here - pinning after explicit user consent
/// is the safer alternative to silently disabling certificate validation altogether.
/// </summary>
public sealed class OmlHostCertStore(string? path = null)
{
    private readonly string _path = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "oml-host-certs.json");
    private readonly object _lock = new();

    private static string Key(string host) => host.Trim().ToLowerInvariant();

    public string? Get(string host)
    {
        lock (_lock) return Load().GetValueOrDefault(Key(host));
    }

    public bool IsTrusted(string host, string thumbprint) => Get(host) == thumbprint;

    public void Trust(string host, string thumbprint)
    {
        lock (_lock)
        {
            var all = Load();
            all[Key(host)] = thumbprint;
            JsonFile.WriteAtomic(_path, all);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException) { }
        return new();
    }
}

/// <summary>Thrown when an OML host presents a TLS certificate that hasn't been trusted yet, carrying enough to show a trust prompt.</summary>
public sealed class OmlUntrustedCertificateException(string host, string thumbprint, string subject) : Exception($"Untrusted certificate for {host}")
{
    public string Host { get; } = host;
    public string Thumbprint { get; } = thumbprint;
    public string Subject { get; } = subject;
}
