using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OmlTerminal.Core.Copilot;

public sealed record LlmSettings(string BaseUrl, string Model, string ApiKey, double Temperature = 0.2);

public sealed record ChatMessage(string Role, string Content);

/// <summary>A minimal OpenAI-compatible chat-completions client - works with LM Studio, Ollama's OpenAI-compat
/// mode, or any other local server that speaks the same /chat/completions shape. No streaming in this first pass:
/// the copilot loop only needs one full reply per turn anyway.</summary>
public sealed class LlmClient(HttpClient http)
{
    public async Task<string> CompleteAsync(LlmSettings settings, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var body = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, CombineUrl(settings.BaseUrl, "/chat/completions"))
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{settings.BaseUrl} returned {(int)response.StatusCode}: {Truncate(text, 300)}");

        using var doc = JsonDocument.Parse(text);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) throw new InvalidOperationException("Model returned no choices.");
        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string CombineUrl(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
