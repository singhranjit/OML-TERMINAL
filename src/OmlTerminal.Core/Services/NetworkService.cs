using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OmlTerminal.Core.Services;

public enum LogLevel { Info, Send, Receive, Success, Warning, Error }

public sealed record ServiceLogEntry(DateTime Time, LogLevel Level, string Peer, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss}  {Level,-7}  {Peer,-21}  {Message}";
}

/// <summary>A start/stoppable local server (TFTP, FTP, Syslog...). All emit their activity through <see cref="Log"/>.</summary>
public interface INetworkService : IDisposable
{
    string Name { get; }
    bool IsRunning { get; }
    event Action<ServiceLogEntry>? Logged;
    event Action? StateChanged;
    void Start();
    void Stop();
}

public abstract class NetworkServiceBase : INetworkService
{
    private int _running;

    public abstract string Name { get; }
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public event Action<ServiceLogEntry>? Logged;
    public event Action? StateChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try { OnStart(); }
        catch { Interlocked.Exchange(ref _running, 0); throw; }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        try { OnStop(); } catch { }
        StateChanged?.Invoke();
    }

    protected abstract void OnStart();
    protected abstract void OnStop();

    protected void Log(LogLevel level, string peer, string message) =>
        Logged?.Invoke(new ServiceLogEntry(DateTime.Now, level, peer, message));

    protected void Log(LogLevel level, IPEndPoint? peer, string message) =>
        Log(level, peer?.ToString() ?? "", message);

    /// <summary>True while the server is meant to be running - the read loops check this to exit cleanly on Stop().</summary>
    protected bool KeepRunning => IsRunning;

    public virtual void Dispose() => Stop();
}

public static class LocalAddresses
{
    /// <summary>Usable IPv4 addresses on up interfaces, most-likely LAN address first, plus 0.0.0.0 (all interfaces).</summary>
    public static IReadOnlyList<string> Ipv4() =>
        new[] { "0.0.0.0" }.Concat(
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .OrderByDescending(a => a.StartsWith("192.168.") || a.StartsWith("10.") || a.StartsWith("172.")))
            .Append("127.0.0.1")
            .Distinct()
            .ToList();

    /// <summary>The best guess for "our LAN IP" - what to show a device to point at.</summary>
    public static string PrimaryIpv4() => Ipv4().FirstOrDefault(a => a is not ("0.0.0.0" or "127.0.0.1")) ?? "127.0.0.1";
}
