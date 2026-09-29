namespace OmlTerminal.Core.Copilot;

/// <summary>Decides whether a command the copilot wants to run needs the operator's approval first. Default-deny:
/// anything not recognized as a read-only/diagnostic command is treated as risky, so new or unusual commands always
/// stop for a human rather than silently being assumed safe.
/// A prefix match alone is not enough - "ls; reboot" or "echo x &gt; /etc/passwd" would otherwise inherit the
/// safety of "ls"/"echo ". Any shell metacharacter that can chain, redirect or substitute commands forces the
/// risky path regardless of what the command starts with.</summary>
public static class CommandRisk
{
    private static readonly char[] ShellMetacharacters = ['|', ';', '&', '`', '$', '>', '<', '(', ')', '\n', '\r'];

    private static readonly string[] SafePrefixes =
    [
        "show ", "show", "display ", "get ", "ping", "traceroute", "tracert", "nslookup", "dig ",
        "ls", "ls ", "cat ", "pwd", "whoami", "uptime", "ifconfig", "ip addr", "ip route", "ip link",
        "netstat", "ss ", "top", "ps ", "ps", "df ", "free", "uname", "grep ", "find ", "head ", "tail ",
        "less ", "more ", "history", "version", "status", "monitor ", "diagnose sys", "diagnose debug",
        "sho ", "wc ", "echo ", "hostname", "vmstat", "iostat", "arp", "route print",
    ];

    public static bool IsRisky(string command)
    {
        var c = (command ?? "").TrimStart().ToLowerInvariant();
        if (c.Length == 0) return true;
        if (c.IndexOfAny(ShellMetacharacters) >= 0) return true;
        return !SafePrefixes.Any(p => c.StartsWith(p, StringComparison.Ordinal));
    }
}
