using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.CliGuide;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Monitoring;
using OmlTerminal.Core.Scripting;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Desktop.Tools;

public sealed class CliGuideTool : UserControl, IToolView
{
    private const string AllCategories = "All categories";
    private readonly ToolContext _ctx;
    private CliVendor _vendor = CliGuideLibrary.Vendors[0];
    private readonly ComboBox _vendorBox = Ui.Combo(CliGuideLibrary.Vendors.Select(v => v.Name).ToList());
    private readonly TextBox _search = Ui.Input("", "show ip route, vlan, ospf neighbor...");
    private readonly TextBlock _count = Ui.Text("", 13, mono: true, color: Ui.Muted);
    private readonly ListBox _categories = new() { Background = Brushes.Transparent };
    private readonly ItemsControl _commands = new();

    public CliGuideTool(ToolContext ctx)
    {
        _ctx = ctx;
        _vendorBox.SelectionChanged += (_, _) =>
        {
            if (_vendorBox.SelectedIndex < 0) return;
            _vendor = CliGuideLibrary.Vendors[_vendorBox.SelectedIndex];
            _categories.ItemsSource = new[] { AllCategories }.Concat(_vendor.Categories).ToList();
            _categories.SelectedIndex = 0;
            Refresh();
        };
        _categories.SelectionChanged += (_, _) => Refresh();
        _search.TextChanged += (_, _) => Refresh();
        _commands.ItemTemplate = new FuncDataTemplate<CliCommand>((c, _) =>
        {
            if (c is null) return new Panel();
            var actions = Ui.Row(
                Ui.Button("Copy", () => ToolUi.Copy(c.Command)),
                Ui.Button("Insert", () => Send(c, run: false), tip: "Type it into the active terminal without pressing Enter"),
                Ui.Button("Run", () => Send(c, run: true), tip: "Type it into the active terminal and press Enter (templates with <placeholders> are only inserted)"));
            DockPanel.SetDock(actions, Dock.Right);
            var detail = Ui.Stack(2,
                new SelectableTextBlock { Text = c.Command, FontFamily = Ui.Mono, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Sky },
                new TextBlock { Text = $"{c.Description}  ·  {CliCommand.ModeLabel(c.Mode)}", FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Syntax:  {c.Syntax}", FontFamily = Ui.Mono, FontSize = 11.5, Foreground = Ui.Muted, IsVisible = c.Syntax.Length > 0, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Example:  {c.Example}", FontFamily = Ui.Mono, FontSize = 11.5, Foreground = Ui.Muted, IsVisible = c.Example.Length > 0, TextWrapping = TextWrapping.Wrap });
            return new Border
            {
                Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8), Background = Ui.CardBack,
                Child = new DockPanel { Children = { actions, detail } },
            };
        });
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("220,*"), ColumnSpacing = 16 };
        body.Children.Add(Ui.Card(new DockPanel { Children = { WithDock(Ui.Section("Categories"), Dock.Top), new ScrollViewer { Content = _categories, Margin = new Thickness(0, 8, 0, 0) } } }));
        var list = new ScrollViewer { Content = _commands };
        Grid.SetColumn(list, 1);
        body.Children.Add(list);
        Content = Ui.Page("CLI Guide", "Searchable command reference for every supported vendor. Insert types a command into the active terminal; Run also presses Enter.", body,
            Ui.Columns("240,*,Auto", Ui.Field("Vendor", _vendorBox), Ui.Field("Search", _search), _count));
        _vendorBox.SelectedIndex = 0;
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void Refresh()
    {
        var category = _categories.SelectedItem as string;
        if (category is null or AllCategories) category = null;
        var results = CliGuideLibrary.Search(_vendor, _search.Text ?? "", category);
        _commands.ItemsSource = results;
        _count.Text = $"{results.Count} / {_vendor.Commands.Count}";
    }

    private void Send(CliCommand c, bool run)
    {
        // A placeholder like <if> or <name> is a template - insert (never auto-run) so the user fills it in.
        bool template = c.Command.Contains('<');
        bool ok = run && !template ? _ctx.SendToActiveSession(c.Command) : _ctx.InsertIntoActiveSession(c.Command);
        if (!ok) _count.Text = "No active session - open a terminal tab first";
    }

    public void Shutdown() { }
}

public sealed class ScriptsTool : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly ScriptRunner _runner = new();
    private CancellationTokenSource? _cts;
    private readonly ListBox _scripts = new() { Background = Brushes.Transparent };
    private readonly ComboBox _session = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
    private readonly TextBox _stdin = Ui.MultiInput("", 90, "Anything the script should read on stdin (optional)");
    private readonly TextBox _output = Ui.Output();
    private readonly TextBlock _status = Ui.Text("", 12, color: Ui.Muted);
    private readonly Button _run, _stop;

    public ScriptsTool(ToolContext ctx)
    {
        _ctx = ctx;
        _scripts.ItemTemplate = new FuncDataTemplate<ScriptDefinition>((s, _) => s is null ? new Panel() : Ui.Stack(1,
            new TextBlock { Text = s.Name + Path.GetExtension(s.Path), FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = s.Description.Length > 0 ? s.Description : s.Interpreter, FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }));
        _runner.LineReceived += line => Dispatcher.UIThread.Post(() => { _output.Text += line + "\n"; _output.CaretIndex = _output.Text.Length; });
        _run = Ui.Button("Run", () => _ = RunAsync(), accent: true);
        _stop = Ui.Button("Stop", () => _cts?.Cancel());
        _stop.IsEnabled = false;

        var left = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Section("Scripts"), Dock.Top),
                WithDock(Ui.Row(Ui.Button("Refresh", Refresh), Ui.Button("Open folder", () => { Directory.CreateDirectory(ScriptCatalog.DefaultDirectory); ToolUi.OpenInFileManager(ScriptCatalog.DefaultDirectory); })), Dock.Bottom),
                new ScrollViewer { Content = _scripts, Margin = new Thickness(0, 8) },
            },
        });
        var right = new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(10, Ui.Columns("*,Auto,Auto", Ui.Field("Session context (optional)", _session), _run, _stop), Ui.Field("Stdin", _stdin)), Dock.Top),
                WithDock(new DockPanel { Margin = new Thickness(0, 12, 0, 6), Children = { WithDock(_status, Dock.Right), Ui.Section("Output") } }, Dock.Top),
                _output,
            },
        };
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("280,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        var kinds = OperatingSystem.IsWindows() ? ".ps1/.py/.sh/.cmd" : ".sh/.py/.ps1 (PowerShell 7)";
        Content = Ui.Page("Scripts", $"Run your own {kinds} scripts as separate processes. The chosen session's name, host, user and folder are passed as OML_SESSION_* environment variables.", body);
        AttachedToVisualTree += (_, _) => { ToolUi.FillSessions(_session, _ctx.Sessions()); Refresh(); };
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void Refresh()
    {
        var dir = ScriptCatalog.DefaultDirectory;
        Directory.CreateDirectory(dir);
        var list = ScriptCatalog.Scan(dir);
        _scripts.ItemsSource = list;
        _status.Text = list.Count == 0 ? $"No scripts yet - drop a script file into {dir}" : $"{list.Count} script(s)";
    }

    private async Task RunAsync()
    {
        if (_cts is not null) return;
        if (_scripts.SelectedItem is not ScriptDefinition script) { _status.Text = "Pick a script first."; return; }
        _output.Text = "";
        var device = _session.SelectedItem as SessionProfile;
        var context = new ScriptContext(device?.Name, device?.Host, device?.Username, device?.ProtocolLabel, device?.Folder,
            (_stdin.Text ?? "").Length > 0 ? _stdin.Text : null);
        _cts = new CancellationTokenSource();
        _run.IsEnabled = false;
        _stop.IsEnabled = true;
        _status.Text = "Running...";
        try { _status.Text = $"Exit code {await _runner.RunAsync(script, context, _cts.Token)}"; }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex) { _output.Text += $"error: {ex.Message}\n"; _status.Text = "Failed."; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _run.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    public void Shutdown() => _cts?.Cancel();
}

public sealed class MonitorRow : LiveRow
{
    private HostStatsSample? _sample;
    private string? _error;
    private bool _connecting = true;

    public required SessionProfile Device { get; init; }
    public HostStatsSample? Sample { get => _sample; set { _sample = value; _error = null; Touch(); } }
    /// <summary>Also clears Sample, so a dropped host never keeps showing its last healthy reading.</summary>
    public string? Error { get => _error; set { _error = value; _sample = null; Touch(); } }
    public bool Connecting { get => _connecting; set { _connecting = value; Touch(); } }

    public string LoadText => Sample is { Load1: not null, Load5: not null, Load15: not null } s ? $"{s.Load1:0.00} / {s.Load5:0.00} / {s.Load15:0.00}" : "—";
    public string MemText => Sample?.MemUsedPercent is { } p ? $"{p:0}%  ({Sample!.MemUsedMb:N0}/{Sample!.MemTotalMb:N0} MB)" : "—";
    public string DiskText => Sample?.DiskUsedPercent is { } p ? $"{p:0}%" : "—";
    public string UptimeText => Sample?.UptimeText ?? "";
    public string StatusText => Connecting ? "Connecting…" : Error is { } e ? e : Sample is { AnyData: true } ? "OK" : Sample is not null ? "No stats - not a Linux/Unix host?" : "—";
    public IBrush StatusBrush => Connecting ? Ui.Muted : Error is not null ? Ui.Rose : Sample is { AnyData: true } ? Ui.Mint : Ui.Amber;
}

public sealed class HostMonitorTool : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private readonly ObservableCollection<MonitorRow> _rows = new();
    private CancellationTokenSource? _cts;
    private readonly DevicePicker _devices = new();
    private readonly NumericUpDown _interval = Ui.Number(5, 2, 300);
    private readonly Button _start, _stop;

    public HostMonitorTool(ToolContext ctx)
    {
        _ctx = ctx;
        _start = Ui.Button("Start monitoring", Start, accent: true);
        _stop = Ui.Button("Stop", Stop);
        _stop.IsEnabled = false;
        var table = Ui.Table(
            Ui.Col<MonitorRow>("", r => "●", 30, r => r.StatusBrush),
            Ui.Col<MonitorRow>("Device", r => r.Device.Name, 160),
            Ui.Col<MonitorRow>("Host", r => r.Device.Host, 130, mono: true),
            Ui.Col<MonitorRow>("Load 1/5/15", r => r.LoadText, 150, mono: true),
            Ui.Col<MonitorRow>("Memory", r => r.MemText, 190, mono: true),
            Ui.Col<MonitorRow>("Disk /", r => r.DiskText, 70, mono: true),
            Ui.Col<MonitorRow>("Uptime", r => r.UptimeText, 110),
            Ui.Col<MonitorRow>("Status", r => r.StatusText, fill: true, color: r => r.StatusBrush));
        table.ItemsSource = _rows;
        var left = Ui.Card(new DockPanel { Children = { WithDock(Ui.Section("Devices"), Dock.Top), _devices } });
        _devices.Margin = new Thickness(0, 8, 0, 0);
        var right = new DockPanel { Children = { WithDock(Ui.Columns("130,Auto,Auto,*", Ui.Field("Poll every (s)", _interval), _start, _stop, new Panel()), Dock.Top), table } };
        table.Margin = new Thickness(0, 12, 0, 0);
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("300,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Content = Ui.Page("Host Monitor", "Live load, memory, disk and uptime for saved SSH sessions (Linux/Unix hosts) - polled on a side channel, never the terminal you're typing in.", body);
        AttachedToVisualTree += (_, _) => _devices.SetDevices(_ctx.SshSessions());
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void Start()
    {
        if (_cts is not null) return;
        var devices = _devices.Selected;
        if (devices.Count == 0) return;
        _rows.Clear();
        foreach (var d in devices) _rows.Add(new MonitorRow { Device = d });
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        var interval = TimeSpan.FromSeconds(Math.Max(2, Ui.IntValue(_interval, 5)));
        foreach (var row in _rows) _ = PollDeviceAsync(row, interval, _cts.Token);
    }

    /// <summary>One side-channel SSH connection per device, reconnecting on failure, polling on its own exec channel.</summary>
    private static async Task PollDeviceAsync(MonitorRow row, TimeSpan interval, CancellationToken ct)
    {
        ConnectedSsh? ssh = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ssh is null)
                    {
                        Update(row, r => r.Connecting = true);
                        ssh = await SshConnector.ConnectAsync(row.Device, ct).ConfigureAwait(false);
                    }
                    using var cmd = ssh.Client.CreateCommand(HostStats.Command);
                    cmd.CommandTimeout = TimeSpan.FromSeconds(10);
                    await cmd.ExecuteAsync(ct).ConfigureAwait(false);
                    var sample = HostStats.Parse(cmd.Result);
                    Update(row, r => { r.Connecting = false; r.Sample = sample; });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ssh?.Dispose();
                    ssh = null;
                    Update(row, r => { r.Connecting = false; r.Error = ex.Message; });
                }
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { ssh?.Dispose(); }
    }

    private static void Update(MonitorRow row, Action<MonitorRow> change) => Dispatcher.UIThread.Post(() => change(row));

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _start.IsEnabled = true;
        _stop.IsEnabled = false;
    }

    public void Shutdown() => _cts?.Cancel();
}
