using System.Security.Cryptography;
using System.Text;

namespace OmlTerminal.Core.Persistence;

/// <summary>
/// Master-password protection for saved credentials. The key is derived with PBKDF2-SHA256 and each secret is sealed
/// with AES-256-GCM using a fresh random nonce, stored as "enc:v1:" + base64(nonce | tag | ciphertext).
/// </summary>
public sealed class SecretProtector
{
    public const string Prefix = "enc:v1:";
    private const int Iterations = 310_000;
    private const int KeyBytes = 32, NonceBytes = 12, TagBytes = 16, SaltBytes = 16;
    private const string VerifierPlaintext = "oml-terminal-master-password-check";

    private readonly byte[] _key;

    private SecretProtector(byte[] key) => _key = key;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static SecretProtector FromPassword(string masterPassword, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        return new(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(masterPassword), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes));
    }

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagBytes];
        using (var gcm = new AesGcm(_key, TagBytes)) gcm.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Returns null if the value is not protected by this key (wrong password or tampered data).</summary>
    public string? Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        try
        {
            var raw = Convert.FromBase64String(value[Prefix.Length..]);
            if (raw.Length < NonceBytes + TagBytes) return null;
            var nonce = raw.AsSpan(0, NonceBytes);
            var tag = raw.AsSpan(NonceBytes, TagBytes);
            var cipher = raw.AsSpan(NonceBytes + TagBytes);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(_key, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A value to store in settings so a typed master password can be checked without keeping it.</summary>
    public string CreateVerifier() => Protect(VerifierPlaintext);

    public bool Verify(string verifier) => Unprotect(verifier) == VerifierPlaintext;
}
