using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class FortiGateConfigParserTests
{
    private const string SampleConfig = """
        #config-version=FGT60F-7.0
        config firewall address
            edit "SERVER1"
                set subnet 10.1.1.10 255.255.255.255
                set comment "Web server"
            next
            edit "NET-A"
                set subnet 10.2.0.0 255.255.255.0
            next
            edit "RANGE-A"
                set type iprange
                set start-ip 10.3.1.10
                set end-ip 10.3.1.20
            next
            edit "FQDN-A"
                set type fqdn
                set fqdn "www.example.com"
            next
        end
        config firewall service custom
            edit "HTTPS-ALT"
                set tcp-portrange 8443
            next
            edit "DNS-CUSTOM"
                set tcp-portrange 53
                set udp-portrange 53
            next
        end
        config firewall policy
            edit 1
                set name "Users-to-Internet"
                set srcintf "port2"
                set dstintf "port1"
                set srcaddr "SERVER1" "NET-A"
                set dstaddr "all"
                set action accept
                set service "HTTP" "HTTPS-ALT"
                set logtraffic all
                set nat enable
            next
            edit 2
                set name "Block-Bad"
                set srcintf "any"
                set dstintf "port1"
                set srcaddr "all"
                set dstaddr "all"
                set action deny
                set service "ALL"
                set status disable
            next
        end
        config vpn ipsec phase1-interface
            edit "VPN-TO-HQ"
                set peertype any
            next
        end
        config router static
            edit 1
                set dst 0.0.0.0 0.0.0.0
                set gateway 203.0.113.1
            next
        end
        """;

    [Fact]
    public void ParsesAddressObjectsOfEveryKind()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        Assert.Equal(4, r.Addresses.Count);
        var s1 = r.Addresses.Single(a => a.Name == "SERVER1");
        Assert.Equal(AddressKind.Host, s1.Kind);
        Assert.Equal("Web server", s1.Comment);
        Assert.Equal(AddressKind.Subnet, r.Addresses.Single(a => a.Name == "NET-A").Kind);
        var range = r.Addresses.Single(a => a.Name == "RANGE-A");
        Assert.Equal(AddressKind.Range, range.Kind);
        Assert.Equal("10.3.1.10-10.3.1.20", range.Value);
        var fqdn = r.Addresses.Single(a => a.Name == "FQDN-A");
        Assert.Equal(AddressKind.Fqdn, fqdn.Kind);
        Assert.Equal("www.example.com", fqdn.Value);
    }

    [Fact]
    public void ParsesServiceObjectsIncludingTcpUdpCombined()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        var https = r.Services.Single(s => s.Name == "HTTPS-ALT");
        Assert.Equal(ServiceProtocol.Tcp, https.Protocol);
        Assert.Equal("8443", https.DestinationPorts);
        var dns = r.Services.Single(s => s.Name == "DNS-CUSTOM");
        Assert.Equal(ServiceProtocol.TcpUdp, dns.Protocol);
    }

    [Fact]
    public void ParsesPolicyWithAddressReferencesAndPredefinedServices()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Users-to-Internet");
        Assert.Equal(PolicyAction.Allow, rule.Action);
        Assert.Equal("port2", rule.SourceInterface);
        Assert.Equal("port1", rule.DestinationInterface);
        Assert.Contains("SERVER1", rule.Sources);
        Assert.Contains("NET-A", rule.Sources);
        Assert.Contains("any", rule.Destinations);
        Assert.Contains("tcp/80", rule.Services);      // predefined HTTP resolved to a real port
        Assert.Contains("HTTPS-ALT", rule.Services);   // custom object name passed through by name, not guessed at
        Assert.True(rule.Log);
        Assert.True(rule.Nat);
    }

    [Fact]
    public void DisabledPolicyBecomesDisabledRule()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        var rule = r.Rules.Single(x => x.Name == "Block-Bad");
        Assert.False(rule.Enabled);
        Assert.Equal(PolicyAction.Deny, rule.Action);
        Assert.Contains("any", rule.Services); // ALL resolved to any
    }

    [Fact]
    public void CollectsInterfacesFound()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        Assert.Contains("port1", r.InterfacesFound);
        Assert.Contains("port2", r.InterfacesFound);
    }

    [Fact]
    public void VpnAndRoutingSectionsAreSurfacedAsReviewItemsNotParsedAsObjects()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        Assert.Contains(r.ReviewItems, x => x.Contains("VPN"));
        Assert.Contains(r.ReviewItems, x => x.Contains("routing"));
        // Nested "edit"/"next" inside the VPN/router blocks must not be mistaken for address/service/policy objects.
        Assert.DoesNotContain(r.Addresses, a => a.Name == "VPN-TO-HQ");
    }

    [Fact]
    public void EmptyConfigProducesNoObjectsAndNoRules()
    {
        var r = FortiGateConfigParser.Parse("");
        Assert.Empty(r.Addresses);
        Assert.Empty(r.Services);
        Assert.Empty(r.Rules);
    }

    [Fact]
    public void ParsedObjectsFeedDirectlyIntoTheExistingCiscoAsaGenerator()
    {
        var r = FortiGateConfigParser.Parse(SampleConfig);
        var result = FirewallGenerators.Addresses(FirewallVendor.CiscoAsa, r.Addresses, new GeneratorOptions());
        Assert.Contains("object network SERVER1", result.Script);
    }
}
