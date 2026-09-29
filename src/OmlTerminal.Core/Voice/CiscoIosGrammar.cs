using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Cisco IOS / IOS-XE grammar: maps natural phrases ("bring it up", "put it in vlan 10", "add a default route via
/// 10.0.0.254") to exact CLI. Anything it doesn't recognize passes through unchanged, so saying the literal command
/// always works. Also the base for the NX-OS, ASA and Arista grammars, which share most of this syntax.
/// </summary>
public partial class CiscoIosGrammar : IVendorGrammar
{
    public virtual string Name => "Cisco IOS";

    private const string Ip = @"\d{1,3}(?:\.\d{1,3}){3}";

    private static readonly (Regex Pattern, Func<Match, CiscoIosGrammar, string> Build)[] Rules =
    [
        // Navigation: "go to X" / "enter X" / "switch to X" just drop the verb - X is translated in turn.
        (GoTo(), (m, g) => g.Translate(m.Groups["rest"].Value)),
        (EnableMode(), (_, _) => "enable"),
        (ConfigMode(), (_, _) => "configure terminal"),
        (EndMode(), (_, _) => "end"),
        (ExitMode(), (_, _) => "exit"),

        // Addressing
        (IpCidr(), (m, _) => CidrHelper.MaskFromPrefix(int.Parse(m.Groups["prefix"].Value)) is { } mask
            ? $"ip address {m.Groups["ip"].Value} {mask}" : m.Value.Trim()),
        (IpMask(), (m, _) => $"ip address {m.Groups["ip"].Value} {m.Groups["mask"].Value}"),
        (IpDhcp(), (_, _) => "ip address dhcp"),
        (DefaultRoute(), (m, _) => $"ip route 0.0.0.0 0.0.0.0 {m.Groups["nh"].Value}"),
        (StaticRoute(), (m, _) => CidrHelper.MaskFromPrefix(int.Parse(m.Groups["prefix"].Value)) is { } mask
            ? $"ip route {m.Groups["net"].Value} {mask} {m.Groups["nh"].Value}" : m.Value.Trim()),

        // Interface state
        (NoShutdown(), (_, _) => "no shutdown"),
        (Shutdown(), (_, _) => "shutdown"),

        // Switching
        (CreateVlan(), (m, _) => $"vlan {m.Groups["id"].Value}"),
        (NameIt(), (m, _) => $"name {m.Groups["name"].Value.Trim()}"),
        (AccessMode(), (_, _) => "switchport mode access"),
        (TrunkMode(), (_, _) => "switchport mode trunk"),
        (AccessVlan(), (m, _) => $"switchport access vlan {m.Groups["id"].Value}"),
        (TrunkAllowed(), (m, _) => $"switchport trunk allowed vlan {m.Groups["ids"].Value.Replace(" ", "")}"),

        // Identity / housekeeping
        (SaveConfig(), (_, _) => "write memory"),
        (Hostname(), (m, _) => $"hostname {m.Groups["name"].Value.Trim()}"),
        (Description(), (m, _) => $"description {m.Groups["text"].Value.Trim()}"),

        // Show commands people say loosely
        (ShowRun(), (_, _) => "show running-config"),
        (ShowIpIntBrief(), (_, _) => "show ip interface brief"),
        (ShowIntStatus(), (_, _) => "show interfaces status"),
        (ShowVlan(), (_, _) => "show vlan brief"),
        (ShowRoute(), (_, _) => "show ip route"),
        (ShowVersion(), (_, _) => "show version"),
        (ShowNeighbors(), (_, _) => "show cdp neighbors"),
    ];

    public virtual bool LooksLikeCli(string line) => VoiceCli.Cisco().IsMatch(line.Trim());

    public virtual string Translate(string phrase)
    {
        phrase = phrase.Trim();
        foreach (var (pattern, build) in Rules)
        {
            var m = pattern.Match(phrase);
            if (m.Success) return build(m, this);
        }
        return phrase;
    }

    [GeneratedRegex(@"^(?:go\s+(?:in)?to|enter|switch\s+to|move\s+to|jump\s+to|open)\s+(?:the\s+)?(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GoTo();

    [GeneratedRegex(@"^(?:enable|privileged(?:\s+exec)?(?:\s+mode)?|enable\s+mode|exec\s+mode|become\s+admin)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnableMode();

    [GeneratedRegex(@"^(?:configure\s+terminal|config(?:uration)?\s+mode|conf\s+t|config\s+t)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigMode();

    [GeneratedRegex(@"^(?:end|(?:exit|leave|quit)\s+(?:config(?:uration)?(?:\s+mode)?|configure\s+terminal)|back\s+to\s+(?:privileged|enable|exec)\s+mode)$", RegexOptions.IgnoreCase)]
    private static partial Regex EndMode();

    [GeneratedRegex(@"^(?:exit|go\s+back|back|leave(?:\s+(?:the\s+)?interface)?|exit\s+(?:the\s+)?interface)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExitMode();

    // "add ip address 10.0.0.1/24", "set the IP to 10.0.0.1/24", "ip address 10.0.0.1/24", "give it ip 10.0.0.1/24"
    [GeneratedRegex(@"^(?:(?:add|set|assign|configure|give(?:\s+it)?|put|apply|use)\s+(?:an?\s+|the\s+)?)?ip(?:\s+address)?\s+(?:of\s+|to\s+|as\s+|=\s*)?(?<ip>" + Ip + @")\s*/\s*(?<prefix>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex IpCidr();

    // "add ip address 10.0.0.1 255.255.255.0", "... with mask 255.255.255.0", "... subnet mask 255.255.255.0"
    [GeneratedRegex(@"^(?:(?:add|set|assign|configure|give(?:\s+it)?|put|apply|use)\s+(?:an?\s+|the\s+)?)?ip(?:\s+address)?\s+(?:of\s+|to\s+|as\s+|=\s*)?(?<ip>" + Ip + @")\s+(?:(?:with\s+)?(?:a\s+|the\s+)?(?:subnet\s+mask|netmask|mask|subnet)\s+)?(?<mask>" + Ip + @")$", RegexOptions.IgnoreCase)]
    private static partial Regex IpMask();

    [GeneratedRegex(@"^(?:(?:set|get|use|configure)\s+(?:the\s+)?)?ip(?:\s+address)?\s+(?:from|via|by|using|to)?\s*dhcp$", RegexOptions.IgnoreCase)]
    private static partial Regex IpDhcp();

    [GeneratedRegex(@"^(?:add\s+(?:a\s+|the\s+)?|set\s+(?:a\s+|the\s+)?|create\s+(?:a\s+)?)?default\s+(?:route|gateway)\s+(?:via\s+|to\s+|through\s+|using\s+|next\s+hop\s+|is\s+)?(?<nh>" + Ip + @")$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultRoute();

    [GeneratedRegex(@"^(?:add\s+(?:a\s+)?|create\s+(?:a\s+)?)?(?:static\s+)?route\s+(?:to\s+|for\s+)?(?<net>" + Ip + @")\s*/\s*(?<prefix>\d{1,2})\s+(?:via\s+|through\s+|next\s+hop\s+|using\s+|to\s+)?(?<nh>" + Ip + @")$", RegexOptions.IgnoreCase)]
    private static partial Regex StaticRoute();

    [GeneratedRegex(@"^(?:no\s+sh(?:u|o|oo)ts?(?:\s*down)?|un\s*shut|bring\s+(?:it|the\s+(?:interface|port|link))?\s*up|enable\s+(?:the\s+|this\s+)?(?:interface|port|link)|turn\s+(?:it|the\s+(?:interface|port))\s+on|activate\s+(?:it|the\s+(?:interface|port)))$", RegexOptions.IgnoreCase)]
    private static partial Regex NoShutdown();

    [GeneratedRegex(@"^(?:shut(?:down)?|shut\s+(?:it\s+)?down|shut\s+down\s+the\s+(?:interface|port|link)|disable\s+(?:the\s+|this\s+)?(?:interface|port|link)|turn\s+(?:it|the\s+(?:interface|port))\s+off|bring\s+(?:it|the\s+(?:interface|port|link))\s+down|deactivate\s+(?:it|the\s+(?:interface|port)))$", RegexOptions.IgnoreCase)]
    private static partial Regex Shutdown();

    [GeneratedRegex(@"^(?:create|add|make|configure|new)\s+(?:a\s+)?vlan\s+(?<id>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex CreateVlan();

    [GeneratedRegex(@"^(?:name\s+it|call\s+it|set\s+(?:the\s+)?(?:vlan\s+)?name\s+(?:to\s+)?|(?:vlan\s+)?name\s+(?:is\s+)?)\s*(?<name>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameIt();

    [GeneratedRegex(@"^(?:switchport\s+mode\s+access|make\s+(?:it|this)\s+(?:an\s+)?access\s+port|set\s+(?:it\s+)?(?:to\s+)?access\s+mode|access\s+mode)$", RegexOptions.IgnoreCase)]
    private static partial Regex AccessMode();

    [GeneratedRegex(@"^(?:switchport\s+mode\s+trunk|make\s+(?:it|this)\s+(?:a\s+)?trunk(?:\s+port)?|set\s+(?:it\s+)?(?:to\s+)?trunk\s+mode|trunk\s+mode)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrunkMode();

    [GeneratedRegex(@"^(?:switchport\s+access\s+vlan|(?:put|place|assign|add|move)\s+(?:it|this(?:\s+port)?|the\s+port)?\s*(?:in|into|to|on)(?:\s+to)?|access)\s+vlan\s+(?<id>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex AccessVlan();

    [GeneratedRegex(@"^(?:switchport\s+trunk\s+)?allow(?:ed)?\s+vlans?\s+(?<ids>[\d,\-\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrunkAllowed();

    [GeneratedRegex(@"^(?:save|save\s+(?:the\s+)?(?:config(?:uration)?|changes|it|running\s+config)|write(?:\s+mem(?:ory)?)?|wr|copy\s+run(?:ning)?(?:[\s-]+config)?\s+start(?:up)?(?:[\s-]+config)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SaveConfig();

    [GeneratedRegex(@"^(?:set|change|rename)\s+(?:the\s+)?(?:host\s*name|device\s+name|switch\s+name|router\s+name)\s+(?:to\s+)?(?<name>\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Hostname();

    [GeneratedRegex(@"^(?:set|add|put)\s+(?:a\s+|the\s+)?description\s+(?:to\s+|of\s+)?(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Description();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+)?(?:run(?:ning)?(?:[\s-]*config(?:uration)?)?|current\s+config(?:uration)?|config(?:uration)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowRun();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+|all\s+)?(?:ip\s+)?(?:int(?:erfaces?)?\s+brief|interface\s+summary|interfaces?\s+ips?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowIpIntBrief();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+)?(?:int(?:erfaces?)?\s+status|port\s+status)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowIntStatus();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+|all\s+)?vlans?(?:\s+brief)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowVlan();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+)?(?:ip\s+)?(?:route|routes|routing\s+table)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowRoute();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+)?(?:version|software\s+version|ios\s+version)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowVersion();

    [GeneratedRegex(@"^show\s+(?:the\s+|me\s+the\s+)?(?:cdp\s+)?neighbou?rs$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowNeighbors();
}
