using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>Voice settings: language, speech model, which model understands requests, and how it behaves.
/// Closes with true when something changed.</summary>
public sealed class VoiceSettingsWindow : Window
{
    public VoiceSettingsWindow(AppSettings settings)
    {
        Title = "Voice settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var languages = new List<(string Code, string Name)> { ("auto", "Detect automatically (any language)") };
        languages.AddRange(WhisperLanguages.Names.OrderBy(kv => kv.Value).Select(kv => (kv.Key, kv.Value)));
        var language = Ui.Combo(languages.Select(l => l.Name).ToList(), Math.Max(0, languages.FindIndex(l => l.Code == settings.VoiceLanguage)));

        var models = WhisperModel.All.ToList();
        var installed = WhisperTranscriber.BestInstalled(settings.VoiceSpeechModel);
        var speech = Ui.Combo(models.Select(m => $"{m.Title}{(WhisperTranscriber.IsInstalled(m) ? "" : "  (not downloaded)")}").ToList(),
            Math.Max(0, models.FindIndex(m => m == installed)));
        var speechNote = Ui.Text("", 11.5, color: Ui.Muted);
        speechNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        void UpdateSpeechNote() => speechNote.Text = models[Math.Max(0, speech.SelectedIndex)].Description +
            (WhisperTranscriber.IsInstalled(models[Math.Max(0, speech.SelectedIndex)]) ? "" : " Download it from the Voice panel.");
        speech.SelectionChanged += (_, _) => UpdateSpeechNote();
        UpdateSpeechNote();

        string[] brainIds = ["builtin", "server", "off"];
        var brain = Ui.Combo(new[]
        {
            $"Built-in model - offline, ~2.5 GB{(LocalVoiceBrain.ModelInstalled ? "" : " (not downloaded)")}",
            $"My model server - {(string.IsNullOrWhiteSpace(settings.CopilotModel) ? "set it up in Tools > AI Copilot" : $"{settings.CopilotModel} at {settings.CopilotBaseUrl}")}",
            "Off - exact commands only (\"show ip route\")",
        }, Math.Max(0, Array.IndexOf(brainIds, settings.VoiceBrain)));

        var speak = Ui.Check("Speak replies aloud", settings.VoiceSpeakReplies);
        var confirm = Ui.Check("Ask before changing a device (say \"yes\" or click Apply)", settings.VoiceConfirmChanges);
        var handsFree = Ui.Check("Hands-free: keep listening after each request", settings.VoiceHandsFree);
        var warn = Ui.Text("Without confirmation, configuration changes are applied as soon as they're understood. Commands that can take a device down (reload, erase, reboot) always need a click.", 11.5, color: Ui.Amber);
        warn.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        warn.IsVisible = confirm.IsChecked != true;
        confirm.IsCheckedChanged += (_, _) => warn.IsVisible = confirm.IsChecked != true;

        var ok = Ui.Button("Save", () =>
        {
            settings.VoiceLanguage = languages[Math.Max(0, language.SelectedIndex)].Code;
            settings.VoiceSpeechModel = models[Math.Max(0, speech.SelectedIndex)].Id;
            settings.VoiceBrain = brainIds[Math.Max(0, brain.SelectedIndex)];
            settings.VoiceSpeakReplies = speak.IsChecked == true;
            settings.VoiceConfirmChanges = confirm.IsChecked == true;
            settings.VoiceHandsFree = handsFree.IsChecked == true;
            Close(true);
        }, accent: true);
        ok.IsDefault = true;
        var cancel = Ui.Button("Cancel", () => Close(false));
        cancel.IsCancel = true;
        var buttons = Ui.Row(cancel, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        var tip = Ui.Text("Talk the way you would to a colleague: \"why is port 2 down?\", \"put port 5 in VLAN 10\", \"open the core switch and show me the BGP neighbors\", \"undo that\". Picking your language instead of auto-detect helps with strong accents and very short phrases.", 12, color: Ui.Muted);
        tip.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                Ui.Field("Language you speak", language),
                Ui.Stack(4, Ui.Field("Speech recognition", speech), speechNote),
                Ui.Field("Understanding requests", brain),
                speak, confirm, warn, handsFree, tip, buttons,
            },
        };
    }
}
