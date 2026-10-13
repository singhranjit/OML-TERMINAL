using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice.Assistant;

public enum DevicePlatform
{
    Unknown,
    CiscoIos,      // IOS / IOS-XE and the many look-alikes (Arista and Dell OS10 prompts read the same)
    CiscoNxos,
    CiscoAsa,
    AristaEos,
    Junos,
    FortiOs,
    PanOs,
    HuaweiVrp,
    MikroTik,
    LinuxShell,
    WindowsShell,
}

/// <summary>What the engineer is looking at: platform, the current prompt and the CLI mode it implies.</summary>
public sealed record ScreenState(DevicePlatform Platform, string Prompt, string Mode, bool InConfigMode)
{
    public string PlatformName => Platform switch
    {
        DevicePlatform.CiscoIos => "Cisco IOS / IOS-XE style CLI",
        DevicePlatform.CiscoNxos => "Cisco NX-OS",
        DevicePlatform.CiscoAsa => "Cisco ASA",
        DevicePlatform.AristaEos => "Arista EOS",
        DevicePlatform.Junos => "Juniper Junos",
        DevicePlatform.FortiOs => "Fortinet FortiOS",
        DevicePlatform.PanOs => "Palo Alto PAN-OS",
        DevicePlatform.HuaweiVrp => "Huawei VRP",
        DevicePlatform.MikroTik => "MikroTik RouterOS",
        DevicePlatform.LinuxShell => "Linux/Unix shell",
        DevicePlatform.WindowsShell => "Windows shell",
        _ => "unknown - infer it from the screen",
    };
}

/// <summary>
/// Reads the terminal screen the way an engineer glancing at it would: the last prompt says which device family this
/// is and which mode the CLI is in ("SW1(config-if)#" = Cisco-style, interface configuration), and banners or earlier
/// output ("Cisco Nexus", "FortiGate") break ties between look-alike prompts. Pure text logic, so it is unit tested.
/// </summary>
public static partial class ScreenContext
{
    public static ScreenState Analyze(string screenText)
    {
        var lines = (screenText ?? "").Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var prompt = lines.Count > 0 ? lines[^1] : "";
        var hints = string.Join('\n', lines.TakeLast(400));

        // Junos: user@host> / user@host#, with an "[edit ...]" banner above the prompt in configuration mode.
        if (JunosPrompt().IsMatch(prompt) && !hints.Contains("PAN-OS", StringComparison.OrdinalIgnoreCase) && !PanHint().IsMatch(hints))
        {
            bool cfg = prompt.EndsWith('#');
            var edit = lines.Count > 1 && lines[^2].StartsWith("[edit") ? lines[^2] : cfg ? "[edit]" : "";
            return new ScreenState(DevicePlatform.Junos, prompt, cfg ? $"configuration mode {edit}".Trim() : "operational mode", cfg);
        }
        if (JunosPrompt().IsMatch(prompt))
        {
            bool cfg = prompt.EndsWith('#');
            return new ScreenState(DevicePlatform.PanOs, prompt, cfg ? "configuration mode" : "operational mode", cfg);
        }
        // MikroTik: [admin@router] /ip address>
        if (MikroTikPrompt().Match(prompt) is { Success: true } mt)
            return new ScreenState(DevicePlatform.MikroTik, prompt, mt.Groups[1].Value.Trim() is { Length: > 0 } path ? $"menu {path}" : "root menu", false);
        // Huawei: <HUAWEI> user view, [HUAWEI] / [~HUAWEI-GigabitEthernet0/0/1] system view.
        if (HuaweiUser().IsMatch(prompt)) return new ScreenState(DevicePlatform.HuaweiVrp, prompt, "user view", false);
        if (HuaweiSystem().Match(prompt) is { Success: true } hw)
            return new ScreenState(DevicePlatform.HuaweiVrp, prompt, hw.Groups[1].Value.Contains('-') ? $"system view, in {hw.Groups[1].Value[(hw.Groups[1].Value.IndexOf('-') + 1)..]}" : "system view", true);
        // FortiOS: "FGT-01 # " or "FGT-01 (interface) # " / "FGT-01 (port1) # ".
        if (FortiPrompt().Match(prompt) is { Success: true } fg && (fg.Groups[1].Success || FortiHint().IsMatch(hints)))
            return new ScreenState(DevicePlatform.FortiOs, prompt, fg.Groups[1].Success ? $"inside config ({fg.Groups[1].Value})" : "top level", fg.Groups[1].Success);
        // Windows: PS C:\> or C:\Users\x>
        if (WindowsPrompt().IsMatch(prompt)) return new ScreenState(DevicePlatform.WindowsShell, prompt, "shell", false);
        // Cisco-style: host> host# host(config)# host(config-if)#
        if (CiscoPrompt().Match(prompt) is { Success: true } c)
        {
            var platform = NxosHint().IsMatch(hints) ? DevicePlatform.CiscoNxos
                : AsaHint().IsMatch(hints) ? DevicePlatform.CiscoAsa
                : AristaHint().IsMatch(hints) ? DevicePlatform.AristaEos
                : DevicePlatform.CiscoIos;
            var sub = c.Groups[2].Success ? c.Groups[2].Value : "";
            string mode = sub.Length == 0
                ? (c.Groups[3].Value == "#" ? "privileged EXEC" : "user EXEC (not enabled)")
                : sub switch
                {
                    "config" => "global configuration",
                    "config-if" => "interface configuration",
                    "config-subif" => "sub-interface configuration",
                    "config-if-range" => "interface-range configuration",
                    "config-vlan" => "VLAN configuration",
                    "config-line" => "line configuration",
                    "config-router" => "router configuration",
                    "config-ext-nacl" or "config-std-nacl" => "access-list configuration",
                    _ => $"configuration ({sub})",
                };
            return new ScreenState(platform, prompt, mode, sub.Length > 0);
        }
        // Unix shells: user@host:~$ / root@host:/etc# / bash-5.1$ / plain $ or #
        if (UnixPrompt().IsMatch(prompt)) return new ScreenState(DevicePlatform.LinuxShell, prompt, prompt.EndsWith('#') ? "root shell" : "shell", false);

        return new ScreenState(DevicePlatform.Unknown, prompt, "unknown", false);
    }

    [GeneratedRegex(@"^[\w.\-]+@[\w.\-]+[>#]\s?$")]
    private static partial Regex JunosPrompt();

    [GeneratedRegex(@"\b(?:PA-\d{3,4}|PA-VM|Panorama|panos)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PanHint();

    [GeneratedRegex(@"^\[[\w.\-]+@[\w.\-]+\]\s*([/\w\s-]*)>\s?$")]
    private static partial Regex MikroTikPrompt();

    [GeneratedRegex(@"^<[\w.\-~]+>$")]
    private static partial Regex HuaweiUser();

    [GeneratedRegex(@"^\[~?\*?([\w.\-/:]+)\]$")]
    private static partial Regex HuaweiSystem();

    [GeneratedRegex(@"^[\w.\-]+ (?:\(([\w.\-]+)\) )?[#$] ?$")]
    private static partial Regex FortiPrompt();

    [GeneratedRegex(@"FortiGate|FortiOS|FGT|config system|FortiAnalyzer", RegexOptions.IgnoreCase)]
    private static partial Regex FortiHint();

    [GeneratedRegex(@"^(?:PS )?[A-Za-z]:\\[^>]*>\s?$")]
    private static partial Regex WindowsPrompt();

    [GeneratedRegex(@"^([\w.\-/]+)(?:\((config[\w.\-]*)\))?([#>])\s?$")]
    private static partial Regex CiscoPrompt();

    [GeneratedRegex(@"NX-OS|Nexus|\bNXOS\b", RegexOptions.IgnoreCase)]
    private static partial Regex NxosHint();

    [GeneratedRegex(@"Adaptive Security Appliance|\bASA\b|ciscoasa", RegexOptions.IgnoreCase)]
    private static partial Regex AsaHint();

    [GeneratedRegex(@"Arista|\bEOS\b|vEOS|cEOS", RegexOptions.IgnoreCase)]
    private static partial Regex AristaHint();

    [GeneratedRegex(@"(?:^[\w.\-]+@[\w.\-]+:[^\s]*[$#]\s?$)|(?:^[\w.\-]+-\d[\d.]*[$#]\s?$)|(?:^[$#]\s?$)|(?:^\[[\w.\-]+@[\w.\-]+ [^\]]+\][$#]\s?$)")]
    private static partial Regex UnixPrompt();
}
