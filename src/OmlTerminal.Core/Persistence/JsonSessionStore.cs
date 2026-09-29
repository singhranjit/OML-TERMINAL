using System.Text.Json;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

public sealed class JsonSessionStore(string? path = null, SecretProtector? protector = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "sessions.json");

    /// <summary>When set, passwords are encrypted on save and decrypted on load. Plain values found on load are re-encrypted on the next save.</summary>
    public SecretProtector? Protector { get; set; } = protector;

    /// <summary>True if the file contains encrypted passwords (so a master password is needed to read them).</summary>
    public bool HasProtectedSecrets()
    {
        if (!File.Exists(Path)) return false;
        try
        {
            var all = JsonSerializer.Deserialize<List<SessionProfile>>(File.ReadAllText(Path), JsonFile.Options);
            return all?.Any(s => SecretProtector.IsProtected(s?.Password) || SecretProtector.IsProtected(s?.PrivateKeyPassphrase)
                                 || SecretProtector.IsProtected(s?.EnablePassword)) == true;
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
            foreach (var s in valid)
            {
                if (SecretProtector.IsProtected(s.Password))
                    s.Password = Protector?.Unprotect(s.Password) ?? "";
                if (SecretProtector.IsProtected(s.PrivateKeyPassphrase))
                    s.PrivateKeyPassphrase = Protector?.Unprotect(s.PrivateKeyPassphrase) ?? "";
                if (SecretProtector.IsProtected(s.EnablePassword))
                    s.EnablePassword = Protector?.Unprotect(s.EnablePassword) ?? "";
            }
            return valid;
        }
        catch (JsonException)
        {
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
            if (Protector is not null)
            {
                copy.Password = Protector.Protect(copy.Password);
                copy.PrivateKeyPassphrase = Protector.Protect(copy.PrivateKeyPassphrase);
                copy.EnablePassword = Protector.Protect(copy.EnablePassword);
            }
            return copy;
        }).ToList();
        JsonFile.WriteAtomic(Path, toWrite);
    }
}
