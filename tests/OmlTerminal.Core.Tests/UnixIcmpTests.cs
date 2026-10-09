using System.Net;
using System.Net.NetworkInformation;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Tests;

public class UnixIcmpTests
{
    private static byte[] Ipv4Header(byte protocolTtl, string src, string dst, int payloadLength)
    {
        var h = new byte[20];
        h[0] = 0x45;
        h[2] = (byte)((20 + payloadLength) >> 8); h[3] = (byte)(20 + payloadLength);
        h[8] = protocolTtl; h[9] = 1;
        IPAddress.Parse(src).GetAddressBytes().CopyTo(h, 12);
        IPAddress.Parse(dst).GetAddressBytes().CopyTo(h, 16);
        return h;
    }

    [Fact]
    public void Echo_request_has_a_valid_checksum()
    {
        var p = UnixIcmp.BuildEcho(0x1234, 0x0102, 32);
        Assert.Equal(40, p.Length);
        Assert.Equal((8, 0x12, 0x34, 0x01, 0x02), (p[0], p[4], p[5], p[6], p[7]));
        Assert.Equal(0, UnixIcmp.Checksum(p)); // a correct checksum makes the whole message sum to zero
    }

    [Fact]
    public void Parses_echo_reply_with_and_without_ip_header()
    {
        var reply = UnixIcmp.BuildEcho(0x1234, 7, 8);
        reply[0] = 0; // echo reply
        var bare = UnixIcmp.Parse(reply);                                // Linux ping socket: ICMP only
        Assert.Equal((0, (ushort)0x1234, (ushort)7, 0), (bare!.Value.Type, bare.Value.Id, bare.Value.Seq, bare.Value.Ttl));

        var withIp = Ipv4Header(57, "1.1.1.1", "10.0.0.2", reply.Length).Concat(reply).ToArray(); // raw socket / macOS
        var parsed = UnixIcmp.Parse(withIp)!.Value;
        Assert.Equal((0, (ushort)7, 57), (parsed.Type, parsed.Seq, parsed.Ttl));
    }

    [Fact]
    public void Time_exceeded_is_matched_through_the_quoted_echo_header()
    {
        var original = UnixIcmp.BuildEcho(0x4242, 99, 32);
        var quoted = Ipv4Header(1, "10.0.0.2", "8.8.8.8", original.Length).Concat(original.Take(8)).ToArray();
        var icmp = new byte[] { 11, 0, 0, 0, 0, 0, 0, 0 }.Concat(quoted).ToArray();
        var packet = Ipv4Header(254, "192.168.1.1", "10.0.0.2", icmp.Length).Concat(icmp).ToArray();

        var m = UnixIcmp.Parse(packet)!.Value;
        Assert.Equal(11, m.Type);
        Assert.Equal((ushort)0x4242, m.Id);
        Assert.Equal((ushort)99, m.Seq);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), m.QuotedDestination);
        Assert.Equal(IPStatus.TtlExpired, UnixIcmp.StatusFor(m.Type, m.Code));
    }

    [Fact]
    public void Ignores_other_icmp_traffic()
    {
        Assert.Null(UnixIcmp.Parse(UnixIcmp.BuildEcho(1, 1, 8)));      // someone's echo request
        Assert.Null(UnixIcmp.Parse(new byte[] { 0, 0, 0 }));             // truncated
        var notOurs = new byte[] { 3, 3, 0, 0, 0, 0, 0, 0 }
            .Concat(Ipv4Header(64, "10.0.0.2", "8.8.8.8", 8)).Concat(new byte[] { 17, 0, 0, 53, 0, 8, 0, 0 }).ToArray(); // quotes a UDP packet
        notOurs[8 + 9] = 17;
        Assert.Null(UnixIcmp.Parse(notOurs));
    }

    [Theory]
    [InlineData(3, 0, IPStatus.DestinationNetworkUnreachable)]
    [InlineData(3, 1, IPStatus.DestinationHostUnreachable)]
    [InlineData(3, 3, IPStatus.DestinationPortUnreachable)]
    [InlineData(3, 13, IPStatus.DestinationProhibited)]
    [InlineData(0, 0, IPStatus.Success)]
    public void Maps_icmp_types_to_ip_status(int type, int code, IPStatus expected) =>
        Assert.Equal(expected, UnixIcmp.StatusFor(type, code));

    [Fact]
    public void Reads_linux_and_macos_ping_output()
    {
        var ok = UnixIcmp.PingCommand.ParseOutput("PING 1.1.1.1 (1.1.1.1) 32(60) bytes of data.\n40 bytes from 1.1.1.1: icmp_seq=1 ttl=57 time=3.42 ms\n", 50);
        Assert.Equal((IPStatus.Success, 3.42, 57), (ok.Status, ok.RttMs, ok.Ttl));
        Assert.Equal(IPAddress.Parse("1.1.1.1"), ok.From);

        var linuxTtl = UnixIcmp.PingCommand.ParseOutput("PING 8.8.8.8 (8.8.8.8) 32(60) bytes of data.\nFrom 192.168.1.1 icmp_seq=1 Time to live exceeded\n", 12.5);
        Assert.Equal((IPStatus.TtlExpired, 12.5), (linuxTtl.Status, linuxTtl.RttMs));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), linuxTtl.From);

        var macTtl = UnixIcmp.PingCommand.ParseOutput("PING 8.8.8.8 (8.8.8.8): 32 data bytes\n36 bytes from 10.1.1.1: Time to live exceeded\nVr HL TOS ...\n", 9);
        Assert.Equal(IPStatus.TtlExpired, macTtl.Status);
        Assert.Equal(IPAddress.Parse("10.1.1.1"), macTtl.From);

        var unreachable = UnixIcmp.PingCommand.ParseOutput("From 10.0.0.1 icmp_seq=1 Destination Host Unreachable\n", 3000);
        Assert.Equal(IPStatus.DestinationHostUnreachable, unreachable.Status);

        Assert.Equal(IPStatus.TimedOut, UnixIcmp.PingCommand.ParseOutput("PING 10.9.9.9 (10.9.9.9) 32(60) bytes of data.\n\n--- 10.9.9.9 ping statistics ---\n1 packets transmitted, 0 received, 100% packet loss\n", 1000).Status);
    }

    [Fact]
    public async Task Loopback_ping_works_without_root()
    {
        if (OperatingSystem.IsWindows()) return; // Windows uses IcmpSendEcho2 (NativeIcmp)
        var r = await UnixIcmp.SendAsync(IPAddress.Loopback, 64, 2000, 32);
        Assert.Equal(IPStatus.Success, r.Status);
        Assert.Equal(IPAddress.Loopback, r.From);
        Assert.InRange(r.RttMs, 0, 2000);
    }

    [Fact]
    public async Task Many_concurrent_probes_share_one_socket()
    {
        if (OperatingSystem.IsWindows()) return;
        var replies = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => UnixIcmp.SendAsync(IPAddress.Loopback, 64, 3000, 16)));
        Assert.All(replies, r => Assert.Equal(IPStatus.Success, r.Status));
    }
}
