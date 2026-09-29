using System.Text;
using OmlTerminal.Core.Macros;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Tests;

public class MacroTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "oml-macros-" + Guid.NewGuid() + ".json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static List<MacroStep> Record(params string[] chunks)
    {
        var r = new MacroRecorder();
        foreach (var c in chunks) r.OnInput(Encoding.UTF8.GetBytes(c));
        return r.Finish();
    }

    [Fact]
    public void RecordsOneStepPerEnteredLine()
    {
        var steps = Record("conf t\r", "int gi0/1\r", "no shut\r");
        Assert.Equal(["conf t", "int gi0/1", "no shut"], steps.Select(s => s.Text));
        Assert.All(steps, s => Assert.True(s.Enter));
    }

    [Fact]
    public void KeystrokesArrivingOneCharacterAtATimeAreJoined()
        => Assert.Equal(["show ip int brief"], Record("show ".Select(c => c.ToString()).Concat(["i", "p", " ", "int brief", "\r"]).ToArray()).Select(s => s.Text));

    [Fact]
    public void BackspaceEditsPendingLine()
        => Assert.Equal(["show run"], Record("show ruy", "\x7f", "n\r").Select(s => s.Text));

    [Fact]
    public void ArrowKeysAndOtherEscapeSequencesAreDropped()
        => Assert.Equal(["ab"], Record("a", "\x1b[A", "\x1b[3~", "\x1bOP", "b\r").Select(s => s.Text));

    [Fact]
    public void TrailingTextWithoutEnterBecomesFinalStepWithoutEnter()
    {
        var steps = Record("ping 10.0.0.1\r", "term len 0");
        Assert.Equal(2, steps.Count);
        Assert.False(steps[1].Enter);
    }

    [Fact]
    public void EmptyEnterIsRecorded_BecauseItMattersAtMorePrompts()
        => Assert.Equal(["", "x"], Record("\r", "x\r").Select(s => s.Text));

    [Fact]
    public void Validation()
    {
        Assert.NotEmpty(new Macro { Name = "", Steps = [new()] }.Validate());
        Assert.NotEmpty(new Macro { Name = "m" }.Validate());
        Assert.NotEmpty(new Macro { Name = "m", Steps = [new()], DelayMs = -1 }.Validate());
        Assert.Empty(new Macro { Name = "m", Steps = [new()] }.Validate());
    }

    [Fact]
    public void StoreRoundTrips_AndRejectsInvalidBeforeWriting()
    {
        var store = new JsonMacroStore(_path);
        store.Save([new Macro { Name = "up", Steps = [new() { Text = "no shut" }] }]);
        Assert.Equal("up", Assert.Single(store.Load()).Name);
        Assert.Throws<InvalidOperationException>(() => store.Save([new Macro { Name = "", Steps = [new()] }]));
        Assert.Equal("up", Assert.Single(store.Load()).Name);
    }

    private sealed class FakeTransport : ITerminalTransport
    {
        public List<byte> Sent { get; } = new();
        public event Action<byte[]>? DataReceived { add { } remove { } }
        public event Action<string?>? Closed { add { } remove { } }
        public Task ConnectAsync(int c, int r, CancellationToken ct = default) => Task.CompletedTask;
        public void Write(ReadOnlySpan<byte> d) => Sent.AddRange(d.ToArray());
        public void Resize(int c, int r) { }
        public void Close() { }
        public void Dispose() { }
        public string Text => Encoding.UTF8.GetString(Sent.ToArray());
    }

    private static async Task<(TerminalSession, FakeTransport)> Connected()
    {
        var t = new FakeTransport();
        var s = new TerminalSession(t, new XTermEngine(80, 24));
        await s.ConnectAsync(80, 24);
        return (s, t);
    }

    [Fact]
    public async Task PlayerSendsEachStepWithEnter()
    {
        var (s, t) = await Connected();
        await MacroPlayer.PlayAsync(s, new Macro { Name = "m", DelayMs = 0, Steps = [new() { Text = "conf t" }, new() { Text = "end", Enter = false }] });
        Assert.Equal("conf t\rend", t.Text);
    }

    [Fact]
    public async Task PlayerCanBeCancelled_AndStopsWhenDisconnected()
    {
        var (s, t) = await Connected();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MacroPlayer.PlayAsync(s, new Macro { Name = "m", Steps = [new() { Text = "x" }] }, cts.Token));
        Assert.Empty(t.Sent);

        s.Close();
        await MacroPlayer.PlayAsync(s, new Macro { Name = "m", DelayMs = 0, Steps = [new() { Text = "y" }] });
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task MultiExecSendsToConnectedSessionsOnly()
    {
        var (a, ta) = await Connected();
        var (b, tb) = await Connected();
        var t3 = new FakeTransport();
        var notConnected = new TerminalSession(t3, new XTermEngine(80, 24));

        int n = MultiExecutor.Broadcast([a, b, notConnected], "show clock");
        Assert.Equal(2, n);
        Assert.Equal("show clock\r", ta.Text);
        Assert.Equal("show clock\r", tb.Text);
        Assert.Empty(t3.Sent);
    }

    [Fact]
    public async Task InputSentEventCarriesWhatWasSent_ForTheRecorder()
    {
        var (s, _) = await Connected();
        var rec = new MacroRecorder();
        s.InputSent += data => rec.OnInput(data);
        s.Send("show ver\r"u8);
        Assert.Equal("show ver", Assert.Single(rec.Finish()).Text);
    }
}

public class HighlightTests
{
    private static string Text(TerminalRow r) => string.Concat(r.Runs.Select(x => x.Text));

    private static TerminalRow RowOf(string text, int fg = 0xD4D4D4, int bg = 0x0C0C0C)
        => new([new TextRun(0, text, text.Length, fg, bg, false, false, false)]);

    [Fact]
    public void ColorsInterfaceStateAndAddresses_WithoutChangingTextOrColumns()
    {
        var row = RowOf("Gi1/0/1 is up, line protocol is down 10.1.1.1/24 0050.7966.6800");
        var result = HighlightRuleSet.Default.Apply(row, 0xD4D4D4);

        Assert.Equal(Text(row), Text(result));
        var byText = result.Runs.Where(r => r.Fg != 0xD4D4D4).ToDictionary(r => r.Text, r => r);
        Assert.Equal(0x38BDF8, byText["Gi1/0/1"].Fg);
        Assert.Equal(0x4ADE80, byText["up"].Fg);
        Assert.Equal(0xF87171, byText["down"].Fg);
        Assert.True(byText["down"].Bold);
        Assert.Equal(0xFB923C, byText["10.1.1.1/24"].Fg);
        Assert.Equal(0xE879F9, byText["0050.7966.6800"].Fg);

        int col = 0;
        foreach (var r in result.Runs) { Assert.Equal(col, r.Column); Assert.Equal(r.Text.Length, r.Cells); col += r.Cells; }
    }

    [Fact]
    public void LeavesTextAloneThatTheRemoteAlreadyColored()
    {
        var row = RowOf("interface is down", fg: 0x123456);
        Assert.Same(row, HighlightRuleSet.Default.Apply(row, 0xD4D4D4));
    }

    [Fact]
    public void WordBoundariesPreventFalsePositives()
    {
        var row = RowOf("uptime is 3 days, download, groupname");
        Assert.Same(row, HighlightRuleSet.Default.Apply(row, 0xD4D4D4));
    }

    [Fact]
    public void RowWithNoMatchesIsReturnedUnchanged()
    {
        var row = RowOf("hello world");
        Assert.Same(row, HighlightRuleSet.Default.Apply(row, 0xD4D4D4));
    }

    [Fact]
    public void EngineAppliesHighlightsButNotOnAlternateScreen()
    {
        var e = new XTermEngine(60, 5) { Highlights = HighlightRuleSet.Default };
        e.Write("port is down\r\n"u8);
        var s = e.GetSnapshot();
        Assert.Contains(s.VisibleRows[0].Runs, r => r.Text == "down" && r.Fg == 0xF87171);

        e.Write("\x1b[?1049h\x1b[Hport is down"u8);
        var alt = e.GetSnapshot();
        Assert.DoesNotContain(alt.VisibleRows[0].Runs, r => r.Fg == 0xF87171);
    }

    [Fact]
    public void EngineWithoutHighlightsIsUntouched()
    {
        var e = new XTermEngine(60, 5);
        e.Write("port is down"u8);
        Assert.DoesNotContain(e.GetSnapshot().VisibleRows[0].Runs, r => r.Fg == 0xF87171);
    }
}
