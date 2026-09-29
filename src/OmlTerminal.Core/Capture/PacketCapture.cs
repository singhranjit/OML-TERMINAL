using System.Diagnostics;
using System.Text;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Core.Capture;

public enum CapturePlatform
{
    /// <summary>tcpdump on a Linux/BSD box (or anything with tcpdump) over SSH.</summary>
    RemoteTcpdump,
    /// <summary>FortiGate "diagnose sniffer packet" over SSH.</summary>
    FortiGateSniffer,
    /// <summary>This PC, through Wireshark's tshark.</summary>
    LocalTshark,
}

public sealed class CaptureFilter
{
    public string Host { get; init; } = "";
    public string Port { get; init; } = "";
    /// <summary>"", "tcp", "udp", "icmp", "arp", "icmp6"</summary>
    public string Protocol { get; init; } = "";
    /// <summary>Raw BPF, ANDed with the fields above.</summary>
    public string Custom { get; init; } = "";

    /// <summary>tcpdump / BPF syntax, which FortiOS' sniffer and tshark's capture filter both accept.</summary>
    public string ToBpf()
    {
        var parts = new List<string>();
        if (Protocol.Length > 0) parts.Add(Protocol);
        foreach (var h in Split(Host)) parts.Add(h.Contains('/') ? $"net {h}" : $"host {h}");
        var ports = Split(Port).ToList();
        if (ports.Count == 1) parts.Add(ports[0].Contains('-') ? $"portrange {ports[0]}" : $"port {ports[0]}");
        else if (ports.Count > 1) parts.Add("(" + string.Join(" or ", ports.Select(p => p.Contains('-') ? $"portrange {p}" : $"port {p}")) + ")");
        if (Custom.Trim().Length > 0) parts.Add(parts.Count > 0 ? $"({Custom.Trim()})" : Custom.Trim());
        return string.Join(" and ", parts);
    }

    private static IEnumerable<string> Split(string s) => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class CaptureRequest
{
    public CapturePlatform Platform { get; init; }
    public string Interface { get; init; } = "any";
    public CaptureFilter Filter { get; init; } = new();
    /// <summary>0 = until stopped.</summary>
    public int Count { get; init; }
    /// <summary>tcpdump: stream raw pcap into this local file instead of decoding text on the device.</summary>
    public string? PcapFile { get; init; }
    public bool UseSudo { get; init; } = true;
    /// <summary>FortiGate verbosity 1-6 (4 = headers + interface name, 6 = full packet hex).</summary>
    public int FortiVerbosity { get; init; } = 4;
    public int Snaplen { get; init; }
}

public static class CaptureCommands
{
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string Build(CaptureRequest r)
    {
        var bpf = r.Filter.ToBpf();
        switch (r.Platform)
        {
            case CapturePlatform.FortiGateSniffer:
            {
                var iface = string.IsNullOrWhiteSpace(r.Interface) ? "any" : r.Interface.Trim();
                var filter = bpf.Length == 0 ? "none" : $"'{bpf}'";
                return $"diagnose sniffer packet {iface} {filter} {Math.Clamp(r.FortiVerbosity, 1, 6)} {Math.Max(0, r.Count)} l";
            }
            case CapturePlatform.RemoteTcpdump:
            {
                var sb = new StringBuilder(r.UseSudo ? "sudo -S -p '' tcpdump" : "tcpdump");
                sb.Append($" -i {(string.IsNullOrWhiteSpace(r.Interface) ? "any" : r.Interface.Trim())} -nn");
                if (r.Count > 0) sb.Append($" -c {r.Count}");
                if (r.Snaplen > 0) sb.Append($" -s {r.Snaplen}");
                sb.Append(r.PcapFile is null ? " -l" : " -U -w -");
                if (bpf.Length > 0) sb.Append(' ').Append(ShellQuote(bpf));
                return sb.ToString();
            }
            default:
                return $"tshark {string.Join(' ', TsharkArgs(r).Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
        }
    }

    public static IReadOnlyList<string> TsharkArgs(CaptureRequest r)
    {
        var args = new List<string> { "-i", string.IsNullOrWhiteSpace(r.Interface) ? "1" : r.Interface.Trim(), "-l", "-n" };
        if (r.Count > 0) { args.Add("-c"); args.Add(r.Count.ToString()); }
        var bpf = r.Filter.ToBpf();
        if (bpf.Length > 0) { args.Add("-f"); args.Add(bpf); }
        if (r.PcapFile is not null) { args.Add("-P"); args.Add("-w"); args.Add(r.PcapFile); }
        return args;
    }

    public static string? FindTshark()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[] { Path.Combine(pf, @"Wireshark\tshark.exe"), @"C:\Program Files (x86)\Wireshark\tshark.exe" };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindWireshark()
    {
        var tshark = FindTshark();
        var ws = tshark is null ? null : Path.Combine(Path.GetDirectoryName(tshark)!, "Wireshark.exe");
        return ws is not null && File.Exists(ws) ? ws : null;
    }

    /// <summary>"1. \Device\NPF_{...} (Ethernet)" lines from tshark -D.</summary>
    public static async Task<IReadOnlyList<string>> ListLocalInterfacesAsync()
    {
        var tshark = FindTshark();
        if (tshark is null) return [];
        var psi = new ProcessStartInfo(tshark, "-D") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        if (p is null) return [];
        var text = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>Runs one capture and streams its output: text lines to <see cref="LineReceived"/>, or raw pcap bytes
/// into the requested file (reporting progress through <see cref="BytesWritten"/>).</summary>
public sealed class PacketCaptureRunner
{
    public event Action<string>? LineReceived;
    public event Action<long>? BytesWritten;

    public async Task RunAsync(CaptureRequest request, SessionProfile? device, CancellationToken ct)
    {
        if (request.Platform == CapturePlatform.LocalTshark) { await RunLocalAsync(request, ct).ConfigureAwait(false); return; }
        if (device is null) throw new InvalidOperationException("Pick an SSH device to capture on.");

        var command = CaptureCommands.Build(request);
        LineReceived?.Invoke($"$ {command}");
        using var ssh = await SshConnector.ConnectAsync(device, ct).ConfigureAwait(false);
        using var cmd = ssh.Client.CreateCommand(command);
        var exec = cmd.ExecuteAsync(ct);

        if (request.Platform == CapturePlatform.RemoteTcpdump && request.UseSudo)
        {
            using var stdin = cmd.CreateInputStream();
            var pw = Encoding.UTF8.GetBytes(device.Password + "\n");
            await stdin.WriteAsync(pw, ct).ConfigureAwait(false);
        }

        var errTask = PumpTextAsync(cmd.ExtendedOutputStream, l => LineReceived?.Invoke(l), ct);
        try
        {
            if (request.PcapFile is not null) await PumpToFileAsync(cmd.OutputStream, request.PcapFile, ct).ConfigureAwait(false);
            else await PumpTextAsync(cmd.OutputStream, l => LineReceived?.Invoke(l), ct).ConfigureAwait(false);
            await exec.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { cmd.CancelAsync(); } catch { }
            throw;
        }
        finally
        {
            try { await errTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }
    }

    private async Task RunLocalAsync(CaptureRequest request, CancellationToken ct)
    {
        var tshark = CaptureCommands.FindTshark() ?? throw new FileNotFoundException(
            "tshark.exe not found. Install Wireshark (with Npcap) to capture on this PC, or capture remotely over SSH.");
        var psi = new ProcessStartInfo(tshark) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in CaptureCommands.TsharkArgs(request)) psi.ArgumentList.Add(a);
        LineReceived?.Invoke($"> {CaptureCommands.Build(request)}");
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start tshark.");
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        var err = PumpTextAsync(proc.StandardError.BaseStream, l => LineReceived?.Invoke(l), CancellationToken.None);
        await PumpTextAsync(proc.StandardOutput.BaseStream, l => LineReceived?.Invoke(l), CancellationToken.None).ConfigureAwait(false);
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await err.ConfigureAwait(false);
        if (request.PcapFile is not null && File.Exists(request.PcapFile)) BytesWritten?.Invoke(new FileInfo(request.PcapFile).Length);
        ct.ThrowIfCancellationRequested();
    }

    private static async Task PumpTextAsync(Stream stream, Action<string> onLine, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            onLine(line);
        }
    }

    private async Task PumpToFileAsync(Stream stream, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = File.Create(path);
        var buf = new byte[1 << 16];
        long total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await file.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            await file.FlushAsync(ct).ConfigureAwait(false);
            total += n;
            BytesWritten?.Invoke(total);
        }
    }
}
