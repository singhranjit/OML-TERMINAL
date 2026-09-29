using System.Text;
using System.Text.Json;

namespace OmlTerminal.Core.Models;

/// <summary>
/// Parses the "launch a whole lab" form of an oml-terminal:// link: {"v":1,"kind":"lab","omlHost":...,"labId":...,
/// "labName":...,"token":...,"source":...}. Distinct from OmlNodeLink, which carries one node's connection
/// details directly; a lab link instead carries a short-lived bearer token used to ask omlHost for the lab's
/// current node list, since node ports are assigned only once a VM is actually running.
///
/// The outer envelope's own issuedAt/expiresAt fields have been observed with expiresAt before issuedAt on real
/// traffic from OML Labs, so they are not used to reject the link; the inner token's own signature and exp are
/// authoritative and are enforced by omlHost on every API call anyway.
/// </summary>
public static class OmlLabLinkParser
{
    private const int MaxLinkLength = 4096;

    public static OmlLabLinkResult Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return Fail("Empty link.");
        link = link.Trim();
        if (link.Length > MaxLinkLength) return Fail("Link is too long.");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(OmlNodeLink.Scheme, StringComparison.OrdinalIgnoreCase))
            return Fail($"Not an {OmlNodeLink.Scheme}:// link.");
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

        if (Str(root, "kind") is not "lab") return Fail("Not a lab-launch link.");

        var omlHost = Str(root, "omlHost");
        if (string.IsNullOrWhiteSpace(omlHost) || !Uri.TryCreate(omlHost, UriKind.Absolute, out var hostUri)
            || hostUri.Scheme is not ("http" or "https"))
            return Fail("Missing or invalid omlHost.");

        var labId = Str(root, "labId");
        if (string.IsNullOrWhiteSpace(labId)) return Fail("Missing labId.");

        var token = Str(root, "token");
        if (string.IsNullOrWhiteSpace(token)) return Fail("Missing token.");

        var labName = Clean(Str(root, "labName"), 128) ?? labId;
        var source = Clean(Str(root, "source"), 64) ?? "";

        return new OmlLabLinkResult(new OmlLabLink(omlHost.TrimEnd('/'), labId, labName, token, source), null);
    }

    private static OmlLabLinkResult Fail(string error) => new(null, error);

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

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}
