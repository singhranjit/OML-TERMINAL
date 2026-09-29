using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class SessionCsvTests
{
    private static SessionProfile Good(string name = "core-sw1") =>
        new() { Name = name, Host = "10.0.0.1", Port = 22, Username = "admin", Password = "pw", Folder = "Lab" };

    [Fact]
    public void ExportThenImport_RoundTripsFields_WhenSecretsIncluded()
    {
        var csv = SessionCsv.Export([Good()], includeSecrets: true);
        var imported = Assert.Single(SessionCsv.Import(csv));
        Assert.Equal("core-sw1", imported.Name);
        Assert.Equal("Lab", imported.Folder);
        Assert.Equal("10.0.0.1", imported.Host);
        Assert.Equal(22, imported.Port);
        Assert.Equal("admin", imported.Username);
        Assert.Equal("pw", imported.Password);
        Assert.Equal(ProtocolKind.Ssh, imported.Protocol);
    }

    [Fact]
    public void Export_OmitsPassword_ButKeepsKeyPath_WhenSecretsNotIncluded()
    {
        var s = Good();
        s.AuthMethod = SshAuthMethod.PrivateKey;
        s.PrivateKeyPath = @"C:\keys\id_ed25519";
        var csv = SessionCsv.Export([s], includeSecrets: false);
        Assert.DoesNotContain("pw", csv);
        // PrivateKeyPath is a real filesystem path, not a secret, and is kept even with includeSecrets: false.
        Assert.Contains(@"C:\keys\id_ed25519", csv);
    }

    [Fact]
    public void Import_HandlesQuotedFieldsWithEmbeddedCommas()
    {
        var csv = "Name,Folder,Protocol,Host,Port,Username,Password,AuthMethod,PrivateKeyPath\r\n"
                 + "\"core, sw1\",\"Site A, Rack 3\",Ssh,10.0.0.1,22,admin,,Password,\r\n";
        var imported = Assert.Single(SessionCsv.Import(csv));
        Assert.Equal("core, sw1", imported.Name);
        Assert.Equal("Site A, Rack 3", imported.Folder);
    }

    [Fact]
    public void Import_SkipsRowsThatFailValidation()
    {
        var csv = "Name,Folder,Protocol,Host,Port,Username,Password,AuthMethod,PrivateKeyPath\r\n"
                 + ",,Ssh,,,,,Password,\r\n" // blank name AND host - invalid
                 + "ok,,Ssh,10.0.0.2,22,admin,,Password,\r\n";
        var imported = Assert.Single(SessionCsv.Import(csv));
        Assert.Equal("ok", imported.Name);
    }

    [Fact]
    public void Import_DefaultsPort_WhenMissingOrZero()
    {
        var csv = "Name,Folder,Protocol,Host,Port,Username,Password,AuthMethod,PrivateKeyPath\r\n"
                 + "sw1,,Ssh,10.0.0.1,,admin,,Password,\r\n";
        var imported = Assert.Single(SessionCsv.Import(csv));
        Assert.Equal(22, imported.Port); // SessionProfile.DefaultPortFor(Ssh)
    }

    [Fact]
    public void Import_UnknownProtocol_FallsBackToSsh()
    {
        var csv = "Name,Folder,Protocol,Host,Port,Username,Password,AuthMethod,PrivateKeyPath\r\n"
                 + "sw1,,BogusProto,10.0.0.1,22,admin,,Password,\r\n";
        var imported = Assert.Single(SessionCsv.Import(csv));
        Assert.Equal(ProtocolKind.Ssh, imported.Protocol);
    }

    [Fact]
    public void Import_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(SessionCsv.Import(""));
    }

    [Fact]
    public void Import_KeyAuthRow_MapsAuthMethodAndPath()
    {
        var csv = "Name,Folder,Protocol,Host,Port,Username,Password,AuthMethod,PrivateKeyPath\r\n"
                 + "sw1,,Ssh,10.0.0.1,22,admin,,PrivateKey,\"C:\\keys\\id_ed25519\"\r\n";
        var imported = Assert.Single(SessionCsv.Import(csv));
        Assert.Equal(SshAuthMethod.PrivateKey, imported.AuthMethod);
        Assert.Equal(@"C:\keys\id_ed25519", imported.PrivateKeyPath);
    }
}
