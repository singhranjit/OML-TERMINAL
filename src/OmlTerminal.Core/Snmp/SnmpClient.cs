using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OmlTerminal.Core.Snmp;

public enum SnmpVersion { V1, V2c, V3 }
public enum SnmpAuth { None, Md5, Sha1, Sha224, Sha256, Sha384, Sha512 }
/// <summary>Aes192/Aes256 extend the key the net-snmp (Blumenthal) way; the Cisco variants use Cisco's (Reeder) method.</summary>
public enum SnmpPriv { None, Des, Aes128, Aes192, Aes256, Aes192Cisco, Aes256Cisco }

/// <summary>How to talk to an agent. Plain properties so it can be saved (secrets go through LocalSecret).</summary>
public sealed class SnmpCredentials
{
    public SnmpVersion Version { get; set; } = SnmpVersion.V2c;
    public string Community { get; set; } = "public";
    public string User { get; set; } = "";
    public SnmpAuth Auth { get; set; } = SnmpAuth.Sha1;
    public string AuthPassword { get; set; } = "";
    public SnmpPriv Priv { get; set; } = SnmpPriv.Aes128;
    public string PrivPassword { get; set; } = "";
    public string ContextName { get; set; } = "";

    public SnmpCredentials Clone() => (SnmpCredentials)MemberwiseClone();
}

public sealed class SnmpException(string message) : Exception(message);

/// <summary>
/// SNMP v1, v2c and v3 (USM: MD5/SHA-1/SHA-2 authentication, DES/AES-128/192/256 privacy) over UDP. Get, GetNext,
/// GetBulk and table walks. v3 engine discovery and time-window resync are automatic.
/// </summary>
public sealed class SnmpClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly SnmpCredentials _cred;
    private readonly int _timeoutMs;
    private readonly int _retries;
    private int _requestId = Random.Shared.Next(1, int.MaxValue / 2);

    // v3 state
    private byte[] _engineId = [];
    private int _engineBoots, _engineTime;
    private DateTime _timeAt;
    private byte[]? _authKey, _privKey;
    private long _salt = Random.Shared.NextInt64();

    public IPEndPoint Endpoint { get; }

    public SnmpClient(IPEndPoint endpoint, SnmpCredentials cred, int timeoutMs = 2000, int retries = 1)
    {
        Endpoint = endpoint;
        _cred = cred;
        _timeoutMs = timeoutMs;
        _retries = retries;
        _udp = new UdpClient(endpoint.AddressFamily);
        _udp.Connect(endpoint);
    }

    public static async Task<SnmpClient> ConnectAsync(string host, int port, SnmpCredentials cred, int timeoutMs = 2000, int retries = 1, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(host, out var ip))
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault()
                 ?? throw new SnmpException($"Can't resolve {host}.");
        }
        return new SnmpClient(new IPEndPoint(ip, port), cred, timeoutMs, retries);
    }

    public Task<IReadOnlyList<SnmpVarbind>> GetAsync(IEnumerable<string> oids, CancellationToken ct = default) =>
        RequestAsync(SnmpType.GetRequest, oids.ToList(), 0, 0, ct);

    public Task<IReadOnlyList<SnmpVarbind>> GetNextAsync(IEnumerable<string> oids, CancellationToken ct = default) =>
        RequestAsync(SnmpType.GetNextRequest, oids.ToList(), 0, 0, ct);

    public Task<IReadOnlyList<SnmpVarbind>> GetBulkAsync(IEnumerable<string> oids, int nonRepeaters, int maxRepetitions, CancellationToken ct = default)
    {
        if (_cred.Version == SnmpVersion.V1) throw new SnmpException("GetBulk needs SNMP v2c or v3.");
        return RequestAsync(SnmpType.GetBulkRequest, oids.ToList(), nonRepeaters, maxRepetitions, ct);
    }

    /// <summary>Every varbind under <paramref name="root"/>, in order (GetBulk on v2c/v3, GetNext on v1).</summary>
    public async Task<IReadOnlyList<SnmpVarbind>> WalkAsync(string root, CancellationToken ct = default, int maxRows = 100_000)
    {
        root = root.Trim('.');
        var result = new List<SnmpVarbind>();
        var current = root;
        while (result.Count < maxRows)
        {
            var batch = _cred.Version == SnmpVersion.V1
                ? await GetNextAsync([current], ct).ConfigureAwait(false)
                : await GetBulkAsync([current], 0, 25, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;
            bool done = false;
            foreach (var vb in batch)
            {
                if (vb.Value.IsException || !Ber.IsUnder(vb.Oid, root) || Ber.CompareOid(vb.Oid, current) <= 0) { done = true; break; }
                result.Add(vb);
                current = vb.Oid;
            }
            if (done) break;
        }
        return result;
    }

    private async Task<IReadOnlyList<SnmpVarbind>> RequestAsync(byte pduType, IReadOnlyList<string> oids, int a, int b, CancellationToken ct)
    {
        if (_cred.Version == SnmpVersion.V3)
        {
            if (_engineId.Length == 0) await DiscoverAsync(ct).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                try { return await ExchangeV3Async(pduType, oids, a, b, ct).ConfigureAwait(false); }
                catch (SnmpException e) when (e.Message == NotInTimeWindow && attempt == 0) { } // resynced - try once more
            }
        }
        int id = NextId();
        var pdu = Pdu(pduType, id, oids, a, b);
        var msg = Ber.Sequence(Ber.Integer(_cred.Version == SnmpVersion.V1 ? 0 : 1), Ber.Octets(_cred.Community), pdu);
        var reply = await SendReceiveAsync(msg, ct, raw => MatchesCommunityReply(raw, id)).ConfigureAwait(false);
        var r = new Ber.Reader(reply);
        r.Enter();
        r.ReadInteger();
        r.ReadOctets();
        return ParsePdu(reply, ref r, id);
    }

    private int NextId() => Interlocked.Increment(ref _requestId) & 0x7FFFFFFF;

    private static byte[] Pdu(byte type, int id, IReadOnlyList<string> oids, int a, int b) =>
        Ber.Seq(type, Ber.Integer(id), Ber.Integer(a), Ber.Integer(b),
            Ber.Sequence(oids.Select(o => Ber.Sequence(Ber.Oid(o), Ber.Null())).ToArray()));

    private static bool MatchesCommunityReply(byte[] raw, int id)
    {
        try
        {
            var r = new Ber.Reader(raw);
            r.Enter();
            r.ReadInteger();
            r.ReadOctets();
            r.Enter();
            return r.ReadInteger() == id;
        }
        catch (FormatException) { return false; }
    }

    private static IReadOnlyList<SnmpVarbind> ParsePdu(ReadOnlySpan<byte> data, ref Ber.Reader r, int expectedId)
    {
        var (tag, _, _) = r.Enter();
        if (tag is not (SnmpType.Response or SnmpType.Report)) throw new SnmpException($"Unexpected PDU 0x{tag:X2}.");
        long id = r.ReadInteger();
        long status = r.ReadInteger();
        long index = r.ReadInteger();
        var (_, vlStart, vlLen) = r.Enter();
        int vlEnd = vlStart + vlLen; // stop at the list's own length - decrypted DES data carries padding after it
        var list = new List<SnmpVarbind>();
        while (r.Position < vlEnd)
        {
            r.Enter();
            var (ot, os, ol) = r.Next();
            if (ot != SnmpType.Oid) throw new FormatException("Varbind without an OID.");
            var oid = Ber.DecodeOid(r.Slice(os, ol));
            var (vt, vs, vl) = r.Next();
            list.Add(new SnmpVarbind(oid, new SnmpValue(vt, r.Slice(vs, vl).ToArray())));
        }
        if (tag == SnmpType.Report) throw new SnmpException(ReportText(list));
        if (status != 0) throw new SnmpException($"Agent error: {ErrorName(status)}{(index > 0 && index <= list.Count ? $" ({list[(int)index - 1].Oid})" : "")}.");
        if (expectedId != 0 && id != expectedId) throw new SnmpException("Reply for a different request.");
        return list;
    }

    private static string ErrorName(long s) => s switch
    {
        1 => "tooBig", 2 => "noSuchName", 3 => "badValue", 4 => "readOnly", 5 => "genErr", 6 => "noAccess", 7 => "wrongType",
        13 => "resourceUnavailable", 16 => "authorizationError", 17 => "notWritable", _ => $"error {s}",
    };

    private async Task<byte[]> SendReceiveAsync(byte[] msg, CancellationToken ct, Func<byte[], bool> accept)
    {
        for (int attempt = 0; attempt <= _retries; attempt++)
        {
            await _udp.SendAsync(msg, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);
            try
            {
                while (true)
                {
                    var res = await _udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (accept(res.Buffer)) return res.Buffer;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw new SnmpException($"{Endpoint} refused SNMP (ICMP port unreachable) - no agent listening on UDP {Endpoint.Port}.");
            }
        }
        throw new SnmpException(_cred.Version == SnmpVersion.V3
            ? $"No reply from {Endpoint} - check the address, that SNMP v3 is enabled and an ACL allows this PC."
            : $"No reply from {Endpoint} - wrong community, SNMP not enabled, or an ACL blocks this PC (agents silently ignore a bad community).");
    }

    // ===================== SNMPv3 / USM (RFC 3414, RFC 3826, RFC 7860) =====================

    private const string NotInTimeWindow = "notInTimeWindow";
    private const byte FlagAuth = 1, FlagPriv = 2, FlagReportable = 4;

    private async Task DiscoverAsync(CancellationToken ct)
    {
        int msgId = NextId();
        var empty = Ber.Sequence(Ber.Octets([]), Ber.Integer(0), Ber.Integer(0), Ber.Octets([]), Ber.Octets([]), Ber.Octets([]));
        var scoped = Ber.Sequence(Ber.Octets([]), Ber.Octets([]), Pdu(SnmpType.GetRequest, NextId(), [], 0, 0));
        var msg = Ber.Sequence(Ber.Integer(3), Header(msgId, FlagReportable), Ber.Octets(empty), scoped);
        var reply = await SendReceiveAsync(msg, ct, raw => V3MsgId(raw) == msgId).ConfigureAwait(false);
        var sec = ParseV3(reply).Security;
        if (sec.EngineId.Length == 0) throw new SnmpException("The agent didn't report its engine ID - is SNMPv3 enabled?");
        _engineId = sec.EngineId;
        SetTime(sec.Boots, sec.Time);
        if (_cred.Auth != SnmpAuth.None)
        {
            _authKey = LocalizedKey(_cred.Auth, _cred.AuthPassword, _engineId);
            if (_cred.Priv != SnmpPriv.None) _privKey = PrivKey(_cred.Auth, _cred.Priv, _cred.PrivPassword, _engineId);
        }
    }

    private void SetTime(int boots, int time) { _engineBoots = boots; _engineTime = time; _timeAt = DateTime.UtcNow; }
    private int EngineTimeNow => _engineTime + (int)(DateTime.UtcNow - _timeAt).TotalSeconds;

    private static byte[] Header(int msgId, byte flags) =>
        Ber.Sequence(Ber.Integer(msgId), Ber.Integer(65507), Ber.Octets([flags]), Ber.Integer(3));

    private async Task<IReadOnlyList<SnmpVarbind>> ExchangeV3Async(byte pduType, IReadOnlyList<string> oids, int a, int b, CancellationToken ct)
    {
        bool auth = _cred.Auth != SnmpAuth.None, priv = auth && _cred.Priv != SnmpPriv.None;
        int msgId = NextId(), reqId = NextId();
        byte flags = (byte)(FlagReportable | (auth ? FlagAuth : 0) | (priv ? FlagPriv : 0));
        int boots = _engineBoots, time = EngineTimeNow;
        var scoped = Ber.Sequence(Ber.Octets(_engineId), Ber.Octets(_cred.ContextName), Pdu(pduType, reqId, oids, a, b));
        byte[] salt = [];
        byte[] data = scoped;
        if (priv)
        {
            (data, salt) = Encrypt(scoped, boots, time);
            data = Ber.Octets(data);
        }
        int macLen = auth ? MacLength(_cred.Auth) : 0;
        var sec = Ber.Sequence(Ber.Octets(_engineId), Ber.Integer(boots), Ber.Integer(time), Ber.Octets(_cred.User),
            Ber.Octets(new byte[macLen]), Ber.Octets(salt));
        var msg = Ber.Sequence(Ber.Integer(3), Header(msgId, flags), Ber.Octets(sec), data);
        if (auth)
        {
            var mac = Hmac(_cred.Auth, _authKey!, msg).AsSpan(0, macLen);
            int at = FindZeros(msg, macLen);
            mac.CopyTo(msg.AsSpan(at));
        }

        var reply = await SendReceiveAsync(msg, ct, raw => V3MsgId(raw) == msgId).ConfigureAwait(false);
        var parsed = ParseV3(reply);
        if (parsed.Security.Boots != 0 || parsed.Security.Time != 0) SetTime(parsed.Security.Boots, parsed.Security.Time);
        if ((parsed.Flags & FlagAuth) != 0 && auth)
        {
            var copy = (byte[])reply.Clone();
            Array.Clear(copy, parsed.MacOffset, parsed.Security.AuthParams.Length);
            var expect = Hmac(_cred.Auth, _authKey!, copy).AsSpan(0, parsed.Security.AuthParams.Length);
            if (!CryptographicOperations.FixedTimeEquals(expect, parsed.Security.AuthParams))
                throw new SnmpException("The reply failed authentication - wrong auth password or protocol?");
        }
        byte[] scopedReply = parsed.Data;
        if ((parsed.Flags & FlagPriv) != 0)
        {
            if (_privKey is null) throw new SnmpException("The agent encrypted its reply but no privacy password is set.");
            scopedReply = Decrypt(parsed.Data, parsed.Security.PrivParams, parsed.Security.Boots, parsed.Security.Time);
        }
        var r = new Ber.Reader(scopedReply);
        r.Enter();
        r.ReadOctets();
        r.ReadOctets();
        try { return ParsePdu(scopedReply, ref r, reqId); }
        catch (SnmpException e) when (e.Message.StartsWith(NotInTimeWindow)) { throw new SnmpException(NotInTimeWindow); }
    }

    private static int FindZeros(byte[] msg, int n)
    {
        // The auth parameter placeholder: an OCTET STRING of n zero bytes inside msgSecurityParameters.
        for (int i = 0; i + 2 + n <= msg.Length; i++)
            if (msg[i] == SnmpType.OctetString && msg[i + 1] == n && msg.AsSpan(i + 2, n).IndexOfAnyExcept((byte)0) < 0) return i + 2;
        throw new InvalidOperationException("auth placeholder not found");
    }

    private sealed record UsmParams(byte[] EngineId, int Boots, int Time, byte[] User, byte[] AuthParams, byte[] PrivParams);

    private sealed record V3Message(byte Flags, UsmParams Security, int MacOffset, byte[] Data);

    private static int V3MsgId(byte[] raw)
    {
        try
        {
            var r = new Ber.Reader(raw);
            r.Enter();
            if (r.ReadInteger() != 3) return -1;
            r.Enter();
            return (int)r.ReadInteger();
        }
        catch (FormatException) { return -1; }
    }

    private static V3Message ParseV3(byte[] raw)
    {
        var r = new Ber.Reader(raw);
        r.Enter();
        r.ReadInteger();
        r.Enter();
        r.ReadInteger();
        r.ReadInteger();
        byte flags = r.ReadOctets() is { Length: > 0 } f ? f[0] : (byte)0;
        r.ReadInteger();
        var (_, secStart, secLen) = r.Next();
        var s = new Ber.Reader(raw.AsSpan(secStart, secLen));
        s.Enter();
        var engine = s.ReadOctets();
        int boots = (int)s.ReadInteger(), time = (int)s.ReadInteger();
        var user = s.ReadOctets();
        var (_, authStart, authLen) = s.Next();
        var authParams = raw.AsSpan(secStart + authStart, authLen).ToArray();
        var privParams = s.ReadOctets();
        int tlvStart = r.Position;
        var (tag, ds, dl) = r.Next();
        // Encrypted: an OCTET STRING whose content is the ciphertext. Plain: the ScopedPDU SEQUENCE itself, tag and all.
        var data = tag == SnmpType.OctetString ? raw.AsSpan(ds, dl).ToArray() : raw.AsSpan(tlvStart, ds + dl - tlvStart).ToArray();
        return new V3Message(flags, new UsmParams(engine, boots, time, user, authParams, privParams), secStart + authStart, data);
    }

    private static string ReportText(IReadOnlyList<SnmpVarbind> vbs)
    {
        var oid = vbs.FirstOrDefault()?.Oid ?? "";
        return oid switch
        {
            "1.3.6.1.6.3.15.1.1.1.0" => "The agent doesn't support that security level for this user (check auth/priv settings).",
            "1.3.6.1.6.3.15.1.1.2.0" => NotInTimeWindow,
            "1.3.6.1.6.3.15.1.1.3.0" => "Unknown SNMPv3 user name.",
            "1.3.6.1.6.3.15.1.1.4.0" => "Unknown engine ID.",
            "1.3.6.1.6.3.15.1.1.5.0" => "Wrong authentication password or protocol.",
            "1.3.6.1.6.3.15.1.1.6.0" => "Wrong privacy password or protocol (decryption failed on the agent).",
            _ => $"The agent sent a report ({oid}).",
        };
    }

    // ---------- keys (RFC 3414 A.2, RFC 7860 for SHA-2) ----------

    public static int MacLength(SnmpAuth a) => a switch
    {
        SnmpAuth.Md5 or SnmpAuth.Sha1 => 12, SnmpAuth.Sha224 => 16, SnmpAuth.Sha256 => 24, SnmpAuth.Sha384 => 32, SnmpAuth.Sha512 => 48, _ => 0,
    };

    private static byte[] Hash(SnmpAuth a, byte[] data) => a switch
    {
        SnmpAuth.Md5 => MD5.HashData(data),
        SnmpAuth.Sha1 => SHA1.HashData(data),
        SnmpAuth.Sha224 => Sha224(data),
        SnmpAuth.Sha256 => SHA256.HashData(data),
        SnmpAuth.Sha384 => SHA384.HashData(data),
        SnmpAuth.Sha512 => SHA512.HashData(data),
        _ => throw new ArgumentException("no auth"),
    };

    private static byte[] Hmac(SnmpAuth a, byte[] key, byte[] data) => a switch
    {
        SnmpAuth.Md5 => HMACMD5.HashData(key, data),
        SnmpAuth.Sha1 => HMACSHA1.HashData(key, data),
        SnmpAuth.Sha224 => HmacSha224(key, data),
        SnmpAuth.Sha256 => HMACSHA256.HashData(key, data),
        SnmpAuth.Sha384 => HMACSHA384.HashData(key, data),
        SnmpAuth.Sha512 => HMACSHA512.HashData(key, data),
        _ => throw new ArgumentException("no auth"),
    };

    /// <summary>Password → localized key: hash 1 MB of the repeated password, then H(Ku | engineID | Ku).</summary>
    public static byte[] LocalizedKey(SnmpAuth a, string password, byte[] engineId)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        if (pw.Length == 0) throw new SnmpException("SNMPv3 passwords can't be empty.");
        if (pw.Length < 8) throw new SnmpException("SNMPv3 passwords must be at least 8 characters.");
        return LocalizedKey(a, pw, engineId);
    }

    private static byte[] LocalizedKey(SnmpAuth a, byte[] pw, byte[] engineId)
    {
        var buf = new byte[1_048_576];
        for (int i = 0; i < buf.Length; i++) buf[i] = pw[i % pw.Length];
        var ku = Hash(a, buf);
        return Hash(a, [.. ku, .. engineId, .. ku]);
    }

    /// <summary>The privacy key, extended (Blumenthal draft, as net-snmp does) when the cipher needs more bytes than the hash gives.</summary>
    public static byte[] PrivKey(SnmpAuth a, SnmpPriv p, string password, byte[] engineId)
    {
        int need = p switch { SnmpPriv.Des => 16, SnmpPriv.Aes128 => 16, SnmpPriv.Aes192 or SnmpPriv.Aes192Cisco => 24, SnmpPriv.Aes256 or SnmpPriv.Aes256Cisco => 32, _ => 0 };
        var key = LocalizedKey(a, password, engineId);
        bool cisco = p is SnmpPriv.Aes192Cisco or SnmpPriv.Aes256Cisco;
        while (key.Length < need)
            key = cisco ? [.. key, .. LocalizedKey(a, key, engineId)] : [.. key, .. Hash(a, key)];
        return key[..need];
    }

    private (byte[] Data, byte[] Salt) Encrypt(byte[] plain, int boots, int time)
    {
        long salt = Interlocked.Increment(ref _salt);
        if (_cred.Priv == SnmpPriv.Des)
        {
            var saltBytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(saltBytes, boots);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(saltBytes.AsSpan(4), (int)salt);
            var iv = new byte[8];
            for (int i = 0; i < 8; i++) iv[i] = (byte)(_privKey![8 + i] ^ saltBytes[i]);
            var padded = new byte[(plain.Length + 7) / 8 * 8];
            plain.CopyTo(padded, 0);
#pragma warning disable SYSLIB0021, CA5351 // DES is what the agent asked for; it's the protocol, not our choice
            using var des = DES.Create();
            des.Key = _privKey![..8];
            return (des.EncryptCbc(padded, iv, PaddingMode.None), saltBytes);
#pragma warning restore SYSLIB0021, CA5351
        }
        var s = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(s, salt);
        return (AesCfb(_privKey!, AesIv(boots, time, s), plain, encrypt: true), s);
    }

    private byte[] Decrypt(byte[] cipher, byte[] salt, int boots, int time)
    {
        if (_cred.Priv == SnmpPriv.Des)
        {
            var iv = new byte[8];
            for (int i = 0; i < 8; i++) iv[i] = (byte)(_privKey![8 + i] ^ salt[i]);
#pragma warning disable SYSLIB0021, CA5351
            using var des = DES.Create();
            des.Key = _privKey![..8];
            return des.DecryptCbc(cipher, iv, PaddingMode.None);
#pragma warning restore SYSLIB0021, CA5351
        }
        return AesCfb(_privKey!, AesIv(boots, time, salt), cipher, encrypt: false);
    }

    /// <summary>AES in 128-bit CFB mode over any length (the last block may be partial, as RFC 3826 requires) -
    /// .NET's own CFB only takes whole blocks without padding.</summary>
    public static byte[] AesCfb(byte[] key, byte[] iv, byte[] input, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var output = new byte[input.Length];
        var feedback = (byte[])iv.Clone();
        var stream = new byte[16];
        for (int off = 0; off < input.Length; off += 16)
        {
            aes.EncryptEcb(feedback, stream, PaddingMode.None);
            int n = Math.Min(16, input.Length - off);
            for (int i = 0; i < n; i++) output[off + i] = (byte)(input[off + i] ^ stream[i]);
            if (n == 16) Array.Copy(encrypt ? output : input, off, feedback, 0, 16);
        }
        return output;
    }

    private static byte[] AesIv(int boots, int time, byte[] salt)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(iv, boots);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(4), time);
        salt.AsSpan(0, 8).CopyTo(iv.AsSpan(8));
        return iv;
    }

    // .NET has no SHA-224; it's SHA-256 with different initial values, truncated to 28 bytes.
    private static byte[] Sha224(byte[] data) => Sha224Impl.Hash(data);
    private static byte[] HmacSha224(byte[] key, byte[] data)
    {
        const int block = 64;
        if (key.Length > block) key = Sha224(key);
        var k = new byte[block];
        key.CopyTo(k, 0);
        var ipad = k.Select(b => (byte)(b ^ 0x36)).ToArray();
        var opad = k.Select(b => (byte)(b ^ 0x5C)).ToArray();
        return Sha224([.. opad, .. Sha224([.. ipad, .. data])]);
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>SHA-224 (FIPS 180-4): SHA-256's compression with its own initial hash values, output truncated.</summary>
internal static class Sha224Impl
{
    private static readonly uint[] K =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
        0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
        0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
        0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];

    public static byte[] Hash(byte[] data)
    {
        uint[] h = [0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939, 0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4];
        long bitLen = (long)data.Length * 8;
        int padLen = (int)((data.Length + 9 + 63) / 64 * 64);
        var msg = new byte[padLen];
        data.CopyTo(msg, 0);
        msg[data.Length] = 0x80;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(msg.AsSpan(padLen - 8), bitLen);
        var w = new uint[64];
        for (int off = 0; off < padLen; off += 64)
        {
            for (int i = 0; i < 16; i++) w[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(off + i * 4));
            for (int i = 16; i < 64; i++)
            {
                uint s0 = uint.RotateRight(w[i - 15], 7) ^ uint.RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = uint.RotateRight(w[i - 2], 17) ^ uint.RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (int i = 0; i < 64; i++)
            {
                uint S1 = uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = hh + S1 + ch + K[i] + w[i];
                uint S0 = uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = S0 + maj;
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }
        var outp = new byte[28];
        for (int i = 0; i < 7; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(outp.AsSpan(i * 4), h[i]);
        return outp;
    }
}
