using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class CredentialListImporterTests
{
    [Fact]
    public void ParsesBasicLine()
    {
        var creds = CredentialListImporter.Parse("webserver(webserver) = hunter2");
        var c = Assert.Single(creds);
        Assert.Equal("webserver", c.Name);
        Assert.Equal("webserver", c.Username);
        Assert.Equal("hunter2", c.Password);
    }

    [Fact]
    public void ParsesMultipleLinesSeparatedByBareCarriageReturn()
    {
        // WinUI's multi-line TextBox hands .Text back with a bare "\r" between lines, not "\n" or "\r\n" -
        // this is what a real paste into ImportCredentialsDialog's InputBox produces.
        var creds = CredentialListImporter.Parse("webserver(webserver) = pw1\rroot(root) = pw2\radmin(admin) = pw3");
        Assert.Equal(3, creds.Count);
        Assert.Equal("pw1", creds[0].Password);
        Assert.Equal("pw2", creds[1].Password);
        Assert.Equal("pw3", creds[2].Password);
    }

    [Fact]
    public void ParsesLineWhereNameDiffersFromUsername()
    {
        var creds = CredentialListImporter.Parse("global controller(root) = s3cr3t");
        var c = Assert.Single(creds);
        Assert.Equal("global controller", c.Name);
        Assert.Equal("root", c.Username);
        Assert.Equal("s3cr3t", c.Password);
    }

    [Fact]
    public void PasswordCanContainSpecialCharactersIncludingEqualsAndParens()
    {
        var creds = CredentialListImporter.Parse("admin(admin) = P@ss=W0rd(2024)");
        var c = Assert.Single(creds);
        Assert.Equal("P@ss=W0rd(2024)", c.Password);
    }

    [Fact]
    public void DoesNotMergeDuplicateUsernamesAcrossDifferentNamedCredentials()
    {
        var creds = CredentialListImporter.Parse("root(root) = pw-one\nglobal controller(root) = pw-two\n");
        Assert.Equal(2, creds.Count);
        Assert.Equal("pw-one", creds.Single(c => c.Name == "root").Password);
        Assert.Equal("pw-two", creds.Single(c => c.Name == "global controller").Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no equals sign here")]
    [InlineData("noparens = value")]
    [InlineData("name() = value")]
    [InlineData("name(user) = ")]
    public void SkipsMalformedLines(string line) => Assert.Empty(CredentialListImporter.Parse(line));

    [Fact]
    public void LinkByUsernameLinksUnambiguousMatchOnly()
    {
        var imported = CredentialListImporter.Parse("webserver(webserver) = pw1\nroot(root) = pw2\n");
        var s1 = new SessionProfile { Username = "webserver" };
        var s2 = new SessionProfile { Username = "someoneelse" };

        var result = CredentialListImporter.LinkByUsername([s1, s2], imported);

        Assert.NotNull(s1.CredentialId);
        Assert.Equal(imported.Single(c => c.Name == "webserver").Id, s1.CredentialId);
        Assert.Null(s2.CredentialId);
        Assert.Empty(result.AmbiguousUsernames);
    }

    [Fact]
    public void LinkByUsernameLeavesAmbiguousUsernamesUnlinkedAndReportsThem()
    {
        var imported = CredentialListImporter.Parse("root(root) = pw1\nglobal controller(root) = pw2\n");
        var session = new SessionProfile { Username = "root" };

        var result = CredentialListImporter.LinkByUsername([session], imported);

        Assert.Null(session.CredentialId);
        Assert.Contains("root", result.AmbiguousUsernames);
    }

    [Fact]
    public void LinkByUsernameNeverOverwritesAnExistingCredentialLink()
    {
        var imported = CredentialListImporter.Parse("webserver(webserver) = pw1\n");
        var existingId = Guid.NewGuid();
        var session = new SessionProfile { Username = "webserver", CredentialId = existingId };

        CredentialListImporter.LinkByUsername([session], imported);

        Assert.Equal(existingId, session.CredentialId);
    }
}
