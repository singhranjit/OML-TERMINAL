using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Text.RegularExpressions;
using Sapi = System.Speech.Recognition;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Runs the network command grammar (<see cref="NetworkSpeechGrammar"/>) over one recorded utterance. Unlike free
/// dictation, a grammar can't glue an IP address to its mask or invent octets - it either finds a command that fits
/// (high confidence) or returns nothing. Used alongside Whisper, which handles everything outside the grammar.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GrammarRecognizer : IDisposable
{
    private readonly Sapi.SpeechRecognitionEngine _engine;
    private readonly object _lock = new();

    /// <summary>True when Windows has an English speech recognizer (every English Windows install does).</summary>
    public static bool IsAvailable()
    {
        try { return Sapi.SpeechRecognitionEngine.InstalledRecognizers().Any(r => r.Culture.TwoLetterISOLanguageName == "en"); }
        catch { return false; }
    }

    public GrammarRecognizer()
    {
        var info = Sapi.SpeechRecognitionEngine.InstalledRecognizers()
                       .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                   ?? throw new InvalidOperationException("No English speech recognizer installed.");
        _engine = new Sapi.SpeechRecognitionEngine(info);
        _engine.LoadGrammar(NetworkSpeechGrammar.Build());
    }

    public (string? Text, float Confidence) Recognize(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        lock (_lock)
        {
            _engine.SetInputToAudioStream(new MemoryStream(pcm), new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var result = _engine.Recognize(TimeSpan.FromSeconds(30));
            _engine.SetInputToNull();
            return result is null ? (null, 0f) : (result.Text, result.Confidence);
        }
    }

    public void Dispose() => _engine.Dispose();
}

public sealed record SpeechDecision(string Text, bool Trusted, string Source, string? Note = null);

/// <summary>
/// Picks between the grammar's and Whisper's reading of the same utterance. The grammar is exact about structure
/// (where an address ends and the mask starts) but can still mishear a digit; Whisper is great with words but mangles
/// long numbers. So: use the grammar when it's confident, but only trust its addresses if Whisper heard the same
/// digits - disagreement means the user must look before anything is sent.
/// </summary>
public static partial class HybridDecision
{
    public const float GrammarThreshold = 0.7f;

    /// <summary>
    /// Without Whisper there's no second opinion to cross-check numbers against, so the grammar alone must be very
    /// sure before anything is trusted; out-of-grammar speech isn't recognized at all (never guessed).
    /// </summary>
    public const float GrammarOnlyThreshold = 0.9f;

    public static SpeechDecision? ChooseGrammarOnly(string? grammarText, float grammarConfidence)
    {
        if (string.IsNullOrWhiteSpace(grammarText) || grammarConfidence < GrammarThreshold) return null;
        return grammarConfidence >= GrammarOnlyThreshold
            ? new SpeechDecision(grammarText, true, "grammar")
            : new SpeechDecision(grammarText, false, "grammar", "Not fully sure what was said - check before sending.");
    }

    public static SpeechDecision Choose(string? grammarText, float grammarConfidence, string whisperText)
    {
        if (string.IsNullOrWhiteSpace(grammarText) || grammarConfidence < GrammarThreshold)
            return new SpeechDecision(whisperText, true, "whisper");

        var grammarIps = IpAddresses(SpokenNormalizer.Normalize(grammarText));
        if (grammarIps.Count == 0) return new SpeechDecision(grammarText, true, "grammar");

        var whisperDigits = new string(whisperText.Where(char.IsDigit).ToArray());
        var disagreeing = grammarIps.Where(ip => !whisperDigits.Contains(ip.Replace(".", ""))).ToList();
        return disagreeing.Count == 0
            ? new SpeechDecision(grammarText, true, "grammar+whisper")
            : new SpeechDecision(grammarText, false, "grammar",
                $"The two recognizers heard different addresses ({string.Join(", ", disagreeing)} vs \"{whisperText}\") - check before sending.");
    }

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b")]
    private static partial Regex Ipv4();

    /// <summary>Host addresses only - masks (255.x, 0.0.0.0) are validated for contiguity instead.</summary>
    private static List<string> IpAddresses(string text) =>
        Ipv4().Matches(text).Select(m => m.Value).Where(ip => !CliValidator.IsMask(ip)).ToList();
}
