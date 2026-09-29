using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Services;

/// <summary>DHCP message encode/decode (RFC 2131/2132) - the subset a simple lab server needs.</summary>
public static class Dhcp
{
    public const int ServerPort = 67, ClientPort = 68;
    private const uint MagicCookie = 0x63825363;

    public enum MessageType : byte { Discover = 1, Offer = 2, Request = 3, Decline = 4, Ack = 5, Nak = 6, Release = 7, Inform = 8 }

    public enum Opt : byte
    {
        SubnetMask = 1, Router = 3, DnsServer = 6, HostName = 12, DomainName = 15, RequestedIp = 50, LeaseTime = 51,
        MessageType = 53, ServerId = 54, ParameterList = 55, RenewalTime = 58, RebindTime = 59, End = 255,
    }

    public sealed class Message
    {
        public byte Op = 1;                 // 1 request, 2 reply
        public uint Xid;
        public ushort Flags;
        public IPAddress CiAddr = IPAddress.Any;
        public IPAddress YiAddr = IPAddress.Any;   // "your" (assigned) address
        public IPAddress SiAddr = IPAddress.Any;   // next server
        public IPAddress GiAddr = IPAddress.Any;   // relay agent
        public byte[] ChAddr = new byte[16];       // client hardware address (first HLen bytes)
        public byte HLen = 6;
        public Dictionary<Opt, byte[]> Options = new();

        public MessageType? Type =>
            Options.TryGetValue(Opt.MessageType, out var v) && v.Length > 0 ? (MessageType)v[0] : null;

        public string MacString => string.Join(":", ChAddr.Take(HLen).Select(b => b.ToString("x2")));

        public bool Broadcast => (Flags & 0x8000) != 0;
    }

    public static Message Parse(byte[] data)
    {
        var m = new Message { Op = data[0], HLen = data[2] };
        m.Xid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        m.Flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        m.CiAddr = new IPAddress(data.AsSpan(12, 4).ToArray());
        m.YiAddr = new IPAddress(data.AsSpan(16, 4).ToArray());
        m.GiAddr = new IPAddress(data.AsSpan(24, 4).ToArray());
        Array.Copy(data, 28, m.ChAddr, 0, 16);
        if (data.Length > 240 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(236)) == MagicCookie)
        {
            int i = 240;
            while (i < data.Length)
            {
                var opt = (Opt)data[i++];
                if (opt == Opt.End) break;
                if (opt == 0) continue; // pad
                int len = data[i++];
                m.Options[opt] = data.AsSpan(i, len).ToArray();
                i += len;
            }
        }
        return m;
    }

    public static byte[] Build(Message m)
    {
        var buf = new byte[300];
        buf[0] = m.Op; buf[1] = 1; buf[2] = m.HLen; buf[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), m.Xid);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10), m.Flags);
        m.CiAddr.GetAddressBytes().CopyTo(buf, 12);
        m.YiAddr.GetAddressBytes().CopyTo(buf, 16);
        m.SiAddr.GetAddressBytes().CopyTo(buf, 20);
        m.GiAddr.GetAddressBytes().CopyTo(buf, 24);
        Array.Copy(m.ChAddr, 0, buf, 28, 16);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(236), MagicCookie);
        int i = 240;
        foreach (var (opt, val) in m.Options)
        {
            buf[i++] = (byte)opt;
            buf[i++] = (byte)val.Length;
            val.CopyTo(buf, i);
            i += val.Length;
        }
        buf[i++] = (byte)Opt.End;
        return buf[..Math.Max(i, 300)];
    }

    public static byte[] Ip(IPAddress ip) => ip.GetAddressBytes();
    public static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    public static byte[] Byte(byte v) => [v];
}

public sealed record DhcpLease(string Mac, IPAddress Ip, DateTime ExpiresUtc, string HostName);

/// <summary>
/// A minimal DHCP server for lab/staging networks (RFC 2131): hands out addresses from a pool with mask, gateway and
/// DNS, remembering leases per MAC. Intended for an isolated segment - running two DHCP servers on one LAN causes
/// chaos, so it warns and is clearly opt-in. Not a replacement for production DHCP.
/// </summary>
public sealed class DhcpServer : NetworkServiceBase
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DhcpLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? _socket;
    private uint _next;

    public IPAddress PoolStart { get; }
    public IPAddress PoolEnd { get; }
    public IPAddress Mask { get; }
    public IPAddress Gateway { get; }
    public IPAddress Dns { get; }
    public IPAddress ServerId { get; }
    public TimeSpan LeaseTime { get; }
    public string BindAddress { get; }

    public override string Name => "DHCP server";

    public IReadOnlyList<DhcpLease> Leases { get { lock (_lock) return _leases.Values.OrderBy(l => Ipv4.ToUInt(l.Ip)).ToList(); } }

    public DhcpServer(string poolStart, string poolEnd, string mask, string gateway, string dns, string serverId, TimeSpan? leaseTime = null, string bindAddress = "0.0.0.0")
    {
        PoolStart = IPAddress.Parse(poolStart);
        PoolEnd = IPAddress.Parse(poolEnd);
        Mask = IPAddress.Parse(mask);
        Gateway = IPAddress.Parse(gateway);
        Dns = IPAddress.Parse(dns);
        ServerId = IPAddress.Parse(serverId);
        LeaseTime = leaseTime ?? TimeSpan.FromHours(8);
        BindAddress = bindAddress;
        _next = Ipv4.ToUInt(PoolStart);
    }

    protected override void OnStart()
    {
        _socket = new UdpClient { EnableBroadcast = true };
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Parse(BindAddress), Dhcp.ServerPort));
        Log(LogLevel.Warning, "", $"Serving {PoolStart}-{PoolEnd} (gw {Gateway}, dns {Dns}). Only run this on an isolated lab LAN.");
        _ = Task.Run(ReceiveLoopAsync);
    }

    protected override void OnStop()
    {
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    private async Task ReceiveLoopAsync()
    {
        var socket = _socket;
        while (KeepRunning && socket is not null)
        {
            UdpReceiveResult res;
            try { res = await socket.ReceiveAsync(); }
            catch { break; }
            try { HandleAndReply(socket, res.Buffer); }
            catch (Exception ex) { Log(LogLevel.Error, res.RemoteEndPoint, ex.Message); }
        }
    }

    private void HandleAndReply(UdpClient socket, byte[] data)
    {
        if (data.Length < 240) return;
        var req = Dhcp.Parse(data);
        if (req.Op != 1 || req.Type is not { } type) return;
        var host = req.Options.TryGetValue(Dhcp.Opt.HostName, out var hn) ? System.Text.Encoding.ASCII.GetString(hn) : "";

        switch (type)
        {
            case Dhcp.MessageType.Discover:
                var offer = Allocate(req.MacString, host);
                if (offer is null) { Log(LogLevel.Warning, "", $"{req.MacString}: pool exhausted"); return; }
                Log(LogLevel.Send, "", $"OFFER {offer.Ip} to {req.MacString} {host}".TrimEnd());
                SendReply(socket, req, Dhcp.MessageType.Offer, offer.Ip);
                break;
            case Dhcp.MessageType.Request:
                var requested = req.Options.TryGetValue(Dhcp.Opt.RequestedIp, out var ri) ? new IPAddress(ri) : req.CiAddr;
                var lease = Confirm(req.MacString, requested, host);
                if (lease is null) { Log(LogLevel.Warning, "", $"NAK {req.MacString} for {requested}"); SendReply(socket, req, Dhcp.MessageType.Nak, IPAddress.Any); return; }
                Log(LogLevel.Success, "", $"ACK {lease.Ip} to {req.MacString} {host}".TrimEnd());
                SendReply(socket, req, Dhcp.MessageType.Ack, lease.Ip);
                break;
            case Dhcp.MessageType.Release:
                lock (_lock) _leases.Remove(req.MacString);
                Log(LogLevel.Info, "", $"RELEASE from {req.MacString}");
                break;
        }
    }

    /// <summary>Assigns (or reuses) an address for a MAC and tentatively reserves it, so a second client's OFFER gets
    /// the next free address rather than the same one. Used to build an OFFER.</summary>
    public DhcpLease? Allocate(string mac, string host)
    {
        lock (_lock)
        {
            if (_leases.TryGetValue(mac, out var existing))
            {
                var reused = existing with { HostName = host, ExpiresUtc = DateTime.UtcNow + LeaseTime };
                _leases[mac] = reused;
                return reused;
            }
            uint start = Ipv4.ToUInt(PoolStart), end = Ipv4.ToUInt(PoolEnd);
            var taken = _leases.Values.Select(l => Ipv4.ToUInt(l.Ip)).ToHashSet();
            for (uint a = start; a <= end; a++)
                if (!taken.Contains(a))
                {
                    var lease = new DhcpLease(mac, Ipv4.FromUInt(a), DateTime.UtcNow + LeaseTime, host);
                    _leases[mac] = lease;
                    return lease;
                }
            return null;
        }
    }

    /// <summary>Commits a lease when the client REQUESTs it (must be in-pool and not held by another MAC).</summary>
    public DhcpLease? Confirm(string mac, IPAddress requested, string host)
    {
        lock (_lock)
        {
            uint ip = Ipv4.ToUInt(requested);
            if (ip < Ipv4.ToUInt(PoolStart) || ip > Ipv4.ToUInt(PoolEnd))
            {
                // Client asked for something outside our pool: offer it our own choice instead of NAK when free.
                var alt = Allocate(mac, host);
                if (alt is null) return null;
                requested = alt.Ip;
            }
            else if (_leases.Values.Any(l => Ipv4.ToUInt(l.Ip) == ip && !l.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)))
                return null;
            var lease = new DhcpLease(mac, requested, DateTime.UtcNow + LeaseTime, host);
            _leases[mac] = lease;
            return lease;
        }
    }

    private void SendReply(UdpClient socket, Dhcp.Message req, Dhcp.MessageType type, IPAddress yiaddr)
    {
        var reply = new Dhcp.Message
        {
            Op = 2, Xid = req.Xid, Flags = req.Flags, HLen = req.HLen, ChAddr = req.ChAddr,
            YiAddr = type == Dhcp.MessageType.Nak ? IPAddress.Any : yiaddr,
            GiAddr = req.GiAddr, SiAddr = ServerId,
        };
        reply.Options[Dhcp.Opt.MessageType] = Dhcp.Byte((byte)type);
        reply.Options[Dhcp.Opt.ServerId] = Dhcp.Ip(ServerId);
        if (type != Dhcp.MessageType.Nak)
        {
            reply.Options[Dhcp.Opt.LeaseTime] = Dhcp.U32((uint)LeaseTime.TotalSeconds);
            reply.Options[Dhcp.Opt.SubnetMask] = Dhcp.Ip(Mask);
            reply.Options[Dhcp.Opt.Router] = Dhcp.Ip(Gateway);
            reply.Options[Dhcp.Opt.DnsServer] = Dhcp.Ip(Dns);
        }
        var bytes = Dhcp.Build(reply);

        // Relay present → unicast to the relay; else broadcast (the client has no IP yet).
        var dest = !req.GiAddr.Equals(IPAddress.Any)
            ? new IPEndPoint(req.GiAddr, Dhcp.ServerPort)
            : new IPEndPoint(IPAddress.Broadcast, Dhcp.ClientPort);
        socket.Send(bytes, bytes.Length, dest);
    }
}
