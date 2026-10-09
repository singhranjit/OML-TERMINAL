using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>
/// Encrypts a secret for the current Windows user with DPAPI, stored as "dpapi:v1:" + base64. Used when no master
/// password is set, so vault passwords are never written to disk in plain text. Only this Windows account on this
/// PC can decrypt them.
/// </summary>
public static class LocalSecret
{
    public const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("oml-terminal-vault");

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext) || SecretProtector.IsProtected(plaintext)) return plaintext;
        if (!OperatingSystem.IsWindows()) return plaintext;
        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Returns null if the value can't be decrypted (another user or PC).</summary>
    public static string? Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
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
