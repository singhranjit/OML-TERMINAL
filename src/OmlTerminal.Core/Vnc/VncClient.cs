using System.Net.Sockets;
using System.Net.WebSockets;

namespace OmlTerminal.Core.Vnc;

/// <summary>
/// A from-scratch RFB 3.8 (RFC 6143) client: version/security handshake (None or VNC DES auth), forces
/// 32-bit BGRA truecolor via SetPixelFormat, and decodes Raw-encoded framebuffer updates (a first pass -
/// enough to see and control a real VNC desktop; further encodings can be added without changing this contract).
/// Works equally over a direct TCP connection or a WebSocket tunnel (e.g. a noVNC/websockify-style bridge,
/// which is how OML Labs exposes VNC consoles) - both feed the same handshake/parsing code via a plain Stream.
/// </summary>
public sealed class VncClient : IDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private readonly object _writeLock = new();
    private int _closing, _raised;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Raised (background thread) with a rectangle and its tightly-packed BGRA32 pixel data, row-major.</summary>
    public event Action<VncRectangle, byte[]>? RectangleUpdated;
    public event Action<string>? ClipboardReceived;
    public event Action? BellRang;
    public event Action<string?>? Closed;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        _tcp = tcp;
        await RunHandshakeAsync(tcp.GetStream(), password, ct).ConfigureAwait(false);
    }

    /// <summary>Connects through a WebSocket-tunneled RFB bridge instead of raw TCP. acceptCertificate lets the
    /// caller pin a specific self-signed certificate (matching OmlHostCertStore's trust-on-first-use model)
    /// instead of turning off validation altogether.</summary>
    public async Task ConnectViaWebSocketAsync(Uri wsUri, string password,
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool>? acceptCertificate = null,
        CancellationToken ct = default)
    {
        var socket = new ClientWebSocket();
        if (acceptCertificate is not null)
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || acceptCertificate(cert as System.Security.Cryptography.X509Certificates.X509Certificate2);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(wsUri, timeout.Token).ConfigureAwait(false);
        }
        await RunHandshakeAsync(new WebSocketRfbStream(socket), password, ct).ConfigureAwait(false);
    }

    private async Task RunHandshakeAsync(Stream stream, string password, CancellationToken ct)
    {
        _stream = stream;
        var serverVersion = await ReadExactAsync(12, ct).ConfigureAwait(false);
        if (!VncWire.TryParseVersion(serverVersion, out _, out _))
            throw new InvalidOperationException("Not a VNC server (bad RFB version line).");
        await WriteAsync(VncWire.VersionLine(3, 8), ct).ConfigureAwait(false);

        byte count = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
        if (count == 0)
        {
            var reason = await ReadFailureReasonAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Server refused connection: {reason}");
        }
        var types = await ReadExactAsync(count, ct).ConfigureAwait(false);
        VncSecurityType chosen = types.Contains((byte)VncSecurityType.VncAuth) ? VncSecurityType.VncAuth
            : types.Contains((byte)VncSecurityType.None) ? VncSecurityType.None
            : throw new InvalidOperationException("Server offers no supported authentication type.");
        await WriteAsync([(byte)chosen], ct).ConfigureAwait(false);

        if (chosen == VncSecurityType.VncAuth)
        {
            var challenge = await ReadExactAsync(16, ct).ConfigureAwait(false);
            await WriteAsync(VncAuth.EncryptChallenge(password, challenge), ct).ConfigureAwait(false);
        }

        var result = await ReadExactAsync(4, ct).ConfigureAwait(false);
        if (VncWire.ReadU32(result, 0) != 0)
        {
            var reason = await ReadFailureReasonAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Authentication failed: {reason}");
        }

        await WriteAsync(VncWire.ClientInit(shared: true), ct).ConfigureAwait(false);
        var serverInit = await ReadExactAsync(24, ct).ConfigureAwait(false);
        Width = VncWire.ReadU16(serverInit, 0);
        Height = VncWire.ReadU16(serverInit, 2);
        uint nameLen = VncWire.ReadU32(serverInit, 20);
        Name = nameLen > 0 ? System.Text.Encoding.UTF8.GetString(await ReadExactAsync((int)nameLen, ct).ConfigureAwait(false)) : "";

        await WriteAsync(VncWire.SetPixelFormat(VncPixelFormat.Bgra32), ct).ConfigureAwait(false);
        await WriteAsync(VncWire.SetEncodings(VncEncoding.Raw), ct).ConfigureAwait(false);

        // Fully async from here on: a blocking synchronous Read() on a Task.Run thread would tie up a threadpool
        // thread for the connection's whole lifetime (worse for the WebSocket-backed stream, whose Read is itself
        // sync-over-async) - with several sessions open at once that can starve unrelated pending work elsewhere
        // in the app, including other connections' own timeouts.
        _ = ReadLoopAsync();
    }

    public void RequestUpdate(bool incremental = true) =>
        Write(VncWire.FramebufferUpdateRequest(incremental, 0, 0, Width, Height));

    public void SendKey(uint keysym, bool down) => Write(VncWire.KeyEvent(keysym, down));
    public void SendPointer(byte buttonMask, int x, int y) => Write(VncWire.PointerEvent(buttonMask, x, y));
    public void SendClipboard(string text) => Write(VncWire.ClientCutText(text));

    private async Task ReadLoopAsync()
    {
        string? error = null;
        try
        {
            while (true)
            {
                byte type = (await ReadExactAsync(1, CancellationToken.None).ConfigureAwait(false))[0];
                switch (type)
                {
                    case 0: await HandleFramebufferUpdateAsync().ConfigureAwait(false); break;
                    case 1: await HandleSetColourMapEntriesAsync().ConfigureAwait(false); break;
                    case 2: BellRang?.Invoke(); break;
                    case 3: await HandleServerCutTextAsync().ConfigureAwait(false); break;
                    default: throw new InvalidOperationException($"Unknown server message type {type}.");
                }
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _closing) == 0) { error = ex.Message; }
        catch { }
        RaiseClosed(error);
    }

    private async Task HandleFramebufferUpdateAsync()
    {
        await ReadExactAsync(1, CancellationToken.None).ConfigureAwait(false); // padding
        int count = VncWire.ReadU16(await ReadExactAsync(2, CancellationToken.None).ConfigureAwait(false), 0);
        for (int i = 0; i < count; i++)
        {
            var header = await ReadExactAsync(12, CancellationToken.None).ConfigureAwait(false);
            int x = VncWire.ReadU16(header, 0), y = VncWire.ReadU16(header, 2);
            int w = VncWire.ReadU16(header, 4), h = VncWire.ReadU16(header, 6);
            int encoding = VncWire.ReadS32(header, 8);
            if (encoding != (int)VncEncoding.Raw)
                throw new InvalidOperationException($"Unsupported VNC encoding {encoding} (only Raw is implemented).");
            var pixels = await ReadExactAsync(w * h * 4, CancellationToken.None).ConfigureAwait(false);
            RectangleUpdated?.Invoke(new VncRectangle(x, y, w, h), pixels);
        }
    }

    private async Task HandleSetColourMapEntriesAsync()
    {
        await ReadExactAsync(1, CancellationToken.None).ConfigureAwait(false); // padding
        var header = await ReadExactAsync(4, CancellationToken.None).ConfigureAwait(false);
        int numColors = VncWire.ReadU16(header, 2);
        await ReadExactAsync(numColors * 6, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleServerCutTextAsync()
    {
        var header = await ReadExactAsync(7, CancellationToken.None).ConfigureAwait(false); // 3 padding + 4 length
        uint len = VncWire.ReadU32(header, 3);
        var text = await ReadExactAsync((int)len, CancellationToken.None).ConfigureAwait(false);
        ClipboardReceived?.Invoke(System.Text.Encoding.Latin1.GetString(text));
    }

    private async Task<string> ReadFailureReasonAsync(CancellationToken ct)
    {
        var lenBytes = await ReadExactAsync(4, ct).ConfigureAwait(false);
        uint len = VncWire.ReadU32(lenBytes, 0);
        if (len == 0 || len > 8192) return "(no reason given)";
        return System.Text.Encoding.UTF8.GetString(await ReadExactAsync((int)len, ct).ConfigureAwait(false));
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream!.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("Connection closed by VNC server.");
            read += n;
        }
        return buf;
    }

    private void Write(byte[] data)
    {
        try { lock (_writeLock) _stream?.Write(data, 0, data.Length); } catch { }
    }

    private Task WriteAsync(byte[] data, CancellationToken ct)
    {
        lock (_writeLock) return _stream!.WriteAsync(data, ct).AsTask();
    }

    public void Close()
    {
        Interlocked.Exchange(ref _closing, 1);
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        RaiseClosed(null);
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 0) Closed?.Invoke(error);
    }

    public void Dispose() => Close();
}
