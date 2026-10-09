using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OmlTerminal.Core.Models;

/// <summary>What omllabs.com says the newest release is (assets/oml-terminal-latest.json, published with each release).</summary>
public sealed record ReleaseInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("notes")] string Notes);

/// <summary>
/// The opt-in "a new version is available" check: one small HTTPS request to omllabs.com, at most once a day. It
/// sends nothing but an ordinary request for a static file - no identifiers, no usage data.
/// </summary>
public static class UpdateChecker
{
    /// <summary>The release feed; OML_TERMINAL_UPDATE_FEED points it elsewhere (testing a release before it's published).</summary>
    public static string FeedUrl => Environment.GetEnvironmentVariable("OML_TERMINAL_UPDATE_FEED") is { Length: > 0 } custom
        ? custom
        : "https://omllabs.com/assets/oml-terminal-latest.json";
    public const string DownloadsUrl = "https://omllabs.com/downloads.html";

    /// <summary>The newer release, or null when this build is current (or the check couldn't be made).</summary>
    public static async Task<ReleaseInfo?> CheckAsync(Version current, CancellationToken ct = default, HttpClient? http = null)
    {
        try
        {
            using var own = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) } : null;
            var client = http ?? own!;
            var info = await client.GetFromJsonAsync<ReleaseInfo>(FeedUrl, ct).ConfigureAwait(false);
            return info is not null && IsNewer(info.Version, current) ? info : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null; // offline, blocked or a bad file: say nothing rather than nag
        }
    }

    /// <summary>"1.0.0" vs 0.3.8 → true. Pre-release suffixes ("1.1.0-beta") are ignored for the comparison.</summary>
    public static bool IsNewer(string published, Version current)
    {
        var core = published.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var v)) return false;
        static Version Norm(Version x) => new(x.Major, x.Minor, Math.Max(0, x.Build), Math.Max(0, x.Revision));
        return Norm(v) > Norm(current);
    }

    /// <summary>True when the last check is more than a day old (or never happened).</summary>
    public static bool Due(DateTime? lastCheckUtc, DateTime nowUtc) => lastCheckUtc is null || nowUtc - lastCheckUtc.Value > TimeSpan.FromHours(23);
}
