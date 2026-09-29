namespace OmlTerminal.Core.Transports;

/// <summary>Socket-free option negotiation: turns parsed events into reply bytes. Avoids reply loops by tracking state.</summary>
public sealed class TelnetNegotiator(string terminalType = "xterm-256color")
{
    private readonly HashSet<byte> _weAre = new();   // options we enabled (WILL sent)
    private readonly HashSet<byte> _theyAre = new(); // options peer enabled (DO sent)
    public int Cols { get; private set; } = 80;
    public int Rows { get; private set; } = 24;

    public byte[] Handle(TelnetEvent e)
    {
        var o = new List<byte>();
        if (e.Kind == TelnetEventKind.Negotiation)
        {
            switch (e.Verb)
            {
                case Telnet.WILL:
                    if (e.Option is Telnet.OptEcho or Telnet.OptSuppressGoAhead)
                    { if (_theyAre.Add(e.Option)) o.AddRange([Telnet.IAC, Telnet.DO, e.Option]); }
                    else o.AddRange([Telnet.IAC, Telnet.DONT, e.Option]);
                    break;
                case Telnet.WONT:
                    if (_theyAre.Remove(e.Option)) o.AddRange([Telnet.IAC, Telnet.DONT, e.Option]);
                    break;
                case Telnet.DO:
                    if (e.Option is Telnet.OptTerminalType or Telnet.OptNaws or Telnet.OptSuppressGoAhead)
                    {
                        if (_weAre.Add(e.Option))
                        {
                            o.AddRange([Telnet.IAC, Telnet.WILL, e.Option]);
                            if (e.Option == Telnet.OptNaws) o.AddRange(BuildNaws());
                        }
                    }
                    else o.AddRange([Telnet.IAC, Telnet.WONT, e.Option]);
                    break;
                case Telnet.DONT:
                    if (_weAre.Remove(e.Option)) o.AddRange([Telnet.IAC, Telnet.WONT, e.Option]);
                    break;
            }
        }
        else if (e.Kind == TelnetEventKind.Subnegotiation
                 && e.Option == Telnet.OptTerminalType && e.Payload.Length > 0 && e.Payload[0] == Telnet.TtypeSend)
        {
            o.AddRange([Telnet.IAC, Telnet.SB, Telnet.OptTerminalType, Telnet.TtypeIs]);
            o.AddRange(System.Text.Encoding.ASCII.GetBytes(terminalType));
            o.AddRange([Telnet.IAC, Telnet.SE]);
        }
        return o.ToArray();
    }

    /// <summary>Records the size and returns a NAWS report if the peer enabled NAWS, otherwise empty.</summary>
    public byte[] SetWindowSize(int cols, int rows)
    {
        Cols = cols; Rows = rows;
        return _weAre.Contains(Telnet.OptNaws) ? BuildNaws() : Array.Empty<byte>();
    }

    public byte[] BuildNaws()
    {
        var o = new List<byte> { Telnet.IAC, Telnet.SB, Telnet.OptNaws };
        foreach (var b in new[] { (byte)(Cols >> 8), (byte)Cols, (byte)(Rows >> 8), (byte)Rows })
        {
            o.Add(b);
            if (b == Telnet.IAC) o.Add(b);
        }
        o.AddRange([Telnet.IAC, Telnet.SE]);
        return o.ToArray();
    }

    public static byte[] EscapeOutgoing(ReadOnlySpan<byte> data)
    {
        var o = new List<byte>(data.Length);
        foreach (var b in data) { o.Add(b); if (b == Telnet.IAC) o.Add(b); }
        return o.ToArray();
    }
}
