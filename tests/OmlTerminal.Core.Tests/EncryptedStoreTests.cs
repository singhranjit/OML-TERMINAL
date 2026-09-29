using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class EncryptedStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oml-enc-" + Guid.NewGuid());
    private readonly byte[] _salt = SecretProtector.NewSalt();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string File_ => Path.Combine(_dir, "sessions.json");
    private static SessionProfile P(string pw = "hunter2") => new() { Name = "sw1", Host = "10.0.0.1", Port = 22, Username = "u", Password = pw };

    [Fact]
    public void PasswordIsEncryptedOnDisk_AndDecryptedOnLoad_WithoutMutatingMemory()
    {
        var protector = SecretProtector.FromPassword("master", _salt);
        var store = new JsonSessionStore(File_, protector);
        var profile = P();
        store.Save([profile]);

        Assert.Equal("hunter2", profile.Password);
        var onDisk = File.ReadAllText(File_);
        Assert.DoesNotContain("hunter2", onDisk);
        Assert.Contains(SecretProtector.Prefix, onDisk);
        Assert.Equal("hunter2", Assert.Single(store.Load()).Password);
    }

    [Fact]
    public void WrongMasterPassword_LoadsSessionsButWithEmptyPasswords()
    {
        new JsonSessionStore(File_, SecretProtector.FromPassword("right", _salt)).Save([P()]);
        var loaded = Assert.Single(new JsonSessionStore(File_, SecretProtector.FromPassword("wrong", _salt)).Load());
        Assert.Equal("sw1", loaded.Name);
        Assert.Equal("", loaded.Password);
    }

    [Fact]
    public void PlainFileIsMigratedToEncryptedOnNextSave()
    {
        new JsonSessionStore(File_).Save([P()]);
        Assert.Contains("hunter2", File.ReadAllText(File_));

        var store = new JsonSessionStore(File_, SecretProtector.FromPassword("m", _salt));
        store.Save(store.Load());
        Assert.DoesNotContain("hunter2", File.ReadAllText(File_));
    }

    [Fact]
    public void RemovingProtector_WritesPlainAgain()
    {
        var store = new JsonSessionStore(File_, SecretProtector.FromPassword("m", _salt));
        store.Save([P()]);
        Assert.True(store.HasProtectedSecrets());
        var loaded = store.Load();
        store.Protector = null;
        store.Save(loaded);
        Assert.False(store.HasProtectedSecrets());
        Assert.Contains("hunter2", File.ReadAllText(File_));
    }

    [Fact]
    public void ValidationStillRejectsBlankEntries_WhenEncrypting()
    {
        var store = new JsonSessionStore(File_, SecretProtector.FromPassword("m", _salt));
        var bad = P(); bad.Host = "";
        Assert.Throws<SessionValidationException>(() => store.Save([bad]));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void PrivateKeyPassphraseIsEncryptedOnDisk_AndDecryptedOnLoad()
    {
        var protector = SecretProtector.FromPassword("master", _salt);
        var store = new JsonSessionStore(File_, protector);
        var profile = P();
        profile.AuthMethod = SshAuthMethod.PrivateKey;
        profile.PrivateKeyPath = @"C:\keys\id_ed25519";
        profile.PrivateKeyPassphrase = "correct-horse-battery-staple";
        store.Save([profile]);

        var onDisk = File.ReadAllText(File_);
        Assert.DoesNotContain("correct-horse-battery-staple", onDisk);
        Assert.Equal("correct-horse-battery-staple", Assert.Single(store.Load()).PrivateKeyPassphrase);
    }

    [Fact]
    public void SettingsReportMasterPasswordOnlyWhenBothFieldsPresent()
    {
        Assert.False(new AppSettings().HasMasterPassword);
        Assert.False(new AppSettings { MasterPasswordSalt = "x" }.HasMasterPassword);
        Assert.True(new AppSettings { MasterPasswordSalt = "x", MasterPasswordVerifier = "y" }.HasMasterPassword);
    }
}
