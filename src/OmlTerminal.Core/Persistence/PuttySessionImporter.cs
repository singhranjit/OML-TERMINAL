using Microsoft.Win32;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Persistence;

/// <summary>Reads existing PuTTY saved sessions (read-only - this never writes to PuTTY's own config) so someone
/// migrating from PuTTY doesn't have to re-type every saved device by hand. On Windows they live in the registry;
/// PuTTY on Linux/macOS keeps one "Key=Value" file per session in ~/.putty/sessions. The dedicated
/// "OML-Terminal-Legacy" session this app itself creates (see PuttyLegacyProfile) is skipped since it's internal
/// plumbing, not a real user session.</summary>
public static class PuttySessionImporter
{
    private const string SessionsRoot = @"Software\SimonTatham\PuTTY\Sessions";

    /// <param name="KeyNeedsConversion">True when the session has a private-key file but it's PuTTY's own
    /// .ppk format, which SSH.NET cannot read directly - the profile is still imported (with password auth)
    /// so the rest of it isn't lost, but the key won't work until re-exported as OpenSSH format via PuTTYgen.</param>
    public sealed record ImportedSession(SessionProfile Profile, bool KeyNeedsConversion);

    public static List<ImportedSession> FindSessions()
    {
        if (!OperatingSystem.IsWindows())
            return FindSessionsInDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".putty", "sessions"));

        var results = new List<ImportedSession>();
        using var root = Registry.CurrentUser.OpenSubKey(SessionsRoot);
        if (root is null) return results;
        foreach (var encodedName in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(encodedName);
            if (key is null) continue;
            if (Map(encodedName, name => key.GetValue(name)) is { } s) results.Add(s);
        }
        return Sorted(results);
    }

    /// <summary>Unix PuTTY's file store: each file is a session (name escaped the same way as the registry key),
    /// each line "Key=Value", numbers written as plain decimal.</summary>
    public static List<ImportedSession> FindSessionsInDirectory(string directory)
    {
        var results = new List<ImportedSession>();
        if (!Directory.Exists(directory)) return results;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            Dictionary<string, string> values;
            try
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(file))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line[..eq]] = line[(eq + 1)..];
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            object? Get(string name) => values.TryGetValue(name, out var v) ? (int.TryParse(v, out var i) ? i : v) : null;
            if (Map(Path.GetFileName(file), Get) is { } s) results.Add(s);
        }
        return Sorted(results);
    }

    private static List<ImportedSession> Sorted(List<ImportedSession> results) =>
        results.OrderBy(r => r.Profile.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static ImportedSession? Map(string encodedName, Func<string, object?> get)
    {
        if (encodedName == PuttyLegacyProfile.SessionName) return null;
        string host = get("HostName") as string ?? "";

        var protocol = (get("Protocol") as string) switch
        {
            "ssh" => ProtocolKind.Ssh,
            "telnet" or "rlogin" or "raw" => ProtocolKind.Telnet,
            "serial" => ProtocolKind.Serial,
            _ => ProtocolKind.Ssh,
        };
        int port = get("PortNumber") is int p and > 0 ? p : SessionProfile.DefaultPortFor(protocol);
        string keyFile = get("PublicKeyFile") as string ?? ""; // PuTTY's confusing name for the private key path
        bool keyIsPpk = keyFile.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase);

        var profile = new SessionProfile
        {
            Name = DecodePuttyKeyName(encodedName),
            Folder = "Imported from PuTTY",
            Protocol = protocol,
            Host = host,
            Port = port,
            Username = get("UserName") as string ?? "",
        };
        if (protocol == ProtocolKind.Serial)
        {
            profile.SerialPortName = get("SerialLine") as string ?? "";
            profile.BaudRate = get("SerialSpeed") is int baud and > 0 ? baud : 9600;
        }
        if (protocol == ProtocolKind.Ssh && !string.IsNullOrWhiteSpace(keyFile) && !keyIsPpk)
        {
            profile.AuthMethod = SshAuthMethod.PrivateKey;
            profile.PrivateKeyPath = keyFile;
        }
        if (profile.Validate().Count > 0) return null; // never hand back something this app's own rules would reject

        return new ImportedSession(profile, protocol == ProtocolKind.Ssh && keyIsPpk);
    }

    /// <summary>PuTTY escapes characters that aren't safe in a registry key name (its "mungestr"); this is a
    /// best-effort decode covering the common %XX cases rather than a full re-implementation of that scheme.</summary>
    private static string DecodePuttyKeyName(string encoded)
    {
        try { return Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return encoded; }
    }
}
