using System.Security.Cryptography;
using System.Text;

namespace OmlTerminal.Core.Vnc;

/// <summary>RFC 6143 §7.2.2 VNC Authentication: the server's 16-byte challenge is DES-ECB encrypted (two 8-byte blocks) using a key built from the password with each byte's bits reversed - a quirk of the original RFB spec, not a typo.</summary>
public static class VncAuth
{
    public static byte[] EncryptChallenge(string password, ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 16) throw new ArgumentException("Challenge must be 16 bytes.", nameof(challenge));

        var key = new byte[8];
        var pwBytes = Encoding.ASCII.GetBytes(password);
        for (int i = 0; i < 8; i++) key[i] = i < pwBytes.Length ? ReverseBits(pwBytes[i]) : (byte)0;

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;
        using var enc = des.CreateEncryptor();

        var input = challenge.ToArray();
        var result = new byte[16];
        enc.TransformBlock(input, 0, 8, result, 0);
        enc.TransformBlock(input, 8, 8, result, 8);
        return result;
    }

    private static byte ReverseBits(byte b)
    {
        byte r = 0;
        for (int i = 0; i < 8; i++) { r = (byte)((r << 1) | (b & 1)); b >>= 1; }
        return r;
    }
}
