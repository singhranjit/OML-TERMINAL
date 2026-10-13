using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>
/// The voice assistant's home: a big mic button, the conversation (what was heard, what the assistant said and ran,
/// output, changes waiting for approval) and a box to type instead of talk. All the thinking lives in Core
/// (<see cref="VoiceAssistant"/>); this only shows it and feeds it.
/// </summary>
public sealed class VoicePanel : UserControl
{
    private const string MicPath = "M12 14a3 3 0 0 0 3-3V5a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.92V21h2v-3.08A7 7 0 0 0 19 11h-2z";
    private const string StopPath = "M7 7h10v10H7z";

    private readonly StackPanel _conversation = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly Button _mic;
    private readonly PathIcon _micIcon = new() { Data = Geometry.Parse(MicPath), Width = 26, Height = 26 };
    private readonly Border _levelRing = new() { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, IsHitTestVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12.5, Foreground = Ui.Muted };
    private readonly TextBlock _engines = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Ui.Muted, TextAlignment = TextAlignment.Center };
    private readonly TextBox _input = Ui.Input("", "Or type: \"why is port 2 down?\"");
    private readonly Border _setup;
    private readonly StackPanel _setupBody = new() { Spacing = 8 };

    public event Action? MicClicked;
    public event Action? SettingsClicked;
    public event Action? CloseClicked;
    public event Action<string>? Typed;
    public event Action? NewTopic;

    public VoicePanel()
    {
        _mic = new Button
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Ui.Solid(0x1B2B36), Content = _micIcon,
        };
        _micIcon.Foreground = Ui.Sky;
        ToolTip.SetTip(_mic, "Talk (Ctrl+Shift+Space)");
        _mic.Click += (_, _) => MicClicked?.Invoke();
        var micStack = new Panel { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center, Children = { _levelRing, _mic } };
        _mic.HorizontalAlignment = HorizontalAlignment.Center;
        _mic.VerticalAlignment = VerticalAlignment.Center;

        var gear = new Button { Content = "Settings", Padding = new Thickness(8, 2), FontSize = 12 };
        gear.Click += (_, _) => SettingsClicked?.Invoke();
        var fresh = new Button { Content = "New topic", Padding = new Thickness(8, 2), FontSize = 12 };
        ToolTip.SetTip(fresh, "Forget the conversation so far");
        fresh.Click += (_, _) => { _conversation.Children.Clear(); NewTopic?.Invoke(); };
        var close = new Button { Content = "×", Padding = new Thickness(8, 0), Background = Brushes.Transparent };
        close.Click += (_, _) => CloseClicked?.Invoke();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = {
            Dock(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { fresh, gear, close } }, global::Avalonia.Controls.Dock.Right),
            new TextBlock { Text = "Voice", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };

        _setup = new Border
        {
            BorderBrush = Ui.Amber, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10),
            Background = Ui.Solid(0x221E14), Child = _setupBody, IsVisible = false, Margin = new Thickness(0, 0, 0, 10),
        };

        _scroll = new ScrollViewer { Content = _conversation, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        var send = Ui.Button("Send", Submit, accent: true);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Children = { Dock(send, global::Avalonia.Controls.Dock.Right), _input } };
        _input.Margin = new Thickness(0, 0, 8, 0);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10), Children = { micStack, _status, _engines } };
        var dock = new DockPanel { Margin = new Thickness(12) };
        foreach (var (c, d) in new (Control, Dock)[] { (header, global::Avalonia.Controls.Dock.Top), (_setup, global::Avalonia.Controls.Dock.Top), (top, global::Avalonia.Controls.Dock.Top), (bottom, global::Avalonia.Controls.Dock.Bottom) })
            dock.Children.Add(Dock(c, d));
        dock.Children.Add(new Border { BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = _scroll });
        Content = new Border { Background = Ui.Solid(0x111418), BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1, 0, 0, 0), Child = dock };
        SetStatus("Click the mic and say what you need - in any language.");
    }

    private static T Dock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void Submit()
    {
        var t = (_input.Text ?? "").Trim();
        if (t.Length == 0) return;
        _input.Text = "";
        Typed?.Invoke(t);
    }

    public void FocusInput() => _input.Focus();

    // ---------- state ----------

    public void SetListening(bool on)
    {
        _micIcon.Data = Geometry.Parse(on ? StopPath : MicPath);
        _micIcon.Foreground = on ? Brushes.White : Ui.Sky;
        _mic.Background = on ? Ui.Rose : Ui.Solid(0x1B2B36);
        ToolTip.SetTip(_mic, on ? "Stop listening (Ctrl+Shift+Space)" : "Talk (Ctrl+Shift+Space)");
        if (!on) SetLevel(0);
    }

    public void SetLevel(double level)
    {
        var a = (byte)Math.Clamp(level * 900, 0, 255);
        _levelRing.BorderBrush = new SolidColorBrush(Color.FromArgb(a, 0xFB, 0x71, 0x85));
    }

    public void SetStatus(string text) => _status.Text = text;
    public void SetEngines(string text) => _engines.Text = text;

    /// <summary>Shows what's missing before voice can work (models to download), with the actions to fix it.</summary>
    public void ShowSetup(string? message, params Control[] actions)
    {
        _setupBody.Children.Clear();
        _setup.IsVisible = message is not null;
        if (message is null) return;
        _setupBody.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        foreach (var a in actions) _setupBody.Children.Add(a);
    }

    // ---------- conversation ----------

    public void AddHeard(string text, string? language)
    {
        Add(Bubble(language is { Length: > 0 } && language != "English" ? $"You · {language}" : "You", text, Ui.Sky, right: true));
    }

    public void AddSaid(string text) => Add(Bubble("OML Voice", text, Ui.Mint));

    public void AddNote(string text, IBrush? color = null) =>
        Add(new TextBlock { Text = text, FontSize = 12, Foreground = color ?? Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0) });

    public void AddRun(string command) =>
        Add(new TextBlock { Text = "› " + command, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Sky, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0) });

    public void AddOutput(string command, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        var lines = output.Replace("\r", "").Split('\n');
        var body = new SelectableTextBlock { Text = output, FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.NoWrap };
        var expander = new Expander
        {
            Header = new TextBlock { Text = $"output · {lines.Length} line(s)", FontSize = 11.5, Foreground = Ui.Muted },
            Content = new ScrollViewer { Content = body, MaxHeight = 260, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            IsExpanded = lines.Length <= 6, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Add(expander);
    }

    public void AddError(string text) => AddNote(text, Ui.Rose);

    /// <summary>A change waiting for approval. Completes with the click; disabled once answered (by click or voice).</summary>
    public Task<bool> AddConfirmation(VoiceConfirmation c, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var destructive = c.Risk == VoiceRisk.Destructive;
        var accent = destructive ? Ui.Rose : Ui.Amber;
        var apply = Ui.Button(destructive ? "Apply (can disrupt service)" : "Apply", () => tcs.TrySetResult(true), accent: true);
        var cancel = Ui.Button("Cancel", () => tcs.TrySetResult(false));
        var hint = new TextBlock
        {
            Text = destructive ? "This can take the device or a service down - click Apply to confirm." : "Say \"yes\" or click Apply. Say \"no\" to cancel.",
            FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap,
        };
        var card = new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Background = Ui.Solid(0x1E1A12),
            Child = new StackPanel { Spacing = 6, Children = {
                new TextBlock { Text = $"Apply to {c.Target}?", FontWeight = FontWeight.SemiBold, Foreground = accent },
                new SelectableTextBlock { Text = string.Join('\n', c.Actions.Select(a => a.ToString())), FontFamily = Ui.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                hint,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } } } },
        };
        Add(card);
        ct.Register(() => tcs.TrySetResult(false));
        _ = tcs.Task.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            apply.IsEnabled = cancel.IsEnabled = false;
            hint.Text = t.Result ? "Applied." : "Cancelled.";
            card.Opacity = 0.75;
        }));
        return tcs.Task;
    }

    /// <summary>Marks the newest confirmation card as answered by voice (the click handlers can't see that).</summary>
    public void Add(Control c)
    {
        _conversation.Children.Add(c);
        while (_conversation.Children.Count > 200) _conversation.Children.RemoveAt(0);
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static Control Bubble(string who, string text, IBrush accent, bool right = false) => new Border
    {
        BorderBrush = accent, BorderThickness = right ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0),
        Background = Ui.CardBack, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(4),
        Margin = right ? new Thickness(24, 0, 0, 0) : new Thickness(0, 0, 24, 0),
        Child = new StackPanel { Spacing = 2, Children = {
            new TextBlock { Text = who, FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = accent },
            new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 } } },
    };
}
