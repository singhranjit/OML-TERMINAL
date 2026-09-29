using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Workflows;

namespace OmlTerminal.App.Views;

public sealed partial class WorkflowDialog : ContentDialog
{
    private readonly List<TroubleshootingWorkflow> _workflows;
    private TroubleshootingWorkflow? _loadedWorkflow;
    private bool _loading;
    public IReadOnlyList<TroubleshootingWorkflow> Workflows => _workflows;
    public TroubleshootingWorkflow? RunWorkflow { get; private set; }

    public WorkflowDialog(XamlRoot root, IEnumerable<TroubleshootingWorkflow> workflows)
    {
        InitializeComponent();
        XamlRoot = root;
        RequestedTheme = ElementTheme.Dark;
        _workflows = workflows.ToList();
        Rebind();
        WorkflowList.SelectedIndex = _workflows.Count > 0 ? 0 : -1;
        PrimaryButtonClick += (_, e) => Commit(e, run: false);
        SecondaryButtonClick += (_, e) => Commit(e, run: true);
    }

    private void Rebind()
    {
        WorkflowList.ItemsSource = null;
        WorkflowList.ItemsSource = _workflows;
        WorkflowList.DisplayMemberPath = nameof(TroubleshootingWorkflow.Name);
    }

    private void LoadSelected()
    {
        if (WorkflowList.SelectedItem is not TroubleshootingWorkflow workflow)
        {
            _loadedWorkflow = null;
            _loading = true;
            NameBox.Text = DescriptionBox.Text = CommandsBox.Text = "";
            _loading = false;
            return;
        }
        _loadedWorkflow = workflow;
        _loading = true;
        NameBox.Text = workflow.Name;
        DescriptionBox.Text = workflow.Description;
        CommandsBox.Text = string.Join("\n", workflow.Commands);
        _loading = false;
    }

    private void CaptureSelected()
    {
        if (_loading || _loadedWorkflow is not { } workflow) return;
        workflow.Name = NameBox.Text.Trim();
        workflow.Description = DescriptionBox.Text.Trim();
        workflow.Commands = CommandsBox.Text.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) CaptureSelected();
        LoadSelected();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelected();
        var workflow = new TroubleshootingWorkflow { Name = "New workflow", Commands = new() { "show version" } };
        _workflows.Add(workflow);
        Rebind();
        WorkflowList.SelectedItem = workflow;
        LoadSelected();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (WorkflowList.SelectedItem is not TroubleshootingWorkflow selected) return;
        _workflows.Remove(selected);
        Rebind();
        WorkflowList.SelectedIndex = _workflows.Count > 0 ? 0 : -1;
        LoadSelected();
    }

    private void Commit(ContentDialogButtonClickEventArgs e, bool run)
    {
        CaptureSelected();
        ErrorBar.IsOpen = false;
        var selected = WorkflowList.SelectedItem as TroubleshootingWorkflow;
        if (selected is null)
        {
            if (run) { ErrorBar.Message = "Select a workflow to run."; ErrorBar.IsOpen = true; e.Cancel = true; return; }
        }
        foreach (var workflow in _workflows)
        {
            var errors = workflow.Validate();
            if (errors.Count > 0) { ErrorBar.Message = $"{workflow.Name}: {string.Join(" ", errors)}"; ErrorBar.IsOpen = true; e.Cancel = true; return; }
        }
        RunWorkflow = run ? selected : null;
    }
}
