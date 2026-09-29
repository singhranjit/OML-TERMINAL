using System.IO.Ports;

namespace OmlTerminal.Core.Transports;

/// <summary>Console/AUX port access over a local COM port (e.g. a USB console cable to a switch).</summary>
public sealed class SerialTransport(string portName, int baudRate) : ITerminalTransport
{
    private SerialPort? _port;
    private int _raised, _closing;

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public static string[] AvailablePorts() => SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    public Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException($"Could not open {portName}. It may be in use by another program.", ex);
        }
        _port = port;
        _ = Task.Run(ReadLoop);
        return Task.CompletedTask;
    }

    private void ReadLoop()
    {
        var buf = new byte[4096];
        string? error = null;
        try
        {
            while (_port is { IsOpen: true } p)
            {
                int n = p.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _closing) == 0) { error = ex.Message; }
        catch { }
        RaiseClosed(error);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_port is not { IsOpen: true } p || data.IsEmpty) return;
        var copy = data.ToArray();
        try { p.Write(copy, 0, copy.Length); } catch { }
    }

    /// <summary>A serial line has no negotiated size; nothing to do here.</summary>
    public void Resize(int cols, int rows) { }

    public void Close()
    {
        Interlocked.Exchange(ref _closing, 1);
        try { _port?.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;
        RaiseClosed(null);
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 0) Closed?.Invoke(error);
    }

    public void Dispose() => Close();
}
