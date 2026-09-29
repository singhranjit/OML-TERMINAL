namespace OmlTerminal.Core.Terminal;

/// <summary>A run of same-styled text starting at <paramref name="Column"/>. Colors are 0xRRGGBB, already resolved (inverse applied).</summary>
public readonly record struct TextRun(int Column, string Text, int Cells, int Fg, int Bg, bool Bold, bool Italic, bool Underline);

public sealed record TerminalRow(IReadOnlyList<TextRun> Runs);

/// <param name="Row">Viewport row of the cursor, or -1 when hidden or scrolled back.</param>
public readonly record struct CursorState(int Col, int Row, bool Visible);

/// <summary>A Find() hit. Row is an absolute buffer row (scrollback included) - the same space ScrollToRow
/// and TerminalSnapshot.TopRow operate in, not viewport-relative like TerminalRow/CursorState.</summary>
public readonly record struct TerminalMatch(int Row, int Column, int Length);

/// <param name="TopRow">Absolute buffer row (scrollback included) that VisibleRows[0] corresponds to - lets
/// a caller holding a Find() result work out whether a match is currently on screen, and at which viewport row.</param>
public sealed record TerminalSnapshot(
    int Cols, int Rows, IReadOnlyList<TerminalRow> VisibleRows, CursorState Cursor,
    int DefaultForeground, int DefaultBackground, int TopRow);

/// <summary>Everything the renderer needs from the emulator. The UI never touches XTerm.NET directly.</summary>
public interface ITerminalEngine : IDisposable
{
    int Cols { get; }
    int Rows { get; }
    bool ApplicationCursorKeys { get; }
    string Title { get; }

    /// <summary>Feed raw bytes from the remote end. Thread-safe.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Feed locally generated text (e.g. status messages). Thread-safe.</summary>
    void WriteLocal(string text);

    void Resize(int cols, int rows);

    /// <summary>Scrolls the viewport into scrollback (negative = up, positive = down).</summary>
    void ScrollViewport(int lines);

    /// <summary>Jumps the viewport back to the live bottom of the buffer.</summary>
    void ScrollToBottom();

    /// <summary>Scrolls so the given absolute buffer row (see TerminalMatch/TerminalSnapshot.TopRow) becomes the top of the viewport.</summary>
    void ScrollToRow(int row);

    /// <summary>Searches the full buffer, scrollback included - not just what's currently visible.</summary>
    IReadOnlyList<TerminalMatch> Find(string query, bool matchCase);

    /// <summary>Consistent copy of the visible screen. Thread-safe.</summary>
    TerminalSnapshot GetSnapshot();

    /// <summary>Plain-text scrollback including off-screen rows, for saving or reviewing command output.</summary>
    string GetBufferText() => string.Join("\n", GetSnapshot().VisibleRows.Select(r => string.Concat(r.Runs.Select(run => run.Text))));

    /// <summary>Raised after EVERY Write/WriteLocal/Resize/ScrollViewport. XTerm.NET has no per-write change event, so this is the render trigger.</summary>
    event Action? Changed;

    /// <summary>Bytes the emulator wants sent to the remote (e.g. device-attribute replies).</summary>
    event Action<byte[]>? Response;

    /// <summary>Optional keyword coloring applied to snapshots. Not applied on the alternate screen (vim, less, top).</summary>
    HighlightRuleSet? Highlights { get; set; }
}
