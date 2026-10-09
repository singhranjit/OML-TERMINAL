using System.Text.Json;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

public sealed class JsonSessionStore(string? path = null, SecretProtector? protector = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "sessions.json");

    /// <summary>When set (a master password is in use), secrets are encrypted with it. Otherwise they're still never
    /// written in plain text: LocalSecret encrypts them for this user on this machine (DPAPI on Windows, a key file on
    /// Linux/macOS). Plain values from older versions load as-is and are encrypted on the next save.</summary>
    public SecretProtector? Protector { get; set; } = protector;

    /// <summary>True if the file contains encrypted passwords (so a master password is needed to read them).</summary>
    public bool HasProtectedSecrets()
    {
        if (!File.Exists(Path)) return false;
        try
        {
            var all = JsonSerializer.Deserialize<List<SessionProfile>>(File.ReadAllText(Path), JsonFile.Options);
            return all?.Any(s => SecretProtector.IsProtected(s?.Password) || SecretProtector.IsProtected(s?.PrivateKeyPassphrase)
                                 || SecretProtector.IsProtected(s?.EnablePassword) || SecretProtector.IsProtected(s?.JumpPassword)) == true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Loads sessions, silently dropping any entry that fails validation. A password that cannot be decrypted loads as empty.</summary>
    public List<SessionProfile> Load()
    {
        if (!File.Exists(Path)) return new();
        try
        {
            var all = JsonSerializer.Deserialize<List<SessionProfile>>(File.ReadAllText(Path), JsonFile.Options);
            var valid = all?.Where(s => s is not null && s.Validate().Count == 0).ToList() ?? new();
            // Profiles that don't validate are left out - and would vanish from disk on the next save. Keep the file as it was.
            if (all is not null && valid.Count != all.Count) UnreadableFile.Keep(Path);
            foreach (var s in valid)
            {
                s.Password = Reveal(s.Password);
                s.PrivateKeyPassphrase = Reveal(s.PrivateKeyPassphrase);
                s.EnablePassword = Reveal(s.EnablePassword);
                s.JumpPassword = Reveal(s.JumpPassword);
            }
            return valid;
        }
        catch (JsonException)
        {
            UnreadableFile.Keep(Path); // the next save would otherwise overwrite the damaged file
            return new();
        }
    }

    /// <summary>Validates every profile and throws before touching disk if any is invalid. The in-memory profiles are never modified.</summary>
    public void Save(IEnumerable<SessionProfile> sessions)
    {
        var list = sessions.ToList();
        foreach (var s in list)
        {
            var errors = s.Validate();
            if (errors.Count > 0) throw new SessionValidationException(errors);
        }
        var toWrite = list.Select(s =>
        {
            var copy = s.Clone();
            copy.Password = Hide(copy.Password);
            copy.PrivateKeyPassphrase = Hide(copy.PrivateKeyPassphrase);
            copy.EnablePassword = Hide(copy.EnablePassword);
            copy.JumpPassword = Hide(copy.JumpPassword);
            return copy;
        }).ToList();
        JsonFile.WriteAtomic(Path, toWrite);
    }

    private string Hide(string value) =>
        string.IsNullOrEmpty(value) ? value : Protector is not null ? Protector.Protect(value) : LocalSecret.Protect(value);

    /// <summary>A secret that can't be decrypted (wrong master password, another user or machine) loads as empty.</summary>
    private string Reveal(string value)
    {
        if (SecretProtector.IsProtected(value)) return Protector?.Unprotect(value) ?? "";
        if (LocalSecret.IsProtected(value)) return LocalSecret.Unprotect(value) ?? "";
        return value;
    }
}
