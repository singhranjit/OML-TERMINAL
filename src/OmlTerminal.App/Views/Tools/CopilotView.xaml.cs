using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.App.Views.Tools;

public sealed class CopilotChatRow(string kind, string text)
{
    public string Kind { get; } = kind;
    public string Text { get; } = text;
    public string Label => Kind switch
    {
        "user" => "YOU",
        "assistant" => "COPILOT",
        "command" => "RUNNING",
        "output" => "OUTPUT",
        "declined" => "DECLINED",
        "done" => "DONE",
        "error" => "ERROR",
        _ => Kind.ToUpperInvariant(),
    };
    public Brush Brush => ToolUi.Brush(Kind switch
    {
        "error" => "OmlRoseBrush",
        "declined" => "OmlAmberBrush",
        "done" => "OmlMintBrush",
        "command" => "OmlSkyBrush",
        "output" => "StatusIdleBrush",
        _ => "OmlVioletBrush",
    });
}

public sealed partial class CopilotView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly LlmClient _llm = new(new HttpClient { Timeout = TimeSpan.FromMinutes(2) });
    private readonly ObservableCollection<CopilotChatRow> _rows = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _pendingConfirm;
    private string? _logPath;
    private bool _loaded;

    public CopilotView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        Transcript.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            ToolUi.FillSessions(DeviceBox, _ctx.SshSessions());
            BaseUrlBox.Text = _ctx.Settings.CopilotBaseUrl;
            ModelBox.Text = _ctx.Settings.CopilotModel;
            EnabledBox.IsOn = _ctx.Settings.CopilotEnabled;
            // Never populate the box with the real key - only a hint that one is saved. Typing replaces it;
            // leaving it blank keeps whatever's already stored.
            ApiKeyHint.Text = _ctx.Settings.CopilotApiKey.Length > 0 ? "A key is saved - leave blank to keep it." : "";
            _loaded = true;
            UpdateInputEnabled();
        };
    }

    private void Config_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _ctx.Settings.CopilotBaseUrl = BaseUrlBox.Text.Trim();
        _ctx.Settings.CopilotModel = ModelBox.Text.Trim();
        _ctx.SaveSettings();
    }

    /// <summary>Encrypted at rest with Windows DPAPI (see <see cref="LocalSecret"/>) - the same way saved
    /// session/vault passwords are protected when no master password is set. Never written or shown in plaintext.</summary>
    private void ApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || ApiKeyBox.Password.Length == 0) return;
        _ctx.Settings.CopilotApiKey = OmlTerminal.Core.Persistence.LocalSecret.Protect(ApiKeyBox.Password);
        _ctx.SaveSettings();
        ApiKeyHint.Text = "A key is saved - leave blank to keep it.";
    }

    private void Enabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _ctx.Settings.CopilotEnabled = EnabledBox.IsOn;
        _ctx.SaveSettings();
        UpdateInputEnabled();
    }

    private void UpdateInputEnabled()
    {
        var on = EnabledBox.IsOn;
        InputBox.IsEnabled = on;
        SendButton.IsEnabled = on && _cts is null;
        StatusText.Text = on ? "" : "Copilot is off - flip the switch above to use it.";
    }

    private void InputBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Send_Click(sender, new RoutedEventArgs());
    }

    private void AddRow(string kind, string text) => DispatcherQueue.TryEnqueue(() => _rows.Add(new CopilotChatRow(kind, text)));

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        if (!EnabledBox.IsOn) { StatusText.Text = "Turn the copilot on first."; return; }
        var device = DeviceBox.SelectedItem as SessionProfile;
        if (device is null) { StatusText.Text = "Pick a device first."; return; }
        var request = InputBox.Text.Trim();
        if (request.Length == 0) return;
        if (string.IsNullOrWhiteSpace(ModelBox.Text)) { StatusText.Text = "Enter a model name (whatever your local server calls it)."; return; }

        InputBox.Text = "";
        AddRow("user", request);

        var apiKey = OmlTerminal.Core.Persistence.LocalSecret.Unprotect(_ctx.Settings.CopilotApiKey) ?? "";
        var settings = new LlmSettings(BaseUrlBox.Text.Trim(), ModelBox.Text.Trim(), apiKey);
        var copilot = new SessionCopilot(_llm, settings);
        _logPath = CopilotAuditLog.Start(device);
        CopilotAuditLog.Append(_logPath, $"REQUEST ({device.Name}): {request}");

        _cts = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "Working...";
        try
        {
            await foreach (var ev in copilot.RunAsync(device, request, ConfirmAsync, _cts.Token))
            {
                CopilotAuditLog.Append(_logPath, $"{ev.Kind.ToUpperInvariant()}: {ev.Text}");
                if (ev.Kind == "confirm-needed") continue; // shown via the confirm bar, not the transcript
                AddRow(ev.Kind, ev.Text);
            }
            StatusText.Text = "Done.";
        }
        catch (OperationCanceledException)
        {
            AddRow("error", "Stopped.");
            StatusText.Text = "Stopped.";
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _cts?.Dispose();
                _cts = null;
                SendButton.IsEnabled = EnabledBox.IsOn;
                StopButton.IsEnabled = false;
                HideConfirmBar();
            });
        }
    }

    /// <summary>Called from inside the copilot's async loop (possibly off the UI thread) whenever a command needs
    /// approval. Shows the confirm bar and waits for Approve/Decline before the loop continues.</summary>
    private Task<bool> ConfirmAsync(string command)
    {
        var tcs = new TaskCompletionSource<bool>();
        _pendingConfirm = tcs;
        DispatcherQueue.TryEnqueue(() =>
        {
            ConfirmText.Text = $"Approve this command on {(DeviceBox.SelectedItem as SessionProfile)?.Name}?\n{command}";
            ConfirmBar.Visibility = Visibility.Visible;
        });
        return tcs.Task;
    }

    private void HideConfirmBar()
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        _pendingConfirm = null;
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        _pendingConfirm?.TrySetResult(true);
        HideConfirmBar();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        _pendingConfirm?.TrySetResult(false);
        HideConfirmBar();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _pendingConfirm?.TrySetResult(false);
        _cts?.Cancel();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "copilot-logs");
        Directory.CreateDirectory(dir);
        ToolUi.OpenInExplorer(dir);
    }

    public void Shutdown()
    {
        _pendingConfirm?.TrySetResult(false);
        _cts?.Cancel();
    }
}
