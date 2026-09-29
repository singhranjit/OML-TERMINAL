namespace OmlTerminal.Core.Models;

/// <summary>Serialized by name (JsonStringEnumConverter), so new members are safe to append anywhere.</summary>
public enum ProtocolKind
{
    Ssh,
    Telnet,
    Serial,
    Rdp,
    Vnc,
    Local,
    /// <summary>Saved SSH credentials that open straight into the SFTP file browser instead of a shell.</summary>
    Sftp,
}

public enum TransportEngine
{
    BuiltIn,
    Plink,
    /// <summary>Windows' own OpenSSH client (ssh.exe) hosted in a ConPTY - the only engine that does X11
    /// forwarding, and it honours ~/.ssh/config, agents and every modern algorithm.</summary>
    OpenSsh,
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
}
