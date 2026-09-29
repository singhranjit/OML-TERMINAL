using System.Globalization;
using System.Text;

namespace OmlTerminal.Core.Monitoring;

/// <summary>One poll of a Linux/Unix host's load, memory, disk and uptime. Any field left null means it couldn't
/// be parsed from that host's output (e.g. network gear that doesn't understand these commands).</summary>
public sealed record HostStatsSample(
    double? Load1, double? Load5, double? Load15,
    long? MemTotalMb, long? MemUsedMb,
    long? DiskTotalMb, long? DiskUsedMb,
    string? UptimeText)
{
    public double? MemUsedPercent => MemTotalMb is > 0 && MemUsedMb is not null ? 100.0 * MemUsedMb.Value / MemTotalMb.Value : null;
    public double? DiskUsedPercent => DiskTotalMb is > 0 && DiskUsedMb is not null ? 100.0 * DiskUsedMb.Value / DiskTotalMb.Value : null;

    /// <summary>True once at least one field was parsed - false means the host's output didn't look like Linux/Unix at all.</summary>
    public bool AnyData => Load1 is not null || MemTotalMb is not null || DiskTotalMb is not null || UptimeText is not null;
}

/// <summary>Builds the single SSH exec command that samples a host and parses its output. Runs over a side-channel
/// connection (see <see cref="Ssh.SshConnector"/>), not the interactive shell, so it never interleaves with what the
/// user is typing.</summary>
public static class HostStats
{
    private const string MemMarker = "::OMLMEM::";
    private const string DiskMarker = "::OMLDISK::";
    private const string UptimeMarker = "::OMLUPTIME::";

    public const string Command =
        $"sh -c 'cat /proc/loadavg 2>/dev/null; echo {MemMarker}; free -m 2>/dev/null; echo {DiskMarker}; df -m / 2>/dev/null; echo {UptimeMarker}; uptime 2>/dev/null'";

    public static HostStatsSample Parse(string output)
    {
        var sections = SplitSections(output);

        double? l1 = null, l5 = null, l15 = null;
        var loadParts = sections.GetValueOrDefault("", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (loadParts.Length >= 3 && TryDouble(loadParts[0], out var a) && TryDouble(loadParts[1], out var b) && TryDouble(loadParts[2], out var c))
        {
            l1 = a; l5 = b; l15 = c;
        }

        long? memTotal = null, memUsed = null;
        var memRow = sections.GetValueOrDefault(MemMarker, "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Mem:"));
        if (memRow is not null)
        {
            var cols = memRow.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 3 && long.TryParse(cols[1], out var t) && long.TryParse(cols[2], out var u)) { memTotal = t; memUsed = u; }
        }

        long? diskTotal = null, diskUsed = null;
        var diskLines = sections.GetValueOrDefault(DiskMarker, "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (diskLines.Count > 1)
        {
            // A long filesystem name (LVM path, overlay mount, ...) makes "df" wrap the data row onto a second
            // line, with the size/used columns starting there instead of after the name. Joining every line after
            // the header before splitting on whitespace reassembles the columns correctly either way.
            var cols = string.Join(' ', diskLines.Skip(1)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 3 && long.TryParse(cols[1], out var dt) && long.TryParse(cols[2], out var du)) { diskTotal = dt; diskUsed = du; }
        }

        var uptimeLine = sections.GetValueOrDefault(UptimeMarker, "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();

        return new HostStatsSample(l1, l5, l15, memTotal, memUsed, diskTotal, diskUsed, uptimeLine);
    }

    private static bool TryDouble(string s, out double value) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Splits on lines that are exactly one of the echoed markers; "" is the section before the first marker.</summary>
    private static Dictionary<string, string> SplitSections(string output)
    {
        var result = new Dictionary<string, string>();
        var current = "";
        var sb = new StringBuilder();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line is MemMarker or DiskMarker or UptimeMarker)
            {
                result[current] = sb.ToString();
                sb.Clear();
                current = line;
                continue;
            }
            sb.Append(raw).Append('\n');
        }
        result[current] = sb.ToString();
        return result;
    }
}
