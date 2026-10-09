using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OmlTerminal.Desktop;

/// <summary>Small modal message, confirm and prompt dialogs, built in code so every one looks the same.</summary>
public static class Dialogs
{
    private static Window Shell(string title, Control body, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) row.Children.Add(b);
        var panel = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(row, Dock.Bottom);
        row.Margin = new Thickness(0, 18, 0, 0);
        panel.Children.Add(row);
        panel.Children.Add(body);
        return new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 380,
            MaxWidth = 620,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    private static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };

    public static async Task MessageAsync(Window owner, string title, string text)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var w = Shell(title, Body(text), ok);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string text, string okText = "OK")
    {
        var ok = new Button { Content = okText, IsDefault = true, Classes = { "accent" }, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var w = Shell(title, Body(text), cancel, ok);
        ok.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        return await w.ShowDialog<bool>(owner);
    }

    /// <summary>A larger free-text box (lists, pasted config). Returns the text, or null if cancelled.</summary>
    public static async Task<string?> PromptMultilineAsync(Window owner, string title, string text, string placeholder = "", string okText = "OK", string initial = "")
    {
        var box = new TextBox
        {
            Text = initial, PlaceholderText = placeholder, AcceptsReturn = true, Height = 260, Width = 520, Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.NoWrap, FontFamily = Tools.Ui.Mono, FontSize = 12.5,
        };
        var stack = new StackPanel { Children = { Body(text), box } };
        var ok = new Button { Content = okText, Classes = { "accent" }, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var w = Shell(title, stack, cancel, ok);
        w.MaxWidth = 700;
        ok.Click += (_, _) => w.Close(box.Text ?? "");
        cancel.Click += (_, _) => w.Close(null);
        w.Opened += (_, _) => box.Focus(NavigationMethod.Tab);
        return await w.ShowDialog<string?>(owner);
    }

    /// <summary>Returns the entered text, or null if cancelled.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string text, bool password = false, string okText = "OK", string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 0), MinWidth = 340 };
        if (password) box.PasswordChar = '•';
        var stack = new StackPanel { Children = { Body(text), box } };
        var ok = new Button { Content = okText, IsDefault = true, Classes = { "accent" }, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var w = Shell(title, stack, cancel, ok);
        ok.Click += (_, _) => w.Close(box.Text ?? "");
        cancel.Click += (_, _) => w.Close(null);
        w.Opened += (_, _) => box.Focus(NavigationMethod.Tab);
        return await w.ShowDialog<string?>(owner);
    }
}
