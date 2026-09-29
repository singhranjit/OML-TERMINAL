using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Tests;

public class PlinkStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "oml-hk-test-" + Guid.NewGuid() + ".json");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void RemembersFingerprintPerHostAndPort_CaseInsensitiveHost()
    {
        var s = new PlinkHostKeyStore(_path);
        Assert.Null(s.Get("SW1", 22));
        s.Set("SW1", 22, "SHA256:abc");
        Assert.Equal("SHA256:abc", new PlinkHostKeyStore(_path).Get("sw1", 22));
        Assert.Null(s.Get("sw1", 2222));
    }

    [Fact]
    public void LegacyFlagIsPerHost_AndDoesNotLeakIntoFingerprints()
    {
        var s = new PlinkHostKeyStore(_path);
        Assert.False(s.IsLegacyAllowed("sw1", 22));
        s.AllowLegacy("sw1", 22);
        Assert.True(new PlinkHostKeyStore(_path).IsLegacyAllowed("sw1", 22));
        Assert.False(s.IsLegacyAllowed("sw2", 22));
        Assert.Null(s.Get("sw1", 22));
    }

    [Fact]
    public void CorruptFileIsTreatedAsEmpty()
    {
        File.WriteAllText(_path, "{{{ not json");
        Assert.Null(new PlinkHostKeyStore(_path).Get("h", 22));
    }
}
