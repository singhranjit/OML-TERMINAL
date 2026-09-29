using OmlTerminal.Core.Voice;
using Windows.Media.SpeechRecognition;

namespace OmlTerminal.App.Services;

public static class SpeechInputs
{
    /// <summary>HRESULT Windows returns when "Online speech recognition" is off in Settings > Privacy > Speech.</summary>
    public const int PrivacyPolicyNotAccepted = unchecked((int)0x80045509);

    public static bool IsPrivacyError(Exception ex) => ex.HResult == PrivacyPolicyNotAccepted
        || ex.Message.Contains("privacy policy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Windows' modern speech service: better accuracy, but requires "Online speech recognition" to be on.</summary>
public sealed class OnlineSpeechInput : ISpeechInput
{
    private readonly SpeechRecognizer _recognizer = new();
    private bool _prepared;

    public string EngineName => "Windows online speech";
    public event Action<string>? Hypothesis;
    public event Action<string, bool>? Recognized;
    public event Action<double>? Level { add { } remove { } } // the WinRT recognizer doesn't expose input level
    public event Action<string?>? Stopped;

    public async Task StartAsync()
    {
        if (_prepared) { await _recognizer.ContinuousRecognitionSession.StartAsync(); return; }
        _prepared = true;
        _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        var compiled = await _recognizer.CompileConstraintsAsync();
        if (compiled.Status != SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException($"Online speech recognition isn't available ({compiled.Status}).");
        _recognizer.HypothesisGenerated += (_, a) => Hypothesis?.Invoke(a.Hypothesis.Text);
        _recognizer.ContinuousRecognitionSession.ResultGenerated += (_, a) =>
            Recognized?.Invoke(a.Result.Text, a.Result.Confidence is SpeechRecognitionConfidence.High or SpeechRecognitionConfidence.Medium);
        _recognizer.ContinuousRecognitionSession.Completed += (_, a) =>
            Stopped?.Invoke(a.Status == SpeechRecognitionResultStatus.Success ? null : a.Status.ToString());
        await _recognizer.ContinuousRecognitionSession.StartAsync();
    }

    public async Task StopAsync()
    {
        try { await _recognizer.ContinuousRecognitionSession.StopAsync(); } catch { }
    }

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>
/// Microphone → utterance detection → two recognizers on each finished phrase, all on this PC:
///  - the network command grammar: exact structure (where an address ends and its mask starts), or no match at all;
///  - Whisper (once its model is downloaded): free-form speech, accents, anything outside the grammar.
/// <see cref="HybridDecision"/> combines them; addresses are only trusted when both heard the same digits.
/// </summary>
public sealed class HybridSpeechInput : ISpeechInput
{
    private static WhisperTranscriber? _whisper;                  // ~150 MB in memory: load once per app
    private static GrammarRecognizer? _grammar;
    private static readonly object SharedLock = new();

    private readonly UtteranceDetector _detector = new();
    private NAudio.Wave.WaveIn? _mic;
    private readonly CancellationTokenSource _cts = new();

    public string EngineName => WhisperTranscriber.ModelInstalled ? "Whisper + command grammar, offline" : "command grammar, offline";
    public event Action<string>? Hypothesis;
    public event Action<string, bool>? Recognized;
    public event Action<double>? Level;
    public event Action<string?>? Stopped;

    public static bool IsAvailable => GrammarRecognizer.IsAvailable();

    private static WhisperTranscriber? Whisper()
    {
        if (!WhisperTranscriber.ModelInstalled) return null;
        lock (SharedLock) return _whisper ??= new WhisperTranscriber();
    }

    private static GrammarRecognizer Grammar()
    {
        lock (SharedLock) return _grammar ??= new GrammarRecognizer();
    }

    public HybridSpeechInput()
    {
        _detector.Level += l => Level?.Invoke(l);
        _detector.SpeakingChanged += speaking => { if (speaking) Hypothesis?.Invoke("(hearing you...)"); };
        _detector.Utterance += samples => _ = RecognizeAsync(samples);
    }

    private async Task RecognizeAsync(float[] samples)
    {
        try
        {
            Hypothesis?.Invoke("(recognizing...)");
            var grammarTask = Task.Run(() => Grammar().Recognize(samples));
            var whisper = Whisper();
            var whisperTask = whisper is null ? Task.FromResult("") : Task.Run(() => whisper.TranscribeAsync(samples, _cts.Token));
            var (grammarText, confidence) = await grammarTask;
            var whisperText = await whisperTask;

            var decision = whisper is null
                ? HybridDecision.ChooseGrammarOnly(grammarText, confidence)
                : HybridDecision.Choose(grammarText, confidence, whisperText);
            if (decision is null || decision.Text.Trim().Length == 0)
            {
                Hypothesis?.Invoke(whisper is null
                    ? "(not a command I know - download the Whisper model for free-form speech)"
                    : "(didn't catch that)");
                return;
            }
            Recognized?.Invoke(decision.Text, decision.Trusted);
            if (decision.Note is not null) Hypothesis?.Invoke($"({decision.Note})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Stopped?.Invoke(ex.Message); }
    }

    public Task StartAsync()
    {
        if (NAudio.Wave.WaveIn.DeviceCount == 0)
            throw new InvalidOperationException(
                "No microphone found. Plug one in - or, over Remote Desktop, allow it in the RDP client: Local Resources > Remote audio > Settings > Record from this computer.");
        _ = Task.Run(() => { Grammar(); Whisper(); }); // warm both engines while the user starts talking
        _mic?.Dispose();                                   // restarting after a stop: release the previous capture device
        _mic = new NAudio.Wave.WaveIn
        {
            WaveFormat = new NAudio.Wave.WaveFormat(UtteranceDetector.SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _mic.DataAvailable += (_, e) =>
        {
            var samples = new float[e.BytesRecorded / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            _detector.Push(samples);
        };
        _mic.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception?.Message);
        _mic.StartRecording();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _detector.Flush(); // recognize whatever was said before the stop click
        _mic?.StopRecording();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mic?.StopRecording(); } catch { }
        _mic?.Dispose();
        _mic = null;
    }
}
