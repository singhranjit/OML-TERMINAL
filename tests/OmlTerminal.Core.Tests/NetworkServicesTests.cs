using System.Net;
using System.Net.Sockets;
using OmlTerminal.Core.Services;

namespace OmlTerminal.Core.Tests;

public class TftpTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "oml-tftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static int FreeUdpPort()
    {
        using var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public void ParseRequest_ReadsFilenameModeAndOptions()
    {
        var packet = Tftp.Request(Tftp.Op.Rrq, "core.cfg", "octet", new Dictionary<string, string> { ["blksize"] = "1024", ["tsize"] = "0" });
        var (name, mode, options) = Tftp.ParseRequest(packet);
        Assert.Equal(("core.cfg", "octet"), (name, mode));
        Assert.Equal("1024", options["blksize"]);
        Assert.Equal("0", options["tsize"]);
    }

    [Theory]
    [InlineData(512, 1000)]     // multi-block, last block partial
    [InlineData(512, 1024)]     // exact multiple - needs a final empty block
    [InlineData(1024, 300000)]  // large file, negotiated block size
    [InlineData(512, 0)]        // empty file
    public async Task ServerAndClient_RoundTripAFile(int blockSize, int size)
    {
        var serverRoot = TempDir();
        var clientDir = TempDir();
        try
        {
            var content = new byte[size];
            new Random(size).NextBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(serverRoot, "firmware.bin"), content);

            int port = FreeUdpPort();
            using var server = new TftpServer(serverRoot, "127.0.0.1", port, allowWrite: true, allowOverwrite: true);
            server.Start();

            // GET
            var client = new TftpClient { BlockSize = blockSize };
            var downloaded = Path.Combine(clientDir, "got.bin");
            await client.GetAsync("127.0.0.1", "firmware.bin", downloaded, port);
            Assert.Equal(content, await File.ReadAllBytesAsync(downloaded));

            // PUT
            await client.PutAsync("127.0.0.1", downloaded, "uploaded.bin", port);
            await Task.Delay(200);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(serverRoot, "uploaded.bin")));
        }
        finally { Directory.Delete(serverRoot, true); Directory.Delete(clientDir, true); }
    }

    [Fact]
    public async Task Get_MissingFile_Throws()
    {
        var root = TempDir();
        try
        {
            int port = FreeUdpPort();
            using var server = new TftpServer(root, "127.0.0.1", port);
            server.Start();
            var client = new TftpClient();
            await Assert.ThrowsAsync<IOException>(() =>
                client.GetAsync("127.0.0.1", "nope.bin", Path.Combine(root, "x"), port));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Server_RefusesPathTraversal()
    {
        var root = TempDir();
        try
        {
            int port = FreeUdpPort();
            using var server = new TftpServer(root, "127.0.0.1", port);
            server.Start();
            var client = new TftpClient();
            await Assert.ThrowsAsync<IOException>(() =>
                client.GetAsync("127.0.0.1", "../../../windows/win.ini", Path.Combine(root, "x"), port));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Server_ReadOnly_RejectsUploads()
    {
        var root = TempDir();
        try
        {
            int port = FreeUdpPort();
            using var server = new TftpServer(root, "127.0.0.1", port, allowWrite: false);
            server.Start();
            await File.WriteAllTextAsync(Path.Combine(root, "src.txt"), "hi");
            await Assert.ThrowsAsync<IOException>(() =>
                new TftpClient().PutAsync("127.0.0.1", Path.Combine(root, "src.txt"), "dst.txt", port));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class SyslogSntpTests
{
    [Theory]
    [InlineData("<189>45: *Mar  1 00:01:02: %LINK-3-UPDOWN: Interface Gi0/1, changed state to down", 5, "%LINK-3-UPDOWN")]
    [InlineData("<0>emergency message", 0, "emergency message")]
    [InlineData("plain text without pri", 6, "plain text without pri")]
    public void Syslog_ParsesSeverityAndBody(string raw, int severity, string contains)
    {
        var (sev, text) = SyslogServer.Parse(raw);
        Assert.Equal(severity, sev);
        Assert.Contains(contains, text);
    }

    [Fact]
    public void Sntp_ReplyIsServerModeAndEchoesOriginate()
    {
        var request = new byte[48];
        request[0] = 0b00_100_011; // client mode 3
        for (int i = 40; i < 48; i++) request[i] = (byte)i; // transmit timestamp
        var reply = SntpServer.BuildReply(request, DateTime.UtcNow);
        Assert.Equal(48, reply.Length);
        Assert.Equal(4, reply[0] & 0b111);          // mode 4 = server
        Assert.Equal(2, reply[1]);                  // stratum 2
        Assert.Equal(request.Skip(40).Take(8), reply.Skip(24).Take(8)); // originate echoes client transmit
    }

    [Fact]
    public async Task Sntp_ServesRealTimeOverUdp()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var server = new SntpServer("127.0.0.1", port);
        server.Start();
        using var client = new UdpClient();
        var request = new byte[48];
        request[0] = 0b00_100_011;
        await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, port));
        var reply = await client.ReceiveAsync(new CancellationTokenSource(3000).Token);
        Assert.Equal(48, reply.Buffer.Length);
        Assert.Equal(4, reply.Buffer[0] & 0b111);
    }
}
