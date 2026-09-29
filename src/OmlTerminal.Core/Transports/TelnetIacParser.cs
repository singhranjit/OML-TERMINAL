namespace OmlTerminal.Core.Transports;

public static class Telnet
{
    public const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    public const byte OptEcho = 1, OptSuppressGoAhead = 3, OptTerminalType = 24, OptNaws = 31;
    public const byte TtypeIs = 0, TtypeSend = 1;
}

public enum TelnetEventKind { Negotiation, Subnegotiation, OtherCommand }

/// <param name="Verb">DO/DONT/WILL/WONT for negotiation; the command byte for OtherCommand; 0 for subnegotiation.</param>
public readonly record struct TelnetEvent(TelnetEventKind Kind, byte Verb, byte Option, byte[] Payload);

public sealed class TelnetParseResult
{
    public List<byte> Data { get; } = new();
    public List<TelnetEvent> Events { get; } = new();
}

/// <summary>Socket-free, stateful Telnet IAC scanner. Feed it arbitrary chunks; state survives across calls.</summary>
public sealed class TelnetIacParser
{
    private enum State { Data, Iac, Verb, SbOption, SbData, SbIac }

    private State _state = State.Data;
    private byte _verb;
    private byte _sbOption;
    private readonly List<byte> _sb = new();
    private bool _afterCr;

    public TelnetParseResult Parse(ReadOnlySpan<byte> input)
    {
        var r = new TelnetParseResult();
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Telnet.IAC) { _state = State.Iac; break; }
                    if (_afterCr && b == 0) { _afterCr = false; break; } // CR NUL == bare CR
                    _afterCr = b == 13;
                    r.Data.Add(b);
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case Telnet.IAC: r.Data.Add(255); _afterCr = false; _state = State.Data; break; // escaped 0xFF
                        case Telnet.DO or Telnet.DONT or Telnet.WILL or Telnet.WONT: _verb = b; _state = State.Verb; break;
                        case Telnet.SB: _state = State.SbOption; break;
                        default:
                            r.Events.Add(new(TelnetEventKind.OtherCommand, b, 0, []));
                            _state = State.Data;
                            break;
                    }
                    break;

                case State.Verb:
                    r.Events.Add(new(TelnetEventKind.Negotiation, _verb, b, []));
                    _state = State.Data;
                    break;

                case State.SbOption:
                    _sbOption = b; _sb.Clear(); _state = State.SbData;
                    break;

                case State.SbData:
                    if (b == Telnet.IAC) _state = State.SbIac; else _sb.Add(b);
                    break;

                case State.SbIac:
                    if (b == Telnet.IAC) { _sb.Add(255); _state = State.SbData; }
                    else if (b == Telnet.SE)
                    {
                        r.Events.Add(new(TelnetEventKind.Subnegotiation, 0, _sbOption, _sb.ToArray()));
                        _sb.Clear(); _state = State.Data;
                    }
                    else { _sb.Clear(); _state = State.Data; } // malformed: abandon the subnegotiation
                    break;
            }
        }
        return r;
    }
}
