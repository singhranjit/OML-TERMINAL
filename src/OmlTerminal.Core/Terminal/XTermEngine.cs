using System.Text;
using XTermTerminal = XTerm.Terminal;
using XTerm.Buffer;
using XTerm.Options;

namespace OmlTerminal.Core.Terminal;

public sealed class XTermEngine : ITerminalEngine
{
    private const int DefaultFgIndex = 256;
    private const int DefaultBgIndex = 257;

    private readonly XTermTerminal _term;
    private readonly object _lock = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    public event Action? Changed;
    public event Action<byte[]>? Response;

    public HighlightRuleSet? Highlights { get; set; }

    public XTermEngine(int cols = 80, int rows = 24, int scrollback = 5000)
    {
        _term = new XTermTerminal(new TerminalOptions
        {
            Cols = cols,
            Rows = rows,
            Scrollback = scrollback,
            Theme = new ThemeOptions { Foreground = "#d4d4d4", Background = "#0c0c0c" },
        });
        _term.DataReceived += (_, e) =>
        {
            var bytes = Encoding.UTF8.GetBytes(e.Data);
            if (bytes.Length > 0) Response?.Invoke(bytes);
        };
    }

    public int Cols { get { lock (_lock) return _term.Cols; } }
    public int Rows { get { lock (_lock) return _term.Rows; } }
    public bool ApplicationCursorKeys { get { lock (_lock) return _term.ApplicationCursorKeys; } }
    public string Title { get { lock (_lock) return _term.Title ?? ""; } }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock)
        {
            // Stateful decoder so a multi-byte UTF-8 char split across network reads is not corrupted.
            var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
            int n = _utf8.GetChars(data, chars, flush: false);
            if (n > 0) _term.Write(new string(chars, 0, n));
        }
        Changed?.Invoke();
    }

    public void WriteLocal(string text)
    {
        lock (_lock) _term.Write(text);
        Changed?.Invoke();
    }

    public void Resize(int cols, int rows)
    {
        if (cols < 2 || rows < 1) return;
        lock (_lock) _term.Resize(cols, rows);
        Changed?.Invoke();
    }

    public void ScrollViewport(int lines)
    {
        lock (_lock) _term.ScrollLines(lines);
        Changed?.Invoke();
    }

    public void ScrollToBottom()
    {
        lock (_lock) _term.ScrollToBottom();
        Changed?.Invoke();
    }

    public void ScrollToRow(int row)
    {
        lock (_lock) _term.Buffer.ScrollToLine(row);
        Changed?.Invoke();
    }

    public IReadOnlyList<TerminalMatch> Find(string query, bool matchCase)
    {
        if (string.IsNullOrEmpty(query)) return [];
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        lock (_lock)
        {
            var lines = _term.Buffer.Lines;
            var matches = new List<TerminalMatch>();
            for (int r = 0; r < lines.Length; r++)
            {
                var line = lines[r];
                if (line is null) continue;
                var text = RowPlainText(line);
                for (int start = 0; ;)
                {
                    int idx = text.IndexOf(query, start, comparison);
                    if (idx < 0) break;
                    matches.Add(new TerminalMatch(r, idx, query.Length));
                    start = idx + 1; // allows overlapping matches - simplest, most predictable behavior
                }
            }
            return matches;
        }
    }

    /// <summary>Plain text of a whole buffer line (unlike BuildRow, this keeps every column - including
    /// trailing blanks - since Find needs stable column offsets, not a trimmed display run).</summary>
    private static string RowPlainText(BufferLine line)
    {
        var sb = new StringBuilder(line.Length);
        for (int c = 0; c < line.Length; c++)
        {
            var cell = line[c];
            if (cell.Width == 0) continue;
            sb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
        }
        return sb.ToString();
    }

    public TerminalSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var b = _term.Buffer;
            int defFg = _term.Colors.Foreground & 0xFFFFFF;
            int defBg = _term.Colors.Background & 0xFFFFFF;
            var rows = new List<TerminalRow>(_term.Rows);
            for (int r = 0; r < _term.Rows; r++)
            {
                int idx = b.YDisp + r;
                rows.Add(idx >= 0 && idx < b.Lines.Length
                    ? BuildRow(b.Lines[idx]!, _term.Cols, defFg, defBg)
                    : new TerminalRow(Array.Empty<TextRun>()));
            }
            var highlights = Highlights;
            if (highlights is not null && !_term.IsAlternateBufferActive)
                for (int r = 0; r < rows.Count; r++) rows[r] = highlights.Apply(rows[r], defFg);
            bool atBottom = b.YDisp == b.YBase;
            var cursor = new CursorState(b.X, atBottom ? b.Y : -1, _term.CursorVisible && atBottom);
            return new TerminalSnapshot(_term.Cols, _term.Rows, rows, cursor, defFg, defBg, b.YDisp);
        }
    }

    public string GetBufferText()
    {
        lock (_lock)
        {
            var lines = _term.Buffer.Lines;
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append(Environment.NewLine);
                var line = lines[i];
                sb.Append(line is null ? "" : RowPlainText(line).TrimEnd());
            }
            return sb.ToString();
        }
    }

    private int Resolve(int color, int mode, int defFg, int defBg, int fallback)
    {
        if (mode == 1) return color & 0xFFFFFF;
        if (color == DefaultFgIndex) return defFg;
        if (color == DefaultBgIndex) return defBg;
        return color is >= 0 and < 256 ? _term.Colors[color] & 0xFFFFFF : fallback;
    }

    private TerminalRow BuildRow(BufferLine line, int cols, int defFg, int defBg)
    {
        var runs = new List<TextRun>();
        var sb = new StringBuilder();
        int runStart = 0, runFg = 0, runBg = 0;
        bool runBold = false, runItalic = false, runUnder = false, open = false;

        void Flush(int endCol)
        {
            if (open && sb.Length > 0)
                runs.Add(new TextRun(runStart, sb.ToString(), endCol - runStart, runFg, runBg, runBold, runItalic, runUnder));
            sb.Clear();
            open = false;
        }

        int limit = Math.Min(cols, line.Length);
        for (int c = 0; c < limit; c++)
        {
            var cell = line[c];
            if (cell.Width == 0) continue; // continuation half of a wide glyph

            var a = cell.Attributes;
            int fg = Resolve(a.GetFgColor(), a.GetFgColorMode(), defFg, defBg, defFg);
            int bg = Resolve(a.GetBgColor(), a.GetBgColorMode(), defFg, defBg, defBg);
            if (a.IsInverse()) (fg, bg) = (bg, fg);
            if (a.IsInvisible()) fg = bg;
            bool bold = a.IsBold(), italic = a.IsItalic(), under = a.IsUnderline();

            string text = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
            bool wide = cell.Width > 1;

            if (open && (wide || fg != runFg || bg != runBg || bold != runBold || italic != runItalic || under != runUnder))
                Flush(c);
            if (!open)
            {
                runStart = c; runFg = fg; runBg = bg; runBold = bold; runItalic = italic; runUnder = under; open = true;
            }
            sb.Append(text);
            if (wide) Flush(c + cell.Width);
        }
        Flush(limit);

        while (runs.Count > 0 && runs[^1].Bg == defBg && !runs[^1].Underline && string.IsNullOrWhiteSpace(runs[^1].Text))
            runs.RemoveAt(runs.Count - 1);
        return new TerminalRow(runs);
    }

    public void Dispose() { lock (_lock) _term.Dispose(); }
}
