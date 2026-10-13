using System.Text;
using System.Text.Json;
using OmlTerminal.Core.Copilot;

namespace OmlTerminal.Core.Voice.Assistant;

public enum VoiceActionKind { Run, Connect, Tool }

/// <summary>One thing the assistant wants to do: type a CLI line, open a saved session, or open a tool.</summary>
public sealed record VoiceAction(VoiceActionKind Kind, string Value, string Target = "")
{
    public override string ToString() => Kind switch
    {
        VoiceActionKind.Run => Value,
        VoiceActionKind.Connect => $"open session \"{Value}\"",
        _ => Target.Length > 0 ? $"open {Value} for {Target}" : $"open {Value}",
    };
}

public enum VoiceNext { Done, Read, Ask }

/// <summary>The model's answer to one turn: what to say, what to do, and whether it needs to read the results.</summary>
public sealed record VoicePlan(string Say, IReadOnlyList<VoiceAction> Actions, VoiceNext Next);

/// <summary>Everything the model is told about the situation - the same picture the engineer has on screen.</summary>
public sealed record VoiceSituation(
    string? DeviceName,
    string? DeviceHost,
    string? Protocol,
    ScreenState Screen,
    string ScreenTail,
    IReadOnlyList<string> SessionNames,
    IReadOnlyList<(string Id, string Title)> Tools,
    string? PlatformOverride = null);

/// <summary>
/// The conversation contract with the language model. The model answers in a few tagged lines -
///   SAY: what to tell the engineer      RUN: one CLI line      CONNECT: session      TOOL: id target      NEXT: done|read|ask
/// - short (fast on a laptop CPU), streamable (read-only commands can start before the model has finished), and
/// constrainable with a grammar so a small local model can't drift out of format. Everything about safety is decided
/// outside the model by <see cref="VoiceSafety"/>: a confused or manipulated model can propose but never silently
/// change a device.
/// </summary>
public static class VoicePlanner
{
    public const int MaxScreenLines = 30;
    public const int MaxOutputChars = 3500;

    public static string SystemPrompt => """
        You are OML Voice, the hands-free assistant in OML Terminal, a terminal for network engineers. Engineers talk to
        you naturally in any language. You get a speech transcript that may contain recognition mistakes. Work out what
        they mean and do it on the device in the active terminal.

        Answer ONLY with these lines:
        SAY: <short reply for the engineer, in their language, plain words>
        RUN: <one exact CLI line>            (zero or more, in order; typed into the active terminal)
        CONNECT: <saved session name>        (opens that session)
        TOOL: <tool id> <IP or host>         (opens a tool)
        NEXT: done | read | ask
          done = finished after these actions
          read = you need the output of your RUN lines to answer; SAY only "checking" in their language
          ask  = something essential is missing or ambiguous; ask in SAY, no actions

        Rules:
        - Exact, complete CLI for the platform, never abbreviated, correct order, starting from the current mode.
          Enter and leave configuration mode yourself.
        - Turn intent into real commands: "bring up port 3" = that port's interface + "no shutdown". Use interface
          names visible on the screen. Give masks where needed (/24 = 255.255.255.0).
        - Fix misheard words using the screen and networking sense ("the land 20" = VLAN 20, "gig zero slash one" =
          GigabitEthernet0/1, "ten dot one dot one dot one" = 10.1.1.1).
        - Questions need read-only show commands and NEXT: read; then answer briefly from the output.
        - Never guess names you can't see. If "port 2" or "the uplink" isn't identifiable on the screen, first list
          the interfaces (e.g. "show interfaces status" or "show ip interface brief") with NEXT: read.
        - Don't save the configuration unless asked; after a change, offer to save it.
        - Anticipate: when an answer shows a problem with an obvious fix, say what's wrong and offer the fix as a
          question ("Shall I bring it up?"). If the engineer then just says "yes", do what you offered.
        - "undo that" = reverse your last change.
        - Only use sessions and tools from the lists. If the words are just chat or make no sense, only SAY.
        - If the engineer only wants to see something, run it and use NEXT: done (they read it on screen). Use
          NEXT: read only when you must answer a question from the output - never answer from a guess.

        Examples (Cisco switch, prompt "SW1#"):
        Engineer: "why is gig 1/0/2 down?"
        SAY: Checking GigabitEthernet1/0/2.
        RUN: show interfaces GigabitEthernet1/0/2
        RUN: show logging | include 1/0/2
        NEXT: read

        (after reading "GigabitEthernet1/0/2 is administratively down")
        SAY: Gi1/0/2 is shut down by configuration. Shall I bring it up?
        NEXT: done

        Engineer: "yes"
        SAY: Bringing up Gi1/0/2.
        RUN: configure terminal
        RUN: interface GigabitEthernet1/0/2
        RUN: no shutdown
        RUN: end
        NEXT: done

        Engineer: "which ports are down?"
        SAY: Checking the ports.
        RUN: show interfaces status
        NEXT: read

        Engineer: "put port 5 in vlan 10 and enable it"
        SAY: Putting port 5 in VLAN 10 and enabling it.
        RUN: configure terminal
        RUN: interface GigabitEthernet1/0/5
        RUN: switchport mode access
        RUN: switchport access vlan 10
        RUN: no shutdown
        RUN: end
        NEXT: done

        Engineer (Hindi): "routing table dikhao"
        SAY: Routing table dikha raha hoon.
        RUN: show ip route
        NEXT: done

        Engineer: "open the firewall" (saved sessions include FGT-01)
        SAY: Opening FGT-01.
        CONNECT: FGT-01
        NEXT: done

        Engineer: "set the ip on the uplink"
        SAY: Which address and mask should the uplink get?
        NEXT: ask
        """;

    public static string DescribeSituation(VoiceSituation s)
    {
        var sb = new StringBuilder();
        if (s.DeviceName is null) sb.AppendLine("Active terminal: none (no session tab is selected).");
        else
        {
            sb.AppendLine($"Active terminal: {s.DeviceName}{(string.IsNullOrEmpty(s.DeviceHost) ? "" : $" ({s.DeviceHost})")}{(string.IsNullOrEmpty(s.Protocol) ? "" : $" over {s.Protocol}")}");
            sb.AppendLine($"Platform: {s.PlatformOverride ?? s.Screen.PlatformName}");
            sb.AppendLine($"Prompt: {(s.Screen.Prompt.Length > 0 ? s.Screen.Prompt : "(none visible)")}   Mode: {s.Screen.Mode}");
            sb.AppendLine("Screen:");
            sb.AppendLine(Tail(s.ScreenTail, MaxScreenLines));
        }
        if (s.SessionNames.Count > 0) sb.AppendLine($"Saved sessions: {string.Join(", ", s.SessionNames.Take(80))}");
        if (s.Tools.Count > 0) sb.AppendLine("Tools: " + string.Join("; ", s.Tools.Select(t => $"{t.Id} = {t.Title}")));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The request turn: situation first, then the words - and which language to answer in.</summary>
    public static string RequestMessage(string transcript, string? languageName, VoiceSituation situation) =>
        $"{DescribeSituation(situation)}\n\nThe engineer said{(string.IsNullOrEmpty(languageName) ? "" : $" in {languageName}")}: \"{transcript.Trim()}\"" +
        (string.IsNullOrEmpty(languageName) ? "" : $"\nReply in {languageName}.");

    /// <summary>The follow-up after NEXT: read - the output the commands produced.</summary>
    public static string OutputMessage(IReadOnlyList<(string Command, string Output)> results, string? languageName = null)
    {
        var sb = new StringBuilder("Output:\n");
        int budget = MaxOutputChars;
        foreach (var (cmd, output) in results)
        {
            var o = output.Length > budget ? output[..Math.Max(0, budget)] + "\n... (truncated)" : output;
            budget = Math.Max(400, budget - o.Length);
            sb.Append("$ ").AppendLine(cmd).AppendLine(o.TrimEnd());
        }
        sb.Append($"Now answer the engineer in SAY{(string.IsNullOrEmpty(languageName) ? "" : $" in {languageName}")}: one or two sentences with the key facts. Add RUN lines only if more is needed.");
        return sb.ToString();
    }

    /// <summary>The follow-up when the device rejected a command: let the model correct itself once.</summary>
    public static string ErrorMessage(string command, string error) =>
        $"The device rejected \"{command}\":\n{error.Trim()}\nCommands after it were not sent. Give corrected RUN lines (starting from the current mode), or explain in SAY.";

    public static string Tail(string text, int lines)
    {
        var all = (text ?? "").Replace("\r", "").Split('\n');
        int start = Math.Max(0, all.Length - lines);
        return string.Join('\n', all[start..]).Trim('\n');
    }

    /// <summary>Reads the tagged-line answer. Tolerant of what small models do: JSON instead of lines, code fences,
    /// several commands on one RUN line, a missing NEXT. Unrecognized text becomes something to say, never a command.</summary>
    public static VoicePlan Parse(string reply)
    {
        var text = (reply ?? "").Trim();
        if (text.StartsWith('{') || (text.Contains("\"say\"") && text.Contains('{'))) return ParseJson(text);
        var stream = new VoicePlanStream();
        stream.Feed(text);
        return stream.Finish();
    }

    private static VoicePlan ParseJson(string text)
    {
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return new VoicePlan(StripFences(text), [], VoiceNext.Done);
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var say = root.TryGetProperty("say", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            var next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? ParseNext(n.GetString()) : VoiceNext.Done;
            var actions = new List<VoiceAction>();
            if (root.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var item in a.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) { AddRun(actions, item.GetString()); continue; }
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (Str(item, "run") is { } run) AddRun(actions, run);
                    else if (Str(item, "connect") is { Length: > 0 } session) actions.Add(new VoiceAction(VoiceActionKind.Connect, session));
                    else if (Str(item, "tool") is { Length: > 0 } tool) actions.Add(new VoiceAction(VoiceActionKind.Tool, tool, Str(item, "target") ?? ""));
                }
            return Finalize(say, actions, next);
        }
        catch (JsonException)
        {
            return new VoicePlan(StripFences(text), [], VoiceNext.Done);
        }
    }

    internal static VoicePlan Finalize(string say, List<VoiceAction> actions, VoiceNext next)
    {
        if (next == VoiceNext.Ask) actions.Clear(); // a question for the engineer never comes with actions
        // Reading output back is for questions; after a change the result is on screen already.
        if (next == VoiceNext.Read && actions.Any(a => VoiceSafety.Classify(a) != VoiceRisk.ReadOnly)) next = VoiceNext.Done;
        return new VoicePlan(say.Trim(), actions, actions.Count == 0 && next == VoiceNext.Read ? VoiceNext.Done : next);
    }

    internal static VoiceNext ParseNext(string? s) => (s ?? "").Trim().Trim('.', '"').ToLowerInvariant() switch
    {
        "read" => VoiceNext.Read,
        "ask" => VoiceNext.Ask,
        _ => VoiceNext.Done,
    };

    internal static void AddRun(List<VoiceAction> actions, string? line)
    {
        // One CLI line per action; a model that put several lines in one string gets them split, never joined.
        foreach (var l in (line ?? "").Replace("\r", "").Split('\n'))
            if (l.Trim().Trim('`') is { Length: > 0 } t) actions.Add(new VoiceAction(VoiceActionKind.Run, t));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    private static string StripFences(string s) => s.Replace("```json", "").Replace("```", "").Trim();

    /// <summary>A grammar (GBNF) for local models that guarantees the tagged-line shape.</summary>
    public const string Gbnf = """
        root ::= say action{0,24} next
        say ::= "SAY: " text "\n"
        action ::= ( "RUN: " cli | "CONNECT: " name | "TOOL: " [a-z]{2,16} ( " " cli )? ) "\n"
        next ::= "NEXT: " ( "done" | "read" | "ask" )
        text ::= [^\n\r]{1,300}
        cli ::= [!-~] [ -~]{0,199}
        name ::= [^\n\r]{1,80}
        """;

    /// <summary>The running conversation as chat messages: system, earlier turns (bounded), the current request.</summary>
    public static List<ChatMessage> BuildMessages(IReadOnlyList<ChatMessage> history, string request, int maxHistoryMessages = 10)
    {
        var messages = new List<ChatMessage> { new("system", SystemPrompt) };
        messages.AddRange(history.Skip(Math.Max(0, history.Count - maxHistoryMessages)));
        messages.Add(new ChatMessage("user", request));
        return messages;
    }
}

/// <summary>
/// Reads the model's answer as it streams in, line by line, so the app can act before the answer is complete:
/// speak the SAY line as soon as it's there, and start read-only commands while the rest is still being written.
/// </summary>
public sealed class VoicePlanStream
{
    private readonly StringBuilder _pending = new();
    private readonly List<VoiceAction> _actions = new();
    private string _say = "";
    private VoiceNext _next = VoiceNext.Done;

    /// <summary>Complete actions parsed so far, in order.</summary>
    public IReadOnlyList<VoiceAction> Actions => _actions;
    public string Say => _say;
    public bool SayComplete { get; private set; }

    /// <summary>Feeds streamed text; returns the actions completed by this chunk.</summary>
    public IReadOnlyList<VoiceAction> Feed(string chunk)
    {
        var added = new List<VoiceAction>();
        _pending.Append(chunk);
        while (true)
        {
            var buf = _pending.ToString();
            int nl = buf.IndexOf('\n');
            if (nl < 0) break;
            _pending.Remove(0, nl + 1);
            ParseLine(buf[..nl], added);
        }
        return added;
    }

    public VoicePlan Finish()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        if (rest.Trim().Length > 0) ParseLine(rest, new List<VoiceAction>());
        SayComplete = true;
        return VoicePlanner.Finalize(_say, new List<VoiceAction>(_actions), _next);
    }

    private void ParseLine(string raw, List<VoiceAction> added)
    {
        var line = raw.Trim().Trim('`').Trim();
        if (line.Length == 0 || line.StartsWith("```")) return;
        int colon = line.IndexOf(':');
        var tag = colon > 0 ? line[..colon].Trim().TrimStart('-', '*', ' ').ToUpperInvariant() : "";
        var value = colon > 0 ? line[(colon + 1)..].Trim() : line;
        int before = _actions.Count;
        switch (tag)
        {
            case "SAY":
                _say = _say.Length == 0 ? value : $"{_say} {value}";
                SayComplete = true;
                break;
            case "RUN":
                VoicePlanner.AddRun(_actions, value);
                break;
            case "CONNECT":
                if (value.Trim('"') is { Length: > 0 } s) _actions.Add(new VoiceAction(VoiceActionKind.Connect, s));
                break;
            case "TOOL":
                var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0) _actions.Add(new VoiceAction(VoiceActionKind.Tool, parts[0], parts.Length > 1 ? parts[1] : ""));
                break;
            case "NEXT":
                _next = VoicePlanner.ParseNext(value);
                break;
            default:
                // Text outside the protocol is something to say, never something to run.
                if (_actions.Count == 0) _say = _say.Length == 0 ? line : $"{_say} {line}";
                break;
        }
        for (int i = before; i < _actions.Count; i++) added.Add(_actions[i]);
    }
}
