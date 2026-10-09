using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Desktop.Controls;

/// <summary>Draws an ITerminalEngine snapshot and forwards input to the session - the Avalonia twin of the Windows
/// app's TerminalControl. All terminal decisions (VT parsing, key encoding, reconnect) live in Core.</summary>
public sealed class TerminalView : Control
{
    private TerminalSession? _session;
    private Typeface _regular, _bold, _italic, _boldItalic;
    private double _cellW = 8, _cellH = 16, _baseline;
    private double _fontSize = 14;
    private string _fontFamily = DefaultFontFamily;
    private bool _invalidatePending, _readyRaised, _suppressText;
    private (int Row, int Col)? _selStart, _selEnd;
    private bool _selecting, _selectionDragged;
    private TerminalMatch? _findHighlight;
    private int _lastTopRow = int.MinValue;
    private readonly Dictionary<int, IBrush> _brushes = new();

    /// <summary>Monospace fonts in the order most Linux/macOS desktops have them; Avalonia picks the first installed.</summary>
    public const string DefaultFontFamily = "Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Ubuntu Mono, Liberation Mono, Menlo, Monaco, Consolas, monospace";

    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromArgb(200, 0x38, 0xBD, 0xF8));
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), 1);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 0x38, 0xBD, 0xF8));
    private static readonly IPen FindPen = new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 2);

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Raised whenever the grid size changes (and once initially).</summary>
    public event Action<int, int>? GridSizeChanged;

    /// <summary>Raised once, the first time a valid size is known - the moment to connect.</summary>
    public event Action<int, int>? Ready;

    /// <summary>Bytes the user typed or pasted (not macro traffic) - what split-view synchronized typing would mirror.</summary>
    public event Action<byte[]>? UserInput;

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        RebuildFonts();
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) _session.Changed -= OnSessionChanged;
            _session = value;
            if (_session is not null) _session.Changed += OnSessionChanged;
            ClearSelection();
            _findHighlight = null;
            _lastTopRow = int.MinValue;
            RecalculateGrid();
            InvalidateVisual();
        }
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set { _fontSize = value; RebuildFonts(); RecalculateGrid(); InvalidateVisual(); }
    }

    public string TerminalFontFamily
    {
        get => _fontFamily;
        set { _fontFamily = string.IsNullOrWhiteSpace(value) ? DefaultFontFamily : value + ", " + DefaultFontFamily; RebuildFonts(); RecalculateGrid(); InvalidateVisual(); }
    }

    public IReadOnlyList<TerminalMatch> FindText(string query, bool matchCase) => _session?.Engine.Find(query, matchCase) ?? [];

    public void SetFindHighlight(TerminalMatch? match)
    {
        _findHighlight = match;
        if (match is { } m) _session?.Engine.ScrollToRow(Math.Max(0, m.Row - 2));
        InvalidateVisual();
    }

    public void Detach()
    {
        if (_session is not null) _session.Changed -= OnSessionChanged;
        _session = null;
    }

    private void SendUser(byte[] bytes)
    {
        if (_session is null) return;
        _session.Send(bytes);
        UserInput?.Invoke(bytes);
    }

    // ---------- layout / fonts ----------

    private void RebuildFonts()
    {
        var family = new FontFamily(_fontFamily);
        _regular = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        _bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(family, FontStyle.Italic, FontWeight.Normal);
        _boldItalic = new Typeface(family, FontStyle.Italic, FontWeight.Bold);
        var probe = Text(new string('M', 20), _regular, Brushes.White);
        _cellW = probe.WidthIncludingTrailingWhitespace / 20.0;
        _cellH = Math.Ceiling(probe.Height);
        _baseline = probe.Baseline;
    }

    private FormattedText Text(string s, Typeface face, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _fontSize, brush);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RecalculateGrid();
        InvalidateVisual();
    }

    private void RecalculateGrid()
    {
        if (_session is null || Bounds.Width < 1 || Bounds.Height < 1 || _cellW <= 0 || _cellH <= 0) return;
        int cols = Math.Max(2, (int)Math.Floor(Bounds.Width / _cellW));
        int rows = Math.Max(1, (int)Math.Floor(Bounds.Height / _cellH));
        bool changed = cols != Cols || rows != Rows;
        Cols = cols; Rows = rows;
        if (changed)
        {
            if (_readyRaised) _session.Resize(cols, rows);
            GridSizeChanged?.Invoke(cols, rows);
        }
        if (!_readyRaised)
        {
            _readyRaised = true;
            Ready?.Invoke(cols, rows);
        }
    }

    private void OnSessionChanged()
    {
        // Output can arrive as hundreds of small chunks a second; repaint at most once per dispatcher turn.
        if (_invalidatePending) return;
        _invalidatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _invalidatePending = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private IBrush BrushFor(int rgb)
    {
        if (!_brushes.TryGetValue(rgb, out var b))
        {
            if (_brushes.Count > 512) _brushes.Clear(); // truecolor gradients: don't grow without bound
            _brushes[rgb] = b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)).ToImmutable();
        }
        return b;
    }

    // ---------- rendering ----------

    public override void Render(DrawingContext ctx)
    {
        if (_session is null)
        {
            ctx.FillRectangle(BrushFor(0x0C0C0C), new Rect(Bounds.Size));
            return;
        }
        var snap = _session.Engine.GetSnapshot();
        ctx.FillRectangle(BrushFor(snap.DefaultBackground), new Rect(Bounds.Size));

        // Selections are viewport-relative; once the view moves (new output or scrolling) they point at other text.
        if (_selStart is not null && _lastTopRow != int.MinValue && snap.TopRow != _lastTopRow)
        {
            _selStart = _selEnd = null;
            _selectionDragged = false;
        }
        _lastTopRow = snap.TopRow;

        for (int r = 0; r < snap.VisibleRows.Count; r++)
        {
            double y = r * _cellH;
            foreach (var run in snap.VisibleRows[r].Runs)
            {
                double x = run.Column * _cellW;
                double w = run.Cells * _cellW;
                if (run.Bg != snap.DefaultBackground)
                    ctx.FillRectangle(BrushFor(run.Bg), new Rect(x, y, w + 0.5, _cellH));
                var fg = BrushFor(run.Fg);
                if (!string.IsNullOrWhiteSpace(run.Text))
                {
                    var face = run.Bold ? (run.Italic ? _boldItalic : _bold) : (run.Italic ? _italic : _regular);
                    ctx.DrawText(Text(run.Text, face, fg), new Point(x, y));
                }
                if (run.Underline)
                    ctx.DrawLine(new Pen(fg, 1), new Point(x, y + _cellH - 1.5), new Point(x + w, y + _cellH - 1.5));
            }
        }

        if (_selStart is { } selS && _selEnd is { } selE)
        {
            var (from, to) = NormalizeSelection(selS, selE);
            for (int r = from.Row; r <= to.Row && r < snap.VisibleRows.Count; r++)
            {
                int startCol = r == from.Row ? from.Col : 0;
                int endCol = r == to.Row ? to.Col : snap.Cols - 1;
                if (endCol < startCol) continue;
                ctx.FillRectangle(SelectionBrush, new Rect(startCol * _cellW, r * _cellH, (endCol - startCol + 1) * _cellW, _cellH));
            }
        }

        if (_findHighlight is { } find)
        {
            int viewportRow = find.Row - snap.TopRow;
            if (viewportRow >= 0 && viewportRow < snap.VisibleRows.Count)
                ctx.DrawRectangle(FindPen, new Rect(find.Column * _cellW, viewportRow * _cellH, find.Length * _cellW, _cellH));
        }

        var c = snap.Cursor;
        if (c.Visible && c.Row >= 0)
        {
            var rect = new Rect(c.Col * _cellW, c.Row * _cellH, _cellW, _cellH);
            if (IsFocused) ctx.FillRectangle(CursorBrush, rect);
            else ctx.DrawRectangle(CursorPen, rect.Deflate(0.5));
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }

    // ---------- selection / copy-paste ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        e.Handled = true;
        if (pt.Properties.IsRightButtonPressed)
        {
            // Right-click copy/paste, PuTTY-style: copy the selection if there is one, otherwise paste.
            if (_selectionDragged) { _ = CopySelectionAsync(); ClearSelection(); }
            else _ = PasteAsync();
            return;
        }
        if (pt.Properties.IsMiddleButtonPressed) { _ = PasteAsync(); return; }
        if (!pt.Properties.IsLeftButtonPressed) return;
        ClearSelection();
        _selStart = _selEnd = CellAt(pt.Position);
        _selecting = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var cell = CellAt(e.GetPosition(this));
        if (cell == _selEnd) return;
        _selEnd = cell;
        _selectionDragged = _selectionDragged || cell != _selStart;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
        if (!_selectionDragged) ClearSelection();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            TerminalFontSize = Math.Clamp(_fontSize + (e.Delta.Y > 0 ? 1 : -1), 8, 40);
        }
        else
        {
            ClearSelection();
            _session.Engine.ScrollViewport(e.Delta.Y > 0 ? -3 : 3);
        }
        e.Handled = true;
    }

    private (int Row, int Col) CellAt(Point p) => (
        Math.Clamp(_cellH > 0 ? (int)(p.Y / _cellH) : 0, 0, Math.Max(0, Rows - 1)),
        Math.Clamp(_cellW > 0 ? (int)(p.X / _cellW) : 0, 0, Math.Max(0, Cols - 1)));

    private void ClearSelection()
    {
        if (_selStart is null && !_selectionDragged) return;
        _selStart = _selEnd = null;
        _selectionDragged = false;
        InvalidateVisual();
    }

    private static ((int Row, int Col) From, (int Row, int Col) To) NormalizeSelection((int Row, int Col) a, (int Row, int Col) b) =>
        a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col) ? (a, b) : (b, a);

    private static string RowText(TerminalRow row)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in row.Runs)
        {
            while (sb.Length < run.Column) sb.Append(' ');
            sb.Append(run.Text);
        }
        return sb.ToString();
    }

    public bool HasSelection => _selectionDragged;

    private string? GetSelectedText()
    {
        if (_selStart is not { } s || _selEnd is not { } e || _session is null) return null;
        var (from, to) = NormalizeSelection(s, e);
        var snap = _session.Engine.GetSnapshot();
        var lines = new List<string>();
        for (int r = from.Row; r <= to.Row && r < snap.VisibleRows.Count; r++)
        {
            var text = RowText(snap.VisibleRows[r]);
            int startCol = Math.Clamp(r == from.Row ? from.Col : 0, 0, text.Length);
            int endCol = Math.Clamp(r == to.Row ? to.Col + 1 : text.Length, startCol, text.Length);
            lines.Add(text.Substring(startCol, endCol - startCol).TrimEnd());
        }
        return string.Join(Environment.NewLine, lines);
    }

    public async Task CopySelectionAsync()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); } catch { }
    }

    public async Task PasteAsync()
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            SendUser(KeyEncoder.EncodeText(text.Replace("\r\n", "\r").Replace('\n', '\r')));
            ClearSelection();
        }
        catch { }
    }

    // ---------- keyboard ----------

    private static TerminalKey? Map(Key k) => k switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Delete => TerminalKey.Delete,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (k - Key.F1),
        _ => null,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _suppressText = false;
        if (_session is null) { base.OnKeyDown(e); return; }
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control), alt = mods.HasFlag(KeyModifiers.Alt), shift = mods.HasFlag(KeyModifiers.Shift);

        // Linux terminals copy/paste with Ctrl+Shift+C/V (Ctrl+C must stay "interrupt"); Shift+Insert pastes too.
        if (ctrl && shift && e.Key == Key.C) { _ = CopySelectionAsync(); e.Handled = true; return; }
        if ((ctrl && shift && e.Key == Key.V) || (shift && e.Key == Key.Insert)) { _ = PasteAsync(); e.Handled = true; return; }
        if (shift && !ctrl && e.Key is Key.PageUp or Key.PageDown)
        {
            _session.Engine.ScrollViewport(e.Key == Key.PageUp ? -Math.Max(1, Rows - 1) : Math.Max(1, Rows - 1));
            e.Handled = true;
            return;
        }

        // Cisco IOS's "abort a running ping/traceroute" is Ctrl+^ (Ctrl+Shift+6): the single byte 0x1E.
        if (ctrl && e.Key == Key.D6) { SendUser([0x1E]); e.Handled = true; _suppressText = true; return; }

        if (Map(e.Key) is { } tk)
        {
            var bytes = KeyEncoder.Encode(tk, _session.Engine.ApplicationCursorKeys, shift);
            if (bytes is not null)
            {
                if (alt && tk is not TerminalKey.Enter) bytes = [0x1b, .. bytes];
                SendUser(bytes);
                ClearSelection();
            }
            e.Handled = true;
            _suppressText = true;
            return;
        }

        // Ctrl+letter is a control character - except the app's own shortcuts (Ctrl+Shift+anything, plus the
        // window's Ctrl+N new session and Ctrl+W close tab), which bubble up to the window's key bindings.
        if (ctrl && !alt && !shift && e.Key is >= Key.A and <= Key.Z && e.Key is not (Key.N or Key.W))
        {
            var bytes = KeyEncoder.EncodeCtrlLetter((char)('A' + (e.Key - Key.A)));
            if (bytes is not null) { SendUser(bytes); ClearSelection(); e.Handled = true; _suppressText = true; }
            return;
        }
        if (ctrl && !alt && !shift)
        {
            byte? b = e.Key switch { Key.Space or Key.D2 => 0x00, Key.OemOpenBrackets => 0x1b, Key.OemPipe => 0x1c, Key.OemCloseBrackets => 0x1d, _ => null };
            if (b is { } cb) { SendUser([cb]); e.Handled = true; _suppressText = true; return; }
        }

        if (alt && !ctrl && e.Key is >= Key.A and <= Key.Z)
        {
            char ch = (char)('a' + (e.Key - Key.A));
            SendUser(KeyEncoder.EncodeText((shift ? char.ToUpperInvariant(ch) : ch).ToString(), alt: true));
            ClearSelection();
            e.Handled = true;
            _suppressText = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        // A key already sent by OnKeyDown (Alt+x, Ctrl+x) can still arrive here as text on some platforms.
        if (_suppressText) { _suppressText = false; return; }
        var text = new string(e.Text.Where(ch => ch >= 0x20 && ch != 0x7f).ToArray());
        if (text.Length == 0) return;
        SendUser(KeyEncoder.EncodeText(text));
        ClearSelection();
    }
}
