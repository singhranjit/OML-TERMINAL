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

/// <summary>Everything netstat -ano shows, plus the owning process name, read straight from the IP Helper API
/// (GetExtendedTcpTable / GetExtendedUdpTable) for IPv4 and IPv6.</summary>
public static class LocalPorts
{
    public static IReadOnlyList<LocalPortEntry> Snapshot()
    {
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
