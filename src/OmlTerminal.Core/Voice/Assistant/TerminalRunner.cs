using System.Text;
using System.Text.RegularExpressions;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Core.Voice.Assistant;

/// <summary>
/// Types one command into a live terminal session - the tab the engineer is watching, so they see exactly what
/// the assistant does - and collects what the device printed back: pagers ("--More--") are paged through, and the
/// command is finished when output has gone quiet and the last line is a prompt again.
/// </summary>
public static partial class TerminalRunner
{
    public static async Task<string> RunAsync(TerminalSession session, string command, CancellationToken ct = default,
        TimeSpan? quiet = null, TimeSpan? limit = null)
    {
        var settle = quiet ?? TimeSpan.FromMilliseconds(900);
        var max = limit ?? TimeSpan.FromSeconds(30);
        var sb = new StringBuilder();
        var gate = new object();
        var lastData = DateTime.UtcNow;
        bool any = false;
        void OnOutput(byte[] data)
        {
            var text = Encoding.UTF8.GetString(AnsiStripper.Strip(data));
            lock (gate) { sb.Append(text); lastData = DateTime.UtcNow; any = true; }
        }

        session.OutputReceived += OnOutput;
        try
        {
            session.Send(Encoding.UTF8.GetBytes(command + "\r"));
            var started = DateTime.UtcNow;
            int pages = 0;
            while (DateTime.UtcNow - started < max)
            {
                await Task.Delay(120, ct).ConfigureAwait(false);
                string snapshot; DateTime last; bool got;
                lock (gate) { snapshot = sb.ToString(); last = lastData; got = any; }
                var tail = LastLine(snapshot);
                if (Pager().IsMatch(tail) && pages < 400)
                {
                    pages++;
                    lock (gate) { sb.Length -= Math.Min(sb.Length, tail.Length); }
                    session.Send(" "u8);
                    continue;
                }
                var idle = DateTime.UtcNow - last;
                // Quiet after output and back at a prompt; or quiet for long with no prompt (a command that never
                // returns one, e.g. "terminal monitor"), give up waiting.
                if (got && idle >= settle && LooksLikePrompt(tail)) break;
                if (got && idle >= settle * 5) break;
                if (!got && DateTime.UtcNow - started > TimeSpan.FromSeconds(8)) break;
                if (!session.IsConnected) break;
            }
            string raw;
            lock (gate) raw = sb.ToString();
            return Clean(raw, command);
        }
        finally { session.OutputReceived -= OnOutput; }
    }

    /// <summary>Removes the echoed command, the final prompt, pager residue and carriage-return overwrites.</summary>
    public static string Clean(string raw, string command)
    {
        var lines = new List<string>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            int cr = line.LastIndexOf('\r');
            if (cr >= 0 && cr < line.Length - 1) line = line[(cr + 1)..];
            line = Backspaces().Replace(line.Replace("\r", ""), "");
            line = Pager().Replace(line, "").TrimEnd();
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[0].Contains(command.Trim(), StringComparison.Ordinal)) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && LooksLikePrompt(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines).TrimEnd();
    }

    public static bool LooksLikePrompt(string line)
    {
        var t = line.TrimEnd();
        if (t.Length == 0 || t.Length > 120) return false;
        return ScreenContext.Analyze(t).Platform != DevicePlatform.Unknown || GenericPrompt().IsMatch(t);
    }

    private static string LastLine(string s)
    {
        var t = s.Replace("\r\n", "\n").TrimEnd('\n');
        int nl = t.LastIndexOf('\n');
        var last = nl >= 0 ? t[(nl + 1)..] : t;
        int cr = last.LastIndexOf('\r');
        return cr >= 0 && cr < last.Length - 1 ? last[(cr + 1)..] : last.Replace("\r", "");
    }

    [GeneratedRegex(@"\s*(?:--\s?More\s?--|<--- More --->|-- More --|\(more\)|---\(more( \d+%)?\)---|Press any key to continue|--More--\s*\(\d+%\))\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Pager();

    [GeneratedRegex(@".\x08")]
    private static partial Regex Backspaces();

    [GeneratedRegex(@"^\S{1,60}\s?[#>$%]\s?$")]
    private static partial Regex GenericPrompt();
}
