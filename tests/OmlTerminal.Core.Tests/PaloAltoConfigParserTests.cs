using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class PaloAltoConfigParserTests
{
    private const string SampleConfig = """
        set address SERVER1 ip-netmask 10.1.1.10/32
        set address SERVER1 description "Web server"
        set address NET-A ip-netmask 10.2.0.0/24
        set address RANGE-A ip-range 10.3.1.10-10.3.1.20
        set address FQDN-A fqdn www.example.com
        set address-group SERVERS static [ SERVER1 NET-A ]
        set service HTTPS-ALT protocol tcp port 8443
        set service-group WEB-SVCS members [ HTTP HTTPS ]
        set rulebase security rules Users-to-Internet from trust
        set rulebase security rules Users-to-Internet to untrust
        set rulebase security rules Users-to-Internet source SERVER1
        set rulebase security rules Users-to-Internet source NET-A
        set rulebase security rules Users-to-Internet destination any
        set rulebase security rules Users-to-Internet service [ HTTPS-ALT ]
        set rulebase security rules Users-to-Internet action allow
        set rulebase security rules Users-to-Internet log-end yes
        set rulebase security rules Block-Bad from any
        set rulebase security rules Block-Bad to untrust
        set rulebase security rules Block-Bad source any
        set rulebase security rules Block-Bad destination any
        set rulebase security rules Block-Bad service any
        set rulebase security rules Block-Bad action deny
        set rulebase security rules Block-Bad disabled yes
        set vsys vsys2 rulebase security rules Other-Vsys-Rule action allow
        set network virtual-router default routing-table ip static-route Default destination 0.0.0.0/0
        """;

    [Fact]
    public void ParsesAddressObjectsAcrossMultipleLinesPerName()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        Assert.Equal(4, r.Addresses.Count);
        var s1 = r.Addresses.Single(a => a.Name == "SERVER1");
        Assert.Equal(AddressKind.Host, s1.Kind);
        Assert.Equal("Web server", s1.Comment);
        Assert.Equal(AddressKind.Subnet, r.Addresses.Single(a => a.Name == "NET-A").Kind);
        var range = r.Addresses.Single(a => a.Name == "RANGE-A");
        Assert.Equal(AddressKind.Range, range.Kind);
        Assert.NotNull(range.Prefix);
        Assert.NotNull(range.RangeEnd);
        Assert.Equal(AddressKind.Fqdn, r.Addresses.Single(a => a.Name == "FQDN-A").Kind);
    }

    [Fact]
    public void ParsesServiceObject()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        var svc = Assert.Single(r.Services);
        Assert.Equal("HTTPS-ALT", svc.Name);
        Assert.Equal(ServiceProtocol.Tcp, svc.Protocol);
        Assert.Equal("8443", svc.DestinationPorts);
    }

    [Fact]
    public void ParsesRuleWithMultipleSourceLinesAndBracketedService()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Users-to-Internet");
        Assert.Equal(PolicyAction.Allow, rule.Action);
        Assert.Equal("trust", rule.SourceInterface);
        Assert.Equal("untrust", rule.DestinationInterface);
        Assert.Contains("SERVER1", rule.Sources);
        Assert.Contains("NET-A", rule.Sources);
        Assert.Contains("any", rule.Destinations);
        Assert.Contains("HTTPS-ALT", rule.Services);
        Assert.True(rule.Log);
    }

    [Fact]
    public void DisabledRuleBecomesDisabled()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Block-Bad");
        Assert.False(rule.Enabled);
        Assert.Equal(PolicyAction.Deny, rule.Action);
        Assert.Equal("", rule.SourceInterface); // "from any" carries no real zone name to migrate
    }

    [Fact]
    public void MultiVsysAndVirtualRouterLinesAreSurfacedAsReviewItemsNotParsed()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        Assert.Contains(r.ReviewItems, x => x.Contains("multi-vsys") || x.Contains("vsys"));
        Assert.Contains(r.ReviewItems, x => x.Contains("routing"));
        Assert.DoesNotContain(r.Rules, x => x.Name == "Other-Vsys-Rule");
    }

    [Fact]
    public void CollectsInterfacesFound()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        Assert.Contains("trust", r.InterfacesFound);
        Assert.Contains("untrust", r.InterfacesFound);
    }

    [Fact]
    public void EmptyConfigProducesNoObjectsAndNoRules()
    {
        var r = PaloAltoConfigParser.Parse("");
        Assert.Empty(r.Addresses);
        Assert.Empty(r.Services);
        Assert.Empty(r.Rules);
    }

    [Fact]
    public void ParsedObjectsFeedDirectlyIntoTheExistingFortiGateGenerator()
    {
        var r = PaloAltoConfigParser.Parse(SampleConfig);
        var addrResult = FirewallGenerators.Addresses(FirewallVendor.FortiGate, r.Addresses, new GeneratorOptions());
        Assert.Contains("config firewall address", addrResult.Script);
        var polResult = FirewallGenerators.Policies(FirewallVendor.FortiGate, r.Rules, new PolicyOptions());
        Assert.Contains("config firewall policy", polResult.Script);
    }
}
