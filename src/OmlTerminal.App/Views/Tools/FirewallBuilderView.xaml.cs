using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Firewall;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class FirewallBuilderView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private GeneratedConfig? _result;
    private bool _loaded;

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

    public FirewallBuilderView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var v in Enum.GetValues<FirewallVendor>())
            VendorBox.Items.Add(new ComboBoxItem { Content = FirewallVendors.DisplayName(v), Tag = v });
        VendorBox.SelectedIndex = 0;
        Loaded += (_, _) => { _loaded = true; UpdateVendorOptions(); Regenerate(); };
    }

    private FirewallVendor Vendor => (VendorBox.SelectedItem as ComboBoxItem)?.Tag is FirewallVendor v ? v : FirewallVendor.FortiGate;
    private bool IsServices => (string?)KindBar.SelectedItem?.Tag == "Services";

    private void Any_SelectionChanged(object sender, object e) { if (_loaded) { UpdateVendorOptions(); Regenerate(); } }
    private void Any_Click(object sender, RoutedEventArgs e) => Regenerate();
    private void Input_TextChanged(object sender, TextChangedEventArgs e) { if (_loaded) Regenerate(); }
    private void OutputBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowOutput();

    private void UpdateVendorOptions()
    {
        FortiOptions.Visibility = Vendor == FirewallVendor.FortiGate ? Visibility.Visible : Visibility.Collapsed;
        PanOptions.Visibility = Vendor == FirewallVendor.PaloAlto ? Visibility.Visible : Visibility.Collapsed;
        ApplyHint.Text = FirewallVendors.HowToApply(Vendor);
        var scopeTag = (PanScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        PanScopeName.IsEnabled = scopeTag is "vsys" or "dg";
        NamingBox.IsEnabled = PrefixBox.IsEnabled = SuffixBox.IsEnabled = !IsServices;
        LegacyWildcardBox.IsEnabled = !IsServices;
        CategoryBox.IsEnabled = IsServices;
        HintText.Text = IsServices
            ? "443 · tcp/8443 · udp/500-501 · tcp-udp/53 · tcp/443:1024-65535 (dst:src) · icmp · Name,tcp/8443,comment"
            : "IP · CIDR · IP MASK · start-end · 10.1.1.5-20 · IPv6 · FQDN · *.wildcard · Name,value,comment";
    }

    private GeneratorOptions Options()
    {
        var scopeTag = (PanScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var scopeName = PanScopeName.Text.Trim();
        return new GeneratorOptions
        {
            GroupName = GroupBox.Text.Trim(),
            GroupComment = CommentBox.Text.Trim(),
            IncludeRollback = RollbackBox.IsChecked == true,
            Vdom = VdomBox.Text.Trim(),
            AssociatedInterface = InterfaceBox.Text.Trim(),
            LegacyWildcardFqdn = LegacyWildcardBox.IsChecked == true,
            ServiceCategory = CategoryBox.Text.Trim(),
            PanScope = scopeTag switch
            {
                "shared" => "shared",
                "vsys" when scopeName.Length > 0 => $"vsys:{scopeName}",
                "dg" when scopeName.Length > 0 => $"dg:{scopeName}",
                _ => "",
            },
            PanTag = PanTagBox.Text.Trim(),
        };
    }

    private void Regenerate()
    {
        if (!_loaded) return;
        List<ParseIssue> issues;
        int count;
        if (IsServices)
        {
            var (objs, iss) = ServiceListParser.Parse(InputBox.Text);
            issues = iss; count = objs.Count;
            _result = FirewallGenerators.Services(Vendor, objs, Options());
        }
        else
        {
            var naming = new NamingOptions
            {
                Style = NamingBox.SelectedIndex == 1 ? NamingStyle.Plain : NamingStyle.Typed,
                Prefix = PrefixBox.Text.Trim(),
                Suffix = SuffixBox.Text.Trim(),
            };
            var (objs, iss) = AddressListParser.Parse(InputBox.Text, naming);
            issues = iss; count = objs.Count;
            _result = FirewallGenerators.Addresses(Vendor, objs, Options());
        }

        CountText.Text = $"{count} object(s){(issues.Count > 0 ? $" · {issues.Count} skipped" : "")}";
        IssuesBar.IsOpen = issues.Count > 0;
        IssuesBar.Message = string.Join("\n", issues.Take(12).Select(i => i.ToString())) + (issues.Count > 12 ? $"\n… and {issues.Count - 12} more" : "");
        WarningsBar.IsOpen = _result.Warnings.Count > 0;
        WarningsBar.Message = string.Join("\n", _result.Warnings);
        RollbackTab.IsEnabled = RollbackBox.IsChecked == true;
        if (RollbackBox.IsChecked != true && OutputBar.SelectedItem == RollbackTab) OutputBar.SelectedItem = OutputBar.Items[0];
        ShowOutput();
    }

    private string CurrentText => OutputBar.SelectedItem == RollbackTab ? _result?.Rollback ?? "" : _result?.Script ?? "";

    private void ShowOutput()
    {
        if (OutputBox is null) return;
        OutputBox.Text = CurrentText;
        SendStatus.Text = "";
    }

    private void Example_Click(object sender, RoutedEventArgs e) => InputBox.Text = IsServices ? ServiceExample : AddressExample;

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) InputBox.Text = await File.ReadAllTextAsync(file.Path);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        ToolUi.Copy(CurrentText);
        SendStatus.Text = "Copied to clipboard.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var kind = IsServices ? "services" : "addresses";
        var which = OutputBar.SelectedItem == RollbackTab ? "-rollback" : "";
        var (ext, type) = FirewallVendors.FileType(Vendor);
        var path = await ToolUi.SaveTextAsync($"{Vendor.ToString().ToLowerInvariant()}-{kind}{which}-{DateTime.Now:yyyyMMdd-HHmm}", CurrentText, ext, type);
        if (path is not null) SendStatus.Text = $"Saved to {path}";
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = CurrentText;
        if (text.Trim().Length == 0) { SendStatus.Text = "Nothing to send yet."; return; }
        SendStatus.Text = _ctx.SendToActiveSession(text)
            ? $"Sent {text.Split('\n').Length} lines to the last active terminal session. Check its output for errors."
            : "No connected terminal session - open an SSH/Telnet/Serial tab to the firewall first.";
    }

    public void Shutdown() { }
}
