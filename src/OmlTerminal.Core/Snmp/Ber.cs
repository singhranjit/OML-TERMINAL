using System.Numerics;
using System.Text;

namespace OmlTerminal.Core.Snmp;

/// <summary>SNMP value types (ASN.1 BER tags).</summary>
public static class SnmpType
{
    public const byte Integer = 0x02, OctetString = 0x04, Null = 0x05, Oid = 0x06, Sequence = 0x30;
    public const byte IpAddress = 0x40, Counter32 = 0x41, Gauge32 = 0x42, TimeTicks = 0x43, Opaque = 0x44, Counter64 = 0x46;
    public const byte NoSuchObject = 0x80, NoSuchInstance = 0x81, EndOfMibView = 0x82;
    public const byte GetRequest = 0xA0, GetNextRequest = 0xA1, Response = 0xA2, SetRequest = 0xA3, GetBulkRequest = 0xA5, Report = 0xA8;
}

/// <summary>A decoded SNMP value: the BER tag and the raw content bytes, with typed accessors.</summary>
public sealed record SnmpValue(byte Type, byte[] Raw)
{
    public static readonly SnmpValue NullValue = new(SnmpType.Null, []);

    public bool IsException => Type is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView;

    public ulong ToUInt64()
    {
        ulong v = 0;
        foreach (var b in Raw) v = (v << 8) | b;
        return v;
    }

    public long ToInt64()
    {
        if (Raw.Length == 0) return 0;
        long v = (sbyte)Raw[0];
        for (int i = 1; i < Raw.Length; i++) v = (v << 8) | Raw[i];
        return v;
    }

    public override string ToString() => Type switch
    {
        SnmpType.Integer => ToInt64().ToString(),
        SnmpType.Counter32 or SnmpType.Gauge32 or SnmpType.TimeTicks or SnmpType.Counter64 => ToUInt64().ToString(),
        SnmpType.OctetString => Raw.All(b => b >= 0x20 && b < 0x7F || b is 0x09 or 0x0A or 0x0D) ? Encoding.UTF8.GetString(Raw).TrimEnd('\0') : Convert.ToHexString(Raw),
        SnmpType.IpAddress when Raw.Length == 4 => string.Join('.', Raw),
        SnmpType.Oid => Ber.DecodeOid(Raw),
        SnmpType.Null => "",
        SnmpType.NoSuchObject => "noSuchObject",
        SnmpType.NoSuchInstance => "noSuchInstance",
        SnmpType.EndOfMibView => "endOfMibView",
        _ => Convert.ToHexString(Raw),
    };
}

public sealed record SnmpVarbind(string Oid, SnmpValue Value);

/// <summary>Minimal BER encoder/decoder - exactly what SNMP needs, nothing more.</summary>
public static class Ber
{
    // ---------- encoding ----------

    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        var len = Length(content.Length);
        var buf = new byte[1 + len.Length + content.Length];
        buf[0] = tag;
        len.CopyTo(buf, 1);
        content.CopyTo(buf.AsSpan(1 + len.Length));
        return buf;
    }

    public static byte[] Seq(byte tag, params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(all, o); o += p.Length; }
        return Tlv(tag, all);
    }

    public static byte[] Sequence(params byte[][] parts) => Seq(SnmpType.Sequence, parts);

    private static byte[] Length(int n)
    {
        if (n < 0x80) return [(byte)n];
        var bytes = new List<byte>();
        for (int v = n; v > 0; v >>= 8) bytes.Insert(0, (byte)v);
        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return bytes.ToArray();
    }

    public static byte[] Integer(long v)
    {
        var bytes = new BigInteger(v).ToByteArray(isUnsigned: false, isBigEndian: true);
        return Tlv(SnmpType.Integer, bytes);
    }

    public static byte[] Octets(ReadOnlySpan<byte> v) => Tlv(SnmpType.OctetString, v);
    public static byte[] Octets(string s) => Octets(Encoding.UTF8.GetBytes(s));
    public static byte[] Null() => [SnmpType.Null, 0];

    public static byte[] Oid(string oid)
    {
        var parts = oid.Trim('.').Split('.').Select(uint.Parse).ToArray();
        if (parts.Length < 2) throw new FormatException($"Bad OID: {oid}");
        var body = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (var p in parts.Skip(2))
        {
            var chunk = new Stack<byte>();
            uint v = p;
            chunk.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) chunk.Push((byte)(0x80 | (v & 0x7F)));
            body.AddRange(chunk);
        }
        return Tlv(SnmpType.Oid, body.ToArray());
    }

    // ---------- decoding ----------

    public ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        public int Position;

        public Reader(ReadOnlySpan<byte> data) { _data = data; Position = 0; }

        public readonly bool More => Position < _data.Length;

        /// <summary>Reads one TLV: returns its tag and the content slice, and moves past it.</summary>
        public (byte Tag, int Start, int Length) Next()
        {
            if (Position + 2 > _data.Length) throw new FormatException("Truncated BER.");
            byte tag = _data[Position++];
            int len = _data[Position++];
            if ((len & 0x80) != 0)
            {
                int n = len & 0x7F;
                if (n is 0 or > 4 || Position + n > _data.Length) throw new FormatException("Bad BER length.");
                len = 0;
                for (int i = 0; i < n; i++) len = (len << 8) | _data[Position++];
            }
            if (len < 0 || Position + len > _data.Length) throw new FormatException("BER length past the end.");
            int start = Position;
            Position += len;
            return (tag, start, len);
        }

        /// <summary>Steps into a constructed TLV (SEQUENCE/PDU) without skipping its content.</summary>
        public (byte Tag, int Start, int Length) Enter()
        {
            var t = Next();
            Position = t.Start;
            return t;
        }

        public readonly ReadOnlySpan<byte> Slice(int start, int length) => _data.Slice(start, length);

        public long ReadInteger()
        {
            var (tag, s, l) = Next();
            if (tag != SnmpType.Integer) throw new FormatException($"Expected INTEGER, got 0x{tag:X2}.");
            return new SnmpValue(tag, _data.Slice(s, l).ToArray()).ToInt64();
        }

        public byte[] ReadOctets()
        {
            var (tag, s, l) = Next();
            if (tag != SnmpType.OctetString) throw new FormatException($"Expected OCTET STRING, got 0x{tag:X2}.");
            return _data.Slice(s, l).ToArray();
        }
    }

    public static string DecodeOid(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return "";
        var sb = new StringBuilder();
        int first = raw[0];
        sb.Append(first < 80 ? first / 40 : 2).Append('.').Append(first < 80 ? first % 40 : first - 80);
        ulong v = 0;
        for (int i = 1; i < raw.Length; i++)
        {
            v = (v << 7) | (uint)(raw[i] & 0x7F);
            if ((raw[i] & 0x80) == 0) { sb.Append('.').Append(v); v = 0; }
        }
        return sb.ToString();
    }

    /// <summary>"1.3.6.1.2.1.2.2.1.10.5" is under "1.3.6.1.2.1.2.2.1.10" - compared numerically, part by part.</summary>
    public static bool IsUnder(string oid, string root) => oid.StartsWith(root + ".", StringComparison.Ordinal);

    public static int CompareOid(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = ulong.Parse(x[i]).CompareTo(ulong.Parse(y[i]));
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}
