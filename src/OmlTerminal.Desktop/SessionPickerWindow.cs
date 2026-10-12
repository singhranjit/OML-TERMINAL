using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using OmlTerminal.Core.Models;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>Pick what opens in the new pane of a split: the same session again, a local shell, or any saved session.
/// Closes with the chosen profile, or null.</summary>
public sealed class SessionPickerWindow : Window
{
    private sealed record Choice(string Title, string Detail, SessionProfile Profile);

    private readonly List<Choice> _all = new();
    private readonly ListBox _list = new();
    private readonly TextBox _search = Ui.Input("", "Search name, host, folder");

    public SessionPickerWindow(IReadOnlyList<SessionProfile> sessions, SessionProfile current)
    {
        Title = "Open in split";
        Width = 520;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all.Add(new Choice($"{current.Name} (again)", "A second connection to the session in this tab", current));
        _all.Add(new Choice("Local Shell", "Your default shell on this computer", new SessionProfile { Name = "Local Shell", Protocol = ProtocolKind.Local }));
        foreach (var s in sessions.OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            _all.Add(new Choice(s.Name, $"{(s.Folder.Length > 0 ? s.Folder + " · " : "")}{s.Protocol.ToString().ToUpperInvariant()} {s.Host}", s));

        _list.ItemTemplate = new FuncDataTemplate<Choice>((c, _) => c is null ? new Panel() : new StackPanel { Margin = new Thickness(2, 1), Children = {
            new TextBlock { Text = c.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = c.Detail, FontSize = 11.5, Foreground = Ui.Muted } } });
        _list.DoubleTapped += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { e.Handled = true; _list.Focus(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        };

        var open = Ui.Button("Open", Accept, accent: true);
        var cancel = Ui.Button("Cancel", () => Close(null));
        cancel.IsCancel = true;
        var buttons = Ui.Row(cancel, open);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        _search.Margin = new Thickness(0, 0, 0, 8);
        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(_search);
        dock.Children.Add(buttons);
        dock.Children.Add(new Border { BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list });
        Content = dock;
        Filter();
        Opened += (_, _) => _search.Focus();
    }

    private void Filter()
    {
        var q = (_search.Text ?? "").Trim();
        var shown = q.Length == 0 ? _all : _all.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = shown;
        _list.SelectedIndex = shown.Count > 0 ? 0 : -1;
    }

    private void Accept()
    {
        if (_list.SelectedItem is Choice c) Close(c.Profile);
    }
}
