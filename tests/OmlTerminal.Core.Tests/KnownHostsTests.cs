using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Core.Tests;

public class KnownHostsTests
{
    private static KnownHostStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "oml-kh-" + Guid.NewGuid().ToString("N") + ".json");
        return new KnownHostStore(path);
    }

    [Fact]
    public void FirstKeyIsNewThenMatchesThenAChangedKeyIsAMismatch()
    {
        var store = NewStore(out var path);
        try
        {
            Assert.Equal(HostKeyStatus.New, store.Check("sw1", 22, "ssh-ed25519", "SHA256:aaa").Status);
            store.Trust("sw1", 22, "ssh-ed25519", "SHA256:aaa");
            Assert.Equal(HostKeyStatus.Match, store.Check("SW1", 22, "ssh-ed25519", "SHA256:aaa").Status);

            var (status, stored) = store.Check("sw1", 22, "ssh-ed25519", "SHA256:bbb");
            Assert.Equal(HostKeyStatus.Mismatch, status);
            Assert.Equal("SHA256:aaa", stored!.Fingerprint);

            // Another key type, or the same host on another port, is a separate entry - not a mismatch.
            Assert.Equal(HostKeyStatus.New, store.Check("sw1", 22, "rsa-sha2-256", "SHA256:ccc").Status);
            Assert.Equal(HostKeyStatus.New, store.Check("sw1", 2222, "ssh-ed25519", "SHA256:bbb").Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void KeysPersistAndCanBeForgotten()
    {
        var store = NewStore(out var path);
        try
        {
            store.Trust("10.0.0.1", 22, "ssh-ed25519", "SHA256:aaa");
            store.Trust("10.0.0.1", 22, "rsa-sha2-256", "SHA256:bbb");

            var reloaded = new KnownHostStore(path);
            Assert.Equal(HostKeyStatus.Match, reloaded.Check("10.0.0.1", 22, "ssh-ed25519", "SHA256:aaa").Status);
            Assert.Equal(2, reloaded.Forget("10.0.0.1", 22));
            Assert.Equal(HostKeyStatus.New, new KnownHostStore(path).Check("10.0.0.1", 22, "ssh-ed25519", "SHA256:zzz").Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MismatchMessageExplainsTheRiskAndTheFix()
    {
        var ex = new HostKeyMismatchException("sw1", 22, "ssh-ed25519 SHA256:aaa", "ssh-ed25519 SHA256:bbb");
        Assert.Contains("CHANGED", ex.Message);
        Assert.Contains("Forget saved host key", ex.Message);
    }
}
