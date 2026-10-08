using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Parsing;

/// <summary>Turns common "show" command output into a <see cref="ShowTable"/>. Known commands are recognised by their
/// header line (so pasted output can include the prompt, banners and footers); anything else that looks like a
/// column-aligned table falls back to an auto-detected fixed-width layout.</summary>
public static partial class ShowTableParser
{
    public static ShowTable? Parse(string text)
    {
        var lines = TextLines.Split(text).Select(l => l.Replace("\t", "    ").TrimEnd()).ToArray();
        foreach (var template in Templates)
        {
            var table = template(lines);
            if (table is { Rows.Count: > 0 }) return table;
        }
        return null;
    }

    private static readonly Func<string[], ShowTable?>[] Templates =
    [
        IosIpInterfaceBrief, NxosIpInterfaceBrief, InterfacesStatus, VlanBrief, CdpNeighbors, LldpNeighbors,
        BgpSummary, JunosBgpSummary, OspfNeighbors, JunosOspfNeighbors, StandbyBrief, JunosInterfacesTerse,
        FortiGateInterfaces, l => RouteTableParser.ToTable(string.Join('\n', l)), MacTable, ArpTable, LinuxBrief, Generic,
    ];

    // ---------- helpers ----------

    private static int FindLine(string[] lines, Regex r)
    {
        for (int i = 0; i < lines.Length; i++) if (r.IsMatch(lines[i])) return i;
        return -1;
    }

    private static bool IsSeparator(string l)
    {
        var t = l.Trim();
        return t.Length > 0 && t.All(c => c is '-' or '=' or '+' or ' ' or '_');
    }

    [GeneratedRegex(@"^[A-Za-z0-9][\w.\-()/@:~]{0,62}\s?[#>$](?:\s|$)")]
    private static partial Regex PromptStart();

    private static bool IsPromptLine(string l) => PromptStart().IsMatch(l);

    /// <summary>Non-blank, non-separator lines after a header, stopping at the first blank line once data has started
    /// (footers like "Total entries displayed" sit after one) or at the next CLI prompt.</summary>
    private static IEnumerable<string> DataLines(string[] lines, int start)
    {
        bool any = false;
        for (int i = start; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim().Length == 0) { if (any) yield break; continue; }
            if (IsPromptLine(l)) yield break;
            if (IsSeparator(l)) continue;
            any = true;
            yield return l;
        }
    }

    private static string[] Tokens(string l) => l.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{1,3}){3}$")]
    private static partial Regex Ipv4Token();

    [GeneratedRegex(@"^(?:[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}|[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5})$")]
    private static partial Regex MacToken();

    [GeneratedRegex(@"^\d+$|^\d{1,2}:\d{2}(:\d{2})?$|^(?:\d+[ywdhms])+\d*$|^-$")]
    private static partial Regex AgeToken();

    public static bool IsIpv4(string s) => Ipv4Token().IsMatch(s);
    public static bool IsMac(string s) => MacToken().IsMatch(s);

    private static ShowTable? RegexRows(string title, string[] lines, Regex header, Regex row, string[] columns, Func<Match, string[]> map)
    {
        int h = FindLine(lines, header);
        if (h < 0) return null;
        var rows = DataLines(lines, h + 1).Select(l => row.Match(l)).Where(m => m.Success).Select(m => (IReadOnlyList<string>)map(m)).ToList();
        return new ShowTable(title, columns, rows);
    }

    /// <summary>Where each column title starts in the header. Each name may list alternatives ("Holdtme|Hldtme").</summary>
    private static int[]? Positions(string header, string[] names)
    {
        var pos = new int[names.Length];
        int from = 0;
        for (int i = 0; i < names.Length; i++)
        {
            int best = -1, len = 0;
            foreach (var alt in names[i].Split('|'))
            {
                int p = header.IndexOf(alt, from, StringComparison.OrdinalIgnoreCase);
                if (p >= 0 && (best < 0 || p < best)) { best = p; len = alt.Length; }
            }
            if (best < 0) return null;
            pos[i] = best;
            from = best + len;
        }
        return pos;
    }

    /// <summary>Cuts a row at the header's column starts. A value that starts a little before its header (right-aligned
    /// numbers, "a-1000" under "Speed") would be split mid-word, so the boundary moves left to keep the word whole.</summary>
    internal static string[] Slice(string line, int[] starts)
    {
        var bounds = new int[starts.Length];
        for (int i = 1; i < starts.Length; i++)
        {
            int p = Math.Min(starts[i], line.Length);
            while (p > bounds[i - 1] && p < line.Length && line[p - 1] != ' ' && line[p] != ' ') p--;
            bounds[i] = Math.Max(p, bounds[i - 1]);
        }
        var cells = new string[starts.Length];
        for (int i = 0; i < starts.Length; i++)
        {
            int end = i + 1 < starts.Length ? bounds[i + 1] : line.Length;
            cells[i] = line[bounds[i]..end].Trim();
        }
        return cells;
    }

    private static ShowTable? FixedWidth(string title, string[] lines, Regex header, string[] names, bool joinWrappedFirstColumn)
    {
        int h = FindLine(lines, header);
        if (h < 0) return null;
        var starts = Positions(lines[h], names);
        if (starts is null) return null;
        var rows = new List<IReadOnlyList<string>>();
        string? wrapped = null;
        foreach (var l in DataLines(lines, h + 1))
        {
            // CDP/LLDP print a long device name alone on its line, with the rest of the entry on the next one.
            if (joinWrappedFirstColumn && !char.IsWhiteSpace(l[0]) && Tokens(l).Length == 1)
            {
                if (wrapped is not null) rows.Add(Blank(wrapped, names.Length));
                wrapped = l.Trim();
                continue;
            }
            var cells = Slice(l, starts);
            if (wrapped is not null)
            {
                if (cells[0].Length == 0) cells[0] = wrapped;
                else rows.Add(Blank(wrapped, names.Length));
                wrapped = null;
            }
            if (cells.Any(c => c.Length > 0)) rows.Add(cells);
        }
        if (wrapped is not null) rows.Add(Blank(wrapped, names.Length));
        return new ShowTable(title, names.Select(n => n.Split('|')[0]).ToList(), rows);

        static string[] Blank(string first, int n) { var a = new string[n]; Array.Fill(a, ""); a[0] = first; return a; }
    }

    // ---------- Cisco interfaces ----------

    [GeneratedRegex(@"^\s*Interface\s+IP-Address\s+OK\?\s+Method\s+Status\s+Protocol\s*$")]
    private static partial Regex IosIpBriefHeader();

    [GeneratedRegex(@"^(?<if>\S+)\s+(?<ip>\S+)\s+(?<ok>YES|NO)\s+(?<method>\S+)\s+(?<status>.+?)\s+(?<proto>\S+)\s*$")]
    private static partial Regex IosIpBriefRow();

    private static ShowTable? IosIpInterfaceBrief(string[] lines) =>
        RegexRows("IP interface brief", lines, IosIpBriefHeader(), IosIpBriefRow(),
            ["Interface", "IP-Address", "OK?", "Method", "Status", "Protocol"],
            m => [m.Groups["if"].Value, m.Groups["ip"].Value, m.Groups["ok"].Value, m.Groups["method"].Value, m.Groups["status"].Value, m.Groups["proto"].Value]);

    [GeneratedRegex(@"^\s*Interface\s+IP Address\s+Interface Status\s*$")]
    private static partial Regex NxosIpBriefHeader();

    [GeneratedRegex(@"^(?<if>\S+)\s+(?<ip>\S+)\s+(?<st>\S+)\s*$")]
    private static partial Regex NxosIpBriefRow();

    private static ShowTable? NxosIpInterfaceBrief(string[] lines) =>
        RegexRows("IP interface brief", lines, NxosIpBriefHeader(), NxosIpBriefRow(),
            ["Interface", "IP Address", "Status"],
            m => [m.Groups["if"].Value, m.Groups["ip"].Value, m.Groups["st"].Value]);

    [GeneratedRegex(@"^\s*Port\s+Name\s+Status\s+Vlan\s+Duplex\s+Speed\s+Type\s*$")]
    private static partial Regex IntStatusHeader();

    private static ShowTable? InterfacesStatus(string[] lines) =>
        FixedWidth("Interface status", lines, IntStatusHeader(), ["Port", "Name", "Status", "Vlan", "Duplex", "Speed", "Type"], false);

    [GeneratedRegex(@"^\s*VLAN\s+Name\s+Status\s+Ports\s*$")]
    private static partial Regex VlanHeader();

    private static ShowTable? VlanBrief(string[] lines)
    {
        int h = FindLine(lines, VlanHeader());
        if (h < 0) return null;
        var starts = Positions(lines[h], ["VLAN", "Name", "Status", "Ports"]);
        if (starts is null) return null;
        var rows = new List<string[]>();
        foreach (var l in DataLines(lines, h + 1))
        {
            if (char.IsDigit(l[0])) rows.Add(Slice(l, starts));
            else if (char.IsWhiteSpace(l[0]) && rows.Count > 0)
            {
                var more = l.Trim();
                rows[^1][3] = rows[^1][3].Length == 0 ? more : $"{rows[^1][3].TrimEnd(',')}, {more}";
            }
        }
        return new ShowTable("VLANs", ["VLAN", "Name", "Status", "Ports"], rows.Cast<IReadOnlyList<string>>().ToList());
    }

    [GeneratedRegex(@"^\s*Device[ -]ID\s+Local Intrfce\s+(?:Holdtme|Hldtme)\s+Capability\s+Platform\s+Port ID\s*$")]
    private static partial Regex CdpHeader();

    private static ShowTable? CdpNeighbors(string[] lines) =>
        FixedWidth("CDP neighbors", lines, CdpHeader(), ["Device ID|Device-ID", "Local Intrfce", "Holdtme|Hldtme", "Capability", "Platform", "Port ID"], true);

    [GeneratedRegex(@"^\s*Device ID\s+Local Intf\s+Hold-time\s+Capability\s+Port ID\s*$")]
    private static partial Regex LldpHeader();

    private static ShowTable? LldpNeighbors(string[] lines) =>
        FixedWidth("LLDP neighbors", lines, LldpHeader(), ["Device ID", "Local Intf", "Hold-time", "Capability", "Port ID"], true);

    // ---------- routing protocols ----------

    [GeneratedRegex(@"^\s*Neighbor\s+V\s+AS\s+MsgRcvd\s+MsgSent\s+TblVer\s+InQ\s+OutQ\s+Up/Down\s+State/PfxRcd\s*$")]
    private static partial Regex BgpHeader();

    /// <summary>Cisco IOS/NX-OS/ASA and FortiOS share this layout. A long (IPv6) neighbor wraps onto its own line.</summary>
    private static ShowTable? BgpSummary(string[] lines)
    {
        int h = FindLine(lines, BgpHeader());
        if (h < 0) return null;
        var rows = new List<IReadOnlyList<string>>();
        string? pending = null;
        foreach (var l in DataLines(lines, h + 1))
        {
            var t = Tokens(l);
            if (t.Length == 1 && !char.IsWhiteSpace(l[0])) { pending = t[0]; continue; }
            if (pending is not null && char.IsWhiteSpace(l[0])) t = [pending, .. t];
            pending = null;
            if (t.Length < 10 || !t[1].All(char.IsDigit)) continue;
            rows.Add([t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8], string.Join(" ", t[9..])]);
        }
        return new ShowTable("BGP summary", ["Neighbor", "V", "AS", "MsgRcvd", "MsgSent", "TblVer", "InQ", "OutQ", "Up/Down", "State/PfxRcd"], rows);
    }

    [GeneratedRegex(@"^\s*Peer\s+AS\s+InPkt\s+OutPkt\s+OutQ\s+Flaps\s+Last Up/Dwn\s+State")]
    private static partial Regex JunosBgpHeader();

    [GeneratedRegex(@"^\d+/(?<rcv>\d+)/\d+/\d+$")]
    private static partial Regex JunosRibCounts();

    /// <summary>Junos prints "Active/Received/Accepted/Damped" in place of the state for single-RIB peers, or "Establ"
    /// followed by one "inet.0: a/r/a/d" line per RIB. Either way the State/PfxRcd column gets the received count,
    /// matching the Cisco convention (a number means Established).</summary>
    private static ShowTable? JunosBgpSummary(string[] lines)
    {
        int h = FindLine(lines, JunosBgpHeader());
        if (h < 0) return null;
        var rows = new List<string[]>();
        foreach (var l in DataLines(lines, h + 1))
        {
            var t = Tokens(l);
            if (char.IsWhiteSpace(l[0]))
            {
                if (rows.Count > 0 && rows[^1][7] == "Establ" && t.Length == 2 && JunosRibCounts().Match(t[1]) is { Success: true } rib)
                    rows[^1][7] = rib.Groups["rcv"].Value;
                continue;
            }
            if (t.Length < 8 || (!IsIpv4(t[0]) && !t[0].Contains(':')) || !t[1].All(char.IsDigit)) continue;
            // Several RIBs print several a/r/a/d groups on the line; the first is inet.0.
            int ribAt = Array.FindIndex(t, 7, x => JunosRibCounts().IsMatch(x));
            int stateAt = ribAt >= 0 ? ribAt : t.Length - 1;
            var state = ribAt >= 0 ? JunosRibCounts().Match(t[ribAt]).Groups["rcv"].Value : t[^1];
            rows.Add([t[0], t[1], t[2], t[3], t[4], t[5], string.Join(" ", t[6..stateAt]), state]);
        }
        return new ShowTable("BGP summary", ["Neighbor", "AS", "InPkt", "OutPkt", "OutQ", "Flaps", "Up/Down", "State/PfxRcd"],
            rows.Cast<IReadOnlyList<string>>().ToList());
    }

    [GeneratedRegex(@"^\s*Neighbor ID\s+Pri\s+State\s+(?:Dead Time|Up Time)\s+Address\s+Interface\s*$")]
    private static partial Regex OspfHeader();

    [GeneratedRegex(@"^\s*(?<id>\d{1,3}(?:\.\d{1,3}){3})\s+(?<pri>\d+)\s+(?<state>[A-Za-z]+/\s*[A-Za-z\-]+|[A-Za-z\-]+)\s+(?<time>\S+)\s+(?<addr>\S+)\s+(?<if>\S+)\s*$")]
    private static partial Regex OspfRow();

    private static readonly string[] OspfColumns = ["Neighbor ID", "Pri", "State", "Time", "Address", "Interface"];

    private static ShowTable? OspfNeighbors(string[] lines) =>
        RegexRows("OSPF neighbors", lines, OspfHeader(), OspfRow(), OspfColumns,
            m => [m.Groups["id"].Value, m.Groups["pri"].Value, Regex.Replace(m.Groups["state"].Value, @"\s+", ""),
                  m.Groups["time"].Value, m.Groups["addr"].Value, m.Groups["if"].Value]);

    [GeneratedRegex(@"^\s*Address\s+Interface\s+State\s+ID\s+Pri\s+Dead\s*$")]
    private static partial Regex JunosOspfHeader();

    [GeneratedRegex(@"^(?<addr>\S+)\s+(?<if>\S+)\s+(?<state>\S+)\s+(?<id>\S+)\s+(?<pri>\d+)\s+(?<dead>\d+)\s*$")]
    private static partial Regex JunosOspfRow();

    private static ShowTable? JunosOspfNeighbors(string[] lines) =>
        RegexRows("OSPF neighbors", lines, JunosOspfHeader(), JunosOspfRow(), OspfColumns,
            m => [m.Groups["id"].Value, m.Groups["pri"].Value, m.Groups["state"].Value, m.Groups["dead"].Value, m.Groups["addr"].Value, m.Groups["if"].Value]);

    [GeneratedRegex(@"^\s*Interface\s+Grp\s+Pri\s+P\s+State\s+Active\s+Standby\s+Virtual IP\s*$")]
    private static partial Regex StandbyHeader();

    /// <summary>The "P" (preempt) column is blank unless preemption is on, so rows have 7 or 8 tokens.</summary>
    private static ShowTable? StandbyBrief(string[] lines)
    {
        int h = FindLine(lines, StandbyHeader());
        if (h < 0) return null;
        var rows = new List<IReadOnlyList<string>>();
        foreach (var l in DataLines(lines, h + 1))
        {
            var t = Tokens(l);
            if (t.Length == 8) rows.Add(t);
            else if (t.Length == 7) rows.Add([t[0], t[1], t[2], "", t[3], t[4], t[5], t[6]]);
        }
        return new ShowTable("HSRP", ["Interface", "Grp", "Pri", "P", "State", "Active", "Standby", "Virtual IP"], rows);
    }

    // ---------- other vendors ----------

    [GeneratedRegex(@"^\s*Interface\s+Admin\s+Link\s+Proto\s+Local\s+Remote\s*$")]
    private static partial Regex JunosTerseHeader();

    private static ShowTable? JunosInterfacesTerse(string[] lines)
    {
        int h = FindLine(lines, JunosTerseHeader());
        if (h < 0) return null;
        var rows = new List<string[]>();
        foreach (var l in DataLines(lines, h + 1))
        {
            var t = Tokens(l);
            if (!char.IsWhiteSpace(l[0]) && t.Length >= 3)
                rows.Add([t[0], t[1], t[2], t.Length > 3 ? t[3] : "", t.Length > 4 ? t[4] : "", t.Length > 5 ? t[5] : ""]);
            else if (char.IsWhiteSpace(l[0]) && rows.Count > 0 && t.Length is 1 or 2)
            {
                var row = rows[^1];
                if (t.Length == 2) row[3] = row[3].Length == 0 ? t[0] : $"{row[3]}, {t[0]}";
                row[4] = row[4].Length == 0 ? t[^1] : $"{row[4]}, {t[^1]}";
            }
        }
        return new ShowTable("Interfaces", ["Interface", "Admin", "Link", "Proto", "Local", "Remote"], rows.Cast<IReadOnlyList<string>>().ToList());
    }

    [GeneratedRegex(@"^\s*==\s?\[(?<name>[^\]]+)\]\s*$")]
    private static partial Regex FortiInterfaceStart();

    [GeneratedRegex(@"^\s*(?<key>mode|ip|status|speed)\s*:\s*(?<value>.*)$")]
    private static partial Regex FortiInterfaceField();

    /// <summary>FortiOS "get system interface physical": "==[port1]" blocks of "key: value" lines.</summary>
    private static ShowTable? FortiGateInterfaces(string[] lines)
    {
        var rows = new List<string[]>();
        string[]? current = null;
        foreach (var l in lines)
        {
            if (FortiInterfaceStart().Match(l) is { Success: true } s) { current = [s.Groups["name"].Value, "", "", "", ""]; rows.Add(current); continue; }
            if (current is null || FortiInterfaceField().Match(l) is not { Success: true } f) continue;
            int col = f.Groups["key"].Value switch { "mode" => 1, "ip" => 2, "status" => 3, _ => 4 };
            current[col] = f.Groups["value"].Value.Trim();
        }
        var real = rows.Where(r => r[1].Length + r[2].Length + r[3].Length > 0).Cast<IReadOnlyList<string>>().ToList();
        return new ShowTable("Interfaces", ["Interface", "Mode", "IP", "Status", "Speed"], real);
    }

    [GeneratedRegex(@"(?i)^\s*(?:\*\s+)?vlan\s+mac address\s+type\b")]
    private static partial Regex MacHeader();

    /// <summary>IOS, NX-OS and most switch MAC tables: VLAN just before the MAC, type just after, port last.</summary>
    private static ShowTable? MacTable(string[] lines)
    {
        int h = FindLine(lines, MacHeader());
        var rows = new List<IReadOnlyList<string>>();
        foreach (var l in h >= 0 ? DataLines(lines, h + 1) : lines)
        {
            var t = Tokens(l);
            int mac = Array.FindIndex(t, IsMac);
            if (mac < 0 || t.Any(IsIpv4)) continue;
            var vlan = mac > 0 ? t[mac - 1] : "";
            if (h < 0 && !vlan.All(char.IsDigit)) continue;
            rows.Add([vlan, t[mac], mac + 1 < t.Length ? t[mac + 1] : "", t.Length - 1 > mac + 1 ? t[^1] : ""]);
        }
        if (h < 0 && rows.Count < 3) return null;
        return new ShowTable("MAC address table", ["VLAN", "MAC Address", "Type", "Port"], rows);
    }

    private static readonly HashSet<string> ArpNoise = new(StringComparer.OrdinalIgnoreCase)
        { "ARPA", "none", "permanent", "static", "dynamic", "incomplete", "Internet", "REACHABLE", "STALE", "DELAY", "PROBE", "lladdr", "dev", "alias" };

    /// <summary>Any ARP/neighbor table: rows holding an IPv4 address and a MAC (IOS, ASA, NX-OS, FortiOS, Junos, Linux).</summary>
    private static ShowTable? ArpTable(string[] lines)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var l in lines)
        {
            if (IsPromptLine(l)) continue;
            var t = Tokens(l);
            int ip = Array.FindIndex(t, IsIpv4), mac = Array.FindIndex(t, IsMac);
            if (ip < 0 || mac < 0) continue;
            int dev = Array.IndexOf(t, "dev");
            string iface = dev >= 0 && dev + 1 < t.Length ? t[dev + 1]
                : t.Where((x, i) => i != ip && i != mac && !AgeToken().IsMatch(x) && !ArpNoise.Contains(x) && !IsIpv4(x)).LastOrDefault() ?? "";
            var age = ip + 1 < t.Length && ip + 1 != mac && AgeToken().IsMatch(t[ip + 1]) ? t[ip + 1] : "";
            rows.Add([t[ip], t[mac], iface, age]);
        }
        return new ShowTable("ARP table", ["IP Address", "MAC Address", "Interface", "Age"], rows);
    }

    [GeneratedRegex(@"^(?<if>\S+)\s+(?<state>UP|DOWN|UNKNOWN|LOWERLAYERDOWN|DORMANT|NOTPRESENT)\b\s*(?<rest>.*)$")]
    private static partial Regex LinuxBriefRow();

    /// <summary>Linux "ip -br link" / "ip -br addr" - no header, so every data line has to fit.</summary>
    private static ShowTable? LinuxBrief(string[] lines)
    {
        var data = lines.Where(l => l.Trim().Length > 0 && !IsPromptLine(l)).ToList();
        var rows = data.Select(l => LinuxBriefRow().Match(l)).Where(m => m.Success)
            .Select(m => (IReadOnlyList<string>)[m.Groups["if"].Value, m.Groups["state"].Value, m.Groups["rest"].Value.Trim()]).ToList();
        if (rows.Count < 2 || rows.Count < data.Count * 0.8) return null;
        return new ShowTable("Interfaces", ["Interface", "State", "Details"], rows);
    }

    [GeneratedRegex(@"\S+(?: \S+)*")]
    private static partial Regex HeaderCell();

    /// <summary>Any other column-aligned table: the first line whose titles are separated by 2+ spaces, followed by rows.</summary>
    private static ShowTable? Generic(string[] lines)
    {
        for (int h = 0; h < Math.Min(lines.Length, 40); h++)
        {
            var cells = HeaderCell().Matches(lines[h]).ToList();
            if (cells.Count < 3 || cells.Any(c => !char.IsLetter(c.Value[0]) || c.Value.EndsWith(':') || c.Value.All(char.IsDigit))) continue;
            if (IsPromptLine(lines[h])) continue;
            var starts = cells.Select(c => c.Index).ToArray();
            var rows = new List<IReadOnlyList<string>>();
            foreach (var l in DataLines(lines, h + 1))
            {
                if (l.Length < starts[1]) continue;
                rows.Add(Slice(l, starts));
            }
            if (rows.Count >= 2) return new ShowTable("Table (auto-detected)", cells.Select(c => c.Value).ToList(), rows);
        }
        return null;
    }

    // ---------- terminal scrollback ----------

    [GeneratedRegex(@"^(?<prompt>[A-Za-z0-9][\w.\-()/@:~]{0,62}\s?[#>$])\s?(?<cmd>\S.*)$")]
    private static partial Regex PromptWithCommand();

    /// <summary>From a terminal's scrollback, the most recent command typed at a CLI prompt and the output it produced
    /// (everything up to the next prompt). Command is null when no prompt line is found - then Output is the whole text.</summary>
    public static (string? Command, string Output) LastCommandOutput(string buffer)
    {
        var lines = TextLines.Split(buffer).Select(l => l.TrimEnd()).ToArray();
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (PromptWithCommand().Match(lines[i]) is not { Success: true } m) continue;
            int end = i + 1;
            while (end < lines.Length && !IsPromptLine(lines[end])) end++;
            if (end - i - 1 == 0) continue; // a command with no output yet (or the line being typed) - keep looking
            return (m.Groups["cmd"].Value.Trim(), string.Join('\n', lines[(i + 1)..end]));
        }
        return (null, buffer);
    }
}
