using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Terminal;

public sealed record HighlightRule(string Name, Regex Pattern, int Color, bool Bold = false);

/// <summary>
/// Colors keywords in terminal output (interface state, IPs, MACs...). Only text drawn in the default foreground color is touched, so
/// anything the remote program colored itself is left alone. Matches are found within one styled run.
/// </summary>
public sealed class HighlightRuleSet(IReadOnlyList<HighlightRule> rules)
{
    public IReadOnlyList<HighlightRule> Rules { get; } = rules;

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    public static HighlightRuleSet Default { get; } = new(
    [
        new("bad", new(@"\b(down|err-disabled|notconnect|administratively down|failed|failure|error|invalid|denied|unreachable|disabled)\b", Opts), 0xF87171, true),
        new("good", new(@"\b(up|connected|established|enabled|active|full)\b", Opts), 0x4ADE80),
        new("warn", new(@"\b(warning|shutdown|blocking|listening|learning|standby)\b", Opts), 0xFBBF24),
        new("ipv4", new(@"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b", Opts), 0xFB923C),
        new("mac", new(@"\b(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}\b|\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", Opts), 0xE879F9),
        new("interface", new(@"\b(?:Gi|Te|Fa|Hu|Fo|Eth|Po|Vlan|Lo|Tu)[A-Za-z]*\d+(?:/\d+)*(?:\.\d+)?\b", Opts), 0x38BDF8),
    ]);

    public TerminalRow Apply(TerminalRow row, int defaultForeground)
    {
        List<TextRun>? result = null;
        for (int i = 0; i < row.Runs.Count; i++)
        {
            var run = row.Runs[i];
            var pieces = run.Fg == defaultForeground && run.Text.Length == run.Cells ? Split(run) : null;
            if (pieces is null)
            {
                result?.Add(run);
                continue;
            }
            result ??= row.Runs.Take(i).ToList();
            result.AddRange(pieces);
        }
        return result is null ? row : new TerminalRow(result);
    }

    private List<TextRun>? Split(TextRun run)
    {
        var claimed = new List<(int Start, int Length, HighlightRule Rule)>();
        foreach (var rule in Rules)
            foreach (Match m in rule.Pattern.Matches(run.Text))
            {
                if (m.Length == 0) continue;
                // Earlier rules win where matches overlap.
                if (claimed.Any(c => m.Index < c.Start + c.Length && c.Start < m.Index + m.Length)) continue;
                claimed.Add((m.Index, m.Length, rule));
            }
        if (claimed.Count == 0) return null;

        claimed.Sort((a, b) => a.Start.CompareTo(b.Start));
        var pieces = new List<TextRun>();
        int pos = 0;
        foreach (var (start, length, rule) in claimed)
        {
            if (start > pos) pieces.Add(run with { Column = run.Column + pos, Text = run.Text[pos..start], Cells = start - pos });
            pieces.Add(run with { Column = run.Column + start, Text = run.Text.Substring(start, length), Cells = length, Fg = rule.Color, Bold = run.Bold || rule.Bold });
            pos = start + length;
        }
        if (pos < run.Text.Length) pieces.Add(run with { Column = run.Column + pos, Text = run.Text[pos..], Cells = run.Text.Length - pos });
        return pieces;
    }
}
