namespace OmlTerminal.Core.Vnc;

public enum VncSecurityType : byte { Invalid = 0, None = 1, VncAuth = 2 }
public enum VncEncoding { Raw = 0 }

public sealed record VncPixelFormat(byte BitsPerPixel, byte Depth, bool BigEndian, bool TrueColor,
    ushort RedMax, ushort GreenMax, ushort BlueMax, byte RedShift, byte GreenShift, byte BlueShift)
{
    /// <summary>32bpp little-endian truecolor, BGRA byte order in the framebuffer - what OML Terminal always requests.</summary>
    public static VncPixelFormat Bgra32 { get; } = new(32, 24, BigEndian: false, TrueColor: true, 255, 255, 255, 16, 8, 0);

    public int BytesPerPixel => BitsPerPixel / 8;

    public byte[] ToWireBytes()
    {
        var b = new byte[16];
        b[0] = BitsPerPixel; b[1] = Depth; b[2] = (byte)(BigEndian ? 1 : 0); b[3] = (byte)(TrueColor ? 1 : 0);
        WriteU16(b, 4, RedMax); WriteU16(b, 6, GreenMax); WriteU16(b, 8, BlueMax);
        b[10] = RedShift; b[11] = GreenShift; b[12] = BlueShift;
        return b;
    }

    private static void WriteU16(byte[] b, int offset, ushort v) { b[offset] = (byte)(v >> 8); b[offset + 1] = (byte)v; }
}

public readonly record struct VncRectangle(int X, int Y, int Width, int Height);

/// <summary>Pure, socket-free framing for the messages OML Terminal sends and parses. Kept separate from VncClient's I/O so it's directly unit-testable.</summary>
public static class VncWire
{
    public static bool TryParseVersion(ReadOnlySpan<byte> line, out int major, out int minor)
    {
        major = minor = 0;
        // "RFB 003.008\n" - exactly 12 bytes.
        if (line.Length != 12 || line[0] != 'R' || line[1] != 'F' || line[2] != 'B' || line[3] != ' ' || line[11] != '\n')
            return false;
        var text = System.Text.Encoding.ASCII.GetString(line);
        var parts = text[4..^1].Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
    }

    public static byte[] VersionLine(int major, int minor) =>
        System.Text.Encoding.ASCII.GetBytes($"RFB {major:000}.{minor:000}\n");

    public static byte[] ClientInit(bool shared) => [(byte)(shared ? 1 : 0)];

    public static byte[] SetPixelFormat(VncPixelFormat fmt)
    {
        var msg = new byte[20];
        msg[0] = 0; // message type
        fmt.ToWireBytes().CopyTo(msg, 4);
        return msg;
    }

    public static byte[] SetEncodings(params VncEncoding[] encodings)
    {
        var msg = new byte[4 + encodings.Length * 4];
        msg[0] = 2;
        msg[2] = (byte)(encodings.Length >> 8); msg[3] = (byte)encodings.Length;
        for (int i = 0; i < encodings.Length; i++)
        {
            int v = (int)encodings[i];
            int o = 4 + i * 4;
            msg[o] = (byte)(v >> 24); msg[o + 1] = (byte)(v >> 16); msg[o + 2] = (byte)(v >> 8); msg[o + 3] = (byte)v;
        }
        return msg;
    }

    public static byte[] FramebufferUpdateRequest(bool incremental, int x, int y, int w, int h)
    {
        var msg = new byte[10];
        msg[0] = 3;
        msg[1] = (byte)(incremental ? 1 : 0);
        WriteU16(msg, 2, x); WriteU16(msg, 4, y); WriteU16(msg, 6, w); WriteU16(msg, 8, h);
        return msg;
    }

    public static byte[] KeyEvent(uint keysym, bool down)
    {
        var msg = new byte[8];
        msg[0] = 4;
        msg[1] = (byte)(down ? 1 : 0);
        msg[4] = (byte)(keysym >> 24); msg[5] = (byte)(keysym >> 16); msg[6] = (byte)(keysym >> 8); msg[7] = (byte)keysym;
        return msg;
    }

    public static byte[] PointerEvent(byte buttonMask, int x, int y)
    {
        var msg = new byte[6];
        msg[0] = 5;
        msg[1] = buttonMask;
        WriteU16(msg, 2, x); WriteU16(msg, 4, y);
        return msg;
    }

    public static byte[] ClientCutText(string text)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(text);
        var msg = new byte[8 + bytes.Length];
        msg[0] = 6;
        uint len = (uint)bytes.Length;
        msg[4] = (byte)(len >> 24); msg[5] = (byte)(len >> 16); msg[6] = (byte)(len >> 8); msg[7] = (byte)len;
        bytes.CopyTo(msg, 8);
        return msg;
    }

    public static ushort ReadU16(ReadOnlySpan<byte> b, int offset) => (ushort)((b[offset] << 8) | b[offset + 1]);
    public static uint ReadU32(ReadOnlySpan<byte> b, int offset) => (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
    public static int ReadS32(ReadOnlySpan<byte> b, int offset) => (int)ReadU32(b, offset);

    private static void WriteU16(byte[] b, int offset, int v) { b[offset] = (byte)(v >> 8); b[offset + 1] = (byte)v; }
}
