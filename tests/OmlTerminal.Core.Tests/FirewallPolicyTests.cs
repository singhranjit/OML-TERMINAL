using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class FirewallPolicyTests
{
    private static string N(string s) => s.Replace("\r\n", "\n");

    private const string Sheet = """
        # comment
        Name,Action,SourceInterface,DestinationInterface,Source,Destination,Service,Application,Schedule,NAT,Log,Comment,Enabled
        Web-Out,allow,port2,port1,10.10.0.0/16,any,tcp/80;tcp/443,,,yes,yes,Browsing,yes
        Block,deny,,,"any",203.0.113.10-203.0.113.50,any,,,no,no,,no
        """;

    [Fact]
    public void Sheet_ParsesRowsQuotesAndDefaults()
    {
        var (rules, issues) = PolicySheet.Parse(Sheet);
        Assert.Empty(issues);
        Assert.Equal(2, rules.Count);
        Assert.Equal(["tcp/80", "tcp/443"], rules[0].Services);
        Assert.True(rules[0].Nat);
        Assert.Equal(PolicyAction.Deny, rules[1].Action);
        Assert.False(rules[1].Enabled);
        Assert.Equal(["any"], rules[1].Sources);
    }

    [Fact]
    public void Sheet_AcceptsAliasHeadersTabsAndReportsBadActions()
    {
        var (rules, issues) = PolicySheet.Parse("Policy Name\tFrom\tTo\tSrc\tDst\tPorts\tAction\nA\ttrust\tuntrust\t10.1.1.1\t10.2.2.2\t22\tpermit\nB\tx\ty\t1.1.1.1\t2.2.2.2\t22\tmaybe");
        Assert.Single(rules);
        Assert.Equal(("trust", "untrust", PolicyAction.Allow), (rules[0].SourceInterface, rules[0].DestinationInterface, rules[0].Action));
        Assert.Contains("action", Assert.Single(issues).Message);
    }

    [Fact]
    public void Sheet_HandlesSemicolonLocaleExcelAndBom()
    {
        var (rules, issues) = PolicySheet.Parse("﻿# comment\r\nName;Action;Source;Destination;Service\r\nWeb;allow;10.1.1.1;any;\"tcp/80;tcp/443\"\r\n");
        Assert.Empty(issues);
        Assert.Equal(["tcp/80", "tcp/443"], Assert.Single(rules).Services);
    }

    [Fact]
    public void Sheet_MissingColumnsIsAnIssue()
    {
        var (rules, issues) = PolicySheet.Parse("Name,Action\nx,allow");
        Assert.Empty(rules);
        Assert.Contains("Source", issues[0].Message);
    }

    [Fact]
    public void Sample_ParsesCleanlyForEveryVendor()
    {
        var (rules, issues) = PolicySheet.Parse(PolicySheet.Sample);
        Assert.Empty(issues);
        Assert.Equal(5, rules.Count);
        foreach (var v in Enum.GetValues<FirewallVendor>())
        {
            var cfg = FirewallGenerators.Policies(v, rules, new PolicyOptions { IncludeRollback = true });
            Assert.False(string.IsNullOrWhiteSpace(cfg.Script), v.ToString());
            Assert.False(string.IsNullOrWhiteSpace(cfg.Rollback), v.ToString());
        }
    }

    [Fact]
    public void FortiGate_PolicyWithObjectsNatAndIds()
    {
        var (rules, _) = PolicySheet.Parse(Sheet);
        var cfg = FirewallGenerators.Policies(FirewallVendor.FortiGate, rules, new PolicyOptions { FortiStartId = 100, IncludeRollback = true });
        var s = N(cfg.Script);
        Assert.Contains("edit \"N-10.10.0.0_16\"\n        set subnet 10.10.0.0 255.255.0.0", s);
        Assert.Contains("edit \"TCP-443\"\n        set tcp-portrange 443", s);
        Assert.Contains("""
                edit 100
                    set name "Web-Out"
                    set srcintf "port2"
                    set dstintf "port1"
                    set action accept
                    set srcaddr "N-10.10.0.0_16"
                    set dstaddr "all"
                    set schedule "always"
                    set service "TCP-80" "TCP-443"
                    set logtraffic all
                    set nat enable
                    set comments "Browsing"
                next
            """.Replace("\r\n", "\n"), s);
        Assert.Contains("    edit 101\n", s);
        Assert.Contains("set srcintf \"any\"", s);
        Assert.Contains("set status disable", s);
        Assert.StartsWith("config firewall policy\n    delete 100\n    delete 101\nend", N(cfg.Rollback));
    }

    [Fact]
    public void PaloAlto_RulesUseZonesAndApplicationDefault()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,From,To,Source,Destination,Service,Application\nWeb,allow,trust,untrust,10.1.1.0/24,any,any,ssl;web-browsing\nDNS,drop,trust,untrust,any,8.8.8.8,tcp-udp/53,");
        var s = N(FirewallGenerators.Policies(FirewallVendor.PaloAlto, rules, new PolicyOptions()).Script);
        Assert.Contains("set rulebase security rules Web from trust\n", s);
        Assert.Contains("set rulebase security rules Web application [ ssl web-browsing ]\n", s);
        Assert.Contains("set rulebase security rules Web service application-default\n", s);
        Assert.Contains("set rulebase security rules DNS service [ TCP-53 UDP-53 ]\n", s);
        Assert.Contains("set rulebase security rules DNS action drop\n", s);
        Assert.Contains("set address H-8.8.8.8 ip-netmask 8.8.8.8/32", s);
    }

    [Fact]
    public void PaloAlto_PanoramaGoesToPreRulebase()
    {
        Assert.Equal("set device-group DG1 pre-rulebase security rules", FirewallGenerators.PanRulebase("dg:DG1"));
        Assert.Equal("set vsys vsys2 rulebase security rules", FirewallGenerators.PanRulebase("vsys:vsys2"));
    }

    [Fact]
    public void CiscoAsa_AclWithGroupsAndAccessGroup()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,SourceInterface,Source,Destination,Service,Log\nWeb,allow,inside,10.1.1.1;10.1.1.2,any,tcp/443,yes\nAll,deny,inside,any,any,any,no");
        var s = N(FirewallGenerators.Policies(FirewallVendor.CiscoAsa, rules, new PolicyOptions { IncludeRollback = true }).Script);
        Assert.Contains("object-group network Web_SRC\n network-object object H-10.1.1.1\n network-object object H-10.1.1.2\n", s);
        Assert.Contains("access-list inside_access_in extended permit object TCP-443 object-group Web_SRC any log\n", s);
        Assert.Contains("access-list inside_access_in extended deny ip any any\n", s);
        Assert.Contains("access-group inside_access_in in interface inside\n", s);
    }

    [Fact]
    public void CheckPoint_OneSessionWithRulesAndDnsDomains()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,Source,Destination,Service\nSaaS,allow,10.1.0.0/16,*.office.com,tcp/443");
        var s = N(FirewallGenerators.Policies(FirewallVendor.CheckPoint, rules, new PolicyOptions()).Script);
        Assert.StartsWith("# Log in once", s);
        Assert.Contains("add dns-domain name \".office.com\" is-sub-domain true", s);
        Assert.Contains("add access-rule layer \"Network\" position \"bottom\" name \"SaaS\" source.1 \"N-10.1.0.0_16\" destination.1 \".office.com\" service.1 \"TCP-443\" action \"Accept\" track.type \"Log\"", s);
        Assert.Equal(1, s.Split("mgmt_cli login").Length - 1);
        Assert.EndsWith("publish\nmgmt_cli -s id.txt logout\n", s);
    }

    [Fact]
    public void Ftd_RulesUseLiterals()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,Source,Destination,Service\nWeb,allow,10.1.1.0/24,10.2.2.2,tcp/443;icmp");
        var s = FirewallGenerators.Policies(FirewallVendor.CiscoFtd, rules, new PolicyOptions()).Script;
        Assert.Contains("/policy/accesspolicies/{containerUUID}/accessrules?bulk=true", s);
        Assert.Contains("\"type\": \"Network\"", s);
        Assert.Contains("\"value\": \"10.1.1.0/24\"", s);
        Assert.Contains("\"type\": \"PortLiteral\"", s);
        Assert.Contains("\"ICMPv4PortLiteral\"", s);
        Assert.Contains("\"action\": \"ALLOW\"", s);
    }

    [Fact]
    public void Junos_ZonePairAndGlobalPolicies()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,From,To,Source,Destination,Service,Enabled\nWeb,allow,trust,untrust,10.1.1.0/24,any,tcp/443,yes\nG,deny,,,any,any,any,no");
        var s = N(FirewallGenerators.Policies(FirewallVendor.JuniperSrx, rules, new PolicyOptions()).Script);
        Assert.Contains("set security address-book global address N-10.1.1.0_24 10.1.1.0/24\n", s);
        Assert.Contains("set applications application TCP-443 protocol tcp destination-port 443\n", s);
        Assert.Contains("set security policies from-zone trust to-zone untrust policy Web match application TCP-443\n", s);
        Assert.Contains("set security policies from-zone trust to-zone untrust policy Web then permit\n", s);
        Assert.Contains("set security policies global policy G then deny\n", s);
        Assert.Contains("deactivate security policies global policy G\n", s);
    }

    [Fact]
    public void Sophos_RulesAsXml()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,From,To,Source,Destination,Service\nWeb,allow,LAN,WAN,10.1.1.0/24,any,tcp/443");
        var s = N(FirewallGenerators.Policies(FirewallVendor.Sophos, rules, new PolicyOptions()).Script);
        Assert.Contains("<IPHost><Name>N-10.1.1.0_24</Name><IPFamily>IPv4</IPFamily><HostType>Network</HostType><IPAddress>10.1.1.0</IPAddress><Subnet>255.255.255.0</Subnet></IPHost>", s);
        Assert.Contains("<Services><Name>TCP-443</Name><Type>TCPorUDP</Type>", s);
        Assert.Contains("<SourceZones><Zone>LAN</Zone></SourceZones>", s);
        Assert.Contains("<SourceNetworks><Network>N-10.1.1.0_24</Network></SourceNetworks>", s);
        Assert.DoesNotContain("<DestinationNetworks>", s);
        Assert.Contains("<Action>Accept</Action>", s);
    }

    [Fact]
    public void PfSense_OneRulePerProtocolWithAliases()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,SourceInterface,Source,Destination,Service\nWeb,allow,LAN,10.1.1.1;10.1.1.2,any,tcp/443;tcp/8443;udp/53");
        var s = N(FirewallGenerators.Policies(FirewallVendor.PfSense, rules, new PolicyOptions()).Script);
        Assert.Contains("<name>H_10_1_1_1</name>", s);
        Assert.Contains("<name>Web_SRC</name>", s);
        Assert.Contains("<address>H_10_1_1_1 H_10_1_1_2</address>", s);
        Assert.Contains("<name>Web_tcp</name>\n    <type>port</type>\n    <address>443 8443</address>", s);
        Assert.Contains("<protocol>tcp</protocol>", s);
        Assert.Contains("<protocol>udp</protocol>", s);
        Assert.Contains("<destination><any/><port>53</port></destination>", s);
        Assert.Contains("<interface>lan</interface>", s);
    }

    [Fact]
    public void PaloAlto_WildcardDestinationBecomesUrlCategory()
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,Source,Destination,Service\nSaaS,allow,10.1.0.0/16,*.office.com,tcp/443");
        var s = N(FirewallGenerators.Policies(FirewallVendor.PaloAlto, rules, new PolicyOptions()).Script);
        Assert.DoesNotContain("WFQDN", s);
        Assert.Contains("set rulebase security rules SaaS destination any\n", s);
        Assert.Contains("set rulebase security rules SaaS category OML-wildcard-fqdn\n", s);
        Assert.Contains("custom-url-category OML-wildcard-fqdn list [ *.office.com ]", s);
    }

    [Theory]
    [InlineData(FirewallVendor.CiscoAsa)]
    [InlineData(FirewallVendor.JuniperSrx)]
    [InlineData(FirewallVendor.CiscoFtd)]
    [InlineData(FirewallVendor.PfSense)]
    public void WildcardOnlyRule_IsSkippedNeverWidenedToAny(FirewallVendor v)
    {
        var (rules, _) = PolicySheet.Parse("Name,Action,Source,Destination,Service\nOnlyWild,allow,10.1.0.0/16,*.office.com,tcp/443\nMixed,allow,10.1.0.0/16,*.x.com;10.9.9.9,tcp/22");
        var cfg = FirewallGenerators.Policies(v, rules, new PolicyOptions());
        Assert.DoesNotContain("OnlyWild", cfg.Script);
        Assert.Contains("Mixed", cfg.Script);
        Assert.DoesNotContain("x.com", cfg.Script.Replace("OML Terminal", ""));
        Assert.Contains(cfg.Warnings, w => w.Contains("OnlyWild") && w.Contains("skipped"));
    }

    [Theory]
    [InlineData(FirewallVendor.CheckPoint)]
    [InlineData(FirewallVendor.CiscoFtd)]
    [InlineData(FirewallVendor.JuniperSrx)]
    [InlineData(FirewallVendor.Sophos)]
    [InlineData(FirewallVendor.PfSense)]
    public void ObjectBuilder_NewVendorsProduceOutput(FirewallVendor v)
    {
        var (addrs, _) = AddressListParser.Parse("10.1.1.1\n10.2.0.0/16\n10.3.3.1-10.3.3.9\nexample.com");
        var (svcs, _) = ServiceListParser.Parse("tcp/443\nudp/500-501");
        var o = new GeneratorOptions { GroupName = "GRP-Test" };
        Assert.Contains("10.2.0.0", FirewallGenerators.Addresses(v, addrs, o).Script);
        Assert.Contains("443", FirewallGenerators.Services(v, svcs, o).Script);
    }

    [Fact]
    public void ObjectBuilder_PfSenseNamesAreLegal()
    {
        Assert.Equal("N_10_2_0_0_16", FirewallGenerators.SafeName("N-10.2.0.0/16", FirewallVendor.PfSense));
        Assert.Equal("A_443", FirewallGenerators.SafeName("443", FirewallVendor.PfSense));
        Assert.True(FirewallGenerators.SafeName(new string('x', 50), FirewallVendor.PfSense).Length <= 31);
    }
}
