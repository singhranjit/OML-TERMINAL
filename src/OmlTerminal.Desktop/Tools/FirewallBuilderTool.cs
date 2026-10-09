using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Desktop.Tools;

public sealed class FirewallBuilderTool : UserControl, IToolView
{
    private const string AddressExample = """
        # Hosts, subnets (CIDR or mask), ranges, IPv6, FQDNs and wildcards - one per line
        10.10.1.25
        10.20.0.0/16
        172.16.5.0 255.255.255.0
        192.168.50.10-192.168.50.40
        2001:db8:100::/48
        login.microsoftonline.com
        *.salesforce.com
        # Or name,value,comment
        WebSrv-01,10.10.1.80,DMZ web server
        """;

    private const string ServiceExample = """
        # proto/port, ranges, dst:src, or name,proto/port,comment
        443
        tcp/8443
        udp/500-501
        tcp-udp/53
        tcp/5000-5010
        SNMP,udp/161,Monitoring
        SYSLOG-TLS,tcp,6514
        icmp
        """;

    private static readonly FirewallVendor[] Vendors = Enum.GetValues<FirewallVendor>();
    private static readonly string[] PanScopes = ["", "shared", "vsys", "dg"];

    private readonly ToolContext _ctx;
    private GeneratedConfig? _result;
    private bool _ready;

    private readonly ComboBox _vendor = Ui.Combo(Vendors.Select(FirewallVendors.DisplayName).ToList());
    private readonly ToggleButton _addressesTab = new() { Content = "Addresses", IsChecked = true }, _servicesTab = new() { Content = "Services" };
    private readonly TextBlock _applyHint = Ui.Text("", 12, color: Ui.Muted), _count = Ui.Text("", 12, mono: true);
    private readonly TextBox _input = Ui.MultiInput("", double.NaN);
    private readonly TextBlock _hint = Ui.Text("", 11.5, color: Ui.Muted);
    private readonly Border _issuesBar, _warningsBar;
    private readonly TextBlock _issuesText, _warningsText;
    private readonly ComboBox _naming = Ui.Combo(new[] { "Typed (H- / N- / R- / FQDN-)", "Plain (the value itself)" });
    private readonly TextBox _prefix = Ui.Input("", "e.g. BLK_"), _suffix = Ui.Input("", "e.g. _CHG1234"), _group = Ui.Input("", "GRP-Blocklist"), _comment = Ui.Input();
    private readonly TextBox _vdom = Ui.Input("", "e.g. root"), _iface = Ui.Input("", "port1 (optional)"), _category = Ui.Input("", "General (optional)");
    private readonly CheckBox _legacyWildcard = Ui.Check("Legacy wildcard-FQDN syntax (FortiOS < 6.2)");
    private readonly ComboBox _panScope = Ui.Combo(new[] { "Firewall (single vsys)", "Shared", "Specific vsys...", "Panorama device-group..." });
    private readonly TextBox _panScopeName = Ui.Input("", "vsys2 or Branches"), _panTag = Ui.Input("", "Blocklist");
    private readonly CheckBox _rollback = Ui.Check("Also generate rollback script");
    private readonly Control _fortiOptions, _panOptions;
    private readonly ToggleButton _scriptTab = new() { Content = "Script", IsChecked = true }, _rollbackTab = new() { Content = "Rollback" };
    private readonly TextBox _output = Ui.Output();
    private readonly TextBlock _sendStatus = Ui.Text("", 12, color: Ui.Muted);

    public FirewallBuilderTool(ToolContext ctx)
    {
        _ctx = ctx;
        _issuesBar = Ui.Banner(out _issuesText, Ui.Amber);
        _warningsBar = Ui.Banner(out _warningsText, Ui.Sky);
        _fortiOptions = Ui.Stack(10, Ui.Field("VDOM (blank = no VDOMs)", _vdom), Ui.Field("Associated interface", _iface), Ui.Field("Service category", _category), _legacyWildcard);
        _panOptions = Ui.Stack(10, Ui.Field("Location", _panScope), Ui.Field("vsys / device-group name", _panScopeName), Ui.Field("Tag (optional)", _panTag));

        foreach (var t in new[] { _addressesTab, _servicesTab })
            t.Click += (s, _) => { _addressesTab.IsChecked = s == _addressesTab; _servicesTab.IsChecked = s == _servicesTab; Changed(); };
        foreach (var t in new[] { _scriptTab, _rollbackTab })
            t.Click += (s, _) => { _scriptTab.IsChecked = s == _scriptTab; _rollbackTab.IsChecked = s == _rollbackTab; ShowOutput(); };
        foreach (var c in new SelectingItemsControl[] { _vendor, _naming, _panScope }) c.SelectionChanged += (_, _) => Changed();
        foreach (var b in new[] { _input, _prefix, _suffix, _group, _comment, _vdom, _iface, _category, _panScopeName, _panTag }) b.TextChanged += (_, _) => Regenerate();
        foreach (var c in new[] { _legacyWildcard, _rollback }) c.IsCheckedChanged += (_, _) => Regenerate();

        var inputCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = {
                    WithDock(Ui.Row(Ui.Button("Example", () => _input.Text = IsServices ? ServiceExample : AddressExample), Ui.Button("Load file...", () => _ = LoadAsync())), Dock.Right),
                    Ui.Section("Input - one per line") } }, Dock.Top),
                WithDock(Ui.Stack(6, _hint, _issuesBar), Dock.Bottom),
                _input,
            },
        });
        _hint.Margin = new Thickness(0, 6, 0, 0);
        var optionsCard = Ui.Card(new ScrollViewer
        {
            Content = Ui.Stack(10, Ui.Section("Options"), Ui.Field("Object naming", _naming), Ui.Field("Name prefix", _prefix), Ui.Field("Name suffix", _suffix),
                Ui.Field("Group name (blank = no group)", _group), Ui.Field("Group comment / description", _comment), _fortiOptions, _panOptions, _rollback),
        });
        var outputCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(WithMargin(Ui.Stack(6, Ui.Row(_scriptTab, _rollbackTab),
                    Ui.Row(Ui.Button("Copy", () => { ToolUi.Copy(CurrentText); _sendStatus.Text = "Copied to clipboard."; }), Ui.Button("Save...", () => _ = SaveAsync()),
                        Ui.Button("Send to session", Send, accent: true, tip: "Type the script into the last active terminal tab")))), Dock.Top),
                WithDock(_sendStatus, Dock.Bottom),
                WithDock(_warningsBar, Dock.Top),
                _output,
            },
        });
        _warningsBar.Margin = new Thickness(0, 0, 0, 8);
        _sendStatus.Margin = new Thickness(0, 6, 0, 0);
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,260,*"), ColumnSpacing = 14 };
        body.Children.Add(inputCard);
        Grid.SetColumn(optionsCard, 1);
        body.Children.Add(optionsCard);
        Grid.SetColumn(outputCard, 2);
        body.Children.Add(outputCard);
        _vendor.Width = 260;
        Content = Ui.Page("Firewall Object Builder", "Turn a list of addresses or services into ready-to-paste objects and a group for 8 firewall vendors - with a rollback script.", body,
            new DockPanel { Children = { WithDock(_count, Dock.Right), Ui.Row(_vendor, _addressesTab, _servicesTab, _applyHint) } });
        _ready = true;
        Changed();
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
    private static Control WithMargin(Control c) { c.Margin = new Thickness(0, 0, 0, 8); return c; }

    private FirewallVendor Vendor => _vendor.SelectedIndex >= 0 ? Vendors[_vendor.SelectedIndex] : FirewallVendor.FortiGate;
    private bool IsServices => _servicesTab.IsChecked == true;

    private void Changed()
    {
        if (!_ready) return;
        _fortiOptions.IsVisible = Vendor == FirewallVendor.FortiGate;
        _panOptions.IsVisible = Vendor == FirewallVendor.PaloAlto;
        _applyHint.Text = FirewallVendors.HowToApply(Vendor);
        _panScopeName.IsEnabled = PanScopes[Math.Max(0, _panScope.SelectedIndex)] is "vsys" or "dg";
        _naming.IsEnabled = _prefix.IsEnabled = _suffix.IsEnabled = _legacyWildcard.IsEnabled = !IsServices;
        _category.IsEnabled = IsServices;
        _hint.Text = IsServices
            ? "443 · tcp/8443 · udp/500-501 · tcp-udp/53 · tcp/443:1024-65535 (dst:src) · icmp · Name,tcp/8443,comment"
            : "IP · CIDR · IP MASK · start-end · 10.1.1.5-20 · IPv6 · FQDN · *.wildcard · Name,value,comment";
        Regenerate();
    }

    private GeneratorOptions Options()
    {
        var scope = PanScopes[Math.Max(0, _panScope.SelectedIndex)];
        var scopeName = (_panScopeName.Text ?? "").Trim();
        return new GeneratorOptions
        {
            GroupName = (_group.Text ?? "").Trim(),
            GroupComment = (_comment.Text ?? "").Trim(),
            IncludeRollback = _rollback.IsChecked == true,
            Vdom = (_vdom.Text ?? "").Trim(),
            AssociatedInterface = (_iface.Text ?? "").Trim(),
            LegacyWildcardFqdn = _legacyWildcard.IsChecked == true,
            ServiceCategory = (_category.Text ?? "").Trim(),
            PanScope = scope switch
            {
                "shared" => "shared",
                "vsys" when scopeName.Length > 0 => $"vsys:{scopeName}",
                "dg" when scopeName.Length > 0 => $"dg:{scopeName}",
                _ => "",
            },
            PanTag = (_panTag.Text ?? "").Trim(),
        };
    }

    private void Regenerate()
    {
        if (!_ready) return;
        List<ParseIssue> issues;
        int count;
        if (IsServices)
        {
            var (objs, iss) = ServiceListParser.Parse(_input.Text ?? "");
            issues = iss; count = objs.Count;
            _result = FirewallGenerators.Services(Vendor, objs, Options());
        }
        else
        {
            var naming = new NamingOptions
            {
                Style = _naming.SelectedIndex == 1 ? NamingStyle.Plain : NamingStyle.Typed,
                Prefix = (_prefix.Text ?? "").Trim(),
                Suffix = (_suffix.Text ?? "").Trim(),
            };
            var (objs, iss) = AddressListParser.Parse(_input.Text ?? "", naming);
            issues = iss; count = objs.Count;
            _result = FirewallGenerators.Addresses(Vendor, objs, Options());
        }
        _count.Text = $"{count} object(s){(issues.Count > 0 ? $" · {issues.Count} skipped" : "")}";
        Ui.Show(_issuesBar, _issuesText, issues.Count == 0 ? null
            : "Skipped lines:\n" + string.Join("\n", issues.Take(12).Select(i => i.ToString())) + (issues.Count > 12 ? $"\n… and {issues.Count - 12} more" : ""));
        Ui.Show(_warningsBar, _warningsText, _result.Warnings.Count == 0 ? null : string.Join("\n", _result.Warnings));
        _rollbackTab.IsEnabled = _rollback.IsChecked == true;
        if (_rollback.IsChecked != true && _rollbackTab.IsChecked == true) { _rollbackTab.IsChecked = false; _scriptTab.IsChecked = true; }
        ShowOutput();
    }

    private string CurrentText => _rollbackTab.IsChecked == true ? _result?.Rollback ?? "" : _result?.Script ?? "";

    private void ShowOutput()
    {
        _output.Text = CurrentText;
        _sendStatus.Text = "";
    }

    private async Task LoadAsync()
    {
        if (await ToolUi.PickOpenPathAsync("Load a list", ("Text or CSV", ["*.txt", "*.csv"]), ("All files", ["*"])) is { } path)
            _input.Text = await File.ReadAllTextAsync(path);
    }

    private async Task SaveAsync()
    {
        var kind = IsServices ? "services" : "addresses";
        var which = _rollbackTab.IsChecked == true ? "-rollback" : "";
        var (ext, type) = FirewallVendors.FileType(Vendor);
        var path = await ToolUi.SaveTextAsync($"{Vendor.ToString().ToLowerInvariant()}-{kind}{which}-{DateTime.Now:yyyyMMdd-HHmm}", CurrentText, ext, type);
        if (path is not null) _sendStatus.Text = $"Saved to {path}";
    }

    private void Send()
    {
        var text = CurrentText;
        if (text.Trim().Length == 0) { _sendStatus.Text = "Nothing to send yet."; return; }
        _sendStatus.Text = _ctx.SendToActiveSession(text)
            ? $"Sent {text.Split('\n').Length} lines to the last active terminal session. Check its output for errors."
            : "No connected terminal session - open an SSH/Telnet/Serial tab to the firewall first.";
    }

    public void Shutdown() { }
}
