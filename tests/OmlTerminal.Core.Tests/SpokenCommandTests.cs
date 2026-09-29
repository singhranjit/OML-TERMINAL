using OmlTerminal.Core.Voice;

namespace OmlTerminal.Core.Tests;

public class SpokenNormalizerTests
{
    [Theory]
    [InlineData("ten dot zero dot zero dot one slash twenty four", "10.0.0.1/24")]
    [InlineData("one nine two dot one six eight dot one dot two fifty four", "192.168.1.254")]
    [InlineData("one ninety two point one sixty eight point ten point one", "192.168.10.1")]
    [InlineData("two fifty five dot two fifty five dot two fifty two dot zero", "255.255.252.0")]
    [InlineData("two hundred fifty five dot two fifty five dot two fifty five dot zero", "255.255.255.0")]
    [InlineData("10.0.0.1 / 24", "10.0.0.1/24")]
    [InlineData("interface gigabit ethernet zero slash zero", "interface GigabitEthernet0/0")]
    [InlineData("interface gig 1/0/24", "interface GigabitEthernet1/0/24")]
    [InlineData("interface ten gig ethernet one slash one", "interface TenGigabitEthernet1/1")]
    [InlineData("interface fast ethernet 0 / 1", "interface FastEthernet0/1")]
    [InlineData("interface loopback zero", "interface Loopback0")]
    [InlineData("interface vlan ten", "interface Vlan10")]
    [InlineData("interface port channel 1", "interface Port-channel1")]
    [InlineData("go to config t", "go to configure terminal")]
    [InlineData("Go to configuration mode.", "Go to configure terminal")]
    [InlineData("create vlan twenty", "create vlan 20")]
    [InlineData("show one thing", "show one thing")]              // not numeric context: the word stays
    [InlineData("interface gi0/0", "interface gi0/0")]            // compact CLI untouched
    public void Normalizes(string spoken, string expected) => Assert.Equal(expected, SpokenNormalizer.Normalize(spoken));

    [Fact]
    public void SentenceBreaksBecomeSeparatorsButIpDotsSurvive()
        => Assert.Equal("Go to interface GigabitEthernet0/0, Add IP address 10.0.0.1/24",
            SpokenNormalizer.Normalize("Go to interface gigabit ethernet zero slash zero. Add IP address 10.0.0.1/24."));
}

public class SpokenCommandTests
{
    private readonly CiscoIosGrammar _ios = new();

    [Fact]
    public void RealDictationOutput_BecomesExactCli()
    {
        var lines = VoiceCommandParser.Expand(
            "Go to configure terminal and then go to interface gigabit ethernet zero slash zero, add IP address ten dot zero dot zero dot one slash twenty four and bring it up. Then save the config.",
            _ios);
        Assert.Equal(["configure terminal", "interface GigabitEthernet0/0", "ip address 10.0.0.1 255.255.255.0", "no shutdown", "write memory"], lines);
    }

    [Fact]
    public void UserExample_IncompleteAddressIsFlaggedNotGuessed()
    {
        var lines = VoiceCommandParser.Expand("go to configure terminal and go to interface gi0/0 and add ip address", _ios);
        Assert.Equal(["configure terminal", "interface gi0/0", "add ip address"], lines);
        Assert.True(VoiceCommandParser.LooksIncomplete(lines[2]));
        Assert.False(VoiceCommandParser.LooksIncomplete(lines[0]));
        Assert.False(VoiceCommandParser.LooksIncomplete(lines[1]));
    }

    [Theory]
    [InlineData("please set the IP to 192.168.10.1/24", "ip address 192.168.10.1 255.255.255.0")]
    [InlineData("give it ip 10.1.1.1 with mask 255.255.255.252", "ip address 10.1.1.1 255.255.255.252")]
    [InlineData("ip address 10.0.0.1 subnet mask 255.0.0.0", "ip address 10.0.0.1 255.0.0.0")]
    [InlineData("get ip from dhcp", "ip address dhcp")]
    [InlineData("add a default route via 10.0.0.254", "ip route 0.0.0.0 0.0.0.0 10.0.0.254")]
    [InlineData("add a static route to 172.16.0.0/16 via 10.0.0.2", "ip route 172.16.0.0 255.255.0.0 10.0.0.2")]
    [InlineData("bring the interface up", "no shutdown")]
    [InlineData("enable the port", "no shutdown")]
    [InlineData("shut it down", "shutdown")]
    [InlineData("disable the interface", "shutdown")]
    [InlineData("create vlan 20", "vlan 20")]
    [InlineData("name it USERS", "name USERS")]
    [InlineData("make it an access port", "switchport mode access")]
    [InlineData("put it in vlan 20", "switchport access vlan 20")]
    [InlineData("make it a trunk", "switchport mode trunk")]
    [InlineData("allow vlans 10,20,30", "switchport trunk allowed vlan 10,20,30")]
    [InlineData("go to privileged mode", "enable")]
    [InlineData("exit config mode", "end")]
    [InlineData("go back", "exit")]
    [InlineData("show me the running config", "show running-config")]
    [InlineData("show interfaces brief", "show ip interface brief")]
    [InlineData("show the routing table", "show ip route")]
    [InlineData("show vlans", "show vlan brief")]
    [InlineData("show neighbors", "show cdp neighbors")]
    [InlineData("copy run start", "write memory")]
    [InlineData("change the hostname to CORE-SW1", "hostname CORE-SW1")]
    public void NaturalPhrases(string phrase, string expected) => Assert.Equal([expected], VoiceCommandParser.Expand(phrase, _ios));

    [Fact]
    public void FillersAreDropped_LiteralCliPassesThrough()
    {
        Assert.Equal(["show ip bgp summary"], VoiceCommandParser.Expand("can you show ip bgp summary please", _ios));
        Assert.Equal(["ping 8.8.8.8"], VoiceCommandParser.Expand("ping 8.8.8.8", _ios));
    }

    [Fact]
    public void NextHopIsNotASeparator()
        => Assert.Equal(["ip route 0.0.0.0 0.0.0.0 10.0.0.1"], VoiceCommandParser.Expand("add default route next hop 10.0.0.1", _ios));

    [Fact]
    public void Asa_UsesItsOwnInterfaceBriefSyntax()
        => Assert.Equal(["show interface ip brief"], VoiceCommandParser.Expand("show interfaces brief", new CiscoAsaGrammar()));
}
