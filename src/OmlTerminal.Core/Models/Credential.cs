namespace OmlTerminal.Core.Models;

/// <summary>A saved login in the password manager: shared by any number of sessions, so changing a password once
/// updates every device that uses it (and every scheduled backup).</summary>
public sealed class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Cisco-style privileged-mode password, typed after "enable" at a '>' prompt.</summary>
    public string EnablePassword { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public Credential Clone() => (Credential)MemberwiseClone();

    public override string ToString() => string.IsNullOrEmpty(Username) ? Name : $"{Name}  ({Username})";
}

public static class Credentials
{
    /// <summary>A copy of the profile with its linked credential's username / password / enable password filled in.
    /// Profiles without a credential (or whose credential was deleted) come back unchanged.</summary>
    public static SessionProfile Resolve(SessionProfile profile, IEnumerable<Credential> vault)
    {
        if (profile.CredentialId is not { } id) return profile;
        var cred = vault.FirstOrDefault(c => c.Id == id);
        if (cred is null) return profile;
        var copy = profile.Clone();
        if (cred.Username.Length > 0) copy.Username = cred.Username;
        copy.Password = cred.Password;
        if (cred.EnablePassword.Length > 0) copy.EnablePassword = cred.EnablePassword;
        return copy;
    }
}
