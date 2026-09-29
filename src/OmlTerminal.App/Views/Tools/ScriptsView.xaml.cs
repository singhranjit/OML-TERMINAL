using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Scripting;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class ScriptsView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly ObservableCollection<ScriptDefinition> _scripts = new();
    private readonly ScriptRunner _runner = new();
    private CancellationTokenSource? _cts;

    public ScriptsView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        ScriptList.ItemsSource = _scripts;
        _runner.LineReceived += line => DispatcherQueue.TryEnqueue(() => OutputBox.Text += line + "\n");
        Loaded += (_, _) =>
        {
            SessionBox.ItemsSource = _ctx.Sessions();
            Refresh();
        };
    }

    private void Refresh()
    {
        var dir = ScriptCatalog.DefaultDirectory;
        Directory.CreateDirectory(dir);
        _scripts.Clear();
        foreach (var s in ScriptCatalog.Scan(dir)) _scripts.Add(s);
        StatusText.Text = _scripts.Count == 0 ? $"No scripts yet - drop a .ps1/.py/.sh/.cmd file into {dir}" : $"{_scripts.Count} script(s)";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = ScriptCatalog.DefaultDirectory;
        Directory.CreateDirectory(dir);
        ToolUi.OpenInExplorer(dir);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        if (ScriptList.SelectedItem is not ScriptDefinition script) { StatusText.Text = "Pick a script first."; return; }

        OutputBox.Text = "";
        var device = SessionBox.SelectedItem as SessionProfile;
        var context = new ScriptContext(device?.Name, device?.Host, device?.Username, device?.ProtocolLabel, device?.Folder,
            StdinBox.Text.Length > 0 ? StdinBox.Text : null);

        _cts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "Running...";
        try
        {
            var exit = await _runner.RunAsync(script, context, _cts.Token);
            StatusText.Text = $"Exit code {exit}";
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped."; }
        catch (Exception ex) { OutputBox.Text += $"error: {ex.Message}\n"; StatusText.Text = "Failed."; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    public void Shutdown() => _cts?.Cancel();
}
