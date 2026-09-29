using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>
/// Imports saved sessions from MobaXterm's plaintext configuration: the <c>[Bookmarks]</c> sections of
/// <c>MobaXterm.ini</c>, or a <c>.mxtsessions</c> export. Each bookmark line is
/// <c>Name= #&lt;icon&gt;#&lt;type&gt;%host%port%user%...</c>; the section's <c>SubRep</c> value is the folder path.
/// Passwords aren't imported: MobaXterm keeps them encrypted (registry or an AES-encrypted <c>.mobaconf</c>), not in
/// this plaintext file - link a Password Manager credential instead.
/// </summary>
public static class MobaXtermImporter
{
    /// <summary>MobaXterm session type codes → our protocol. Unlisted types (Shell, Browser, S3...) are skipped.</summary>
    private static ProtocolKind? Protocol(int type) => type switch
    {
        0 => ProtocolKind.Ssh,
        1 => ProtocolKind.Telnet,
        4 => ProtocolKind.Rdp,
        5 => ProtocolKind.Vnc,
        7 => ProtocolKind.Sftp,
        8 => ProtocolKind.Serial,
        _ => null,
    };

    /// <summary>True for MobaXterm's encrypted export - which this importer can't read (needs the master password).</summary>
    public static bool IsEncryptedExport(string content) => content.TrimStart().StartsWith("_@");

    public static List<ImportResult> Parse(string iniContent)
    {
        if (IsEncryptedExport(iniContent))
            throw new ImportException(
                "This is an encrypted MobaXterm export (.mobaconf). Export again with \"Common settings\" and no master " +
                "password, or point me at your plaintext MobaXterm.ini (Settings > Configuration > shows its path).");

        var results = new List<ImportResult>();
        string folder = "Imported from MobaXterm";
        bool inBookmarks = false;

        foreach (var rawLine in iniContent.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith('['))
            {
                inBookmarks = line.StartsWith("[Bookmarks", StringComparison.OrdinalIgnoreCase);
                folder = "Imported from MobaXterm";
                continue;
            }
            if (!inBookmarks || line.Length == 0) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];

            if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Trim().Length > 0) folder = "MobaXterm/" + value.Trim().Replace('\\', '/');
                continue;
            }
            if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase)) continue;

            if (Parse(key, value, folder) is { } imported) results.Add(imported);
        }
        return results;
    }

    /// <summary>Parses one bookmark: <c>Name= #icon#type%host%port%user%...</c>.</summary>
    public static ImportResult? Parse(string name, string value, string folder)
    {
        var hashParts = value.Split('#');
        if (hashParts.Length < 3) return null;                 // need at least " #icon#typedata"
        var payload = hashParts[2];                            // "type%host%port%user%..."
        var fields = payload.Split('%');
        if (fields.Length < 2 || !int.TryParse(fields[0], out var type)) return null;
        if (Protocol(type) is not { } protocol) return null;

        var profile = new SessionProfile
        {
            Name = name.Trim(),
            Folder = folder,
            Protocol = protocol,
            Port = SessionProfile.DefaultPortFor(protocol),
        };

        if (protocol == ProtocolKind.Serial)
        {
            profile.SerialPortName = Field(fields, 1);
            profile.BaudRate = int.TryParse(Field(fields, 2), out var baud) && baud > 0 ? baud : 9600;
        }
        else
        {
            profile.Host = Field(fields, 1);
            if (int.TryParse(Field(fields, 2), out var port) && port is > 0 and <= 65535) profile.Port = port;
            profile.Username = Field(fields, 3);
            if (string.IsNullOrWhiteSpace(profile.Host)) return null;
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = protocol == ProtocolKind.Serial ? profile.SerialPortName : profile.Host;

        return new ImportResult(profile);
    }

    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index].Trim() : "";
}
