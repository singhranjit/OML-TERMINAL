using System.Text;

namespace OmlTerminal.Core.Firewall;

public enum PolicyAction { Allow, Deny, Drop, Reject }

/// <summary>One row of the policy sheet. Address/service cells are kept as raw tokens: each one is later resolved to
/// "any", a literal (IP, subnet, range, FQDN, tcp/443...) that gets its own object, or the name of an existing object.</summary>
public sealed class PolicyRule
{
    public int Line { get; init; }
    public string Name { get; set; } = "";
    public PolicyAction Action { get; init; } = PolicyAction.Allow;
    public string SourceInterface { get; init; } = "";
    public string DestinationInterface { get; init; } = "";
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> Destinations { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<string> Applications { get; init; } = [];
    public string Schedule { get; init; } = "";
    public bool Nat { get; init; }
    public bool Log { get; init; } = true;
    public string Comment { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Reads the policy sheet: a CSV (or tab-separated text pasted from Excel) with a header row. Column names are matched
/// loosely ("srcintf", "From", "Source Zone" all mean SourceInterface) so exports from other tools mostly just work.
/// </summary>
public static class PolicySheet
{
    public static readonly string[] Columns =
        ["Name", "Action", "SourceInterface", "DestinationInterface", "Source", "Destination", "Service", "Application", "Schedule", "NAT", "Log", "Comment", "Enabled"];

    public const string Sample = """
        # OML Terminal - Firewall Policy Builder sheet. One rule per row; lines starting with # are ignored.
        # Open in Excel, edit, and save as CSV. Several values in one cell: separate them with ; (semicolon).
        #
        # Name                  rule name (blank = Rule-<n>)
        # Action                allow | deny | drop | reject
        # SourceInterface       ingress interface / zone (FortiGate port1, Palo Alto trust, SRX zone, Sophos LAN, pfSense lan...)
        # DestinationInterface  egress interface / zone (ignored by Check Point and pfSense)
        # Source, Destination   IP, CIDR, IP MASK, start-end range, FQDN, *.wildcard, an existing object name, or any
        # Service               tcp/443, udp/53, tcp/8000-8010, tcp-udp/53, icmp, 443 (=tcp), an existing service name (HTTPS), or any
        # Application           Palo Alto App-ID (e.g. ssl;web-browsing) - other vendors ignore it. Blank = any
        # Schedule              FortiGate / Sophos schedule name (blank = always)
        # NAT                   yes | no - source NAT on the policy (FortiGate); other vendors configure NAT separately
        # Log, Enabled          yes | no
        Name,Action,SourceInterface,DestinationInterface,Source,Destination,Service,Application,Schedule,NAT,Log,Comment,Enabled
        Users-to-Internet,allow,port2,port1,10.10.0.0/16,any,tcp/80;tcp/443;udp/53,,always,yes,yes,Outbound browsing,yes
        Admins-to-Servers,allow,port2,port3,10.10.5.10;10.10.5.11,172.16.20.0/24,tcp/22;tcp/3389;tcp/443,,,no,yes,Admin jump hosts,yes
        DMZ-Web-Inbound,allow,port1,port3,any,172.16.20.80,tcp/443;tcp/8443,ssl;web-browsing,,no,yes,Public web,yes
        SaaS-Allow,allow,port2,port1,10.10.0.0/16,login.microsoftonline.com;*.office.com,tcp/443,,,yes,yes,M365,yes
        Block-Bad-Range,deny,any,any,any,203.0.113.10-203.0.113.50,any,,,no,yes,Threat intel block,yes
        """;

    public static (List<PolicyRule> Rules, List<ParseIssue> Issues) Parse(string text)
    {
        var rules = new List<PolicyRule>();
        var issues = new List<ParseIssue>();
        var lines = TextLines.Split(text.TrimStart('﻿'));
        Dictionary<string, int>? header = null;
        char delimiter = ',';

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            if (header is null)
            {
                delimiter = DetectDelimiter(raw);
                header = ParseHeader(SplitRow(raw, delimiter), issues, i + 1);
                if (header is null) return (rules, issues);
                continue;
            }

            var cells = SplitRow(raw, delimiter);
            string Cell(string column) => header.TryGetValue(column, out var idx) && idx < cells.Count ? cells[idx].Trim() : "";
            if (cells.All(c => c.Trim().Length == 0)) continue;

            if (!TryAction(Cell("Action"), out var action))
            {
                issues.Add(new ParseIssue(i + 1, Cell("Action"), "action must be allow, deny, drop or reject"));
                continue;
            }
            var rule = new PolicyRule
            {
                Line = i + 1,
                Name = Cell("Name"),
                Action = action,
                SourceInterface = Cell("SourceInterface"),
                DestinationInterface = Cell("DestinationInterface"),
                Sources = Values(Cell("Source")),
                Destinations = Values(Cell("Destination")),
                Services = Values(Cell("Service")),
                Applications = Values(Cell("Application")),
                Schedule = Cell("Schedule"),
                Nat = YesNo(Cell("NAT"), false),
                Log = YesNo(Cell("Log"), true),
                Comment = Cell("Comment"),
                Enabled = YesNo(Cell("Enabled"), true),
            };
            if (rule.Name.Length == 0) rule.Name = $"Rule-{rules.Count + 1}";
            rules.Add(rule);
        }
        if (header is null) issues.Add(new ParseIssue(0, "", "no header row found - start from the sample sheet"));
        return (rules, issues);
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "Name", ["rule"] = "Name", ["rulename"] = "Name", ["policyname"] = "Name", ["policy"] = "Name",
        ["action"] = "Action",
        ["sourceinterface"] = "SourceInterface", ["srcintf"] = "SourceInterface", ["from"] = "SourceInterface", ["fromzone"] = "SourceInterface",
        ["sourcezone"] = "SourceInterface", ["srczone"] = "SourceInterface", ["ingress"] = "SourceInterface", ["interface"] = "SourceInterface",
        ["destinationinterface"] = "DestinationInterface", ["dstintf"] = "DestinationInterface", ["to"] = "DestinationInterface",
        ["tozone"] = "DestinationInterface", ["destinationzone"] = "DestinationInterface", ["dstzone"] = "DestinationInterface", ["egress"] = "DestinationInterface",
        ["source"] = "Source", ["src"] = "Source", ["srcaddr"] = "Source", ["sourceaddress"] = "Source", ["sourceip"] = "Source",
        ["destination"] = "Destination", ["dst"] = "Destination", ["dstaddr"] = "Destination", ["destinationaddress"] = "Destination", ["destinationip"] = "Destination",
        ["service"] = "Service", ["services"] = "Service", ["port"] = "Service", ["ports"] = "Service", ["portnumber"] = "Service",
        ["application"] = "Application", ["applications"] = "Application", ["app"] = "Application", ["appid"] = "Application",
        ["schedule"] = "Schedule", ["nat"] = "NAT", ["log"] = "Log", ["logging"] = "Log",
        ["comment"] = "Comment", ["comments"] = "Comment", ["description"] = "Comment", ["remark"] = "Comment",
        ["enabled"] = "Enabled", ["status"] = "Enabled", ["enable"] = "Enabled",
    };

    private static Dictionary<string, int>? ParseHeader(List<string> cells, List<ParseIssue> issues, int line)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cells.Count; i++)
        {
            var key = new string(cells[i].Where(char.IsLetterOrDigit).ToArray());
            if (Aliases.TryGetValue(key, out var column) && !map.ContainsKey(column)) map[column] = i;
        }
        var missing = new[] { "Source", "Destination", "Service" }.Where(c => !map.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            issues.Add(new ParseIssue(line, string.Join(",", cells), $"header is missing column(s): {string.Join(", ", missing)}"));
            return null;
        }
        return map;
    }

    /// <summary>Tab (pasted from Excel), comma, or semicolon - Excel writes ';' in locales that use ',' as the decimal
    /// separator. Cells stay free to use ';' between values because Excel quotes any cell containing the delimiter.</summary>
    public static char DetectDelimiter(string headerLine)
    {
        if (headerLine.Contains('\t')) return '\t';
        return headerLine.Count(c => c == ';') > headerLine.Count(c => c == ',') ? ';' : ',';
    }

    /// <summary>RFC 4180-ish: quoted fields may contain the delimiter and doubled quotes.</summary>
    public static List<string> SplitRow(string line, char delimiter)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    /// <summary>A cell's values: ';', '|' or newlines separate them (never ',' - that's the column separator).</summary>
    public static IReadOnlyList<string> Values(string cell) =>
        cell.Split([';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryAction(string s, out PolicyAction action)
    {
        action = s.Trim().ToLowerInvariant() switch
        {
            "" or "allow" or "accept" or "permit" or "pass" => PolicyAction.Allow,
            "deny" or "block" => PolicyAction.Deny,
            "drop" or "discard" => PolicyAction.Drop,
            "reject" or "reset" => PolicyAction.Reject,
            _ => (PolicyAction)(-1),
        };
        return (int)action >= 0;
    }

    private static bool YesNo(string s, bool fallback) => s.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "enable" or "enabled" or "on" => true,
        "no" or "n" or "false" or "0" or "disable" or "disabled" or "off" => false,
        _ => fallback,
    };

    public static bool IsAny(string token) => token.Trim().ToLowerInvariant() is "any" or "all" or "*" or "any4" or "any6";
}
