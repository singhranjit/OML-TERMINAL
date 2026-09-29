using System.Text;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>Minimal RFC4180-ish CSV for bulk session import/export - migrating a spreadsheet of devices at
/// once, or exporting the current list to one. No external dependency: the schema here is small and fixed
/// enough that a hand-rolled reader/writer is simpler than pulling in a CSV library.</summary>
public static class SessionCsv
{
    private static readonly string[] Columns =
        ["Name", "Folder", "Tags", "Protocol", "Host", "Port", "Username", "Password", "AuthMethod", "PrivateKeyPath"];

    public static string Export(IEnumerable<SessionProfile> sessions, bool includeSecrets)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns)).Append("\r\n");
        foreach (var s in sessions)
        {
            string[] fields =
            [
                s.Name, s.Folder, s.Tags, s.Protocol.ToString(), s.Host, s.Port.ToString(),
                s.Username, includeSecrets ? s.Password : "", s.AuthMethod.ToString(),
                s.AuthMethod == SshAuthMethod.PrivateKey ? s.PrivateKeyPath : "",
            ];
            sb.Append(string.Join(",", fields.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Never throws on malformed input - a row that doesn't parse into a valid SessionProfile is
    /// silently skipped, same policy as JsonSessionStore.Load() uses for corrupt entries.</summary>
    public static List<SessionProfile> Import(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<SessionProfile>();
        if (lines.Length == 0) return result;

        var header = ParseLine(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            var values = ParseLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Count && c < values.Count; c++) row[header[c].Trim()] = values[c];

            string Get(string key) => row.TryGetValue(key, out var v) ? v : "";
            var protocol = Enum.TryParse<ProtocolKind>(Get("Protocol"), ignoreCase: true, out var p) ? p : ProtocolKind.Ssh;
            var profile = new SessionProfile
            {
                Name = Get("Name"),
                Folder = Get("Folder"),
                Tags = Get("Tags"),
                Protocol = protocol,
                Host = Get("Host"),
                Port = int.TryParse(Get("Port"), out var port) && port > 0 ? port : SessionProfile.DefaultPortFor(protocol),
                Username = Get("Username"),
                Password = Get("Password"),
                AuthMethod = Enum.TryParse<SshAuthMethod>(Get("AuthMethod"), ignoreCase: true, out var auth) ? auth : SshAuthMethod.Password,
                PrivateKeyPath = Get("PrivateKeyPath"),
            };
            if (profile.Validate().Count > 0) continue;
            result.Add(profile);
        }
        return result;
    }

    private static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    private static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
