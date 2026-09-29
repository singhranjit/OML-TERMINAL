using Microsoft.Win32;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Persistence;

/// <summary>Reads existing PuTTY saved sessions from the registry (read-only - this never writes to PuTTY's
/// own config) so someone migrating from PuTTY doesn't have to re-type every saved device by hand. The
/// dedicated "OML-Terminal-Legacy" session this app itself creates (see PuttyLegacyProfile) is skipped since
/// it's internal plumbing, not a real user session.</summary>
public static class PuttySessionImporter
{
    private const string SessionsRoot = @"Software\SimonTatham\PuTTY\Sessions";

    /// <param name="KeyNeedsConversion">True when the session has a private-key file but it's PuTTY's own
    /// .ppk format, which SSH.NET cannot read directly - the profile is still imported (with password auth)
    /// so the rest of it isn't lost, but the key won't work until re-exported as OpenSSH format via PuTTYgen.</param>
    public sealed record ImportedSession(SessionProfile Profile, bool KeyNeedsConversion);

    public static List<ImportedSession> FindSessions()
    {
        var results = new List<ImportedSession>();
        if (!OperatingSystem.IsWindows()) return results;
        using var root = Registry.CurrentUser.OpenSubKey(SessionsRoot);
        if (root is null) return results;

        foreach (var encodedName in root.GetSubKeyNames())
        {
            if (encodedName == PuttyLegacyProfile.SessionName) continue;
            using var key = root.OpenSubKey(encodedName);
            if (key is null) continue;

            string host = key.GetValue("HostName") as string ?? "";

            var protocol = (key.GetValue("Protocol") as string) switch
            {
                "ssh" => ProtocolKind.Ssh,
                "telnet" or "rlogin" or "raw" => ProtocolKind.Telnet,
                "serial" => ProtocolKind.Serial,
                _ => ProtocolKind.Ssh,
            };
            int port = key.GetValue("PortNumber") is int p and > 0 ? p : SessionProfile.DefaultPortFor(protocol);
            string keyFile = key.GetValue("PublicKeyFile") as string ?? ""; // PuTTY's confusing name for the private key path
            bool keyIsPpk = keyFile.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase);

            var profile = new SessionProfile
            {
                Name = DecodePuttyKeyName(encodedName),
                Folder = "Imported from PuTTY",
                Protocol = protocol,
                Host = host,
                Port = port,
                Username = key.GetValue("UserName") as string ?? "",
            };
            if (protocol == ProtocolKind.Serial)
            {
                profile.SerialPortName = key.GetValue("SerialLine") as string ?? "";
                profile.BaudRate = key.GetValue("SerialSpeed") is int baud and > 0 ? baud : 9600;
            }
            if (protocol == ProtocolKind.Ssh && !string.IsNullOrWhiteSpace(keyFile) && !keyIsPpk)
            {
                profile.AuthMethod = SshAuthMethod.PrivateKey;
                profile.PrivateKeyPath = keyFile;
            }
            if (profile.Validate().Count > 0) continue; // never hand back something this app's own rules would reject

            results.Add(new ImportedSession(profile, protocol == ProtocolKind.Ssh && keyIsPpk));
        }
        return results.OrderBy(r => r.Profile.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>PuTTY escapes characters that aren't safe in a registry key name (its "mungestr"); this is a
    /// best-effort decode covering the common %XX cases rather than a full re-implementation of that scheme.</summary>
    private static string DecodePuttyKeyName(string encoded)
    {
        try { return Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return encoded; }
    }
}
