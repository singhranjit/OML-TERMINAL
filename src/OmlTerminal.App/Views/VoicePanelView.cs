using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.App.Views.Tools;
using OmlTerminal.Core.Voice.Assistant;
using Windows.UI;

namespace OmlTerminal.App.Views;

/// <summary>
/// The voice assistant's panel beside the terminal: a big mic button, the conversation (what was heard, what the
/// assistant said and ran, output, changes waiting for approval) and a box to type instead of talk. The thinking
/// lives in Core (<see cref="VoiceSession"/>); this only shows it and feeds it. Built in code to match the
/// Linux/macOS panel one-for-one.
/// </summary>
public sealed class VoicePanelView : UserControl
{
    private readonly StackPanel _conversation = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly Button _mic;
    private readonly FontIcon _micIcon = new() { Glyph = "", FontSize = 24 };
    private readonly Border _levelRing = new() { Width = 74, Height = 74, CornerRadius = new CornerRadius(37), BorderThickness = new Thickness(3), IsHitTestVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12.5, Opacity = 0.75 };
    private readonly TextBlock _engines = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.55, TextAlignment = TextAlignment.Center };
    private readonly TextBox _input = new() { PlaceholderText = "Or type: \"why is port 2 down?\"" };
    private readonly Border _setup;
    private readonly StackPanel _setupBody = new() { Spacing = 8 };

    public event Action? MicClicked;
    public event Action? SettingsClicked;
    public event Action? CloseClicked;
    public event Action<string>? Typed;
    public event Action? NewTopic;

    private static Brush B(string key) => ToolUi.Brush(key);
    private static readonly SolidColorBrush MicIdle = new(Color.FromArgb(255, 0x1B, 0x2B, 0x36));

    public VoicePanelView()
    {
        _mic = new Button
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Padding = new Thickness(0), Background = MicIdle,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Content = _micIcon,
        };
        _micIcon.Foreground = B("OmlSkyBrush");
        ToolTipService.SetToolTip(_mic, "Talk (Ctrl+Shift+Space)");
        _mic.Click += (_, _) => MicClicked?.Invoke();
        _levelRing.BorderBrush = new SolidColorBrush(Colors.Transparent);
        var micStack = new Grid { Width = 74, Height = 74, HorizontalAlignment = HorizontalAlignment.Center };
        micStack.Children.Add(_levelRing);
        micStack.Children.Add(_mic);

        var fresh = new Button { Content = "New topic", FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
        ToolTipService.SetToolTip(fresh, "Forget the conversation so far");
        fresh.Click += (_, _) => { _conversation.Children.Clear(); NewTopic?.Invoke(); };
        var settings = new Button { Content = "Settings", FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
        settings.Click += (_, _) => SettingsClicked?.Invoke();
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(8, 4, 8, 4), Background = new SolidColorBrush(Colors.Transparent) };
        close.Click += (_, _) => CloseClicked?.Invoke();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Voice", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { fresh, settings, close } };
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        _setup = new Border
        {
            BorderBrush = B("OmlAmberBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10),
            Child = _setupBody, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10),
        };

        _scroll = new ScrollViewer { Content = _conversation, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Submit(); } };
        var send = new Button { Content = "Send", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        send.Click += (_, _) => Submit();
        var bottom = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_input);
        Grid.SetColumn(send, 1);
        bottom.Children.Add(send);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10), Children = { micStack, _status, _engines } };
        var root = new Grid { Padding = new Thickness(12) };
        foreach (var _ in Enumerable.Range(0, 5)) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
        var convoBorder = new Border { BorderBrush = B("HairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = _scroll };
        FrameworkElement[] rows = [header, _setup, top, convoBorder, bottom];
        for (int i = 0; i < rows.Length; i++) { Grid.SetRow(rows[i], i); root.Children.Add(rows[i]); }
        Content = root;
        SetStatus("Click the mic and say what you need - in any language.");
    }

    private void Submit()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        _input.Text = "";
        Typed?.Invoke(t);
    }

    // ---------- state ----------

    public void SetListening(bool on)
    {
        _micIcon.Glyph = on ? "" : ""; // stop square vs microphone
        _micIcon.Foreground = on ? new SolidColorBrush(Colors.White) : B("OmlSkyBrush");
        _mic.Background = on ? B("OmlRoseBrush") : MicIdle;
        ToolTipService.SetToolTip(_mic, on ? "Stop listening (Ctrl+Shift+Space)" : "Talk (Ctrl+Shift+Space)");
        if (!on) SetLevel(0);
    }

    public void SetLevel(double level) =>
        _levelRing.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(level * 900, 0, 255), 0xFB, 0x71, 0x85));

    public void SetStatus(string text) => _status.Text = text;
    public void SetEngines(string text) => _engines.Text = text;

    public void ShowSetup(string? message, params UIElement[] actions)
    {
        _setupBody.Children.Clear();
        _setup.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (message is null) return;
        _setupBody.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        foreach (var a in actions) _setupBody.Children.Add(a);
    }

    // ---------- conversation ----------

    public void AddHeard(string text, string? language) =>
        Add(Bubble(language is { Length: > 0 } && language != "English" ? $"You · {language}" : "You", text, B("OmlSkyBrush"), right: true));

    public void AddSaid(string text) => Add(Bubble("OML Voice", text, B("OmlMintBrush")));

    public void AddNote(string text, Brush? color = null) =>
        Add(new TextBlock { Text = text, FontSize = 12, Foreground = color ?? B("StatusIdleBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0), IsTextSelectionEnabled = true });

    public void AddRun(string command) =>
        Add(new TextBlock { Text = "› " + command, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 12, Foreground = B("OmlSkyBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0) });

    public void AddOutput(string command, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        var lines = output.Replace("\r", "").Split('\n');
        var body = new TextBlock { Text = output, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 11.5, IsTextSelectionEnabled = true };
        Add(new Expander
        {
            Header = $"output · {lines.Length} line(s)",
            Content = new ScrollViewer { Content = body, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled },
            IsExpanded = lines.Length <= 6, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });
    }

    public void AddError(string text) => AddNote(text, B("OmlRoseBrush"));

    /// <summary>A change waiting for approval. Completes with the click; disabled once answered (by click or voice).</summary>
    public Task<bool> AddConfirmation(VoiceConfirmation c, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var destructive = c.Risk == VoiceRisk.Destructive;
        var accent = destructive ? B("OmlRoseBrush") : B("OmlAmberBrush");
        var apply = new Button { Content = destructive ? "Apply (can disrupt service)" : "Apply", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "Cancel" };
        apply.Click += (_, _) => tcs.TrySetResult(true);
        cancel.Click += (_, _) => tcs.TrySetResult(false);
        var hint = new TextBlock
        {
            Text = destructive ? "This can take the device or a service down - click Apply to confirm." : "Say \"yes\" or click Apply. Say \"no\" to cancel.",
            FontSize = 11.5, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        };
        var card = new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10),
            Child = new StackPanel { Spacing = 6, Children = {
                new TextBlock { Text = $"Apply to {c.Target}?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = accent },
                new TextBlock { Text = string.Join('\n', c.Actions.Select(a => a.ToString())), FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                hint,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } } } },
        };
        Add(card);
        ct.Register(() => tcs.TrySetResult(false));
        var queue = DispatcherQueue;
        _ = tcs.Task.ContinueWith(t => queue.TryEnqueue(() =>
        {
            apply.IsEnabled = cancel.IsEnabled = false;
            hint.Text = t.Result ? "Applied." : "Cancelled.";
            card.Opacity = 0.75;
        }));
        return tcs.Task;
    }

    private void Add(UIElement c)
    {
        _conversation.Children.Add(c);
        while (_conversation.Children.Count > 200) _conversation.Children.RemoveAt(0);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null));
    }

    private static Border Bubble(string who, string text, Brush accent, bool right = false) => new()
    {
        BorderBrush = accent, BorderThickness = right ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0),
        Background = B("InputSurfaceBrush"), Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(4),
        Margin = right ? new Thickness(24, 0, 0, 0) : new Thickness(0, 0, 24, 0),
        Child = new StackPanel { Spacing = 2, Children = {
            new TextBlock { Text = who, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = accent },
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, IsTextSelectionEnabled = true } } },
    };
}
