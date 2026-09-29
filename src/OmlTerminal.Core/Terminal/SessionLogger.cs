namespace OmlTerminal.Core.Terminal;

/// <summary>Appends a session's raw output stream to a plain-text transcript file, stripping ANSI escape
/// sequences so the log reads like a paper printout instead of raw control codes. Taps only the output
/// stream (matching PuTTY's default "log session output" behavior), not typed input - most SSH/Telnet
/// targets echo back what's typed as part of that same output stream, and logging input too would both
/// duplicate it and risk capturing a password typed at a non-echoing prompt in plain text.</summary>
public sealed class SessionLogger : IDisposable
{
    private readonly TerminalSession _session;
    private readonly FileStream _file;
    private readonly object _lock = new();
    private bool _disposed;

    public string Path { get; }

    private SessionLogger(TerminalSession session, string path, FileStream file)
    {
        _session = session;
        Path = path;
        _file = file;
        // Subscribes via the session's own stable event, not session.Transport.DataReceived directly - a
        // reconnect swaps in a brand new Transport instance, and a direct subscription would silently stop
        // receiving anything the moment that happened.
        _session.OutputReceived += OnData;
    }

    /// <summary>Creates a fresh timestamped log file under <paramref name="directory"/> and starts capturing.</summary>
    public static SessionLogger Start(TerminalSession session, string directory, string sessionName)
    {
        Directory.CreateDirectory(directory);
        var safeName = new string(sessionName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var path = System.IO.Path.Combine(directory, $"{safeName}_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new SessionLogger(session, path, file);
    }

    private void OnData(byte[] data)
    {
        var clean = AnsiStripper.Strip(data);
        if (clean.Length == 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            try { _file.Write(clean, 0, clean.Length); _file.Flush(); } catch { }
        }
    }

    public void Dispose()
    {
        _session.OutputReceived -= OnData;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _file.Dispose(); } catch { }
        }
    }
}
