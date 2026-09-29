using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Shells;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Tests;

public class ToolingTests
{
    [Fact]
    public void Backup_StripsEchoAndTrailingPrompt()
    {
        var raw = "fw01 # show full-configuration\r\n#config-version=FGT60F\r\nconfig system global\r\nend\r\n\r\nfw01 # ";
        Assert.Equal("#config-version=FGT60F\nconfig system global\nend", ConfigBackup.StripEchoAndPrompt(raw, "show full-configuration"));
    }

    [Fact]
    public void Backup_NormalizeMakesEquivalentOutputIdentical()
    {
        Assert.Equal(ConfigBackup.Normalize("a  \r\nb\r\n\r\n"), ConfigBackup.Normalize("a\nb"));
    }

    [Fact]
    public void Backup_DiffIgnoresVolatileLinesAndOrder()
    {
        var before = "! Last configuration change at 10:00\nhostname sw1\ninterface Gi1\n shutdown\n";
        var after = "! Last configuration change at 11:00\ninterface Gi1\nhostname sw1\n no shutdown\n";
        var (added, removed) = ConfigBackup.Diff(before, after);
        Assert.Equal([" no shutdown"], added);
        Assert.Equal([" shutdown"], removed);
    }

    [Fact]
    public void Backup_TargetPathFindsPreviousBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-bk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dev = new SessionProfile { Name = "core fw/1", Host = "10.0.0.1" };
            var (first, prev) = ConfigBackup.TargetPath(root, dev, ".cfg");
            Assert.Null(prev);
            Assert.Contains("core_fw_1", first);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            File.WriteAllText(first, "x");
            var (_, prev2) = ConfigBackup.TargetPath(root, dev, ".cfg");
            Assert.Equal(first, prev2);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Backup_PresetsAllHaveCommands()
    {
        Assert.All(ConfigBackup.Presets, p => Assert.NotEmpty(p.Commands));
        Assert.Equal(["term len 0", "show run"], ConfigBackup.Custom(BackupMode.Shell, "term len 0\r\n\r\nshow run\n").Commands);
    }

    [Fact]
    public void Shells_ParsesUtf16WslListing()
    {
        Assert.Equal(["Ubuntu", "kali-linux"], ShellCatalog.ParseWslList("Ubuntu\r\n\0kali-linux\r\n\r\n"));
        Assert.Empty(ShellCatalog.ParseWslList("Windows Subsystem for Linux has no installed distributions.\r\n"));
    }

    [Fact]
    public void Shells_CygwinGetsStayInCwdEnvironment()
    {
        Assert.Equal("1", ShellCatalog.EnvironmentFor(@"C:\cygwin64\bin\bash.exe")!["CHERE_INVOKING"]);
        Assert.Null(ShellCatalog.EnvironmentFor(@"C:\Windows\System32\cmd.exe"));
    }

    [Fact]
    public void LocalTransport_QuotesPathsWithSpaces()
    {
        Assert.Equal("\"C:\\Program Files\\Git\\bin\\bash.exe\" --login -i",
            LocalTransport.BuildCommandLine(@"C:\Program Files\Git\bin\bash.exe", "--login -i"));
        Assert.Equal(@"C:\Windows\System32\cmd.exe", LocalTransport.BuildCommandLine(@"C:\Windows\System32\cmd.exe", ""));
    }

    [Fact]
    public void OpenSsh_BuildsArgumentsWithX11AndKey()
    {
        var p = new SessionProfile
        {
            Name = "lab", Host = "10.0.0.5", Port = 2222, Username = "admin", Engine = TransportEngine.OpenSsh,
            X11Forwarding = true, AuthMethod = SshAuthMethod.PrivateKey, PrivateKeyPath = @"C:\keys\my key",
        };
        Assert.Equal("-o ServerAliveInterval=30 -Y -p 2222 -l admin -i \"C:\\keys\\my key\" 10.0.0.5", OpenSshTransport.BuildArguments(p));
    }

    [Fact]
    public void Profile_X11RequiresOpenSshEngine()
    {
        var p = new SessionProfile { Name = "x", Host = "h", Port = 22, X11Forwarding = true, Engine = TransportEngine.BuiltIn };
        Assert.Contains(p.Validate(), e => e.Contains("X11"));
        p.Engine = TransportEngine.OpenSsh;
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void Profile_SftpIsSshBasedAndValidatesPort()
    {
        var p = new SessionProfile { Name = "files", Protocol = ProtocolKind.Sftp, Host = "h", Port = 0 };
        Assert.True(p.IsSshBased);
        Assert.Contains(p.Validate(), e => e.Contains("Port"));
        Assert.Equal(22, SessionProfile.DefaultPortFor(ProtocolKind.Sftp));
    }
}
