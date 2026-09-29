using OmlTerminal.Core.Vnc;

namespace OmlTerminal.Core.Tests;

public class VncAuthTests
{
    [Fact]
    public void EncryptChallenge_MatchesIndependentPythonPycryptodomeReference()
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var result = VncAuth.EncryptChallenge("secret1", challenge);
        Assert.Equal("bfa07a377a819a0bc3c9fbb13c9869cd", Convert.ToHexString(result).ToLowerInvariant());
    }

    [Fact]
    public void PasswordLongerThan8Chars_IsTruncated()
    {
        var challenge = new byte[16];
        // VNC only ever uses the first 8 characters of the password.
        Assert.Equal(VncAuth.EncryptChallenge("12345678", challenge), VncAuth.EncryptChallenge("12345678extra-ignored", challenge));
    }

    [Fact]
    public void ShortPassword_IsZeroPadded()
    {
        var challenge = new byte[16];
        Assert.NotEqual(VncAuth.EncryptChallenge("ab", challenge), VncAuth.EncryptChallenge("abc", challenge));
    }

    [Fact]
    public void RejectsWrongChallengeLength() =>
        Assert.Throws<ArgumentException>(() => VncAuth.EncryptChallenge("pw", new byte[10]));
}

public class VncWireTests
{
    [Fact]
    public void ParsesStandardVersionLine()
    {
        Assert.True(VncWire.TryParseVersion("RFB 003.008\n"u8, out var major, out var minor));
        Assert.Equal((3, 8), (major, minor));
    }

    [Fact]
    public void ParsesOlderProtocolVersion() => Assert.True(VncWire.TryParseVersion("RFB 003.003\n"u8, out var maj, out var min) && maj == 3 && min == 3);

    [Theory]
    [InlineData("NOTRFB003.8\n")]
    [InlineData("too short")]
    public void RejectsNonVersionLines(string s) => Assert.False(VncWire.TryParseVersion(System.Text.Encoding.ASCII.GetBytes(s), out _, out _));

    [Fact]
    public void VersionLineRoundTrips() => Assert.True(VncWire.TryParseVersion(VncWire.VersionLine(3, 8), out var maj, out var min) && maj == 3 && min == 8);

    [Fact]
    public void SetPixelFormat_EncodesForced32BitTrueColor()
    {
        var msg = VncWire.SetPixelFormat(VncPixelFormat.Bgra32);
        Assert.Equal(20, msg.Length);
        Assert.Equal(0, msg[0]);
        Assert.Equal(32, msg[4]); // bits-per-pixel
        Assert.Equal(1, msg[7]);  // true-color-flag
    }

    [Fact]
    public void SetEncodings_EncodesCountAndEachEncodingBigEndian()
    {
        var msg = VncWire.SetEncodings(VncEncoding.Raw);
        Assert.Equal(2, msg[0]);
        Assert.Equal(1, VncWire.ReadU16(msg, 2)); // count
        Assert.Equal(0, VncWire.ReadS32(msg, 4)); // Raw == 0
    }

    [Fact]
    public void FramebufferUpdateRequest_EncodesFields()
    {
        var msg = VncWire.FramebufferUpdateRequest(incremental: true, 1, 2, 800, 600);
        Assert.Equal(3, msg[0]);
        Assert.Equal(1, msg[1]);
        Assert.Equal(1, VncWire.ReadU16(msg, 2));
        Assert.Equal(2, VncWire.ReadU16(msg, 4));
        Assert.Equal(800, VncWire.ReadU16(msg, 6));
        Assert.Equal(600, VncWire.ReadU16(msg, 8));
    }

    [Fact]
    public void KeyEvent_EncodesDownFlagAndKeysym()
    {
        var down = VncWire.KeyEvent(0x61, down: true);
        Assert.Equal(4, down[0]);
        Assert.Equal(1, down[1]);
        Assert.Equal(0x61u, VncWire.ReadU32(down, 4));
        Assert.Equal(0, VncWire.KeyEvent(0x61, down: false)[1]);
    }

    [Fact]
    public void PointerEvent_EncodesButtonsAndPosition()
    {
        var msg = VncWire.PointerEvent(0b101, 10, 20);
        Assert.Equal(5, msg[0]);
        Assert.Equal(0b101, msg[1]);
        Assert.Equal(10, VncWire.ReadU16(msg, 2));
        Assert.Equal(20, VncWire.ReadU16(msg, 4));
    }

    [Fact]
    public void ClientCutText_EncodesLengthAndUtf8AsLatin1Bytes()
    {
        var msg = VncWire.ClientCutText("hi");
        Assert.Equal(6, msg[0]);
        Assert.Equal(2u, VncWire.ReadU32(msg, 4));
        Assert.Equal("hi"u8.ToArray(), msg[8..]);
    }

    [Fact]
    public void U16AndU32RoundTrip()
    {
        var buf = new byte[8];
        buf[0] = 0x12; buf[1] = 0x34;
        Assert.Equal(0x1234, VncWire.ReadU16(buf, 0));
        buf[4] = 0xDE; buf[5] = 0xAD; buf[6] = 0xBE; buf[7] = 0xEF;
        Assert.Equal(0xDEADBEEFu, VncWire.ReadU32(buf, 4));
    }
}
