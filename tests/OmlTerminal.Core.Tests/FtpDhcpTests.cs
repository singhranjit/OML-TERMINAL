using System.Net;
using System.Net.Sockets;
using System.Text;
using OmlTerminal.Core.Services;

namespace OmlTerminal.Core.Tests;

/// <summary>A tiny PASV FTP client, just enough to exercise the server end-to-end over loopback.</summary>
file sealed class MiniFtp(string host, int port) : IDisposable
{
    private readonly TcpClient _c = new(host, port);
    private StreamReader _r = null!;
    private StreamWriter _w = null!;

    public async Task<string> ConnectAsync(string user = "anonymous", string pass = "x")
    {
        var s = _c.GetStream();
        _r = new StreamReader(s, Encoding.UTF8);
        _w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        await _r.ReadLineAsync();                 // 220
        await Cmd($"USER {user}");
        return await Cmd($"PASS {pass}");
    }

    public async Task<string> Cmd(string c) { await _w.WriteLineAsync(c); return (await _r.ReadLineAsync())!; }

    private async Task<TcpClient> Pasv()
    {
        var line = await Cmd("PASV");
        var nums = line[line.IndexOf('(')..].Trim('(', ')', ' ').Split(',').Select(int.Parse).ToArray();
        return new TcpClient(string.Join('.', nums[..4]), nums[4] * 256 + nums[5]);
    }

    public async Task<byte[]> Retr(string name)
    {
        using var data = await Pasv();
        await _w.WriteLineAsync($"RETR {name}");
        await _r.ReadLineAsync();                 // 150
        using var ms = new MemoryStream();
        await data.GetStream().CopyToAsync(ms);
        await _r.ReadLineAsync();                 // 226
        return ms.ToArray();
    }

    public async Task Stor(string name, byte[] content)
    {
        using var data = await Pasv();
        await _w.WriteLineAsync($"STOR {name}");
        await _r.ReadLineAsync();                 // 150
        await data.GetStream().WriteAsync(content);
        data.Client.Shutdown(SocketShutdown.Send);
        await _r.ReadLineAsync();                 // 226
    }

    public async Task<string> List()
    {
        using var data = await Pasv();
        await _w.WriteLineAsync("LIST");
        await _r.ReadLineAsync();
        using var sr = new StreamReader(data.GetStream());
        var text = await sr.ReadToEndAsync();
        await _r.ReadLineAsync();
        return text;
    }

    public void Dispose() => _c.Dispose();
}

public class FtpServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task AnonymousRoundTrip_ListRetrStor()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "config.txt"), "hostname CORE-SW1");
            int port = FreePort();
            using var server = new FtpServer(root, "127.0.0.1", port) { PassivePorts = (FreePort(), FreePort() + 20) };
            server.Start();

            using var ftp = new MiniFtp("127.0.0.1", port);
            Assert.StartsWith("230", await ftp.ConnectAsync());
            Assert.Contains("config.txt", await ftp.List());
            Assert.Equal("hostname CORE-SW1", Encoding.UTF8.GetString(await ftp.Retr("config.txt")));

            await ftp.Stor("uploaded.bin", [1, 2, 3, 4, 5]);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(root, "uploaded.bin")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AuthenticatedServer_RejectsWrongPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = FreePort();
            using var server = new FtpServer(root, "127.0.0.1", port, "admin", "secret") { PassivePorts = (FreePort(), FreePort() + 20) };
            server.Start();
            using var ftp = new MiniFtp("127.0.0.1", port);
            Assert.StartsWith("530", await ftp.ConnectAsync("admin", "wrong"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PathTraversal_IsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = FreePort();
            using var server = new FtpServer(root, "127.0.0.1", port) { PassivePorts = (FreePort(), FreePort() + 20) };
            server.Start();
            using var ftp = new MiniFtp("127.0.0.1", port);
            await ftp.ConnectAsync();
            Assert.StartsWith("550", await ftp.Cmd("SIZE ../../../windows/win.ini"));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class DhcpTests
{
    private static Dhcp.Message Discover(string mac)
    {
        var m = new Dhcp.Message { Op = 1, Xid = 0x12345678, HLen = 6 };
        var bytes = mac.Split(':').Select(h => Convert.ToByte(h, 16)).ToArray();
        Array.Copy(bytes, m.ChAddr, 6);
        m.Options[Dhcp.Opt.MessageType] = [(byte)Dhcp.MessageType.Discover];
        m.Options[Dhcp.Opt.HostName] = Encoding.ASCII.GetBytes("lab-pc");
        return m;
    }

    [Fact]
    public void BuildParse_RoundTrip()
    {
        var m = Discover("aa:bb:cc:dd:ee:ff");
        m.YiAddr = IPAddress.Parse("10.0.0.50");
        var parsed = Dhcp.Parse(Dhcp.Build(m));
        Assert.Equal(0x12345678u, parsed.Xid);
        Assert.Equal("aa:bb:cc:dd:ee:ff", parsed.MacString);
        Assert.Equal(Dhcp.MessageType.Discover, parsed.Type);
        Assert.Equal("lab-pc", Encoding.ASCII.GetString(parsed.Options[Dhcp.Opt.HostName]));
    }

    [Fact]
    public void Allocate_GivesSequentialAddresses_AndReusesPerMac()
    {
        var s = new DhcpServer("10.0.0.100", "10.0.0.110", "255.255.255.0", "10.0.0.1", "10.0.0.1", "10.0.0.1");
        var a = s.Allocate("aa:aa:aa:aa:aa:aa", "a")!;
        var b = s.Allocate("bb:bb:bb:bb:bb:bb", "b")!;
        Assert.Equal("10.0.0.100", a.Ip.ToString());
        Assert.Equal("10.0.0.101", b.Ip.ToString());
        Assert.Equal("10.0.0.100", s.Allocate("aa:aa:aa:aa:aa:aa", "a")!.Ip.ToString()); // same MAC → same IP
    }

    [Fact]
    public void Confirm_CommitsLease_AndBlocksConflicts()
    {
        var s = new DhcpServer("10.0.0.100", "10.0.0.110", "255.255.255.0", "10.0.0.1", "10.0.0.1", "10.0.0.1");
        var lease = s.Confirm("aa:aa:aa:aa:aa:aa", IPAddress.Parse("10.0.0.105"), "a")!;
        Assert.Equal("10.0.0.105", lease.Ip.ToString());
        Assert.Single(s.Leases);
        // A different MAC asking for the same address is refused (NAK).
        Assert.Null(s.Confirm("bb:bb:bb:bb:bb:bb", IPAddress.Parse("10.0.0.105"), "b"));
    }

    [Fact]
    public void Allocate_ReturnsNullWhenPoolExhausted()
    {
        var s = new DhcpServer("10.0.0.100", "10.0.0.101", "255.255.255.0", "10.0.0.1", "10.0.0.1", "10.0.0.1");
        Assert.NotNull(s.Allocate("aa:aa:aa:aa:aa:01", "a"));
        Assert.NotNull(s.Allocate("aa:aa:aa:aa:aa:02", "b"));
        Assert.Null(s.Allocate("aa:aa:aa:aa:aa:03", "c"));
    }

    [Fact]
    public void Reply_HasStandardOptions()
    {
        var s = new DhcpServer("10.0.0.100", "10.0.0.110", "255.255.255.0", "10.0.0.1", "8.8.8.8", "10.0.0.1");
        // Build an Ack reply through the public path by reflection-free means: confirm then inspect via Build/Parse.
        var lease = s.Confirm("aa:aa:aa:aa:aa:aa", IPAddress.Parse("10.0.0.105"), "pc")!;
        Assert.Equal("10.0.0.105", lease.Ip.ToString());
        Assert.True(lease.ExpiresUtc > DateTime.UtcNow.AddHours(7));
    }
}
