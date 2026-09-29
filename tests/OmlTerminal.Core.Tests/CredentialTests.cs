using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Core.Tests;

public class CredentialTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"oml-cred-{Guid.NewGuid():N}.json");

    [Fact]
    public void Vault_IsNeverPlainTextOnDisk_WithoutMasterPassword()
    {
        var path = TempFile();
        try
        {
            var store = new CredentialStore(path);
            store.Save([new Credential { Name = "core", Username = "admin", Password = "S3cret!", EnablePassword = "En4ble!" }]);
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("S3cret!", raw);
            Assert.DoesNotContain("En4ble!", raw);
            Assert.Contains(LocalSecret.Prefix, raw);
            var loaded = Assert.Single(store.Load());
            Assert.Equal(("admin", "S3cret!", "En4ble!"), (loaded.Username, loaded.Password, loaded.EnablePassword));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Vault_UsesMasterPasswordWhenSet()
    {
        var path = TempFile();
        try
        {
            var protector = SecretProtector.FromPassword("master-pw", SecretProtector.NewSalt());
            new CredentialStore(path, protector).Save([new Credential { Name = "fw", Password = "pw1" }]);
            Assert.Contains(SecretProtector.Prefix, File.ReadAllText(path));
            Assert.True(new CredentialStore(path).HasMasterProtectedSecrets());
            Assert.Equal("", new CredentialStore(path).Load()[0].Password); // locked: no key, no secret
            Assert.Equal("pw1", new CredentialStore(path, protector).Load()[0].Password);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_FillsSessionFromLinkedCredential()
    {
        var cred = new Credential { Name = "tacacs", Username = "netops", Password = "p", EnablePassword = "e" };
        var session = new SessionProfile { Name = "sw1", Host = "10.0.0.1", Username = "old", CredentialId = cred.Id };
        var resolved = Credentials.Resolve(session, [cred]);
        Assert.Equal(("netops", "p", "e"), (resolved.Username, resolved.Password, resolved.EnablePassword));
        Assert.Equal("old", session.Username); // original untouched
        var unlinked = new SessionProfile { Name = "x", Password = "own" };
        Assert.Same(unlinked, Credentials.Resolve(unlinked, [cred]));
        var dangling = new SessionProfile { Name = "y", CredentialId = Guid.NewGuid(), Password = "own" };
        Assert.Equal("own", Credentials.Resolve(dangling, [cred]).Password); // deleted credential: keep own fields
    }

    [Fact]
    public void SessionStore_EncryptsEnablePasswordWithMaster()
    {
        var path = TempFile();
        try
        {
            var protector = SecretProtector.FromPassword("m", SecretProtector.NewSalt());
            var store = new JsonSessionStore(path, protector);
            store.Save([new SessionProfile { Name = "sw1", Host = "h", Port = 22, EnablePassword = "en-secret" }]);
            Assert.DoesNotContain("en-secret", File.ReadAllText(path));
            Assert.Equal("en-secret", store.Load()[0].EnablePassword);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Automator_TelnetLoginThenEnable()
    {
        var a = new LoginAutomator("admin", "pw", "en", answerLogin: true);
        Assert.Null(a.OnOutput("\r\nUser Access Verification\r\n\r\n"));
        Assert.Equal("admin\r", a.OnOutput("Username: "));
        Assert.Equal("pw\r", a.OnOutput("admin\r\nPassword: "));
        Assert.Equal("enable\r", a.OnOutput("\r\nsw1>"));
        Assert.Equal("en\r", a.OnOutput("enable\r\nPassword: "));
        Assert.Null(a.OnOutput("\r\nsw1#"));
        Assert.True(a.IsDone);
        Assert.Null(a.OnOutput("Password: ")); // never answers again
    }

    [Fact]
    public void Automator_SshSkipsEnableWhenAlreadyPrivileged()
    {
        var a = new LoginAutomator("admin", "pw", "en", answerLogin: false);
        Assert.Null(a.OnOutput("Welcome\r\ncore-rtr1#"));
        Assert.True(a.IsDone);
    }

    [Theory]
    [InlineData("admin@srx1> ")]   // Junos
    [InlineData("admin@PA-VM> ")]  // PAN-OS
    [InlineData("PS C:\\> ")]      // PowerShell
    public void Automator_DoesNotSendEnableToNonCiscoPrompts(string prompt)
    {
        var a = new LoginAutomator("", "", "en", answerLogin: false);
        Assert.Null(a.OnOutput(prompt));
    }

    [Fact]
    public void Automator_DoesNothingWithoutSecrets()
    {
        Assert.True(new LoginAutomator("", "", "", answerLogin: false).IsDone);
    }
}
