using System.Text;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Persistence;
using Whisper.net;
using Whisper.net.Ggml;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Offline speech-to-text with OpenAI's Whisper (via whisper.cpp) - far more accurate than Windows' built-in
/// recognizer, especially with accents, and nothing leaves the PC. The prompt primes it with network vocabulary so it
/// writes "VLAN 20" and "GigabitEthernet0/0" instead of "the land 20". Needs the model file once (~148 MB).
/// </summary>
public sealed partial class WhisperTranscriber : IDisposable
{
    public const string ModelFileName = "ggml-base.en.bin";
    public const long ApproxModelBytes = 147_964_211;

    /// <summary>Vocabulary priming - Whisper conditions on this text as if it had just been said.</summary>
    public const string NetworkPrompt =
        "Cisco switch and router CLI. configure terminal. interface GigabitEthernet0/0. interface Loopback10. ip address 10.0.0.1 255.255.255.0. " +
        "ip address 192.168.1.1/24. no shutdown. shutdown. VLAN 20. switchport mode access. switchport access VLAN 20. " +
        "switchport mode trunk. show running-config. show ip interface brief. show VLAN brief. show ip route. " +
        "default route via 10.0.0.254. hostname CORE-SW1. write memory. exit. end. enable.";

    private readonly WhisperFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string ModelDirectory => Path.Combine(AppPaths.DataDirectory, "models");
    public static string ModelPath => Path.Combine(ModelDirectory, ModelFileName);
    public static bool ModelInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 100_000_000;

    public WhisperTranscriber(string? modelPath = null)
    {
        _factory = WhisperFactory.FromPath(modelPath ?? ModelPath);
    }

    /// <summary>
    /// Whisper's encoder always processes a 30 s window (1500 audio frames), even for a 2 s command - most of the cost.
    /// Sizing the audio context to the utterance (50 frames per second, plus margin) makes short commands several
    /// times faster with no practical accuracy loss. Greedy decoding and every core do the rest.
    /// </summary>
    private WhisperProcessor CreateProcessor(double seconds)
    {
        int audioContext = Math.Clamp((int)Math.Ceiling(seconds * 50) + 128, 256, 1500);
        return _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(NetworkPrompt)
            .WithNoContext()
            .WithSingleSegment()
            .WithGreedySamplingStrategy()
            .WithAudioContextSize(audioContext)
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
            .Build();
    }

    /// <summary>Downloads the base English model from Hugging Face (the official whisper.cpp model host).
    /// Written to a .part file and renamed only when complete, so an interrupted download is never used.</summary>
    public static async Task DownloadModelAsync(IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelDirectory);
        var part = ModelPath + ".part";
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, QuantizationType.NoQuantization, ct).ConfigureAwait(false))
        await using (var target = File.Create(part))
        {
            var buffer = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                progress?.Report(total);
            }
        }
        File.Move(part, ModelPath, overwrite: true);
    }

    /// <summary>Transcribes one utterance of 16 kHz mono samples. Calls are serialized (one model, one context).</summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var processor = CreateProcessor(samples.Length / 16000.0);
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false)) sb.Append(segment.Text);
            return Clean(sb.ToString());
        }
        finally { _gate.Release(); }
    }

    public async Task<string> TranscribeWaveAsync(Stream wave, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var processor = CreateProcessor(30);
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wave, ct).ConfigureAwait(false)) sb.Append(segment.Text);
            return Clean(sb.ToString());
        }
        finally { _gate.Release(); }
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex NonSpeechTags();

    /// <summary>Drops Whisper's non-speech markers ("[BLANK_AUDIO]", "(keyboard clicking)") and whitespace.</summary>
    public static string Clean(string text)
    {
        var t = NonSpeechTags().Replace(text, " ").Trim();
        // Whisper sometimes repeats a short phrase ("configure terminal. configure terminal.") - that would send the
        // command twice, so drop a sentence identical to the one before it.
        var sentences = SentenceSplit().Split(t).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var kept = new List<string>();
        foreach (var s in sentences)
            if (kept.Count == 0 || !string.Equals(kept[^1].TrimEnd('.', '!', '?'), s.TrimEnd('.', '!', '?'), StringComparison.OrdinalIgnoreCase)) kept.Add(s);
        return string.Join(' ', kept);
    }

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Za-z])")]
    private static partial Regex SentenceSplit();

    public void Dispose()
    {
        _factory.Dispose();
        _gate.Dispose();
    }
}
