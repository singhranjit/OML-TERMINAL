using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Firewall;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Desktop.Tools;

public sealed class FirewallPolicyTool : UserControl, IToolView
{
    private static readonly FirewallVendor[] Vendors = Enum.GetValues<FirewallVendor>();
    private static readonly string[] PanScopes = ["", "shared", "vsys", "dg"];
    private static readonly string[] Positions = ["bottom", "top"];

    private readonly ToolContext _ctx;
    private GeneratedConfig? _result;
    private List<PolicyRule> _rules = new();
    private string? _sheetPath;
    private FileSystemWatcher? _watcher;
    private string _sheetText = "";
    private bool _ready, _loadingFile;
    private int _reloadVersion;

    private readonly ComboBox _vendor = Ui.Combo(Vendors.Select(FirewallVendors.DisplayName).ToList());
    private readonly TextBlock _file = Ui.Text("No sheet loaded - create a sample or open a CSV.", 12, mono: true, color: Ui.Muted);
    private readonly TextBlock _applyHint = Ui.Text("", 12, color: Ui.Muted);
    private readonly ToggleButton _rulesTab = new() { Content = "Rules", IsChecked = true }, _csvTab = new() { Content = "Sheet (CSV)" };
    private readonly ItemsControl _ruleList = new();
    private readonly TextBox _sheet = Ui.MultiInput("", double.NaN);
    private readonly Control _preview;
    private readonly TextBlock _empty = Ui.Text("No rules yet. Each sheet row is one rule: name, action, source/destination interface and addresses, services, comment.", 13, color: Ui.Muted);
    private readonly Border _issuesBar, _warningsBar;
    private readonly TextBlock _issuesText, _warningsText;
    private readonly TextBox _prefix = Ui.Input("", "e.g. CHG1234_"), _vdom = Ui.Input("", "e.g. root"), _panScopeName = Ui.Input("", "vsys2 or Branches"),
        _layer = Ui.Input("Network"), _aclSuffix = Ui.Input("_access_in");
    private readonly NumericUpDown _startId = Ui.Number(0, 0, int.MaxValue);
    private readonly CheckBox _rollback = Ui.Check("Also generate rollback script"), _accessGroup = Ui.Check("Bind ACLs with access-group", true);
    private readonly ComboBox _panScope = Ui.Combo(new[] { "Firewall (single vsys)", "Shared", "Specific vsys...", "Panorama device-group..." }),
        _position = Ui.Combo(new[] { "bottom", "top" });
    private readonly Control _forti, _pan, _cp, _asa;
    private readonly ToggleButton _scriptTab = new() { Content = "Script", IsChecked = true }, _rollbackTab = new() { Content = "Rollback" };
    private readonly TextBox _output = Ui.Output();
    private readonly TextBlock _status = Ui.Text("", 12, color: Ui.Muted);
    private readonly Button _send;

    public FirewallPolicyTool(ToolContext ctx)
    {
        _ctx = ctx;
        _issuesBar = Ui.Banner(out _issuesText, Ui.Amber);
        _warningsBar = Ui.Banner(out _warningsText, Ui.Sky);
        _forti = Ui.Stack(10, Ui.Field("VDOM (blank = no VDOMs)", _vdom), Ui.Field("First policy ID (0 = next free)", _startId));
        _pan = Ui.Stack(10, Ui.Field("Location", _panScope), Ui.Field("vsys / device-group name", _panScopeName));
        _cp = Ui.Stack(10, Ui.Field("Access layer", _layer), Ui.Field("Rule position", _position));
        _asa = Ui.Stack(10, Ui.Field("ACL name = interface +", _aclSuffix), _accessGroup);
        _send = Ui.Button("Send to session", Send, accent: true, tip: "Type the script into the last active terminal tab");

        _ruleList.ItemTemplate = new FuncDataTemplate<(PolicyRule Rule, int Number)>((x, _) =>
        {
            var r = x.Rule;
            if (r is null) return new Panel();
            static string Join(IReadOnlyList<string> v) => v.Count == 0 ? "any" : string.Join(", ", v);
            var allow = r.Action == PolicyAction.Allow;
            var action = new Border
            {
                Background = new SolidColorBrush(allow ? Color.FromArgb(0x26, 0x34, 0xD3, 0x99) : Color.FromArgb(0x26, 0xFB, 0x71, 0x85)), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = r.Action.ToString().ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = allow ? Ui.Mint : Ui.Rose },
            };
            return new Border
            {
                Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8), Background = Ui.CardBack, Opacity = r.Enabled ? 1 : 0.5,
                Child = Ui.Stack(2,
                    Ui.Row(new TextBlock { Text = x.Number.ToString(), Foreground = Ui.Muted, FontFamily = Ui.Mono }, action,
                        new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"{(r.SourceInterface.Length > 0 ? r.SourceInterface : "any")} → {(r.DestinationInterface.Length > 0 ? r.DestinationInterface : "any")}", Foreground = Ui.Sky, FontSize = 12 }),
                    new TextBlock { Text = $"src  {Join(r.Sources)}\ndst  {Join(r.Destinations)}\nsvc  {Join(r.Services)}{(r.Applications.Count > 0 ? $"  ·  app {Join(r.Applications)}" : "")}", FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = r.Enabled ? r.Comment : $"(disabled) {r.Comment}".Trim(), FontSize = 11.5, Foreground = Ui.Muted, IsVisible = r.Comment.Length > 0 || !r.Enabled }),
            };
        });
        _preview = new Panel { Children = { new ScrollViewer { Content = _ruleList }, _empty } };
        _sheet.IsVisible = false;
        foreach (var t in new[] { _rulesTab, _csvTab })
            t.Click += (s, _) => { _rulesTab.IsChecked = s == _rulesTab; _csvTab.IsChecked = s == _csvTab; _sheet.IsVisible = s == _csvTab; _preview.IsVisible = s == _rulesTab; };
        foreach (var t in new[] { _scriptTab, _rollbackTab })
            t.Click += (s, _) => { _scriptTab.IsChecked = s == _scriptTab; _rollbackTab.IsChecked = s == _rollbackTab; _output.Text = CurrentText; };
        foreach (var c in new SelectingItemsControl[] { _vendor, _panScope, _position }) c.SelectionChanged += (_, _) => { UpdateVendorOptions(); Regenerate(); };
        foreach (var b in new[] { _prefix, _vdom, _panScopeName, _layer, _aclSuffix }) b.TextChanged += (_, _) => Regenerate();
        foreach (var c in new[] { _rollback, _accessGroup }) c.IsCheckedChanged += (_, _) => Regenerate();
        _startId.ValueChanged += (_, _) => Regenerate();
        _sheet.TextChanged += (_, _) => { if (_loadingFile) return; _sheetText = _sheet.Text ?? ""; Regenerate(); };

        var sheetCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(8,
                    Ui.Row(Ui.Button("New sample sheet...", () => _ = SampleAsync(), accent: true), Ui.Button("Open CSV...", () => _ = OpenAsync()), Ui.Button("Built-in example", LoadExample)),
                    _file, Ui.Row(_rulesTab, _csvTab)), Dock.Top),
                WithDock(_issuesBar, Dock.Bottom),
                new Panel { Children = { _preview, _sheet }, Margin = new Thickness(0, 8) },
            },
        });
        var optionsCard = Ui.Card(new ScrollViewer { Content = Ui.Stack(10, Ui.Section("Options"), Ui.Field("Prefix for new objects", _prefix), _rollback, _forti, _pan, _cp, _asa) });
        var outputCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(WithMargin(Ui.Stack(6, Ui.Row(_scriptTab, _rollbackTab), Ui.Row(Ui.Button("Copy", () => { ToolUi.Copy(CurrentText); _status.Text = "Copied to clipboard."; }), Ui.Button("Save...", () => _ = SaveAsync()), _send))), Dock.Top),
                WithDock(_status, Dock.Bottom),
                WithDock(_warningsBar, Dock.Top),
                _output,
            },
        });
        _warningsBar.Margin = new Thickness(0, 0, 0, 8);
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,240,*"), ColumnSpacing = 14 };
        body.Children.Add(sheetCard);
        Grid.SetColumn(optionsCard, 1);
        body.Children.Add(optionsCard);
        Grid.SetColumn(outputCard, 2);
        body.Children.Add(outputCard);
        _vendor.Width = 260;
        Content = Ui.Page("Firewall Policy Builder", "Write rules in a spreadsheet, get ready-to-apply policies for 8 firewall vendors. Edit the CSV in LibreOffice or Excel - this page reloads every time you save.", body,
            Ui.Row(_vendor, _applyHint));
        _ready = true;
        UpdateVendorOptions();
        Regenerate();
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
    private static Control WithMargin(Control c) { c.Margin = new Thickness(0, 0, 0, 8); return c; }

    private FirewallVendor Vendor => _vendor.SelectedIndex >= 0 ? Vendors[_vendor.SelectedIndex] : FirewallVendor.FortiGate;

    private static bool IsCliVendor(FirewallVendor v) =>
        v is FirewallVendor.FortiGate or FirewallVendor.PaloAlto or FirewallVendor.CiscoAsa or FirewallVendor.JuniperSrx;

    private void SetSheet(string text)
    {
        _sheetText = text;
        _loadingFile = true;
        _sheet.Text = text;
        _loadingFile = false;
        Regenerate();
    }

    private async Task SampleAsync()
    {
        // Written with a BOM so spreadsheet apps read UTF-8 correctly; "#" comment rows are skipped by the parser.
        var sample = "﻿" + PolicySheet.Sample.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var path = await ToolUi.SaveTextAsync("firewall-policy-sheet", sample, ".csv", "CSV");
        if (path is null) return;
        LoadFile(path);
        _status.Text = "Sample saved and opened. Edit the rows and save (keep CSV format) - this page reloads automatically.";
        try
        {
            var psi = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [path]) : new ProcessStartInfo("xdg-open", [path]);
            psi.UseShellExecute = false;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { _status.Text = $"Saved to {path}, but couldn't open it: {ex.Message}"; }
    }

    private async Task OpenAsync()
    {
        var path = await ToolUi.PickOpenPathAsync("Open a policy sheet", ("CSV / text", ["*.csv", "*.tsv", "*.txt"]), ("Excel workbook", ["*.xlsx"]));
        if (path is null) return;
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "That's a workbook - save it as CSV (UTF-8) from your spreadsheet app and open the .csv here.";
            return;
        }
        LoadFile(path);
    }

    private void LoadExample()
    {
        StopWatching();
        _sheetPath = null;
        _file.Text = "Built-in example (edit it in the Sheet tab)";
        SetSheet(PolicySheet.Sample);
    }

    private void LoadFile(string path)
    {
        StopWatching();
        _sheetPath = path;
        _file.Text = path;
        ReadSheet();
        // Spreadsheet apps save by writing a temp file and renaming, so watch the folder for anything touching this name.
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnSheetChanged;
        _watcher.Created += OnSheetChanged;
        _watcher.Renamed += OnSheetChanged;
    }

    private void OnSheetChanged(object sender, FileSystemEventArgs e)
    {
        int version = Interlocked.Increment(ref _reloadVersion);
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(400); // let the editor finish writing
            if (version != _reloadVersion) return;
            ReadSheet();
            _status.Text = $"Reloaded {Path.GetFileName(_sheetPath)} at {DateTime.Now:HH:mm:ss}.";
        });
    }

    private void ReadSheet()
    {
        if (_sheetPath is null) return;
        try
        {
            using var fs = new FileStream(_sheetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
            SetSheet(reader.ReadToEnd());
        }
        catch (Exception ex) { _status.Text = $"Couldn't read the sheet: {ex.Message}"; }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void UpdateVendorOptions()
    {
        if (!_ready) return;
        var v = Vendor;
        _forti.IsVisible = v == FirewallVendor.FortiGate;
        _pan.IsVisible = v == FirewallVendor.PaloAlto;
        _cp.IsVisible = v == FirewallVendor.CheckPoint;
        _asa.IsVisible = v == FirewallVendor.CiscoAsa;
        _panScopeName.IsEnabled = PanScopes[Math.Max(0, _panScope.SelectedIndex)] is "vsys" or "dg";
        _applyHint.Text = FirewallVendors.HowToApply(v);
        _send.IsEnabled = IsCliVendor(v);
    }

    private PolicyOptions Options()
    {
        var scope = PanScopes[Math.Max(0, _panScope.SelectedIndex)];
        var scopeName = (_panScopeName.Text ?? "").Trim();
        return new PolicyOptions
        {
            ObjectPrefix = (_prefix.Text ?? "").Trim(),
            IncludeRollback = _rollback.IsChecked == true,
            Vdom = (_vdom.Text ?? "").Trim(),
            FortiStartId = (int)Math.Min((double)(_startId.Value ?? 0), int.MaxValue),
            PanScope = scope switch
            {
                "shared" => "shared",
                "vsys" when scopeName.Length > 0 => $"vsys:{scopeName}",
                "dg" when scopeName.Length > 0 => $"dg:{scopeName}",
                _ => "",
            },
            CheckPointLayer = (_layer.Text ?? "").Trim(),
            CheckPointPosition = Positions[Math.Max(0, _position.SelectedIndex)],
            AsaAclSuffix = (_aclSuffix.Text ?? "").Trim(),
            AsaAccessGroup = _accessGroup.IsChecked == true,
        };
    }

    private void Regenerate()
    {
        if (!_ready) return;
        var (rules, issues) = PolicySheet.Parse(_sheetText);
        _rules = rules;
        _ruleList.ItemsSource = rules.Select((r, i) => (r, i + 1)).ToList();
        _empty.IsVisible = rules.Count == 0;
        _rulesTab.Content = rules.Count == 0 ? "Rules" : $"Rules ({rules.Count})";
        Ui.Show(_issuesBar, _issuesText, issues.Count == 0 ? null
            : "Skipped rows:\n" + string.Join("\n", issues.Take(10).Select(i => i.ToString())) + (issues.Count > 10 ? $"\n… and {issues.Count - 10} more" : ""));
        _result = rules.Count == 0 ? null : FirewallGenerators.Policies(Vendor, rules, Options());
        Ui.Show(_warningsBar, _warningsText, _result is { Warnings.Count: > 0 } ? string.Join("\n", _result.Warnings) : null);
        _rollbackTab.IsEnabled = _rollback.IsChecked == true;
        if (_rollback.IsChecked != true && _rollbackTab.IsChecked == true) { _rollbackTab.IsChecked = false; _scriptTab.IsChecked = true; }
        _output.Text = CurrentText;
    }

    private string CurrentText => _rollbackTab.IsChecked == true ? _result?.Rollback ?? "" : _result?.Script ?? "";

    private async Task SaveAsync()
    {
        if (CurrentText.Length == 0) return;
        var (ext, type) = FirewallVendors.FileType(Vendor);
        var which = _rollbackTab.IsChecked == true ? "-rollback" : "";
        var path = await ToolUi.SaveTextAsync($"{Vendor.ToString().ToLowerInvariant()}-policies{which}-{DateTime.Now:yyyyMMdd-HHmm}", CurrentText, ext, type);
        if (path is not null) _status.Text = $"Saved to {path}";
    }

    private void Send()
    {
        var text = CurrentText;
        if (text.Trim().Length == 0) { _status.Text = "Nothing to send yet."; return; }
        // Section headers are comments for the reader, not commands for the device.
        var lines = Core.TextLines.Split(text).Where(l => !l.StartsWith("# ----") && !l.StartsWith("! ----") && !l.StartsWith("# commit"));
        _status.Text = _ctx.SendToActiveSession(string.Join("\n", lines))
            ? $"Sent {_rules.Count} rule(s) to the last active terminal session. Check its output, then commit/save on the device."
            : "No connected terminal session - open an SSH tab to the firewall first.";
    }

    public void Shutdown() => StopWatching();
}

public sealed class MigrationTool : UserControl, IToolView
{
    private enum Source { CiscoAsa, CiscoFtd, FortiGate, PaloAlto, JuniperSrx, PfSense }

    private sealed class InterfaceMap(string original)
    {
        public string Original { get; } = original;
        public string Mapped { get; set; } = "";
    }

    /// <summary>Preset = null: no SSH pull for this source (paste or load a file instead).</summary>
    private static readonly (Source Source, string Display, string? Preset)[] Sources =
    [
        (Source.CiscoAsa, "Cisco ASA", "Cisco ASA"),
        (Source.CiscoFtd, "Cisco FTD (diagnostic CLI / Lina)", null),
        (Source.FortiGate, "FortiGate", "FortiGate (full-configuration)"),
        (Source.PaloAlto, "Palo Alto (set format)", "Palo Alto PAN-OS (set format)"),
        (Source.JuniperSrx, "Juniper SRX", "Juniper Junos"),
        (Source.PfSense, "pfSense (config.xml)", null),
    ];
    private static readonly FirewallVendor[] Targets = Enum.GetValues<FirewallVendor>();

    private readonly ToolContext _ctx;
    private readonly ObservableCollection<InterfaceMap> _interfaces = new();
    private CancellationTokenSource? _pullCts;
    private List<AddressObject> _addresses = [];
    private List<ServiceObject> _services = [];
    private List<PolicyRule> _rules = [];
    private IReadOnlyList<string> _review = [];
    private bool _parsed;

    private readonly ComboBox _source = Ui.Combo(Sources.Select(s => s.Display).ToList());
    private readonly ComboBox _target = Ui.Combo(Targets.Select(FirewallVendors.DisplayName).ToList());
    private readonly ComboBox _device = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
    private readonly TextBox _input = Ui.MultiInput("", double.NaN, "Paste the source firewall's configuration here, load a file, or pull it over SSH.");
    private readonly TextBlock _summary = Ui.Text("", 12.5, color: Ui.Muted);
    private readonly Border _issuesBar, _reviewBar;
    private readonly TextBlock _issuesText, _reviewText;
    private readonly ItemsControl _interfaceList = new();
    private readonly Button _generate;
    private readonly TextBox _output = Ui.Output();

    public MigrationTool(ToolContext ctx)
    {
        _ctx = ctx;
        _issuesBar = Ui.Banner(out _issuesText, Ui.Amber);
        _reviewBar = Ui.Banner(out _reviewText, Ui.Sky);
        _generate = Ui.Button("Generate", Generate, accent: true);
        _generate.IsEnabled = false;
        _generate.HorizontalAlignment = HorizontalAlignment.Stretch;
        _interfaceList.ItemsSource = _interfaces;
        _interfaceList.ItemTemplate = new FuncDataTemplate<InterfaceMap>((m, _) =>
        {
            if (m is null) return new Panel();
            var box = new TextBox { Text = m.Mapped, PlaceholderText = m.Original, FontFamily = Ui.Mono, FontSize = 12 };
            box.TextChanged += (_, _) => m.Mapped = box.Text ?? "";
            return Ui.Columns("*,*", new TextBlock { Text = m.Original, FontFamily = Ui.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, box) is var g ? WithMargin(g) : null!;
        });

        var sourceCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(8, Ui.Field("Source vendor", _source),
                    Ui.Field("Or pull from a saved SSH session", _device),
                    Ui.Row(Ui.Button("Pull", () => _ = PullAsync()), Ui.Button("Load file...", () => _ = LoadAsync()))), Dock.Top),
                WithDock(Ui.Stack(8, Ui.Row(Ui.Button("Parse", Parse, accent: true)), _summary, _issuesBar), Dock.Bottom),
                _input,
            },
        });
        _input.Margin = new Thickness(0, 8);
        var mapCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(8, Ui.Field("Migrate to", _target), Ui.Section("Interface mapping (blank = keep the name)")), Dock.Top),
                WithDock(_generate, Dock.Bottom),
                new ScrollViewer { Content = _interfaceList, Margin = new Thickness(0, 8) },
            },
        });
        var outputCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = {
                    WithDock(Ui.Row(Ui.Button("Copy", () => ToolUi.Copy(_output.Text ?? "")), Ui.Button("Save...", () => _ = SaveAsync())), Dock.Right), Ui.Section("Generated config") } }, Dock.Top),
                WithDock(_reviewBar, Dock.Top),
                _output,
            },
        });
        _reviewBar.Margin = new Thickness(0, 0, 0, 8);
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,280,*"), ColumnSpacing = 14 };
        body.Children.Add(sourceCard);
        Grid.SetColumn(mapCard, 1);
        body.Children.Add(mapCard);
        Grid.SetColumn(outputCard, 2);
        body.Children.Add(outputCard);
        Content = Ui.Page("Config Migration", "Move address and service objects and policy rules between Cisco ASA/FTD, FortiGate, Palo Alto, Juniper SRX and pfSense. Anything that can't be translated safely is listed for review.", body);
        AttachedToVisualTree += (_, _) => ToolUi.FillSessions(_device, _ctx.SshSessions());
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
    private static Control WithMargin(Control c) { c.Margin = new Thickness(0, 0, 0, 6); return c; }

    private async Task PullAsync()
    {
        var source = Sources[Math.Max(0, _source.SelectedIndex)];
        if (source.Preset is null) { _summary.Text = $"Pulling isn't supported for {source.Display} - paste or load a file instead."; return; }
        if (_device.SelectedItem is not SessionProfile device) { _summary.Text = "Pick a saved SSH session first."; return; }
        var preset = ConfigBackup.Presets.FirstOrDefault(p => p.Name == source.Preset);
        if (preset is null) return;
        _pullCts = new CancellationTokenSource();
        _summary.Text = "Pulling config...";
        try
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "migration-pulls");
            var ct = _pullCts.Token;
            var result = await Task.Run(() => ConfigBackup.RunAsync(device, preset, dir, ct: ct));
            if (!result.Success || result.FilePath is null) { _summary.Text = $"Pull failed: {result.Message}"; return; }
            _input.Text = await File.ReadAllTextAsync(result.FilePath);
            _summary.Text = $"Pulled {result.Lines:N0} lines from {device.Name} - click Parse.";
        }
        catch (OperationCanceledException) { _summary.Text = "Cancelled."; }
        finally { _pullCts = null; }
    }

    private async Task LoadAsync()
    {
        if (await ToolUi.PickOpenPathAsync("Load a configuration", ("Config files", ["*.txt", "*.cfg", "*.xml", "*.set", "*.conf"]), ("All files", ["*"])) is { } path)
            _input.Text = await File.ReadAllTextAsync(path);
    }

    private void Parse()
    {
        var text = _input.Text ?? "";
        (List<AddressObject> A, List<ServiceObject> S, List<PolicyRule> R, IReadOnlyList<string> I, IReadOnlyList<string> Review, IReadOnlyList<ParseIssue> Issues) result;
        switch (Sources[Math.Max(0, _source.SelectedIndex)].Source)
        {
            case Source.CiscoAsa: { var p = AsaConfigParser.Parse(text); result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues); break; }
            case Source.CiscoFtd:
            {
                var p = AsaConfigParser.Parse(text);
                var review = p.ReviewItems.Append(
                    "Parsed using Cisco ASA syntax rules - FTD's diagnostic CLI (the underlying Lina engine) is ASA-derived, but this captures only the dataplane config, not FMC's Access Control Policies. If this FTD is managed by FMC, export its policies from FMC directly instead of pasting diagnostic-CLI output.")
                    .ToList();
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, review, p.Issues);
                break;
            }
            case Source.FortiGate: { var p = FortiGateConfigParser.Parse(text); result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues); break; }
            case Source.PaloAlto: { var p = PaloAltoConfigParser.Parse(text); result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues); break; }
            case Source.JuniperSrx: { var p = JuniperSrxConfigParser.Parse(text); result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues); break; }
            default: { var p = PfSenseConfigParser.Parse(text); result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues); break; }
        }
        (_addresses, _services, _rules, _review) = (result.A, result.S, result.R, result.Review);
        _parsed = true;
        _summary.Text = $"{_addresses.Count} address object(s) · {_services.Count} service object(s) · {_rules.Count} rule(s) parsed"
            + (result.Issues.Count > 0 ? $" · {result.Issues.Count} line(s) skipped" : "");
        Ui.Show(_issuesBar, _issuesText, result.Issues.Count == 0 ? null : "Lines skipped (couldn't be parsed with confidence):\n" + string.Join("\n", result.Issues.Select(i => i.ToString())));
        _interfaces.Clear();
        foreach (var iface in result.I) _interfaces.Add(new InterfaceMap(iface));
        _generate.IsEnabled = _addresses.Count + _services.Count + _rules.Count > 0;
        _output.Text = "";
        Ui.Show(_reviewBar, _reviewText, null);
    }

    private void Generate()
    {
        if (!_parsed) return;
        var target = Targets[Math.Max(0, _target.SelectedIndex)];
        var sourceName = Sources[Math.Max(0, _source.SelectedIndex)].Display;
        var map = _interfaces.ToDictionary(r => r.Original, r => r.Mapped.Trim().Length > 0 ? r.Mapped.Trim() : r.Original, StringComparer.OrdinalIgnoreCase);
        var mappedRules = _rules.Select(r => new PolicyRule
        {
            Line = r.Line, Name = r.Name, Action = r.Action,
            SourceInterface = map.GetValueOrDefault(r.SourceInterface, r.SourceInterface),
            DestinationInterface = map.GetValueOrDefault(r.DestinationInterface, r.DestinationInterface),
            Sources = r.Sources, Destinations = r.Destinations, Services = r.Services, Applications = r.Applications,
            Schedule = r.Schedule, Nat = r.Nat, Log = r.Log, Comment = r.Comment, Enabled = r.Enabled,
        }).ToList();
        var addr = FirewallGenerators.Addresses(target, _addresses, new GeneratorOptions());
        var svc = FirewallGenerators.Services(target, _services, new GeneratorOptions());
        var pol = FirewallGenerators.Policies(target, mappedRules, new PolicyOptions());
        var sb = new StringBuilder();
        sb.AppendLine($"# Migrated from {sourceName} to {FirewallVendors.DisplayName(target)}");
        sb.AppendLine($"# {FirewallVendors.HowToApply(target)}");
        sb.AppendLine();
        if (addr.Script.Trim().Length > 0) { sb.AppendLine("# ---- Address objects ----"); sb.AppendLine(addr.Script); }
        if (svc.Script.Trim().Length > 0) { sb.AppendLine("# ---- Service objects ----"); sb.AppendLine(svc.Script); }
        if (pol.Script.Trim().Length > 0) { sb.AppendLine("# ---- Policy rules ----"); sb.AppendLine(pol.Script); }
        _output.Text = sb.ToString();
        var review = addr.Warnings.Concat(svc.Warnings).Concat(pol.Warnings).Concat(_review).Distinct().ToList();
        Ui.Show(_reviewBar, _reviewText, review.Count == 0 ? null : "Needs manual review - not migrated:\n" + string.Join("\n", review));
    }

    private async Task SaveAsync()
    {
        var target = Targets[Math.Max(0, _target.SelectedIndex)];
        var (ext, type) = FirewallVendors.FileType(target);
        await ToolUi.SaveTextAsync($"migration-{target}-{DateTime.Now:yyyyMMdd-HHmm}", _output.Text ?? "", ext, type);
    }

    public void Shutdown() => _pullCts?.Cancel();
}
