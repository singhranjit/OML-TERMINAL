using OmlTerminal.Core.Sftp;

namespace OmlTerminal.Core.Tests;

public class SftpPathTests
{
    [Theory]
    [InlineData("/home/admin", "file.txt", "/home/admin/file.txt")]
    [InlineData("/", "file.txt", "/file.txt")]
    [InlineData("/home/admin/", "file.txt", "/home/admin/file.txt")]
    public void Combine(string dir, string name, string expected) => Assert.Equal(expected, SftpSession.Combine(dir, name));

    [Theory]
    [InlineData("/home/admin/file.txt", "/home/admin")]
    [InlineData("/home/admin", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    [InlineData("/home/admin/", "/home")]
    public void Parent(string path, string expected) => Assert.Equal(expected, SftpSession.Parent(path));
}
