using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Voice.Assistant;

/// <summary>What still stands between the engineer and a working voice assistant.</summary>
public enum VoiceSetupNeed { None, SpeechModel, BrainModel, BrainServer }

/// <summary>
/// Everything behind the Voice panel that isn't drawing: the assistant, which language model it uses (loaded in the
/// background), the microphone and speech recognition, spoken replies, and the settings that drive them. Both desktop
/// apps use this, so voice behaves identically on Windows, Linux and macOS. Events fire on background threads - the
/// UI marshals them.
/// </summary>
public sealed class VoiceSession : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Func<IAudioCapture?> _captureFactory;
    private readonly HttpClient _http;
    private readonly VoiceSpeaker _speaker = new();
    private readonly CancellationTokenSource _cts = new();
    private VoiceListener? _listener;
    private IVoiceBrain? _brain;
    private string _brainKey = "";
    private Task? _brainLoading;
    private string _lastLanguage = "en";
    private bool _lastWasSpoken;

    public VoiceAssistant Assistant { get; }

    /// <summary>Assistant events (heard, said, ran, output, confirm, error, done).</summary>
    public event Action<VoiceEvent>? Event;
    /// <summary>One-line status under the mic ("Listening...", "Recognizing...").</summary>
    public event Action<string>? Status;
    public event Action<double>? Level;
    public event Action<bool>? ListeningChanged;
    /// <summary>Problems worth showing in the conversation (no microphone, model failed to load).</summary>
    public event Action<string>? Problem;

    public VoiceSession(IVoiceHost host, AppSettings settings, Func<IAudioCapture?> captureFactory, HttpClient http)
    {
        _settings = settings;
        _captureFactory = captureFactory;
        _http = http;
        Assistant = new VoiceAssistant(host);
        Assistant.Event += OnAssistantEvent;
        ApplySettings();
    }

    public bool Listening => _listener?.Listening == true;
    public bool Busy => Assistant.Busy;
    public WhisperModel? SpeechModel => WhisperTranscriber.BestInstalled(_settings.VoiceSpeechModel);

    public VoiceSetupNeed SetupNeed =>
        SpeechModel is null ? VoiceSetupNeed.SpeechModel
        : _settings.VoiceBrain == "builtin" && !LocalVoiceBrain.ModelInstalled ? VoiceSetupNeed.BrainModel
        : _settings.VoiceBrain == "server" && string.IsNullOrWhiteSpace(_settings.CopilotModel) ? VoiceSetupNeed.BrainServer
        : VoiceSetupNeed.None;

    public string EnginesText
    {
        get
        {
            var brain = _settings.VoiceBrain switch
            {
                "server" => string.IsNullOrWhiteSpace(_settings.CopilotModel) ? "your server (not set up)" : $"{_settings.CopilotModel} on your server",
                "off" => "exact commands only",
                _ => LocalVoiceBrain.ModelInstalled ? "built-in model (offline)" : "built-in model (not downloaded)",
            };
            var lang = _settings.VoiceLanguage == "auto" ? "any language" : WhisperTranscriber.LanguageName(_settings.VoiceLanguage);
            return $"Hearing: {SpeechModel?.Title ?? "no speech model yet"} · {lang}\nUnderstanding: {brain}";
        }
    }

    public string IdleStatus => "Click the mic and say what you need - in any language.";

    /// <summary>Re-reads the settings: confirmation, speaking, language model, and the speech model.</summary>
    public void ApplySettings()
    {
        Assistant.ConfirmChanges = _settings.VoiceConfirmChanges;
        _speaker.Enabled = _settings.VoiceSpeakReplies;
        if (_listener is not null && _listener.Model != SpeechModel)
        {
            _listener.Dispose();
            _listener = null;
            ListeningChanged?.Invoke(false);
        }
        EnsureBrain();
    }

    /// <summary>Creates (or swaps) the language model. The built-in one loads in the background with its
    /// instructions pre-read; a request made meanwhile waits for it.</summary>
    public void EnsureBrain()
    {
        var s = _settings;
        var key = s.VoiceBrain switch
        {
            "server" => string.IsNullOrWhiteSpace(s.CopilotModel) ? "none" : $"server|{s.CopilotBaseUrl}|{s.CopilotModel}|{s.CopilotApiKey}",
            "off" => "off",
            _ => LocalVoiceBrain.ModelInstalled ? "builtin" : "none",
        };
        if (key == _brainKey) return;
        _brainKey = key;
        var old = _brain;
        _brain = null;
        Assistant.Brain = null;
        if (old is not null) _ = Task.Run(() => { try { old.Dispose(); } catch { } });
        if (key == "builtin")
        {
            Status?.Invoke("Loading the built-in model...");
            _brainLoading = Task.Run(async () =>
            {
                try
                {
                    var brain = new LocalVoiceBrain();
                    await brain.WarmUpAsync(VoicePlanner.SystemPrompt).ConfigureAwait(false);
                    if (_brainKey != "builtin") { brain.Dispose(); return; }
                    _brain = brain;
                    Assistant.Brain = brain;
                    Status?.Invoke(Listening ? "Listening... speak in any language." : "Ready. Click the mic and talk, or type below.");
                }
                catch (Exception ex) { Problem?.Invoke($"Couldn't load the built-in model: {ex.Message}"); }
            });
        }
        else if (key.StartsWith("server|"))
        {
            var apiKey = LocalSecret.Unprotect(s.CopilotApiKey) ?? "";
            _brain = new HttpVoiceBrain(_http, new LlmSettings(s.CopilotBaseUrl, s.CopilotModel, apiKey));
            Assistant.Brain = _brain;
        }
    }

    // ---------- listening ----------

    public void ToggleListening()
    {
        if (Listening) StopListening();
        else StartListening();
    }

    public void StartListening()
    {
        var model = SpeechModel;
        if (model is null)
        {
            Status?.Invoke("Download a speech model first (above) - or type below.");
            return;
        }
        try
        {
            if (_listener is null || _listener.Model != model)
            {
                _listener?.Dispose();
                var capture = _captureFactory();
                if (capture is null)
                {
                    Problem?.Invoke(OperatingSystem.IsWindows()
                        ? "No microphone found. Plug one in - or, over Remote Desktop, allow it in the RDP client: Local Resources > Remote audio > Settings > Record from this computer."
                        : "No way to record from the microphone was found. Install one of: pipewire (pw-record), pulseaudio-utils (parecord), alsa-utils (arecord) or ffmpeg - e.g. sudo apt install pulseaudio-utils.");
                    return;
                }
                _listener = new VoiceListener(capture, model);
                _listener.Level += l => Level?.Invoke(l);
                _listener.Speaking += on => { if (on && !_speaker.IsSpeaking) Status?.Invoke("Hearing you..."); };
                _listener.Recognizing += () => Status?.Invoke("Recognizing...");
                _listener.Heard += OnHeard;
                _listener.Error += err => { Problem?.Invoke($"Microphone: {err}"); ListeningChanged?.Invoke(false); };
            }
            _listener.Language = _settings.VoiceLanguage;
            _listener.TranslateToEnglish = _settings.VoiceBrain == "off";
            _listener.Start();
            ListeningChanged?.Invoke(true);
            Status?.Invoke(_settings.VoiceHandsFree ? "Listening (hands-free)... speak in any language." : "Listening... speak in any language.");
        }
        catch (Exception ex)
        {
            Problem?.Invoke(ex.Message);
            ListeningChanged?.Invoke(false);
        }
    }

    public void StopListening()
    {
        if (_listener?.Listening == true) _listener.Stop();
        ListeningChanged?.Invoke(false);
        if (!Assistant.Busy) Status?.Invoke(IdleStatus);
    }

    private void OnHeard(Transcript t)
    {
        if (_speaker.IsSpeaking) return; // our own reply coming back through the microphone
        if (!_settings.VoiceHandsFree) StopListening(); // one request per click (a yes/no counts as one)
        Submit(t, typed: false);
    }

    /// <summary>A request, spoken or typed. Typed requests reuse the last spoken language for the voice reply.</summary>
    public void Submit(Transcript t, bool typed)
    {
        if (!typed && t.LanguageCode.Length > 0) _lastLanguage = t.LanguageCode;
        _lastWasSpoken = !typed;
        _speaker.Stop();
        var loading = _brainLoading;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            if (loading is { IsCompleted: false } && Assistant.Brain is null)
            {
                Status?.Invoke("Still loading the built-in model - one moment...");
                try { await loading.ConfigureAwait(false); } catch { }
            }
            await Assistant.HandleAsync(t, ct).ConfigureAwait(false);
        });
    }

    public void Submit(string typedText) => Submit(new Transcript(typedText, _lastLanguage, ""), typed: true);

    private void OnAssistantEvent(VoiceEvent e)
    {
        switch (e.Kind)
        {
            case VoiceEventKind.Heard: Status?.Invoke("Working on it..."); break;
            case VoiceEventKind.Thinking: Status?.Invoke(e.Text); break;
            case VoiceEventKind.Confirm:
                Status?.Invoke("Waiting for your go-ahead - say yes or no.");
                // Asked by voice: listen for the answer without another click (once the question has been spoken).
                if (_lastWasSpoken && !Listening && SpeechModel is not null) _ = ListenForAnswerAsync();
                break;
            case VoiceEventKind.Done: Status?.Invoke(Listening ? "Listening... speak in any language." : IdleStatus); break;
        }
        Event?.Invoke(e);
        if (e.Speak && e.Text.Length > 0) _speaker.Speak(e.Text, _lastLanguage);
    }

    private async Task ListenForAnswerAsync()
    {
        await Task.Delay(400).ConfigureAwait(false);
        for (int i = 0; i < 100 && _speaker.IsSpeaking; i++) await Task.Delay(100).ConfigureAwait(false);
        if (Assistant.AwaitingConfirmation && !Listening) StartListening();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Dispose();
        _speaker.Dispose();
        _brain?.Dispose();
    }
}
