using System.Text;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Transports;
using XTermTerminal = XTerm.Terminal;

namespace OmlTerminal.Core.Tests;

public class XTermBehaviourTests
{
    [Fact]
    public void BufferChanged_FiresOnlyOnAltScreenSwitch_NotOnEveryWrite()
    {
        // Throwaway verification of the documented gotcha: this is why the engine raises its own Changed event.
        var t = new XTermTerminal(new XTerm.Options.TerminalOptions { Cols = 20, Rows = 5 });
        int fired = 0;
        t.BufferChanged += (_, _) => fired++;

        t.Write("hello\r\nworld");
        t.Write("more");
        Assert.Equal(0, fired);

        t.Write("\x1b[?1049h");
        Assert.Equal(1, fired);
    }
}

public class XTermEngineTests
{
    private static string RowText(TerminalSnapshot s, int row)
        => string.Concat(s.VisibleRows[row].Runs.Select(r => r.Text)).TrimEnd();

    [Fact]
    public void WritesTextIntoVisibleRows_AndPlacesCursor()
    {
        var e = new XTermEngine(20, 5);
        e.Write("hello\r\nworld"u8);
        var s = e.GetSnapshot();
        Assert.Equal("hello", RowText(s, 0));
        Assert.Equal("world", RowText(s, 1));
        Assert.Equal(new CursorState(5, 1, true), s.Cursor);
    }

    [Fact]
    public void ChangedFiresOnEveryWrite()
    {
        var e = new XTermEngine(20, 5);
        int n = 0;
        e.Changed += () => n++;
        e.Write("a"u8); e.Write("b"u8); e.WriteLocal("c"); e.Resize(30, 6);
        Assert.Equal(4, n);
    }

    [Fact]
    public void ResolvesPaletteAndTrueColor()
    {
        var e = new XTermEngine(20, 5);
        e.Write("\x1b[31mR\x1b[0m \x1b[38;2;1;2;3mT"u8);
        var runs = e.GetSnapshot().VisibleRows[0].Runs;
        var red = runs.First(r => r.Text.StartsWith('R'));
        Assert.NotEqual(e.GetSnapshot().DefaultForeground, red.Fg);
        Assert.Equal(0x010203, runs.Last().Fg);
    }

    [Fact]
    public void InverseSwapsColors()
    {
        var e = new XTermEngine(20, 5);
        e.Write("\x1b[7mX"u8);
        var s = e.GetSnapshot();
        var run = s.VisibleRows[0].Runs[0];
        Assert.Equal(s.DefaultBackground, run.Fg);
        Assert.Equal(s.DefaultForeground, run.Bg);
    }

    [Fact]
    public void Utf8CharSplitAcrossWrites_IsNotCorrupted()
    {
        var e = new XTermEngine(20, 5);
        var bytes = Encoding.UTF8.GetBytes("é");   // 2 bytes
        e.Write(bytes.AsSpan(0, 1));
        e.Write(bytes.AsSpan(1, 1));
        Assert.Equal("é", RowText(e.GetSnapshot(), 0));
    }

    [Fact]
    public void DeviceAttributeQuery_ProducesResponseBytes()
    {
        var e = new XTermEngine(20, 5);
        var got = new List<byte>();
        e.Response += b => got.AddRange(b);
        e.Write("\x1b[c"u8);
        Assert.NotEmpty(got);
        Assert.Equal((byte)0x1b, got[0]);
    }

    [Fact]
    public void AlternateScreenAndApplicationCursorKeys()
    {
        var e = new XTermEngine(20, 5);
        Assert.False(e.ApplicationCursorKeys);
        e.Write("\x1b[?1h"u8);
        Assert.True(e.ApplicationCursorKeys);
    }

    [Fact]
    public void ScrollbackViewport_HidesCursorWhenScrolledBack()
    {
        var e = new XTermEngine(20, 3);
        for (int i = 0; i < 10; i++) e.WriteLocal($"line{i}\r\n");
        e.ScrollViewport(-2);
        Assert.False(e.GetSnapshot().Cursor.Visible);
        e.ScrollViewport(100);
        Assert.True(e.GetSnapshot().Cursor.Visible);
    }

    [Fact]
    public void ResizeChangesDimensions()
    {
        var e = new XTermEngine(80, 24);
        e.Resize(100, 30);
        var s = e.GetSnapshot();
        Assert.Equal((100, 30), (s.Cols, s.Rows));
        Assert.Equal(30, s.VisibleRows.Count);
    }

    [Fact]
    public void Snapshot_TopRowTracksScrollPosition()
    {
        var e = new XTermEngine(20, 3, scrollback: 100);
        for (int i = 0; i < 10; i++) e.WriteLocal($"line{i}\r\n");
        Assert.True(e.GetSnapshot().TopRow > 0); // auto-followed to the bottom
        e.ScrollToRow(0);
        Assert.Equal(0, e.GetSnapshot().TopRow);
    }

    [Fact]
    public void Find_MatchesAcrossScrollback_NotJustTheVisibleViewport()
    {
        var e = new XTermEngine(20, 3, scrollback: 100);
        for (int i = 0; i < 10; i++) e.WriteLocal($"needle{i}\r\n");
        e.ScrollToBottom();

        var matches = e.Find("needle", matchCase: false);
        Assert.Equal(10, matches.Count);
        // The earliest match has long since scrolled out of the 3-row viewport, but Find still sees it.
        Assert.True(matches[0].Row < e.GetSnapshot().TopRow);
    }

    [Fact]
    public void Find_IsCaseInsensitiveByDefault_AndCaseSensitiveWhenAsked()
    {
        var e = new XTermEngine(20, 3);
        e.WriteLocal("Hello World\r\n");
        Assert.Single(e.Find("hello", matchCase: false));
        Assert.Empty(e.Find("hello", matchCase: true));
        Assert.Single(e.Find("Hello", matchCase: true));
    }

    [Fact]
    public void Find_ReturnsCorrectColumnAndLength()
    {
        var e = new XTermEngine(20, 3);
        e.WriteLocal("show interface\r\n");
        var m = Assert.Single(e.Find("interface", matchCase: false));
        Assert.Equal(5, m.Column);
        Assert.Equal(9, m.Length);
    }

    [Fact]
    public void Find_EmptyQuery_ReturnsNoMatches()
    {
        var e = new XTermEngine(20, 3);
        e.WriteLocal("anything\r\n");
        Assert.Empty(e.Find("", matchCase: false));
    }
}

public class KeyEncoderTests
{
    [Theory]
    [InlineData(TerminalKey.Enter, "\r")]
    [InlineData(TerminalKey.Backspace, "\x7f")]
    [InlineData(TerminalKey.Tab, "\t")]
    [InlineData(TerminalKey.Escape, "\x1b")]
    [InlineData(TerminalKey.Up, "\x1b[A")]
    [InlineData(TerminalKey.Down, "\x1b[B")]
    [InlineData(TerminalKey.Right, "\x1b[C")]
    [InlineData(TerminalKey.Left, "\x1b[D")]
    [InlineData(TerminalKey.Delete, "\x1b[3~")]
    [InlineData(TerminalKey.Home, "\x1b[H")]
    [InlineData(TerminalKey.End, "\x1b[F")]
    [InlineData(TerminalKey.PageUp, "\x1b[5~")]
    public void NormalMode(TerminalKey k, string expected)
        => Assert.Equal(expected, Encoding.ASCII.GetString(KeyEncoder.Encode(k)!));

    [Fact]
    public void ApplicationCursorMode_UsesSs3()
        => Assert.Equal("\x1bOA", Encoding.ASCII.GetString(KeyEncoder.Encode(TerminalKey.Up, true)!));

    [Fact]
    public void CtrlLetters()
    {
        Assert.Equal([0x01], KeyEncoder.EncodeCtrlLetter('a'));
        Assert.Equal([0x03], KeyEncoder.EncodeCtrlLetter('C'));
        Assert.Equal([0x1A], KeyEncoder.EncodeCtrlLetter('z'));
        Assert.Null(KeyEncoder.EncodeCtrlLetter('1'));
    }

    [Fact]
    public void TextAndAlt()
    {
        Assert.Equal([(byte)'a'], KeyEncoder.EncodeText("a"));
        Assert.Equal([0x1b, (byte)'b'], KeyEncoder.EncodeText("b", alt: true));
    }
}

public class TerminalSessionTests
{
    private sealed class FakeTransport : ITerminalTransport
    {
        public bool ConnectShouldThrow;
        public List<byte> Sent { get; } = new();
        public (int, int) LastResize;
        public bool CloseCalled;
        public event Action<byte[]>? DataReceived;
        public event Action<string?>? Closed;
        public Task ConnectAsync(int c, int r, CancellationToken ct = default) =>
            ConnectShouldThrow ? Task.FromException(new IOException("refused")) : Task.CompletedTask;
        public void Write(ReadOnlySpan<byte> d) => Sent.AddRange(d.ToArray());
        public void Resize(int c, int r) => LastResize = (c, r);
        public void Close() { CloseCalled = true; Closed?.Invoke(null); }
        public void Dispose() { }
        public void Push(string s) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(s));
        public void Drop(string? err) => Closed?.Invoke(err);
    }

    [Fact]
    public async Task DataFlowsToEngine_AndRaisesChangedPerWrite()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(20, 5));
        await s.ConnectAsync(20, 5);
        int changes = 0;
        s.Changed += () => changes++;
        t.Push("hi");
        t.Push("!");
        Assert.Equal(2, changes);
        Assert.Contains("hi!", string.Concat(s.Engine.GetSnapshot().VisibleRows[0].Runs.Select(r => r.Text)));
    }

    [Fact]
    public async Task EngineResponsesGoBackToTransport()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(20, 5));
        await s.ConnectAsync(20, 5);
        t.Push("\x1b[c");
        Assert.NotEmpty(t.Sent);
    }

    [Fact]
    public async Task ResizePropagatesToEngineAndTransport_OnlyWhenChanged()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(80, 24));
        await s.ConnectAsync(80, 24);
        t.LastResize = default;
        s.Resize(80, 24);
        Assert.Equal(default, t.LastResize);
        s.Resize(120, 40);
        Assert.Equal((120, 40), t.LastResize);
        Assert.Equal(120, s.Engine.Cols);
    }

    [Fact]
    public async Task RemoteDrop_ShowsMessageAndRaisesEnded()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(40, 5));
        string? ended = "unset";
        s.Ended += e => ended = e;
        await s.ConnectAsync(40, 5);
        t.Drop("reset by peer");
        Assert.Equal("reset by peer", ended);
        Assert.False(s.IsConnected);
        var text = string.Join("|", s.Engine.GetSnapshot().VisibleRows.Select(r => string.Concat(r.Runs.Select(x => x.Text))));
        Assert.Contains("reset by peer", text);
    }

    [Fact]
    public async Task SendIsIgnoredAfterClose_AndCloseReachesTransport()
    {
        var t = new FakeTransport();
        var s = new TerminalSession(t, new XTermEngine(20, 5));
        await s.ConnectAsync(20, 5);
        s.Close();
        s.Send("x"u8);
        Assert.True(t.CloseCalled);
        Assert.Empty(t.Sent);
        s.Dispose();
    }

    [Fact]
    public async Task Reconnect_RetriesWithBackoff_AndSucceedsOnALaterAttempt()
    {
        var t1 = new FakeTransport();
        using var s = new TerminalSession(t1, new XTermEngine(20, 5)) { ReconnectDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)] };
        await s.ConnectAsync(20, 5);

        int factoryCalls = 0;
        FakeTransport? t2 = null;
        s.ReconnectTransportFactory = () =>
        {
            factoryCalls++;
            if (factoryCalls == 1) return new FakeTransport { ConnectShouldThrow = true };
            t2 = new FakeTransport();
            return t2;
        };
        bool reconnected = false;
        s.Reconnected += () => reconnected = true;
        string? ended = null;
        s.Ended += e => ended = e;

        t1.Drop("link down");
        for (int i = 0; i < 200 && !reconnected; i++) await Task.Delay(10);

        Assert.True(reconnected);
        Assert.Null(ended); // must not fire once a retry succeeds
        Assert.True(s.IsConnected);
        Assert.Equal(2, factoryCalls);
        Assert.Same(t2, s.Transport);
    }

    [Fact]
    public async Task Reconnect_GivesUpAndRaisesEnded_AfterEveryAttemptFails()
    {
        var t1 = new FakeTransport();
        using var s = new TerminalSession(t1, new XTermEngine(20, 5)) { ReconnectDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)] };
        await s.ConnectAsync(20, 5);
        s.ReconnectTransportFactory = () => new FakeTransport { ConnectShouldThrow = true };

        string? ended = "unset";
        s.Ended += e => ended = e;
        t1.Drop("link down");
        for (int i = 0; i < 200 && ended == "unset"; i++) await Task.Delay(10);

        Assert.Equal("link down", ended);
        Assert.False(s.IsConnected);
    }

    /// <summary>Mirrors real transports' actual Close()/Dispose() shape (SshTransport, LocalTransport): both
    /// end by calling a single idempotency-guarded RaiseClosed, including for a Dispose() on an attempt that
    /// never connected. The shared FakeTransport above has a no-op Dispose(), so it can't reproduce the bug
    /// this exists to catch - a failed reconnect attempt's own disposal re-entering OnTransportClosed.</summary>
    private sealed class RealisticFakeTransport : ITerminalTransport
    {
        public bool ConnectShouldThrow;
        private bool _raised;
        public event Action<byte[]>? DataReceived;
        public event Action<string?>? Closed;
        public Task ConnectAsync(int c, int r, CancellationToken ct = default) =>
            ConnectShouldThrow ? Task.FromException(new IOException("refused")) : Task.CompletedTask;
        public void Write(ReadOnlySpan<byte> d) { }
        public void Resize(int c, int r) { }
        public void Close() => RaiseClosed(null);
        public void Dispose() => Close();
        public void Drop(string? err) => RaiseClosed(err);
        private void RaiseClosed(string? error) { if (!_raised) { _raised = true; Closed?.Invoke(error); } }
    }

    [Fact]
    public async Task Reconnect_FailedAttemptDisposal_DoesNotPrematurelyRaiseEnded()
    {
        var t1 = new RealisticFakeTransport();
        using var s = new TerminalSession(t1, new XTermEngine(20, 5)) { ReconnectDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)] };
        await s.ConnectAsync(20, 5);

        int endedCount = 0;
        string? lastEnded = null;
        s.Ended += e => { endedCount++; lastEnded = e; };
        s.ReconnectTransportFactory = () => new RealisticFakeTransport { ConnectShouldThrow = true };

        t1.Drop("link down");
        for (int i = 0; i < 200 && endedCount == 0; i++) await Task.Delay(10);

        // Two attempts both fail and get disposed before give-up; each disposal raises Closed(null) on that
        // attempt's own transport, which - without the fix - re-enters OnTransportClosed and fires Ended early.
        Assert.Equal(1, endedCount);
        Assert.Equal("link down", lastEnded);
    }

    [Fact]
    public async Task Reconnect_FailedThenSucceeded_NeverRaisesEnded_UsingRealisticTransport()
    {
        var t1 = new RealisticFakeTransport();
        using var s = new TerminalSession(t1, new XTermEngine(20, 5)) { ReconnectDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)] };
        await s.ConnectAsync(20, 5);

        int endedCount = 0;
        s.Ended += _ => endedCount++;
        bool reconnected = false;
        s.Reconnected += () => reconnected = true;
        int factoryCalls = 0;
        s.ReconnectTransportFactory = () =>
        {
            factoryCalls++;
            return new RealisticFakeTransport { ConnectShouldThrow = factoryCalls == 1 }; // first attempt fails, second succeeds
        };

        t1.Drop("link down");
        for (int i = 0; i < 200 && !reconnected; i++) await Task.Delay(10);

        Assert.True(reconnected);
        Assert.Equal(0, endedCount); // the first failed attempt's disposal must not have fired Ended at all
        Assert.True(s.IsConnected);
    }

    [Fact]
    public async Task Reconnect_NotAttempted_WhenFactoryIsNull()
    {
        // Matches the pre-reconnect behavior exactly: Ended fires synchronously, no background retry loop at all.
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(20, 5));
        await s.ConnectAsync(20, 5);
        string? ended = "unset";
        s.Ended += e => ended = e;
        t.Drop("boom");
        Assert.Equal("boom", ended);
    }

    [Fact]
    public async Task Close_DuringReconnectWait_StopsIt_WithoutCallingTheFactory()
    {
        var t1 = new FakeTransport();
        var s = new TerminalSession(t1, new XTermEngine(20, 5)) { ReconnectDelays = [TimeSpan.FromSeconds(5)] };
        await s.ConnectAsync(20, 5);
        int factoryCalls = 0;
        s.ReconnectTransportFactory = () => { factoryCalls++; return new FakeTransport(); };

        t1.Drop("boom");
        await Task.Delay(50); // let the loop start its (long) wait
        s.Close();
        await Task.Delay(100);

        Assert.Equal(0, factoryCalls);
        s.Dispose();
    }

    [Fact]
    public async Task OutputReceived_SurvivesTheTransportSwap_AfterAReconnect()
    {
        var t1 = new FakeTransport();
        using var s = new TerminalSession(t1, new XTermEngine(20, 5)) { ReconnectDelays = [TimeSpan.FromMilliseconds(1)] };
        await s.ConnectAsync(20, 5);
        var received = new List<string>();
        s.OutputReceived += data => received.Add(Encoding.UTF8.GetString(data));

        FakeTransport? t2 = null;
        bool reconnected = false;
        s.Reconnected += () => reconnected = true;
        s.ReconnectTransportFactory = () => { t2 = new FakeTransport(); return t2; };
        t1.Drop("boom");
        for (int i = 0; i < 200 && !reconnected; i++) await Task.Delay(10);

        Assert.NotNull(t2);
        t2!.Push("post-reconnect data");
        Assert.Contains(received, s => s == "post-reconnect data");
    }
}

public class SessionLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oml-logtests-" + Guid.NewGuid());

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class FakeTransport : ITerminalTransport
    {
        public event Action<byte[]>? DataReceived;
        public event Action<string?>? Closed;
        public Task ConnectAsync(int c, int r, CancellationToken ct = default) => Task.CompletedTask;
        public void Write(ReadOnlySpan<byte> d) { }
        public void Resize(int c, int r) { }
        public void Close() { }
        public void Dispose() { }
        public void Push(string s) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(s));
    }

    [Fact]
    public void CapturesOutput_AndStripsAnsiFromTheFile()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(80, 24));
        var logger = SessionLogger.Start(s, _dir, "core-sw1");

        t.Push("\x1b[32mInterface up\x1b[0m\r\n");
        logger.Dispose();

        // Equality (not a substring/contains check - xUnit resolves DoesNotContain(string,string) ambiguously
        // against a single-control-character needle) is also the stronger assertion: it already proves no
        // escape bytes of any kind survived, not just this particular one.
        var text = File.ReadAllText(logger.Path);
        Assert.Equal("Interface up\r\n", text);
    }

    [Fact]
    public void StopsCapturing_AfterDispose()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(80, 24));
        var logger = SessionLogger.Start(s, _dir, "core-sw1");
        t.Push("before\r\n");
        logger.Dispose();
        t.Push("after\r\n");

        var text = File.ReadAllText(logger.Path);
        Assert.Contains("before", text);
        Assert.DoesNotContain("after", text);
    }

    [Fact]
    public void SanitizesInvalidFileNameCharactersInSessionName()
    {
        var t = new FakeTransport();
        using var s = new TerminalSession(t, new XTermEngine(80, 24));
        using var logger = SessionLogger.Start(s, _dir, "lab:core/sw1");
        Assert.True(File.Exists(logger.Path));
    }
}

public class AnsiStripperTests
{
    [Fact]
    public void RemovesCsiSequences()
        => Assert.Equal("hello"u8.ToArray(), AnsiStripper.Strip(Encoding.UTF8.GetBytes("\x1b[1mhello\x1b[0m")));

    [Fact]
    public void RemovesOscSequences_TerminatedByBelOrEscBackslash()
    {
        Assert.Equal("hi"u8.ToArray(), AnsiStripper.Strip(Encoding.UTF8.GetBytes("\x1b]0;title\x07hi")));
        Assert.Equal("hi"u8.ToArray(), AnsiStripper.Strip(Encoding.UTF8.GetBytes("\x1b]0;title\x1b\\hi")));
    }

    [Fact]
    public void PassesPlainTextThrough_Unmodified()
        => Assert.Equal("plain text\r\n"u8.ToArray(), AnsiStripper.Strip(Encoding.UTF8.GetBytes("plain text\r\n")));
}
