using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OmlTerminal.Core.NetTools;

public sealed record LocalPortEntry(string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort,
    string State, int Pid, string ProcessName)
{
    public string Local => $"{FormatAddress(LocalAddress)}:{LocalPort}";
    public string Remote => RemotePort == 0 && Protocol.StartsWith("UDP") ? "*:*" : $"{FormatAddress(RemoteAddress)}:{RemotePort}";
    private static string FormatAddress(string a) => a.Contains(':') ? $"[{a}]" : a;
}

/// <summary>Everything netstat -ano shows, plus the owning process name. Windows: the IP Helper API
/// (GetExtendedTcpTable / GetExtendedUdpTable). Linux: /proc/net/{tcp,udp}{,6}, with owners found through each
/// process's open socket inodes (other users' processes need root to see, as with ss -p).</summary>
public static class LocalPorts
{
    public static IReadOnlyList<LocalPortEntry> Snapshot()
    {
        if (OperatingSystem.IsLinux()) return LinuxSnapshot();
        if (!OperatingSystem.IsWindows()) return [];
        var names = new Dictionary<int, string>();
        string NameOf(int pid)
        {
            if (names.TryGetValue(pid, out var n)) return n;
            try { n = pid == 0 ? "System Idle" : pid == 4 ? "System" : Process.GetProcessById(pid).ProcessName; }
            catch { n = "?"; }
            return names[pid] = n;
        }

        var list = new List<LocalPortEntry>();
        list.AddRange(Tcp(AF_INET, NameOf));
        list.AddRange(Tcp(AF_INET6, NameOf));
        list.AddRange(Udp(AF_INET, NameOf));
        list.AddRange(Udp(AF_INET6, NameOf));
        return list.OrderBy(e => e.State == "LISTEN" ? 0 : 1).ThenBy(e => e.LocalPort).ToList();
    }

    private static IReadOnlyList<LocalPortEntry> LinuxSnapshot()
    {
        var owners = SocketOwners();
        var list = new List<LocalPortEntry>();
        foreach (var (file, proto) in new[] { ("tcp", "TCP"), ("tcp6", "TCPv6"), ("udp", "UDP"), ("udp6", "UDPv6") })
        {
            string text;
            try { text = File.ReadAllText("/proc/net/" + file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            list.AddRange(ParseProcNet(text, proto, owners));
        }
        return list.OrderBy(e => e.State == "LISTEN" ? 0 : 1).ThenBy(e => e.LocalPort).ToList();
    }

    /// <summary>socket inode -> (pid, process name), from the "socket:[inode]" links in /proc/PID/fd.</summary>
    private static Dictionary<long, (int Pid, string Name)> SocketOwners()
    {
        var map = new Dictionary<long, (int, string)>();
        IEnumerable<string> pids;
        try { pids = Directory.EnumerateDirectories("/proc"); } catch { return map; }
        foreach (var dir in pids)
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            string? name = null;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")))
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                    if (!long.TryParse(target.AsSpan(8, target.Length - 9), out long inode)) continue;
                    name ??= ReadComm(dir);
                    map.TryAdd(inode, (pid, name));
                }
            }
            catch { } // another user's process (needs root), or it exited while we looked
        }
        return map;

        static string ReadComm(string dir)
        {
            try { return File.ReadAllText(Path.Combine(dir, "comm")).Trim(); } catch { return "?"; }
        }
    }

    /// <summary>Parses one /proc/net/tcp|udp[6] table. Addresses are hex in kernel (little-endian) word order.</summary>
    public static IEnumerable<LocalPortEntry> ParseProcNet(string text, string protocol, IReadOnlyDictionary<long, (int Pid, string Name)>? owners = null)
    {
        bool tcp = protocol.StartsWith("TCP", StringComparison.Ordinal);
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10) continue;
            if (!TryEndpoint(f[1], out var localIp, out int localPort) || !TryEndpoint(f[2], out var remoteIp, out int remotePort)) continue;
            int st = Convert.ToInt32(f[3], 16);
            string state = tcp ? LinuxTcpState(st) : st == 1 ? "ESTABLISHED" : "";
            long.TryParse(f[9], out long inode);
            var (pid, name) = owners is not null && owners.TryGetValue(inode, out var o) ? o : (0, inode == 0 ? "" : "?");
            // Like the Windows table: no peer for a listener, and UDP shows "*" (it's connectionless).
            bool noPeer = !tcp || state == "LISTEN";
            yield return new LocalPortEntry(protocol, localIp, localPort, tcp ? remoteIp : "*", noPeer ? 0 : remotePort, state, pid, name);
        }
    }

    private static string LinuxTcpState(int st) => st switch
    {
        1 => "ESTABLISHED", 2 => "SYN_SENT", 3 => "SYN_RCVD", 4 => "FIN_WAIT1", 5 => "FIN_WAIT2", 6 => "TIME_WAIT",
        7 => "CLOSED", 8 => "CLOSE_WAIT", 9 => "LAST_ACK", 10 => "LISTEN", 11 => "CLOSING", _ => st.ToString(),
    };

    private static bool TryEndpoint(string hex, out string address, out int port)
    {
        address = ""; port = 0;
        int colon = hex.IndexOf(':');
        if (colon < 0) return false;
        port = Convert.ToInt32(hex[(colon + 1)..], 16);
        var a = hex[..colon];
        if (a.Length is not (8 or 32)) return false;
        var bytes = new byte[a.Length / 2];
        // Each 32-bit word is printed as the host's (little-endian) integer: reverse the bytes within every word.
        for (int w = 0; w < bytes.Length / 4; w++)
            for (int b = 0; b < 4; b++)
                bytes[w * 4 + b] = Convert.ToByte(a.Substring(w * 8 + (3 - b) * 2, 2), 16);
        var ip = new IPAddress(bytes);
        address = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        return true;
    }

    public static string TcpStateName(uint state) => state switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RCVD", 5 => "ESTABLISHED", 6 => "FIN_WAIT1",
        7 => "FIN_WAIT2", 8 => "CLOSE_WAIT", 9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB",
        _ => state.ToString(),
    };

    /// <summary>The table stores ports in network byte order in the low 16 bits of a DWORD.</summary>
    public static int PortFromDword(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

    private static IEnumerable<LocalPortEntry> Tcp(int af, Func<int, string> nameOf)
    {
        var buffer = Fetch((IntPtr b, ref int size) => GetExtendedTcpTable(b, ref size, true, af, TCP_TABLE_OWNER_PID_ALL, 0));
        if (buffer == IntPtr.Zero) yield break;
        try
        {
            int count = Marshal.ReadInt32(buffer);
            IntPtr row = buffer + 4;
            if (af == AF_INET)
            {
                int size = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++, row += size)
                {
                    var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                    yield return new LocalPortEntry("TCP", new IPAddress(r.localAddr).ToString(), PortFromDword(r.localPort),
                        new IPAddress(r.remoteAddr).ToString(), r.state == 2 ? 0 : PortFromDword(r.remotePort),
                        TcpStateName(r.state), (int)r.owningPid, nameOf((int)r.owningPid));
                }
            }
            else
            {
                int size = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < count; i++, row += size)
                {
                    var r = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(row);
                    yield return new LocalPortEntry("TCPv6", new IPAddress(r.localAddr).ToString(), PortFromDword(r.localPort),
                        new IPAddress(r.remoteAddr).ToString(), r.state == 2 ? 0 : PortFromDword(r.remotePort),
                        TcpStateName(r.state), (int)r.owningPid, nameOf((int)r.owningPid));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IEnumerable<LocalPortEntry> Udp(int af, Func<int, string> nameOf)
    {
        var buffer = Fetch((IntPtr b, ref int size) => GetExtendedUdpTable(b, ref size, true, af, UDP_TABLE_OWNER_PID, 0));
        if (buffer == IntPtr.Zero) yield break;
        try
        {
            int count = Marshal.ReadInt32(buffer);
            IntPtr row = buffer + 4;
            if (af == AF_INET)
            {
                int size = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < count; i++, row += size)
                {
                    var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(row);
                    yield return new LocalPortEntry("UDP", new IPAddress(r.localAddr).ToString(), PortFromDword(r.localPort),
                        "*", 0, "", (int)r.owningPid, nameOf((int)r.owningPid));
                }
            }
            else
            {
                int size = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                for (int i = 0; i < count; i++, row += size)
                {
                    var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(row);
                    yield return new LocalPortEntry("UDPv6", new IPAddress(r.localAddr).ToString(), PortFromDword(r.localPort),
                        "*", 0, "", (int)r.owningPid, nameOf((int)r.owningPid));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private delegate uint TableCall(IntPtr buffer, ref int size);

    /// <summary>Standard size-probe-then-fill loop; the table can grow between the two calls, hence the retry.</summary>
    private static IntPtr Fetch(TableCall call)
    {
        int size = 0;
        call(IntPtr.Zero, ref size);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            uint rc = call(buffer, ref size);
            if (rc == 0) return buffer;
            Marshal.FreeHGlobal(buffer);
            if (rc != 122) break; // ERROR_INSUFFICIENT_BUFFER
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID { public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId, localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
        public uint remoteScopeId, remotePort, state, owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID { public uint localAddr, localPort, owningPid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId, localPort, owningPid;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
