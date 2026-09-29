using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace OmlTerminal.Core.NetTools;

/// <summary>An IPv4 or IPv6 prefix. Parses "10.0.0.0/24", "10.0.0.5 255.255.255.0", "10.0.0.5/255.255.255.0",
/// a bare host ("10.0.0.5" = /32) and IPv6 ("2001:db8::/48").</summary>
public sealed record Subnet(IPAddress Network, int PrefixLength)
{
    public bool IsV6 => Network.AddressFamily == AddressFamily.InterNetworkV6;
    public int Bits => IsV6 ? 128 : 32;

    public static bool TryParse(string text, out Subnet subnet)
    {
        subnet = null!;
        try { subnet = Parse(text); return true; } catch (FormatException) { return false; }
    }

    public static Subnet Parse(string text)
    {
        var t = text.Trim();
        string addrPart = t, maskPart = "";
        int slash = t.IndexOf('/');
        if (slash >= 0) { addrPart = t[..slash].Trim(); maskPart = t[(slash + 1)..].Trim(); }
        else
        {
            var parts = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { addrPart = parts[0]; maskPart = parts[1]; }
            else if (parts.Length != 1) throw new FormatException($"'{text}' is not an address or subnet.");
        }
        if (!IPAddress.TryParse(addrPart, out var ip) || ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || (ip.AddressFamily == AddressFamily.InterNetwork && addrPart.Count(c => c == '.') != 3))
            throw new FormatException($"'{addrPart}' is not a valid IP address.");

        int bits = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        int prefix;
        if (maskPart.Length == 0) prefix = bits;
        else if (int.TryParse(maskPart, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
        {
            if (p < 0 || p > bits) throw new FormatException($"/{p} is out of range for {(bits == 32 ? "IPv4" : "IPv6")}.");
            prefix = p;
        }
        else if (bits == 32 && IPAddress.TryParse(maskPart, out var mask)) prefix = PrefixFromMask(mask);
        else throw new FormatException($"'{maskPart}' is not a valid mask or prefix length.");

        return new Subnet(Mask(ip, prefix), prefix) { OriginalAddress = ip };
    }

    /// <summary>The address as typed (Network is always masked down to the network address).</summary>
    public IPAddress OriginalAddress { get; init; } = IPAddress.None;

    public bool IsHost => PrefixLength == Bits;

    public static int PrefixFromMask(IPAddress mask)
    {
        uint m = Ipv4.ToUInt(mask);
        // Also accept Cisco wildcard masks (0.0.0.255) by inverting when the high bit is clear.
        if (m != 0 && (m & 0x80000000) == 0) m = ~m;
        int prefix = BitOperations.PopCount(m);
        if (prefix != 0 && m != uint.MaxValue << (32 - prefix)) throw new FormatException($"{mask} is not a contiguous subnet mask.");
        return prefix;
    }

    public static IPAddress Mask(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++)
        {
            int keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }

    public IPAddress SubnetMask => IsV6 ? throw new NotSupportedException() : Ipv4.FromUInt(PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength));
    public IPAddress Wildcard => Ipv4.FromUInt(~Ipv4.ToUInt(SubnetMask));
    public IPAddress Broadcast => IsV6 ? LastAddress : Ipv4.FromUInt(Ipv4.ToUInt(Network) | ~Ipv4.ToUInt(SubnetMask));

    public IPAddress LastAddress
    {
        get
        {
            var bytes = Network.GetAddressBytes();
            for (int i = 0; i < bytes.Length; i++)
            {
                int keep = Math.Clamp(PrefixLength - i * 8, 0, 8);
                bytes[i] |= (byte)~(0xFF << (8 - keep));
            }
            return new IPAddress(bytes);
        }
    }

    public BigInteger TotalAddresses => BigInteger.One << (Bits - PrefixLength);

    /// <summary>Usable hosts: /31 and /32 are point-to-point / host routes (RFC 3021), so all addresses count.</summary>
    public BigInteger UsableHosts => IsV6 || PrefixLength >= 31 ? TotalAddresses : TotalAddresses - 2;

    public IPAddress FirstHost => IsV6 || PrefixLength >= 31 ? Network : Ipv4.FromUInt(Ipv4.ToUInt(Network) + 1);
    public IPAddress LastHost => IsV6 || PrefixLength >= 31 ? LastAddress : Ipv4.FromUInt(Ipv4.ToUInt(Broadcast) - 1);

    public bool Contains(IPAddress ip) =>
        ip.AddressFamily == Network.AddressFamily && Mask(ip, PrefixLength).Equals(Network);

    public string Cidr => $"{Network}/{PrefixLength}";
    public override string ToString() => Cidr;

    public string Classification
    {
        get
        {
            if (IsV6)
            {
                var b = Network.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return "Unique local (RFC 4193)";
                if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return "Link-local";
                if (b[0] == 0xFF) return "Multicast";
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return "Documentation (RFC 3849)";
                return "Global unicast";
            }
            uint a = Ipv4.ToUInt(Network);
            if (Within(a, "10.0.0.0", 8) || Within(a, "172.16.0.0", 12) || Within(a, "192.168.0.0", 16)) return "Private (RFC 1918)";
            if (Within(a, "100.64.0.0", 10)) return "Carrier-grade NAT (RFC 6598)";
            if (Within(a, "127.0.0.0", 8)) return "Loopback";
            if (Within(a, "169.254.0.0", 16)) return "Link-local (APIPA)";
            if (Within(a, "224.0.0.0", 4)) return "Multicast";
            if (Within(a, "192.0.2.0", 24) || Within(a, "198.51.100.0", 24) || Within(a, "203.0.113.0", 24)) return "Documentation (RFC 5737)";
            if (Within(a, "240.0.0.0", 4)) return "Reserved";
            return "Public";
        }
    }

    private static bool Within(uint a, string net, int prefix) =>
        (a & (uint.MaxValue << (32 - prefix))) == Ipv4.ToUInt(IPAddress.Parse(net));

    /// <summary>Splits this network into equal subnets of the given prefix (capped so the UI can't ask for millions).</summary>
    public IReadOnlyList<Subnet> Split(int newPrefix, int max = 4096)
    {
        if (IsV6) throw new NotSupportedException("Splitting is IPv4 only.");
        if (newPrefix < PrefixLength || newPrefix > 32) throw new ArgumentOutOfRangeException(nameof(newPrefix));
        var list = new List<Subnet>();
        uint start = Ipv4.ToUInt(Network);
        ulong step = 1UL << (32 - newPrefix);
        ulong count = 1UL << (newPrefix - PrefixLength);
        for (ulong i = 0; i < count && list.Count < max; i++)
            list.Add(new Subnet(Ipv4.FromUInt((uint)(start + i * step)), newPrefix));
        return list;
    }
}

public static class Ipv4
{
    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) throw new FormatException($"{ip} is not IPv4.");
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    public static bool IsIpv4(string s) =>
        IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') == 3;

    /// <summary>Minimal set of CIDR blocks exactly covering start..end - what an ACL or a "range" object really means.</summary>
    public static IReadOnlyList<Subnet> RangeToCidrs(IPAddress start, IPAddress end)
    {
        ulong s = ToUInt(start), e = ToUInt(end);
        if (s > e) (s, e) = (e, s);
        var result = new List<Subnet>();
        while (s <= e)
        {
            int size = s == 0 ? 32 : BitOperations.TrailingZeroCount((uint)s);
            while (size > 0 && s + (1UL << size) - 1 > e) size--;
            result.Add(new Subnet(FromUInt((uint)s), 32 - size));
            s += 1UL << size;
        }
        return result;
    }

    /// <summary>Merges overlapping/adjacent IPv4 prefixes into the smallest equivalent list (route summarisation).</summary>
    public static IReadOnlyList<Subnet> Summarize(IEnumerable<Subnet> subnets)
    {
        var ranges = subnets.Where(n => !n.IsV6)
            .Select(n => (Start: (ulong)ToUInt(n.Network), End: (ulong)ToUInt(n.Broadcast)))
            .OrderBy(r => r.Start).ToList();
        var merged = new List<(ulong Start, ulong End)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End + 1)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        }
        return merged.SelectMany(r => RangeToCidrs(FromUInt((uint)r.Start), FromUInt((uint)r.End))).ToList();
    }
}
