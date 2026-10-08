using OmlTerminal.Core.Models;
using OmlTerminal.Core.Shells;

namespace OmlTerminal.Core.Transports;

public static class TransportFactory
{
    /// <param name="x11Display">DISPLAY value for X11-forwarded OpenSSH sessions (e.g. "127.0.0.1:0.0").</param>
    public static ITerminalTransport Create(SessionProfile p, string? plinkPath = null, string? x11Display = null)
    {
        if (p.UseJumpHost && p.Protocol == ProtocolKind.Ssh && p.Engine != TransportEngine.BuiltIn)
            throw new NotSupportedException("Jump host requires the built-in SSH engine.");

        JumpHostGateway? gateway = p.UseJumpHost
            ? new JumpHostGateway(p.JumpHost, p.JumpPort, p.JumpUsername, p.JumpPassword, p.Host, p.Port)
            : null;

        return p.Protocol switch
        {
            ProtocolKind.Ssh when p.Engine == TransportEngine.Plink =>
                new PlinkTransport(p.Host, p.Port, p.Username, p.Password, plinkPath),
            ProtocolKind.Ssh when p.Engine == TransportEngine.OpenSsh =>
                new OpenSshTransport(p, p.X11Forwarding ? x11Display ?? "127.0.0.1:0.0" : null),
            ProtocolKind.Ssh => new SshTransport(p.Host, p.Port, p.Username, p.Password, gateway,
                p.AuthMethod, p.PrivateKeyPath, p.PrivateKeyPassphrase, retrustChangedKey: p.IsLabNode),
            ProtocolKind.Telnet => new TelnetTransport(p.Host, p.Port, gateway),
            ProtocolKind.Serial => new SerialTransport(p.SerialPortName, p.BaudRate),
            ProtocolKind.Local => new LocalTransport(p.LocalShellPath, null, p.LocalShellArgs, ShellCatalog.EnvironmentFor(p.LocalShellPath)),
            _ => throw new NotSupportedException($"{p.Protocol} is not supported through a terminal tab."),
        };
    }
}
