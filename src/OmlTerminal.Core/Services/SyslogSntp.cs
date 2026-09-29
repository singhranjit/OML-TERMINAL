using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Services;

/// <summary>
/// A syslog server (UDP 514, RFC 3164/5424): receives log messages from network devices and shows them with parsed
/// facility/severity, optionally saving to a file. The staple companion to config changes - point a switch's
/// "logging host" at this PC while you work.
/// </summary>
public sealed partial class SyslogServer : NetworkServiceBase
{
    private readonly int _port;
    private UdpClient? _socket;
    private StreamWriter? _file;

    public string BindAddress { get; }
    public string? LogFilePath { get; }
    public override string Name => "Syslog server";

    public SyslogServer(string bindAddress = "0.0.0.0", int port = 514, string? logFilePath = null)
    {
        BindAddress = bindAddress;
        _port = port;
        LogFilePath = logFilePath;
    }

    protected override void OnStart()
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Parse(BindAddress), _port));
        if (LogFilePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogFilePath))!);
            _file = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        }
        Log(LogLevel.Info, "", $"Listening on {BindAddress}:{_port}{(LogFilePath is null ? "" : $", logging to {LogFilePath}")}");
        _ = Task.Run(ReceiveLoopAsync);
    }

    protected override void OnStop()
    {
        try { _socket?.Dispose(); } catch { }
        try { _file?.Dispose(); } catch { }
        _socket = null;
        _file = null;
    }

    private async Task ReceiveLoopAsync()
    {
        var socket = _socket;
        while (KeepRunning && socket is not null)
        {
            UdpReceiveResult res;
            try { res = await socket.ReceiveAsync(); }
            catch { break; }
            var (severity, text) = Parse(Encoding.UTF8.GetString(res.Buffer));
            Log(SeverityLevel(severity), res.RemoteEndPoint, text);
            _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{res.RemoteEndPoint.Address}\t{text}");
        }
    }

    [GeneratedRegex(@"^<(?<pri>\d{1,3})>(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex PriRegex();

    /// <summary>Extracts the syslog severity (0-7) from the &lt;PRI&gt; header and returns the human-readable body.</summary>
    public static (int Severity, string Text) Parse(string raw)
    {
        var m = PriRegex().Match(raw.Trim());
        if (!m.Success) return (6, raw.Trim());
        int pri = int.Parse(m.Groups["pri"].Value);
        return (pri % 8, m.Groups["rest"].Value.Trim());
    }

    private static LogLevel SeverityLevel(int severity) => severity switch
    {
        <= 3 => LogLevel.Error,     // emergency..error
        4 => LogLevel.Warning,      // warning
        <= 5 => LogLevel.Info,      // notice
        _ => LogLevel.Receive,      // info/debug
    };
}

/// <summary>
/// An SNTP/NTP time server (UDP 123): answers time queries with this PC's clock. Useful in an isolated lab so devices
/// can sync time without internet. Serves as a stratum-2 server referencing the local clock.
/// </summary>
public sealed class SntpServer : NetworkServiceBase
{
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly int _port;
    private UdpClient? _socket;

    public string BindAddress { get; }
    public override string Name => "SNTP server";

    public SntpServer(string bindAddress = "0.0.0.0", int port = 123)
    {
        BindAddress = bindAddress;
        _port = port;
    }

    protected override void OnStart()
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Parse(BindAddress), _port));
        Log(LogLevel.Info, "", $"Serving time on {BindAddress}:{_port} (stratum 2, local clock)");
        _ = Task.Run(ReceiveLoopAsync);
    }

    protected override void OnStop()
    {
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    private async Task ReceiveLoopAsync()
    {
        var socket = _socket;
        while (KeepRunning && socket is not null)
        {
            UdpReceiveResult res;
            try { res = await socket.ReceiveAsync(); }
            catch { break; }
            if (res.Buffer.Length < 48) continue;
            var reply = BuildReply(res.Buffer, DateTime.UtcNow);
            await socket.SendAsync(reply, res.RemoteEndPoint);
            Log(LogLevel.Send, res.RemoteEndPoint, $"Time request answered ({DateTime.Now:HH:mm:ss})");
        }
    }

    /// <summary>Builds a 48-byte NTP reply: server mode, stratum 2, echoing the client's transmit time as originate.</summary>
    public static byte[] BuildReply(byte[] request, DateTime utcNow)
    {
        var r = new byte[48];
        r[0] = 0b00_100_100;                 // LI=0, VN=4, Mode=4 (server)
        r[1] = 2;                            // stratum 2
        r[2] = request.Length > 2 ? request[2] : (byte)4; // poll
        r[3] = 0xEC;                         // precision ~ -20
        WriteTimestamp(r, 16, utcNow);       // reference
        Array.Copy(request, 40, r, 24, 8);   // originate = client's transmit timestamp
        WriteTimestamp(r, 32, utcNow);       // receive
        WriteTimestamp(r, 40, utcNow);       // transmit
        return r;
    }

    private static void WriteTimestamp(byte[] buf, int offset, DateTime utc)
    {
        var span = utc - NtpEpoch;
        uint seconds = (uint)span.TotalSeconds;
        uint fraction = (uint)((span.TotalSeconds - seconds) * 4294967296.0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset), seconds);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 4), fraction);
    }
}
