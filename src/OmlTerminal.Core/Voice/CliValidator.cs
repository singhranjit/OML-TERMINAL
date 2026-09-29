using System.Net;
using System.Text.RegularExpressions;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Checks the values inside a translated command, not just its first word: an IP address must have four octets
/// ≤ 255, a mask must be contiguous, "interface" needs an interface ID, VLANs are 1-4094. Speech recognizers are at
/// their weakest on numbers ("10.10.10.1255.255.2555…"), so anything that fails here is never sent hands-free.
/// </summary>
public static partial class CliValidator
{
    /// <summary>Null when the line is fine, otherwise a short reason to show the user.</summary>
    public static string? Problem(string line)
    {
        var l = line.Trim();

        if (IpAddressCmd().Match(l) is { Success: true } ip)
        {
            var rest = ip.Groups["rest"].Value.Trim();
            if (rest.Equals("dhcp", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Equals("secondary", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (parts.Length == 0) return "no address given";
            if (!IsIpv4(parts[0])) return $"\"{parts[0]}\" isn't a valid IP address";
            if (parts.Length < 2) return "no subnet mask given";
            if (!IsMask(parts[1])) return $"\"{parts[1]}\" isn't a valid subnet mask";
            if (parts.Length > 2) return "unexpected extra values after the mask";
            return null;
        }

        if (IpRouteCmd().Match(l) is { Success: true } route)
        {
            if (!IsIpv4(route.Groups["net"].Value) || !IsMask(route.Groups["mask"].Value) || !IsIpv4(route.Groups["nh"].Value))
                return "route has an invalid network, mask or next hop";
            return null;
        }
        if (l.StartsWith("ip route", StringComparison.OrdinalIgnoreCase)) return "route needs network, mask and next hop";

        if (InterfaceCmd().Match(l) is { Success: true } intf)
            return intf.Groups["id"].Value.Trim().Length == 0 ? "missing a value"
                 : InterfaceId().IsMatch(intf.Groups["id"].Value) ? null : $"\"{intf.Groups["id"].Value.Trim()}\" isn't an interface name";

        if (VlanCmd().Match(l) is { Success: true } vlan)
            return int.TryParse(vlan.Groups["id"].Value, out var v) && v is >= 1 and <= 4094 ? null : "VLAN ID must be 1-4094";

        if (NeedsArgument().IsMatch(l)) return "missing a value";
        return null;
    }

    public static bool IsIpv4(string s) =>
        s.Count(c => c == '.') == 3 && s.Split('.').All(o => o.Length is > 0 and <= 3 && int.TryParse(o, out var v) && v <= 255) && IPAddress.TryParse(s, out _);

    public static bool IsMask(string s)
    {
        if (!IsIpv4(s)) return false;
        try { Subnet.PrefixFromMask(IPAddress.Parse(s)); return true; }
        catch (FormatException) { return false; }
    }

    [GeneratedRegex(@"^ip\s+address(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex IpAddressCmd();

    [GeneratedRegex(@"^ip\s+route\s+(?<net>\S+)\s+(?<mask>\S+)\s+(?<nh>\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex IpRouteCmd();

    [GeneratedRegex(@"^(?:interface|int)(?<id>(?:\s+.*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceCmd();

    /// <summary>"GigabitEthernet1/0/2", "gi0/1", "Loopback10", "Vlan20", "Port-channel1", "Ethernet1/1.100", "range gi1/0/1 - 4".</summary>
    [GeneratedRegex(@"^\s*(?:range\s+)?[A-Za-z][A-Za-z-]*\s?\d+(?:/\d+){0,3}(?:\.\d+)?(?:\s*[-,]\s*\S+)*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceId();

    [GeneratedRegex(@"^(?:vlan|switchport\s+access\s+vlan)\s+(?<id>\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VlanCmd();

    [GeneratedRegex(@"^(?:vlan|hostname|description|name|ping|traceroute|switchport\s+access\s+vlan|switchport\s+trunk\s+allowed\s+vlan|ip\s+address|interface|int|router\s+ospf|router\s+bgp|network)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NeedsArgument();
}
