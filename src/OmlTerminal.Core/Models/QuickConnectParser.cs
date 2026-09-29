namespace OmlTerminal.Core.Models;

/// <summary>Parses quick-connect strings such as "ssh admin@10.0.0.1:2222", "telnet 10.0.0.1 2001", "admin@sw1" or "sw1".</summary>
public static class QuickConnectParser
{
    public static SessionProfile? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = input.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

        var protocol = ProtocolKind.Ssh;
        if (parts[0].Equals("ssh", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        else if (parts[0].Equals("telnet", StringComparison.OrdinalIgnoreCase)) { protocol = ProtocolKind.Telnet; parts.RemoveAt(0); }
        if (parts.Count == 0) return null;

        string target = parts[0];
        int? port = null;
        if (parts.Count >= 2 && int.TryParse(parts[1], out var p2)) port = p2;

        string user = "";
        int at = target.LastIndexOf('@');
        if (at >= 0) { user = target[..at]; target = target[(at + 1)..]; }

        int colon = target.LastIndexOf(':');
        if (colon > 0 && target.Count(c => c == ':') == 1 && int.TryParse(target[(colon + 1)..], out var p1))
        {
            port = p1;
            target = target[..colon];
        }
        if (target.Length == 0) return null;

        var profile = new SessionProfile
        {
            Name = target,
            Protocol = protocol,
            Host = target,
            Username = user,
            Port = port ?? SessionProfile.DefaultPortFor(protocol),
        };
        return profile.Validate().Count == 0 ? profile : null;
    }
}
