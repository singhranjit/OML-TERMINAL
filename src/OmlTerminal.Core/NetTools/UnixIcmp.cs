using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.NetTools;

/// <summary>How ICMP is sent on Linux/macOS - the best one this process is allowed to use.</summary>
public enum UnixIcmpMode
{
    /// <summary>Raw ICMP socket: root, or the binary has cap_net_raw (the .deb grants it, as mtr's package does).</summary>
    Raw,
    /// <summary>Unprivileged ICMP ("ping") socket - Linux when net.ipv4.ping_group_range includes the user (Ubuntu and
    /// Fedora default), and macOS always. TTL-expired replies arrive on the socket's error queue (Linux) or as data (macOS).</summary>
    Datagram,
    /// <summary>Neither is allowed: run the system ping command (setuid/cap_net_raw) once per probe. Works everywhere,
    /// but is slower and times TTL-expired hops less precisely.</summary>
    PingCommand,
}

public sealed record IcmpReply(IPAddress? From, IPStatus Status, double RttMs, int Ttl);

/// <summary>
/// IPv4 ICMP echo for Linux and macOS without needing to run as root. .NET's Ping falls back to the ping command when it
/// can't open a raw socket, and that path refuses custom payloads and can't report the router that answered a
/// TTL-limited probe - which is everything a traceroute needs. One shared socket serves every probe in the process
/// (Ping Monitor may have hundreds in flight); replies are matched by sequence number, and ICMP errors by the original
/// echo header they quote.
/// </summary>
public static class UnixIcmp
{
    private static readonly Lazy<Engine?> Shared = new(Engine.TryCreate);

    /// <summary>The mode in use. OML_ICMP_MODE=raw|datagram|command forces one (for testing).</summary>
    [UnsupportedOSPlatform("windows")]
    public static UnixIcmpMode Mode => Shared.Value?.Mode ?? UnixIcmpMode.PingCommand;

    /// <summary>One line for the UI explaining the mode, and how to get a better one when on the fallback.</summary>
    [UnsupportedOSPlatform("windows")]
    public static string ModeDescription => Mode switch
    {
        UnixIcmpMode.Raw => "ICMP: raw socket",
        UnixIcmpMode.Datagram => "ICMP: unprivileged ping socket",
        _ => OperatingSystem.IsLinux()
            ? "ICMP: using the system ping command (slower). For full speed: sudo setcap cap_net_raw+ep on the oml-terminal binary, or allow ping sockets with sysctl net.ipv4.ping_group_range."
            : "ICMP: using the system ping command (slower).",
    };

    [UnsupportedOSPlatform("windows")]
    public static Task<IcmpReply> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct = default)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork) throw new NotSupportedException("UnixIcmp is IPv4 only.");
        ttl = Math.Clamp(ttl, 1, 255);
        size = Math.Clamp(size, 0, 1472);
        return Shared.Value is { } engine
            ? engine.SendAsync(target, ttl, timeoutMs, size, ct)
            : PingCommand.SendAsync(target, ttl, timeoutMs, size, ct);
    }

    internal static IPStatus StatusFor(int type, int code) => (type, code) switch
    {
        (0, _) => IPStatus.Success,
        (11, _) => IPStatus.TtlExpired,
        (3, 0) or (3, 6) or (3, 11) => IPStatus.DestinationNetworkUnreachable,
        (3, 1) or (3, 7) or (3, 12) => IPStatus.DestinationHostUnreachable,
        (3, 2) => IPStatus.DestinationProtocolUnreachable,
        (3, 3) => IPStatus.DestinationPortUnreachable,
        (3, 4) => IPStatus.PacketTooBig,
        (3, 9) or (3, 10) or (3, 13) => IPStatus.DestinationProhibited,
        (3, _) => IPStatus.DestinationUnreachable,
        (12, _) => IPStatus.ParameterProblem,
        _ => IPStatus.Unknown,
    };

    /// <summary>An ICMP echo request: type 8, our id and sequence number, zero-filled payload, checksum.</summary>
    internal static byte[] BuildEcho(ushort id, ushort seq, int size)
    {
        var p = new byte[8 + size];
        p[0] = 8;
        p[4] = (byte)(id >> 8); p[5] = (byte)id;
        p[6] = (byte)(seq >> 8); p[7] = (byte)seq;
        ushort sum = Checksum(p);
        p[2] = (byte)(sum >> 8); p[3] = (byte)sum;
        return p;
    }

    internal static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (data.Length % 2 == 1) sum += (uint)(data[^1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>A received ICMP message reduced to what matching needs.</summary>
    internal readonly record struct Parsed(int Type, int Code, ushort Id, ushort Seq, IPAddress? QuotedDestination, int Ttl);

    /// <summary>Parses a datagram read from an ICMP socket - with an IPv4 header in front (raw sockets; macOS ping
    /// sockets) or without (Linux ping sockets). Echo replies carry id/seq directly; errors (time exceeded,
    /// unreachable) quote the IP header and first 8 bytes of our original echo request, which carry them instead.</summary>
    internal static Parsed? Parse(ReadOnlySpan<byte> b)
    {
        int ttl = 0, off = 0;
        // ICMP types are all below 64, so a first nibble of 4 can only be an IPv4 header.
        if (b.Length >= 28 && b[0] >> 4 == 4)
        {
            off = (b[0] & 0x0F) * 4;
            ttl = b[8];
        }
        if (b.Length < off + 8) return null;
        int type = b[off], code = b[off + 1];
        if (type == 0)
            return new Parsed(type, code, (ushort)(b[off + 4] << 8 | b[off + 5]), (ushort)(b[off + 6] << 8 | b[off + 7]), null, ttl);
        if (type is not (3 or 11 or 12)) return null;
        int inner = off + 8;
        if (b.Length < inner + 20 || b[inner] >> 4 != 4) return null;
        int innerIcmp = inner + (b[inner] & 0x0F) * 4;
        if (b.Length < innerIcmp + 8 || b[innerIcmp] != 8) return null; // not a reply to an echo request
        var dst = new IPAddress(b.Slice(inner + 16, 4));
        return new Parsed(type, code, (ushort)(b[innerIcmp + 4] << 8 | b[innerIcmp + 5]), (ushort)(b[innerIcmp + 6] << 8 | b[innerIcmp + 7]), dst, ttl);
    }

    private sealed class Engine
    {
        private sealed class Pending(IPAddress target)
        {
            public readonly IPAddress Target = target;
            public readonly TaskCompletionSource<IcmpReply> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public long Started;
        }

        private readonly Socket _socket;
        private readonly ushort _id = (ushort)Environment.ProcessId;
        private readonly ConcurrentDictionary<ushort, Pending> _pending = new();
        private readonly object _sendLock = new();
        private int _seq = Random.Shared.Next(ushort.MaxValue);
        private int _lastTtl = -1;

        public UnixIcmpMode Mode { get; }

        private Engine(Socket socket, UnixIcmpMode mode)
        {
            _socket = socket;
            Mode = mode;
            try { _socket.ReceiveBufferSize = 1 << 20; } catch { }
            if (mode == UnixIcmpMode.Datagram && OperatingSystem.IsLinux())
            {
                int one = 1;
                Native.setsockopt((int)_socket.Handle, 0 /* SOL_IP */, 11 /* IP_RECVERR */, ref one, sizeof(int));
            }
            new Thread(ReceiveLoop) { IsBackground = true, Name = "OML ICMP receive" }.Start();
        }

        public static Engine? TryCreate()
        {
            var forced = Environment.GetEnvironmentVariable("OML_ICMP_MODE")?.Trim().ToLowerInvariant();
            if (forced is "command") return null;
            if (forced is null or "" or "raw")
            {
                try { return new Engine(new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp), UnixIcmpMode.Raw); }
                catch (SocketException) { }
            }
            if (forced is null or "" or "datagram")
            {
                try { return new Engine(new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp), UnixIcmpMode.Datagram); }
                catch (SocketException) { }
            }
            return null;
        }

        public async Task<IcmpReply> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct)
        {
            var p = new Pending(target);
            ushort seq;
            do seq = (ushort)Interlocked.Increment(ref _seq);
            while (!_pending.TryAdd(seq, p));
            try
            {
                var packet = BuildEcho(_id, seq, size);
                lock (_sendLock)
                {
                    if (ttl != _lastTtl) { _socket.Ttl = (short)ttl; _lastTtl = ttl; }
                    p.Started = Stopwatch.GetTimestamp();
                    _socket.SendTo(packet, new IPEndPoint(target, 0));
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Math.Max(1, timeoutMs));
                await using (timeout.Token.Register(() => p.Tcs.TrySetResult(new IcmpReply(null, IPStatus.TimedOut, 0, 0))))
                {
                    var reply = await p.Tcs.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return reply;
                }
            }
            catch (SocketException ex)
            {
                // The local stack refused before anything was sent (no route, interface down).
                var status = ex.SocketErrorCode switch
                {
                    SocketError.NetworkUnreachable => IPStatus.DestinationNetworkUnreachable,
                    SocketError.HostUnreachable => IPStatus.DestinationHostUnreachable,
                    SocketError.AccessDenied => IPStatus.DestinationProhibited,
                    _ => IPStatus.Unknown,
                };
                return new IcmpReply(null, status, 0, 0);
            }
            finally { _pending.TryRemove(seq, out _); }
        }

        private void Complete(ushort seq, int type, int code, IPAddress from, int ttl, ushort? id, IPAddress? quotedDst)
        {
            long now = Stopwatch.GetTimestamp();
            if (!_pending.TryGetValue(seq, out var p)) return;
            // Raw sockets see every ICMP message on the host: make sure it's ours (our id) and about the right target.
            if (Mode == UnixIcmpMode.Raw && id is { } i && i != _id) return;
            if (type == 0 ? !from.Equals(p.Target) : quotedDst is not null && !quotedDst.Equals(p.Target)) return;
            double ms = Stopwatch.GetElapsedTime(p.Started, now).TotalMilliseconds;
            p.Tcs.TrySetResult(new IcmpReply(from, StatusFor(type, code), ms, ttl));
        }

        private void ReceiveLoop()
        {
            int fd = (int)_socket.Handle;
            const int BufSize = 2048, CtrlSize = 512, AddrSize = 128;
            IntPtr buf = Marshal.AllocHGlobal(BufSize), ctrl = Marshal.AllocHGlobal(CtrlSize), addr = Marshal.AllocHGlobal(AddrSize);
            IntPtr iov = Marshal.AllocHGlobal(Marshal.SizeOf<Native.IoVec>()), msg = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MsgHdr>());
            var managed = new byte[BufSize];
            int dontWait = OperatingSystem.IsMacOS() ? 0x80 : 0x40;
            bool errQueue = Mode == UnixIcmpMode.Datagram && OperatingSystem.IsLinux();
            try
            {
                while (true)
                {
                    var pfd = new Native.PollFd { Fd = fd, Events = 0x1 /* POLLIN */ };
                    int r = Native.poll(ref pfd, 1, 1000);
                    if (r < 0 && Marshal.GetLastPInvokeError() != 4 /* EINTR */) return;
                    if (r <= 0) continue;

                    // Linux ping sockets: TTL-expired / unreachable replies are queued as errors, not data.
                    while (errQueue && ReadError(fd, buf, BufSize, ctrl, CtrlSize, iov, msg, managed, dontWait)) { }

                    while (true)
                    {
                        int addrLen = AddrSize;
                        int n = Native.recvfrom(fd, buf, BufSize, dontWait, addr, ref addrLen);
                        if (n <= 0) break;
                        Marshal.Copy(buf, managed, 0, n);
                        var from = new IPAddress(ReadAddr(addr, 4)); // sockaddr_in.sin_addr: offset 4 on Linux and macOS
                        if (Parse(managed.AsSpan(0, n)) is { } m)
                            Complete(m.Seq, m.Type, m.Code, from, m.Ttl, Mode == UnixIcmpMode.Raw ? m.Id : null, m.QuotedDestination);
                    }
                }
            }
            catch (ObjectDisposedException) { }
            finally
            {
                Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(ctrl); Marshal.FreeHGlobal(addr);
                Marshal.FreeHGlobal(iov); Marshal.FreeHGlobal(msg);
            }
        }

        private static byte[] ReadAddr(IntPtr p, int offset)
        {
            var a = new byte[4];
            Marshal.Copy(p + offset, a, 0, 4);
            return a;
        }

        /// <summary>Reads one entry from a Linux socket error queue: the payload is our original echo request (so the
        /// sequence number), and an IP_RECVERR control message says what happened and which router said it.</summary>
        private bool ReadError(int fd, IntPtr buf, int bufSize, IntPtr ctrl, int ctrlSize, IntPtr iov, IntPtr msg, byte[] managed, int dontWait)
        {
            Marshal.StructureToPtr(new Native.IoVec { Base = buf, Len = (UIntPtr)bufSize }, iov, false);
            Marshal.StructureToPtr(new Native.MsgHdr { Iov = iov, IovLen = (UIntPtr)1, Control = ctrl, ControlLen = (UIntPtr)ctrlSize }, msg, false);
            int n = Native.recvmsg(fd, msg, 0x2000 /* MSG_ERRQUEUE */ | dontWait);
            if (n < 0) return false;
            var hdr = Marshal.PtrToStructure<Native.MsgHdr>(msg);
            if (n < 8) return true;
            Marshal.Copy(buf, managed, 0, 8);
            if (managed[0] != 8) return true;
            ushort seq = (ushort)(managed[6] << 8 | managed[7]);

            long ctrlLen = (long)hdr.ControlLen.ToUInt64();
            for (long off = 0; off + 16 <= ctrlLen;)
            {
                long len = Marshal.ReadInt64(ctrl, (int)off);
                int level = Marshal.ReadInt32(ctrl, (int)off + 8), type = Marshal.ReadInt32(ctrl, (int)off + 12);
                if (len < 16) break;
                if (level == 0 /* SOL_IP */ && type == 11 /* IP_RECVERR */ && len >= 16 + 16 + 8)
                {
                    int data = (int)off + 16;
                    byte origin = Marshal.ReadByte(ctrl, data + 4);
                    if (origin == 2 /* SO_EE_ORIGIN_ICMP */)
                    {
                        int icmpType = Marshal.ReadByte(ctrl, data + 5), icmpCode = Marshal.ReadByte(ctrl, data + 6);
                        var offender = new IPAddress(ReadAddr(ctrl, data + 16 + 4)); // sockaddr_in right after sock_extended_err
                        Complete(seq, icmpType, icmpCode, offender, 0, null, null);
                    }
                }
                off += (len + 7) & ~7L;
            }
            return true;
        }
    }

    /// <summary>Last resort: the system ping (setuid or cap_net_raw), one process per probe.</summary>
    internal static class PingCommand
    {
        public static async Task<IcmpReply> SendAsync(IPAddress target, int ttl, int timeoutMs, int size, CancellationToken ct)
        {
            int secs = Math.Max(1, (timeoutMs + 999) / 1000);
            var args = OperatingSystem.IsMacOS()
                ? new[] { "-n", "-c", "1", "-m", ttl.ToString(), "-W", Math.Max(1, timeoutMs).ToString(), "-s", size.ToString(), target.ToString() }
                : new[] { "-n", "-c", "1", "-t", ttl.ToString(), "-W", secs.ToString(), "-s", size.ToString(), target.ToString() };
            var psi = new ProcessStartInfo("ping") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C"; // the parser below reads English output
            var sw = Stopwatch.StartNew();
            try
            {
                using var proc = Process.Start(psi);
                if (proc is null) return new IcmpReply(null, IPStatus.Unknown, 0, 0);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(timeoutMs + 2000);
                var output = proc.StandardOutput.ReadToEndAsync(limit.Token);
                try { await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return new IcmpReply(null, IPStatus.TimedOut, 0, 0);
                }
                return ParseOutput(await output.ConfigureAwait(false), sw.Elapsed.TotalMilliseconds);
            }
            catch (System.ComponentModel.Win32Exception) { return new IcmpReply(null, IPStatus.Unknown, 0, 0); }
        }

        private static readonly Regex Echo = new(@"bytes from (?<ip>[\d.]+):.*?ttl=(?<ttl>\d+).*?time[=<](?<ms>[\d.]+)", RegexOptions.IgnoreCase);
        private static readonly Regex Error = new(@"(?:From|bytes from) (?<ip>[\d.]+)[:\s](?:\s*icmp_seq=\d+)?\s*(?<msg>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>Reads iputils (Linux) or BSD (macOS) ping output for a single probe.</summary>
        public static IcmpReply ParseOutput(string output, double elapsedMs)
        {
            if (Echo.Match(output) is { Success: true } e)
                return new IcmpReply(IPAddress.Parse(e.Groups["ip"].Value), IPStatus.Success,
                    double.Parse(e.Groups["ms"].Value, CultureInfo.InvariantCulture), int.Parse(e.Groups["ttl"].Value));
            foreach (Match m in Error.Matches(output))
            {
                var msg = m.Groups["msg"].Value;
                IPStatus? status =
                    msg.Contains("Time to live exceeded", StringComparison.OrdinalIgnoreCase) ? IPStatus.TtlExpired
                    : msg.Contains("Net Unreachable", StringComparison.OrdinalIgnoreCase) ? IPStatus.DestinationNetworkUnreachable
                    : msg.Contains("Host Unreachable", StringComparison.OrdinalIgnoreCase) ? IPStatus.DestinationHostUnreachable
                    : msg.Contains("Port Unreachable", StringComparison.OrdinalIgnoreCase) ? IPStatus.DestinationPortUnreachable
                    : msg.Contains("Prohibited", StringComparison.OrdinalIgnoreCase) || msg.Contains("filtered", StringComparison.OrdinalIgnoreCase) ? IPStatus.DestinationProhibited
                    : null;
                if (status is { } s) return new IcmpReply(IPAddress.Parse(m.Groups["ip"].Value), s, elapsedMs, 0);
            }
            return new IcmpReply(null, IPStatus.TimedOut, 0, 0);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd { public int Fd; public short Events, REvents; }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoVec { public IntPtr Base; public UIntPtr Len; }

        /// <summary>struct msghdr - identical on 64-bit Linux and macOS for the fields used (socklen_t is padded).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MsgHdr
        {
            public IntPtr Name;
            public uint NameLen;
            public IntPtr Iov;
            public UIntPtr IovLen;
            public IntPtr Control;
            public UIntPtr ControlLen;
            public int Flags;
        }

        [DllImport("libc", SetLastError = true)] public static extern int poll(ref PollFd fds, uint nfds, int timeout);
        [DllImport("libc", SetLastError = true)] public static extern int recvfrom(int fd, IntPtr buf, int len, int flags, IntPtr addr, ref int addrLen);
        [DllImport("libc", SetLastError = true)] public static extern int recvmsg(int fd, IntPtr msg, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int setsockopt(int fd, int level, int name, ref int value, int len);
    }
}
