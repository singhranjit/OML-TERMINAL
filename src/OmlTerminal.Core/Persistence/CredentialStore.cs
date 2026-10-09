using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>
/// Encrypts a secret for the current user when no master password is set, so vault passwords are never written to
/// disk in plain text. Windows: DPAPI ("dpapi:v1:" + base64) - only this Windows account on this PC can decrypt.
/// Linux/macOS: AES-256-GCM ("local:v1:" + base64 nonce|ciphertext|tag) with a random 256-bit key in
/// &lt;data&gt;/.local-key, created readable by the owner only (mode 600, like an SSH private key).
/// </summary>
public static class LocalSecret
{
    public const string DpapiPrefix = "dpapi:v1:";
    public const string KeyFilePrefix = "local:v1:";
    /// <summary>The prefix this platform writes.</summary>
    public static string Prefix => OperatingSystem.IsWindows() ? DpapiPrefix : KeyFilePrefix;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("oml-terminal-vault");
    private static readonly object KeyLock = new();
    private static byte[]? _key;

    public static bool IsProtected(string? value) =>
        value?.StartsWith(DpapiPrefix, StringComparison.Ordinal) == true || value?.StartsWith(KeyFilePrefix, StringComparison.Ordinal) == true;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext) || SecretProtector.IsProtected(plaintext)) return plaintext;
        if (OperatingSystem.IsWindows())
            return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
        var key = Key();
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length), Entropy);
        return KeyFilePrefix + Convert.ToBase64String(blob);
    }

    /// <summary>Returns null if the value can't be decrypted (another user, PC, or a lost key file).</summary>
    public static string? Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        try
        {
            if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
            }
            var blob = Convert.FromBase64String(value[KeyFilePrefix.Length..]);
            if (blob.Length < 28) return null;
            var plain = new byte[blob.Length - 28];
            using var gcm = new AesGcm(Key(), 16);
            gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length), plain, Entropy);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The per-user key (Linux/macOS), created on first use with owner-only permissions.</summary>
    private static byte[] Key()
    {
        lock (KeyLock)
        {
            if (_key is not null) return _key;
            var path = Path.Combine(AppPaths.DataDirectory, ".local-key");
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32) return _key = existing;
                UnreadableFile.Keep(path); // wrong size: keep it, make a new one (old secrets become unreadable)
            }
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var key = RandomNumberGenerator.GetBytes(32);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var f = new FileStream(path, options)) f.Write(key);
            return _key = key;
        }
    }
}

/// <summary>
/// The password manager's vault (credentials.json). Secrets are always encrypted at rest: with the master password
/// (AES-256-GCM) when one is set, otherwise with Windows DPAPI.
/// </summary>
public sealed class CredentialStore(string? path = null, SecretProtector? protector = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "credentials.json");
    public SecretProtector? Protector { get; set; } = protector;

    public List<Credential> Load()
    {
        if (!File.Exists(Path)) return new();
        try
        {
            var all = JsonSerializer.Deserialize<List<Credential>>(File.ReadAllText(Path), JsonFile.Options) ?? new();
            foreach (var c in all)
            {
                c.Password = Open(c.Password);
                c.EnablePassword = Open(c.EnablePassword);
            }
            return all.Where(c => c.Name.Length > 0).ToList();
        }
        catch (JsonException) { UnreadableFile.Keep(Path); return new(); }
    }

    private string Open(string value)
    {
        if (SecretProtector.IsProtected(value)) return Protector?.Unprotect(value) ?? "";
        if (LocalSecret.IsProtected(value)) return LocalSecret.Unprotect(value) ?? "";
        return value;
    }

    private string Seal(string value) => Protector is not null ? Protector.Protect(value) : LocalSecret.Protect(value);

    public void Save(IEnumerable<Credential> credentials)
    {
        var toWrite = credentials.Select(c =>
        {
            var copy = c.Clone();
            copy.Password = Seal(copy.Password);
            copy.EnablePassword = Seal(copy.EnablePassword);
            return copy;
        }).ToList();
        JsonFile.WriteAtomic(Path, toWrite);
    }

    /// <summary>True if the file holds master-password-encrypted secrets (so it needs unlocking first).</summary>
    public bool HasMasterProtectedSecrets()
    {
        if (!File.Exists(Path)) return false;
        try
        {
            var all = JsonSerializer.Deserialize<List<Credential>>(File.ReadAllText(Path), JsonFile.Options);
            return all?.Any(c => SecretProtector.IsProtected(c.Password) || SecretProtector.IsProtected(c.EnablePassword)) == true;
        }
        catch (JsonException) { return false; }
    }
}
