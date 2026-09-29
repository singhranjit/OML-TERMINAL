using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Tests;

public class TelnetIacParserTests
{
    [Fact]
    public void PlainDataPassesThrough()
    {
        var r = new TelnetIacParser().Parse("hello"u8);
        Assert.Equal("hello"u8.ToArray(), r.Data.ToArray());
        Assert.Empty(r.Events);
    }

    [Fact]
    public void ParsesNegotiation()
    {
        var r = new TelnetIacParser().Parse([255, 251, 1, (byte)'x']);
        Assert.Equal([(byte)'x'], r.Data.ToArray());
        var e = Assert.Single(r.Events);
        Assert.Equal(TelnetEventKind.Negotiation, e.Kind);
        Assert.Equal(Telnet.WILL, e.Verb);
        Assert.Equal(Telnet.OptEcho, e.Option);
    }

    [Fact]
    public void IncompleteIacSplitAcrossTwoReads()
    {
        var p = new TelnetIacParser();
        var first = p.Parse([(byte)'a', 255]);                 // ends mid-IAC
        Assert.Equal([(byte)'a'], first.Data.ToArray());
        Assert.Empty(first.Events);

        var second = p.Parse([253, 31, (byte)'b']);           // DO NAWS arrives later
        Assert.Equal([(byte)'b'], second.Data.ToArray());
        var e = Assert.Single(second.Events);
        Assert.Equal(Telnet.DO, e.Verb);
        Assert.Equal(Telnet.OptNaws, e.Option);
    }

    [Fact]
    public void IncompleteVerbSplitBeforeOptionByte()
    {
        var p = new TelnetIacParser();
        Assert.Empty(p.Parse([255, 251]).Events);
        var e = Assert.Single(p.Parse([3]).Events);
        Assert.Equal(Telnet.OptSuppressGoAhead, e.Option);
    }

    [Fact]
    public void SubnegotiationNotYetClosedBySe_EmitsNothingUntilClosed()
    {
        var p = new TelnetIacParser();
        var open = p.Parse([255, 250, 24, 1]);                 // IAC SB TTYPE SEND ... no SE yet
        Assert.Empty(open.Events);
        Assert.Empty(open.Data);

        var closed = p.Parse([255, 240, (byte)'z']);           // IAC SE, then data
        Assert.Equal([(byte)'z'], closed.Data.ToArray());
        var e = Assert.Single(closed.Events);
        Assert.Equal(TelnetEventKind.Subnegotiation, e.Kind);
        Assert.Equal(Telnet.OptTerminalType, e.Option);
        Assert.Equal([Telnet.TtypeSend], e.Payload);
    }

    [Fact]
    public void SubnegotiationSplitBetweenIacAndSe()
    {
        var p = new TelnetIacParser();
        Assert.Empty(p.Parse([255, 250, 24, 1, 255]).Events);
        var e = Assert.Single(p.Parse([240]).Events);
        Assert.Equal(TelnetEventKind.Subnegotiation, e.Kind);
    }

    [Fact]
    public void EscapedLiteral0xFF_IsDataNotCommand()
    {
        var r = new TelnetIacParser().Parse([(byte)'a', 255, 255, (byte)'b']);
        Assert.Equal([(byte)'a', 255, (byte)'b'], r.Data.ToArray());
        Assert.Empty(r.Events);
    }

    [Fact]
    public void EscapedFFSplitAcrossReads()
    {
        var p = new TelnetIacParser();
        var a = p.Parse([255]);
        var b = p.Parse([255]);
        Assert.Empty(a.Data);
        Assert.Equal([255], b.Data.ToArray());
    }

    [Fact]
    public void EscapedFFInsideSubnegotiationPayload()
    {
        var e = Assert.Single(new TelnetIacParser().Parse([255, 250, 31, 255, 255, 5, 255, 240]).Events);
        Assert.Equal([255, 5], e.Payload);
    }

    [Fact]
    public void CrNulIsCollapsedToCr()
    {
        var r = new TelnetIacParser().Parse([(byte)'a', 13, 0, (byte)'b']);
        Assert.Equal([(byte)'a', 13, (byte)'b'], r.Data.ToArray());
    }
}

public class TelnetNegotiatorTests
{
    [Fact]
    public void AcceptsServerEcho_AndDoesNotLoopOnRepeat()
    {
        var n = new TelnetNegotiator();
        var ev = new TelnetEvent(TelnetEventKind.Negotiation, Telnet.WILL, Telnet.OptEcho, []);
        Assert.Equal([Telnet.IAC, Telnet.DO, Telnet.OptEcho], n.Handle(ev));
        Assert.Empty(n.Handle(ev));
    }

    [Fact]
    public void DoNaws_RepliesWillAndReportsSize()
    {
        var n = new TelnetNegotiator();
        n.SetWindowSize(120, 40);
        var reply = n.Handle(new(TelnetEventKind.Negotiation, Telnet.DO, Telnet.OptNaws, []));
        byte[] expected = [255, 251, 31, 255, 250, 31, 0, 120, 0, 40, 255, 240];
        Assert.Equal(expected, reply);
    }

    [Fact]
    public void NawsEscapes0xFFInSize()
    {
        var n = new TelnetNegotiator();
        n.Handle(new(TelnetEventKind.Negotiation, Telnet.DO, Telnet.OptNaws, []));
        var naws = n.SetWindowSize(255, 24);
        Assert.Equal([255, 250, 31, 0, 255, 255, 0, 24, 255, 240], naws);
    }

    [Fact]
    public void ResizeBeforeNawsEnabled_SendsNothing()
        => Assert.Empty(new TelnetNegotiator().SetWindowSize(100, 30));

    [Fact]
    public void RefusesUnknownOptions()
    {
        var n = new TelnetNegotiator();
        Assert.Equal([255, 252, 99], n.Handle(new(TelnetEventKind.Negotiation, Telnet.DO, 99, [])));
        Assert.Equal([255, 254, 99], n.Handle(new(TelnetEventKind.Negotiation, Telnet.WILL, 99, [])));
    }

    [Fact]
    public void AnswersTerminalTypeSend()
    {
        var n = new TelnetNegotiator("vt100");
        var reply = n.Handle(new(TelnetEventKind.Subnegotiation, 0, Telnet.OptTerminalType, [Telnet.TtypeSend]));
        byte[] expected = [255, 250, 24, 0, .. "vt100"u8.ToArray(), 255, 240];
        Assert.Equal(expected, reply);
    }

    [Fact]
    public void OutgoingFFIsDoubled()
        => Assert.Equal([1, 255, 255, 2], TelnetNegotiator.EscapeOutgoing([1, 255, 2]));
}
