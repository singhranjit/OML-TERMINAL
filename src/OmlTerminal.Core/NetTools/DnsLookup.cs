using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace OmlTerminal.Core.NetTools;

public enum DnsRecordType : ushort
{
    A = 1, NS = 2, CNAME = 5, SOA = 6, PTR = 12, MX = 15, TXT = 16, AAAA = 28, SRV = 33, CAA = 257, ANY = 255,
}

public sealed record DnsRecord(string Section, string Name, DnsRecordType Type, uint Ttl, string Data);

public sealed record DnsResponse(IPAddress Server, string ResponseCode, bool Authoritative, bool Truncated,
    IReadOnlyList<DnsRecord> Records, TimeSpan Elapsed);

/// <summary>
/// A small `dig`: asks one specific server (the system resolver by default) for any record type, and shows the raw
/// answer/authority/additional sections with TTLs - which System.Net.Dns hides. UDP first, TCP on truncation.
/// </summary>
public static class DnsLookup
{
    public static IPAddress SystemResolver()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var dns = nic.GetIPProperties().DnsAddresses
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("fec0"));
            if (dns is not null) return dns;
        }
        return IPAddress.Parse("1.1.1.1");
    }

    /// <summary>"10.1.2.3" → "3.2.1.10.in-addr.arpa" so a PTR lookup can be typed as a plain address.</summary>
    public static string ReverseName(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length == 4) return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        var nibbles = b.Reverse().SelectMany(x => new[] { x & 0xF, x >> 4 }).Select(n => n.ToString("x"));
        return string.Join('.', nibbles) + ".ip6.arpa";
    }

    public static async Task<DnsResponse> QueryAsync(string name, DnsRecordType type, IPAddress? server = null,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        server ??= SystemResolver();
        if (type == DnsRecordType.PTR && IPAddress.TryParse(name, out var ptrIp)) name = ReverseName(ptrIp);
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var query = BuildQuery(id, name, type);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        byte[] reply;
        using (var udp = new UdpClient(server.AddressFamily))
        {
            udp.Connect(server, 53);
            await udp.SendAsync(query, cts.Token).ConfigureAwait(false);
            try { reply = (await udp.ReceiveAsync(cts.Token).ConfigureAwait(false)).Buffer; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer from {server} within {timeoutMs} ms.");
            }
        }
        var parsed = Parse(reply, id);
        if (parsed.Truncated)
        {
            reply = await QueryTcpAsync(server, query, cts.Token).ConfigureAwait(false);
            parsed = Parse(reply, id);
        }
        return parsed with { Server = server, Elapsed = sw.Elapsed };
    }

    private static async Task<byte[]> QueryTcpAsync(IPAddress server, byte[] query, CancellationToken ct)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, 53, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
        var lenBuf = new byte[2];
        await stream.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        var reply = new byte[BinaryPrimitives.ReadUInt16BigEndian(lenBuf)];
        await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
        return reply;
    }

    public static byte[] BuildQuery(ushort id, string name, DnsRecordType type)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // QDCOUNT
        ms.Write(header);
        foreach (var label in name.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) throw new FormatException($"Label '{label}' is longer than 63 characters.");
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // IN
        ms.Write(tail);
        return ms.ToArray();
    }

    public static DnsResponse Parse(byte[] msg, ushort? expectedId = null)
    {
        if (msg.Length < 12) throw new FormatException("DNS reply too short.");
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(msg);
        if (expectedId is { } e && id != e) throw new FormatException("DNS reply ID mismatch.");
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(2));
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
        int an = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
        int ns = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(8));
        int ar = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(10));
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(msg, ref pos); pos += 4; }

        var records = new List<DnsRecord>();
        void ReadSection(string section, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var name = ReadName(msg, ref pos);
                var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(pos + 4));
                int rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos + 8));
                pos += 10;
                int rdStart = pos;
                string data = DecodeRdata(msg, type, rdStart, rdlen);
                pos = rdStart + rdlen;
                if (type == (DnsRecordType)41) continue; // OPT pseudo-record
                records.Add(new DnsRecord(section, name, type, ttl, data));
            }
        }
        ReadSection("ANSWER", an);
        ReadSection("AUTHORITY", ns);
        ReadSection("ADDITIONAL", ar);

        string rcode = (flags & 0xF) switch
        {
            0 => "NOERROR", 1 => "FORMERR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 4 => "NOTIMP", 5 => "REFUSED", var c => $"RCODE{c}",
        };
        return new DnsResponse(IPAddress.None, rcode, (flags & 0x0400) != 0, (flags & 0x0200) != 0, records, TimeSpan.Zero);
    }

    private static string DecodeRdata(byte[] msg, DnsRecordType type, int start, int len)
    {
        int p = start;
        switch (type)
        {
            case DnsRecordType.A when len == 4: return new IPAddress(msg.AsSpan(start, 4)).ToString();
            case DnsRecordType.AAAA when len == 16: return new IPAddress(msg.AsSpan(start, 16)).ToString();
            case DnsRecordType.NS or DnsRecordType.CNAME or DnsRecordType.PTR: return ReadName(msg, ref p);
            case DnsRecordType.MX:
            {
                int pref = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(p)); p += 2;
                return $"{pref} {ReadName(msg, ref p)}";
            }
            case DnsRecordType.SRV:
            {
                int prio = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(p));
                int weight = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(p + 2));
                int port = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(p + 4));
                p += 6;
                return $"{prio} {weight} {port} {ReadName(msg, ref p)}";
            }
            case DnsRecordType.SOA:
            {
                var mname = ReadName(msg, ref p);
                var rname = ReadName(msg, ref p);
                uint serial = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(p));
                return $"{mname} {rname} serial {serial}";
            }
            case DnsRecordType.TXT:
            {
                var parts = new List<string>();
                while (p < start + len)
                {
                    int l = msg[p++];
                    parts.Add("\"" + Encoding.UTF8.GetString(msg, p, l) + "\"");
                    p += l;
                }
                return string.Join(' ', parts);
            }
            case DnsRecordType.CAA when len >= 2:
            {
                int flags = msg[p], tagLen = msg[p + 1];
                var tag = Encoding.ASCII.GetString(msg, p + 2, tagLen);
                var value = Encoding.ASCII.GetString(msg, p + 2 + tagLen, len - 2 - tagLen);
                return $"{flags} {tag} \"{value}\"";
            }
            default: return Convert.ToHexString(msg, start, len);
        }
    }

    public static string ReadName(byte[] msg, ref int pos)
    {
        var labels = new List<string>();
        int p = pos;
        bool jumped = false;
        for (int guard = 0; guard < 128; guard++)
        {
            int len = msg[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int target = ((len & 0x3F) << 8) | msg[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            labels.Add(Encoding.ASCII.GetString(msg, p + 1, len));
            p += len + 1;
        }
        if (!jumped) pos = p;
        return labels.Count == 0 ? "." : string.Join('.', labels) + ".";
    }
}
