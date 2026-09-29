using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.CliGuide;

namespace OmlTerminal.App.Views.Tools;

public sealed class CliRow(CliCommand c)
{
    public CliCommand Source { get; } = c;
    public string Command => Source.Command;
    public string Description => Source.Description;
    public string Mode => CliCommand.ModeLabel(Source.Mode);
    public string SyntaxLine => $"Syntax:  {Source.Syntax}";
    public string ExampleLine => $"Example:  {Source.Example}";
    public Visibility HasSyntax => Source.Syntax.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasExample => Source.Example.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasDetail => Source.Syntax.Length + Source.Example.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class CliGuideView : UserControl, IToolView
{
    private const string AllCategories = "All categories";
    private readonly ToolContext _ctx;
    private CliVendor _vendor;

    public CliGuideView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var v in CliGuideLibrary.Vendors) VendorBox.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v });
        _vendor = CliGuideLibrary.Vendors[0];
        // Prefer the vendor matching the active session's device, if we can guess it.
        VendorBox.SelectedIndex = 0;
    }

    private void Vendor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((VendorBox.SelectedItem as ComboBoxItem)?.Tag is not CliVendor v) return;
        _vendor = v;
        RebuildCategories();
        Refresh();
    }

    private void RebuildCategories()
    {
        CategoryList.SelectionChanged -= Category_SelectionChanged;
        CategoryList.Items.Clear();
        CategoryList.Items.Add(AllCategories);
        foreach (var c in _vendor.Categories) CategoryList.Items.Add(c);
        CategoryList.SelectedIndex = 0;
        CategoryList.SelectionChanged += Category_SelectionChanged;
    }

    private void Category_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        if (CommandList is null) return;
        var category = CategoryList.SelectedItem as string;
        if (category is null or AllCategories) category = null;
        var results = CliGuideLibrary.Search(_vendor, SearchBox.Text, category);
        CommandList.ItemsSource = results.Select(c => new CliRow(c)).ToList();
        CountText.Text = $"{results.Count} / {_vendor.Commands.Count}";
    }

    private static CliRow? Row(object sender) => (sender as FrameworkElement)?.Tag as CliRow;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } r) ToolUi.Copy(r.Command);
    }

    private void Insert_Click(object sender, RoutedEventArgs e) => SendCommand(Row(sender), run: false);
    private void Run_Click(object sender, RoutedEventArgs e) => SendCommand(Row(sender), run: true);

    private void SendCommand(CliRow? row, bool run)
    {
        if (row is null) return;
        // A placeholder like <if> or <name> is a template - insert (never auto-run) so the user fills it in.
        var command = row.Command;
        bool template = command.Contains('<');
        bool ok = run && !template
            ? _ctx.SendToActiveSession(command)
            : _ctx.InsertIntoActiveSession(command);
        if (!ok) CountText.Text = "No active session - open a terminal tab first";
    }

    public void Shutdown() { }
}
