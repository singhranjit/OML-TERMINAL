using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace OmlTerminal.Core.Capture;

/// <param name="Id">Npcap device name (\Device\NPF_{GUID}) or, for the raw-socket fallback, the adapter's IPv4 address.</param>
public sealed record CaptureInterface(string Id, string Name, string Description, bool IsWireless, bool IsUp, IReadOnlyList<string> Addresses, bool ViaNpcap)
{
    public override string ToString() => $"{Name}  ·  {(IsWireless ? "Wi-Fi" : Name == "Loopback" ? "this PC" : "Ethernet")}{(Addresses.Count > 0 ? "  ·  " + Addresses[0] : "")}{(IsUp ? "" : "  (down)")}";
}

public interface ILiveCapture : IDisposable
{
    int LinkType { get; }
    /// <summary>Raised on the capture thread for every frame.</summary>
    event Action<RawFrame>? FrameArrived;
    /// <summary>Raised once if the capture stops on its own (error, adapter gone).</summary>
    event Action<string>? Failed;
    void Start();
    void Stop();
    (long Received, long Dropped) Statistics { get; }
}

public static class CaptureEngine
{
    private static readonly string NpcapDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");

    public static bool NpcapInstalled => File.Exists(Path.Combine(NpcapDir, "wpcap.dll"));

    /// <summary>Adapters to capture on - every Npcap device when Npcap is installed, otherwise each adapter with an
    /// IPv4 address (the raw-socket fallback can only bind to an address).</summary>
    public static IReadOnlyList<CaptureInterface> ListInterfaces()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        if (NpcapInstalled)
        {
            try
            {
                var list = new List<CaptureInterface>();
                foreach (var (name, desc) in Npcap.Devices())
                {
                    var guid = name.Contains('{') ? name[name.IndexOf('{')..] : "";
                    var nic = nics.FirstOrDefault(n => n.Id.Equals(guid, StringComparison.OrdinalIgnoreCase));
                    if (nic is null && !name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;
                    // Idle virtual adapters (Wi-Fi Direct, WAN miniports) - keep Wi-Fi even when it's disconnected.
                    if (nic is not null && nic.OperationalStatus != OperationalStatus.Up && Ipv4Of(nic).Count == 0
                        && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    list.Add(nic is null
                        ? new CaptureInterface(name, "Loopback", "Npcap loopback (this PC talking to itself)", false, true, ["127.0.0.1"], true)
                        : new CaptureInterface(name, nic.Name, nic.Description, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                            nic.OperationalStatus == OperationalStatus.Up, Ipv4Of(nic), true));
                }
                return list.OrderByDescending(i => i.IsUp).ThenByDescending(i => i.Addresses.Count).ToList();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        }
        return nics.Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && Ipv4Of(n).Count > 0)
            .Select(n => new CaptureInterface(Ipv4Of(n)[0], n.Name, n.Description, n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                n.OperationalStatus == OperationalStatus.Up, Ipv4Of(n), false))
            .ToList();
    }

    private static List<string> Ipv4Of(NetworkInterface n)
    {
        try { return n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(); }
        catch (NetworkInformationException) { return []; }
    }

    public static ILiveCapture Open(CaptureInterface iface, string bpfFilter, bool promiscuous, bool monitorMode) =>
        iface.ViaNpcap ? new NpcapCapture(iface.Id, bpfFilter, promiscuous, monitorMode) : new RawSocketCapture(iface.Id);
}

/// <summary>Capture through Npcap (Wireshark's driver): full link-layer frames, BPF capture filters, and 802.11
/// monitor mode on adapters that support it.</summary>
internal sealed class NpcapCapture(string device, string bpf, bool promiscuous, bool monitorMode) : ILiveCapture
{
    private IntPtr _handle;
    private Thread? _thread;
    private volatile bool _stop;

    public int LinkType { get; private set; } = LinkTypes.Ethernet;
    public event Action<RawFrame>? FrameArrived;
    public event Action<string>? Failed;

    public (long Received, long Dropped) Statistics
    {
        get
        {
            if (_handle == IntPtr.Zero) return (0, 0);
            return Npcap.pcap_stats(_handle, out var s) == 0 ? (s.ps_recv, s.ps_drop + s.ps_ifdrop) : (0, 0);
        }
    }

    public void Start()
    {
        Npcap.EnsureLoaded();
        var err = new StringBuilder(512);
        _handle = Npcap.pcap_create(device, err);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException($"Couldn't open the adapter: {err}");
        Npcap.pcap_set_snaplen(_handle, 262144);
        Npcap.pcap_set_promisc(_handle, promiscuous ? 1 : 0);
        Npcap.pcap_set_timeout(_handle, 200);
        Npcap.pcap_set_buffer_size(_handle, 16 * 1024 * 1024);
        Npcap.pcap_set_immediate_mode(_handle, 1);
        if (monitorMode && Npcap.pcap_set_rfmon(_handle, 1) != 0)
            throw Fail("This adapter can't do monitor mode with Npcap (it needs a supported Wi-Fi chipset and Npcap installed with \"802.11 raw\" support).");
        int rc = Npcap.pcap_activate(_handle);
        if (rc < 0)
            throw Fail(rc == -8 /* PCAP_ERROR_PERM_DENIED */
                ? "Permission denied - Npcap may be installed in \"admin only\" mode; run OML Terminal as administrator."
                : $"Couldn't start capturing: {Npcap.Error(_handle)}");
        LinkType = Npcap.pcap_datalink(_handle);
        if (!string.IsNullOrWhiteSpace(bpf))
        {
            if (Npcap.pcap_compile(_handle, out var prog, bpf, 1, 0xffffffff) != 0)
                throw Fail($"Capture filter error: {Npcap.Error(_handle)}");
            int set = Npcap.pcap_setfilter(_handle, ref prog);
            Npcap.pcap_freecode(ref prog);
            if (set != 0) throw Fail($"Couldn't apply the capture filter: {Npcap.Error(_handle)}");
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "npcap-capture" };
        _thread.Start();
    }

    private InvalidOperationException Fail(string message)
    {
        Npcap.pcap_close(_handle);
        _handle = IntPtr.Zero;
        return new InvalidOperationException(message);
    }

    private void Loop()
    {
        while (!_stop)
        {
            int rc = Npcap.pcap_next_ex(_handle, out var hdrPtr, out var dataPtr);
            if (rc == 1)
            {
                var hdr = Marshal.PtrToStructure<Npcap.pcap_pkthdr>(hdrPtr);
                var data = new byte[hdr.caplen];
                Marshal.Copy(dataPtr, data, 0, data.Length);
                var ts = DateTime.UnixEpoch.AddSeconds(hdr.tv_sec).AddTicks(hdr.tv_usec * 10L).ToLocalTime();
                FrameArrived?.Invoke(new RawFrame(ts, data, (int)hdr.len, LinkType));
            }
            else if (rc < 0 && !_stop)
            {
                Failed?.Invoke($"Capture stopped: {Npcap.Error(_handle)}");
                return;
            }
        }
    }

    public void Stop()
    {
        _stop = true;
        if (_handle != IntPtr.Zero) Npcap.pcap_breakloop(_handle);
        _thread?.Join(2000);
        if (_handle != IntPtr.Zero) { Npcap.pcap_close(_handle); _handle = IntPtr.Zero; }
    }

    public void Dispose() => Stop();
}

/// <summary>No-driver fallback: Windows' raw socket with SIO_RCVALL. Sees IPv4 packets on one address only (no
/// Ethernet header, ARP or VLAN tags) and needs administrator rights.</summary>
internal sealed class RawSocketCapture(string address) : ILiveCapture
{
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _stop;
    private long _received;

    public int LinkType => LinkTypes.Ipv4;
    public event Action<RawFrame>? FrameArrived;
    public event Action<string>? Failed;
    public (long Received, long Dropped) Statistics => (Interlocked.Read(ref _received), 0);

    public void Start()
    {
        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            _socket.Bind(new IPEndPoint(IPAddress.Parse(address), 0));
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
            _socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
            _socket.ReceiveTimeout = 500;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            _socket?.Dispose();
            throw new InvalidOperationException("Capturing without Npcap needs administrator rights. Run OML Terminal as administrator, or install Npcap (npcap.com) for full Ethernet-level capture.");
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "raw-capture" };
        _thread.Start();
    }

    private void Loop()
    {
        var buf = new byte[65535];
        while (!_stop)
        {
            try
            {
                int n = _socket!.Receive(buf);
                if (n <= 0) continue;
                Interlocked.Increment(ref _received);
                FrameArrived?.Invoke(new RawFrame(DateTime.Now, buf.AsSpan(0, n).ToArray(), n, LinkTypes.Ipv4));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (!_stop) Failed?.Invoke($"Capture stopped: {ex.Message}");
                return;
            }
        }
    }

    public void Stop()
    {
        _stop = true;
        try { _socket?.Close(); } catch { }
        _thread?.Join(1500);
    }

    public void Dispose() => Stop();
}

internal static class Npcap
{
    private const string Lib = "wpcap.dll";
    private static bool _loaded;

    /// <summary>Npcap keeps wpcap.dll in System32\Npcap, off the default DLL search path; loading it (and Packet.dll
    /// beside it) by full path first makes the [DllImport]s below bind to it.</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
        NativeLibrary.Load(Path.Combine(dir, "Packet.dll"));
        NativeLibrary.Load(Path.Combine(dir, "wpcap.dll"));
        _loaded = true;
    }

    public static List<(string Name, string Description)> Devices()
    {
        EnsureLoaded();
        var err = new StringBuilder(512);
        if (pcap_findalldevs(out var all, err) != 0) throw new InvalidOperationException(err.ToString());
        var list = new List<(string, string)>();
        try
        {
            for (var p = all; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
            {
                var name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(p, IntPtr.Size)) ?? "";
                var desc = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(p, IntPtr.Size * 2)) ?? "";
                list.Add((name, desc));
            }
        }
        finally { pcap_freealldevs(all); }
        return list;
    }

    public static string Error(IntPtr h) => h == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(pcap_geterr(h)) ?? "unknown error";

    [StructLayout(LayoutKind.Sequential)]
    public struct pcap_pkthdr { public int tv_sec; public int tv_usec; public uint caplen; public uint len; }

    [StructLayout(LayoutKind.Sequential)]
    public struct bpf_program { public uint bf_len; public IntPtr bf_insns; }

    [StructLayout(LayoutKind.Sequential)]
    public struct pcap_stat { public uint ps_recv; public uint ps_drop; public uint ps_ifdrop; }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_findalldevs(out IntPtr alldevs, StringBuilder errbuf);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void pcap_freealldevs(IntPtr alldevs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern IntPtr pcap_create(string source, StringBuilder errbuf);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_set_snaplen(IntPtr p, int snaplen);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_set_promisc(IntPtr p, int promisc);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_set_timeout(IntPtr p, int ms);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_set_buffer_size(IntPtr p, int size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_set_immediate_mode(IntPtr p, int on);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_set_rfmon(IntPtr p, int rfmon);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_activate(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_datalink(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int pcap_compile(IntPtr p, out bpf_program fp, string str, int optimize, uint netmask);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_setfilter(IntPtr p, ref bpf_program fp);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void pcap_freecode(ref bpf_program fp);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_next_ex(IntPtr p, out IntPtr header, out IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void pcap_breakloop(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void pcap_close(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr pcap_geterr(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int pcap_stats(IntPtr p, out pcap_stat stats);
}
