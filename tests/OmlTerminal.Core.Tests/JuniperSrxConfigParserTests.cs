using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class JuniperSrxConfigParserTests
{
    private const string SampleConfig = """
        set security address-book global address SERVER1 10.1.1.10/32
        set security address-book global address NET-A 10.2.0.0/24
        set security address-book global address RANGE-A range-address 10.3.1.10 to 10.3.1.20
        set security address-book global address FQDN-A dns-name www.example.com
        set security address-book global address-set SERVERS address SERVER1
        set applications application HTTPS-ALT protocol tcp destination-port 8443
        set security policies from-zone trust to-zone untrust policy Users-to-Internet match source-address SERVER1
        set security policies from-zone trust to-zone untrust policy Users-to-Internet match source-address NET-A
        set security policies from-zone trust to-zone untrust policy Users-to-Internet match destination-address any
        set security policies from-zone trust to-zone untrust policy Users-to-Internet match application HTTPS-ALT
        set security policies from-zone trust to-zone untrust policy Users-to-Internet then permit
        set security policies from-zone trust to-zone untrust policy Users-to-Internet then log session-close
        set security policies from-zone any to-zone untrust policy Block-Bad match source-address any
        set security policies from-zone any to-zone untrust policy Block-Bad match destination-address any
        set security policies from-zone any to-zone untrust policy Block-Bad then deny
        set security nat source rule-set trust-to-untrust rule src-nat match source-address 10.0.0.0/8
        set security ike proposal IKE-PROP authentication-method pre-shared-keys
        set routing-options static route 0.0.0.0/0 next-hop 203.0.113.1
        """;

    [Fact]
    public void ParsesAddressBookEntriesOfEveryKind()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
        Assert.Equal(4, r.Addresses.Count);
        Assert.Equal(AddressKind.Host, r.Addresses.Single(a => a.Name == "SERVER1").Kind);
        Assert.Equal(AddressKind.Subnet, r.Addresses.Single(a => a.Name == "NET-A").Kind);
        var range = r.Addresses.Single(a => a.Name == "RANGE-A");
        Assert.Equal(AddressKind.Range, range.Kind);
        Assert.NotNull(range.RangeEnd);
        Assert.Equal(AddressKind.Fqdn, r.Addresses.Single(a => a.Name == "FQDN-A").Kind);
    }

    [Fact]
    public void AddressSetIsNotTreatedAsAnAddressObject()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
        Assert.DoesNotContain(r.Addresses, a => a.Name == "SERVERS");
        Assert.Contains(r.ReviewItems, x => x.Contains("address-set"));
    }

    [Fact]
    public void ParsesApplicationAsServiceObject()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
        var svc = Assert.Single(r.Services);
        Assert.Equal("HTTPS-ALT", svc.Name);
        Assert.Equal(ServiceProtocol.Tcp, svc.Protocol);
        Assert.Equal("8443", svc.DestinationPorts);
    }

    [Fact]
    public void ParsesPolicyAcrossMultipleMatchLinesForTheSameZonePair()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
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
    public void ParsesDenyPolicy()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Block-Bad");
        Assert.Equal(PolicyAction.Deny, rule.Action);
        Assert.Equal("untrust", rule.DestinationInterface);
    }

    [Fact]
    public void NatIkeAndRoutingLinesAreSurfacedAsReviewItems()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
        Assert.Contains(r.ReviewItems, x => x.Contains("NAT"));
        Assert.Contains(r.ReviewItems, x => x.Contains("VPN") || x.Contains("IKE"));
        Assert.Contains(r.ReviewItems, x => x.Contains("routing"));
    }

    [Fact]
    public void EmptyConfigProducesNoObjectsAndNoRules()
    {
        var r = JuniperSrxConfigParser.Parse("");
        Assert.Empty(r.Addresses);
        Assert.Empty(r.Services);
        Assert.Empty(r.Rules);
    }

    [Fact]
    public void ParsedObjectsFeedDirectlyIntoTheExistingPaloAltoGenerator()
    {
        var r = JuniperSrxConfigParser.Parse(SampleConfig);
        var addrResult = FirewallGenerators.Addresses(FirewallVendor.PaloAlto, r.Addresses, new GeneratorOptions());
        Assert.Contains("set address", addrResult.Script);
    }
}
