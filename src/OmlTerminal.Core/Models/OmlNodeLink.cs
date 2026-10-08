using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Models;

public sealed record OmlLinkResult(SessionProfile? Profile, string? Label, string? Source, string? Error)
{
    public bool Ok => Profile is not null;
}

/// <summary>
/// Parses oml-terminal://connect?data=&lt;base64url JSON&gt; links handed over by a lab platform.
/// The payload is untrusted input: version, required fields, sizes, character sets and expiry are all checked before a profile exists.
/// </summary>
public static partial class OmlNodeLink
{
    public const string Scheme = "oml-terminal";
    private const int MaxLinkLength = 4096;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    [GeneratedRegex(@"^[A-Za-z0-9._:\-\[\]]{1,253}$")]
    private static partial Regex HostRegex();

    public static OmlLinkResult Parse(string? link, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(link)) return Fail("Empty link.");
        link = link.Trim();
        if (link.Length > MaxLinkLength) return Fail("Link is too long.");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
            return Fail($"Not an {Scheme}:// link.");
        if (!uri.Host.Equals("connect", StringComparison.OrdinalIgnoreCase)) return Fail("Unsupported action (expected connect).");

        string? data = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv[0] == "data" && kv.Length == 2) data = Uri.UnescapeDataString(kv[1]);
        }
        if (string.IsNullOrEmpty(data)) return Fail("Link has no data.");

        byte[] json;
        try { json = FromBase64Url(data); }
        catch (FormatException) { return Fail("Link data is not valid base64url."); }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return Fail("Link data is not valid JSON."); }
        if (root.ValueKind != JsonValueKind.Object) return Fail("Link data must be a JSON object.");

        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 1)
            return Fail("Unsupported link version.");

        var protocolText = Str(root, "protocol");
        ProtocolKind protocol;
        if (protocolText?.ToLowerInvariant() == "ssh") protocol = ProtocolKind.Ssh;
        else if (protocolText?.ToLowerInvariant() == "telnet") protocol = ProtocolKind.Telnet;
        else return Fail("Protocol must be ssh or telnet.");

        var host = Str(root, "host");
        if (host is null || !HostRegex().IsMatch(host)) return Fail("Missing or invalid host.");

        int port = SessionProfile.DefaultPortFor(protocol);
        if (root.TryGetProperty("port", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out port) || port is < 1 or > 65535) return Fail("Invalid port.");
        }

        var source = Clean(Str(root, "source"), 64);
        if (string.IsNullOrEmpty(source)) return Fail("Link does not say where it came from (source).");

        if (!TryTime(root, "issuedAt", out var issued)) return Fail("Missing or invalid issuedAt.");
        if (issued > now + ClockSkew) return Fail("Link was issued in the future.");
        DateTimeOffset expires = issued + DefaultLifetime;
        if (root.TryGetProperty("expiresAt", out var e) && e.ValueKind != JsonValueKind.Null)
        {
            if (!TryTime(root, "expiresAt", out expires)) return Fail("Invalid expiresAt.");
        }
        if (expires <= now) return Fail("This link has expired. Request a new one from the lab.");

        var label = Clean(Str(root, "label"), 64);
        var profile = new SessionProfile
        {
            Name = string.IsNullOrEmpty(label) ? host : label,
            Folder = "OML Labs",
            Protocol = protocol,
            Host = host,
            Port = port,
            Username = Clean(Str(root, "username"), 128) ?? "",
            Password = Str(root, "password") is { Length: <= 256 } pw ? pw : "",
            IsLabNode = true,
        };
        return profile.Validate().Count == 0 ? new OmlLinkResult(profile, profile.Name, source, null) : Fail("Link describes an invalid session.");
    }

    private static OmlLinkResult Fail(string error) => new(null, null, null, error);

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? Clean(string? s, int max)
    {
        if (s is null) return null;
        var sb = new StringBuilder();
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        var t = sb.ToString().Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static bool TryTime(JsonElement o, string name, out DateTimeOffset value)
    {
        value = default;
        if (!o.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
        {
            value = n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
            return true;
        }
        return el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out value);
    }

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    /// <summary>Builds a link (used by tests and by anything that wants to hand a session to this app).</summary>
    public static string Build(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Scheme}://connect?data={b64}";
    }
}
