namespace OmlTerminal.Core.Terminal;

/// <summary>Strips ANSI/VT escape sequences from a raw byte stream so a transcript reads like plain text
/// instead of raw control codes. Deliberately simple (not a full terminal parser) - good enough for a
/// human-readable log, not for re-rendering.</summary>
public static class AnsiStripper
{
    public static byte[] Strip(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            byte b = input[i];
            if (b != 0x1B) { output.Add(b); i++; continue; }

            // ESC seen - figure out how long this escape sequence runs and skip it entirely, without emitting it.
            i++;
            if (i >= input.Length) break;
            byte next = input[i];
            if (next == (byte)'[')
            {
                // CSI: ESC [ params... final-byte (0x40-0x7E)
                i++;
                while (i < input.Length && (input[i] < 0x40 || input[i] > 0x7E)) i++;
                if (i < input.Length) i++;
            }
            else if (next == (byte)']')
            {
                // OSC: ESC ] ... terminated by BEL or ESC \
                i++;
                while (i < input.Length && input[i] != 0x07 && !(input[i] == 0x1B && i + 1 < input.Length && input[i + 1] == (byte)'\\'))
                    i++;
                if (i < input.Length && input[i] == 0x07) i++;
                else if (i + 1 < input.Length) i += 2;
            }
            else
            {
                i++; // simple two-byte escape (e.g. ESC =, ESC >, ESC M)
            }
        }
        return output.ToArray();
    }
}
