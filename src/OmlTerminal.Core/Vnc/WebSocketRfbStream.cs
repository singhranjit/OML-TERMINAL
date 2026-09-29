using System.Net.WebSockets;

namespace OmlTerminal.Core.Vnc;

/// <summary>
/// Adapts a ClientWebSocket to a plain Stream so VncClient's RFB parsing code works unchanged whether the VNC
/// server is reached directly over TCP or, as OML Labs (and noVNC/websockify generally) does it, tunneled as
/// binary WebSocket frames. A received WS message can be larger than one caller's Read request, so leftover
/// bytes are buffered between calls; a Close frame surfaces as a clean EOF (Read returns 0), matching how
/// VncClient already treats a closed TCP connection.
/// </summary>
public sealed class WebSocketRfbStream(ClientWebSocket socket) : Stream
{
    private byte[] _buffer = [];
    private int _bufferPos, _bufferLen;
    private bool _eof;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_bufferPos >= _bufferLen && !_eof)
            await FillAsync(cancellationToken).ConfigureAwait(false);
        if (_bufferPos >= _bufferLen) return 0; // EOF

        int n = Math.Min(count, _bufferLen - _bufferPos);
        Array.Copy(_buffer, _bufferPos, buffer, offset, n);
        _bufferPos += n;
        return n;
    }

    private async Task FillAsync(CancellationToken ct)
    {
        var chunk = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) { _eof = true; return; }
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = System.Text.Encoding.UTF8.GetString(chunk, 0, result.Count);
                throw new IOException($"Console proxy sent an unexpected message: {text}");
            }
            ms.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);

        _buffer = ms.ToArray();
        _bufferPos = 0;
        _bufferLen = _buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(buffer, offset, count), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) socket.Dispose();
        base.Dispose(disposing);
    }
}
