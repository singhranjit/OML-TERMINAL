namespace OmlTerminal.Core.Transports;

public interface ITerminalTransport : IDisposable
{
    Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default);
    void Write(ReadOnlySpan<byte> data);
    void Resize(int cols, int rows);
    void Close();

    /// <summary>Raised on a background thread with raw bytes from the remote end.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Raised once when the connection ends; the argument is an error message, or null for a clean close.</summary>
    event Action<string?>? Closed;
}
