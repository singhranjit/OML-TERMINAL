using System.Runtime.InteropServices;
using System.Text;

namespace OmlTerminal.Core.Wifi;

public sealed record WifiAdapter(Guid Id, string Description, string State);

public sealed record WifiConnection(string Ssid, string Bssid, int Rssi, int SignalQuality, int RxRateMbps, int TxRateMbps, string ProfileName);

public sealed class WifiUnavailableException(string message) : Exception(message);

/// <summary>Source of scan results - the real Windows Wi-Fi API, or a recorded scan file for offline work.</summary>
public interface IWifiSource : IDisposable
{
    IReadOnlyList<WifiAdapter> Adapters();
    /// <summary>Asks the adapter for a fresh scan; results show up in the next GetNetworks a few seconds later.</summary>
    void RequestScan(Guid adapter);
    IReadOnlyList<WifiNetwork> GetNetworks(Guid adapter);
    WifiConnection? CurrentConnection(Guid adapter);
}

/// <summary>Windows Native Wi-Fi (wlanapi.dll) - no driver or extra install needed. Windows 11 also requires Location
/// access to be allowed for desktop apps before it will reveal access point details.</summary>
public sealed class WlanSource : IWifiSource
{
    private IntPtr _handle;

    public WlanSource()
    {
        uint rc;
        try { rc = WlanOpenHandle(2, IntPtr.Zero, out _, out _handle); }
        catch (DllNotFoundException) { throw new WifiUnavailableException("This PC has no Wi-Fi support (wlanapi.dll is missing)."); }
        if (rc == 1062 /* ERROR_SERVICE_NOT_ACTIVE */ || rc == 1068)
            throw new WifiUnavailableException("The WLAN AutoConfig service isn't running - this PC has no Wi-Fi adapter, or Wi-Fi is disabled.");
        if (rc != 0) throw new WifiUnavailableException($"Couldn't open the Wi-Fi API (error {rc}).");
    }

    public IReadOnlyList<WifiAdapter> Adapters()
    {
        Check(WlanEnumInterfaces(_handle, IntPtr.Zero, out var list));
        try
        {
            int count = Marshal.ReadInt32(list);
            var result = new List<WifiAdapter>();
            for (int k = 0; k < count; k++)
            {
                var item = list + 8 + k * 532;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var desc = Marshal.PtrToStringUni(item + 16, 256).TrimEnd('\0');
                int state = Marshal.ReadInt32(item + 16 + 512);
                result.Add(new WifiAdapter(guid, desc, state switch { 1 => "Connected", 4 => "Disconnected", 5 => "Associating", 6 => "Discovering", 7 => "Authenticating", 0 => "Not ready", _ => "Disconnecting" }));
            }
            return result;
        }
        finally { WlanFreeMemory(list); }
    }

    public void RequestScan(Guid adapter) => WlanScan(_handle, ref adapter, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public IReadOnlyList<WifiNetwork> GetNetworks(Guid adapter)
    {
        Check(WlanGetNetworkBssList(_handle, ref adapter, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out var list));
        try
        {
            int count = Marshal.ReadInt32(list + 4);
            var result = new List<WifiNetwork>(count);
            for (int k = 0; k < count; k++) result.Add(ParseEntry(list + 8 + k * 360));
            return result;
        }
        finally { WlanFreeMemory(list); }
    }

    /// <summary>WLAN_BSS_ENTRY is read by offset (its layout is fixed at 360 bytes) rather than marshalled as a struct.</summary>
    private static WifiNetwork ParseEntry(IntPtr e)
    {
        int ssidLen = Math.Min(32, Marshal.ReadInt32(e));
        var ssidBytes = new byte[ssidLen];
        Marshal.Copy(e + 4, ssidBytes, 0, ssidLen);
        var bssid = new byte[6];
        Marshal.Copy(e + 40, bssid, 0, 6);
        int rssi = Marshal.ReadInt32(e + 56);
        int quality = Marshal.ReadInt32(e + 60);
        int beacon = (ushort)Marshal.ReadInt16(e + 66);
        int capability = (ushort)Marshal.ReadInt16(e + 88);
        int freqKhz = Marshal.ReadInt32(e + 92);
        int rateCount = Math.Min(126, Marshal.ReadInt32(e + 96) / 2);
        double maxRate = 0;
        for (int r = 0; r < rateCount; r++) maxRate = Math.Max(maxRate, ((ushort)Marshal.ReadInt16(e + 100 + r * 2) & 0x7fff) * 0.5);
        int ieOffset = Marshal.ReadInt32(e + 352), ieSize = Marshal.ReadInt32(e + 356);
        var ies = new byte[Math.Max(0, ieSize)];
        if (ieSize > 0) Marshal.Copy(e + ieOffset, ies, 0, ieSize);

        int mhz = freqKhz / 1000;
        var (channel, band) = WifiMath.ChannelFor(mhz);
        var n = new WifiNetwork
        {
            Ssid = Encoding.UTF8.GetString(ssidBytes).TrimEnd('\0'),
            Bssid = string.Join(":", bssid.Select(b => b.ToString("x2"))),
            Rssi = rssi,
            LinkQuality = quality,
            FrequencyMHz = mhz,
            Channel = channel,
            Band = band,
            BeaconIntervalTu = beacon,
            MaxBasicRateMbps = maxRate,
        };
        InformationElements.Apply(n, ies, (capability & 0x0010) != 0);
        return n;
    }

    public WifiConnection? CurrentConnection(Guid adapter)
    {
        if (WlanQueryInterface(_handle, ref adapter, 7 /* current_connection */, IntPtr.Zero, out _, out var data, out _) != 0) return null;
        try
        {
            if (Marshal.ReadInt32(data) != 1 /* connected */) return null;
            var profile = Marshal.PtrToStringUni(data + 8, 256).TrimEnd('\0');
            var a = data + 520;
            int ssidLen = Math.Min(32, Marshal.ReadInt32(a));
            var ssid = new byte[ssidLen];
            Marshal.Copy(a + 4, ssid, 0, ssidLen);
            var bssid = new byte[6];
            Marshal.Copy(a + 40, bssid, 0, 6);
            int quality = Marshal.ReadInt32(a + 56);
            int rx = Marshal.ReadInt32(a + 60), tx = Marshal.ReadInt32(a + 64);
            int rssi = quality / 2 - 100;
            if (WlanQueryInterface(_handle, ref adapter, 0x10000102 /* rssi */, IntPtr.Zero, out _, out var rssiPtr, out _) == 0)
            {
                rssi = Marshal.ReadInt32(rssiPtr);
                WlanFreeMemory(rssiPtr);
            }
            return new WifiConnection(Encoding.UTF8.GetString(ssid), string.Join(":", bssid.Select(b => b.ToString("x2"))), rssi, quality, rx / 1000, tx / 1000, profile);
        }
        finally { WlanFreeMemory(data); }
    }

    private static void Check(uint rc)
    {
        if (rc == 0) return;
        if (rc == 5) throw new WifiUnavailableException("Windows blocked access to Wi-Fi details. Turn on Location services and allow desktop apps to use your location (Settings → Privacy & security → Location), then try again.");
        throw new WifiUnavailableException($"Wi-Fi API error {rc}.");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { WlanCloseHandle(_handle, IntPtr.Zero); _handle = IntPtr.Zero; }
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ieData, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool securityEnabled, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out uint dataSize, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}

/// <summary>Plays back a saved scan (JSON list of WifiNetwork per adapter) - for reviewing a survey off-site, or for
/// running the analyzer on a PC without Wi-Fi. Each GetNetworks wobbles the signal slightly so graphs move.</summary>
public sealed class ReplayWifiSource(IReadOnlyList<WifiNetwork> networks, WifiConnection? connection = null) : IWifiSource
{
    private static readonly Guid Id = new("00000000-0000-0000-0000-00000000f1f1");
    private readonly Random _rnd = new(7);

    public IReadOnlyList<WifiAdapter> Adapters() => [new(Id, "Recorded scan (replay)", connection is null ? "Disconnected" : "Connected")];
    public void RequestScan(Guid adapter) { }

    public IReadOnlyList<WifiNetwork> GetNetworks(Guid adapter) => networks.Select(n =>
    {
        var copy = (WifiNetwork)n.MemberwiseCloneHelper();
        copy.Rssi = Math.Clamp(n.Rssi + _rnd.Next(-3, 4), -95, -20);
        copy.LastSeen = DateTime.Now;
        return copy;
    }).ToList();

    public WifiConnection? CurrentConnection(Guid adapter) => connection is null ? null : connection with { Rssi = connection.Rssi + _rnd.Next(-2, 3) };
    public void Dispose() { }
}

internal static class WifiNetworkCloning
{
    public static object MemberwiseCloneHelper(this WifiNetwork n) => new WifiNetwork
    {
        Ssid = n.Ssid, Bssid = n.Bssid, Rssi = n.Rssi, LinkQuality = n.LinkQuality, FrequencyMHz = n.FrequencyMHz, Channel = n.Channel,
        Band = n.Band, ChannelWidth = n.ChannelWidth, CoveredChannels = n.CoveredChannels, Security = n.Security, Standard = n.Standard,
        StationCount = n.StationCount, ChannelUtilization = n.ChannelUtilization, Country = n.Country, BeaconIntervalTu = n.BeaconIntervalTu,
        MaxBasicRateMbps = n.MaxBasicRateMbps, LastSeen = n.LastSeen,
    };
}
