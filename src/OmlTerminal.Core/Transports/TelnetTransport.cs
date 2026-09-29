using System.Net.Sockets;

namespace OmlTerminal.Core.Transports;

public sealed class TelnetTransport(string host, int port, JumpHostGateway? gateway = null) : ITerminalTransport
{
    private readonly TelnetIacParser _parser = new();
    private readonly TelnetNegotiator _neg = new();
    private readonly object _writeLock = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _raised, _closing;

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public async Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        _neg.SetWindowSize(cols, rows);
        string effectiveHost = host;
        int effectivePort = port;
        if (gateway is not null)
        {
            await gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
            effectiveHost = gateway.BoundHost;
            effectivePort = gateway.BoundPort;
        }
        _tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _tcp.ConnectAsync(effectiveHost, effectivePort, timeout.Token).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _ = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        string? error = null;
        try
        {
            int n;
            while (_stream is { } s && (n = s.Read(buf, 0, buf.Length)) > 0)
            {
                var r = _parser.Parse(buf.AsSpan(0, n));
                foreach (var e in r.Events)
                {
                    var reply = _neg.Handle(e);
                    if (reply.Length > 0) WriteRaw(reply);
                }
                if (r.Data.Count > 0) DataReceived?.Invoke(r.Data.ToArray());
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _closing) == 0) { error = ex.Message; }
        catch { }
        RaiseClosed(error);
    }

    private void WriteRaw(byte[] bytes)
    {
        try { lock (_writeLock) { _stream?.Write(bytes, 0, bytes.Length); } } catch { }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        WriteRaw(TelnetNegotiator.EscapeOutgoing(data));
    }

    public void Resize(int cols, int rows)
    {
        var naws = _neg.SetWindowSize(cols, rows);
        if (naws.Length > 0) WriteRaw(naws);
    }

    public void Close()
    {
        Interlocked.Exchange(ref _closing, 1);
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        gateway?.Dispose();
        RaiseClosed(null);
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 0) Closed?.Invoke(error);
    }

    public void Dispose() => Close();
}
