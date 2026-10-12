using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using OmlTerminal.Core.Workflows;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>Edit saved troubleshooting workflows (named lists of show commands) and pick one to run.
/// Closes with a <see cref="Result"/>, or null if cancelled.</summary>
public sealed class WorkflowWindow : Window
{
    public sealed record Result(List<TroubleshootingWorkflow> Workflows, TroubleshootingWorkflow? Run);

    private readonly List<TroubleshootingWorkflow> _workflows;
    private readonly ListBox _list = new() { ItemTemplate = new FuncDataTemplate<TroubleshootingWorkflow>((w, _) => new TextBlock { Text = w?.Name }) };
    private readonly TextBox _name = Ui.Input(), _description = Ui.Input();
    private readonly TextBox _commands = Ui.MultiInput("", 220, "show version\nshow interfaces status\nshow logging | last 50");
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private TroubleshootingWorkflow? _loaded;
    private bool _loading;

    public WorkflowWindow(IEnumerable<TroubleshootingWorkflow> workflows)
    {
        Title = "Troubleshooting Workflows";
        Width = 820;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _workflows = workflows.ToList();
        _commands.FontFamily = Ui.Mono;
        _banner = Ui.Banner(out _bannerText, Ui.Rose);
        _list.SelectionChanged += (_, _) => { if (!_loading) Capture(); Load(); };
        _name.LostFocus += (_, _) => { Capture(); Rebind(); }; // show the new name in the list

        var left = new DockPanel { Children = {
            WithDock(Ui.Row(Ui.Button("New", New), Ui.Button("Delete", Delete)), Dock.Bottom),
            new Border { BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 8), Child = _list } } };
        var right = Ui.Stack(10,
            Ui.Field("Name", _name), Ui.Field("Description", _description),
            Ui.Field("Commands (one per line, sent in order)", _commands),
            Ui.Text("Workflows type their commands into the selected session exactly as written - keep them to read-only show commands unless you mean otherwise.", 12, color: Ui.Muted));
        ((TextBlock)right.Children[^1]).TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("240,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var save = Ui.Button("Save", () => Commit(run: false));
        var run = Ui.Button("Save and run on current tab", () => Commit(run: true), accent: true);
        var cancel = Ui.Button("Cancel", () => Close(null));
        cancel.IsCancel = true;
        var bottom = Ui.Row(cancel, save, run);
        bottom.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Margin = new Thickness(0, 12, 0, 0);
        var dock = new DockPanel { Margin = new Thickness(20) };
        dock.Children.Add(WithDock(bottom, Dock.Bottom));
        dock.Children.Add(WithDock(_banner, Dock.Bottom));
        dock.Children.Add(body);
        Content = dock;

        Rebind();
        _list.SelectedIndex = _workflows.Count > 0 ? 0 : -1;
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void Rebind()
    {
        _loading = true;
        var sel = _list.SelectedItem;
        _list.ItemsSource = null;
        _list.ItemsSource = _workflows.ToList();
        _list.SelectedItem = sel;
        _loading = false;
    }

    private void Load()
    {
        _loaded = _list.SelectedItem as TroubleshootingWorkflow;
        _loading = true;
        _name.Text = _loaded?.Name ?? "";
        _description.Text = _loaded?.Description ?? "";
        _commands.Text = _loaded is null ? "" : string.Join("\n", _loaded.Commands);
        _name.IsEnabled = _description.IsEnabled = _commands.IsEnabled = _loaded is not null;
        _loading = false;
    }

    private void Capture()
    {
        if (_loading || _loaded is not { } w) return;
        w.Name = (_name.Text ?? "").Trim();
        w.Description = (_description.Text ?? "").Trim();
        w.Commands = (_commands.Text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private void New()
    {
        Capture();
        var w = new TroubleshootingWorkflow { Name = "New workflow", Commands = ["show version"] };
        _workflows.Add(w);
        Rebind();
        _list.SelectedItem = w;
    }

    private void Delete()
    {
        if (_list.SelectedItem is not TroubleshootingWorkflow w) return;
        _workflows.Remove(w);
        _loaded = null;
        Rebind();
        _list.SelectedIndex = _workflows.Count > 0 ? 0 : -1;
        Load();
    }

    private void Commit(bool run)
    {
        Capture();
        Rebind(); // names may have changed
        var selected = _list.SelectedItem as TroubleshootingWorkflow;
        if (run && selected is null) { Ui.Show(_banner, _bannerText, "Select a workflow to run."); return; }
        foreach (var w in _workflows)
        {
            var errors = w.Validate();
            if (errors.Count > 0) { Ui.Show(_banner, _bannerText, $"{(w.Name.Length > 0 ? w.Name : "A workflow")}: {string.Join(" ", errors)}"); return; }
        }
        Close(new Result(_workflows, run ? selected : null));
    }
}
