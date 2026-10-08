using OmlTerminal.Core.Backup;

namespace OmlTerminal.Core.ChangeGuard;

/// <summary>How a check's output is understood when comparing before and after.</summary>
public enum CheckKind { Interfaces, Routes, BgpPeers, OspfNeighbors, InterfaceErrors, CdpNeighbors, Hsrp, MacCount, ArpCount, Raw }

public sealed record GuardCheck(string Title, string Command, CheckKind Kind);

/// <summary>A vendor's set of read-only checks. Every command is a "show"/"get" - Change Guard never changes anything.</summary>
public sealed record GuardProfile(string Name, BackupMode Mode, IReadOnlyList<string> Prep, IReadOnlyList<GuardCheck> Checks)
{
    public override string ToString() => Name;
}

public static class GuardProfiles
{
    public const string CustomName = "Custom";

    public static readonly IReadOnlyList<GuardProfile> All =
    [
        new("Cisco IOS / IOS-XE", BackupMode.Shell, ["terminal length 0"],
        [
            new("Interfaces", "show ip interface brief", CheckKind.Interfaces),
            new("Routing table", "show ip route", CheckKind.Routes),
            new("BGP peers", "show ip bgp summary", CheckKind.BgpPeers),
            new("OSPF neighbors", "show ip ospf neighbor", CheckKind.OspfNeighbors),
            new("Interface errors", "show interfaces", CheckKind.InterfaceErrors),
            new("CDP neighbors", "show cdp neighbors", CheckKind.CdpNeighbors),
            new("HSRP", "show standby brief", CheckKind.Hsrp),
            new("MAC table", "show mac address-table", CheckKind.MacCount),
            new("ARP table", "show ip arp", CheckKind.ArpCount),
        ]),
        new("Cisco NX-OS", BackupMode.Exec, [],
        [
            new("Interfaces", "show interface status", CheckKind.Interfaces),
            new("Routing table", "show ip route", CheckKind.Routes),
            new("BGP peers", "show ip bgp summary", CheckKind.BgpPeers),
            new("OSPF neighbors", "show ip ospf neighbors", CheckKind.OspfNeighbors),
            new("CDP neighbors", "show cdp neighbors", CheckKind.CdpNeighbors),
            new("MAC table", "show mac address-table", CheckKind.MacCount),
            new("ARP table", "show ip arp", CheckKind.ArpCount),
        ]),
        new("Cisco ASA", BackupMode.Shell, ["terminal pager 0"],
        [
            new("Interfaces", "show interface ip brief", CheckKind.Interfaces),
            new("Routing table", "show route", CheckKind.Routes),
            new("BGP peers", "show bgp summary", CheckKind.BgpPeers),
            new("OSPF neighbors", "show ospf neighbor", CheckKind.OspfNeighbors),
            new("ARP table", "show arp", CheckKind.ArpCount),
            new("Failover", "show failover state", CheckKind.Raw),
        ]),
        new("Juniper Junos", BackupMode.Exec, [],
        [
            new("Interfaces", "show interfaces terse | no-more", CheckKind.Interfaces),
            new("Routing table", "show route terse | no-more", CheckKind.Routes),
            new("BGP peers", "show bgp summary | no-more", CheckKind.BgpPeers),
            new("OSPF neighbors", "show ospf neighbor | no-more", CheckKind.OspfNeighbors),
            new("ARP table", "show arp no-resolve | no-more", CheckKind.ArpCount),
            new("LLDP neighbors", "show lldp neighbors | no-more", CheckKind.Raw),
        ]),
        new("FortiGate", BackupMode.Exec, [],
        [
            new("Interfaces", "get system interface physical", CheckKind.Interfaces),
            new("Routing table", "get router info routing-table all", CheckKind.Routes),
            new("BGP peers", "get router info bgp summary", CheckKind.BgpPeers),
            new("OSPF neighbors", "get router info ospf neighbor", CheckKind.OspfNeighbors),
            new("ARP table", "get system arp", CheckKind.ArpCount),
        ]),
        new("Linux", BackupMode.Exec, [],
        [
            new("Interfaces", "ip -br link", CheckKind.Interfaces),
            new("Routing table", "ip route", CheckKind.Routes),
            new("Neighbors (ARP)", "ip neigh", CheckKind.ArpCount),
        ]),
    ];

    public static GuardProfile? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>User-supplied commands, compared line by line (no semantic understanding).</summary>
    public static GuardProfile Custom(BackupMode mode, string commandsText) =>
        new(CustomName, mode, [], TextLines.Split(commandsText).Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(c => new GuardCheck(c, c, CheckKind.Raw)).ToList());
}
