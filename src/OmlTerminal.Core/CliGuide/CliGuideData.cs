namespace OmlTerminal.Core.CliGuide;

/// <summary>The built-in command reference data, one builder per vendor. Split across partial files by vendor family.</summary>
public static partial class CliGuideData
{
    public static IReadOnlyList<CliVendor> BuildAll() =>
    [
        CiscoIos(), CiscoNxos(), CiscoAsa(), AristaEos(),
        JuniperJunos(), FortiOs(), PanOs(),
    ];

    private static CliVendor JuniperJunos() => new("juniper-junos", "Juniper Junos", "user@host>",
    [
        // Operational
        C("show configuration", "Display the committed configuration.", CliMode.Operational, "Show & verify", "show configuration [| display set]", "show configuration | display set | match bgp"),
        C("show interfaces terse", "One-line status and IP of every interface.", CliMode.Operational, "Show & verify"),
        C("show interfaces <if>", "Detailed interface information.", CliMode.Operational, "Show & verify", "show interfaces ge-0/0/0 [detail | extensive]"),
        C("show route", "The routing table.", CliMode.Operational, "Routing", "show route [<prefix> | protocol bgp | table inet.0]", "show route 10.0.0.0/8"),
        C("show route summary", "Route counts per protocol and table.", CliMode.Operational, "Routing"),
        C("show bgp summary", "BGP neighbors and state.", CliMode.Operational, "Routing"),
        C("show ospf neighbor", "OSPF adjacencies.", CliMode.Operational, "Routing"),
        C("show lldp neighbors", "Connected LLDP devices.", CliMode.Operational, "Discovery"),
        C("show version", "Junos version and model.", CliMode.Operational, "Show & verify"),
        C("show chassis hardware", "Hardware components and serials.", CliMode.Operational, "Show & verify"),
        C("show system alarms", "Active system alarms.", CliMode.Operational, "Show & verify"),
        C("show security policies", "Configured security policies (SRX).", CliMode.Operational, "Security", "show security policies [hit-count]"),
        C("show security flow session", "Active sessions through the SRX.", CliMode.Operational, "Security"),
        C("monitor traffic interface <if>", "tcpdump-style live capture on an interface.", CliMode.Operational, "Troubleshoot", "monitor traffic interface ge-0/0/0 no-resolve"),
        C("ping <host>", "ICMP reachability test.", CliMode.Operational, "Troubleshoot", "ping 8.8.8.8 count 5"),
        C("traceroute <host>", "Trace the path to a host.", CliMode.Operational, "Troubleshoot", "traceroute 8.8.8.8"),
        C("request system reboot", "Reboot the device.", CliMode.Operational, "Save & manage"),
        // Configuration (set style)
        C("configure", "Enter configuration mode.", CliMode.Operational, "Config basics", "configure [exclusive | private]"),
        C("set system host-name <name>", "Set the device name.", CliMode.Config, "Config basics", "set system host-name CORE-SW1"),
        C("set interfaces <if> ...", "Configure an interface / unit.", CliMode.Config, "Interfaces", "set interfaces ge-0/0/0 unit 0 family inet address 10.0.0.1/24"),
        C("set routing-options static route ...", "Add a static route.", CliMode.Config, "Routing", "set routing-options static route 0.0.0.0/0 next-hop 10.0.0.254"),
        C("set protocols ospf area ...", "Configure OSPF.", CliMode.Config, "Routing", "set protocols ospf area 0 interface ge-0/0/0"),
        C("delete <path>", "Remove a configuration statement.", CliMode.Config, "Config basics", "delete interfaces ge-0/0/0 unit 0"),
        C("show | compare", "Diff the candidate config against the committed one.", CliMode.Config, "Config basics", "show | compare"),
        C("commit check", "Validate the candidate config without applying it.", CliMode.Config, "Save & manage", "commit check"),
        C("commit", "Apply the candidate configuration.", CliMode.Config, "Save & manage", "commit [confirmed <mins> | and-quit | comment \"<text>\"]"),
        C("rollback <n>", "Load a previous committed configuration.", CliMode.Config, "Save & manage", "rollback 1"),
    ]);

    private static CliVendor FortiOs() => new("fortinet-fortios", "Fortinet FortiOS", "FGT#",
    [
        C("get system status", "Firmware version, model, serial and mode.", CliMode.Operational, "Show & verify"),
        C("show", "Display the current (non-default) configuration of the scope you are in.", CliMode.Operational, "Show & verify", "show [full-configuration]"),
        C("show full-configuration", "Display the complete configuration including defaults.", CliMode.Operational, "Show & verify"),
        C("get system interface", "Interface addresses and status.", CliMode.Operational, "Show & verify", "get system interface [physical]"),
        C("diagnose sniffer packet <if> <filter> <v> <count>", "Built-in packet sniffer (tcpdump-style).", CliMode.Diagnostic, "Troubleshoot", "diagnose sniffer packet any 'host 8.8.8.8' 4 20 l"),
        C("get router info routing-table all", "The full routing table.", CliMode.Operational, "Routing"),
        C("get router info bgp summary", "BGP neighbor summary.", CliMode.Operational, "Routing"),
        C("diagnose debug flow", "Trace how the firewall handles a packet flow.", CliMode.Diagnostic, "Troubleshoot", "diagnose debug flow filter addr 8.8.8.8\ndiagnose debug flow trace start 10"),
        C("execute ping <host>", "ICMP reachability test.", CliMode.Operational, "Troubleshoot", "execute ping 8.8.8.8"),
        C("execute traceroute <host>", "Trace the path to a host.", CliMode.Operational, "Troubleshoot", "execute traceroute 8.8.8.8"),
        C("config firewall address", "Enter firewall address-object configuration.", CliMode.Config, "Objects", "config firewall address\n edit WEB\n set subnet 10.1.1.10 255.255.255.255\n next\nend"),
        C("config firewall policy", "Enter firewall policy configuration.", CliMode.Config, "Security", "config firewall policy\n edit 0\n ...\n next\nend"),
        C("config system interface", "Enter interface configuration.", CliMode.Config, "Interfaces", "config system interface\n edit port1\n set ip 10.0.0.1 255.255.255.0\n next\nend"),
        C("edit <name|id>", "Select or create an object within a config table.", CliMode.Config, "Config basics", "edit port1", "edit 0"),
        C("set <field> <value>", "Set a field on the object being edited.", CliMode.Config, "Config basics", "set ip 10.0.0.1 255.255.255.0"),
        C("next", "Save the current object and stay in the table.", CliMode.Config, "Config basics"),
        C("end", "Save and leave the current config table.", CliMode.Config, "Config basics"),
        C("execute backup config <dest>", "Back up the configuration to TFTP/USB/FTP.", CliMode.Operational, "Save & manage", "execute backup config tftp core.conf 10.0.0.9"),
        C("execute reboot", "Reboot the device.", CliMode.Operational, "Save & manage"),
        C("diagnose hardware deviceinfo nic <if>", "Low-level NIC counters and errors.", CliMode.Diagnostic, "Troubleshoot", "diagnose hardware deviceinfo nic port1"),
    ]);

    private static CliVendor PanOs() => new("paloalto-panos", "Palo Alto PAN-OS", "admin@PA>",
    [
        C("show system info", "Model, serial, software and management IP.", CliMode.Operational, "Show & verify"),
        C("show interface all", "Status of all interfaces.", CliMode.Operational, "Show & verify", "show interface <name>"),
        C("show routing route", "The routing table.", CliMode.Operational, "Routing"),
        C("show routing protocol bgp summary", "BGP neighbor summary.", CliMode.Operational, "Routing"),
        C("show session all", "Active sessions through the firewall.", CliMode.Operational, "Security", "show session all filter destination 8.8.8.8"),
        C("show session id <id>", "Details of one session.", CliMode.Operational, "Security"),
        C("test security-policy-match ...", "Find which security rule a flow hits.", CliMode.Operational, "Security", "test security-policy-match source 10.1.1.1 destination 8.8.8.8 destination-port 443 protocol 6"),
        C("show counter global", "Global packet counters (drops, etc.).", CliMode.Operational, "Troubleshoot", "show counter global filter delta yes severity drop"),
        C("ping host <host>", "ICMP reachability test.", CliMode.Operational, "Troubleshoot", "ping host 8.8.8.8"),
        C("traceroute host <host>", "Trace the path to a host.", CliMode.Operational, "Troubleshoot", "traceroute host 8.8.8.8"),
        C("show jobs all", "Commit and other job history/status.", CliMode.Operational, "Save & manage"),
        C("configure", "Enter configuration mode.", CliMode.Operational, "Config basics"),
        C("set deviceconfig system hostname <name>", "Set the device name.", CliMode.Config, "Config basics", "set deviceconfig system hostname PA-EDGE"),
        C("set network interface ethernet ...", "Configure an interface.", CliMode.Config, "Interfaces", "set network interface ethernet ethernet1/1 layer3 ip 10.0.0.1/24"),
        C("set address <name> ip-netmask <cidr>", "Create an address object.", CliMode.Config, "Objects", "set address WEB ip-netmask 10.1.1.10/32"),
        C("set rulebase security rules ...", "Create/modify a security rule.", CliMode.Config, "Security", "set rulebase security rules Allow-Web from trust to untrust application ssl service application-default action allow"),
        C("show | match <text>", "Filter configuration output.", CliMode.Config, "Config basics", "show | match hostname"),
        C("commit", "Apply the candidate configuration.", CliMode.Config, "Save & manage", "commit [description \"<text>\"]"),
        C("commit force", "Commit ignoring warnings.", CliMode.Config, "Save & manage"),
        C("exit", "Leave configuration mode / the session.", CliMode.Config, "Config basics"),
    ]);
}
