using System.Net;
using OmlTerminal.Core.Capture;

namespace OmlTerminal.Core.Tests;

public class LinuxCaptureTests
{
    /// <summary>Real libpcap capture on loopback - runs only where libpcap is installed and capturing is permitted
    /// (root or cap_net_raw), which the Linux test machine provides.</summary>
    [Fact]
    public async Task Captures_loopback_traffic_through_libpcap()
    {
        if (OperatingSystem.IsWindows() || !CaptureEngine.PcapAvailable) return;
        var lo = CaptureEngine.ListInterfaces().FirstOrDefault(i => i.Name == "Loopback");
        Assert.NotNull(lo);

        using var cap = CaptureEngine.Open(lo!, "udp port 39999", promiscuous: false, monitorMode: false);
        var frames = new List<RawFrame>();
        cap.FrameArrived += f => { lock (frames) frames.Add(f); };
        try { cap.Start(); }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Permission denied")) { return; } // not privileged here

        using (var udp = new System.Net.Sockets.UdpClient())
            for (int i = 0; i < 5; i++) { await udp.SendAsync(new byte[] { 1, 2, 3, (byte)i }, new IPEndPoint(IPAddress.Loopback, 39999)); await Task.Delay(50); }
        for (int i = 0; i < 40 && frames.Count < 5; i++) await Task.Delay(50);
        cap.Stop();

        Assert.True(frames.Count >= 5, $"captured {frames.Count} frames");
        var f = frames[0];
        Assert.InRange((DateTime.Now - f.Timestamp).TotalSeconds, -5, 30); // the header's 64-bit timeval was read correctly
        Assert.Equal(f.Data.Length, f.OriginalLength);
        var decoded = PacketDecoder.Decode(f.Data, f.LinkType, 1, f.Timestamp, f.OriginalLength);
        Assert.Equal(("127.0.0.1", "UDP"), (decoded.DstIp, decoded.Protocol));
        Assert.Contains("39999", decoded.Info);
    }
}
