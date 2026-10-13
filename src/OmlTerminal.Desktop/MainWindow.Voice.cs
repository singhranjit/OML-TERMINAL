using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>The voice assistant in the main window: the panel, and the host through which the assistant reads the
/// terminal and opens sessions and tools. The engines themselves live in Core (<see cref="VoiceSession"/>).</summary>
public partial class MainWindow
{
    private VoicePanel? _voicePanel;
    private VoiceSession? _voice;
    private bool _downloading;
    private static readonly HttpClient VoiceHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    private void VoiceToggle_Click(object? sender, RoutedEventArgs e) => ToggleVoicePanel();

    private void VoiceMic_Click(object? sender, RoutedEventArgs e)
    {
        if (_voicePanel is not { IsVisible: true }) ToggleVoicePanel(true);
        _voice?.ToggleListening();
    }

    private void ToggleVoicePanel(bool? show = null)
    {
        EnsureVoice();
        bool visible = show ?? !_voicePanel!.IsVisible;
        _voicePanel!.IsVisible = visible;
        Body.ColumnDefinitions[3].Width = visible ? new GridLength(4) : new GridLength(0);
        Body.ColumnDefinitions[4].Width = visible ? new GridLength(380) : new GridLength(0);
        if (visible) { RefreshVoiceSetup(); _voice!.EnsureBrain(); }
        else _voice!.StopListening();
    }

    private void EnsureVoice()
    {
        if (_voicePanel is not null) return;
        _voicePanel = new VoicePanel { IsVisible = false };
        Grid.SetColumn(_voicePanel, 4);
        Body.Children.Add(_voicePanel);
        var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 3);
        Body.Children.Add(splitter);

        _voice = new VoiceSession(new DesktopVoiceHost(this), _vm.Settings, () => ProcessAudioCapture.Create(), VoiceHttp);
        _voice.Event += e => Dispatcher.UIThread.Post(() => OnVoiceEvent(e));
        _voice.Status += s => Dispatcher.UIThread.Post(() => _voicePanel?.SetStatus(s));
        _voice.Level += l => Dispatcher.UIThread.Post(() => _voicePanel?.SetLevel(l));
        _voice.ListeningChanged += on => Dispatcher.UIThread.Post(() => _voicePanel?.SetListening(on));
        _voice.Problem += p => Dispatcher.UIThread.Post(() => _voicePanel?.AddError(p));
        _voicePanel.MicClicked += () => _voice.ToggleListening();
        _voicePanel.Typed += text => _voice.Submit(text);
        _voicePanel.SettingsClicked += () => _ = OpenVoiceSettingsAsync();
        _voicePanel.CloseClicked += () => ToggleVoicePanel(false);
        _voicePanel.NewTopic += () => _voice.Assistant.Reset();
    }

    private void OnVoiceEvent(VoiceEvent e)
    {
        var p = _voicePanel;
        if (p is null) return;
        switch (e.Kind)
        {
            case VoiceEventKind.Heard: p.AddHeard(e.Text, e.Detail); break;
            case VoiceEventKind.Say: p.AddSaid(e.Text); break;
            case VoiceEventKind.Run: p.AddRun(e.Text); break;
            case VoiceEventKind.Output: p.AddOutput(e.Text, e.Detail ?? ""); break;
            case VoiceEventKind.Opened: p.AddNote(e.Text, Ui.Violet); break;
            case VoiceEventKind.Confirm: p.AddSaid(e.Text); break;
            case VoiceEventKind.Declined: p.AddNote(e.Text, Ui.Amber); break;
            case VoiceEventKind.Error: p.AddError(e.Text); break;
        }
    }

    // ---------- setup: models ----------

    private void RefreshVoiceSetup()
    {
        if (_voicePanel is null || _voice is null) return;
        _voicePanel.SetEngines(_voice.EnginesText);
        if (_downloading) return;
        switch (_voice.SetupNeed)
        {
            case VoiceSetupNeed.SpeechModel:
                _voicePanel.ShowSetup("To hear you, OML Terminal needs a speech model (downloaded once, then fully offline). Pick one - you can switch later:",
                    DownloadButton(WhisperModel.Small, "Fast (488 MB)"), DownloadButton(WhisperModel.Turbo, "Most accurate (574 MB)"),
                    Note("You can type requests below meanwhile."));
                break;
            case VoiceSetupNeed.BrainModel:
                _voicePanel.ShowSetup("To understand requests in your own words (any language), download the built-in model - about 2.5 GB, runs offline on this computer. Or use a model server you already have.",
                    BrainDownloadButton(),
                    Ui.Button("Use my server (Copilot settings)", () => { _vm.Settings.VoiceBrain = "server"; SaveVoiceSettings(); _voice.ApplySettings(); RefreshVoiceSetup(); }),
                    Note("Until then, exact commands like \"show ip route\" still work."));
                break;
            case VoiceSetupNeed.BrainServer:
                _voicePanel.ShowSetup("Voice is set to use your model server, but none is configured yet. Set the endpoint and model in Tools > AI Copilot, or switch to the built-in model in Voice settings.",
                    Ui.Button("Open AI Copilot settings", () => OpenTool("copilot")));
                break;
            default:
                _voicePanel.ShowSetup(null);
                break;
        }
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };

    private Control DownloadButton(WhisperModel model, string label) =>
        DownloadControl($"Download {label}", model.Bytes, p => WhisperTranscriber.DownloadAsync(model, p), () =>
        {
            _vm.Settings.VoiceSpeechModel = model.Id;
            SaveVoiceSettings();
        }, accent: false);

    private Control BrainDownloadButton() =>
        DownloadControl("Download the built-in model (2.5 GB)", LocalVoiceBrain.ApproxModelBytes, p => LocalVoiceBrain.DownloadModelAsync(p), () => { }, accent: true);

    private Control DownloadControl(string text, long bytes, Func<IProgress<long>, Task> download, Action done, bool accent)
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, IsVisible = false };
        var button = Ui.Button(text, () => { }, accent: accent);
        button.Click += async (_, _) =>
        {
            _downloading = true;
            button.IsEnabled = false;
            progress.IsVisible = true;
            try
            {
                var p = new Progress<long>(b => { progress.Value = b / (double)bytes; button.Content = $"Downloading... {b / 1_048_576} of {bytes / 1_048_576} MB"; });
                await Task.Run(() => download(p));
                done();
                _voice?.ApplySettings();
            }
            catch (Exception ex) { _voicePanel?.AddError($"Download failed: {ex.Message} - press the button again to resume."); }
            finally { _downloading = false; RefreshVoiceSetup(); }
        };
        return new StackPanel { Spacing = 4, Children = { button, progress } };
    }

    private void SaveVoiceSettings()
    {
        try { _vm.SaveSettings(); } catch { }
    }

    private async Task OpenVoiceSettingsAsync()
    {
        var changed = await new VoiceSettingsWindow(_vm.Settings).ShowDialog<bool>(this);
        if (!changed) return;
        SaveVoiceSettings();
        _voice?.ApplySettings();
        RefreshVoiceSetup();
    }

    private void DisposeVoice() => _voice?.Dispose();

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
