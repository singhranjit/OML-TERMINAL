using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OmlTerminal.Core.Models;

/// <summary>Talks to an OML Labs instance's REST API to resolve a lab-launch link into its current node list.</summary>
public sealed class OmlLabsClient(OmlHostCertStore? certStore = null) : IDisposable
{
    private readonly OmlHostCertStore _certStore = certStore ?? new OmlHostCertStore();
    private readonly PendingCert _pending = new();
    private HttpClient? _client;
    private string? _clientHost;

    /// <summary>Throws OmlUntrustedCertificateException on first contact with a host until the caller trusts it via OmlHostCertStore and retries.</summary>
    public async Task<IReadOnlyList<OmlLabNode>> GetNodesAsync(OmlLabLink link, CancellationToken ct = default)
    {
        var client = GetClient(link.OmlHost);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{link.OmlHost}/api/labs/{Uri.EscapeDataString(link.LabId)}/nodes/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", link.Token);
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (_pending.Thumbprint is not null)
        {
            // Exceptions thrown inside ServerCertificateCustomValidationCallback don't propagate reliably through
            // SslStream, so the callback just records the untrusted cert and fails validation normally; the real,
            // typed exception is raised here instead, once we're safely outside the TLS handshake.
            var (host, thumb, subj) = (_pending.Host, _pending.Thumbprint, _pending.Subject);
            _pending.Thumbprint = null;
            throw new OmlUntrustedCertificateException(host, thumb, subj ?? "");
        }
        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            throw new HttpRequestException($"OML Labs returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var nodes = new List<OmlLabNode>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            nodes.Add(new OmlLabNode(
                Id: Str(el, "id") ?? "",
                Name: Str(el, "name") ?? "(unnamed)",
                NodeType: Str(el, "node_type") ?? "",
                DeviceTemplate: Str(el, "device_template") ?? "",
                Status: Str(el, "status") ?? "unknown",
                ConsoleType: ParseConsoleType(Str(el, "console_type")),
                ConsolePort: IntOrNull(el, "console_port"),
                VncPort: IntOrNull(el, "vnc_port"),
                VncWsPort: IntOrNull(el, "vnc_ws_port"),
                RdpPort: IntOrNull(el, "rdp_port")));
        }
        return nodes;
    }

    private static OmlConsoleType ParseConsoleType(string? s) => s?.ToLowerInvariant() switch
    {
        "telnet" => OmlConsoleType.Telnet,
        "vnc" => OmlConsoleType.Vnc,
        "rdp" => OmlConsoleType.Rdp,
        "ssh" => OmlConsoleType.Ssh,
        _ => OmlConsoleType.None,
    };

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? IntOrNull(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "(no body)"; }
    }

    private sealed class PendingCert
    {
        public string Host = "";
        public string? Thumbprint;
        public string? Subject;
    }

    private HttpClient GetClient(string omlHost)
    {
        var host = new Uri(omlHost).Host;
        _pending.Host = host;
        if (_client is not null && _clientHost == host) return _client;
        _client?.Dispose();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (cert is null) return false;
                var thumbprint = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
                if (_certStore.IsTrusted(host, thumbprint)) return true;
                _pending.Thumbprint = thumbprint;
                _pending.Subject = cert.Subject;
                return false;
            },
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _clientHost = host;
        return _client;
    }

    public void Dispose() => _client?.Dispose();
}
