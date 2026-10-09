using System.Buffers.Binary;

namespace OmlTerminal.Core.Capture;

public sealed record RawFrame(DateTime Timestamp, byte[] Data, int OriginalLength, int LinkType);

/// <summary>Writes classic libpcap files (what Wireshark, tcpdump and every other tool open).</summary>
public sealed class PcapWriter : IDisposable
{
    private readonly Stream _out;

    public PcapWriter(Stream output, int linkType, int snapLen = 262144)
    {
        _out = output;
        Span<byte> h = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0xa1b2c3d4);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)snapLen);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)linkType);
        _out.Write(h);
    }

    public void Write(DateTime timestamp, ReadOnlySpan<byte> data, int originalLength)
    {
        var utc = timestamp.ToUniversalTime();
        long ticks = (utc - DateTime.UnixEpoch).Ticks;
        Span<byte> h = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)(ticks / TimeSpan.TicksPerSecond));
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(ticks % TimeSpan.TicksPerSecond / 10));
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)Math.Max(originalLength, data.Length));
        _out.Write(h);
        _out.Write(data);
    }

    public void Dispose() => _out.Flush();
}

/// <summary>Reads libpcap (either byte order, micro- or nanosecond) and pcapng files.</summary>
public static class PcapReader
{
    public static IReadOnlyList<RawFrame> Read(string path) => Read(path, long.MaxValue, out _);

    /// <summary>Reads at most <paramref name="maxBytes"/> of the file (a multi-gigabyte capture would otherwise be
    /// loaded whole); <paramref name="truncated"/> says whether there was more.</summary>
    public static IReadOnlyList<RawFrame> Read(string path, long maxBytes, out bool truncated)
    {
        using var f = File.OpenRead(path);
        long take = Math.Min(f.Length, Math.Min(maxBytes, Array.MaxLength));
        truncated = take < f.Length;
        var bytes = new byte[take];
        f.ReadExactly(bytes);
        return Read(bytes);
    }

    public static IReadOnlyList<RawFrame> Read(byte[] b)
    {
        if (b.Length < 24) throw new InvalidDataException("File is too small to be a capture.");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(b);
        return magic switch
        {
            0x0a0d0d0a => ReadPcapNg(b),
            0xa1b2c3d4 or 0xa1b23c4d => ReadPcap(b, little: true, nano: magic == 0xa1b23c4d),
            0xd4c3b2a1 or 0x4d3cb2a1 => ReadPcap(b, little: false, nano: magic == 0x4d3cb2a1),
            _ => throw new InvalidDataException("Not a pcap or pcapng file."),
        };
    }

    private static uint U32(byte[] b, int o, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    private static IReadOnlyList<RawFrame> ReadPcap(byte[] b, bool little, bool nano)
    {
        int link = (int)(U32(b, 20, little) & 0x0fffffff);
        var frames = new List<RawFrame>();
        int i = 24;
        while (i + 16 <= b.Length)
        {
            uint sec = U32(b, i, little), frac = U32(b, i + 4, little), incl = U32(b, i + 8, little), orig = U32(b, i + 12, little);
            i += 16;
            if (incl > b.Length - i) break;
            var ts = DateTime.UnixEpoch.AddSeconds(sec).AddTicks(nano ? frac / 100 : frac * 10).ToLocalTime();
            frames.Add(new RawFrame(ts, b.AsSpan(i, (int)incl).ToArray(), (int)orig, link));
            i += (int)incl;
        }
        return frames;
    }

    private static IReadOnlyList<RawFrame> ReadPcapNg(byte[] b)
    {
        var frames = new List<RawFrame>();
        var links = new List<(int Link, double TicksPerUnit)>();
        bool little = true;
        int i = 0;
        while (i + 12 <= b.Length)
        {
            uint type = U32(b, i, little);
            if (type == 0x0a0d0d0a)
            {
                little = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 8)) == 0x1a2b3c4d;
                links.Clear();
            }
            uint len = U32(b, i + 4, little);
            if (len < 12 || i + len > b.Length) break;
            switch (type)
            {
                case 1: // Interface Description Block
                    int link = little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i + 8)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 8));
                    double tpu = 10; // default resolution: microseconds
                    int opt = i + 16;
                    while (opt + 4 <= i + len - 4)
                    {
                        int code = little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(opt));
                        int olen = little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 2)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(opt + 2));
                        if (code == 0) break;
                        if (code == 9 && olen >= 1)
                        {
                            byte r = b[opt + 4];
                            double unitsPerSecond = (r & 0x80) != 0 ? Math.Pow(2, r & 0x7f) : Math.Pow(10, r & 0x7f);
                            tpu = TimeSpan.TicksPerSecond / unitsPerSecond;
                        }
                        opt += 4 + (olen + 3) / 4 * 4;
                    }
                    links.Add((link, tpu));
                    break;
                case 6: // Enhanced Packet Block
                    {
                        int ifIdx = (int)U32(b, i + 8, little);
                        ulong ts = (ulong)U32(b, i + 12, little) << 32 | U32(b, i + 16, little);
                        int cap = (int)U32(b, i + 20, little), orig = (int)U32(b, i + 24, little);
                        if (ifIdx < links.Count && cap <= len - 32)
                        {
                            var (lk, t) = links[ifIdx];
                            var when = DateTime.UnixEpoch.AddTicks((long)(ts * t)).ToLocalTime();
                            frames.Add(new RawFrame(when, b.AsSpan(i + 28, cap).ToArray(), orig, lk));
                        }
                        break;
                    }
                case 3: // Simple Packet Block
                    if (links.Count > 0)
                    {
                        int orig = (int)U32(b, i + 8, little);
                        int cap = Math.Min(orig, (int)len - 16);
                        frames.Add(new RawFrame(DateTime.Now, b.AsSpan(i + 12, cap).ToArray(), orig, links[0].Link));
                    }
                    break;
            }
            i += (int)len;
        }
        return frames;
    }
}
