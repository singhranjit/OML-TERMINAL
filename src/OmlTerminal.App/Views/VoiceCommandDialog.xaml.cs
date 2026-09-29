using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.App.Services;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Voice;

namespace OmlTerminal.App.Views;

public sealed partial class VoiceCommandDialog : ContentDialog
{
    private readonly TerminalSession _session;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly (string Label, IVendorGrammar Grammar)[] _grammars =
    [
        ("Cisco IOS / IOS-XE", new CiscoIosGrammar()),
        ("Cisco NX-OS", new CiscoNxosGrammar()),
        ("Cisco ASA", new CiscoAsaGrammar()),
        ("Arista EOS", new AristaEosGrammar()),
        ("Juniper Junos (basic)", new JuniperGrammar()),
        ("Fortinet FortiOS (basic)", new FortinetGrammar()),
        ("Palo Alto PAN-OS (basic)", new PaloAltoGrammar()),
        ("Generic - any vendor (exact CLI)", new GenericGrammar()),
    ];

    private IReadOnlyList<string> _expanded = [];
    private ISpeechInput? _speech;
    private bool _listening, _starting;
    private readonly List<string> _sentLog = new();
    private string? _pending;          // hands-free: an utterance that ended mid-command, joined with the next one
    private bool _pendingTrusted;
    private DateTime _pendingSince;

    public VoiceCommandDialog(XamlRoot xamlRoot, TerminalSession session, AppSettings settings, Action saveSettings)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        _session = session;
        _settings = settings;
        _saveSettings = saveSettings;
        foreach (var (label, _) in _grammars) VendorBox.Items.Add(label);
        var remembered = Array.FindIndex(_grammars, g => g.Label == settings.VoiceGrammar);
        VendorBox.SelectedIndex = remembered >= 0 ? remembered : 0;
        HandsFreeBox.IsChecked = settings.VoiceHandsFree;
        ModelBar.IsOpen = !WhisperTranscriber.ModelInstalled;
        PrimaryButtonClick += (_, _) => Send(_expanded);
        Closed += (_, _) =>
        {
            _settings.VoiceGrammar = _grammars[VendorBox.SelectedIndex].Label;
            _settings.VoiceHandsFree = HandsFreeBox.IsChecked == true;
            _saveSettings();
            _ = TeardownAsync();
        };
    }

    private IVendorGrammar Grammar => _grammars[Math.Max(0, VendorBox.SelectedIndex)].Grammar;

    private void Refresh(object sender, object e)
    {
        if (PreviewList is null) return;
        _expanded = VoiceCommandParser.Expand(PhraseBox.Text, Grammar);
        PreviewList.ItemsSource = _expanded;
        EmptyText.Visibility = _expanded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = _expanded.Count > 0;
        var incomplete = _expanded.Where(VoiceCommandParser.LooksIncomplete).ToList();
        var notCli = _expanded.Where(l => !VoiceCommandParser.LooksIncomplete(l) && !Grammar.LooksLikeCli(l)).ToList();
        IncompleteBar.IsOpen = incomplete.Count + notCli.Count > 0;
        IncompleteBar.Message = string.Join("\n", new[]
        {
            incomplete.Count == 0 ? "" : $"Missing a value (e.g. the address): \"{string.Join("\", \"", incomplete)}\".",
            notCli.Count == 0 ? "" : $"Doesn't look like a {_grammars[VendorBox.SelectedIndex].Label} command: \"{string.Join("\", \"", notCli)}\".",
            "Fix the text or say it again - Send types it exactly as shown.",
        }.Where(x => x.Length > 0));
    }

    private void Send(IReadOnlyList<string> lines)
    {
        // Off the UI thread, like macro playback, so the per-line delay never freezes the window.
        _ = Task.Run(async () =>
        {
            foreach (var line in lines)
            {
                if (!_session.IsConnected) return;
                _session.Send(System.Text.Encoding.UTF8.GetBytes(line + "\r"));
                await Task.Delay(350);
            }
        });
    }

    private void HandsFree_Click(object sender, RoutedEventArgs e)
    {
        if (HandsFreeBox.IsChecked == true && !_listening) MicButton_Click(sender, e);
    }

    // ---------- speech

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listening) { await StopListeningAsync(); return; }
        if (_starting) return;
        _starting = true;
        MicErrorBar.IsOpen = false;
        try { await StartListeningAsync(); }
        catch (Exception ex) { ShowError(ex.Message); await TeardownAsync(); }
        finally { _starting = false; }
    }

    /// <summary>
    /// Engine order: Whisper (offline, most accurate - once its model is downloaded), then Windows' online recognizer
    /// (needs "Online speech recognition" on), then Windows' offline recognizer with the network command grammar.
    /// </summary>
    private async Task StartListeningAsync()
    {
        if (_speech is null)
        {
            ISpeechInput? speech = null;
            if (HybridSpeechInput.IsAvailable)
            {
                speech = new HybridSpeechInput();
                Wire(speech);
                await speech.StartAsync();
            }
            else
            {
                // No English desktop recognizer (non-English Windows): Windows' online service is the only option.
                speech = new OnlineSpeechInput();
                Wire(speech);
                await speech.StartAsync();
            }
            _speech = speech;
        }
        else await _speech.StartAsync();
        SetListening(true);
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        ModelButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        ModelBar.Message = "Downloading the Whisper model from Hugging Face (148 MB)...";
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                ModelProgress.Value = Math.Min(1, bytes / (double)WhisperTranscriber.ApproxModelBytes);
                ModelBar.Title = $"Downloading... {bytes / 1_048_576} of {WhisperTranscriber.ApproxModelBytes / 1_048_576} MB";
            });
            await Task.Run(() => WhisperTranscriber.DownloadModelAsync(progress));
            await TeardownAsync(); // the next mic click picks Whisper
            ModelBar.Severity = InfoBarSeverity.Success;
            ModelBar.Title = "Whisper is ready";
            ModelBar.Message = "Click the mic - recognition now runs on Whisper, offline.";
            ModelProgress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ModelBar.Severity = InfoBarSeverity.Error;
            ModelBar.Title = "Download failed";
            ModelBar.Message = ex.Message;
            ModelButton.IsEnabled = true;
        }
    }

    private static string FriendlyOnlineError(Exception ex) => SpeechInputs.IsPrivacyError(ex)
        ? "Online speech recognition is off in Windows and no offline recognizer is installed. Download the Whisper model above, or turn on online speech in Settings > Privacy & security > Speech."
        : ex is UnauthorizedAccessException
            ? "Microphone access is blocked. Check Settings > Privacy & security > Microphone."
            : ex.Message;

    private void Wire(ISpeechInput speech)
    {
        speech.Hypothesis += text => DispatcherQueue.TryEnqueue(() => ListeningText.Text = text.StartsWith('(') ? text.Trim('(', ')') : $"Hearing: {text}");
        speech.Recognized += (text, trusted) => DispatcherQueue.TryEnqueue(() => OnRecognized(text, trusted));
        speech.Level += level => DispatcherQueue.TryEnqueue(() => LevelMeter.Value = level);
        speech.Stopped += error => DispatcherQueue.TryEnqueue(() =>
        {
            if (error is not null && _listening) ShowError($"Listening stopped: {error}");
            SetListening(false);
        });
    }

    private void OnRecognized(string text, bool trusted)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (HandsFreeBox.IsChecked == true)
        {
            // Hands-free sends without review, so the bar is high: a reliable engine AND every line a complete,
            // real command for this device type. Anything else waits in the box for the user to check.
            if (_pending is not null && DateTime.UtcNow - _pendingSince < TimeSpan.FromSeconds(20))
            {
                text = $"{_pending} {text}";
                trusted &= _pendingTrusted;
            }
            _pending = null;
            var lines = VoiceCommandParser.Expand(text, Grammar);
            PhraseBox.Text = text;
            if (VoiceCommandParser.EndsMidCommand(lines))
            {
                // "ip address" ... pause ... "10.1.1.1 255.255.255.0": wait for the rest instead of sending half a command.
                _pending = text;
                _pendingTrusted = trusted;
                _pendingSince = DateTime.UtcNow;
                ListeningText.Text = $"Heard \"{text}\" - waiting for the rest...";
                return;
            }
            if (trusted && VoiceCommandParser.SafeToAutoSend(lines, Grammar))
            {
                Send(lines);
                _sentLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {string.Join("  ⏎  ", lines)}");
                if (_sentLog.Count > 4) _sentLog.RemoveAt(4);
                SentText.Text = "Sent:\n" + string.Join("\n", _sentLog);
                ListeningText.Text = "Sent. Listening... (hands-free)";
            }
            else
            {
                var reason = lines.Select(CliValidator.Problem).FirstOrDefault(p => p is not null)
                             ?? (lines.Any(l => !Grammar.LooksLikeCli(l)) ? $"not a {_grammars[VendorBox.SelectedIndex].Label} command" : null)
                             ?? (trusted ? "not a complete command" : "not sure it heard that right");
                ListeningText.Text = $"Heard \"{text}\" - NOT sent ({reason}). Say it again, or fix it and press Send.";
            }
            return;
        }
        PhraseBox.Text = string.IsNullOrWhiteSpace(PhraseBox.Text) ? text : $"{PhraseBox.Text.TrimEnd()} and {text}";
        PhraseBox.SelectionStart = PhraseBox.Text.Length;
        ListeningText.Text = "Listening... click the mic again to stop.";
    }

    private async Task StopListeningAsync()
    {
        if (_speech is not null) await _speech.StopAsync();
        SetListening(false);
    }

    private async Task TeardownAsync()
    {
        if (_speech is null) return;
        try { await _speech.StopAsync(); } catch { }
        _speech.Dispose();
        _speech = null;
        _listening = false;
    }

    private void SetListening(bool on)
    {
        _listening = on;
        MicIcon.Glyph = on ? "" : ""; // stop-square vs microphone
        ToolTipService.SetToolTip(MicButton, on ? "Stop listening" : "Start listening");
        MeterRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) LevelMeter.Value = 0;
        if (on) ListeningText.Text = $"Listening ({_speech?.EngineName})... speak now.";
        else if (!ListeningText.Text.StartsWith("Heard")) ListeningText.Text = "Click the mic to talk, or type above.";
    }

    private void ShowError(string message)
    {
        MicErrorBar.Message = message;
        MicErrorBar.IsOpen = true;
        SetListening(false);
    }

    private async void OnlineSpeechLink_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-speech"));
}
