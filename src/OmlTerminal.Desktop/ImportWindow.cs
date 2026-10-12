using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OmlTerminal.Core.Models;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>"Here's what we found - pick which to bring in": the shared preview for every session import.
/// Returns the chosen profiles, or null if cancelled.</summary>
public sealed class ImportWindow : Window
{
    private readonly List<(SessionProfile Profile, CheckBox Box)> _rows = new();

    public ImportWindow(IReadOnlyList<SessionProfile> found, string source, IReadOnlyCollection<SessionProfile> existing, string? note = null)
    {
        Title = "Import sessions";
        Width = 620;
        Height = 520;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new StackPanel { Spacing = 2 };
        foreach (var p in found)
        {
            bool dup = existing.Any(s => s.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && s.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase));
            var subtitle = p.Protocol == ProtocolKind.Serial ? $"{p.SerialPortName} @ {p.BaudRate} baud"
                : $"{p.Protocol.ToString().ToUpperInvariant()} · {(p.Username.Length > 0 ? p.Username + "@" : "")}{p.Host}:{p.Port}";
            if (p.Folder.Length > 0) subtitle += $" · {p.Folder}";
            if (dup) subtitle += " · already saved";
            var box = new CheckBox
            {
                IsChecked = !dup,
                Content = new StackPanel { Children = {
                    new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = dup ? Ui.Amber : Ui.Muted } } },
            };
            _rows.Add((p, box));
            list.Children.Add(box);
        }
        var count = Ui.Text("", 12, color: Ui.Muted);
        void UpdateCount() => count.Text = $"{_rows.Count(r => r.Box.IsChecked == true)} of {_rows.Count} selected";
        foreach (var r in _rows) r.Box.IsCheckedChanged += (_, _) => UpdateCount();
        UpdateCount();

        var import = Ui.Button("Import", () => Close(_rows.Where(r => r.Box.IsChecked == true).Select(r => r.Profile).ToList()), accent: true);
        import.IsDefault = true;
        var cancel = Ui.Button("Cancel", () => Close(null));
        cancel.IsCancel = true;
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0), Children = {
            WithDock(Ui.Row(cancel, import), Dock.Right),
            Ui.Row(Ui.Button("All", () => SetAll(true)), Ui.Button("None", () => SetAll(false)), count) } };
        var header = Ui.Stack(6, Ui.Text($"Found {found.Count} session(s) in {source}. Select the ones to import.", 13.5));
        if (note is not null) header.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Amber });
        header.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel { Margin = new Thickness(20) };
        dock.Children.Add(WithDock(header, Dock.Top));
        dock.Children.Add(WithDock(bottom, Dock.Bottom));
        dock.Children.Add(new Border
        {
            BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
            Child = new ScrollViewer { Content = list },
        });
        Content = dock;
    }

    private void SetAll(bool on) { foreach (var r in _rows) r.Box.IsChecked = on; }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
}
