using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>The voice assistant's wiring in the main window: panel, microphone, models, speaking, and the host
/// interface through which the assistant reads the terminal and opens sessions and tools.</summary>
public partial class MainWindow
{
    private VoicePanel? _voicePanel;
    private VoiceAssistant? _assistant;
    private VoiceListener? _listener;
    private readonly VoiceSpeaker _speaker = new();
    private IVoiceBrain? _brain;
    private string _brainKey = "";
    private Task? _brainLoading;
    private string _lastLanguage = "en";
    private bool _downloading;
    private CancellationTokenSource? _voiceCts;
    private static readonly HttpClient VoiceHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    private void VoiceToggle_Click(object? sender, RoutedEventArgs e) => ToggleVoicePanel();

    private void ToggleVoicePanel(bool? show = null)
    {
        EnsureVoicePanel();
        bool visible = show ?? !_voicePanel!.IsVisible;
        _voicePanel!.IsVisible = visible;
        Body.ColumnDefinitions[3].Width = visible ? new GridLength(4) : new GridLength(0);
        Body.ColumnDefinitions[4].Width = visible ? new GridLength(380) : new GridLength(0);
        if (visible) { RefreshVoiceSetup(); EnsureBrain(); }
        else StopListening();
    }

    private void EnsureVoicePanel()
    {
        if (_voicePanel is not null) return;
        _voicePanel = new VoicePanel { IsVisible = false };
        Grid.SetColumn(_voicePanel, 4);
        Body.Children.Add(_voicePanel);
        var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 3);
        Body.Children.Add(splitter);

        _assistant = new VoiceAssistant(new DesktopVoiceHost(this)) { ConfirmChanges = _vm.Settings.VoiceConfirmChanges };
        _assistant.Event += e => Dispatcher.UIThread.Post(() => OnVoiceEvent(e));
        _voicePanel.MicClicked += ToggleListening;
        _voicePanel.Typed += text => SubmitVoice(new Transcript(text, _lastLanguage, ""), typed: true);
        _voicePanel.SettingsClicked += () => _ = OpenVoiceSettingsAsync();
        _voicePanel.CloseClicked += () => ToggleVoicePanel(false);
        _voicePanel.NewTopic += () => _assistant.Reset();
        _speaker.Enabled = _vm.Settings.VoiceSpeakReplies;
    }

    // ---------- setup: models and engines ----------

    private WhisperModel? SpeechModel => WhisperTranscriber.BestInstalled(_vm.Settings.VoiceSpeechModel);

    private void RefreshVoiceSetup()
    {
        if (_voicePanel is null) return;
        var speech = SpeechModel;
        var brainMode = _vm.Settings.VoiceBrain;
        var brainText = brainMode switch
        {
            "server" => string.IsNullOrWhiteSpace(_vm.Settings.CopilotModel) ? "your server (not set up)" : $"{_vm.Settings.CopilotModel} on your server",
            "off" => "exact commands only",
            _ => LocalVoiceBrain.ModelInstalled ? "built-in model (offline)" : "built-in model (not downloaded)",
        };
        _voicePanel.SetEngines($"Hearing: {speech?.Title ?? "no speech model yet"} · {(_vm.Settings.VoiceLanguage == "auto" ? "any language" : WhisperTranscriber.LanguageName(_vm.Settings.VoiceLanguage))}\nUnderstanding: {brainText}");
        if (_downloading) return;

        if (speech is null)
        {
            _voicePanel.ShowSetup("To hear you, OML Terminal needs a speech model (downloaded once, then fully offline). Pick one - you can switch later:",
                DownloadButton(WhisperModel.Small, "Fast (488 MB)"), DownloadButton(WhisperModel.Turbo, "Most accurate (574 MB)"),
                Note("You can type requests below meanwhile."));
            return;
        }
        if (brainMode == "builtin" && !LocalVoiceBrain.ModelInstalled)
        {
            _voicePanel.ShowSetup("To understand requests in your own words (any language), download the built-in model - about 2.5 GB, runs offline on this computer. Or use a model server you already have.",
                BrainDownloadButton(),
                Ui.Button("Use my server (Copilot settings)", () => { _vm.Settings.VoiceBrain = "server"; SaveVoiceSettings(); RefreshVoiceSetup(); EnsureBrain(); }),
                Note("Until then, exact commands like \"show ip route\" still work."));
            return;
        }
        if (brainMode == "server" && string.IsNullOrWhiteSpace(_vm.Settings.CopilotModel))
        {
            _voicePanel.ShowSetup("Voice is set to use your model server, but none is configured yet. Set the endpoint and model in Tools > AI Copilot, or switch to the built-in model in Voice settings.",
                Ui.Button("Open AI Copilot settings", () => OpenTool("copilot")));
            return;
        }
        _voicePanel.ShowSetup(null);
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };

    private Control DownloadButton(WhisperModel model, string label)
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, IsVisible = false };
        var button = Ui.Button($"Download {label}", () => { });
        button.Click += async (_, _) =>
        {
            _downloading = true;
            button.IsEnabled = false;
            progress.IsVisible = true;
            try
            {
                var p = new Progress<long>(b => { progress.Value = b / (double)model.Bytes; button.Content = $"Downloading... {b / 1_048_576} of {model.Bytes / 1_048_576} MB"; });
                await Task.Run(() => WhisperTranscriber.DownloadAsync(model, p));
                _vm.Settings.VoiceSpeechModel = model.Id;
                SaveVoiceSettings();
                _listener?.Dispose();
                _listener = null;
            }
            catch (Exception ex) { _voicePanel?.AddError($"Download failed: {ex.Message}"); }
            finally { _downloading = false; RefreshVoiceSetup(); }
        };
        return new StackPanel { Spacing = 4, Children = { button, progress } };
    }

    private Control BrainDownloadButton()
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, IsVisible = false };
        var button = Ui.Button("Download the built-in model (2.5 GB)", () => { }, accent: true);
        button.Click += async (_, _) =>
        {
            _downloading = true;
            button.IsEnabled = false;
            progress.IsVisible = true;
            try
            {
                var p = new Progress<long>(b => { progress.Value = b / (double)LocalVoiceBrain.ApproxModelBytes; button.Content = $"Downloading... {b / 1_048_576} of {LocalVoiceBrain.ApproxModelBytes / 1_048_576} MB"; });
                await Task.Run(() => LocalVoiceBrain.DownloadModelAsync(p));
            }
            catch (Exception ex) { _voicePanel?.AddError($"Download failed: {ex.Message} - press the button again to resume."); }
            finally { _downloading = false; RefreshVoiceSetup(); EnsureBrain(); }
        };
        return new StackPanel { Spacing = 4, Children = { button, progress } };
    }

    /// <summary>Creates (or swaps) the language model behind the assistant. The built-in one takes a few seconds to
    /// load, so it loads in the background with its instructions pre-read; requests made meanwhile wait for it.</summary>
    private void EnsureBrain()
    {
        if (_assistant is null) return;
        var s = _vm.Settings;
        var key = s.VoiceBrain switch
        {
            "server" => $"server|{s.CopilotBaseUrl}|{s.CopilotModel}",
            "off" => "off",
            _ => LocalVoiceBrain.ModelInstalled ? "builtin" : "none",
        };
        if (key == _brainKey) return;
        _brainKey = key;
        var old = _brain;
        _brain = null;
        _assistant.Brain = null;
        if (old is not null) _ = Task.Run(() => { try { old.Dispose(); } catch { } });
        switch (key)
        {
            case "builtin":
                _voicePanel?.SetStatus("Loading the built-in model...");
                _brainLoading = Task.Run(async () =>
                {
                    try
                    {
                        var brain = new LocalVoiceBrain();
                        await brain.WarmUpAsync(VoicePlanner.SystemPrompt);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_brainKey != "builtin") { brain.Dispose(); return; }
                            _brain = brain;
                            _assistant.Brain = brain;
                            _voicePanel?.SetStatus(_listener?.Listening == true ? "Listening... speak in any language." : "Ready. Click the mic and talk, or type below.");
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => _voicePanel?.AddError($"Couldn't load the built-in model: {ex.Message}"));
                    }
                });
                break;
            case "off":
            case "none":
                break;
            default:
                var apiKey = LocalSecret.Unprotect(s.CopilotApiKey) ?? "";
                _brain = new HttpVoiceBrain(VoiceHttp, new LlmSettings(s.CopilotBaseUrl, s.CopilotModel, apiKey));
                _assistant.Brain = _brain;
                break;
        }
    }

    private void SaveVoiceSettings()
    {
        try { _vm.SaveSettings(); } catch { }
    }

    // ---------- listening ----------

    private void VoiceMic_Click(object? sender, RoutedEventArgs e)
    {
        if (_voicePanel is not { IsVisible: true }) ToggleVoicePanel(true);
        ToggleListening();
    }

    private void ToggleListening()
    {
        if (_listener?.Listening == true) { StopListening(); return; }
        StartListening();
    }

    private void StartListening()
    {
        if (_voicePanel is null) return;
        var model = SpeechModel;
        if (model is null) { RefreshVoiceSetup(); _voicePanel.SetStatus("Download a speech model first (above) - or type below."); return; }
        try
        {
            if (_listener is null || _listener.Model != model)
            {
                _listener?.Dispose();
                var capture = ProcessAudioCapture.Create();
                if (capture is null)
                {
                    _voicePanel.AddError("No way to record from the microphone was found. Install one of: pipewire (pw-record), pulseaudio-utils (parecord), alsa-utils (arecord) or ffmpeg - e.g. sudo apt install pulseaudio-utils.");
                    return;
                }
                _listener = new VoiceListener(capture, model);
                _listener.Level += l => Dispatcher.UIThread.Post(() => _voicePanel?.SetLevel(l));
                _listener.Speaking += on => Dispatcher.UIThread.Post(() => { if (on && !_speaker.IsSpeaking) _voicePanel?.SetStatus("Hearing you..."); });
                _listener.Recognizing += () => Dispatcher.UIThread.Post(() => _voicePanel?.SetStatus("Recognizing..."));
                _listener.Heard += t => Dispatcher.UIThread.Post(() => OnHeard(t));
                _listener.Error += err => Dispatcher.UIThread.Post(() => { _voicePanel?.AddError($"Microphone: {err}"); _voicePanel?.SetListening(false); });
            }
            _listener.Language = _vm.Settings.VoiceLanguage;
            _listener.TranslateToEnglish = _vm.Settings.VoiceBrain == "off";
            _listener.Start();
            _voicePanel.SetListening(true);
            _voicePanel.SetStatus(_vm.Settings.VoiceHandsFree ? "Listening (hands-free)... speak in any language." : "Listening... speak in any language.");
        }
        catch (Exception ex) { _voicePanel.AddError(ex.Message); _voicePanel.SetListening(false); }
    }

    private void StopListening()
    {
        if (_listener?.Listening == true) _listener.Stop();
        _voicePanel?.SetListening(false);
        if (_voicePanel is not null && _assistant?.Busy != true) _voicePanel.SetStatus("Click the mic and say what you need - in any language.");
    }

    private void OnHeard(Transcript t)
    {
        // Our own spoken reply coming back through the microphone isn't a request.
        if (_speaker.IsSpeaking) return;
        if (!_vm.Settings.VoiceHandsFree && _assistant?.AwaitingConfirmation != true) StopListening();
        SubmitVoice(t, typed: false);
    }

    private void SubmitVoice(Transcript t, bool typed)
    {
        if (_assistant is null) return;
        if (!typed && t.LanguageCode.Length > 0) _lastLanguage = t.LanguageCode;
        _speaker.Stop();
        _voiceCts ??= new CancellationTokenSource();
        var ct = _voiceCts.Token;
        var loading = _brainLoading;
        _ = Task.Run(async () =>
        {
            // A request made while the built-in model is still loading waits for it rather than failing.
            if (loading is { IsCompleted: false } && _assistant.Brain is null)
            {
                Dispatcher.UIThread.Post(() => _voicePanel?.SetStatus("Still loading the built-in model - one moment..."));
                await loading;
            }
            await _assistant.HandleAsync(t, ct);
        });
    }

    private void OnVoiceEvent(VoiceEvent e)
    {
        var p = _voicePanel;
        if (p is null) return;
        switch (e.Kind)
        {
            case VoiceEventKind.Heard: p.AddHeard(e.Text, e.Detail); p.SetStatus("Working on it..."); break;
            case VoiceEventKind.Thinking: p.SetStatus(e.Text); break;
            case VoiceEventKind.Say: p.AddSaid(e.Text); break;
            case VoiceEventKind.Run: p.AddRun(e.Text); break;
            case VoiceEventKind.Output: p.AddOutput(e.Text, e.Detail ?? ""); break;
            case VoiceEventKind.Opened: p.AddNote(e.Text, Ui.Violet); break;
            case VoiceEventKind.Confirm: p.AddSaid(e.Text); p.SetStatus("Waiting for your go-ahead - say yes or no."); break;
            case VoiceEventKind.Declined: p.AddNote(e.Text, Ui.Amber); break;
            case VoiceEventKind.Error: p.AddError(e.Text); break;
            case VoiceEventKind.Done:
                p.SetStatus(_listener?.Listening == true ? "Listening... speak in any language." : "Click the mic and say what you need - in any language.");
                break;
        }
        if (e.Speak && e.Text.Length > 0) _speaker.Speak(e.Text, _lastLanguage);
    }

    private async Task OpenVoiceSettingsAsync()
    {
        var changed = await new VoiceSettingsWindow(_vm.Settings).ShowDialog<bool>(this);
        if (!changed) return;
        SaveVoiceSettings();
        _speaker.Enabled = _vm.Settings.VoiceSpeakReplies;
        if (_assistant is not null) _assistant.ConfirmChanges = _vm.Settings.VoiceConfirmChanges;
        if (_listener is not null && _listener.Model != SpeechModel) { _listener.Dispose(); _listener = null; _voicePanel?.SetListening(false); }
        RefreshVoiceSetup();
        EnsureBrain();
    }

    private void DisposeVoice()
    {
        _voiceCts?.Cancel();
        _listener?.Dispose();
        _speaker.Dispose();
        _brain?.Dispose();
    }

    /// <summary>What the assistant can see and do in this window. Called from background threads.</summary>
    private sealed class DesktopVoiceHost(MainWindow w) : IVoiceHost
    {
        public VoiceTerminal? ActiveTerminal() => Dispatcher.UIThread.Invoke(() =>
        {
            if (w.CurrentTab() is not { } t) return null;
            var session = t.Vm.Session;
            return new VoiceTerminal(t.Vm.Profile.Name, t.Vm.Profile.Host, t.Vm.ProtocolText, session, () => session.Engine.GetBufferText());
        });

        public IReadOnlyList<SessionProfile> Sessions() => Dispatcher.UIThread.Invoke(() => w._vm.IsLocked ? [] : (IReadOnlyList<SessionProfile>)w._vm.Sessions.ToList());

        public IReadOnlyList<(string Id, string Title)> Tools() => ToolCatalog.All.Select(t => (t.Id, t.Title)).ToList();

        public Task<bool> ConnectAsync(SessionProfile profile) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            w.Connect(profile);
            return true;
        }).GetTask();

        public Task<bool> OpenToolAsync(string id, string target) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            var tool = ToolCatalog.Find(id) ?? ToolCatalog.All.FirstOrDefault(t => t.Title.Contains(id, StringComparison.OrdinalIgnoreCase));
            if (tool is null) return false;
            w.OpenTool(tool.Id, c => { if (target.Length > 0 && c is IVoiceTarget v) v.RunFor(target); });
            return true;
        }).GetTask();

        public Task<bool> ConfirmAsync(VoiceConfirmation confirmation, CancellationToken ct) =>
            Dispatcher.UIThread.InvokeAsync(() => w._voicePanel?.AddConfirmation(confirmation, ct) ?? Task.FromResult(false));
    }
}
