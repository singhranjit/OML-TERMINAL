using System.Runtime.CompilerServices;
using System.Text;
using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Transports;
using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;

namespace OmlTerminal.Core.Tests;

public class ScreenContextTests
{
    [Theory]
    [InlineData("SW1#", DevicePlatform.CiscoIos, "privileged EXEC", false)]
    [InlineData("SW1>", DevicePlatform.CiscoIos, "user EXEC (not enabled)", false)]
    [InlineData("SW1(config)#", DevicePlatform.CiscoIos, "global configuration", true)]
    [InlineData("SW1(config-if)#", DevicePlatform.CiscoIos, "interface configuration", true)]
    [InlineData("admin@mx1>", DevicePlatform.Junos, "operational mode", false)]
    [InlineData("FGT-01 (port1) # ", DevicePlatform.FortiOs, "inside config (port1)", true)]
    [InlineData("<HUAWEI>", DevicePlatform.HuaweiVrp, "user view", false)]
    [InlineData("[admin@MikroTik] /ip address>", DevicePlatform.MikroTik, "menu /ip address", false)]
    [InlineData("oml@ubuntu:~$", DevicePlatform.LinuxShell, "shell", false)]
    [InlineData("PS C:\\Users\\me>", DevicePlatform.WindowsShell, "shell", false)]
    public void ReadsPlatformAndModeFromThePrompt(string prompt, DevicePlatform platform, string mode, bool config)
    {
        var s = ScreenContext.Analyze($"some output\n\n{prompt}");
        Assert.Equal(platform, s.Platform);
        Assert.Equal(mode, s.Mode);
        Assert.Equal(config, s.InConfigMode);
    }

    [Fact]
    public void BannersBreakTiesBetweenLookAlikePrompts()
    {
        Assert.Equal(DevicePlatform.CiscoNxos, ScreenContext.Analyze("Cisco Nexus Operating System (NX-OS) Software\nN9K#").Platform);
        Assert.Equal(DevicePlatform.AristaEos, ScreenContext.Analyze("Arista vEOS\nleaf1#").Platform);
        Assert.Equal(DevicePlatform.FortiOs, ScreenContext.Analyze("FortiGate-100F v7.4\nFGT-01 # ").Platform);
        Assert.Equal(DevicePlatform.PanOs, ScreenContext.Analyze("PA-VM\nadmin@PA-VM>").Platform);
        Assert.Equal(DevicePlatform.Unknown, ScreenContext.Analyze("").Platform);
    }
}

public class VoiceSafetyTests2
{
    [Theory]
    [InlineData("show ip interface brief", VoiceRisk.ReadOnly)]
    [InlineData("do show run | include hostname", VoiceRisk.ReadOnly)]
    [InlineData("show running-config | section interface", VoiceRisk.ReadOnly)]
    [InlineData("ping 8.8.8.8", VoiceRisk.ReadOnly)]
    [InlineData("configure terminal", VoiceRisk.ReadOnly)]
    [InlineData("end", VoiceRisk.ReadOnly)]
    [InlineData("get system status", VoiceRisk.ReadOnly)]
    [InlineData("display interface brief", VoiceRisk.ReadOnly)]
    [InlineData("/interface print", VoiceRisk.ReadOnly)]
    [InlineData("ip addr", VoiceRisk.ReadOnly)]
    [InlineData("ip address 10.30.0.1 255.255.255.0", VoiceRisk.Change)] // looks like Linux "ip addr" - must not slip through
    [InlineData("interface GigabitEthernet1/0/3", VoiceRisk.Change)]
    [InlineData("no shutdown", VoiceRisk.Change)]
    [InlineData("hostname CORE-SW9", VoiceRisk.Change)]
    [InlineData("write memory", VoiceRisk.Change)]
    [InlineData("show run | redirect flash:x.txt", VoiceRisk.Change)]
    [InlineData("show version; reload", VoiceRisk.Change)]
    [InlineData("ls; rm -rf /", VoiceRisk.Change)]
    [InlineData("cat x > /etc/passwd", VoiceRisk.Change)]
    [InlineData("date -s 2020-01-01", VoiceRisk.Change)]
    [InlineData("reload", VoiceRisk.Destructive)]
    [InlineData("write erase", VoiceRisk.Destructive)]
    [InlineData("request system reboot", VoiceRisk.Destructive)]
    [InlineData("execute factoryreset", VoiceRisk.Destructive)]
    [InlineData("clear ip bgp *", VoiceRisk.Destructive)]
    [InlineData("no router ospf 1", VoiceRisk.Destructive)]
    public void ClassifiesCommands(string command, VoiceRisk expected) => Assert.Equal(expected, VoiceSafety.Classify(command));

    [Theory]
    [InlineData("yes")] [InlineData("Yes, go ahead.")] [InlineData("haan kar do")] [InlineData("हाँ")] [InlineData("sí")]
    [InlineData("oui")] [InlineData("ja")] [InlineData("да")] [InlineData("はい")] [InlineData("是的")] [InlineData("네")] [InlineData("نعم")]
    public void UnderstandsYesInManyLanguages(string utterance)
    {
        Assert.True(VoiceReplyWords.IsYes(utterance));
        Assert.False(VoiceReplyWords.IsNo(utterance));
    }

    [Theory]
    [InlineData("no")] [InlineData("cancel that")] [InlineData("nahi")] [InlineData("नहीं")] [InlineData("nein")] [InlineData("ne")]
    [InlineData("нет")] [InlineData("いいえ")] [InlineData("不要")] [InlineData("아니요")] [InlineData("لا")] [InlineData("don't")]
    public void UnderstandsNoInManyLanguages(string utterance)
    {
        Assert.True(VoiceReplyWords.IsNo(utterance));
        Assert.False(VoiceReplyWords.IsYes(utterance));
    }

    [Fact]
    public void ALongSentenceIsANewRequestNotAYes() =>
        Assert.False(VoiceReplyWords.IsYes("yes and also show me the routing table on the core switch please right now"));
}

public class VoicePlannerTests
{
    [Fact]
    public void ParsesTaggedLines()
    {
        var plan = VoicePlanner.Parse("SAY: Bringing up port 3.\nRUN: interface Gi1/0/3\nRUN: no shutdown\nCONNECT: CORE-SW2\nTOOL: pathtrace 8.8.8.8\nNEXT: done");
        Assert.Equal("Bringing up port 3.", plan.Say);
        Assert.Equal(VoiceNext.Done, plan.Next);
        Assert.Equal(["interface Gi1/0/3", "no shutdown"], plan.Actions.Where(a => a.Kind == VoiceActionKind.Run).Select(a => a.Value));
        Assert.Contains(plan.Actions, a => a is { Kind: VoiceActionKind.Connect, Value: "CORE-SW2" });
        Assert.Contains(plan.Actions, a => a is { Kind: VoiceActionKind.Tool, Value: "pathtrace", Target: "8.8.8.8" });
    }

    [Fact]
    public void AcceptsJsonAndFencesFromModelsThatIgnoreTheFormat()
    {
        var plan = VoicePlanner.Parse("```json\n{\"say\":\"ok\",\"actions\":[{\"run\":\"show ip route\"},\"show vlan brief\"],\"next\":\"read\"}\n```");
        Assert.Equal(["show ip route", "show vlan brief"], plan.Actions.Select(a => a.Value));
        Assert.Equal(VoiceNext.Read, plan.Next);
    }

    [Fact]
    public void TextOutsideTheProtocolIsSaidNeverRun()
    {
        var plan = VoicePlanner.Parse("I think you should reload the switch.");
        Assert.Empty(plan.Actions);
        Assert.Contains("reload", plan.Say);
    }

    [Fact]
    public void AQuestionNeverCarriesActions_AndChangesNeverWaitToRead()
    {
        Assert.Empty(VoicePlanner.Parse("SAY: Which port?\nRUN: show ip int brief\nNEXT: ask").Actions);
        Assert.Equal(VoiceNext.Done, VoicePlanner.Parse("SAY: ok\nRUN: interface Gi1/0/1\nRUN: shutdown\nNEXT: read").Next);
    }

    [Fact]
    public void StreamingYieldsActionsAsTheirLinesComplete()
    {
        var s = new VoicePlanStream();
        Assert.Empty(s.Feed("SAY: Check"));
        Assert.False(s.SayComplete);
        Assert.Empty(s.Feed("ing.\nRUN: show int"));
        Assert.True(s.SayComplete);
        var added = s.Feed("erfaces status\nRUN: show log");
        Assert.Equal("show interfaces status", Assert.Single(added).Value);
        var plan = s.Finish();
        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal("Checking.", plan.Say);
    }

    [Fact]
    public void RequestTellsTheModelTheLanguageAndTheScreen()
    {
        var screen = ScreenContext.Analyze("SW1#");
        var msg = VoicePlanner.RequestMessage("port do kyun down hai", "Hindi",
            new VoiceSituation("SW1", "10.0.0.1", "SSH", screen, "SW1#", ["SW1"], [("pathtrace", "Visual Trace")]));
        Assert.Contains("in Hindi", msg);
        Assert.Contains("Reply in Hindi", msg);
        Assert.Contains("Prompt: SW1#", msg);
        Assert.Contains("pathtrace = Visual Trace", msg);
    }
}

public class CliPlanFixerTests
{
    private static List<VoiceAction> Runs(params string[] lines) => lines.Select(l => new VoiceAction(VoiceActionKind.Run, l)).ToList();
    private static IEnumerable<string> Lines(IReadOnlyList<VoiceAction> a) => a.Select(x => x.Value);

    [Fact]
    public void AddsConfigureTerminalAndEndAroundConfigFromExecMode()
    {
        var fixedPlan = CliPlanFixer.Fix(Runs("interface gi1/0/4", "shutdown"), ScreenContext.Analyze("SW1#"));
        Assert.Equal(["configure terminal", "interface gi1/0/4", "shutdown", "end"], Lines(fixedPlan));
    }

    [Fact]
    public void LeavesACompletePlanAlone_AndDropsADuplicateConfigureTerminal()
    {
        var fixedPlan = CliPlanFixer.Fix(Runs("configure terminal", "interface Vlan30", "no shutdown", "end"), ScreenContext.Analyze("SW1#"));
        Assert.Equal(["configure terminal", "interface Vlan30", "no shutdown", "end"], Lines(fixedPlan));
        var inConfig = CliPlanFixer.Fix(Runs("configure terminal", "vlan 30"), ScreenContext.Analyze("SW1(config)#"));
        Assert.Equal(["vlan 30"], Lines(inConfig));
    }

    [Fact]
    public void PrefixesShowWithDoInsideIosConfigMode_AndStripsDoOutsideIt()
    {
        Assert.Equal(["do show ip route"], Lines(CliPlanFixer.Fix(Runs("show ip route"), ScreenContext.Analyze("SW1(config-if)#"))));
        Assert.Equal(["show ip route"], Lines(CliPlanFixer.Fix(Runs("do show ip route"), ScreenContext.Analyze("SW1#"))));
        // NX-OS runs show inside config mode as-is.
        Assert.Equal(["show ip route"], Lines(CliPlanFixer.Fix(Runs("show ip route"), ScreenContext.Analyze("NX-OS\nN9K(config)#"))));
    }

    [Fact]
    public void ExecChangesLikeWriteMemoryStayInExec()
    {
        Assert.Equal(["write memory"], Lines(CliPlanFixer.Fix(Runs("write memory"), ScreenContext.Analyze("SW1#"))));
    }

    [Fact]
    public void JunosSetCommandsAreCommitted()
    {
        var fixedPlan = CliPlanFixer.Fix(Runs("set interfaces ge-0/0/1 description uplink"), ScreenContext.Analyze("admin@mx1>"));
        Assert.Equal(["configure", "set interfaces ge-0/0/1 description uplink", "commit and-quit"], Lines(fixedPlan));
    }
}

public class LiteralCliTests
{
    [Theory]
    [InlineData("show ip route")]
    [InlineData("show ip interface brief")]
    [InlineData("ping 8.8.8.8")]
    public void PlainCommandsSkipTheModel(string said) =>
        Assert.NotNull(LiteralCli.TryParse(said, ScreenContext.Analyze("SW1#")));

    [Theory]
    [InlineData("show me the routing table")]
    [InlineData("why is port 2 down")]
    [InlineData("bring up port 3")]
    [InlineData("पोर्ट 2 क्यों डाउन है")]
    public void ConversationGoesToTheModel(string said) =>
        Assert.Null(LiteralCli.TryParse(said, ScreenContext.Analyze("SW1#")));

    [Fact]
    public void NoLiteralPathOnUnknownPlatforms() => Assert.Null(LiteralCli.TryParse("show ip route", ScreenContext.Analyze("")));
}

/// <summary>A pretend Cisco switch behind a transport: echoes, prompts, modes, a pager, and rejects nonsense.</summary>
internal sealed class FakeSwitch : ITerminalTransport
{
    private string _mode = "";
    private readonly StringBuilder _line = new();
    private string? _paging;
    public List<string> Received { get; } = new();
    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed { add { } remove { } }

    public string Prompt => $"SW1{(_mode.Length > 0 ? $"({_mode})" : "")}#";

    public Task ConnectAsync(int c, int r, CancellationToken ct = default)
    {
        Emit($"\r\n{Prompt}");
        return Task.CompletedTask;
    }

    public void Write(ReadOnlySpan<byte> d)
    {
        foreach (var ch in Encoding.UTF8.GetString(d))
        {
            if (_paging is not null && ch == ' ') { var rest = _paging; _paging = null; Emit($"\r          \r{rest}\r\n{Prompt}"); continue; }
            if (ch == '\r') { var cmd = _line.ToString(); _line.Clear(); Received.Add(cmd); Respond(cmd); }
            else _line.Append(ch);
        }
    }

    private void Respond(string cmd)
    {
        var c = cmd.Trim();
        string output = "";
        if (c == "configure terminal") { _mode = "config"; output = "Enter configuration commands, one per line.  End with CNTL/Z."; }
        else if (c.StartsWith("interface ")) _mode = "config-if";
        else if (c == "end") _mode = "";
        else if (c is "no shutdown" or "shutdown" || c.StartsWith("switchport") || c.StartsWith("description") || c.StartsWith("ip address")) { }
        else if (c == "show ip route")
        {
            Emit($"{cmd}\r\nCodes: C - connected, S - static\r\nGateway of last resort is 10.0.0.1\r\n --More-- ");
            _paging = "S*    0.0.0.0/0 [1/0] via 10.0.0.1\r\nC     10.20.0.0/24 is directly connected, Vlan20";
            return;
        }
        else if (c == "show interfaces GigabitEthernet1/0/2") output = "GigabitEthernet1/0/2 is down, line protocol is down (notconnect)";
        else output = "                   ^\r\n% Invalid input detected at '^' marker.";
        Emit($"{cmd}\r\n{(output.Length > 0 ? output + "\r\n" : "")}{Prompt}");
    }

    private void Emit(string s) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(s));
    public void Resize(int c, int r) { }
    public void Close() { }
    public void Dispose() { }
}

internal sealed class ScriptedBrain(params string[] replies) : IVoiceBrain
{
    private int _next;
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();
    public string Name => "scripted";

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls.Add(messages.ToList());
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        foreach (var chunk in reply.Chunk(7)) { await Task.Yield(); yield return new string(chunk); }
    }

    public void Dispose() { }
}

internal sealed class FakeHost(TerminalSession session) : IVoiceHost
{
    public bool Approve { get; set; } = true;
    public List<VoiceConfirmation> Asked { get; } = new();
    public List<string> Connected { get; } = new();
    public List<string> Tools2 { get; } = new();
    public TaskCompletionSource<bool>? HoldConfirm { get; set; }

    public VoiceTerminal ActiveTerminal() => new("SW1", "10.0.0.1", "SSH", session, () => session.Engine.GetBufferText());
    VoiceTerminal? IVoiceHost.ActiveTerminal() => ActiveTerminal();
    public IReadOnlyList<SessionProfile> Sessions() => [new SessionProfile { Name = "CORE-SW2", Host = "10.0.0.2" }, new SessionProfile { Name = "FGT-01", Host = "10.0.0.250" }];
    public IReadOnlyList<(string Id, string Title)> Tools() => [("pathtrace", "Visual Trace")];
    public Task<bool> ConnectAsync(SessionProfile profile) { Connected.Add(profile.Name); return Task.FromResult(true); }
    public Task<bool> OpenToolAsync(string id, string target) { Tools2.Add($"{id} {target}".Trim()); return Task.FromResult(true); }
    public Task<bool> ConfirmAsync(VoiceConfirmation confirmation, CancellationToken ct)
    {
        Asked.Add(confirmation);
        return HoldConfirm?.Task ?? Task.FromResult(Approve);
    }
}

public class VoiceAssistantTests
{
    private static async Task<(VoiceAssistant, FakeSwitch, FakeHost, List<VoiceEvent>)> Setup(params string[] replies)
    {
        var sw = new FakeSwitch();
        var session = new TerminalSession(sw, new XTermEngine(120, 40));
        await session.ConnectAsync(120, 40);
        var host = new FakeHost(session);
        var assistant = new VoiceAssistant(host) { Brain = new ScriptedBrain(replies) };
        var events = new List<VoiceEvent>();
        assistant.Event += e => { lock (events) events.Add(e); };
        return (assistant, sw, host, events);
    }

    private static Transcript Said(string text, string lang = "English") => new(text, "en", lang);

    [Fact]
    public async Task AChangeIsConfirmedThenTypedWithTheRightModes()
    {
        var (a, sw, host, events) = await Setup("SAY: Shutting down port 4.\nRUN: interface GigabitEthernet1/0/4\nRUN: shutdown\nNEXT: done");
        await a.HandleAsync(Said("bhai gig 1/0/4 ko shut kar do", "Hindi"));
        var asked = Assert.Single(host.Asked);
        Assert.Equal(VoiceRisk.Change, asked.Risk);
        Assert.Equal(["configure terminal", "interface GigabitEthernet1/0/4", "shutdown", "end"], sw.Received);
        Assert.Contains(events, e => e.Kind == VoiceEventKind.Confirm && e.Speak);
    }

    [Fact]
    public async Task ADeclinedChangeSendsNothing()
    {
        var (a, sw, host, events) = await Setup("SAY: ok\nRUN: interface Vlan30\nRUN: no shutdown\nNEXT: done");
        host.Approve = false;
        await a.HandleAsync(Said("bring up vlan 30"));
        Assert.Empty(sw.Received);
        Assert.Contains(events, e => e.Kind == VoiceEventKind.Declined);
    }

    [Fact]
    public async Task SpokenYesAnswersAPendingChange()
    {
        var (a, sw, host, _) = await Setup("SAY: Enabling port 3.\nRUN: interface GigabitEthernet1/0/3\nRUN: no shutdown\nNEXT: done");
        host.HoldConfirm = new TaskCompletionSource<bool>(); // the click never comes
        var work = a.HandleAsync(Said("bring up port 3"));
        for (int i = 0; i < 100 && !a.AwaitingConfirmation; i++) await Task.Delay(20);
        Assert.True(a.AwaitingConfirmation);
        await a.HandleAsync(Said("हाँ", "Hindi"));
        await work;
        Assert.Contains("no shutdown", sw.Received);
    }

    [Fact]
    public async Task DestructiveCommandsNeedAClickNotJustAYes()
    {
        var (a, sw, host, events) = await Setup("SAY: Reloading.\nRUN: reload\nNEXT: done");
        host.HoldConfirm = new TaskCompletionSource<bool>();
        var work = a.HandleAsync(Said("reboot the switch"));
        for (int i = 0; i < 100 && !a.AwaitingConfirmation; i++) await Task.Delay(20);
        Assert.Equal(VoiceRisk.Destructive, Assert.Single(host.Asked).Risk);
        Assert.True(a.Answer("yes"));
        Assert.True(a.AwaitingConfirmation); // still waiting - voice alone isn't enough
        host.HoldConfirm.SetResult(false);
        await work;
        Assert.DoesNotContain("reload", sw.Received);
    }

    [Fact]
    public async Task ReadOnlyQuestionsRunWithoutAsking_PageThroughMore_AndAreAnswered()
    {
        var (a, sw, host, events) = await Setup(
            "SAY: Checking.\nRUN: show ip route\nNEXT: read",
            "SAY: The default route goes via 10.0.0.1.\nNEXT: done");
        await a.HandleAsync(Said("where does the default route go?"));
        Assert.Empty(host.Asked);
        Assert.Equal(["show ip route"], sw.Received);
        var output = events.First(e => e.Kind == VoiceEventKind.Output).Detail!;
        Assert.Contains("0.0.0.0/0", output);         // the pager was paged through
        Assert.DoesNotContain("--More--", output);
        Assert.Contains(events, e => e.Kind == VoiceEventKind.Say && e.Speak && e.Text.Contains("10.0.0.1"));
        var brain = (ScriptedBrain)a.Brain!;
        Assert.Contains("0.0.0.0/0", brain.Calls[1][^1].Content); // the answer was based on the real output
    }

    [Fact]
    public async Task ARejectedCommandStopsThePlan_AndTheModelCorrectsIt()
    {
        var (a, sw, host, events) = await Setup(
            "SAY: Done.\nRUN: interface GigabitEthernet1/0/3\nRUN: bogus command\nRUN: no shutdown\nNEXT: done",
            "SAY: Fixed.\nRUN: interface GigabitEthernet1/0/3\nRUN: no shutdown\nNEXT: done");
        await a.HandleAsync(Said("bring up port 3"));
        // The first attempt stopped at the rejected line (its "no shutdown" was never sent), went back to EXEC mode,
        // and the corrected plan started cleanly from there.
        Assert.Equal(["configure terminal", "interface GigabitEthernet1/0/3", "bogus command", "end",
                      "configure terminal", "interface GigabitEthernet1/0/3", "no shutdown", "end"], sw.Received);
        Assert.Contains(events, e => e.Kind == VoiceEventKind.Error && e.Text.Contains("Invalid input"));
    }

    [Fact]
    public async Task YesToTheAssistantsOwnOfferIsTheApproval_ButOnlyForThatOffer()
    {
        var (a, sw, host, _) = await Setup(
            "SAY: Gi1/0/2 is shut down by configuration. Shall I bring it up?\nNEXT: done",
            "SAY: Bringing it up.\nRUN: interface GigabitEthernet1/0/2\nRUN: no shutdown\nNEXT: done",
            "SAY: Shutting it.\nRUN: interface GigabitEthernet1/0/2\nRUN: shutdown\nNEXT: done");
        await a.HandleAsync(Said("why is gig 1/0/2 down?"));
        await a.HandleAsync(Said("yes"));
        Assert.Empty(host.Asked); // the yes answered the offer - no second question
        Assert.Contains("no shutdown", sw.Received);
        await a.HandleAsync(Said("now shut it again"));
        Assert.Single(host.Asked); // a new change without an offer is asked about as usual
    }

    [Fact]
    public async Task YesToAnOfferNeverApprovesDestructiveCommands()
    {
        var (a, sw, host, _) = await Setup(
            "SAY: The switch looks hung. Shall I reload it?\nNEXT: done",
            "SAY: Reloading.\nRUN: reload\nNEXT: done");
        host.Approve = false;
        await a.HandleAsync(Said("the switch is stuck"));
        await a.HandleAsync(Said("yes"));
        Assert.Equal(VoiceRisk.Destructive, Assert.Single(host.Asked).Risk);
        Assert.DoesNotContain("reload", sw.Received);
    }

    [Fact]
    public async Task LiteralCommandsRunWithoutTheModel()
    {
        var (a, sw, _, _) = await Setup("SAY: should not be asked\nNEXT: done");
        await a.HandleAsync(Said("show ip route"));
        Assert.Equal(["show ip route"], sw.Received);
        Assert.Empty(((ScriptedBrain)a.Brain!).Calls);
    }

    [Fact]
    public async Task OpensSessionsAndTools_ByFuzzyName()
    {
        var (a, _, host, _) = await Setup("SAY: Opening the firewall.\nCONNECT: fgt 01\nTOOL: pathtrace 8.8.8.8\nNEXT: done");
        await a.HandleAsync(Said("open the firewall and trace to 8.8.8.8"));
        Assert.Equal(["FGT-01"], host.Connected);
        Assert.Equal(["pathtrace 8.8.8.8"], host.Tools2);
    }

    [Fact]
    public async Task TheConversationIsRememberedForFollowUps()
    {
        var (a, _, _, _) = await Setup(
            "SAY: Port 3 is up.\nRUN: interface GigabitEthernet1/0/3\nRUN: no shutdown\nNEXT: done",
            "SAY: Undoing.\nRUN: interface GigabitEthernet1/0/3\nRUN: shutdown\nNEXT: done");
        await a.HandleAsync(Said("bring up port 3"));
        await a.HandleAsync(Said("undo that"));
        var second = ((ScriptedBrain)a.Brain!).Calls[1];
        Assert.Contains(second, m => m.Role == "assistant" && m.Content.Contains("RUN: no shutdown"));
        Assert.Contains(second, m => m.Role == "user" && m.Content.Contains("bring up port 3"));
    }

    [Fact]
    public async Task WithoutABrainOnlyLiteralCommandsWork()
    {
        var (a, sw, _, events) = await Setup();
        a.Brain = null;
        await a.HandleAsync(Said("bring up port 3"));
        Assert.Empty(sw.Received);
        Assert.Contains(events, e => e.Kind == VoiceEventKind.Say && e.Text.Contains("language model"));
    }

    [Fact]
    public void RecognizesRejectionsFromManyPlatforms()
    {
        Assert.NotNull(VoiceAssistant.Rejected("   ^\n% Invalid input detected at '^' marker."));
        Assert.NotNull(VoiceAssistant.Rejected("syntax error, expecting <command>."));
        Assert.NotNull(VoiceAssistant.Rejected("Command fail. Return code -61"));
        Assert.NotNull(VoiceAssistant.Rejected("bash: shwo: command not found"));
        Assert.Null(VoiceAssistant.Rejected("GigabitEthernet1/0/2 is down, line protocol is down"));
    }

    [Fact]
    public void WhisperNoiseIsIgnored()
    {
        Assert.True(VoiceListener.IsNoise("Thank you."));
        Assert.True(VoiceListener.IsNoise(" ... "));
        Assert.False(VoiceListener.IsNoise("show ip route"));
    }
}
