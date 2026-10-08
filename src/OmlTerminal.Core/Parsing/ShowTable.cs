using System.Text;

namespace OmlTerminal.Core.Parsing;

/// <summary>A "show" command's output turned into columns and rows.</summary>
public sealed record ShowTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int ColumnIndex(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public string Cell(IReadOnlyList<string> row, string column)
    {
        int i = ColumnIndex(column);
        return i >= 0 && i < row.Count ? row[i] : "";
    }

    public string ToCsv()
    {
        static string Esc(string f) => f.Contains(',') || f.Contains('"') || f.Contains('\n') ? $"\"{f.Replace("\"", "\"\"")}\"" : f;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns.Select(Esc)));
        foreach (var r in Rows) sb.AppendLine(string.Join(",", r.Select(Esc)));
        return sb.ToString();
    }

    public string ToMarkdown()
    {
        static string Esc(string f) => f.Replace("|", "\\|");
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", Columns.Select(Esc)) + " |");
        sb.AppendLine("|" + string.Concat(Columns.Select(_ => " --- |")));
        foreach (var r in Rows) sb.AppendLine("| " + string.Join(" | ", r.Select(Esc)) + " |");
        return sb.ToString();
    }

    /// <summary>Column widths for monospace display, each capped so one long cell can't push the rest off screen.</summary>
    public int[] Widths(IEnumerable<IReadOnlyList<string>> rows, int cap = 48)
    {
        var w = Columns.Select(c => Math.Min(cap, c.Length)).ToArray();
        foreach (var r in rows)
            for (int i = 0; i < w.Length && i < r.Count; i++)
                w[i] = Math.Max(w[i], Math.Min(cap, r[i].Length));
        return w;
    }

    public static string Pad(IReadOnlyList<string> cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < widths.Length; i++)
        {
            var c = i < cells.Count ? cells[i] : "";
            if (c.Length > widths[i]) c = c[..(widths[i] - 1)] + "…";
            sb.Append(c.PadRight(widths[i]));
            if (i < widths.Length - 1) sb.Append("  ");
        }
        return sb.ToString().TrimEnd();
    }
}
