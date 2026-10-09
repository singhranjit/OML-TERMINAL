using OmlTerminal.Core.Models;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Tests;

public class TunnelSpecTests
{
    private static TunnelSpec Local(int boundPort = 8080, string targetHost = "10.0.0.1", int targetPort = 80) =>
        new() { Kind = TunnelKind.Local, BoundPort = boundPort, TargetHost = targetHost, TargetPort = targetPort };

    [Fact]
    public void ValidLocalTunnel_HasNoErrors() => Assert.Empty(Local().Validate());

    [Fact]
    public void LocalAndDynamic_AllowAutomaticPortZero()
    {
        Assert.Empty(Local(boundPort: 0).Validate());
        Assert.Empty(new TunnelSpec { Kind = TunnelKind.Dynamic, BoundPort = 0 }.Validate());
    }

    [Fact]
    public void Remote_RequiresExplicitPort()
        => Assert.Contains("Remote port must be specified explicitly.", new TunnelSpec { Kind = TunnelKind.Remote, BoundPort = 0, TargetHost = "h", TargetPort = 80 }.Validate());

    [Theory]
    [InlineData("", 80)]
    [InlineData("h", 0)]
    [InlineData("h", 70000)]
    public void LocalAndRemote_RequireTarget(string host, int port)
    {
        Assert.NotEmpty(Local(targetHost: host, targetPort: port).Validate());
        Assert.NotEmpty(new TunnelSpec { Kind = TunnelKind.Remote, BoundPort = 1, TargetHost = host, TargetPort = port }.Validate());
    }

    [Fact]
    public void Dynamic_IgnoresTargetFields()
        => Assert.Empty(new TunnelSpec { Kind = TunnelKind.Dynamic, BoundPort = 1080 }.Validate());

    [Fact]
    public void DisplayDescribesEachKind()
    {
        Assert.Contains("Local", Local().Display);
        Assert.Contains("Remote", new TunnelSpec { Kind = TunnelKind.Remote, BoundPort = 1, TargetHost = "h", TargetPort = 1 }.Display);
        Assert.Contains("SOCKS", new TunnelSpec { Kind = TunnelKind.Dynamic, BoundPort = 1080 }.Display);
    }
}

public class JumpHostValidationTests
{
    private static SessionProfile Base() => new()
    {
        Name = "sw1", Protocol = ProtocolKind.Ssh, Host = "10.0.0.1", Port = 22, Username = "admin", Password = "pw",
    };

    [Fact]
    public void ValidJumpHostConfig_HasNoErrors()
    {
        var p = Base();
        p.UseJumpHost = true;
        p.JumpHost = "bastion.example.net";
        p.JumpPort = 22;
        p.JumpUsername = "jumpuser";
        p.JumpPassword = "jumppw";
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void JumpHost_RejectsPlinkEngine()
    {
        var p = Base();
        p.UseJumpHost = true; p.Engine = TransportEngine.Plink;
        p.JumpHost = "b"; p.JumpUsername = "u";
        Assert.Contains(p.Validate(), e => e.Contains("PuTTY"));
    }

    [Fact]
    public void JumpHost_RejectsUnsupportedProtocol()
    {
        var p = Base();
        p.UseJumpHost = true; p.Protocol = ProtocolKind.Rdp; p.JumpHost = "b"; p.JumpUsername = "u";
        Assert.Contains(p.Validate(), e => e.Contains("SSH and Telnet"));
    }

    [Theory]
    [InlineData("", 22, "u")]
    [InlineData("b", 0, "u")]
    [InlineData("b", 70000, "u")]
    [InlineData("b", 22, "")]
    public void JumpHost_RequiresItsOwnFields(string host, int port, string user)
    {
        var p = Base();
        p.UseJumpHost = true; p.JumpHost = host; p.JumpPort = port; p.JumpUsername = user;
        Assert.NotEmpty(p.Validate());
    }

    [Fact]
    public void JumpHostFieldsAreIgnoredWhenNotEnabled()
    {
        var p = Base();
        p.JumpHost = ""; // left blank, UseJumpHost is false
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void TelnetTargetWithJumpHost_IsAllowed()
    {
        var p = Base();
        p.Protocol = ProtocolKind.Telnet; p.Port = 23;
        p.UseJumpHost = true; p.JumpHost = "b"; p.JumpUsername = "u";
        Assert.Empty(p.Validate());
    }
}

public class TransportFactoryJumpHostTests
{
    [Fact]
    public void ThrowsBeforeCreatingAnything_WhenPlinkCombinedWithJumpHost()
    {
        var p = new SessionProfile
        {
            Name = "x", Protocol = ProtocolKind.Ssh, Engine = TransportEngine.Plink, Host = "h", Port = 22, Username = "u",
            UseJumpHost = true, JumpHost = "b", JumpUsername = "u",
        };
        Assert.Throws<NotSupportedException>(() => TransportFactory.Create(p));
    }
}

public class TransportFactoryKeyAuthTests
{
    [Fact]
    public void BuildsSshTransport_ForPrivateKeyProfile_WithoutConnecting()
    {
        var p = new SessionProfile
        {
            Name = "x", Protocol = ProtocolKind.Ssh, Host = "h", Port = 22, Username = "u",
            AuthMethod = SshAuthMethod.PrivateKey, PrivateKeyPath = @"C:\keys\id_ed25519",
        };
        using var transport = TransportFactory.Create(p);
        Assert.IsType<SshTransport>(transport);
    }
}

public class LocalTransportTests
{
    // No test here spawns a real ConPTY process, matching how SshTransport/TelnetTransport have no live-network
    // tests either. The Unix pty is the exception: /bin/sh is always present there, and the pty plumbing
    // (new session, controlling terminal, window size) is exactly what can't be checked any other way.

    [Fact]
    public void DefaultShell_ReturnsAnExistingExecutable()
    {
        var path = LocalTransport.DefaultShell();
        Assert.True(File.Exists(path), $"Expected '{path}' to exist.");
        if (OperatingSystem.IsWindows())
            Assert.True(path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                     || path.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unix_shell_runs_in_a_pty_with_the_requested_size()
    {
        if (OperatingSystem.IsWindows()) return;
        using var transport = new LocalTransport("/bin/sh", Path.GetTempPath(),
            "-c 'stty size; tty; echo \"$TERM\"; exit 3'", new Dictionary<string, string> { ["OML_TEST"] = "1" });
        var output = new System.Text.StringBuilder();
        var closed = new TaskCompletionSource<string?>();
        transport.DataReceived += b => { lock (output) output.Append(System.Text.Encoding.UTF8.GetString(b)); };
        transport.Closed += e => closed.TrySetResult(e);

        await transport.ConnectAsync(132, 43);
        var done = await Task.WhenAny(closed.Task, Task.Delay(10_000));
        Assert.Same(closed.Task, done);
        Assert.Null(await closed.Task);
        string text;
        lock (output) text = output.ToString();
        Assert.Contains("43 132", text);                    // TIOCSWINSZ applied before the shell started
        Assert.Matches(@"/dev/(pts/\d+|ttys\d+)", text);    // stdin really is the pty, not a pipe
        Assert.Contains("xterm-256color", text);
    }

    [Fact]
    public void Arguments_split_like_a_shell_command_line()
    {
        Assert.Equal(["-l"], LocalTransport.SplitArguments("  -l "));
        Assert.Equal(["-c", "echo hi there", "x y"], LocalTransport.SplitArguments("-c 'echo hi there' \"x y\""));
        Assert.Equal(["a b", "it's"], LocalTransport.SplitArguments(@"a\ b ""it's"""));
        Assert.Equal([""], LocalTransport.SplitArguments("''"));
        Assert.Empty(LocalTransport.SplitArguments(""));
    }

    [Fact]
    public void BuildsLocalTransport_ForLocalProfile_WithoutConnecting()
    {
        var p = new SessionProfile { Name = "Local Shell", Protocol = ProtocolKind.Local };
        using var transport = TransportFactory.Create(p);
        Assert.IsType<LocalTransport>(transport);
    }

    [Fact]
    public void Validate_DoesNotRequireHost_ForLocalProtocol()
    {
        var p = new SessionProfile { Name = "Local Shell", Protocol = ProtocolKind.Local };
        Assert.Empty(p.Validate());
    }
}
