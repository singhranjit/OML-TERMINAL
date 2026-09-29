using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class PfSenseConfigParserTests
{
    private const string SampleConfig = """
        <?xml version="1.0"?>
        <pfsense>
          <aliases>
            <alias>
              <name>SERVER1</name>
              <type>host</type>
              <address>10.1.1.10</address>
              <descr>Web server</descr>
            </alias>
            <alias>
              <name>NET_A</name>
              <type>network</type>
              <address>10.2.0.0/24</address>
            </alias>
            <alias>
              <name>MULTI_HOSTS</name>
              <type>host</type>
              <address>10.4.1.1 10.4.1.2</address>
            </alias>
            <alias>
              <name>WEB_PORTS</name>
              <type>port</type>
              <address>80 443</address>
            </alias>
          </aliases>
          <filter>
            <rule>
              <type>pass</type>
              <interface>lan</interface>
              <protocol>tcp</protocol>
              <source><address>SERVER1</address></source>
              <destination><any></any><port>WEB_PORTS</port></destination>
              <descr>Web access</descr>
              <log></log>
            </rule>
            <rule>
              <type>block</type>
              <interface>wan</interface>
              <source><any></any></source>
              <destination><any></any></destination>
              <descr>Default deny</descr>
              <disabled></disabled>
            </rule>
            <rule>
              <type>pass</type>
              <interface>lan</interface>
              <protocol>tcp</protocol>
              <source><address>NET_A</address><not></not></source>
              <destination><any></any></destination>
              <descr>Negated rule</descr>
            </rule>
          </filter>
          <nat>
            <rule>
              <descr>Some NAT rule</descr>
            </rule>
          </nat>
          <gateways>
            <gateway_item>
              <name>WAN_GW</name>
            </gateway_item>
          </gateways>
        </pfsense>
        """;

    [Fact]
    public void ParsesSingleValueHostAndNetworkAliases()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        var s1 = r.Addresses.Single(a => a.Name == "SERVER1");
        Assert.Equal(AddressKind.Host, s1.Kind);
        Assert.Equal("Web server", s1.Comment);
        Assert.Equal(AddressKind.Subnet, r.Addresses.Single(a => a.Name == "NET_A").Kind);
    }

    [Fact]
    public void SplitsMultiValueAliasIntoNumberedAddressObjects()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        Assert.Contains(r.Addresses, a => a.Name == "MULTI_HOSTS_1" && a.Value == "10.4.1.1");
        Assert.Contains(r.Addresses, a => a.Name == "MULTI_HOSTS_2" && a.Value == "10.4.1.2");
    }

    [Fact]
    public void ParsesRuleWithAliasSourceAndPortAliasDestination()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Web access");
        Assert.Equal(PolicyAction.Allow, rule.Action);
        Assert.Equal("lan", rule.SourceInterface);
        Assert.Contains("SERVER1", rule.Sources);
        Assert.Contains("any", rule.Destinations);
        Assert.Contains("tcp/80", rule.Services);
        Assert.Contains("tcp/443", rule.Services);
        Assert.True(rule.Log);
    }

    [Fact]
    public void DisabledBlockRuleBecomesDisabledDenyRule()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Default deny");
        Assert.Equal(PolicyAction.Deny, rule.Action);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void NegatedMatchRuleIsSkippedRatherThanMigratedInverted()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        Assert.DoesNotContain(r.Rules, x => x.Name == "Negated rule");
        Assert.Contains(r.Issues, i => i.Message.Contains("negated"));
    }

    [Fact]
    public void NatAndGatewaySectionsAreSurfacedAsReviewItems()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        Assert.Contains(r.ReviewItems, x => x.Contains("NAT"));
        Assert.Contains(r.ReviewItems, x => x.Contains("routing") || x.Contains("Gateway"));
    }

    [Fact]
    public void InvalidXmlProducesAnIssueNotAnException()
    {
        var r = PfSenseConfigParser.Parse("not xml at all <<<");
        Assert.Empty(r.Addresses);
        Assert.NotEmpty(r.Issues);
    }

    [Fact]
    public void EmptyDocumentProducesNoObjectsAndNoRules()
    {
        var r = PfSenseConfigParser.Parse("<pfsense></pfsense>");
        Assert.Empty(r.Addresses);
        Assert.Empty(r.Rules);
    }

    [Fact]
    public void ParsedObjectsFeedDirectlyIntoTheExistingCiscoAsaGenerator()
    {
        var r = PfSenseConfigParser.Parse(SampleConfig);
        var addrResult = FirewallGenerators.Addresses(FirewallVendor.CiscoAsa, r.Addresses, new GeneratorOptions());
        Assert.Contains("object network SERVER1", addrResult.Script);
    }
}
