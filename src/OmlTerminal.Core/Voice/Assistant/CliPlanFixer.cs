using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice.Assistant;

/// <summary>
/// The CLI-mode know-how an engineer applies without thinking, applied to whatever the model proposed so a small
/// model's slip never reaches the device:
///  - Cisco-style: configuration lines from EXEC mode get "configure terminal" before them and "end" after;
///    show/EXEC commands issued inside configuration mode get "do " on IOS.
///  - Junos: set/delete/edit lines from operational mode get "configure" before and "commit and-quit" after;
///    show commands inside configuration mode get "run ".
/// Other platforms pass through unchanged.
/// </summary>
public static partial class CliPlanFixer
{
    public static IReadOnlyList<VoiceAction> Fix(IReadOnlyList<VoiceAction> actions, ScreenState screen) => screen.Platform switch
    {
        DevicePlatform.CiscoIos or DevicePlatform.AristaEos or DevicePlatform.CiscoNxos or DevicePlatform.CiscoAsa => FixCisco(actions, screen),
        DevicePlatform.Junos => FixJunos(actions, screen),
        _ => actions,
    };

    private static List<VoiceAction> FixCisco(IReadOnlyList<VoiceAction> actions, ScreenState screen)
    {
        var result = new List<VoiceAction>();
        bool inConfig = screen.InConfigMode, enteredHere = false;
        // IOS needs "do" for EXEC commands inside config mode; NX-OS, ASA and EOS accept show there directly.
        bool needsDo = screen.Platform == DevicePlatform.CiscoIos;
        foreach (var a in actions)
        {
            if (a.Kind != VoiceActionKind.Run) { result.Add(a); continue; }
            var cmd = a.Value.Trim();
            if (EnterConfig().IsMatch(cmd))
            {
                if (inConfig) continue; // already there - a second "configure terminal" is an error on some platforms
                inConfig = true;
                result.Add(a with { Value = "configure terminal" });
                continue;
            }
            if (Regex.IsMatch(cmd, @"^end$", RegexOptions.IgnoreCase))
            {
                if (!inConfig) continue;
                inConfig = false;
                result.Add(a);
                continue;
            }
            bool isExec = ExecCommand().IsMatch(cmd) || DoPrefixed().IsMatch(cmd);
            if (isExec)
            {
                if (inConfig && needsDo && !DoPrefixed().IsMatch(cmd)) result.Add(a with { Value = "do " + cmd });
                else if (!inConfig && DoPrefixed().IsMatch(cmd)) result.Add(a with { Value = cmd[3..].TrimStart() });
                else result.Add(a);
                continue;
            }
            // A configuration line.
            if (!inConfig)
            {
                result.Add(new VoiceAction(VoiceActionKind.Run, "configure terminal"));
                inConfig = true;
                enteredHere = true;
            }
            result.Add(a);
        }
        if (enteredHere && inConfig && !screen.InConfigMode) result.Add(new VoiceAction(VoiceActionKind.Run, "end"));
        return result;
    }

    private static List<VoiceAction> FixJunos(IReadOnlyList<VoiceAction> actions, ScreenState screen)
    {
        var result = new List<VoiceAction>();
        bool inConfig = screen.InConfigMode, enteredHere = false, changed = false;
        foreach (var a in actions)
        {
            if (a.Kind != VoiceActionKind.Run) { result.Add(a); continue; }
            var cmd = a.Value.Trim();
            if (Regex.IsMatch(cmd, @"^(?:configure|edit)(?:\s+(?:private|exclusive))?$", RegexOptions.IgnoreCase) && cmd.StartsWith("configure", StringComparison.OrdinalIgnoreCase))
            {
                if (inConfig) continue;
                inConfig = true;
                result.Add(a);
                continue;
            }
            if (Regex.IsMatch(cmd, @"^(?:exit|quit)(?:\s+configuration-mode)?$|^commit\s+and-quit$", RegexOptions.IgnoreCase))
            {
                inConfig = false;
                result.Add(a);
                continue;
            }
            bool isConfig = JunosConfig().IsMatch(cmd);
            if (isConfig && !inConfig)
            {
                result.Add(new VoiceAction(VoiceActionKind.Run, "configure"));
                inConfig = true;
                enteredHere = true;
            }
            if (isConfig) changed |= !cmd.StartsWith("commit", StringComparison.OrdinalIgnoreCase);
            if (cmd.StartsWith("commit", StringComparison.OrdinalIgnoreCase)) changed = false;
            if (!isConfig && inConfig && cmd.StartsWith("show ", StringComparison.OrdinalIgnoreCase)) { result.Add(a with { Value = "run " + cmd }); continue; }
            result.Add(a);
        }
        if (enteredHere && inConfig) result.Add(new VoiceAction(VoiceActionKind.Run, changed ? "commit and-quit" : "exit"));
        return result;
    }

    [GeneratedRegex(@"^(?:configure(?:\s+terminal)?|conf(?:\s+t(?:erminal)?)?|config\s+t)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnterConfig();

    [GeneratedRegex(@"^do\s+", RegexOptions.IgnoreCase)]
    private static partial Regex DoPrefixed();

    // EXEC-mode commands (run from the # prompt, not inside configuration).
    [GeneratedRegex(@"^(?:show|ping|traceroute|clear|copy|write|reload|debug|undebug|no\s+debug|delete|erase|mkdir|rmdir|rename|archive|verify|test|send|terminal|clock\s+set|license|install|request|disable|enable|logout|dir|more|squeeze|undelete|telnet|ssh|connect|monitor\s+capture|packet-tracer|capture|failover\s+(?:active|reload-standby)|show-tech)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecCommand();

    [GeneratedRegex(@"^(?:set|delete|edit|activate|deactivate|rename|insert|annotate|copy|replace|rollback|commit|top|up|wildcard|protect|unprotect|load)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JunosConfig();
}
