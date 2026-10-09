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

public class LinuxLocalPortsTests
{
    private const string Tcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0100007F:0035 00000000:0000 0A 00000000:00000000 00:00000000 00000000   101        0 23456 1 0000000000000000 100 0 0 10 0
           1: 4F01A8C0:0016 0401A8C0:D2F4 01 00000000:00000000 02:00098D5B 00000000     0        0 98765 4 0000000000000000 20 4 30 10 -1
        """;

    [Fact]
    public void Parses_ipv4_tcp_with_owners()
    {
        var owners = new Dictionary<long, (int, string)> { [98765] = (4242, "sshd") };
        var rows = OmlTerminal.Core.NetTools.LocalPorts.ParseProcNet(Tcp, "TCP", owners).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("127.0.0.1", 53, "LISTEN", "127.0.0.1:53", 0), (rows[0].LocalAddress, rows[0].LocalPort, rows[0].State, rows[0].Local, rows[0].RemotePort));
        Assert.Equal("?", rows[0].ProcessName); // owner not visible (another user's process without root)
        Assert.Equal(("192.168.1.79:22", "192.168.1.4:54004", "ESTABLISHED", 4242, "sshd"),
            (rows[1].Local, rows[1].Remote, rows[1].State, rows[1].Pid, rows[1].ProcessName));
    }

    [Fact]
    public void Parses_ipv6_and_mapped_addresses()
    {
        const string tcp6 = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000000000000000000000000000:0050 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 111 1
               1: 00000000000000000000000001000000:0277 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 112 1
               2: 0000000000000000FFFF00004F01A8C0:01BB 0000000000000000FFFF00000401A8C0:C350 01 00000000:00000000 00:00000000 00000000     0        0 113 1
            """;
        var rows = OmlTerminal.Core.NetTools.LocalPorts.ParseProcNet(tcp6, "TCPv6").ToList();
        Assert.Equal("[::]:80", rows[0].Local);
        Assert.Equal("[::1]:631", rows[1].Local);
        Assert.Equal(("192.168.1.79:443", "192.168.1.4:50000"), (rows[2].Local, rows[2].Remote));
    }

    [Fact]
    public void Udp_rows_have_no_peer()
    {
        const string udp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
             100: 00000000:0044 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 555 2 0000000000000000 0
            """;
        var row = Assert.Single(OmlTerminal.Core.NetTools.LocalPorts.ParseProcNet(udp, "UDP"));
        Assert.Equal(("0.0.0.0:68", "*:*", ""), (row.Local, row.Remote, row.State));
    }

    [Fact]
    public void Live_snapshot_on_linux_includes_a_listener_we_open()
    {
        if (!OperatingSystem.IsLinux()) return;
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try
        {
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            var mine = OmlTerminal.Core.NetTools.LocalPorts.Snapshot().FirstOrDefault(e => e.LocalPort == port && e.State == "LISTEN");
            Assert.NotNull(mine);
            Assert.Equal(Environment.ProcessId, mine!.Pid);
        }
        finally { l.Stop(); }
    }
}
