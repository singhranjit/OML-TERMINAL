namespace OmlTerminal.Core.Models;

public sealed class SessionProfile
{
    public static readonly IReadOnlyDictionary<ProtocolKind, int> DefaultPorts = new Dictionary<ProtocolKind, int>
    {
        [ProtocolKind.Ssh] = 22,
        [ProtocolKind.Telnet] = 23,
        [ProtocolKind.Rdp] = 3389,
        [ProtocolKind.Vnc] = 5900,
        [ProtocolKind.Sftp] = 22,
    };

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProtocolKind Protocol { get; set; } = ProtocolKind.Ssh;
    public TransportEngine Engine { get; set; } = TransportEngine.BuiltIn;
    public string Folder { get; set; } = "";
    /// <summary>Comma separated operational labels, e.g. production, core, ios-xe. Labels are local metadata.</summary>
    public string Tags { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Password-manager credential supplying Username / Password / EnablePassword at connect time
    /// (see <see cref="Credentials.Resolve"/>). Null = use the fields saved on this session.</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>Cisco-style enable password: typed automatically after login when the device lands at a '>' prompt,
    /// and used by Config Backup to reach privileged mode.</summary>
    public string EnablePassword { get; set; } = "";

    /// <summary>SSH only. Password auth (the default) leaves this as Password; PrivateKey ignores Password
    /// and authenticates with PrivateKeyPath instead (PrivateKeyPassphrase only if the key itself is encrypted).</summary>
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
    public string PrivateKeyPath { get; set; } = "";
    public string PrivateKeyPassphrase { get; set; } = "";

    public int BaudRate { get; set; } = 9600;
    public string SerialPortName { get; set; } = "";

    /// <summary>Local shell only. Blank auto-detects PowerShell, falling back to cmd.exe.</summary>
    public string LocalShellPath { get; set; } = "";

    /// <summary>Local shell only. Arguments appended after the (quoted) shell path, e.g. "--login -i" for Cygwin bash
    /// or "-d Ubuntu" for wsl.exe.</summary>
    public string LocalShellArgs { get; set; } = "";

    /// <summary>SSH only, OpenSSH engine only. Forwards X11 to the local X server (ssh -Y), starting one if needed.</summary>
    public bool X11Forwarding { get; set; }

    /// <summary>Lines typed into the session once it connects (one command per line) - e.g. "terminal length 0".</summary>
    public string StartupCommands { get; set; } = "";

    /// <summary>Free-text notes shown in the sidebar tooltip: rack location, change ticket, owner, etc.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Tab/sidebar accent so production gear stands out from lab gear. One of <see cref="ColorTags"/>, or blank.</summary>
    public string ColorTag { get; set; } = "";

    public static readonly IReadOnlyList<string> ColorTags = ["", "Red", "Orange", "Yellow", "Green", "Blue", "Purple"];

    /// <summary>Connect through an SSH jump/gateway host first. Works for both SSH and Telnet targets; not supported with the PuTTY engine.</summary>
    public bool UseJumpHost { get; set; }
    public string JumpHost { get; set; } = "";
    public int JumpPort { get; set; } = 22;
    public string JumpUsername { get; set; } = "";
    public string JumpPassword { get; set; } = "";

    public static int DefaultPortFor(ProtocolKind protocol) =>
        DefaultPorts.TryGetValue(protocol, out var p) ? p : 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Name is required.");
        if (Protocol == ProtocolKind.Serial)
        {
            if (string.IsNullOrWhiteSpace(SerialPortName)) errors.Add("Serial port is required.");
            if (BaudRate < 1) errors.Add("Baud rate must be positive.");
        }
        else if (Protocol != ProtocolKind.Local)
        {
            if (string.IsNullOrWhiteSpace(Host)) errors.Add("Host is required.");
            if (Protocol is ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Rdp or ProtocolKind.Vnc or ProtocolKind.Sftp
                && (Port < 1 || Port > 65535))
                errors.Add("Port must be between 1 and 65535.");
        }
        if (Protocol is ProtocolKind.Ssh or ProtocolKind.Sftp && AuthMethod == SshAuthMethod.PrivateKey)
        {
            if (Protocol == ProtocolKind.Ssh && Engine == TransportEngine.Plink) errors.Add("Private-key auth requires the built-in SSH engine, not PuTTY.");
            if (string.IsNullOrWhiteSpace(PrivateKeyPath)) errors.Add("Private key file is required.");
        }
        if (X11Forwarding && !(Protocol == ProtocolKind.Ssh && Engine == TransportEngine.OpenSsh))
            errors.Add("X11 forwarding needs an SSH session using the OpenSSH engine.");
        if (UseJumpHost)
        {
            if (Protocol == ProtocolKind.Ssh && Engine != TransportEngine.BuiltIn)
                errors.Add("Jump host requires the built-in SSH engine, not PuTTY or OpenSSH.");
            if (Protocol is not (ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Sftp))
                errors.Add("Jump host is only supported for SSH and Telnet targets (SFTP included).");
            if (string.IsNullOrWhiteSpace(JumpHost)) errors.Add("Jump host address is required.");
            if (JumpPort is < 1 or > 65535) errors.Add("Jump host port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(JumpUsername)) errors.Add("Jump host username is required.");
        }
        return errors;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProtocolLabel => Protocol switch
    {
        ProtocolKind.Telnet => "TELNET",
        ProtocolKind.Serial => "SERIAL",
        ProtocolKind.Local => "SHELL",
        _ => Protocol.ToString().ToUpperInvariant(),
    };

    /// <summary>True for anything that speaks SSH underneath (a shell or the SFTP browser) - both can feed the
    /// SSH-based tools (config backup, remote capture).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSshBased => Protocol is ProtocolKind.Ssh or ProtocolKind.Sftp;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Folder) ? Name : $"{Folder}/{Name}";

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> TagList => Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public SessionProfile Clone() => (SessionProfile)MemberwiseClone();
}
