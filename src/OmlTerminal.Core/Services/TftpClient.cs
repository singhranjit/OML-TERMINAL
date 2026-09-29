using System.Net;
using System.Net.Sockets;

namespace OmlTerminal.Core.Services;

/// <summary>A TFTP client: fetch a file from, or push a file to, a remote TFTP server (a switch, router or another PC).</summary>
public sealed class TftpClient
{
    public int BlockSize { get; init; } = 512;
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>Downloads <paramref name="remoteFile"/> from the server into <paramref name="localPath"/>.</summary>
    public async Task GetAsync(string host, string remoteFile, string localPath, int port = Tftp.DefaultPort, Action<long>? progress = null, CancellationToken ct = default)
    {
        var server = await Resolve(host, port, ct);
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.ReceiveTimeout = TimeoutMs;
        var options = BlockSize != 512 ? new Dictionary<string, string> { ["blksize"] = BlockSize.ToString() } : null;

        await socket.SendAsync(Tftp.Request(Tftp.Op.Rrq, remoteFile, "octet", options), server, ct);
        await using var file = File.Create(localPath);

        int blockSize = BlockSize;
        ushort expected = 1;
        long total = 0;
        IPEndPoint? tid = null;
        // First reply establishes the server's transfer port (TID); an OACK confirms options, else it's block 1.
        while (true)
        {
            var res = await ReceiveAsync(socket, ct);
            tid ??= res.RemoteEndPoint;
            var op = Tftp.PeekOp(res.Buffer);
            if (op == Tftp.Op.Error) throw new IOException(ErrorText(res.Buffer));
            if (op == Tftp.Op.OAck) { await socket.SendAsync(Tftp.Ack(0), tid, ct); continue; }
            if (op != Tftp.Op.Data) continue;
            if (Tftp.Block(res.Buffer) != expected) { await socket.SendAsync(Tftp.Ack((ushort)(expected - 1)), tid, ct); continue; }

            var data = res.Buffer.AsMemory(4);
            await file.WriteAsync(data, ct);
            total += data.Length;
            progress?.Invoke(total);
            await socket.SendAsync(Tftp.Ack(expected), tid, ct);
            expected++;
            if (data.Length < blockSize) return;
        }
    }

    /// <summary>Uploads <paramref name="localPath"/> to the server as <paramref name="remoteFile"/>.</summary>
    public async Task PutAsync(string host, string localPath, string remoteFile, int port = Tftp.DefaultPort, Action<long>? progress = null, CancellationToken ct = default)
    {
        var server = await Resolve(host, port, ct);
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.ReceiveTimeout = TimeoutMs;

        // Negotiate the block size so the server uses the same size to detect end-of-transfer.
        var options = BlockSize != 512 ? new Dictionary<string, string> { ["blksize"] = BlockSize.ToString() } : null;
        await socket.SendAsync(Tftp.Request(Tftp.Op.Wrq, remoteFile, "octet", options), server, ct);
        var first = await ReceiveAsync(socket, ct);
        var tid = first.RemoteEndPoint;
        if (Tftp.PeekOp(first.Buffer) == Tftp.Op.Error) throw new IOException(ErrorText(first.Buffer));
        // OACK accepts our options; a plain ACK 0 means the server ignored them, so fall back to 512.
        int blockSize = 512;
        if (Tftp.PeekOp(first.Buffer) == Tftp.Op.OAck && Tftp.ParseOptions(first.Buffer).TryGetValue("blksize", out var bs) && int.TryParse(bs, out var v))
            blockSize = v;

        await using var file = File.OpenRead(localPath);
        var buffer = new byte[blockSize];
        ushort block = 1;
        long total = 0;
        int read;
        do
        {
            read = await file.ReadAsync(buffer.AsMemory(0, blockSize), ct);
            if (!await SendAndAwaitAckAsync(socket, tid, Tftp.Data(block, buffer.AsSpan(0, read)), block, ct))
                throw new IOException($"No ACK for block {block}");
            total += read;
            progress?.Invoke(total);
            block++;
        } while (read == blockSize && !ct.IsCancellationRequested);
    }

    private async Task<bool> SendAndAwaitAckAsync(UdpClient socket, IPEndPoint tid, byte[] packet, ushort block, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await socket.SendAsync(packet, tid, ct);
            try
            {
                var res = await ReceiveAsync(socket, ct);
                if (Tftp.PeekOp(res.Buffer) == Tftp.Op.Ack && Tftp.Block(res.Buffer) == block) return true;
                if (Tftp.PeekOp(res.Buffer) == Tftp.Op.Error) throw new IOException(ErrorText(res.Buffer));
            }
            catch (SocketException) { }
        }
        return false;
    }

    private static async Task<UdpReceiveResult> ReceiveAsync(UdpClient socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(6000);
        return await socket.ReceiveAsync(timeout.Token);
    }

    private static async Task<IPEndPoint> Resolve(string host, int port, CancellationToken ct)
    {
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed
            : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).First();
        return new IPEndPoint(ip, port);
    }

    private static string ErrorText(byte[] packet) =>
        packet.Length > 4 ? System.Text.Encoding.ASCII.GetString(packet, 4, packet.Length - 5) : "TFTP error";
}
