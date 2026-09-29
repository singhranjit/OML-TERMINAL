using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Maps one spoken/typed phrase to the exact CLI syntax for a vendor. Deliberately conservative: a phrase this
/// grammar doesn't recognize is passed through unchanged (trimmed) rather than dropped or guessed at, since the
/// most common case - the user simply says the literal command ("show ip interface brief") - needs no translation
/// at all. Only shorthands ("go to interface gi0/0", CIDR addresses) are expanded.
/// </summary>
public interface IVendorGrammar
{
    string Name { get; }
    string Translate(string phrase);

    /// <summary>
    /// Whether a translated line starts like a real command for this platform. Hands-free mode only sends lines that
    /// pass, so misheard speech ("are there enough") is never typed into a device on its own.
    /// </summary>
    bool LooksLikeCli(string line) => VoiceCli.Generic().IsMatch(line.Trim());
}

public static partial class VoiceCli
{
    [GeneratedRegex(@"^(?:show|ping|traceroute|trace|interface|int|ip|ipv6|no|vlan|switchport|hostname|description|configure|conf|enable|disable|exit|end|write|wr|copy|router|network|neighbor|access-list|spanning-tree|channel-group|name|shutdown|do|clear|terminal|logging|ntp|snmp-server|username|line|password|login|transport|service|banner|crypto|aaa|tacacs-server|radius-server|standby|vrrp|speed|duplex|mtu|encapsulation|reload|debug|undebug|set|delete|commit|edit|run|request|monitor|rollback|get|execute|diagnose|config|next|unset|append|purge|test|less|tail|nameif|security-level|route|object|object-group|nat|access-group|mode|feature|vpc|default)\b", RegexOptions.IgnoreCase)]
    public static partial Regex Generic();

    [GeneratedRegex(@"^(?:show|ping|traceroute|trace|interface|int|ip|ipv6|no|vlan|switchport|hostname|description|configure|conf|enable|disable|exit|end|write|wr|copy|router|network|neighbor|access-list|spanning-tree|channel-group|name|shutdown|do|clear|terminal|logging|ntp|snmp-server|username|line|password|login|transport|service|banner|crypto|aaa|tacacs-server|radius-server|standby|vrrp|speed|duplex|mtu|encapsulation|reload|debug|undebug|nameif|security-level|route|object|object-group|nat|access-group|mode|feature|vpc|default|dir|more|verify|archive|boot|clock|cdp|lldp|errdisable|storm-control|policy-map|class-map|service-policy|power|udld|vtp|track|key|area|redistribute|passive-interface|version|auto-summary|mac)\b", RegexOptions.IgnoreCase)]
    public static partial Regex Cisco();

    [GeneratedRegex(@"^(?:show|set|delete|commit|edit|run|request|monitor|rollback|configure|exit|up|top|ping|traceroute|clear|restart|file|help|load|save|annotate|activate|deactivate|rename|insert|copy|replace|status|quit)\b", RegexOptions.IgnoreCase)]
    public static partial Regex Junos();

    [GeneratedRegex(@"^(?:config|edit|set|unset|next|end|show|get|execute|diagnose|append|select|unselect|delete|purge|rename|clone|move|abort|exit)\b", RegexOptions.IgnoreCase)]
    public static partial Regex FortiOs();

    [GeneratedRegex(@"^(?:show|set|configure|commit|delete|edit|exit|request|test|debug|ping|traceroute|run|less|tail|clear|top|up|rename|move|copy|save|load|revert|validate|quit)\b", RegexOptions.IgnoreCase)]
    public static partial Regex PanOs();
}
