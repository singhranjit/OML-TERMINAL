using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class AsaConfigParserTests
{
    private const string SampleConfig = """
        : Saved
        ASA Version 9.12(4)
        !
        object network SERVER1
         host 10.1.1.10
         description Web server
        object network NET-A
         subnet 10.2.0.0 255.255.255.0
        object network RANGE-A
         range 10.3.1.10 10.3.1.20
        object network FQDN-A
         fqdn www.example.com
        !
        object-group network SERVERS
         network-object object SERVER1
         network-object host 10.1.1.11
         network-object 10.4.0.0 255.255.255.0
        !
        object service HTTPS-ALT
         service tcp destination eq 8443
        !
        object-group service WEB-PORTS tcp
         port-object eq www
         port-object eq https
        !
        access-list OUTSIDE_IN extended permit tcp object-group SERVERS any eq https
        access-list OUTSIDE_IN extended permit tcp host 10.1.1.20 any object-group WEB-PORTS log
        access-list OUTSIDE_IN extended permit icmp any any
        access-list OUTSIDE_IN extended deny ip any any log
        access-list INSIDE_IN extended permit tcp 10.5.0.0 255.255.255.0 any eq 22 inactive
        access-list BAD_PORT extended permit tcp any gt 1023 any eq 443
        !
        access-group OUTSIDE_IN in interface outside
        access-group INSIDE_IN in interface inside
        !
        object network NAT-HOST
         host 10.1.1.10
         nat (inside,outside) static 203.0.113.10
        nat (inside,outside) source dynamic any interface
        !
        crypto map OUTSIDE_MAP 10 set peer 203.0.113.5
        tunnel-group 203.0.113.5 type ipsec-l2l
        route outside 0.0.0.0 0.0.0.0 203.0.113.1 1
        """;

    [Fact]
    public void ParsesHostSubnetRangeAndFqdnObjects()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        Assert.Equal(5, r.Addresses.Count); // SERVER1, NET-A, RANGE-A, FQDN-A, and NAT-HOST (used by the NAT test below)

        var server1 = r.Addresses.Single(a => a.Name == "SERVER1");
        Assert.Equal(AddressKind.Host, server1.Kind);
        Assert.Equal("10.1.1.10", server1.Value);
        Assert.Equal("Web server", server1.Comment);

        var netA = r.Addresses.Single(a => a.Name == "NET-A");
        Assert.Equal(AddressKind.Subnet, netA.Kind);

        var rangeA = r.Addresses.Single(a => a.Name == "RANGE-A");
        Assert.Equal(AddressKind.Range, rangeA.Kind);

        var fqdnA = r.Addresses.Single(a => a.Name == "FQDN-A");
        Assert.Equal(AddressKind.Fqdn, fqdnA.Kind);
        Assert.Equal("www.example.com", fqdnA.Value);
    }

    [Fact]
    public void ParsesObjectService()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        var svc = Assert.Single(r.Services);
        Assert.Equal("HTTPS-ALT", svc.Name);
        Assert.Equal(ServiceProtocol.Tcp, svc.Protocol);
        Assert.Equal("8443", svc.DestinationPorts);
    }

    [Fact]
    public void FlattensNetworkObjectGroupIntoRuleSourcesAtReferenceTime()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "OUTSIDE_IN" && x.Sources.Contains("SERVER1"));
        // SERVERS = SERVER1 (object reference) + host 10.1.1.11 + 10.4.0.0/24
        Assert.Equal(3, rule.Sources.Count);
        Assert.Contains("SERVER1", rule.Sources);
        Assert.Contains("10.1.1.11", rule.Sources);
        Assert.Contains("10.4.0.0 255.255.255.0", rule.Sources);
    }

    [Fact]
    public void ResolvesServiceGroupUsedAsAPortSpec()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Destinations.Count > 0 && x.Sources.Contains("10.1.1.20"));
        Assert.Contains("tcp/80", rule.Services);
        Assert.Contains("tcp/443", rule.Services);
        Assert.True(rule.Log);
    }

    [Fact]
    public void MapsIcmpAndImplicitDenyIp()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        var icmpRule = r.Rules.Single(x => x.Services.Contains("icmp"));
        Assert.Equal(PolicyAction.Allow, icmpRule.Action);

        var denyAll = r.Rules.Single(x => x.Action == PolicyAction.Deny);
        Assert.Contains("any", denyAll.Sources);
        Assert.Contains("any", denyAll.Destinations);
        Assert.True(denyAll.Log);
    }

    [Fact]
    public void AssignsSourceInterfaceFromAccessGroupBinding()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        Assert.All(r.Rules.Where(x => x.Name == "OUTSIDE_IN"), x => Assert.Equal("outside", x.SourceInterface));
        Assert.All(r.Rules.Where(x => x.Name == "INSIDE_IN"), x => Assert.Equal("inside", x.SourceInterface));
        Assert.Contains("outside", r.InterfacesFound);
        Assert.Contains("inside", r.InterfacesFound);
    }

    [Fact]
    public void InactiveAceBecomesDisabledRule()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "INSIDE_IN");
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void UnsupportedPortOperatorSkipsTheRuleRatherThanWideningIt()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        // "gt 1023" has no direct equivalent - must not silently become "any port" or get dropped without trace.
        Assert.DoesNotContain(r.Rules, x => x.Name == "BAD_PORT");
        Assert.Contains(r.Issues, i => i.Message.Contains("gt/lt/neq"));
    }

    [Fact]
    public void NatLinesAreSurfacedAsReviewItemsNotTranslated()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        Assert.Contains(r.ReviewItems, x => x.Contains("NAT"));
        // The object-level "nat (inside,outside) static ..." line inside NAT-HOST must not corrupt that
        // object's own address parsing.
        var natHost = r.Addresses.Single(a => a.Name == "NAT-HOST");
        Assert.Equal(AddressKind.Host, natHost.Kind);
        Assert.Equal("10.1.1.10", natHost.Value);
    }

    [Fact]
    public void VpnAndRoutingLinesAreSurfacedAsReviewItems()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        Assert.Contains(r.ReviewItems, x => x.Contains("VPN") || x.Contains("crypto"));
        Assert.Contains(r.ReviewItems, x => x.Contains("routing"));
    }

    [Fact]
    public void EmptyConfigProducesNoObjectsAndNoRules()
    {
        var r = AsaConfigParser.Parse("");
        Assert.Empty(r.Addresses);
        Assert.Empty(r.Services);
        Assert.Empty(r.Rules);
    }

    [Fact]
    public void ParsedAddressesFeedDirectlyIntoTheExistingFortiGateGenerator()
    {
        // The whole point of the shared model: no new output code needed for a migration target.
        var r = AsaConfigParser.Parse(SampleConfig);
        var result = FirewallGenerators.Addresses(FirewallVendor.FortiGate, r.Addresses, new GeneratorOptions());
        Assert.Contains("config firewall address", result.Script);
        Assert.Contains("SERVER1", result.Script);
    }

    [Fact]
    public void ParsedRulesFeedDirectlyIntoTheExistingPaloAltoPolicyGenerator()
    {
        var r = AsaConfigParser.Parse(SampleConfig);
        var result = FirewallGenerators.Policies(FirewallVendor.PaloAlto, r.Rules, new PolicyOptions());
        Assert.Contains("rulebase security rules", result.Script);
    }

    private const string ModernSyntax = """
        object network WEB-01
         host 172.16.10.20
        object network APP
         subnet 10.10.60.0 255.255.255.0
        object-group network WEB-SERVERS
         network-object object WEB-01
         network-object host 172.16.10.21
        object service HTTPS-8443
         service tcp destination eq 8443
        object-group service WEB-PORTS tcp
         port-object eq www
         port-object eq https
        access-list OUTSIDE-IN extended permit tcp any object-group WEB-SERVERS eq https
        access-list OUTSIDE-IN extended permit object HTTPS-8443 any object APP
        access-list OUTSIDE-IN extended permit object-group WEB-PORTS any object-group WEB-SERVERS
        access-list OUTSIDE-IN extended permit object NOT-DEFINED any any
        access-group OUTSIDE-IN in interface outside
        """;

    [Fact]
    public void NetworkGroupAfterSourceIsTheDestinationNotASourcePortGroup()
    {
        var rule = AsaConfigParser.Parse(ModernSyntax).Rules[0];
        Assert.Equal(new[] { "any" }, rule.Sources);
        Assert.Equal(new[] { "WEB-01", "172.16.10.21" }, rule.Destinations);
        Assert.Equal(new[] { "tcp/443" }, rule.Services);
    }

    [Fact]
    public void ServiceObjectOrGroupInPlaceOfTheProtocolBecomesTheRuleServices()
    {
        var r = AsaConfigParser.Parse(ModernSyntax);
        Assert.Equal(3, r.Rules.Count);
        Assert.Equal(new[] { "tcp/8443" }, r.Rules[1].Services);
        Assert.Equal(new[] { "APP" }, r.Rules[1].Destinations);
        Assert.Equal(new[] { "tcp/80", "tcp/443" }, r.Rules[2].Services);
        Assert.Contains(r.Issues, i => i.Message.Contains("'NOT-DEFINED' isn't defined"));
    }
}
