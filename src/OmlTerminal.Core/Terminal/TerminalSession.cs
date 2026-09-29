using OmlTerminal.Core.Transports;

namespace OmlTerminal.Core.Terminal;

/// <summary>Owns one transport + one engine and wires them together. All lifecycle logic lives here, not in the UI.</summary>
public sealed class TerminalSession : IDisposable
{
    private int _disposed;
    private volatile bool _localClose;
    private CancellationTokenSource? _reconnectCts;

    public ITerminalTransport Transport { get; private set; }
    public ITerminalEngine Engine { get; }
    public bool IsConnected { get; private set; }

    /// <summary>When set, an unexpected drop (not a user-initiated Close()) is retried with backoff instead of
    /// just showing "[Disconnected]" - called to produce a fresh transport for each attempt, since the one
    /// that just failed can't be reused. Left null (the default), behavior is unchanged from before reconnect
    /// support existed.</summary>
    public Func<ITerminalTransport>? ReconnectTransportFactory { get; set; }

    /// <summary>The backoff schedule reconnect attempts wait through. Exposed settable purely so tests can
    /// swap in near-zero delays instead of waiting through the real multi-second/up-to-30s schedule.</summary>
    public TimeSpan[] ReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    /// <summary>Raised (any thread) after the screen changed and needs repainting.</summary>
    public event Action? Changed;

    /// <summary>Raised (any thread) once when the connection ends for good - either a clean close, or every
    /// reconnect attempt was exhausted. Not raised while reconnect attempts are still in progress.</summary>
    public event Action<string?>? Ended;

    /// <summary>Raised (any thread) when a reconnect attempt succeeds.</summary>
    public event Action? Reconnected;

    /// <summary>Raised (any thread) with every raw byte chunk received from the remote - stable across a
    /// reconnect's transport swap, unlike subscribing to Transport.DataReceived directly.</summary>
    public event Action<byte[]>? OutputReceived;

    public TerminalSession(ITerminalTransport transport, ITerminalEngine engine)
    {
        Transport = transport;
        Engine = engine;
        WireTransport(transport);
        engine.Response += bytes => Transport.Write(bytes);
        engine.Changed += () => Changed?.Invoke();
    }

    private void WireTransport(ITerminalTransport transport)
    {
        transport.DataReceived += data => { Engine.Write(data); OutputReceived?.Invoke(data); };
        transport.Closed += OnTransportClosed;
    }

    public async Task ConnectAsync(int cols, int rows, CancellationToken ct = default)
    {
        Engine.Resize(cols, rows);
        await Transport.ConnectAsync(cols, rows, ct).ConfigureAwait(false);
        IsConnected = true;
    }

    /// <summary>Raised with a copy of everything sent to the remote by the user, a macro or multi-exec (not terminal auto-replies).</summary>
    public event Action<byte[]>? InputSent;

    public void Send(ReadOnlySpan<byte> data)
    {
        if (!IsConnected) return;
        Transport.Write(data);
        Engine.ScrollToBottom(); // typing/pasting/macro output while scrolled back should jump back to the live edge, like any real terminal
        InputSent?.Invoke(data.ToArray());
    }

    public void Resize(int cols, int rows)
    {
        if (cols < 2 || rows < 1) return;
        if (cols == Engine.Cols && rows == Engine.Rows) return;
        Engine.Resize(cols, rows);
        if (IsConnected) Transport.Resize(cols, rows);
    }

    private void OnTransportClosed(string? error)
    {
        if (_localClose) return; // user-initiated close: the caller already knows, no 'connection closed' banner
        IsConnected = false;
        if (Volatile.Read(ref _disposed) != 0) return;

        if (error is not null && ReconnectTransportFactory is not null)
        {
            _ = ReconnectLoopAsync(error);
            return;
        }
        Engine.WriteLocal(error is null
            ? "\r\n\x1b[33m[Connection closed]\x1b[0m\r\n"
            : $"\r\n\x1b[31m[Disconnected: {error}]\x1b[0m\r\n");
        Ended?.Invoke(error);
    }

    private async Task ReconnectLoopAsync(string firstError)
    {
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        Engine.WriteLocal($"\r\n\x1b[31m[Disconnected: {firstError}]\x1b[0m\r\n");

        for (int attempt = 0; attempt < ReconnectDelays.Length; attempt++)
        {
            var delay = ReconnectDelays[attempt];
            Engine.WriteLocal($"\x1b[33m[Reconnecting in {(int)delay.TotalSeconds}s (attempt {attempt + 1}/{ReconnectDelays.Length})...]\x1b[0m\r\n");
            try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; } // Close()/Dispose() during the wait

            if (_localClose || Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var fresh = ReconnectTransportFactory!();
                WireTransport(fresh);
                Transport = fresh;
                await fresh.ConnectAsync(Engine.Cols, Engine.Rows, cts.Token).ConfigureAwait(false);
                IsConnected = true;
                Engine.WriteLocal("\x1b[32m[Reconnected]\x1b[0m\r\n");
                Reconnected?.Invoke();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Unsubscribe before disposing: every transport's Close()/Dispose() ends by raising Closed(null)
                // unconditionally (even for a failed attempt that never connected), and that's still wired to
                // OnTransportClosed here - without this, disposing the failed attempt re-enters OnTransportClosed
                // synchronously and fires a spurious premature Ended/"[Connection closed]" mid-retry.
                Transport.Closed -= OnTransportClosed;
                try { Transport.Dispose(); } catch { } // the failed attempt's transport - never connected, nothing else will clean it up
                Engine.WriteLocal($"\x1b[31m[Reconnect attempt {attempt + 1} failed: {ex.Message}]\x1b[0m\r\n");
            }
        }
        Engine.WriteLocal("\x1b[31m[Giving up after repeated reconnect failures - double-click the session to try again]\x1b[0m\r\n");
        Ended?.Invoke(firstError);
    }

    public void Close()
    {
        _localClose = true;
        _reconnectCts?.Cancel();
        IsConnected = false;
        Transport.Close();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Close();
        Transport.Dispose();
        Engine.Dispose();
    }
}
