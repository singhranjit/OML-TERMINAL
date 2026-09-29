using System.Security.Cryptography;
using System.Text;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>
/// Imports saved sessions from SecureCRT's Config\Sessions folder. Each session is an <c>.ini</c> file of
/// <c>S:"Key"=value</c> / <c>D:"Key"=hex</c> / <c>Z:"Key"=value</c> lines. The folder tree under Sessions becomes the
/// session folder. Passwords stored in the modern "Password V2" (AES-256) format are decrypted when no SecureCRT
/// Config Passphrase is set, or when the passphrase is supplied; the decrypt is self-validating, so a wrong result is
/// never imported. The older format is left for the user to re-enter.
/// </summary>
public static class SecureCrtImporter
{
    /// <param name="configPassphrase">The SecureCRT "Configuration Passphrase", if one is set (blank otherwise).</param>
    public static List<ImportResult> ParseSession(string iniText, string folder, string configPassphrase = "")
    {
        var fields = ParseFields(iniText);
        string Get(params string[] keys) => keys.Select(k => fields.GetValueOrDefault(k)).FirstOrDefault(v => v is not null) ?? "";

        var protoName = Get("Protocol Name").ToUpperInvariant();
        var protocol = protoName switch
        {
            "SSH2" or "SSH1" => ProtocolKind.Ssh,
            "TELNET" => ProtocolKind.Telnet,
            "SERIAL" => ProtocolKind.Serial,
            "RDP" => ProtocolKind.Rdp,
            _ => ProtocolKind.Ssh,
        };

        var profile = new SessionProfile
        {
            Name = Get("_session_name"),
            Folder = folder,
            Protocol = protocol,
            Username = Get("Username"),
            Port = SessionProfile.DefaultPortFor(protocol),
        };
        if (protocol == ProtocolKind.Serial)
        {
            profile.SerialPortName = Get("Serial Port", "Port");
            if (int.TryParse(Get("Baud Rate"), out var baud) && baud > 0) profile.BaudRate = baud;
        }
        else
        {
            profile.Host = Get("Hostname", "Server");
            if (ParseHexInt(Get($"[{protoName}] Port", "Port", "[SSH2] Port", "[SSH1] Port")) is { } port and > 0 and <= 65535)
                profile.Port = port;
            if (string.IsNullOrWhiteSpace(profile.Host)) return [];
        }

        string? note = null;
        var pwValue = Get("Password V2");
        if (pwValue.StartsWith("02:"))
        {
            if (TryDecryptV2(pwValue[3..], configPassphrase, out var password)) profile.Password = password;
            else note = "password couldn't be decrypted (Config Passphrase needed?) - re-enter it";
        }
        else if (fields.ContainsKey("Password") || pwValue.Length > 0)
            note = "password uses SecureCRT's older format - re-enter it or use the Password Manager";

        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = protocol == ProtocolKind.Serial ? profile.SerialPortName : profile.Host;
        return [new ImportResult(profile, note)];
    }

    /// <summary>Scans a SecureCRT Config\Sessions directory tree for session .ini files.</summary>
    public static List<ImportResult> ScanDirectory(string sessionsDir, string configPassphrase = "")
    {
        var results = new List<ImportResult>();
        if (!Directory.Exists(sessionsDir)) return results;
        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.ini", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals("__FolderData__", StringComparison.OrdinalIgnoreCase)) continue;
            var relDir = Path.GetRelativePath(sessionsDir, Path.GetDirectoryName(file)!).Replace('\\', '/').Trim('.', '/');
            var folder = "SecureCRT" + (relDir.Length > 0 ? "/" + relDir : "");
            try
            {
                var text = $"S:\"_session_name\"={name}\n" + File.ReadAllText(file);
                results.AddRange(ParseSession(text, folder, configPassphrase));
            }
            catch { /* skip unreadable session */ }
        }
        return results;
    }

    /// <summary>Parses the <c>S:/D:/Z:"Key"=value</c> lines into a flat map (the "Key" without quotes).</summary>
    public static Dictionary<string, string> ParseFields(string iniText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in iniText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 4 || line[1] != ':' || line[2] != '"') continue;
            int endQuote = line.IndexOf('"', 3);
            if (endQuote < 0) continue;
            var key = line[3..endQuote];
            int eq = line.IndexOf('=', endQuote);
            if (eq < 0) continue;
            map[key] = line[(eq + 1)..];
        }
        return map;
    }

    private static int? ParseHexInt(string hex) =>
        int.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;

    /// <summary>
    /// SecureCRT "Password V2": AES-256-CBC with key = SHA-256(config passphrase), zero IV. The plaintext is a
    /// 4-byte little-endian length, the UTF-8 password, four zero bytes, then a SHA-256 of the password as an
    /// integrity check. That structure lets us reject a wrong key instead of returning garbage.
    /// </summary>
    public static bool TryDecryptV2(string hex, string configPassphrase, out string password)
    {
        password = "";
        byte[] cipher;
        try { cipher = Convert.FromHexString(hex); }
        catch (FormatException) { return false; }
        if (cipher.Length == 0 || cipher.Length % 16 != 0) return false;

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(configPassphrase));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        byte[] plain;
        try { plain = aes.DecryptCbc(cipher, aes.IV, PaddingMode.None); }
        catch (CryptographicException) { return false; }

        if (plain.Length < 8) return false;
        int len = BinaryPrimitives_ReadInt32LittleEndian(plain);
        if (len < 0 || len + 8 > plain.Length) return false;

        var pw = plain.AsSpan(4, len).ToArray();
        var expected = SHA256.HashData(pw);
        // Integrity: SHA-256(password) follows the password, either immediately or after a 4-byte terminator
        // (the layout varies by SecureCRT version). A match confirms the passphrase and rules out garbage.
        bool valid = (4 + len + 32 <= plain.Length && plain.AsSpan(4 + len, 32).SequenceEqual(expected))
                  || (4 + len + 4 + 32 <= plain.Length && plain.AsSpan(4 + len + 4, 32).SequenceEqual(expected));
        if (!valid) return false;
        password = Encoding.UTF8.GetString(pw);
        return true;
    }

    private static int BinaryPrimitives_ReadInt32LittleEndian(byte[] b) => b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24;
}
