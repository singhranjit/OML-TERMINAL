using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OmlTerminal.Core.Models;
using OmlTerminal.Desktop.Controls;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>App settings. Closes with true when saved; the caller applies a master password change.</summary>
public sealed class SettingsWindow : Window
{
    /// <summary>A validated new master password to apply, or null if unchanged.</summary>
    public string? NewMasterPassword { get; private set; }
    public bool RemoveMasterPassword { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        Title = "Settings";
        Width = 560;
        Height = 720;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var font = Ui.Input(settings.FontFamily, "Cascadia Mono, DejaVu Sans Mono...");
        var size = Ui.Number(settings.FontSize, 8, 40);
        var scrollback = Ui.Number(settings.ScrollbackLines, 0, 200000, 1000);
        var logDir = Ui.Input(settings.LogDirectory, mono: true);
        var alwaysLog = Ui.Check("Automatically log every new session to a file", settings.AlwaysLogSessions);
        var reconnect = Ui.Check("Automatically reconnect SSH/Telnet sessions that drop unexpectedly", settings.AutoReconnect);
        var restore = Ui.Check("Restore terminal sessions from the last workspace on launch", settings.RestoreWorkspaceOnLaunch);
        var highlight = Ui.Check("Highlight keywords (up/down, errors, IP addresses) in terminal output", settings.HighlightKeywords);
        var updates = Ui.Check("Check omllabs.com for new versions once a day", settings.CheckForUpdates == true);
        var newMaster = new TextBox { PasswordChar = '•' };
        var confirmMaster = new TextBox { PasswordChar = '•' };
        var removeMaster = Ui.Check("Remove the master password (passwords stay encrypted for your user account)");
        removeMaster.IsVisible = settings.HasMasterPassword;
        var error = Ui.Text("", 12.5, color: Ui.Rose);
        var masterHint = Ui.Text(settings.HasMasterPassword
            ? "A master password is set. Enter a new one to change it."
            : "Encrypts saved passwords with a key derived from a password only you know (AES-256). You'll be asked for it each time the app starts. There is no recovery if you forget it.",
            12, color: Ui.Muted);

        var save = Ui.Button("Save", () =>
        {
            error.Text = "";
            string? master = null;
            bool remove = removeMaster.IsChecked == true;
            var a = newMaster.Text ?? "";
            var b = confirmMaster.Text ?? "";
            if (!remove && (a.Length > 0 || b.Length > 0))
            {
                if (a != b) { error.Text = "The two master passwords do not match."; return; }
                if (a.Length < 6) { error.Text = "Use at least 6 characters."; return; }
                master = a;
            }
            if (!string.IsNullOrWhiteSpace(font.Text)) settings.FontFamily = font.Text.Trim();
            settings.FontSize = (double)(size.Value ?? 14);
            settings.ScrollbackLines = Ui.IntValue(scrollback, 5000);
            if (!string.IsNullOrWhiteSpace(logDir.Text)) settings.LogDirectory = logDir.Text.Trim();
            settings.AlwaysLogSessions = alwaysLog.IsChecked == true;
            settings.AutoReconnect = reconnect.IsChecked == true;
            settings.RestoreWorkspaceOnLaunch = restore.IsChecked == true;
            settings.HighlightKeywords = highlight.IsChecked == true;
            settings.CheckForUpdates = updates.IsChecked == true;
            NewMasterPassword = master;
            RemoveMasterPassword = remove;
            Close(true);
        }, accent: true);
        var cancel = Ui.Button("Cancel", () => Close(false));

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                    [DockPanel.DockProperty] = Dock.Bottom, Children = { cancel, save } },
                new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { error } },
                new ScrollViewer
                {
                    Content = Ui.Stack(12,
                        Ui.Section("Terminal"),
                        Ui.Columns("*,110", Ui.Field("Font (first installed one is used)", font), Ui.Field("Size", size)),
                        Ui.Field("Scrollback lines (applies to new tabs)", scrollback),
                        highlight,
                        Ui.Section("Sessions"),
                        Ui.Columns("*,Auto", Ui.Field("Session log folder", logDir), Ui.Button("Browse...", async () => { if (await ToolUi.PickFolderAsync() is { } p) logDir.Text = p; })),
                        alwaysLog, reconnect, restore,
                        Ui.Section("Updates"), updates,
                        Ui.Section("Master password"), masterHint,
                        Ui.Field("New master password (min 6 characters)", newMaster),
                        Ui.Field("Confirm master password", confirmMaster),
                        removeMaster),
                },
            },
        };
    }
}
