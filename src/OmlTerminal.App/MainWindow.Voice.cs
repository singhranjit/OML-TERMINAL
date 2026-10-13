using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.App.Services;
using OmlTerminal.App.Views.Tools;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;

namespace OmlTerminal.App;

/// <summary>The voice assistant in the main window: the docked panel, and the host through which the assistant reads
/// the terminal and opens sessions and tools. The engines live in Core (<see cref="VoiceSession"/>), shared with the
/// Linux/macOS app.</summary>
public sealed partial class MainWindow
{
    private VoiceSession? _voice;
    private bool _voiceDownloading;
    private static readonly HttpClient VoiceHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    private void VoiceToggle_Click(object sender, RoutedEventArgs e) => ToggleVoicePanel();

    private void VoiceMic_Click(object sender, RoutedEventArgs e)
    {
        if (VoiceHost.Visibility != Visibility.Visible) ToggleVoicePanel(true);
        _voice?.ToggleListening();
    }

    private void ToggleVoicePanel(bool? show = null)
    {
        EnsureVoice();
        bool visible = show ?? VoiceHost.Visibility != Visibility.Visible;
        VoiceHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        VoiceColumn.Width = visible ? new GridLength(390) : new GridLength(0);
        if (visible) { RefreshVoiceSetup(); _voice!.EnsureBrain(); }
        else _voice!.StopListening();
    }

    private void EnsureVoice()
    {
        if (_voice is not null) return;
        var q = DispatcherQueue;
        _voice = new VoiceSession(new WindowsVoiceHost(this), _vm.Settings, () => NAudioCapture.Create(), VoiceHttp);
        _voice.Event += e => q.TryEnqueue(() => OnVoiceEvent(e));
        _voice.Status += s => q.TryEnqueue(() => VoicePanel.SetStatus(s));
        _voice.Level += l => q.TryEnqueue(() => VoicePanel.SetLevel(l));
        _voice.ListeningChanged += on => q.TryEnqueue(() => VoicePanel.SetListening(on));
        _voice.Problem += p => q.TryEnqueue(() => VoicePanel.AddError(p));
        VoicePanel.MicClicked += () => _voice.ToggleListening();
        VoicePanel.Typed += text => _voice.Submit(text);
        VoicePanel.SettingsClicked += () => _ = OpenVoiceSettingsAsync();
        VoicePanel.CloseClicked += () => ToggleVoicePanel(false);
        VoicePanel.NewTopic += () => _voice.Assistant.Reset();
    }

    private void OnVoiceEvent(VoiceEvent e)
    {
        var p = VoicePanel;
        switch (e.Kind)
        {
            case VoiceEventKind.Heard: p.AddHeard(e.Text, e.Detail); break;
            case VoiceEventKind.Say: p.AddSaid(e.Text); break;
            case VoiceEventKind.Run: p.AddRun(e.Text); break;
            case VoiceEventKind.Output: p.AddOutput(e.Text, e.Detail ?? ""); break;
            case VoiceEventKind.Opened: p.AddNote(e.Text, ToolUi.Brush("OmlVioletBrush")); break;
            case VoiceEventKind.Confirm: p.AddSaid(e.Text); break;
            case VoiceEventKind.Declined: p.AddNote(e.Text, ToolUi.Brush("OmlAmberBrush")); break;
            case VoiceEventKind.Error: p.AddError(e.Text); break;
        }
    }

    // ---------- setup: models ----------

    private void RefreshVoiceSetup()
    {
        if (_voice is null) return;
        VoicePanel.SetEngines(_voice.EnginesText);
        if (_voiceDownloading) return;
        switch (_voice.SetupNeed)
        {
            case VoiceSetupNeed.SpeechModel:
                VoicePanel.ShowSetup("To hear you, OML Terminal needs a speech model (downloaded once, then fully offline). Pick one - you can switch later:",
                    DownloadControl($"Download Fast ({WhisperModel.Small.Bytes / 1_000_000} MB)", WhisperModel.Small.Bytes, p => WhisperTranscriber.DownloadAsync(WhisperModel.Small, p), () => _vm.Settings.VoiceSpeechModel = WhisperModel.Small.Id, accent: false),
                    DownloadControl($"Download Most accurate ({WhisperModel.Turbo.Bytes / 1_000_000} MB)", WhisperModel.Turbo.Bytes, p => WhisperTranscriber.DownloadAsync(WhisperModel.Turbo, p), () => _vm.Settings.VoiceSpeechModel = WhisperModel.Turbo.Id, accent: false),
                    Note("You can type requests below meanwhile."));
                break;
            case VoiceSetupNeed.BrainModel:
                var server = new Button { Content = "Use my server (AI Copilot settings)" };
                server.Click += (_, _) => { _vm.Settings.VoiceBrain = "server"; _vm.SaveSettings(); _voice.ApplySettings(); RefreshVoiceSetup(); };
                VoicePanel.ShowSetup("To understand requests in your own words (any language), download the built-in model - about 2.5 GB, runs offline on this PC. Or use a model server you already have.",
                    DownloadControl("Download the built-in model (2.5 GB)", LocalVoiceBrain.ApproxModelBytes, p => LocalVoiceBrain.DownloadModelAsync(p), () => { }, accent: true),
                    server, Note("Until then, exact commands like \"show ip route\" still work."));
                break;
            case VoiceSetupNeed.BrainServer:
                var copilot = new Button { Content = "Open AI Copilot settings" };
                copilot.Click += (_, _) => OpenTool("copilot");
                VoicePanel.ShowSetup("Voice is set to use your model server, but none is configured yet. Set the endpoint and model in AI Copilot, or switch to the built-in model in Voice settings.", copilot);
                break;
            default:
                VoicePanel.ShowSetup(null);
                break;
        }
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };

    private UIElement DownloadControl(string text, long bytes, Func<IProgress<long>, Task> download, Action done, bool accent)
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
        var button = new Button { Content = text };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) =>
        {
            _voiceDownloading = true;
            button.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                var p = new Progress<long>(b => { progress.Value = b / (double)bytes; button.Content = $"Downloading... {b / 1_048_576} of {bytes / 1_048_576} MB"; });
                await Task.Run(() => download(p));
                done();
                _vm.SaveSettings();
                _voice?.ApplySettings();
            }
            catch (Exception ex) { VoicePanel.AddError($"Download failed: {ex.Message} - press the button again to resume."); }
            finally { _voiceDownloading = false; RefreshVoiceSetup(); }
        };
        return new StackPanel { Spacing = 4, Children = { button, progress } };
    }

    private async Task OpenVoiceSettingsAsync()
    {
        var s = _vm.Settings;
        var languages = new List<(string Code, string Name)> { ("auto", "Detect automatically (any language)") };
        languages.AddRange(WhisperLanguages.Names.OrderBy(kv => kv.Value).Select(kv => (kv.Key, kv.Value)));
        var language = new ComboBox { Header = "Language you speak", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = languages.Select(l => l.Name).ToList() };
        language.SelectedIndex = Math.Max(0, languages.FindIndex(l => l.Code == s.VoiceLanguage));
        var models = WhisperModel.All.ToList();
        var speech = new ComboBox
        {
            Header = "Speech recognition", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = models.Select(m => $"{m.Title}{(WhisperTranscriber.IsInstalled(m) ? "" : "  (not downloaded)")}").ToList(),
        };
        speech.SelectedIndex = Math.Max(0, models.FindIndex(m => m == WhisperTranscriber.BestInstalled(s.VoiceSpeechModel)));
        string[] brainIds = ["builtin", "server", "off"];
        var brain = new ComboBox
        {
            Header = "Understanding requests", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                $"Built-in model - offline, ~2.5 GB{(LocalVoiceBrain.ModelInstalled ? "" : " (not downloaded)")}",
                $"My model server - {(string.IsNullOrWhiteSpace(s.CopilotModel) ? "set it up in AI Copilot" : $"{s.CopilotModel} at {s.CopilotBaseUrl}")}",
                "Off - exact commands only (\"show ip route\")",
            },
            SelectedIndex = Math.Max(0, Array.IndexOf(brainIds, s.VoiceBrain)),
        };
        var speak = new CheckBox { Content = "Speak replies aloud", IsChecked = s.VoiceSpeakReplies };
        var confirm = new CheckBox { Content = "Ask before changing a device (say \"yes\" or click Apply)", IsChecked = s.VoiceConfirmChanges };
        var handsFree = new CheckBox { Content = "Hands-free: keep listening after each request", IsChecked = s.VoiceHandsFree };
        var tip = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65,
            Text = "Talk the way you would to a colleague: \"why is port 2 down?\", \"put port 5 in VLAN 10\", \"open the core switch and show me the BGP neighbors\", \"undo that\". Commands that can take a device down (reload, erase, reboot) always need a click.",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "Voice settings",
            Content = new StackPanel { Spacing = 12, MinWidth = 460, Children = { language, speech, brain, speak, confirm, handsFree, tip } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        s.VoiceLanguage = languages[Math.Max(0, language.SelectedIndex)].Code;
        s.VoiceSpeechModel = models[Math.Max(0, speech.SelectedIndex)].Id;
        s.VoiceBrain = brainIds[Math.Max(0, brain.SelectedIndex)];
        s.VoiceSpeakReplies = speak.IsChecked == true;
        s.VoiceConfirmChanges = confirm.IsChecked == true;
        s.VoiceHandsFree = handsFree.IsChecked == true;
        _vm.SaveSettings();
        _voice?.ApplySettings();
        RefreshVoiceSetup();
    }

    private void DisposeVoice() => _voice?.Dispose();

    /// <summary>Runs on the UI thread and waits for the result - for the assistant, which works on background threads.</summary>
    private static Task<T> OnUi<T>(DispatcherQueue q, Func<T> f)
    {
        if (q.HasThreadAccess) return Task.FromResult(f());
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!q.TryEnqueue(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } }))
            tcs.SetException(new InvalidOperationException("The window is closing."));
        return tcs.Task;
    }

    /// <summary>What the assistant can see and do in this window. Called from background threads.</summary>
    private sealed class WindowsVoiceHost(MainWindow w) : IVoiceHost
    {
        public VoiceTerminal? ActiveTerminal() => OnUi(w.DispatcherQueue, () =>
        {
            if (w.SelectedTerminalTab() is not { } t) return null;
            var session = t.Session;
            return new VoiceTerminal(t.Profile.Name, t.Profile.Host, t.ProtocolText, session, () => session.Engine.GetBufferText());
        }).GetAwaiter().GetResult();

        public IReadOnlyList<SessionProfile> Sessions() => OnUi(w.DispatcherQueue, () => (IReadOnlyList<SessionProfile>)w._vm.Sessions.ToList()).GetAwaiter().GetResult();

        public IReadOnlyList<(string Id, string Title)> Tools() => ToolCatalog.All.Select(t => (t.Id, t.Title)).ToList();

        public Task<bool> ConnectAsync(SessionProfile profile) => OnUi(w.DispatcherQueue, () =>
        {
            w.OpenSession(profile);
            return true;
        });

        public Task<bool> OpenToolAsync(string id, string target) => OnUi(w.DispatcherQueue, () =>
        {
            var tool = ToolCatalog.Find(id) ?? ToolCatalog.All.FirstOrDefault(t => t.Title.Contains(id, StringComparison.OrdinalIgnoreCase));
            if (tool is null) return false;
            w.OpenTool(tool.Id, v => { if (target.Length > 0 && v is IVoiceTarget vt) vt.RunFor(target); });
            return true;
        });

        public Task<bool> ConfirmAsync(VoiceConfirmation confirmation, CancellationToken ct) =>
            OnUi(w.DispatcherQueue, () => w.VoicePanel.AddConfirmation(confirmation, ct)).Unwrap();
    }
}
