using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Firewall;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.App.Views.Tools;

public sealed record VendorItem(FirewallVendor Vendor)
{
    public override string ToString() => FirewallVendors.DisplayName(Vendor);
}

public sealed class InterfaceMapRow(string original)
{
    public string Original { get; } = original;
    public string MappedName { get; set; } = "";
}

public enum MigrationSource { CiscoAsa, CiscoFtd, FortiGate, PaloAlto, JuniperSrx, PfSense }

public sealed partial class MigrationView : UserControl, IToolView
{
    /// <summary>Preset = null means "no SSH pull for this source" (paste or load a file instead) - either because
    /// the vendor has no flat-file config export (pfSense is pulled as config.xml over HTTPS, not SSH), or because
    /// pulling it needs steps Config Backup doesn't automate yet (FTD's diagnostic CLI requires dropping into an
    /// elevated shell first).</summary>
    private static readonly (MigrationSource Source, string Display, string? Preset)[] Sources =
    [
        (MigrationSource.CiscoAsa, "Cisco ASA", "Cisco ASA"),
        (MigrationSource.CiscoFtd, "Cisco FTD (diagnostic CLI / Lina)", null),
        (MigrationSource.FortiGate, "FortiGate", "FortiGate (full-configuration)"),
        (MigrationSource.PaloAlto, "Palo Alto (set format)", "Palo Alto PAN-OS (set format)"),
        (MigrationSource.JuniperSrx, "Juniper SRX", "Juniper Junos"),
        (MigrationSource.PfSense, "pfSense (config.xml)", null),
    ];

    private readonly ToolContext _ctx;
    private readonly ObservableCollection<InterfaceMapRow> _interfaceRows = new();
    private CancellationTokenSource? _pullCts;

    private List<AddressObject> _addresses = [];
    private List<ServiceObject> _services = [];
    private List<PolicyRule> _rules = [];
    private IReadOnlyList<string> _reviewItems = [];
    private IReadOnlyList<ParseIssue> _issues = [];
    private bool _parsedOnce;

    public MigrationView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var s in Sources) SourceVendorBox.Items.Add(s.Display);
        SourceVendorBox.SelectedIndex = 0;
        foreach (FirewallVendor v in Enum.GetValues<FirewallVendor>()) TargetVendorBox.Items.Add(new VendorItem(v));
        TargetVendorBox.SelectedIndex = 0;
        InterfaceList.ItemsSource = _interfaceRows;
        Loaded += (_, _) => DeviceBox.ItemsSource = _ctx.SshSessions();
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        var source = Sources[SourceVendorBox.SelectedIndex];
        if (source.Preset is null) { SummaryText.Text = $"Pulling isn't supported for {source.Display} - paste or load a file instead."; return; }
        if (DeviceBox.SelectedItem is not SessionProfile device) { SummaryText.Text = "Pick a saved SSH session first."; return; }
        var preset = ConfigBackup.Presets.FirstOrDefault(p => p.Name == source.Preset);
        if (preset is null) return;

        _pullCts = new CancellationTokenSource();
        SummaryText.Text = "Pulling config...";
        try
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "migration-pulls");
            var result = await Task.Run(() => ConfigBackup.RunAsync(device, preset, dir, ct: _pullCts.Token));
            if (!result.Success || result.FilePath is null) { SummaryText.Text = $"Pull failed: {result.Message}"; return; }
            InputBox.Text = await File.ReadAllTextAsync(result.FilePath);
            SummaryText.Text = $"Pulled {result.Lines:N0} lines from {device.Name} - click Parse.";
        }
        catch (OperationCanceledException) { SummaryText.Text = "Cancelled."; }
        finally { _pullCts = null; }
    }

    private async void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".cfg");
        picker.FileTypeFilter.Add(".xml");
        picker.FileTypeFilter.Add(".set");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        InputBox.Text = await File.ReadAllTextAsync(file.Path);
    }

    private void Parse_Click(object sender, RoutedEventArgs e)
    {
        var source = Sources[SourceVendorBox.SelectedIndex].Source;
        List<string> interfacesFound;
        (List<AddressObject> Addresses, List<ServiceObject> Services, List<PolicyRule> Rules, IReadOnlyList<string> Interfaces, IReadOnlyList<string> Review, IReadOnlyList<ParseIssue> Issues) result;

        switch (source)
        {
            case MigrationSource.CiscoAsa:
            {
                var p = AsaConfigParser.Parse(InputBox.Text);
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues);
                break;
            }
            case MigrationSource.CiscoFtd:
            {
                var p = AsaConfigParser.Parse(InputBox.Text);
                var review = p.ReviewItems.Append(
                    "Parsed using Cisco ASA syntax rules - FTD's diagnostic CLI (the underlying Lina engine) is ASA-derived, but this captures only the dataplane config, not FMC's Access Control Policies. If this FTD is managed by FMC, export its policies from FMC directly instead of pasting diagnostic-CLI output.")
                    .ToList();
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, review, p.Issues);
                break;
            }
            case MigrationSource.FortiGate:
            {
                var p = FortiGateConfigParser.Parse(InputBox.Text);
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues);
                break;
            }
            case MigrationSource.PaloAlto:
            {
                var p = PaloAltoConfigParser.Parse(InputBox.Text);
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues);
                break;
            }
            case MigrationSource.JuniperSrx:
            {
                var p = JuniperSrxConfigParser.Parse(InputBox.Text);
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues);
                break;
            }
            case MigrationSource.PfSense:
            {
                var p = PfSenseConfigParser.Parse(InputBox.Text);
                result = (p.Addresses, p.Services, p.Rules, p.InterfacesFound, p.ReviewItems, p.Issues);
                break;
            }
            default: return;
        }

        _addresses = result.Addresses;
        _services = result.Services;
        _rules = result.Rules;
        _reviewItems = result.Review;
        _issues = result.Issues;
        interfacesFound = result.Interfaces.ToList();
        _parsedOnce = true;

        SummaryText.Text = $"{_addresses.Count} address object(s) · {_services.Count} service object(s) · {_rules.Count} rule(s) parsed"
            + (_issues.Count > 0 ? $" · {_issues.Count} line(s) skipped" : "");

        IssuesBar.IsOpen = _issues.Count > 0;
        IssuesBar.Message = string.Join("\n", _issues.Select(i => i.ToString()));

        _interfaceRows.Clear();
        foreach (var iface in interfacesFound) _interfaceRows.Add(new InterfaceMapRow(iface));

        GenerateButton.IsEnabled = _addresses.Count + _services.Count + _rules.Count > 0;
        OutputBox.Text = "";
        ReviewBar.IsOpen = false;
    }

    private void TargetVendor_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!_parsedOnce || TargetVendorBox.SelectedItem is not VendorItem targetItem) return;
        var target = targetItem.Vendor;
        var sourceName = Sources[SourceVendorBox.SelectedIndex].Display;

        var map = _interfaceRows.ToDictionary(
            r => r.Original,
            r => r.MappedName.Trim().Length > 0 ? r.MappedName.Trim() : r.Original,
            StringComparer.OrdinalIgnoreCase);

        var mappedRules = _rules.Select(r => new PolicyRule
        {
            Line = r.Line,
            Name = r.Name,
            Action = r.Action,
            SourceInterface = map.GetValueOrDefault(r.SourceInterface, r.SourceInterface),
            DestinationInterface = map.GetValueOrDefault(r.DestinationInterface, r.DestinationInterface),
            Sources = r.Sources,
            Destinations = r.Destinations,
            Services = r.Services,
            Applications = r.Applications,
            Schedule = r.Schedule,
            Nat = r.Nat,
            Log = r.Log,
            Comment = r.Comment,
            Enabled = r.Enabled,
        }).ToList();

        var addrResult = FirewallGenerators.Addresses(target, _addresses, new GeneratorOptions());
        var svcResult = FirewallGenerators.Services(target, _services, new GeneratorOptions());
        var polResult = FirewallGenerators.Policies(target, mappedRules, new PolicyOptions());

        var sb = new StringBuilder();
        sb.AppendLine($"# Migrated from {sourceName} to {FirewallVendors.DisplayName(target)}");
        sb.AppendLine($"# {FirewallVendors.HowToApply(target)}");
        sb.AppendLine();
        if (addrResult.Script.Trim().Length > 0) { sb.AppendLine("# ---- Address objects ----"); sb.AppendLine(addrResult.Script); }
        if (svcResult.Script.Trim().Length > 0) { sb.AppendLine("# ---- Service objects ----"); sb.AppendLine(svcResult.Script); }
        if (polResult.Script.Trim().Length > 0) { sb.AppendLine("# ---- Policy rules ----"); sb.AppendLine(polResult.Script); }
        OutputBox.Text = sb.ToString();

        var review = addrResult.Warnings
            .Concat(svcResult.Warnings)
            .Concat(polResult.Warnings)
            .Concat(_reviewItems)
            .Distinct()
            .ToList();
        ReviewBar.IsOpen = review.Count > 0;
        ReviewBar.Message = string.Join("\n", review);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ToolUi.Copy(OutputBox.Text);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TargetVendorBox.SelectedItem is not VendorItem targetItem) return;
        var (ext, _) = FirewallVendors.FileType(targetItem.Vendor);
        await ToolUi.SaveTextAsync($"migration-{targetItem.Vendor}-{DateTime.Now:yyyyMMdd-HHmm}", OutputBox.Text, ext);
    }

    public void Shutdown() => _pullCts?.Cancel();
}
