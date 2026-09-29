using System.Text;

namespace OmlTerminal.Core.Terminal;

public enum TerminalKey
{
    Enter, Backspace, Tab, Escape, Up, Down, Left, Right,
    Delete, Home, End, PageUp, PageDown, Insert,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

/// <summary>Maps logical keys and typed characters to the bytes a VT terminal sends.</summary>
public static class KeyEncoder
{
    public static byte[]? Encode(TerminalKey key, bool applicationCursorKeys = false, bool shift = false)
    {
        string? s = key switch
        {
            TerminalKey.Enter => "\r",
            TerminalKey.Backspace => "\x7f",
            TerminalKey.Tab => shift ? "\x1b[Z" : "\t",
            TerminalKey.Escape => "\x1b",
            TerminalKey.Up => applicationCursorKeys ? "\x1bOA" : "\x1b[A",
            TerminalKey.Down => applicationCursorKeys ? "\x1bOB" : "\x1b[B",
            TerminalKey.Right => applicationCursorKeys ? "\x1bOC" : "\x1b[C",
            TerminalKey.Left => applicationCursorKeys ? "\x1bOD" : "\x1b[D",
            TerminalKey.Home => applicationCursorKeys ? "\x1bOH" : "\x1b[H",
            TerminalKey.End => applicationCursorKeys ? "\x1bOF" : "\x1b[F",
            TerminalKey.Insert => "\x1b[2~",
            TerminalKey.Delete => "\x1b[3~",
            TerminalKey.PageUp => "\x1b[5~",
            TerminalKey.PageDown => "\x1b[6~",
            TerminalKey.F1 => "\x1bOP",
            TerminalKey.F2 => "\x1bOQ",
            TerminalKey.F3 => "\x1bOR",
            TerminalKey.F4 => "\x1bOS",
            TerminalKey.F5 => "\x1b[15~",
            TerminalKey.F6 => "\x1b[17~",
            TerminalKey.F7 => "\x1b[18~",
            TerminalKey.F8 => "\x1b[19~",
            TerminalKey.F9 => "\x1b[20~",
            TerminalKey.F10 => "\x1b[21~",
            TerminalKey.F11 => "\x1b[23~",
            TerminalKey.F12 => "\x1b[24~",
            _ => null,
        };
        return s is null ? null : Encoding.ASCII.GetBytes(s);
    }

    /// <summary>Ctrl+A..Z -> 0x01..0x1A. Returns null for non-letters.</summary>
    public static byte[]? EncodeCtrlLetter(char letter)
    {
        char u = char.ToUpperInvariant(letter);
        return u is >= 'A' and <= 'Z' ? [(byte)(u - 'A' + 1)] : null;
    }

    /// <summary>Text typed by the user (or a voice command) as UTF-8; Alt prefixes ESC.</summary>
    public static byte[] EncodeText(string text, bool alt = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!alt) return bytes;
        var withEsc = new byte[bytes.Length + 1];
        withEsc[0] = 0x1b;
        bytes.CopyTo(withEsc, 1);
        return withEsc;
    }
}
