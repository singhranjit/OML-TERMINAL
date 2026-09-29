using System.Text.Json;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Transports;

/// <summary>Remembers host-key fingerprints the user trusted for plink sessions ("host:port" to "SHA256:..."), since plink cannot prompt over a pipe.</summary>
public sealed class PlinkHostKeyStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(AppPaths.DataDirectory, "plink-hostkeys.json");
    private readonly object _lock = new();

    private static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public string? Get(string host, int port)
    {
        lock (_lock) return Load().GetValueOrDefault(Key(host, port));
    }

    public void Set(string host, int port, string fingerprint)
    {
        lock (_lock)
        {
            var all = Load();
            all[Key(host, port)] = fingerprint;
            JsonFile.WriteAtomic(_path, all);
        }
    }

    public bool IsLegacyAllowed(string host, int port)
    {
        lock (_lock) return Load().ContainsKey(Key(host, port) + "#legacy");
    }

    public void AllowLegacy(string host, int port)
    {
        lock (_lock)
        {
            var all = Load();
            all[Key(host, port) + "#legacy"] = "1";
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
