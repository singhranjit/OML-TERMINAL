using System.Security.Cryptography;
using System.Text;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class MobaXtermImporterTests
{
    private const string Ini = """
        [Misc]
        Something=1

        [Bookmarks]
        SubRep=
        ImgNum=42
        Core Switch= #109#0%10.0.0.1%22%admin%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%
        Edge Router= #109#1%192.168.1.254%23%netops%%-1%
        Windows Jump= #91#4%10.0.0.50%3389%administrator%

        [Bookmarks_1]
        SubRep=Datacenter\Firewalls
        ImgNum=42
        FortiGate= #109#0%172.16.0.1%22%admin%
        Console= #109#8%COM3%9600%
        """;

    [Fact]
    public void Parse_ReadsSessionsAcrossFoldersAndProtocols()
    {
        var r = MobaXtermImporter.Parse(Ini);
        Assert.Equal(5, r.Count);

        var core = r[0].Profile;
        Assert.Equal(("Core Switch", ProtocolKind.Ssh, "10.0.0.1", 22, "admin"), (core.Name, core.Protocol, core.Host, core.Port, core.Username));
        Assert.Equal("Imported from MobaXterm", core.Folder);

        Assert.Equal(ProtocolKind.Telnet, r[1].Profile.Protocol);
        Assert.Equal(23, r[1].Profile.Port);
        Assert.Equal(ProtocolKind.Rdp, r[2].Profile.Protocol);

        var forti = r[3].Profile;
        Assert.Equal("MobaXterm/Datacenter/Firewalls", forti.Folder);
        Assert.Equal("172.16.0.1", forti.Host);

        var console = r[4].Profile;
        Assert.Equal(ProtocolKind.Serial, console.Protocol);
        Assert.Equal("COM3", console.SerialPortName);
        Assert.Equal(9600, console.BaudRate);
    }

    [Fact]
    public void Parse_IgnoresUnsupportedTypesAndNonBookmarks()
    {
        var r = MobaXtermImporter.Parse("[Bookmarks]\nSubRep=\nShellSession= #128#10%bash%\nBrowser= #128#12%http://x%\n");
        Assert.Empty(r);
    }

    [Fact]
    public void EncryptedExport_IsDetectedAndExplained()
    {
        Assert.True(MobaXtermImporter.IsEncryptedExport("_@wRMQ1LXcOGRybIneEwE8"));
        var ex = Assert.Throws<ImportException>(() => MobaXtermImporter.Parse("_@wRMQ1LXcOGRyb"));
        Assert.Contains("encrypted", ex.Message);
    }
}

public class SecureCrtImporterTests
{
    /// <summary>Encrypts a password exactly as SecureCRT's Password V2 scheme does, to prove the importer decrypts it.</summary>
    private static string EncryptV2(string password, string passphrase)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var plain = new byte[4 + pw.Length + 4 + 32];
        BitConverter.GetBytes(pw.Length).CopyTo(plain, 0);
        pw.CopyTo(plain, 4);
        // 4 zero bytes terminator already zero
        SHA256.HashData(pw).CopyTo(plain, 4 + pw.Length + 4);
        // pad to AES block
        int padded = (plain.Length + 15) / 16 * 16;
        Array.Resize(ref plain, padded);

        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return "02:" + Convert.ToHexString(aes.EncryptCbc(plain, aes.IV, PaddingMode.None));
    }

    [Fact]
    public void ParseSession_ReadsSshWithPortUserAndDecryptsV2Password()
    {
        var pw = EncryptV2("S3cr3t!", "");
        var ini = $"""
            S:"Protocol Name"=SSH2
            S:"Hostname"=10.0.0.1
            D:"[SSH2] Port"=00000016
            S:"Username"=admin
            S:"Password V2"={pw}
            """;
        var r = SecureCrtImporter.ParseSession("S:\"_session_name\"=Core-SW1\n" + ini, "SecureCRT");
        var p = Assert.Single(r).Profile;
        Assert.Equal(("Core-SW1", ProtocolKind.Ssh, "10.0.0.1", 22, "admin", "S3cr3t!"),
            (p.Name, p.Protocol, p.Host, p.Port, p.Username, p.Password));
        Assert.Null(r[0].Note);
    }

    [Fact]
    public void V2Decrypt_RoundTripsWithConfigPassphrase()
    {
        Assert.True(SecureCrtImporter.TryDecryptV2(EncryptV2("hunter2", "myphrase")[3..], "myphrase", out var pw));
        Assert.Equal("hunter2", pw);
    }

    [Fact]
    public void V2Decrypt_WrongPassphrase_IsRejectedNotGuessed()
    {
        // Self-validation must fail rather than return garbage.
        Assert.False(SecureCrtImporter.TryDecryptV2(EncryptV2("hunter2", "right")[3..], "wrong", out var pw));
        Assert.Equal("", pw);
    }

    [Fact]
    public void ParseSession_LegacyPassword_ImportsSessionWithNote()
    {
        var ini = "S:\"_session_name\"=Old\nS:\"Protocol Name\"=SSH2\nS:\"Hostname\"=10.0.0.9\nS:\"Password\"=uABCDEF\n";
        var r = SecureCrtImporter.ParseSession(ini, "SecureCRT");
        Assert.Equal("10.0.0.9", r[0].Profile.Host);
        Assert.Equal("", r[0].Profile.Password);
        Assert.Contains("older format", r[0].Note);
    }

    [Theory]
    [InlineData("TELNET", ProtocolKind.Telnet)]
    [InlineData("SERIAL", ProtocolKind.Serial)]
    [InlineData("RDP", ProtocolKind.Rdp)]
    public void ParseSession_MapsProtocols(string proto, ProtocolKind expected)
    {
        var ini = $"S:\"_session_name\"=X\nS:\"Protocol Name\"={proto}\nS:\"Hostname\"=10.0.0.1\nS:\"Serial Port\"=COM1\n";
        Assert.Equal(expected, SecureCrtImporter.ParseSession(ini, "SecureCRT")[0].Profile.Protocol);
    }

    [Fact]
    public void ScanDirectory_UsesFolderTreeAsSessionFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-scrt-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "Datacenter", "Core");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(Path.Combine(sub, "sw1.ini"), "S:\"Protocol Name\"=SSH2\nS:\"Hostname\"=10.1.1.1\n");
            var r = SecureCrtImporter.ScanDirectory(root);
            var p = Assert.Single(r).Profile;
            Assert.Equal("SecureCRT/Datacenter/Core", p.Folder);
            Assert.Equal("sw1", p.Name);
        }
        finally { Directory.Delete(root, true); }
    }
}
