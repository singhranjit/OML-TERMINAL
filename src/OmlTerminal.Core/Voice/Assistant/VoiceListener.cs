using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmlTerminal.Core.Voice.Assistant;

/// <summary>A microphone delivering 16 kHz mono samples. Events fire on a background thread.</summary>
public interface IAudioCapture : IDisposable
{
    string Name { get; }
    event Action<float[]>? Samples;
    event Action<string?>? Stopped;
    void Start();
    void Stop();
}

/// <summary>
/// Microphone capture on Linux and macOS through whichever recorder the system has - PipeWire, PulseAudio, ALSA, or
/// ffmpeg - reading raw 16 kHz mono PCM from its stdout. No native audio bindings to ship or break, and it records
/// from the same default microphone the desktop's sound settings point at.
/// </summary>
public sealed class ProcessAudioCapture : IAudioCapture
{
    private static readonly (string Exe, string[] Args, string Name)[] Recorders =
    [
        ("pw-record", ["--rate", "16000", "--channels", "1", "--format", "s16", "-"], "PipeWire"),
        ("parecord", ["--raw", "--rate=16000", "--channels=1", "--format=s16le"], "PulseAudio"),
        ("arecord", ["-q", "-f", "S16_LE", "-r", "16000", "-c", "1", "-t", "raw"], "ALSA"),
        ("ffmpeg", ["-loglevel", "error", "-f", OperatingSystem.IsMacOS() ? "avfoundation" : "pulse", "-i", OperatingSystem.IsMacOS() ? ":0" : "default", "-ar", "16000", "-ac", "1", "-f", "s16le", "-"], "ffmpeg"),
        ("sox", ["-q", "-d", "-r", "16000", "-c", "1", "-b", "16", "-e", "signed-integer", "-t", "raw", "-"], "SoX"),
    ];

    private readonly string _exe;
    private readonly string[] _args;
    private Process? _process;
    private volatile bool _stopping;

    public string Name { get; }
    public event Action<float[]>? Samples;
    public event Action<string?>? Stopped;

    private ProcessAudioCapture(string exe, string[] args, string name) { _exe = exe; _args = args; Name = name; }

    /// <summary>The first recorder found on PATH, or null (the UI then explains what to install).</summary>
    public static ProcessAudioCapture? Create()
    {
        foreach (var (exe, args, name) in Recorders)
            if (FindOnPath(exe) is { } path) return new ProcessAudioCapture(path, args, name);
        return null;
    }

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public void Start()
    {
        if (_process is { HasExited: false }) return;
        _stopping = false;
        var psi = new ProcessStartInfo(_exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in _args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {_exe}.");
        _process = p;
        _ = Task.Run(() => PumpAsync(p));
    }

    private async Task PumpAsync(Process p)
    {
        string? error = null;
        try
        {
            var stream = p.StandardOutput.BaseStream;
            var buffer = new byte[3200]; // 100 ms
            int carry = 0;
            while (true)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry)).ConfigureAwait(false);
                if (n <= 0) break;
                int total = carry + n, whole = total / 2 * 2;
                var samples = new float[whole / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)(buffer[2 * i] | buffer[2 * i + 1] << 8) / 32768f;
                carry = total - whole;
                if (carry > 0) buffer[0] = buffer[whole];
                Samples?.Invoke(samples);
            }
            if (!_stopping)
            {
                var err = (await p.StandardError.ReadToEndAsync().ConfigureAwait(false)).Trim();
                error = err.Length > 0 ? err.Split('\n')[^1] : $"{Name} recording stopped (exit code {(p.HasExited ? p.ExitCode : -1)}).";
            }
        }
        catch (Exception ex) when (!_stopping) { error = ex.Message; }
        catch { }
        Stopped?.Invoke(error);
    }

    public void Stop()
    {
        _stopping = true;
        try { if (_process is { HasExited: false } p) p.Kill(); } catch { }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Listens continuously: microphone → speech/silence detection → Whisper on each finished phrase → a transcript in
/// the language it was spoken. One shared Whisper model per process (it is hundreds of MB).
/// </summary>
public sealed class VoiceListener : IDisposable
{
    private static WhisperTranscriber? _whisper;
    private static readonly object WhisperLock = new();

    private readonly IAudioCapture _capture;
    private readonly UtteranceDetector _detector = new();
    private readonly CancellationTokenSource _cts = new();
    private int _inFlight;

    /// <summary>"auto" or a Whisper language code. A fixed language helps strong accents and short phrases.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Also produce an English version (for the offline command grammar when no language model is on).</summary>
    public bool TranslateToEnglish { get; set; }
    public WhisperModel Model { get; }
    public bool Listening { get; private set; }

    public event Action<double>? Level;
    public event Action<bool>? Speaking;
    public event Action<Transcript>? Heard;
    public event Action? Recognizing;
    public event Action<string>? Error;

    public VoiceListener(IAudioCapture capture, WhisperModel model)
    {
        _capture = capture;
        Model = model;
        _capture.Samples += s => _detector.Push(s);
        _capture.Stopped += err => { Listening = false; if (err is not null) Error?.Invoke(err); };
        _detector.Level += l => Level?.Invoke(l);
        _detector.SpeakingChanged += s => Speaking?.Invoke(s);
        _detector.Utterance += samples => _ = RecognizeAsync(samples);
    }

    /// <summary>Loads (once) the speech model - call early so the first phrase isn't slow.</summary>
    public static WhisperTranscriber Whisper(WhisperModel model)
    {
        lock (WhisperLock)
        {
            if (_whisper is not null && _whisper.Model == model) return _whisper;
            _whisper?.Dispose();
            return _whisper = new WhisperTranscriber(model);
        }
    }

    public void Start()
    {
        _ = Task.Run(() => { try { Whisper(Model); } catch (Exception ex) { Error?.Invoke(ex.Message); } });
        _capture.Start();
        Listening = true;
    }

    public void Stop()
    {
        _detector.Flush(); // recognize whatever was said before the stop
        _capture.Stop();
        Listening = false;
    }

    /// <summary>Recognizes a recorded clip (tests, "type instead" doesn't need this).</summary>
    public Task<Transcript> RecognizeClipAsync(float[] samples) =>
        Whisper(Model).RecognizeAsync(samples, Model.Multilingual ? Language : "en", false, _cts.Token);

    private async Task RecognizeAsync(float[] samples)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            Recognizing?.Invoke();
            var whisper = Whisper(Model);
            var lang = Model.Multilingual ? Language : "en";
            var t = await whisper.RecognizeAsync(samples, lang, false, _cts.Token).ConfigureAwait(false);
            if (IsNoise(t.Text)) return;
            if (TranslateToEnglish && t.LanguageCode != "en" && Model.Multilingual)
            {
                var en = await whisper.RecognizeAsync(samples, t.LanguageCode, true, _cts.Token).ConfigureAwait(false);
                t = t with { Text = en.Text.Length > 0 ? en.Text : t.Text };
            }
            Heard?.Invoke(t);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    /// <summary>Whisper's stock hallucinations on silence or noise ("Thank you.", "you", subtitles credits).</summary>
    public static bool IsNoise(string text)
    {
        var t = text.Trim().Trim('.', '!', '?', ' ').ToLowerInvariant();
        return t.Length < 2 || t is "you" or "thank you" or "thanks for watching" or "bye" or "thank you for watching" or "ご視聴ありがとうございました" or "谢谢观看" or "obrigado" or "gracias"
               || t.Contains("subtitles by") || t.Contains("amara.org") || t.All(c => !char.IsLetterOrDigit(c));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _capture.Dispose();
    }
}

/// <summary>Speaks the assistant's replies with the system's voices: Windows' installed voices, speech-dispatcher or
/// espeak-ng on Linux, "say" on macOS. Only speaks in a language a voice exists for - otherwise the reply is shown.</summary>
public sealed class VoiceSpeaker : IDisposable
{
    private Process? _current;
    private readonly object _lock = new();

    public bool Enabled { get; set; } = true;

    public void Speak(string text, string languageCode)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        Stop();
        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows()) { SpeakWindows(text, languageCode); return; }
                ProcessStartInfo? psi = null;
                if (OperatingSystem.IsMacOS()) psi = new ProcessStartInfo("say", [text]);
                else if (ProcessAudioCapture.FindOnPath("espeak-ng") is { } es) psi = new ProcessStartInfo(es, ["-v", EspeakVoice(languageCode), "-s", "165", text]);
                else if (ProcessAudioCapture.FindOnPath("spd-say") is { } spd) psi = new ProcessStartInfo(spd, ["-w", "-l", languageCode, text]);
                if (psi is null) return;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                var p = Process.Start(psi);
                lock (_lock) _current = p;
                p?.WaitForExit();
            }
            catch { /* speaking is a nicety - never break the assistant over it */ }
        });
    }

    private static string EspeakVoice(string code) => code switch
    {
        "zh" => "cmn",
        "yue" => "yue",
        "jw" => "jv",
        "no" => "nb",
        _ => code,
    };

    private System.Speech.Synthesis.SpeechSynthesizer? _synth;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void SpeakWindows(string text, string code)
    {
        _synth ??= new System.Speech.Synthesis.SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
        var voice = _synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == code);
        if (voice is null && code != "en") return; // no voice for this language: the reply is on screen
        if (voice is not null) _synth.SelectVoice(voice.VoiceInfo.Name);
        _synth.Speak(text);
    }

    public void Stop()
    {
        lock (_lock)
        {
            try { if (_current is { HasExited: false } p) p.Kill(); } catch { }
            _current = null;
        }
        if (OperatingSystem.IsWindows()) try { _synth?.SpeakAsyncCancelAll(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        if (OperatingSystem.IsWindows()) _synth?.Dispose();
    }
}
