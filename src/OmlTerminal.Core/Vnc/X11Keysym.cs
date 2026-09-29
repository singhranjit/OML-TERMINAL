namespace OmlTerminal.Core.Vnc;

/// <summary>Maps input to X11 keysyms for VNC KeyEvent messages. Covers printable ASCII and the common editing/navigation keys; not an exhaustive X11 keysym table.</summary>
public static class X11Keysym
{
    public const uint BackSpace = 0xff08, Tab = 0xff09, Return = 0xff0d, Escape = 0xff1b, Delete = 0xffff;
    public const uint Home = 0xff50, Left = 0xff51, Up = 0xff52, Right = 0xff53, Down = 0xff54;
    public const uint PageUp = 0xff55, PageDown = 0xff56, End = 0xff57, Insert = 0xff63;
    public const uint ShiftL = 0xffe1, ControlL = 0xffe3, AltL = 0xffe9;
    public const uint F1 = 0xffbe; // F1..F12 are F1+0..11

    /// <summary>Printable ASCII (0x20-0x7E) maps 1:1 onto its own keysym value - a real, documented X11/Latin-1 property, not a simplification.</summary>
    public static uint? FromChar(char c) => c switch
    {
        '\r' or '\n' => Return,
        '\b' => BackSpace,
        '\t' => Tab,
        (char)27 => Escape,
        >= (char)0x20 and < (char)0x7f => c,
        _ => null,
    };

    public static uint FunctionKey(int n) => n is >= 1 and <= 12 ? F1 + (uint)(n - 1) : throw new ArgumentOutOfRangeException(nameof(n));
}
