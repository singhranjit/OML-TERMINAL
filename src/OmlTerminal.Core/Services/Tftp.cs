using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OmlTerminal.Core.Services;

/// <summary>TFTP wire protocol (RFC 1350) with the common option extensions (RFC 2347-2349): blksize, tsize, timeout.</summary>
public static class Tftp
{
    public const int DefaultPort = 69;
    public const int DefaultBlockSize = 512;

    public enum Op : ushort { Rrq = 1, Wrq = 2, Data = 3, Ack = 4, Error = 5, OAck = 6 }

    public enum ErrorCode : ushort
    {
        NotDefined = 0, FileNotFound = 1, AccessViolation = 2, DiskFull = 3, IllegalOperation = 4,
        UnknownTid = 5, FileExists = 6, NoSuchUser = 7, OptionRejected = 8,
    }

    public static byte[] Request(Op op, string filename, string mode, IReadOnlyDictionary<string, string>? options = null)
    {
        var ms = new MemoryStream();
        WriteOp(ms, op);
        WriteZ(ms, filename);
        WriteZ(ms, mode);
        if (options is not null)
            foreach (var (k, v) in options) { WriteZ(ms, k); WriteZ(ms, v); }
        return ms.ToArray();
    }

    public static byte[] Data(ushort block, ReadOnlySpan<byte> data)
    {
        var buf = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)Op.Data);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), block);
        data.CopyTo(buf.AsSpan(4));
        return buf;
    }

    public static byte[] Ack(ushort block)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)Op.Ack);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), block);
        return buf;
    }

    public static byte[] Error(ErrorCode code, string message)
    {
        var ms = new MemoryStream();
        WriteOp(ms, Op.Error);
        var c = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(c, (ushort)code);
        ms.Write(c);
        WriteZ(ms, message);
        return ms.ToArray();
    }

    public static byte[] OAck(IReadOnlyDictionary<string, string> options)
    {
        var ms = new MemoryStream();
        WriteOp(ms, Op.OAck);
        foreach (var (k, v) in options) { WriteZ(ms, k); WriteZ(ms, v); }
        return ms.ToArray();
    }

    /// <summary>Parses the option key/value pairs from an OACK packet (key/value strings after the opcode).</summary>
    public static Dictionary<string, string> ParseOptions(ReadOnlySpan<byte> oack)
    {
        var parts = SplitZ(oack[2..]);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < parts.Count; i += 2) options[parts[i]] = parts[i + 1];
        return options;
    }

    public static Op PeekOp(ReadOnlySpan<byte> packet) => (Op)BinaryPrimitives.ReadUInt16BigEndian(packet);
    public static ushort Block(ReadOnlySpan<byte> packet) => BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);

    /// <summary>Parses an RRQ/WRQ into filename, mode and any options (option keys are lower-cased).</summary>
    public static (string Filename, string Mode, Dictionary<string, string> Options) ParseRequest(ReadOnlySpan<byte> packet)
    {
        var parts = SplitZ(packet[2..]);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i + 1 < parts.Count; i += 2) options[parts[i]] = parts[i + 1];
        return (parts.Count > 0 ? parts[0] : "", parts.Count > 1 ? parts[1] : "octet", options);
    }

    private static void WriteOp(Stream s, Op op)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)op);
        s.Write(b);
    }

    private static void WriteZ(Stream s, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        s.Write(bytes);
        s.WriteByte(0);
    }

    private static List<string> SplitZ(ReadOnlySpan<byte> data)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < data.Length; i++)
            if (data[i] == 0) { result.Add(Encoding.ASCII.GetString(data[start..i])); start = i + 1; }
        return result;
    }
}

/// <summary>
/// A TFTP server (RFC 1350) that serves and, optionally, receives files under a root folder - the tftpd64 use case of
/// pushing firmware and pulling configs to/from network gear. Each transfer runs on its own ephemeral UDP socket, as
/// the RFC requires. Path traversal outside the root is refused.
/// </summary>
public sealed class TftpServer : NetworkServiceBase
{
    private readonly int _port;
    private UdpClient? _listener;

    public string RootDirectory { get; }
    public string BindAddress { get; }
    public bool AllowWrite { get; }
    public bool AllowOverwrite { get; }

    public override string Name => "TFTP server";

    public TftpServer(string rootDirectory, string bindAddress = "0.0.0.0", int port = Tftp.DefaultPort, bool allowWrite = true, bool allowOverwrite = false)
    {
        RootDirectory = rootDirectory;
        BindAddress = bindAddress;
        _port = port;
        AllowWrite = allowWrite;
        AllowOverwrite = allowOverwrite;
    }

    protected override void OnStart()
    {
        Directory.CreateDirectory(RootDirectory);
        _listener = new UdpClient(new IPEndPoint(IPAddress.Parse(BindAddress), _port));
        Log(LogLevel.Info, "", $"Listening on {BindAddress}:{_port}, root {RootDirectory}{(AllowWrite ? "" : " (read-only)")}");
        _ = Task.Run(AcceptLoopAsync);
    }

    protected override void OnStop()
    {
        try { _listener?.Dispose(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        while (KeepRunning && listener is not null)
        {
            UdpReceiveResult req;
            try { req = await listener.ReceiveAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(req.Buffer, req.RemoteEndPoint));
        }
    }

    private async Task HandleAsync(byte[] packet, IPEndPoint client)
    {
        if (packet.Length < 4) return;
        var op = Tftp.PeekOp(packet);
        var (filename, _, options) = Tftp.ParseRequest(packet);
        var path = ResolvePath(filename);

        using var socket = new UdpClient(new IPEndPoint(IPAddress.Parse(BindAddress), 0));
        socket.Connect(client);
        socket.Client.ReceiveTimeout = 5000;

        try
        {
            if (op == Tftp.Op.Rrq)
            {
                if (path is null || !File.Exists(path)) { await socket.SendAsync(Tftp.Error(Tftp.ErrorCode.FileNotFound, "File not found")); Log(LogLevel.Warning, client, $"RRQ {filename}: not found"); return; }
                await SendFileAsync(socket, client, path, filename, options);
            }
            else if (op == Tftp.Op.Wrq)
            {
                if (!AllowWrite) { await socket.SendAsync(Tftp.Error(Tftp.ErrorCode.AccessViolation, "Uploads disabled")); Log(LogLevel.Warning, client, $"WRQ {filename}: uploads disabled"); return; }
                if (path is null) { await socket.SendAsync(Tftp.Error(Tftp.ErrorCode.AccessViolation, "Illegal path")); return; }
                if (File.Exists(path) && !AllowOverwrite) { await socket.SendAsync(Tftp.Error(Tftp.ErrorCode.FileExists, "File exists")); Log(LogLevel.Warning, client, $"WRQ {filename}: already exists"); return; }
                await ReceiveFileAsync(socket, client, path, filename, options);
            }
        }
        catch (Exception ex) { Log(LogLevel.Error, client, $"{filename}: {ex.Message}"); }
    }

    private async Task SendFileAsync(UdpClient socket, IPEndPoint client, string path, string filename, Dictionary<string, string> options)
    {
        await using var file = File.OpenRead(path);
        int blockSize = Tftp.DefaultBlockSize;
        var accepted = NegotiateOptions(options, file.Length, ref blockSize);
        if (accepted.Count > 0 && !await ExpectAckAsync(socket, Tftp.OAck(accepted), 0)) return;

        Log(LogLevel.Send, client, $"RRQ {filename} ({file.Length:N0} bytes, blksize {blockSize})");
        ushort block = accepted.Count > 0 ? (ushort)1 : (ushort)1;
        var buffer = new byte[blockSize];
        int read;
        do
        {
            read = await file.ReadAsync(buffer.AsMemory(0, blockSize));
            if (!await ExpectAckAsync(socket, Tftp.Data(block, buffer.AsSpan(0, read)), block)) { Log(LogLevel.Error, client, $"{filename}: no ACK for block {block}"); return; }
            block++;
        } while (read == blockSize && KeepRunning);
        Log(LogLevel.Success, client, $"Sent {filename}");
    }

    private async Task ReceiveFileAsync(UdpClient socket, IPEndPoint client, string path, string filename, Dictionary<string, string> options)
    {
        int blockSize = Tftp.DefaultBlockSize;
        var accepted = NegotiateOptions(options, null, ref blockSize);
        await socket.SendAsync(accepted.Count > 0 ? Tftp.OAck(accepted) : Tftp.Ack(0));

        Log(LogLevel.Receive, client, $"WRQ {filename} (blksize {blockSize})");
        await using var file = File.Create(path);
        ushort expected = 1;
        long total = 0;
        while (KeepRunning)
        {
            UdpReceiveResult res;
            try { res = await socket.ReceiveAsync(); } catch { Log(LogLevel.Error, client, $"{filename}: receive timed out"); return; }
            if (Tftp.PeekOp(res.Buffer) != Tftp.Op.Data) return;
            var block = Tftp.Block(res.Buffer);
            if (block == expected)
            {
                var data = res.Buffer.AsMemory(4);
                await file.WriteAsync(data);
                total += data.Length;
                await socket.SendAsync(Tftp.Ack(block));
                expected++;
                if (data.Length < blockSize) { Log(LogLevel.Success, client, $"Received {filename} ({total:N0} bytes)"); return; }
            }
            else await socket.SendAsync(Tftp.Ack((ushort)(expected - 1))); // duplicate: re-ack
        }
    }

    private static Dictionary<string, string> NegotiateOptions(Dictionary<string, string> requested, long? fileLength, ref int blockSize)
    {
        var accepted = new Dictionary<string, string>();
        if (requested.TryGetValue("blksize", out var bs) && int.TryParse(bs, out var v) && v is >= 8 and <= 65464)
        {
            blockSize = v;
            accepted["blksize"] = v.ToString();
        }
        if (requested.ContainsKey("tsize") && fileLength is { } len) accepted["tsize"] = len.ToString();
        if (requested.TryGetValue("timeout", out var t) && int.TryParse(t, out var ts) && ts is >= 1 and <= 255) accepted["timeout"] = ts.ToString();
        return accepted;
    }

    /// <summary>Sends a packet and waits for the ACK of <paramref name="expectedBlock"/>, retrying a few times.</summary>
    private static async Task<bool> ExpectAckAsync(UdpClient socket, byte[] packet, ushort expectedBlock)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await socket.SendAsync(packet);
            try
            {
                var res = await socket.ReceiveAsync();
                if (Tftp.PeekOp(res.Buffer) == Tftp.Op.Ack && Tftp.Block(res.Buffer) == expectedBlock) return true;
                if (Tftp.PeekOp(res.Buffer) == Tftp.Op.Error) return false;
            }
            catch (SocketException) { /* timeout: retransmit */ }
        }
        return false;
    }

    /// <summary>Resolves a requested filename inside the root, refusing anything that escapes it (path traversal).</summary>
    private string? ResolvePath(string filename)
    {
        var clean = filename.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(RootDirectory, clean));
        var root = Path.GetFullPath(RootDirectory);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full == root ? full : null;
    }
}
