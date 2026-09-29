using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oml-tests-" + Guid.NewGuid());

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private JsonSessionStore Store() => new(Path.Combine(_dir, "sessions.json"));

    private static SessionProfile Good(string name = "core-sw1") =>
        new() { Name = name, Host = "10.0.0.1", Port = 22, Username = "admin", Password = "pw" };

    [Theory]
    [InlineData("", "10.0.0.1")]
    [InlineData("   ", "10.0.0.1")]
    [InlineData("sw", "")]
    [InlineData("sw", "  ")]
    public void Save_RejectsBlankNameOrHost_AndWritesNothing(string name, string host)
    {
        var store = Store();
        var bad = Good(); bad.Name = name; bad.Host = host;
        Assert.Throws<SessionValidationException>(() => store.Save([Good(), bad]));
        Assert.False(File.Exists(store.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Save_RejectsBadPort(int port)
    {
        var s = Good(); s.Port = port;
        Assert.Throws<SessionValidationException>(() => Store().Save([s]));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var s = Good(); s.Protocol = ProtocolKind.Ssh; s.Engine = TransportEngine.Plink; s.Folder = "Lab";
        var store = Store();
        store.Save([s]);
        var loaded = Assert.Single(store.Load());
        Assert.Equal(s.Id, loaded.Id);
        Assert.Equal(TransportEngine.Plink, loaded.Engine);
        Assert.Equal("Lab", loaded.Folder);
        Assert.Equal("pw", loaded.Password);
    }

    [Fact]
    public void Save_FailureDoesNotDestroyExistingFile()
    {
        var store = Store();
        store.Save([Good("keep")]);
        var bad = Good(); bad.Name = "";
        Assert.Throws<SessionValidationException>(() => store.Save([bad]));
        Assert.Equal("keep", Assert.Single(store.Load()).Name);
    }

    [Fact]
    public void Load_DropsCorruptEntriesAndSurvivesGarbage()
    {
        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.Path, """[{"Name":"","Host":""},{"Name":"ok","Host":"h","Port":22}]""");
        Assert.Equal("ok", Assert.Single(store.Load()).Name);

        File.WriteAllText(store.Path, "not json {{{");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Settings_RoundTripAndDefaults()
    {
        var store = new JsonSettingsStore(Path.Combine(_dir, "settings.json"));
        Assert.Equal("Cascadia Mono", store.Load().FontFamily);
        store.Save(new AppSettings { FontSize = 18 });
        Assert.Equal(18, store.Load().FontSize);
    }

    [Fact]
    public void DefaultPorts()
    {
        Assert.Equal(22, SessionProfile.DefaultPortFor(ProtocolKind.Ssh));
        Assert.Equal(23, SessionProfile.DefaultPortFor(ProtocolKind.Telnet));
    }

    [Fact]
    public void KeyAuth_RequiresPrivateKeyPath()
    {
        var s = Good(); s.AuthMethod = SshAuthMethod.PrivateKey;
        Assert.Contains(s.Validate(), e => e.Contains("Private key file"));
        s.PrivateKeyPath = @"C:\keys\id_ed25519";
        Assert.DoesNotContain(s.Validate(), e => e.Contains("Private key file"));
    }

    [Fact]
    public void KeyAuth_RejectsPlinkEngine()
    {
        var s = Good();
        s.AuthMethod = SshAuthMethod.PrivateKey;
        s.PrivateKeyPath = @"C:\keys\id_ed25519";
        s.Engine = TransportEngine.Plink;
        Assert.Contains(s.Validate(), e => e.Contains("PuTTY"));
    }

    [Fact]
    public void KeyAuth_NotRequiredForPasswordAuth()
    {
        var s = Good(); // AuthMethod defaults to Password
        Assert.Empty(s.Validate());
    }
}
