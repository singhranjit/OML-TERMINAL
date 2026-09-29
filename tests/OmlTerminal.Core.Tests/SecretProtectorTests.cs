using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class SecretProtectorTests
{
    private static readonly byte[] Salt = SecretProtector.NewSalt();

    [Fact]
    public void RoundTrips_AndCiphertextDoesNotContainThePlaintext()
    {
        var p = SecretProtector.FromPassword("correct horse", Salt);
        var sealedValue = p.Protect("s3cret-pw");
        Assert.True(SecretProtector.IsProtected(sealedValue));
        Assert.DoesNotContain("s3cret-pw", sealedValue);
        Assert.Equal("s3cret-pw", p.Unprotect(sealedValue));
    }

    [Fact]
    public void SamePlaintextGivesDifferentCiphertext()
    {
        var p = SecretProtector.FromPassword("pw", Salt);
        Assert.NotEqual(p.Protect("x"), p.Protect("x"));
    }

    [Fact]
    public void WrongPassword_OrTamperedData_ReturnsNull_NotGarbage()
    {
        var good = SecretProtector.FromPassword("right", Salt);
        var bad = SecretProtector.FromPassword("wrong", Salt);
        var sealedValue = good.Protect("secret");
        Assert.Null(bad.Unprotect(sealedValue));

        var raw = Convert.FromBase64String(sealedValue[SecretProtector.Prefix.Length..]);
        raw[^1] ^= 0x01;
        Assert.Null(good.Unprotect(SecretProtector.Prefix + Convert.ToBase64String(raw)));
        Assert.Null(good.Unprotect(SecretProtector.Prefix + "!!!not base64!!!"));
    }

    [Fact]
    public void EmptyAndAlreadyProtectedValuesAreLeftAlone()
    {
        var p = SecretProtector.FromPassword("pw", Salt);
        Assert.Equal("", p.Protect(""));
        var once = p.Protect("x");
        Assert.Equal(once, p.Protect(once));
        Assert.Equal("plain", p.Unprotect("plain"));
    }

    [Fact]
    public void VerifierAcceptsOnlyTheRightMasterPassword()
    {
        var verifier = SecretProtector.FromPassword("right", Salt).CreateVerifier();
        Assert.True(SecretProtector.FromPassword("right", Salt).Verify(verifier));
        Assert.False(SecretProtector.FromPassword("wrong", Salt).Verify(verifier));
    }
}
