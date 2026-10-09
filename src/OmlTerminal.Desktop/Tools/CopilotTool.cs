using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Copilot;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Desktop.Tools;

public sealed class CopilotTool : UserControl, IToolView
{
    private sealed record ChatRow(string Kind, string Text)
    {
        public string Label => Kind switch
        {
            "user" => "YOU", "assistant" => "COPILOT", "command" => "RUNNING", "output" => "OUTPUT",
            "declined" => "DECLINED", "done" => "DONE", "error" => "ERROR", _ => Kind.ToUpperInvariant(),
        };
        public IBrush Brush => Kind switch
        {
            "error" => Ui.Rose, "declined" => Ui.Amber, "done" => Ui.Mint, "command" => Ui.Sky, "output" => Ui.Muted, _ => Ui.Violet,
        };
    }

    private readonly ToolContext _ctx;
    private readonly LlmClient _llm = new(new HttpClient { Timeout = TimeSpan.FromMinutes(2) });
    private readonly ObservableCollection<ChatRow> _rows = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _pendingConfirm;
    private bool _loaded;

    private readonly ComboBox _device = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Pick a saved SSH session",
        ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
    private readonly TextBox _baseUrl = Ui.Input("", "http://localhost:1234/v1", mono: true), _model = Ui.Input("", "qwen2.5-coder-7b-instruct", mono: true);
    private readonly TextBox _apiKey = new() { PasswordChar = '•', FontFamily = Ui.Mono };
    private readonly TextBlock _apiKeyHint = Ui.Text("", 11, color: Ui.Muted);
    private readonly ToggleSwitch _enabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly ScrollViewer _transcriptScroll;
    private readonly Border _confirmBar;
    private readonly TextBlock _confirmText = new() { TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _input = Ui.Input("", "Ask the copilot to check or fix something on the selected device...");
    private readonly Button _send, _stop;
    private readonly TextBlock _status = Ui.Text("", 12, color: Ui.Muted);

    public CopilotTool(ToolContext ctx)
    {
        _ctx = ctx;
        var transcript = new ItemsControl
        {
            ItemsSource = _rows,
            ItemTemplate = new FuncDataTemplate<ChatRow>((r, _) => r is null ? new Panel() : new Border
            {
                BorderBrush = r.Brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8), Background = Ui.CardBack,
                Child = Ui.Stack(2, new TextBlock { Text = r.Label, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = r.Brush },
                    new SelectableTextBlock { Text = r.Text, TextWrapping = TextWrapping.Wrap, FontFamily = r.Kind is "command" or "output" ? Ui.Mono : FontFamily.Default, FontSize = 12.5 }),
            }),
        };
        _transcriptScroll = new ScrollViewer { Content = transcript };
        _rows.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => _transcriptScroll.ScrollToEnd(), DispatcherPriority.Background);
        _send = Ui.Button("Send", () => _ = SendAsync(), accent: true);
        _stop = Ui.Button("Stop", Stop);
        _stop.IsEnabled = false;
        _confirmBar = new Border
        {
            BorderBrush = Ui.Amber, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Background = Ui.CardBack, IsVisible = false,
            Child = new DockPanel { Children = {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Ui.Button("Approve", () => Answer(true), accent: true), Ui.Button("Decline", () => Answer(false)) } },
                _confirmText } },
        };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); } };
        _baseUrl.TextChanged += (_, _) => SaveConfig();
        _model.TextChanged += (_, _) => SaveConfig();
        _apiKey.LostFocus += (_, _) => SaveKey();
        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (!_loaded) return;
            _ctx.Settings.CopilotEnabled = _enabled.IsChecked == true;
            _ctx.SaveSettings();
            UpdateInputEnabled();
        };

        var bottom = Ui.Stack(8, _confirmBar,
            Ui.Columns("*,Auto,Auto", _input, _send, _stop),
            new DockPanel { Children = { WithDock(Ui.Button("Open audit logs", OpenLogs), Dock.Right), _status } });
        var body = new DockPanel { Children = { WithDock(bottom, Dock.Bottom), Ui.Card(_transcriptScroll) } };
        bottom.Margin = new Thickness(0, 12, 0, 0);
        Content = Ui.Page("AI Copilot",
            "A local-LLM assistant with its own SSH connection to one device. It proposes show/config commands; anything that changes the device waits for your approval, and every step is written to an audit log. Works with any OpenAI-compatible server (LM Studio, Ollama, vLLM).",
            body,
            Ui.Columns("240,*,220,Auto", Ui.Field("Device", _device), Ui.Field("Model endpoint (OpenAI-compatible)", _baseUrl), Ui.Field("Model", _model), Ui.Field("Copilot", _enabled)),
            Ui.Columns("*,*", Ui.Field("API key (only if your endpoint needs one)", _apiKey), _apiKeyHint));
        AttachedToVisualTree += (_, _) =>
        {
            ToolUi.FillSessions(_device, _ctx.SshSessions());
            _loaded = false;
            _baseUrl.Text = _ctx.Settings.CopilotBaseUrl;
            _model.Text = _ctx.Settings.CopilotModel;
            _enabled.IsChecked = _ctx.Settings.CopilotEnabled;
            // Never show the real key - only a hint that one is saved. Typing replaces it; blank keeps it.
            _apiKeyHint.Text = _ctx.Settings.CopilotApiKey.Length > 0 ? "A key is saved - leave blank to keep it." : "";
            _apiKeyHint.VerticalAlignment = VerticalAlignment.Bottom;
            _loaded = true;
            UpdateInputEnabled();
        };
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void SaveConfig()
    {
        if (!_loaded) return;
        _ctx.Settings.CopilotBaseUrl = (_baseUrl.Text ?? "").Trim();
        _ctx.Settings.CopilotModel = (_model.Text ?? "").Trim();
        _ctx.SaveSettings();
    }

    /// <summary>Encrypted at rest (LocalSecret) like saved passwords; never written or shown in plain text.</summary>
    private void SaveKey()
    {
        if (!_loaded || string.IsNullOrEmpty(_apiKey.Text)) return;
        _ctx.Settings.CopilotApiKey = LocalSecret.Protect(_apiKey.Text);
        _ctx.SaveSettings();
        _apiKey.Text = "";
        _apiKeyHint.Text = "A key is saved - leave blank to keep it.";
    }

    private void UpdateInputEnabled()
    {
        var on = _enabled.IsChecked == true;
        _input.IsEnabled = on;
        _send.IsEnabled = on && _cts is null;
        _status.Text = on ? "" : "Copilot is off - flip the switch above to use it.";
    }

    private void AddRow(string kind, string text) => Dispatcher.UIThread.Post(() => _rows.Add(new ChatRow(kind, text)));

    private async Task SendAsync()
    {
        if (_cts is not null) return;
        if (_enabled.IsChecked != true) { _status.Text = "Turn the copilot on first."; return; }
        if (_device.SelectedItem is not SessionProfile device) { _status.Text = "Pick a device first."; return; }
        var request = (_input.Text ?? "").Trim();
        if (request.Length == 0) return;
        if (string.IsNullOrWhiteSpace(_model.Text)) { _status.Text = "Enter a model name (whatever your local server calls it)."; return; }
        SaveKey();
        _input.Text = "";
        AddRow("user", request);

        var apiKey = LocalSecret.Unprotect(_ctx.Settings.CopilotApiKey) ?? "";
        var copilot = new SessionCopilot(_llm, new LlmSettings((_baseUrl.Text ?? "").Trim(), (_model.Text ?? "").Trim(), apiKey));
        var logPath = CopilotAuditLog.Start(device);
        CopilotAuditLog.Append(logPath, $"REQUEST ({device.Name}): {request}");
        _cts = new CancellationTokenSource();
        _send.IsEnabled = false;
        _stop.IsEnabled = true;
        _status.Text = "Working...";
        try
        {
            await foreach (var ev in copilot.RunAsync(device, request, ConfirmAsync, _cts.Token))
            {
                CopilotAuditLog.Append(logPath, $"{ev.Kind.ToUpperInvariant()}: {ev.Text}");
                if (ev.Kind == "confirm-needed") continue; // shown in the confirm bar
                AddRow(ev.Kind, ev.Text);
            }
            _status.Text = "Done.";
        }
        catch (OperationCanceledException) { AddRow("error", "Stopped."); _status.Text = "Stopped."; }
        catch (Exception ex) { AddRow("error", ex.Message); _status.Text = "Failed."; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _send.IsEnabled = _enabled.IsChecked == true;
            _stop.IsEnabled = false;
            _confirmBar.IsVisible = false;
            _pendingConfirm = null;
        }
    }

    /// <summary>Called from the copilot loop (maybe off the UI thread) when a command needs approval; waits for the answer.</summary>
    private Task<bool> ConfirmAsync(string command)
    {
        var tcs = new TaskCompletionSource<bool>();
        _pendingConfirm = tcs;
        Dispatcher.UIThread.Post(() =>
        {
            _confirmText.Text = $"Approve this command on {(_device.SelectedItem as SessionProfile)?.Name}?\n{command}";
            _confirmBar.IsVisible = true;
        });
        return tcs.Task;
    }

    private void Answer(bool approve)
    {
        _pendingConfirm?.TrySetResult(approve);
        _pendingConfirm = null;
        _confirmBar.IsVisible = false;
    }

    private void Stop()
    {
        _pendingConfirm?.TrySetResult(false);
        _cts?.Cancel();
    }

    private void OpenLogs()
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "copilot-logs");
        Directory.CreateDirectory(dir);
        ToolUi.OpenInFileManager(dir);
    }

    public void Shutdown() => Stop();
}
