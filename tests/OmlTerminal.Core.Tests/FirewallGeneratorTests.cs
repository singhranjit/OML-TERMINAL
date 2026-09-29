using OmlTerminal.Core.Firewall;

namespace OmlTerminal.Core.Tests;

public class FirewallGeneratorTests
{
    private static string N(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void AddressParser_RecognisesEveryKind()
    {
        var (objs, issues) = AddressListParser.Parse("""
            10.1.1.1
            10.2.0.0/16
            10.3.0.0 255.255.255.0
            10.4.4.10-10.4.4.20
            10.5.5.1-9
            2001:db8::/64
            example.com
            *.example.org
            # a comment line
            """);
        Assert.Empty(issues);
        Assert.Equal(
            [AddressKind.Host, AddressKind.Subnet, AddressKind.Subnet, AddressKind.Range, AddressKind.Range, AddressKind.Subnet, AddressKind.Fqdn, AddressKind.WildcardFqdn],
            objs.Select(o => o.Kind));
        Assert.Equal(["H-10.1.1.1", "N-10.2.0.0_16", "N-10.3.0.0_24", "R-10.4.4.10-10.4.4.20", "R-10.5.5.1-10.5.5.9", "N-2001:db8::_64", "FQDN-example.com", "WFQDN-example.org"],
            objs.Select(o => o.Name));
    }

    [Fact]
    public void AddressParser_HandlesCsvNamesCommentsAndErrors()
    {
        var (objs, issues) = AddressListParser.Parse("""
            WebSrv,10.1.1.10,DMZ web server
            10.9.9.9; 10.8.8.8
            10.1.1.5/24
            not_an_address!
            WebSrv,10.1.1.11
            """);
        Assert.Equal(["WebSrv", "H-10.9.9.9", "H-10.8.8.8"], objs.Select(o => o.Name));
        Assert.Equal("DMZ web server", objs[0].Comment);
        Assert.Equal(3, issues.Count);
        Assert.Contains("10.1.1.0/24", issues[0].Message); // host bits set - suggests the network
        Assert.Contains("duplicate", issues[2].Message);
    }

    [Fact]
    public void Parsers_AcceptBareCarriageReturnLineBreaks()
    {
        // WinUI's multi-line TextBox separates lines with a bare "\r".
        var (addrs, addrIssues) = AddressListParser.Parse("# comment\r10.1.1.1\r10.2.0.0/16");
        Assert.Empty(addrIssues);
        Assert.Equal(2, addrs.Count);
        var (svcs, _) = ServiceListParser.Parse("tcp/443\rudp/53");
        Assert.Equal(2, svcs.Count);
        Assert.Equal(["term len 0", "show run"], Backup.ConfigBackup.Custom(Backup.BackupMode.Shell, "term len 0\rshow run").Commands);
    }

    [Fact]
    public void AddressParser_PlainNamingWithPrefix()
    {
        var (objs, _) = AddressListParser.Parse("10.1.1.1\n10.2.0.0/16", new NamingOptions { Style = NamingStyle.Plain, Prefix = "BLK_" });
        Assert.Equal(["BLK_10.1.1.1", "BLK_10.2.0.0/16"], objs.Select(o => o.Name));
    }

    [Fact]
    public void ServiceParser_AcceptsCommonNotations()
    {
        var (objs, issues) = ServiceListParser.Parse("""
            443
            udp/500-501
            tcp-udp/53
            HTTPS-ALT,tcp/8443,Alt web
            SNMP,udp,161
            tcp/443:1024-65535
            icmp
            tcp/99999
            """);
        Assert.Equal(["TCP-443", "UDP-500-501", "TCP_UDP-53", "HTTPS-ALT", "SNMP", "ICMP-ALL"], objs.Select(o => o.Name));
        Assert.Equal("Alt web", objs[3].Comment);
        Assert.Equal(ServiceProtocol.Udp, objs[4].Protocol);
        // tcp/443:1024-65535 auto-names to TCP-443 (a duplicate); tcp/99999 is out of range.
        Assert.Equal(2, issues.Count);
        Assert.Contains("duplicate", issues[0].Message);
        Assert.Equal(8, issues[1].Line);
    }

    [Fact]
    public void FortiGate_AddressesAndGroup()
    {
        var (objs, _) = AddressListParser.Parse("10.1.1.1\n10.2.0.0/16\n10.4.4.10-10.4.4.20\nexample.com\n*.example.org");
        var cfg = FirewallGenerators.Addresses(FirewallVendor.FortiGate, objs, new GeneratorOptions { GroupName = "GRP-Test", IncludeRollback = true });
        Assert.Equal(N("""
            config firewall address
                edit "H-10.1.1.1"
                    set subnet 10.1.1.1 255.255.255.255
                next
                edit "N-10.2.0.0_16"
                    set subnet 10.2.0.0 255.255.0.0
                next
                edit "R-10.4.4.10-10.4.4.20"
                    set type iprange
                    set start-ip 10.4.4.10
                    set end-ip 10.4.4.20
                next
                edit "FQDN-example.com"
                    set type fqdn
                    set fqdn "example.com"
                next
                edit "WFQDN-example.org"
                    set type fqdn
                    set fqdn "*.example.org"
                next
            end
            config firewall addrgrp
                edit "GRP-Test"
                    set member "H-10.1.1.1" "N-10.2.0.0_16" "R-10.4.4.10-10.4.4.20" "FQDN-example.com" "WFQDN-example.org"
                next
            end

            """), N(cfg.Script));
        Assert.StartsWith(N("config firewall addrgrp\n    delete \"GRP-Test\"\nend\nconfig firewall address\n    delete \"H-10.1.1.1\""), N(cfg.Rollback));
    }

    [Fact]
    public void FortiGate_VdomLegacyWildcardAndIpv6()
    {
        var (objs, _) = AddressListParser.Parse("10.1.1.1\n2001:db8::/64\n*.example.org");
        var cfg = FirewallGenerators.Addresses(FirewallVendor.FortiGate, objs,
            new GeneratorOptions { GroupName = "G", Vdom = "root", LegacyWildcardFqdn = true, AssociatedInterface = "port1" });
        var s = N(cfg.Script);
        Assert.StartsWith("config vdom\nedit root\nconfig firewall address\n", s);
        Assert.Contains("        set associated-interface \"port1\"\n", s);
        Assert.Contains("config firewall wildcard-fqdn custom\n    edit \"WFQDN-example.org\"\n        set wildcard-fqdn \"*.example.org\"", s);
        Assert.Contains("config firewall address6\n    edit \"N-2001:db8::_64\"\n        set ip6 2001:db8::/64", s);
        Assert.Contains("config firewall addrgrp6\n    edit \"G_v6\"", s);
        Assert.EndsWith("end\nend\n", s);
        Assert.Equal(2, cfg.Warnings.Count);
    }

    [Fact]
    public void FortiGate_Services()
    {
        var (objs, _) = ServiceListParser.Parse("tcp/8080\ntcp-udp/53\nicmp");
        var cfg = FirewallGenerators.Services(FirewallVendor.FortiGate, objs, new GeneratorOptions { GroupName = "SG-App" });
        Assert.Equal(N("""
            config firewall service custom
                edit "TCP-8080"
                    set tcp-portrange 8080
                next
                edit "TCP_UDP-53"
                    set tcp-portrange 53
                    set udp-portrange 53
                next
                edit "ICMP-ALL"
                    set protocol ICMP
                next
            end
            config firewall service group
                edit "SG-App"
                    set member "TCP-8080" "TCP_UDP-53" "ICMP-ALL"
                next
            end

            """), N(cfg.Script));
    }

    [Fact]
    public void PaloAlto_AddressesSanitizesNamesAndMovesWildcards()
    {
        var (objs, _) = AddressListParser.Parse("10.2.0.0/16\n10.4.4.10-10.4.4.20\nexample.com\n*.example.org");
        var cfg = FirewallGenerators.Addresses(FirewallVendor.PaloAlto, objs, new GeneratorOptions { GroupName = "GRP Test", PanScope = "dg:Branch Sites" });
        var s = N(cfg.Script);
        Assert.Contains("set device-group \"Branch Sites\" address N-10.2.0.0_16 ip-netmask 10.2.0.0/16\n", s);
        Assert.Contains("address R-10.4.4.10-10.4.4.20 ip-range 10.4.4.10-10.4.4.20\n", s);
        Assert.Contains("address FQDN-example.com fqdn example.com\n", s);
        Assert.Contains("address-group \"GRP Test\" static [ N-10.2.0.0_16 R-10.4.4.10-10.4.4.20 FQDN-example.com ]", s);
        Assert.Contains("custom-url-category \"GRP Test-wildcard-fqdn\" list [ *.example.org ]", s);
        Assert.Single(cfg.Warnings);
    }

    [Fact]
    public void PaloAlto_ScopePrefixes()
    {
        Assert.Equal("set ", FirewallGenerators.PanPrefix(""));
        Assert.Equal("set shared ", FirewallGenerators.PanPrefix("shared"));
        Assert.Equal("set vsys vsys2 ", FirewallGenerators.PanPrefix("vsys:vsys2"));
        Assert.Equal("set device-group DG1 ", FirewallGenerators.PanPrefix("dg:DG1"));
    }

    [Fact]
    public void PaloAlto_ServicesSplitTcpUdpAndSkipIcmp()
    {
        var (objs, _) = ServiceListParser.Parse("tcp/443\ntcp-udp/53\nicmp");
        var cfg = FirewallGenerators.Services(FirewallVendor.PaloAlto, objs, new GeneratorOptions { GroupName = "SG", IncludeRollback = true });
        Assert.Equal(N("""
            set service TCP-443 protocol tcp port 443
            set service TCP-53 protocol tcp port 53
            set service UDP-53 protocol udp port 53
            set service-group SG members [ TCP-443 TCP-53 UDP-53 ]

            """), N(cfg.Script));
        Assert.StartsWith("delete service-group SG", cfg.Rollback);
        Assert.Single(cfg.Warnings);
    }

    [Fact]
    public void CiscoAsa_ObjectsAndGroups()
    {
        var (addrs, _) = AddressListParser.Parse("10.1.1.1\n10.2.0.0/16\n10.4.4.10-10.4.4.20\nexample.com");
        var a = N(FirewallGenerators.Addresses(FirewallVendor.CiscoAsa, addrs, new GeneratorOptions { GroupName = "OG-Test" }).Script);
        Assert.Contains("object network H-10.1.1.1\n host 10.1.1.1\n", a);
        Assert.Contains("object network N-10.2.0.0_16\n subnet 10.2.0.0 255.255.0.0\n", a);
        Assert.Contains(" range 10.4.4.10 10.4.4.20\n", a);
        Assert.Contains(" fqdn v4 example.com\n", a);
        Assert.Contains("object-group network OG-Test\n network-object object H-10.1.1.1\n", a);

        var (svcs, _) = ServiceListParser.Parse("tcp/8000-8010\nudp/161");
        var s = N(FirewallGenerators.Services(FirewallVendor.CiscoAsa, svcs, new GeneratorOptions()).Script);
        Assert.Equal("object service TCP-8000-8010\n service tcp destination range 8000 8010\nobject service UDP-161\n service udp destination eq 161\n", s);
    }

    [Fact]
    public void SafeName_EnforcesVendorRules()
    {
        Assert.Equal("wildcard.example.com", FirewallGenerators.SafeName("*.example.com", FirewallVendor.PaloAlto));
        Assert.Equal("N-10.0.0.0_8", FirewallGenerators.SafeName("N-10.0.0.0/8", FirewallVendor.PaloAlto));
        Assert.Equal("my_object", FirewallGenerators.SafeName("my object", FirewallVendor.CiscoAsa));
        Assert.Equal(63, FirewallGenerators.SafeName(new string('a', 100), FirewallVendor.PaloAlto).Length);
    }
}
