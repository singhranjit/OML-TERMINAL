using Microsoft.Win32;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

/// <summary>Exercises PuTTY's real stores rather than mocking them, matching this project's existing style of testing
/// persistence against real I/O: on Windows the HKCU registry (every subkey is uniquely GUID-named and removed in
/// Dispose, so this can't collide with or leak into a real PuTTY installation's saved sessions); elsewhere a temp
/// copy of Unix PuTTY's ~/.putty/sessions file layout.</summary>
public class PuttySessionImporterTests : IDisposable
{
    private const string SessionsRoot = @"Software\SimonTatham\PuTTY\Sessions";
    private readonly List<string> _createdKeys = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oml-putty-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            using var root = Registry.CurrentUser.OpenSubKey(SessionsRoot, writable: true);
            foreach (var name in _createdKeys) root?.DeleteSubKey(name, throwOnMissingSubKey: false);
        }
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Values are strings or ints, as PuTTY stores them (REG_SZ / REG_DWORD; decimal text in the files).</summary>
    private string CreateSession(Dictionary<string, object> values, string? name = null)
    {
        name ??= "oml-test-" + Guid.NewGuid().ToString("N")[..8];
        if (OperatingSystem.IsWindows())
        {
            using var root = Registry.CurrentUser.CreateSubKey(SessionsRoot);
            using var key = root.CreateSubKey(name);
            foreach (var (k, v) in values)
                key.SetValue(k, v, v is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
        else
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(Path.Combine(_dir, name), values.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        _createdKeys.Add(name);
        return name;
    }

    private List<PuttySessionImporter.ImportedSession> Find() =>
        OperatingSystem.IsWindows() ? PuttySessionImporter.FindSessions() : PuttySessionImporter.FindSessionsInDirectory(_dir);

    [Fact]
    public void ImportsBasicSshSession()
    {
        var name = CreateSession(new() { ["HostName"] = "10.0.0.5", ["PortNumber"] = 22, ["UserName"] = "admin", ["Protocol"] = "ssh" });

        var found = Find().Single(s => s.Profile.Name == name);
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
        var name = CreateSession(new() { ["Protocol"] = "ssh" });
        Assert.DoesNotContain(Find(), s => s.Profile.Name == name);
    }

    [Fact]
    public void FlagsPpkKeysAsNeedingConversion_ButStillImportsTheSessionWithPasswordAuth()
    {
        var name = CreateSession(new() { ["HostName"] = "10.0.0.6", ["Protocol"] = "ssh", ["UserName"] = "admin", ["PublicKeyFile"] = @"C:\keys\id.ppk" });

        var found = Find().Single(s => s.Profile.Name == name);
        Assert.True(found.KeyNeedsConversion);
        Assert.Equal(SshAuthMethod.Password, found.Profile.AuthMethod);
    }

    [Fact]
    public void ImportsOpenSshFormatKeyDirectly()
    {
        var name = CreateSession(new() { ["HostName"] = "10.0.0.7", ["Protocol"] = "ssh", ["UserName"] = "admin", ["PublicKeyFile"] = @"C:\keys\id_ed25519" });

        var found = Find().Single(s => s.Profile.Name == name);
        Assert.False(found.KeyNeedsConversion);
        Assert.Equal(SshAuthMethod.PrivateKey, found.Profile.AuthMethod);
        Assert.Equal(@"C:\keys\id_ed25519", found.Profile.PrivateKeyPath);
    }

    [Fact]
    public void MapsSerialSessions_WithoutRequiringAHost()
    {
        var name = CreateSession(new() { ["Protocol"] = "serial", ["SerialLine"] = "COM3", ["SerialSpeed"] = 115200 });

        var found = Find().Single(s => s.Profile.Name == name);
        Assert.Equal(ProtocolKind.Serial, found.Profile.Protocol);
        Assert.Equal("COM3", found.Profile.SerialPortName);
        Assert.Equal(115200, found.Profile.BaudRate);
    }

    [Fact]
    public void MapsTelnetAndRlogin_ToTelnetProtocol()
    {
        var telnetName = CreateSession(new() { ["HostName"] = "10.0.0.8", ["Protocol"] = "telnet" });
        var rloginName = CreateSession(new() { ["HostName"] = "10.0.0.9", ["Protocol"] = "rlogin" });

        var all = Find();
        Assert.Equal(ProtocolKind.Telnet, all.Single(s => s.Profile.Name == telnetName).Profile.Protocol);
        Assert.Equal(ProtocolKind.Telnet, all.Single(s => s.Profile.Name == rloginName).Profile.Protocol);
    }

    [Fact]
    public void NeverImportsTheAppsOwnLegacyProfile()
    {
        // PuttyLegacyProfile.Ensure() creates a real "OML-Terminal-Legacy" session for plink's -load flag;
        // it must never show up as something for the user to import back in.
        if (OperatingSystem.IsWindows()) Transports.PuttyLegacyProfile.Ensure();
        else CreateSession(new() { ["HostName"] = "127.0.0.1", ["Protocol"] = "ssh" }, Transports.PuttyLegacyProfile.SessionName);
        Assert.DoesNotContain(Find(), s => s.Profile.Name == Transports.PuttyLegacyProfile.SessionName);
    }

    [Fact]
    public void Escaped_names_decode_to_the_session_name()
    {
        CreateSession(new() { ["HostName"] = "10.0.0.10", ["Protocol"] = "ssh" }, "core%20switch");
        Assert.Contains(Find(), s => s.Profile.Name == "core switch");
    }
}
