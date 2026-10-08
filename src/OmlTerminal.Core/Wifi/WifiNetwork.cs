using System.Buffers.Binary;
using System.Text;

namespace OmlTerminal.Core.Wifi;

public enum WifiBand { Band2_4, Band5, Band6 }

/// <summary>One access point radio (BSSID) as seen in a scan.</summary>
public sealed class WifiNetwork
{
    public string Ssid { get; set; } = "";
    public string Bssid { get; set; } = "";
    public int Rssi { get; set; }
    public int LinkQuality { get; set; }
    public int FrequencyMHz { get; set; }
    public int Channel { get; set; }
    public WifiBand Band { get; set; }
    /// <summary>20, 40, 80, 160 or 320 MHz.</summary>
    public int ChannelWidth { get; set; } = 20;
    /// <summary>Primary channel plus any bonded channels the radio occupies (e.g. 36-48 for an 80 MHz radio on 36).</summary>
    public IReadOnlyList<int> CoveredChannels { get; set; } = [];
    public string Security { get; set; } = "Open";
    /// <summary>"Wi-Fi 7 (be)", "Wi-Fi 6 (ax)", "Wi-Fi 6E (ax)", "Wi-Fi 5 (ac)", "Wi-Fi 4 (n)", "802.11a/g", "802.11b".</summary>
    public string Standard { get; set; } = "";
    public int? StationCount { get; set; }
    /// <summary>0-100 % of airtime the AP reports busy (802.11 BSS Load element), when advertised.</summary>
    public int? ChannelUtilization { get; set; }
    public string? Country { get; set; }
    public int BeaconIntervalTu { get; set; }
    public double MaxBasicRateMbps { get; set; }
    public bool Hidden => Ssid.Length == 0;
    public bool LocallyAdministeredBssid => Bssid.Length >= 2 && (Convert.ToByte(Bssid[..2], 16) & 0x02) != 0;
    public DateTime LastSeen { get; set; } = DateTime.Now;

    public string DisplaySsid => Hidden ? "(hidden network)" : Ssid;
    public string BandText => Band switch { WifiBand.Band2_4 => "2.4 GHz", WifiBand.Band5 => "5 GHz", _ => "6 GHz" };
}

public static class WifiMath
{
    public static (int Channel, WifiBand Band) ChannelFor(int mhz) => mhz switch
    {
        2484 => (14, WifiBand.Band2_4),
        >= 2412 and < 2484 => ((mhz - 2407) / 5, WifiBand.Band2_4),
        >= 5955 and <= 7115 => ((mhz - 5950) / 5, WifiBand.Band6),
        >= 4910 and < 5955 => ((mhz - 5000) / 5, WifiBand.Band5),
        _ => (0, WifiBand.Band5),
    };

    /// <summary>Signal quality as people describe it, using the usual thresholds for data and voice.</summary>
    public static string SignalWord(int rssi) => rssi switch
    {
        >= -50 => "Excellent",
        >= -60 => "Very good",
        >= -67 => "Good",
        >= -70 => "Fair",
        >= -80 => "Weak",
        _ => "Unusable",
    };

    public static readonly int[] Channels2_4 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
    public static readonly int[] Channels5 = [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165];
    public static readonly int[] Channels6 = Enumerable.Range(0, 59).Select(i => 1 + i * 4).ToArray();

    /// <summary>DFS channels may be vacated at any moment when radar is detected - worth avoiding for critical WLANs.</summary>
    public static bool IsDfs(int channel) => channel is >= 52 and <= 144;

    /// <summary>The 20 MHz channels a radio occupies given its primary channel, width and band. 5/6 GHz bonding follows
    /// the fixed channel blocks (e.g. 80 MHz: 36-48, 52-64...); 2.4 GHz 40 MHz uses the HT secondary offset.</summary>
    public static IReadOnlyList<int> Covered(int primary, int width, WifiBand band, int secondaryOffset = 0)
    {
        if (width <= 20 || primary == 0) return [primary];
        if (band == WifiBand.Band2_4)
            return secondaryOffset < 0 ? [primary - 4, primary] : [primary, primary + 4];
        int blocks = width / 20;
        int origin = band == WifiBand.Band6 ? 1 : 36;
        if (band == WifiBand.Band5 && primary >= 149) origin = 149;
        else if (band == WifiBand.Band5 && primary >= 100) origin = 100;
        int start = origin + (primary - origin) / (blocks * 4) * blocks * 4;
        return Enumerable.Range(0, blocks).Select(i => start + i * 4).ToList();
    }
}

/// <summary>Reads the Information Elements an access point broadcasts in its beacons: security (RSN/WPA), the
/// Wi-Fi generation (HT/VHT/HE/EHT capabilities), channel width, client count and airtime use (BSS Load), country.</summary>
public static class InformationElements
{
    public static void Apply(WifiNetwork n, ReadOnlySpan<byte> ies, bool privacyBit)
    {
        bool ht = false, vht = false, he = false, eht = false, wpa = false;
        string? rsn = null;
        int secondaryOffset = 0, width = 20;
        int i = 0;
        while (i + 2 <= ies.Length)
        {
            int id = ies[i], len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            var v = ies.Slice(i + 2, len);
            switch (id)
            {
                case 0 when n.Ssid.Length == 0 && len > 0 && v.IndexOfAnyExcept((byte)0) >= 0:
                    n.Ssid = Encoding.UTF8.GetString(v);
                    break;
                case 3 when len >= 1 && n.Channel == 0:
                    n.Channel = v[0];
                    break;
                case 7 when len >= 2:
                    n.Country = Encoding.ASCII.GetString(v[..2]);
                    break;
                case 11 when len >= 5:
                    n.StationCount = BinaryPrimitives.ReadUInt16LittleEndian(v);
                    n.ChannelUtilization = (int)Math.Round(v[2] * 100 / 255.0);
                    break;
                case 45:
                    ht = true;
                    break;
                case 61 when len >= 2:
                    ht = true;
                    int sco = v[1] & 0x03;
                    if ((v[1] & 0x04) != 0 && sco is 1 or 3) { width = Math.Max(width, 40); secondaryOffset = sco == 1 ? 1 : -1; }
                    break;
                case 191:
                    vht = true;
                    break;
                case 192 when len >= 3:
                    vht = true;
                    if (v[0] == 1) width = Math.Max(width, v[2] != 0 && Math.Abs(v[2] - v[1]) == 8 ? 160 : 80);
                    else if (v[0] is 2 or 3) width = Math.Max(width, 160);
                    break;
                case 48 when len >= 2:
                    rsn = RsnSecurity(v);
                    break;
                case 221 when len >= 4 && v[0] == 0x00 && v[1] == 0x50 && v[2] == 0xf2 && v[3] == 0x01:
                    wpa = true;
                    break;
                case 255 when len >= 1:
                    if (v[0] == 35) he = true;
                    else if (v[0] == 108) eht = true;
                    else if (v[0] == 36 && len >= 7) width = Math.Max(width, HeOperationWidth(v[1..]));
                    else if (v[0] == 106 && len >= 6) width = Math.Max(width, EhtOperationWidth(v[1..]));
                    break;
            }
            i += 2 + len;
        }
        n.ChannelWidth = width;
        n.Security = rsn is not null ? (wpa && rsn.StartsWith("WPA2") ? "WPA/" + rsn : rsn) : wpa ? "WPA" : privacyBit ? "WEP" : "Open";
        n.Standard = eht ? "Wi-Fi 7 (be)"
            : he ? (n.Band == WifiBand.Band6 ? "Wi-Fi 6E (ax)" : "Wi-Fi 6 (ax)")
            : vht ? "Wi-Fi 5 (ac)"
            : ht ? "Wi-Fi 4 (n)"
            : n.Band == WifiBand.Band2_4 && n.MaxBasicRateMbps <= 11 ? "802.11b"
            : n.Band == WifiBand.Band2_4 ? "802.11g" : "802.11a";
        n.CoveredChannels = WifiMath.Covered(n.Channel, n.ChannelWidth, n.Band, secondaryOffset);
    }

    /// <summary>The AKM suites in an RSN element mapped to the names on a router's settings page.</summary>
    public static string RsnSecurity(ReadOnlySpan<byte> v)
    {
        int i = 2 + 4; // version + group cipher
        if (v.Length < i + 2) return "WPA2";
        int pairwise = BinaryPrimitives.ReadUInt16LittleEndian(v[i..]);
        i += 2 + pairwise * 4;
        if (v.Length < i + 2) return "WPA2";
        int akmCount = BinaryPrimitives.ReadUInt16LittleEndian(v[i..]);
        i += 2;
        var akms = new HashSet<int>();
        for (int k = 0; k < akmCount && i + 4 <= v.Length; k++, i += 4)
            if (v[i] == 0x00 && v[i + 1] == 0x0f && v[i + 2] == 0xac) akms.Add(v[i + 3]);
        bool psk = akms.Contains(2) || akms.Contains(6), sae = akms.Contains(8) || akms.Contains(24);
        bool ent = akms.Contains(1) || akms.Contains(5), suiteB = akms.Contains(12) || akms.Contains(13);
        if (akms.Contains(18)) return "OWE (Enhanced Open)";
        if (suiteB) return "WPA3-Enterprise 192-bit";
        if (sae && psk) return "WPA2/WPA3-Personal";
        if (sae) return "WPA3-Personal";
        if (ent && akms.Contains(5) && !akms.Contains(1)) return "WPA3-Enterprise";
        if (ent) return "WPA2-Enterprise";
        if (psk) return "WPA2-Personal";
        return "WPA2";
    }

    /// <summary>6 GHz Operation Information inside the HE Operation element carries the channel width.</summary>
    private static int HeOperationWidth(ReadOnlySpan<byte> v)
    {
        // HE Operation Parameters (3) + BSS Color (1) + Basic MCS (2), then optional fields flagged in the parameters.
        if (v.Length < 6) return 20;
        uint parms = (uint)(v[0] | v[1] << 8 | v[2] << 16);
        bool vhtInfo = (parms & (1 << 14)) != 0, coHosted = (parms & (1 << 15)) != 0, sixGhz = (parms & (1 << 17)) != 0;
        int i = 6 + (vhtInfo ? 3 : 0) + (coHosted ? 1 : 0);
        if (!sixGhz || i + 2 > v.Length) return 20;
        return (v[i + 1] & 0x03) switch { 1 => 40, 2 => 80, 3 => 160, _ => 20 };
    }

    private static int EhtOperationWidth(ReadOnlySpan<byte> v)
    {
        if (v.Length < 6 || (v[0] & 0x01) == 0) return 20;
        return (v[5] & 0x07) switch { 1 => 40, 2 => 80, 3 => 160, 4 => 320, _ => 20 };
    }
}
