using OmlTerminal.Core.Wifi;

namespace OmlTerminal.Core.Tests;

public class LinuxWifiTests
{
    /// <summary>`iw dev wlan0 scan dump` output (iw 6.x): keys indented one tab, sub-items two.</summary>
    private static readonly string IwScan = string.Join("\n",
        "BSS 3c:84:6a:11:22:33(on wlan0) -- associated",
        "\tlast seen: 1234.567s [boottime]",
        "\tfreq: 5180.0",
        "\tbeacon interval: 100 TUs",
        "\tcapability: ESS Privacy SpectrumMgmt RadioMeasure (0x1111)",
        "\tsignal: -48.00 dBm",
        "\tSSID: OML-Office",
        "\tSupported rates: 6.0* 9.0 12.0* 18.0 24.0* 36.0 48.0 54.0 ",
        "\tDS Parameter set: channel 36",
        "\tCountry: IN\tEnvironment: Indoor/Outdoor",
        "\tBSS Load:",
        "\t\t * station count: 7",
        "\t\t * channel utilisation: 51/255",
        "\tHT capabilities:",
        "\t\tCapabilities: 0x9ef",
        "\tHT operation:",
        "\t\t * primary channel: 36",
        "\t\t * secondary channel offset: above",
        "\t\t * STA channel width: any",
        "\tVHT operation:",
        "\t\t * channel width: 1 (80 MHz)",
        "\t\t * center freq segment 1: 42",
        "\t\t * center freq segment 2: 0",
        "\tRSN:\t * Version: 1",
        "\t\t * Group cipher: CCMP",
        "\t\t * Pairwise ciphers: CCMP",
        "\t\t * Authentication suites: PSK SAE",
        "\tHE capabilities:",
        "\t\tHE MAC Capabilities (0x000801185018):",
        "BSS a0:b1:c2:d3:e4:f5(on wlan0)",
        "\tfreq: 2437",
        "\tcapability: ESS (0x0401)",
        "\tsignal: -71.00 dBm",
        "\tSSID: Cafe\\x20Guest",
        "\tSupported rates: 1.0* 2.0* 5.5* 11.0* ",
        "\tDS Parameter set: channel 6",
        "BSS 11:22:33:44:55:66(on wlan0)",
        "\tfreq: 2412",
        "\tcapability: ESS Privacy (0x0411)",
        "\tsignal: -80.00 dBm",
        "\tSSID: ",
        "\tRSN:\t * Version: 1",
        "\t\t * Authentication suites: IEEE 802.1X",
        "\tWPA:\t * Version: 1");

    [Fact]
    public void Parses_iw_scan_dump()
    {
        var nets = LinuxWifiSource.ParseIwScan(IwScan);
        Assert.Equal(3, nets.Count);

        var office = nets[0];
        Assert.Equal(("OML-Office", "3c:84:6a:11:22:33", -48, 36, WifiBand.Band5), (office.Ssid, office.Bssid, office.Rssi, office.Channel, office.Band));
        Assert.Equal((80, "Wi-Fi 6 (ax)", "WPA2/WPA3-Personal"), (office.ChannelWidth, office.Standard, office.Security));
        Assert.Equal([36, 40, 44, 48], office.CoveredChannels);
        Assert.Equal((7, 20, "IN", 100), (office.StationCount, office.ChannelUtilization, office.Country, office.BeaconIntervalTu));

        var cafe = nets[1];
        Assert.Equal(("Cafe Guest", 6, 20, "Open", "802.11b"), (cafe.Ssid, cafe.Channel, cafe.ChannelWidth, cafe.Security, cafe.Standard));

        var hidden = nets[2];
        Assert.True(hidden.Hidden);
        Assert.Equal("WPA/WPA2-Enterprise", hidden.Security);
    }

    [Fact]
    public void Parses_nmcli_terse_list()
    {
        // nmcli -t -e yes: fields split on ':', literal colons escaped as '\:'.
        string text = string.Join("\n",
            @"3C\:84\:6A\:11\:22\:33:OML-Office:36:5180 MHz:540 Mbit/s:88:WPA2 WPA3:80 MHz",
            @"A0\:B1\:C2\:D3\:E4\:F5:Cafe\:Guest:6:2437 MHz:54 Mbit/s:45:--:20 MHz");
        var nets = LinuxWifiSource.ParseNmcli(text);
        Assert.Equal(2, nets.Count);
        Assert.Equal(("3c:84:6a:11:22:33", "OML-Office", 36, -56, 80, "WPA2/WPA3-Personal"),
            (nets[0].Bssid, nets[0].Ssid, nets[0].Channel, nets[0].Rssi, nets[0].ChannelWidth, nets[0].Security));
        Assert.Equal(("Cafe:Guest", "Open", WifiBand.Band2_4), (nets[1].Ssid, nets[1].Security, nets[1].Band));
    }

    [Fact]
    public void Parses_iw_link()
    {
        string link = string.Join("\n",
            "Connected to 3c:84:6a:11:22:33 (on wlp2s0)",
            "\tSSID: OML-Office",
            "\tfreq: 5180.0",
            "\tRX: 1234 bytes (10 packets)",
            "\tsignal: -52 dBm",
            "\trx bitrate: 866.7 MBit/s VHT-MCS 9 80MHz short GI VHT-NSS 2",
            "\ttx bitrate: 650.0 MBit/s");
        var c = LinuxWifiSource.ParseIwLink(link)!;
        Assert.Equal(("OML-Office", "3c:84:6a:11:22:33", -52, 866, 650), (c.Ssid, c.Bssid, c.Rssi, c.RxRateMbps, c.TxRateMbps));
        Assert.Null(LinuxWifiSource.ParseIwLink("Not connected."));
    }

    [Fact]
    public void Adapter_ids_are_stable_per_interface_name()
    {
        Assert.Equal(LinuxWifiSource.IdFor("wlan0"), LinuxWifiSource.IdFor("wlan0"));
        Assert.NotEqual(LinuxWifiSource.IdFor("wlan0"), LinuxWifiSource.IdFor("wlan1"));
    }
}
