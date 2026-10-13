using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Voice.Assistant;

/// <summary>The language model that understands what the engineer meant. Streams its answer so the app can act early.</summary>
public interface IVoiceBrain : IDisposable
{
    string Name { get; }
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

/// <summary>Any OpenAI-compatible server (LM Studio, Ollama, vLLM, or a cloud API with a key), streamed.</summary>
public sealed class HttpVoiceBrain(HttpClient http, LlmSettings settings) : IVoiceBrain
{
    public string Name => $"{settings.Model} at {settings.BaseUrl}";

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new
        {
            model = settings.Model,
            temperature = 0.1,
            stream = true,
            max_tokens = 600,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/chat/completions") { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"{settings.BaseUrl} returned {(int)response.StatusCode}: {(err.Length > 300 ? err[..300] : err)}");
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        bool sawEvent = false;
        var whole = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            whole.AppendLine(line);
            if (!line.StartsWith("data:")) continue;
            sawEvent = true;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            string? piece = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    piece = c.GetString();
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
        // A server that ignored "stream": true answered with one ordinary JSON body.
        if (!sawEvent && whole.Length > 0)
        {
            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(whole.ToString());
                content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(content)) yield return content;
        }
    }

    public void Dispose() { }
}

/// <summary>
/// The built-in brain: a small multilingual model (Qwen3 4B Instruct, ~2.5 GB, downloaded once) running on this
/// computer through llama.cpp - nothing leaves the machine. Two things keep it quick on an ordinary laptop CPU:
/// the long instructions are evaluated once and reused for every request (forked from a cached conversation), and
/// a grammar forces the short tagged-line answer format so no tokens are wasted.
/// </summary>
public sealed class LocalVoiceBrain : IVoiceBrain
{
    public const string ModelFileName = "qwen3-4b-instruct-2507-q4_k_m.gguf";
    public const string ModelUrl = "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    public const long ApproxModelBytes = 2_497_281_120;

    public static string ModelPath => Path.Combine(WhisperTranscriber.ModelDirectory, ModelFileName);
    public static bool ModelInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 2_000_000_000;

    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private readonly BatchedExecutor _executor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Conversation? _prefix;
    private LLamaToken[] _prefixTokens = [];

    public string Name => "built-in model (Qwen3 4B, offline)";

    public LocalVoiceBrain(string? modelPath = null, int? threads = null)
    {
        int t = threads ?? DefaultThreads();
        _params = new ModelParams(modelPath ?? ModelPath)
        {
            ContextSize = 6144,
            GpuLayerCount = 0,
            Threads = t,
            BatchThreads = Math.Max(t, Math.Min(Environment.ProcessorCount, 32)),
            BatchSize = 512,
        };
        _weights = LLamaWeights.LoadFromFile(_params);
        _executor = new BatchedExecutor(_weights, _params, null);
    }

    /// <summary>Generation is memory-bound: a few more threads than physical cores helps, hyper-threads beyond that don't.</summary>
    public static int DefaultThreads() => Math.Clamp(Environment.ProcessorCount / 2, 2, 16);

    /// <summary>Evaluates the fixed instructions ahead of time so the first request is as fast as the rest.</summary>
    public async Task WarmUpAsync(string systemPrompt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsurePrefixAsync(Tokenize(Template([new ChatMessage("system", systemPrompt)], addAssistant: false)), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Conversation? conversation = null;
        try
        {
            var all = Tokenize(Template(messages, addAssistant: true));
            var system = Tokenize(Template(messages.Take(1).ToList(), addAssistant: false));
            if (StartsWith(all, system))
            {
                await EnsurePrefixAsync(system, ct).ConfigureAwait(false);
                conversation = _prefix!.Fork();
                conversation.Prompt(all.AsSpan(system.Length));
            }
            else
            {
                conversation = _executor.Create();
                conversation.Prompt(all.AsSpan());
            }
            using var sampler = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopK = 20,
                Grammar = new Grammar(VoicePlanner.Gbnf, "root"),
            };
            var decoder = new StreamingTokenDecoder(_executor.Context);
            for (int i = 0; i < 700; i++)
            {
                ct.ThrowIfCancellationRequested();
                await InferUntilReadyAsync(conversation, ct).ConfigureAwait(false);
                var token = sampler.Sample(_executor.Context.NativeHandle, conversation.GetSampleIndex());
                if (token.IsEndOfGeneration(_weights.Vocab)) break;
                decoder.Add(token);
                var text = decoder.Read();
                if (text.Length > 0) yield return text;
                conversation.Prompt(token);
            }
        }
        finally
        {
            conversation?.Dispose();
            _gate.Release();
        }
    }

    private async Task EnsurePrefixAsync(LLamaToken[] system, CancellationToken ct)
    {
        if (_prefix is not null && _prefixTokens.AsSpan().SequenceEqual(system)) return;
        _prefix?.Dispose();
        _prefix = _executor.Create();
        _prefix.Prompt(system.AsSpan());
        await InferUntilReadyAsync(_prefix, ct).ConfigureAwait(false);
        _prefixTokens = system;
    }

    /// <summary>A prompt longer than one batch is decoded over several Infer calls.</summary>
    private async Task InferUntilReadyAsync(Conversation conversation, CancellationToken ct)
    {
        do
        {
            var result = await _executor.Infer(ct).ConfigureAwait(false);
            if (result != DecodeResult.Ok) throw new InvalidOperationException($"The built-in model failed to decode ({result}).");
        } while (conversation.RequiresInference);
    }

    private string Template(IReadOnlyList<ChatMessage> messages, bool addAssistant)
    {
        var template = new LLamaTemplate(_weights) { AddAssistant = addAssistant };
        foreach (var m in messages) template.Add(m.Role, m.Content);
        return PromptTemplateTransformer.ToModelPrompt(template);
    }

    private LLamaToken[] Tokenize(string text) => _weights.Tokenize(text, false, true, Encoding.UTF8);

    private static bool StartsWith(LLamaToken[] all, LLamaToken[] prefix) =>
        prefix.Length > 0 && all.Length > prefix.Length && all.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    /// <summary>Downloads the model once (resumable .part file, renamed only when complete).</summary>
    public static Task DownloadModelAsync(IProgress<long>? progress = null, CancellationToken ct = default) =>
        ModelDownloader.DownloadAsync(ModelUrl, ModelPath, progress, ct);

    public void Dispose()
    {
        _prefix?.Dispose();
        _executor.Dispose();
        _weights.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Plain HTTPS download with resume: models are large, connections drop, and a half file must never be used.</summary>
public static class ModelDownloader
{
    public static async Task DownloadAsync(string url, string target, IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored the range: start over
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long total = have;
            progress?.Report(total);
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                progress?.Report(total);
            }
        }
        File.Move(part, target, overwrite: true);
    }
}
