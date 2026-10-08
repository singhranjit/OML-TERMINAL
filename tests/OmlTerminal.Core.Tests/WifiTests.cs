using OmlTerminal.Core.Wifi;

namespace OmlTerminal.Core.Tests;

public class WifiTests
{
    private static byte[] Ie(int id, params byte[] v) => [(byte)id, (byte)v.Length, .. v];

    private static byte[] Rsn(params byte[] akms)
    {
        var l = new List<byte> { 1, 0, 0x00, 0x0f, 0xac, 4, 1, 0, 0x00, 0x0f, 0xac, 4, (byte)akms.Length, 0 };
        foreach (var a in akms) l.AddRange([0x00, 0x0f, 0xac, a]);
        l.AddRange([0, 0]);
        return l.ToArray();
    }

    private static WifiNetwork Parse(int mhz, byte[] ies, bool privacy = true)
    {
        var (ch, band) = WifiMath.ChannelFor(mhz);
        var n = new WifiNetwork { FrequencyMHz = mhz, Channel = ch, Band = band, MaxBasicRateMbps = 54 };
        InformationElements.Apply(n, ies, privacy);
        return n;
    }

    [Theory]
    [InlineData(2412, 1, WifiBand.Band2_4)]
    [InlineData(2437, 6, WifiBand.Band2_4)]
    [InlineData(2484, 14, WifiBand.Band2_4)]
    [InlineData(5180, 36, WifiBand.Band5)]
    [InlineData(5745, 149, WifiBand.Band5)]
    [InlineData(5955, 1, WifiBand.Band6)]
    [InlineData(6115, 33, WifiBand.Band6)]
    public void MapsFrequencyToChannel(int mhz, int channel, WifiBand band) => Assert.Equal((channel, band), WifiMath.ChannelFor(mhz));

    [Theory]
    [InlineData(new byte[] { 2 }, "WPA2-Personal")]
    [InlineData(new byte[] { 8 }, "WPA3-Personal")]
    [InlineData(new byte[] { 2, 8 }, "WPA2/WPA3-Personal")]
    [InlineData(new byte[] { 1 }, "WPA2-Enterprise")]
    [InlineData(new byte[] { 12 }, "WPA3-Enterprise 192-bit")]
    [InlineData(new byte[] { 18 }, "OWE (Enhanced Open)")]
    public void ReadsSecurityFromRsn(byte[] akms, string expected) => Assert.Equal(expected, InformationElements.RsnSecurity(Rsn(akms)));

    [Fact]
    public void OpenAndWepWithoutRsn()
    {
        Assert.Equal("Open", Parse(2437, Ie(0, (byte)'x'), privacy: false).Security);
        Assert.Equal("WEP", Parse(2437, Ie(0, (byte)'x'), privacy: true).Security);
    }

    [Fact]
    public void Wifi5At80MhzWithClientLoad()
    {
        var n = Parse(5180, [.. Ie(0, "Corp"u8.ToArray()), .. Ie(48, Rsn(1)), .. Ie(45, new byte[26]), .. Ie(191, new byte[12]),
            .. Ie(192, 1, 42, 0), .. Ie(11, 17, 0, 128, 0, 0)]);
        Assert.Equal("Corp", n.Ssid);
        Assert.Equal("Wi-Fi 5 (ac)", n.Standard);
        Assert.Equal(80, n.ChannelWidth);
        Assert.Equal(new[] { 36, 40, 44, 48 }, n.CoveredChannels);
        Assert.Equal(17, n.StationCount);
        Assert.Equal(50, n.ChannelUtilization);
        Assert.Equal("WPA2-Enterprise", n.Security);
    }

    [Fact]
    public void Wifi6EAndWifi7()
    {
        Assert.Equal("Wi-Fi 6E (ax)", Parse(6115, [.. Ie(255, 35, 0, 0)]).Standard);
        Assert.Equal("Wi-Fi 6 (ax)", Parse(5180, [.. Ie(255, 35, 0, 0)]).Standard);
        Assert.Equal("Wi-Fi 7 (be)", Parse(6115, [.. Ie(255, 35, 0), .. Ie(255, 108, 0)]).Standard);
    }

    [Fact]
    public void TwoPointFourGhz40MhzCoversTheSecondaryChannel()
    {
        var n = Parse(2412, [.. Ie(45, new byte[26]), .. Ie(61, 1, 0x05, 0, 0, 0)]);
        Assert.Equal(40, n.ChannelWidth);
        Assert.Equal(new[] { 1, 5 }, n.CoveredChannels);
        Assert.Contains(ChannelPlanner.Warnings([n]), w => w.Contains("40 MHz"));
    }

    private static WifiNetwork Net(string ssid, int ch, int rssi, WifiBand band = WifiBand.Band2_4, string sec = "WPA2-Personal") =>
        new() { Ssid = ssid, Bssid = $"00:11:22:33:{ch:x2}:{-rssi:x2}", Channel = ch, Band = band, Rssi = rssi, CoveredChannels = [ch], Security = sec };

    [Fact]
    public void RecommendsTheQuietestNonOverlappingChannel()
    {
        var nets = new[] { Net("a", 1, -45), Net("b", 1, -60), Net("c", 6, -50), Net("d", 11, -85), Net("e", 3, -70, sec: "Open") };
        var advice = ChannelPlanner.Advise(nets, WifiBand.Band2_4);
        Assert.Equal(11, advice.BestChannel);
        var warnings = ChannelPlanner.Warnings(nets);
        Assert.Contains(warnings, w => w.Contains("overlapping channels (3)"));
        Assert.Contains(warnings, w => w.Contains("weak or no security"));
    }

    [Fact]
    public void FiveGhzAdviceAvoidsDfsUnlessAllowed()
    {
        var nets = WifiMath.Channels5.Where(c => !WifiMath.IsDfs(c)).Select(c => Net($"n{c}", c, -60, WifiBand.Band5)).ToArray();
        Assert.False(WifiMath.IsDfs(ChannelPlanner.Advise(nets, WifiBand.Band5).BestChannel));
        Assert.True(WifiMath.IsDfs(ChannelPlanner.Advise(nets, WifiBand.Band5, allowDfs: true).BestChannel));
    }

    [Fact]
    public void RoamTrackerLogsRoamsAndDrops()
    {
        var t = new RoamTracker();
        var at = new DateTime(2026, 10, 8, 9, 0, 0);
        t.Update(new WifiConnection("Corp", "aa:aa:aa:aa:aa:01", -55, 90, 866, 866, "Corp"), at);
        t.Update(new WifiConnection("Corp", "aa:aa:aa:aa:aa:02", -60, 80, 866, 866, "Corp"), at.AddSeconds(5));
        t.Update(null, at.AddSeconds(9));
        Assert.Equal(3, t.Events.Count);
        Assert.Contains("Roamed aa:aa:aa:aa:aa:01 (-55 dBm) → aa:aa:aa:aa:aa:02", t.Events[1]);
        Assert.Contains("Disconnected", t.Events[0]);
    }
}

public class SiteSurveyTests
{
    private static SurveyPoint Point(double x, double y, params (string Ssid, string Bssid, int Rssi, int Ch)[] r) =>
        new() { X = x, Y = y, Readings = r.Select(v => new SurveyReading(v.Ssid, v.Bssid, v.Rssi, v.Ch, WifiBand.Band5)).ToList() };

    [Fact]
    public void MetricsPerPoint()
    {
        var p = Point(0.5, 0.5, ("Corp", "ap1", -52, 36), ("Corp", "ap2", -64, 44), ("Guest", "ap3", -70, 36), ("Other", "x", -88, 36));
        Assert.Equal(-52, Heatmap.Value(p, HeatmapMetric.Signal, "Corp"));
        Assert.Equal(-64, Heatmap.Value(p, HeatmapMetric.SecondaryCoverage, "Corp"));
        Assert.Equal(3, Heatmap.Value(p, HeatmapMetric.NetworksHeard, null));
        Assert.Equal(18, Heatmap.Value(p, HeatmapMetric.SignalToInterference, "Corp"));
        Assert.Null(Heatmap.Value(p, HeatmapMetric.Signal, "Missing"));
    }

    [Fact]
    public void InterpolationHonoursSamplesAndLeavesUnwalkedAreasBlank()
    {
        var grid = Heatmap.Interpolate([(0.1, 0.1, -40), (0.3, 0.1, -80)], 50, 50, reach: 0.2);
        Assert.InRange(grid[5, 5], -45, -40);
        Assert.InRange(grid[5, 15], -80, -75);
        Assert.InRange(grid[5, 10], -70, -50);
        Assert.True(double.IsNaN(grid[45, 45]));
        Assert.Equal(0u, Heatmap.Color(double.NaN, HeatmapMetric.Signal));
        Assert.NotEqual(Heatmap.Color(-45, HeatmapMetric.Signal), Heatmap.Color(-85, HeatmapMetric.Signal));
        Assert.InRange(Heatmap.Coverage(grid, -67), 1, 99);
    }

    [Fact]
    public void SurveySavesAndLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"survey-{Guid.NewGuid():N}.omlsurvey");
        try
        {
            var s = new SurveyProject { Name = "HQ L2", FloorPlanBase64 = "AAA=", WidthMeters = 40 };
            s.Points.Add(Point(0.2, 0.3, ("Corp", "ap1", -55, 36)));
            s.Save(path);
            var back = SurveyProject.Load(path);
            Assert.Equal("HQ L2", back.Name);
            Assert.Equal(-55, back.Points[0].Readings[0].Rssi);
            Assert.Equal(new[] { "Corp" }, back.Ssids);
            Assert.Contains("Corp", back.ToCsv());
        }
        finally { File.Delete(path); }
    }
}
