using System.Text;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Core.Voice.Assistant;

/// <summary>The terminal the engineer is looking at, as the assistant sees it.</summary>
public sealed record VoiceTerminal(string Name, string Host, string Protocol, TerminalSession Session, Func<string> ScreenText);

/// <summary>A change waiting for the engineer's go-ahead.</summary>
public sealed record VoiceConfirmation(string Target, IReadOnlyList<VoiceAction> Actions, VoiceRisk Risk, string Say);

/// <summary>What the app gives the assistant: the current terminal, saved sessions, tools, and a way to ask.
/// Implementations marshal to the UI thread themselves; every member may be called from a background thread.</summary>
public interface IVoiceHost
{
    VoiceTerminal? ActiveTerminal();
    IReadOnlyList<SessionProfile> Sessions();
    IReadOnlyList<(string Id, string Title)> Tools();
    /// <summary>Opens a session tab; completes when the tab exists (not necessarily logged in yet).</summary>
    Task<bool> ConnectAsync(SessionProfile profile);
    Task<bool> OpenToolAsync(string id, string target);
    /// <summary>Shows the change for approval. Completes with the click, or when <see cref="VoiceAssistant.Answer"/>
    /// resolves it by voice (the host doesn't need to listen itself).</summary>
    Task<bool> ConfirmAsync(VoiceConfirmation confirmation, CancellationToken ct);
}

public enum VoiceEventKind { Heard, Thinking, Say, Run, Output, Opened, Confirm, Declined, Error, Done }

public sealed record VoiceEvent(VoiceEventKind Kind, string Text, string? Detail = null, bool Speak = false);

/// <summary>
/// The voice assistant: an engineer says what they want, in any language, and it gets done. One request flows
/// heard → understood (the language model, or straight through for a literal command) → checked
/// (<see cref="CliPlanFixer"/>, <see cref="VoiceSafety"/>) → confirmed when it changes something → typed into the
/// engineer's own terminal → read back and answered when it was a question. The conversation is remembered, so
/// "now the same on port 5" and "undo that" work. Everything it does is written to an audit log.
/// </summary>
public sealed partial class VoiceAssistant
{
    private readonly IVoiceHost _host;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private TaskCompletionSource<bool>? _pendingConfirm;
    private VoiceRisk _pendingRisk;
    private readonly string _logPath;

    public IVoiceBrain? Brain { get; set; }
    /// <summary>Changes wait for a yes/click (default). Destructive commands always need a click regardless.</summary>
    public bool ConfirmChanges { get; set; } = true;
    /// <summary>Overrides the detected platform (the engineer picked one in the voice settings).</summary>
    public string? PlatformOverride { get; set; }

    public event Action<VoiceEvent>? Event;

    public VoiceAssistant(IVoiceHost host)
    {
        _host = host;
        var dir = Path.Combine(AppPaths.DataDirectory, "voice-logs");
        try { Directory.CreateDirectory(dir); } catch { }
        _logPath = Path.Combine(dir, $"voice_{DateTime.Now:yyyyMMdd}.log");
    }

    public bool Busy => _busy.CurrentCount == 0;
    public bool AwaitingConfirmation => _pendingConfirm is { Task.IsCompleted: false };

    /// <summary>Forget the conversation ("new topic").</summary>
    public void Reset() => _history.Clear();

    /// <summary>
    /// Feeds one utterance in. While a change waits for approval, a spoken yes/no answers it (any language);
    /// anything else cancels the pending change and is handled as a new request.
    /// </summary>
    public async Task HandleAsync(Transcript heard, CancellationToken ct = default)
    {
        var text = heard.Text.Trim();
        if (text.Length == 0) return;
        if (AwaitingConfirmation && Answer(text)) return;

        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Raise(VoiceEventKind.Error, "Still working on the last request - say it again in a moment.");
            return;
        }
        try
        {
            Raise(VoiceEventKind.Heard, text, heard.LanguageName);
            Log($"HEARD [{heard.LanguageName}] {text}");
            await ProcessAsync(heard, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Raise(VoiceEventKind.Error, "Stopped."); }
        catch (Exception ex)
        {
            Log($"ERROR {ex.Message}");
            Raise(VoiceEventKind.Error, ex.Message);
        }
        finally
        {
            _busy.Release();
            Raise(VoiceEventKind.Done, "");
        }
    }

    /// <summary>Resolves a pending confirmation from speech. Returns true when the words were a yes or a no.</summary>
    public bool Answer(string utterance)
    {
        var pending = _pendingConfirm;
        if (pending is null || pending.Task.IsCompleted) return false;
        if (VoiceReplyWords.IsNo(utterance)) { pending.TrySetResult(false); return true; }
        if (VoiceReplyWords.IsYes(utterance))
        {
            if (_pendingRisk == VoiceRisk.Destructive)
            {
                Raise(VoiceEventKind.Say, "This one can take the device down - click Apply to confirm it.", speak: true);
                return true;
            }
            pending.TrySetResult(true);
            return true;
        }
        pending.TrySetResult(false); // something else entirely: drop the pending change, treat as a new request
        return false;
    }

    private async Task ProcessAsync(Transcript heard, CancellationToken ct)
    {
        var terminal = _host.ActiveTerminal();
        var screenText = terminal?.ScreenText() ?? "";
        var screen = ScreenContext.Analyze(screenText);

        // 1. A literal command ("show ip route", "no shutdown") needs no interpretation - run it straight away.
        if (terminal is not null && LiteralCli.TryParse(heard.Text, screen) is { } literal)
        {
            Log($"LITERAL {string.Join(" | ", literal)}");
            var actions = literal.Select(l => new VoiceAction(VoiceActionKind.Run, l)).ToList();
            await ExecutePlanAsync(new VoicePlan("", actions, VoiceNext.Done), heard, screen, alreadyRun: 0, ct).ConfigureAwait(false);
            Remember(heard.Text, actions, "");
            return;
        }

        if (Brain is null)
        {
            Raise(VoiceEventKind.Say, "I can run exact commands, but to understand requests in your own words I need a language model - turn one on in Voice settings.", speak: true);
            return;
        }

        // 2. Ask the model, acting on the answer as it streams in.
        var situation = new VoiceSituation(terminal?.Name, terminal?.Host, terminal?.Protocol, screen, screenText,
            _host.Sessions().Select(s => s.Name).ToList(), _host.Tools(), PlatformOverride);
        var messages = VoicePlanner.BuildMessages(_history, VoicePlanner.RequestMessage(heard.Text, heard.LanguageName, situation));
        Raise(VoiceEventKind.Thinking, "Thinking...");
        var plan = await AskAsync(messages, terminal, screen, ct).ConfigureAwait(false);
        int ranEarly = plan.EarlyRuns.Count;
        var results = new List<(string, string)>(plan.EarlyRuns);

        for (int round = 0; ; round++)
        {
            var outcome = await ExecutePlanAsync(plan.Plan, heard, screen, ranEarly, ct, results).ConfigureAwait(false);
            Remember(round == 0 ? heard.Text : null, outcome.Ran, plan.Raw);

            if (outcome.Rejected is { } rejected && round < 2)
            {
                // The device said no: show the model what happened and let it correct itself once.
                messages.Add(new ChatMessage("assistant", plan.Raw));
                messages.Add(new ChatMessage("user", VoicePlanner.ErrorMessage(rejected.Command, rejected.Output)));
                terminal = _host.ActiveTerminal();
                screen = ScreenContext.Analyze(terminal?.ScreenText() ?? "");
                Raise(VoiceEventKind.Thinking, "The device rejected that - fixing it...");
                plan = await AskAsync(messages, terminal, screen, ct, allowEarly: false).ConfigureAwait(false);
                ranEarly = 0;
                results.Clear();
                continue;
            }
            if (plan.Plan.Next == VoiceNext.Read && outcome.Completed && results.Count > 0 && round < 2)
            {
                // A question: hand the output back for the answer.
                messages.Add(new ChatMessage("assistant", plan.Raw));
                messages.Add(new ChatMessage("user", VoicePlanner.OutputMessage(results, heard.LanguageName)));
                Raise(VoiceEventKind.Thinking, "Reading the output...");
                terminal = _host.ActiveTerminal();
                screen = ScreenContext.Analyze(terminal?.ScreenText() ?? "");
                plan = await AskAsync(messages, terminal, screen, ct, allowEarly: false).ConfigureAwait(false);
                ranEarly = 0;
                results = new List<(string, string)>();
                continue;
            }
            break;
        }
    }

    private sealed record Asked(VoicePlan Plan, string Raw, List<(string Command, string Output)> EarlyRuns);

    /// <summary>Streams the model's answer. Leading read-only commands start as soon as their line arrives, so the
    /// engineer sees "show ..." output while the rest of the answer is still being written.</summary>
    private async Task<Asked> AskAsync(List<ChatMessage> messages, VoiceTerminal? terminal, ScreenState screen, CancellationToken ct, bool allowEarly = true)
    {
        var stream = new VoicePlanStream();
        var raw = new StringBuilder();
        var early = new List<(string, string)>();
        bool sayShown = false, stillLeading = allowEarly && terminal is not null && !screen.InConfigMode;
        await foreach (var piece in Brain!.StreamAsync(messages, ct).ConfigureAwait(false))
        {
            raw.Append(piece);
            var added = stream.Feed(piece);
            if (!sayShown && stream.SayComplete && stream.Say.Length > 0)
            {
                sayShown = true;
                Raise(VoiceEventKind.Thinking, stream.Say);
            }
            foreach (var a in added)
            {
                if (!stillLeading || a.Kind != VoiceActionKind.Run || VoiceSafety.Classify(a) != VoiceRisk.ReadOnly || LiteralCli.IsNavigation(a.Value))
                {
                    stillLeading = false;
                    continue;
                }
                Raise(VoiceEventKind.Run, a.Value);
                Log($"RUN {a.Value}");
                var output = await TerminalRunner.RunAsync(terminal!.Session, a.Value, ct).ConfigureAwait(false);
                early.Add((a.Value, output));
                Raise(VoiceEventKind.Output, a.Value, output);
            }
        }
        var plan = stream.Finish();
        Log($"PLAN {raw.ToString().Replace('\n', ' ')}");
        return new Asked(plan, raw.ToString(), early);
    }

    private sealed record Outcome(List<VoiceAction> Ran, bool Completed, (string Command, string Output)? Rejected);

    private async Task<Outcome> ExecutePlanAsync(VoicePlan plan, Transcript heard, ScreenState screen, int alreadyRun, CancellationToken ct,
        List<(string, string)>? results = null)
    {
        var ran = new List<VoiceAction>();
        var remaining = plan.Actions.Skip(alreadyRun).ToList();
        var fixedActions = CliPlanFixer.Fix(remaining, screen);
        var risk = VoiceSafety.Highest(fixedActions);
        bool speakSay = plan.Next != VoiceNext.Read && plan.Say.Length > 0;

        if (plan.Next == VoiceNext.Ask || fixedActions.Count == 0)
        {
            if (plan.Say.Length > 0) Raise(VoiceEventKind.Say, plan.Say, speak: true);
            return new Outcome(ran, true, null);
        }

        if (risk != VoiceRisk.ReadOnly && (ConfirmChanges || risk == VoiceRisk.Destructive))
        {
            var target = _host.ActiveTerminal()?.Name ?? "this device";
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingConfirm = tcs;
            _pendingRisk = risk;
            var confirmation = new VoiceConfirmation(target, fixedActions, risk, plan.Say);
            Raise(VoiceEventKind.Confirm, plan.Say.Length > 0 ? plan.Say : $"Apply {fixedActions.Count} command(s) to {target}?",
                string.Join('\n', fixedActions), speak: true);
            Log($"CONFIRM? [{risk}] {string.Join(" | ", fixedActions)}");
            var click = _host.ConfirmAsync(confirmation, ct);
            var winner = await Task.WhenAny(tcs.Task, click).ConfigureAwait(false);
            bool approved = winner == tcs.Task ? tcs.Task.Result : await click.ConfigureAwait(false);
            tcs.TrySetResult(approved);
            _pendingConfirm = null;
            Log(approved ? "APPROVED" : "DECLINED");
            if (!approved)
            {
                Raise(VoiceEventKind.Declined, "Not applied.");
                return new Outcome(ran, false, null);
            }
            speakSay = false; // already said when asking
        }
        else if (speakSay) Raise(VoiceEventKind.Say, plan.Say, speak: true);

        foreach (var action in fixedActions)
        {
            ct.ThrowIfCancellationRequested();
            switch (action.Kind)
            {
                case VoiceActionKind.Connect:
                    var profile = FindSession(action.Value);
                    if (profile is null) { Raise(VoiceEventKind.Error, $"There's no saved session called \"{action.Value}\"."); return new Outcome(ran, false, null); }
                    Raise(VoiceEventKind.Opened, $"Opening {profile.Name}");
                    Log($"CONNECT {profile.Name}");
                    if (!await _host.ConnectAsync(profile).ConfigureAwait(false)) return new Outcome(ran, false, null);
                    await WaitForPromptAsync(ct).ConfigureAwait(false);
                    ran.Add(action);
                    break;
                case VoiceActionKind.Tool:
                    Raise(VoiceEventKind.Opened, $"Opening {action}");
                    Log($"TOOL {action.Value} {action.Target}");
                    await _host.OpenToolAsync(action.Value, action.Target).ConfigureAwait(false);
                    ran.Add(action);
                    break;
                default:
                    var terminal = _host.ActiveTerminal();
                    if (terminal is null || !terminal.Session.IsConnected)
                    {
                        Raise(VoiceEventKind.Error, "There's no connected terminal to run that in - open a session first.");
                        return new Outcome(ran, false, null);
                    }
                    Raise(VoiceEventKind.Run, action.Value);
                    Log($"RUN {action.Value}");
                    var output = await TerminalRunner.RunAsync(terminal.Session, action.Value, ct).ConfigureAwait(false);
                    ran.Add(action);
                    results?.Add((action.Value, output));
                    Raise(VoiceEventKind.Output, action.Value, output);
                    if (Rejected(output) is { } why)
                    {
                        Log($"REJECTED {action.Value}: {why}");
                        Raise(VoiceEventKind.Error, $"{terminal.Name} rejected \"{action.Value}\": {why}");
                        // Don't leave the engineer stranded in a configuration mode the plan entered.
                        if (!screen.InConfigMode && screen.Platform is DevicePlatform.CiscoIos or DevicePlatform.CiscoNxos or DevicePlatform.CiscoAsa or DevicePlatform.AristaEos
                            && ScreenContext.Analyze(terminal.ScreenText()).InConfigMode)
                        {
                            Log("RUN end (back to where the engineer was)");
                            await TerminalRunner.RunAsync(terminal.Session, "end", ct).ConfigureAwait(false);
                        }
                        return new Outcome(ran, false, (action.Value, output));
                    }
                    break;
            }
        }
        return new Outcome(ran, true, null);
    }

    /// <summary>After opening a session: wait until it's connected and its prompt has appeared (login done).</summary>
    private async Task WaitForPromptAsync(CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            var t = _host.ActiveTerminal();
            if (t is { Session.IsConnected: true } && TerminalRunner.LooksLikePrompt(LastLine(t.ScreenText()))) return;
        }
    }

    private SessionProfile? FindSession(string name)
    {
        var sessions = _host.Sessions();
        var n = name.Trim().Trim('"');
        return sessions.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? sessions.FirstOrDefault(s => s.Host.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? sessions.FirstOrDefault(s => Squash(s.Name) == Squash(n))
               ?? sessions.Where(s => Squash(s.Name).Contains(Squash(n)) || Squash(n).Contains(Squash(s.Name))).OrderBy(s => s.Name.Length).FirstOrDefault();
    }

    private static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>What the device printed when it refused a command, or null when it accepted it.</summary>
    public static string? Rejected(string output)
    {
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (RejectionLine().IsMatch(t)) return t.TrimStart('%', ' ');
        }
        return null;
    }

    [GeneratedRegex(@"^(?:%\s*(?:Invalid|Incomplete|Ambiguous|Unknown|Unrecognized|Bad|Error|Cannot|Not allowed)|%\s*\S.*\berror\b|ERROR:|Error: )|\b(?:syntax error|unknown command|Command fail|Unknown action|command not found|is not recognized as|invalid command|Unrecognized command|invalid input)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RejectionLine();

    private static string LastLine(string s)
    {
        var lines = s.Replace("\r", "").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--) if (lines[i].Trim().Length > 0) return lines[i];
        return "";
    }

    /// <summary>Keeps the conversation short: what was asked and what was actually done (not the whole screen).</summary>
    private void Remember(string? request, IReadOnlyList<VoiceAction> ran, string raw)
    {
        if (request is not null) _history.Add(new ChatMessage("user", $"The engineer said: \"{request}\""));
        var done = ran.Count == 0 ? "(nothing was run)" : string.Join('\n', ran.Select(a => a.Kind switch
        {
            VoiceActionKind.Run => $"RUN: {a.Value}",
            VoiceActionKind.Connect => $"CONNECT: {a.Value}",
            _ => $"TOOL: {a.Value} {a.Target}".TrimEnd(),
        }));
        var say = VoicePlanner.Parse(raw).Say;
        _history.Add(new ChatMessage("assistant", $"SAY: {say}\n{done}\nNEXT: done"));
        while (_history.Count > 16) _history.RemoveAt(0);
    }

    private void Raise(VoiceEventKind kind, string text, string? detail = null, bool speak = false) =>
        Event?.Invoke(new VoiceEvent(kind, text, detail, speak));

    private void Log(string line)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n"); } catch { }
    }
}

/// <summary>
/// Recognizes when the engineer simply said a command ("show ip route", "no shutdown", "ping 8.8.8.8") - those run
/// at once, without waiting for the language model. Anything conversational ("show me the routes") is left to the
/// model, since a CLI would reject it.
/// </summary>
public static partial class LiteralCli
{
    public static IReadOnlyList<string>? TryParse(string text, ScreenState screen)
    {
        var grammar = GrammarFor(screen.Platform);
        if (grammar is null) return null;
        var t = text.Trim().TrimEnd('.', '!', '?', '。');
        if (t.Length == 0 || t.Length > 160 || NonAscii().IsMatch(t) || Conversational().IsMatch(t)) return null;
        var lines = VoiceCommandParser.Expand(t, grammar);
        if (lines.Count == 0 || lines.Count > 6) return null;
        foreach (var l in lines)
            if (!grammar.LooksLikeCli(l) || VoiceCommandParser.LooksIncomplete(l) || CliValidator.Problem(l) is not null) return null;
        return lines;
    }

    public static bool IsNavigation(string command) => Navigation().IsMatch(command.Trim());

    private static IVendorGrammar? GrammarFor(DevicePlatform p) => p switch
    {
        DevicePlatform.CiscoIos => new CiscoIosGrammar(),
        DevicePlatform.CiscoNxos => new CiscoNxosGrammar(),
        DevicePlatform.CiscoAsa => new CiscoAsaGrammar(),
        DevicePlatform.AristaEos => new AristaEosGrammar(),
        DevicePlatform.Junos => new JuniperGrammar(),
        DevicePlatform.FortiOs => new FortinetGrammar(),
        DevicePlatform.PanOs => new PaloAltoGrammar(),
        _ => null,
    };

    [GeneratedRegex(@"[^\x00-\x7F]")]
    private static partial Regex NonAscii();

    // Words that make it a sentence rather than a command.
    [GeneratedRegex(@"\b(?:me|my|the|please|what|whats|what's|why|how|which|where|when|can|could|would|should|tell|check|give|i|you|your|is|are|was|were|it|its|it's|this|that|these|those|them|some|any|bring|put|make|let|lets|let's|want|need|again|undo|same|also)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Conversational();

    [GeneratedRegex(@"^(?:configure(?:\s+terminal)?|conf\s+t|end|exit|top|up|enable|disable)$", RegexOptions.IgnoreCase)]
    private static partial Regex Navigation();
}
