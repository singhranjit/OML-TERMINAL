using Microsoft.Win32;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

/// <summary>Exercises the real HKCU registry (like PuttyLegacyProfile already does in production code) rather
/// than mocking it, matching this project's existing style of testing persistence against real I/O. Every
/// created subkey is uniquely GUID-named and removed in Dispose, so this can't collide with or leak into a
/// real PuTTY installation's saved sessions.</summary>
public class PuttySessionImporterTests : IDisposable
{
    private const string SessionsRoot = @"Software\SimonTatham\PuTTY\Sessions";
    private readonly List<string> _createdKeys = new();

    public void Dispose()
    {
        using var root = Registry.CurrentUser.OpenSubKey(SessionsRoot, writable: true);
        foreach (var name in _createdKeys) root?.DeleteSubKey(name, throwOnMissingSubKey: false);
    }

    private string CreateSession(Action<RegistryKey> configure)
    {
        var name = "oml-test-" + Guid.NewGuid().ToString("N")[..8];
        using (var root = Registry.CurrentUser.CreateSubKey(SessionsRoot))
        using (var key = root.CreateSubKey(name))
            configure(key);
        _createdKeys.Add(name);
        return name;
    }

    [Fact]
    public void ImportsBasicSshSession()
    {
        var name = CreateSession(key =>
        {
            key.SetValue("HostName", "10.0.0.5");
            key.SetValue("PortNumber", 22, RegistryValueKind.DWord);
            key.SetValue("UserName", "admin");
            key.SetValue("Protocol", "ssh");
        });

        var found = PuttySessionImporter.FindSessions().Single(s => s.Profile.Name == name);
        Assert.Equal("10.0.0.5", found.Profile.Host);
        Assert.Equal(22, found.Profile.Port);
        Assert.Equal("admin", found.Profile.Username);
        Assert.Equal(ProtocolKind.Ssh, found.Profile.Protocol);
        Assert.Equal("Imported from PuTTY", found.Profile.Folder);
        Assert.False(found.KeyNeedsConversion);
    }

    [Fact]
    public void SkipsBlankTemplateSessions()
    {
        var name = CreateSession(key => key.SetValue("Protocol", "ssh"));
        Assert.DoesNotContain(PuttySessionImporter.FindSessions(), s => s.Profile.Name == name);
    }

    [Fact]
    public void FlagsPpkKeysAsNeedingConversion_ButStillImportsTheSessionWithPasswordAuth()
    {
        var name = CreateSession(key =>
        {
            key.SetValue("HostName", "10.0.0.6");
            key.SetValue("Protocol", "ssh");
            key.SetValue("UserName", "admin");
            key.SetValue("PublicKeyFile", @"C:\keys\id.ppk");
        });

        var found = PuttySessionImporter.FindSessions().Single(s => s.Profile.Name == name);
        Assert.True(found.KeyNeedsConversion);
        Assert.Equal(SshAuthMethod.Password, found.Profile.AuthMethod);
    }

    [Fact]
    public void ImportsOpenSshFormatKeyDirectly()
    {
        var name = CreateSession(key =>
        {
            key.SetValue("HostName", "10.0.0.7");
            key.SetValue("Protocol", "ssh");
            key.SetValue("UserName", "admin");
            key.SetValue("PublicKeyFile", @"C:\keys\id_ed25519");
        });

        var found = PuttySessionImporter.FindSessions().Single(s => s.Profile.Name == name);
        Assert.False(found.KeyNeedsConversion);
        Assert.Equal(SshAuthMethod.PrivateKey, found.Profile.AuthMethod);
        Assert.Equal(@"C:\keys\id_ed25519", found.Profile.PrivateKeyPath);
    }

    [Fact]
    public void MapsSerialSessions_WithoutRequiringAHost()
    {
        var name = CreateSession(key =>
        {
            key.SetValue("Protocol", "serial");
            key.SetValue("SerialLine", "COM3");
            key.SetValue("SerialSpeed", 115200, RegistryValueKind.DWord);
        });

        var found = PuttySessionImporter.FindSessions().Single(s => s.Profile.Name == name);
        Assert.Equal(ProtocolKind.Serial, found.Profile.Protocol);
        Assert.Equal("COM3", found.Profile.SerialPortName);
        Assert.Equal(115200, found.Profile.BaudRate);
    }

    [Fact]
    public void MapsTelnetAndRlogin_ToTelnetProtocol()
    {
        var telnetName = CreateSession(key => { key.SetValue("HostName", "10.0.0.8"); key.SetValue("Protocol", "telnet"); });
        var rloginName = CreateSession(key => { key.SetValue("HostName", "10.0.0.9"); key.SetValue("Protocol", "rlogin"); });

        var all = PuttySessionImporter.FindSessions();
        Assert.Equal(ProtocolKind.Telnet, all.Single(s => s.Profile.Name == telnetName).Profile.Protocol);
        Assert.Equal(ProtocolKind.Telnet, all.Single(s => s.Profile.Name == rloginName).Profile.Protocol);
    }

    [Fact]
    public void NeverImportsTheAppsOwnLegacyProfile()
    {
        // PuttyLegacyProfile.Ensure() creates a real "OML-Terminal-Legacy" session for plink's -load flag;
        // it must never show up as something for the user to import back in.
        Transports.PuttyLegacyProfile.Ensure();
        Assert.DoesNotContain(PuttySessionImporter.FindSessions(), s => s.Profile.Name == Transports.PuttyLegacyProfile.SessionName);
    }
}
