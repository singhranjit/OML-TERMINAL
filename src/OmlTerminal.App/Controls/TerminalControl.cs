using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OmlTerminal.Core.Terminal;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace OmlTerminal.App.Controls;

/// <summary>Draws an ITerminalEngine snapshot and forwards input to the session. All decisions live in Core.</summary>
public sealed class TerminalControl : UserControl
{
    private readonly CanvasControl _canvas = new();
    private TerminalSession? _session;
    private CanvasTextFormat _regular = null!, _bold = null!, _italic = null!, _boldItalic = null!;
    private double _cellW = 8, _cellH = 16;
    private double _fontSize = 14;
    private string _fontFamily = "Cascadia Mono";
    private bool _invalidatePending;
    private bool _readyRaised;
    private char _highSurrogate;
    private CancellationTokenSource? _heartbeatCts;
    private bool _hasPaintedSinceLoad;
    private (int Row, int Col)? _selStart;
    private (int Row, int Col)? _selEnd;
    private bool _selecting;
    private bool _selectionDragged;
    private TerminalMatch? _findHighlight;
    private int _lastTopRow = int.MinValue;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Raised whenever the control's cols/rows change (and once initially).</summary>
    public event Action<int, int>? GridSizeChanged;

    /// <summary>Raised once, the first time a valid size is known — the moment to connect.</summary>
    public event Action<int, int>? Ready;

    /// <summary>Raised with bytes the user typed or pasted into this control - not macro or multi-exec traffic,
    /// which goes straight to the session. Split view's synchronized typing mirrors exactly this.</summary>
    public event Action<byte[]>? UserInput;

    private void SendUser(byte[] bytes)
    {
        if (_session is null) return;
        _session.Send(bytes);
        UserInput?.Invoke(bytes);
    }

    public TerminalControl()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        IsFocusEngagementEnabled = false;
        _canvas.ClearColor = Color.FromArgb(255, 12, 12, 12);
        _canvas.IsTabStop = false;
        // A transparent Grid is what makes the whole area hit-testable (UserControl.Background is not, and the canvas is set non-hit-testable below).
        var hitSurface = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        hitSurface.Children.Add(_canvas);
        Content = hitSurface;
        RebuildFonts();

        _canvas.Draw += OnDraw;
        SizeChanged += (_, _) => { RecalculateGrid(); RequestRepaint(); };
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        PointerWheelChanged += OnWheel;
        // The canvas would otherwise swallow the click, so the control could never be focused by mouse.
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _canvas.IsHitTestVisible = false;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        RightTapped += OnRightTapped;
        GotFocus += (_, _) => RequestRepaint();
        LostFocus += (_, _) => RequestRepaint();
        Loaded += (_, _) => { Focus(FocusState.Programmatic); RequestRepaint(); StartHeartbeat(); };
        Unloaded += (_, _) => StopHeartbeat();
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
            RequestRepaint();
        }
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set { _fontSize = value; RebuildFonts(); RecalculateGrid(); RequestRepaint(); }
    }

    public string TerminalFontFamily
    {
        get => _fontFamily;
        set { _fontFamily = value; RebuildFonts(); RecalculateGrid(); RequestRepaint(); }
    }

    /// <summary>Called from Tabs_SelectionChanged whenever this control's tab becomes the active one again.
    /// A plain RequestRepaint() alone isn't reliable here: the same "Win2D silently drops Invalidate()"
    /// problem StartHeartbeat's own doc comment describes for first-load also happens when a CanvasControl
    /// regains visibility after being hidden behind another tab (not just when it's brand new), and this
    /// control is created once per tab and never reloaded, so Loaded/StartHeartbeat only ever fired once, at
    /// tab creation - switching back to an already-open tab never got that same retry-until-it-actually-
    /// paints safety net. Restarting the heartbeat here extends the exact same self-limiting retry (it stops
    /// itself as soon as OnDraw actually fires) to every tab switch, not just the first paint.</summary>
    public void Refresh()
    {
        StartHeartbeat();
        RequestRepaint();
    }

    // ---------- find ----------

    /// <summary>Searches the full buffer (scrollback included), not just what's currently on screen.</summary>
    public IReadOnlyList<TerminalMatch> FindText(string query, bool matchCase) => _session?.Engine.Find(query, matchCase) ?? [];

    /// <summary>Scrolls so the match is on screen (with a little leading context) and outlines it. Pass null to clear.</summary>
    public void SetFindHighlight(TerminalMatch? match)
    {
        _findHighlight = match;
        if (match is { } m) _session?.Engine.ScrollToRow(Math.Max(0, m.Row - 2));
        RequestRepaint();
    }

    // A single Invalidate() can be silently dropped if Win2D's device/swapchain isn't fully ready yet - seen right
    // after opening or switching to a tab, and also mid-session on a virtualized/remote-desktop GPU under bursty
    // redraw load (e.g. scrolling). A short burst of retries reliably lands at least one once the canvas catches up.
    //
    // Only one burst is ever kept in flight, though - it used to be started fresh on every single call (so every
    // incoming byte of session output), which meant a login banner/MOTD streaming in as dozens of small updates
    // over the first several seconds spawned dozens of overlapping delayed-Invalidate chains, well beyond the
    // display's actual refresh rate. That redraw storm is what showed up as the terminal visibly flickering right
    // after connecting. Coalescing to one in-flight burst keeps the same drop-recovery guarantee for every redraw,
    // not just the first, while capping the rate to at most a handful of extra invalidates per ~400ms window no
    // matter how many updates arrive in that window.
    private bool _repaintBurstInFlight;

    private void RequestRepaint()
    {
        _canvas.Invalidate();
        if (_repaintBurstInFlight) return;
        _repaintBurstInFlight = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _canvas.Invalidate());
        _ = RepaintBurstAsync();
    }

    private async Task RepaintBurstAsync()
    {
        try
        {
            foreach (var ms in new[] { 50, 150, 400 })
            {
                await Task.Delay(ms);
                DispatcherQueue.TryEnqueue(() => _canvas.Invalidate());
            }
        }
        finally { _repaintBurstInFlight = false; }
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        _hasPaintedSinceLoad = false;
        var cts = new CancellationTokenSource();
        _heartbeatCts = cts;
        _ = HeartbeatLoopAsync(cts.Token);
    }

    private void StopHeartbeat()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts = null;
    }

    // A fixed retry burst isn't enough: if Win2D's Direct3D device/swapchain takes longer than the burst window to
    // (re)create after this control re-enters the visual tree - plausible on a virtualized/remote-desktop GPU, and
    // more so when several CanvasControls are created close together (e.g. "Connect All") - Invalidate() calls made
    // in the meantime are silently dropped and nothing ever asks again. Keep nudging at a low, cheap rate until
    // OnDraw actually fires once (proof the device is ready and Invalidate() is no longer being dropped), so
    // recovery isn't bounded by a guessed timeout no matter how long that takes. Stop as soon as that happens
    // instead of running for the tab's whole lifetime - a continuous forced full-redraw is wasteful and was
    // visibly flashing the terminal on every tick once the canvas was already painting fine on its own.
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_hasPaintedSinceLoad)
            {
                await Task.Delay(300, ct);
                DispatcherQueue.TryEnqueue(() => _canvas.Invalidate());
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Detach()
    {
        StopHeartbeat();
        if (_session is not null) _session.Changed -= OnSessionChanged;
        _session = null;
        Content = null;
        _canvas.RemoveFromVisualTree();
    }

    private void RebuildFonts()
    {
        string family = ResolveFamily(_fontFamily);
        CanvasTextFormat Make(Windows.UI.Text.FontWeight w, Windows.UI.Text.FontStyle s) => new()
        {
            FontFamily = family,
            FontSize = (float)_fontSize,
            FontWeight = w,
            FontStyle = s,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        _regular = Make(Microsoft.UI.Text.FontWeights.Normal, Windows.UI.Text.FontStyle.Normal);
        _bold = Make(Microsoft.UI.Text.FontWeights.Bold, Windows.UI.Text.FontStyle.Normal);
        _italic = Make(Microsoft.UI.Text.FontWeights.Normal, Windows.UI.Text.FontStyle.Italic);
        _boldItalic = Make(Microsoft.UI.Text.FontWeights.Bold, Windows.UI.Text.FontStyle.Italic);

        using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), new string('M', 20), _regular, 10000, 1000);
        _cellW = layout.LayoutBounds.Width / 20.0;
        _cellH = Math.Ceiling(layout.LayoutBounds.Height);
    }

    private static string ResolveFamily(string preferred)
    {
        var installed = CanvasTextFormat.GetSystemFontFamilies();
        foreach (var f in new[] { preferred, "Cascadia Mono", "Consolas" })
            if (Array.IndexOf(installed, f) >= 0) return f;
        return "Consolas";
    }

    private void RecalculateGrid()
    {
        if (_session is null || ActualWidth < 1 || ActualHeight < 1 || _cellW <= 0 || _cellH <= 0) return;
        int cols = Math.Max(2, (int)Math.Floor(ActualWidth / _cellW));
        int rows = Math.Max(1, (int)Math.Floor(ActualHeight / _cellH));
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
        if (_invalidatePending) return;
        _invalidatePending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _invalidatePending = false;
            RequestRepaint();
        });
    }

    private static Color ToColor(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _hasPaintedSinceLoad = true;
        var ds = args.DrawingSession;
        if (_session is null) { ds.Clear(_canvas.ClearColor); return; }

        var snap = _session.Engine.GetSnapshot();
        ds.Clear(ToColor(snap.DefaultBackground));

        // A selection's (Row, Col) are viewport-relative. OnWheel already clears it before a manual scroll for
        // exactly this reason ("row indices are viewport-relative and go stale the moment the view scrolls"),
        // but a live-following viewport also moves on its own when new remote output arrives while the user
        // hasn't scrolled back - that path (OnSessionChanged) never scrolls directly, so nothing caught it
        // there. Catching it here instead, by comparing TopRow frame-to-frame, covers both cases without
        // needing every future scroll source to remember to clear the selection itself.
        if (_selStart is not null && _lastTopRow != int.MinValue && snap.TopRow != _lastTopRow)
        {
            _selStart = _selEnd = null;
            _selectionDragged = false;
        }
        _lastTopRow = snap.TopRow;

        for (int r = 0; r < snap.VisibleRows.Count; r++)
        {
            float y = (float)(r * _cellH);
            foreach (var run in snap.VisibleRows[r].Runs)
            {
                float x = (float)(run.Column * _cellW);
                float w = (float)(run.Cells * _cellW);
                if (run.Bg != snap.DefaultBackground)
                    ds.FillRectangle(x, y, w + 0.5f, (float)_cellH, ToColor(run.Bg));

                var fmt = run.Bold ? (run.Italic ? _boldItalic : _bold) : (run.Italic ? _italic : _regular);
                var fg = ToColor(run.Fg);
                ds.DrawText(run.Text, x, y, fg, fmt);
                if (run.Underline)
                    ds.DrawLine(x, y + (float)_cellH - 1.5f, x + w, y + (float)_cellH - 1.5f, fg, 1f);
            }
        }

        if (_selStart is { } selS && _selEnd is { } selE)
        {
            var (from, to) = NormalizeSelection(selS, selE);
            var selColor = Color.FromArgb(90, 0x38, 0xBD, 0xF8);
            for (int r = from.Row; r <= to.Row && r < snap.VisibleRows.Count; r++)
            {
                int startCol = r == from.Row ? from.Col : 0;
                int endCol = r == to.Row ? to.Col : snap.Cols - 1;
                if (endCol < startCol) continue;
                float x = (float)(startCol * _cellW);
                float y = (float)(r * _cellH);
                float w = (float)((endCol - startCol + 1) * _cellW);
                ds.FillRectangle(x, y, w, (float)_cellH, selColor);
            }
        }

        if (_findHighlight is { } find)
        {
            int viewportRow = find.Row - snap.TopRow;
            if (viewportRow >= 0 && viewportRow < snap.VisibleRows.Count)
            {
                float x = (float)(find.Column * _cellW);
                float y = (float)(viewportRow * _cellH);
                float w = (float)(find.Length * _cellW);
                ds.DrawRectangle(x, y, w, (float)_cellH, Color.FromArgb(255, 0xF5, 0x9E, 0x0B), 2f);
            }
        }

        var c = snap.Cursor;
        if (c.Visible && c.Row >= 0)
        {
            var rect = new Windows.Foundation.Rect(c.Col * _cellW, c.Row * _cellH, _cellW, _cellH);
            var cursorColor = Color.FromArgb(255, 0x38, 0xBD, 0xF8);
            if (FocusState != FocusState.Unfocused)
            {
                ds.FillRectangle(rect, Color.FromArgb(200, cursorColor.R, cursorColor.G, cursorColor.B));
            }
            else
            {
                ds.DrawRectangle(rect, cursorColor, 1f);
            }
        }
    }

    // ---------- selection / copy-paste ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        e.Handled = true; // otherwise the TabView's default click-to-focus takes focus straight back
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        ClearSelection();
        _selStart = _selEnd = CellAt(pt.Position);
        _selecting = true;
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        var cell = CellAt(e.GetCurrentPoint(this).Position);
        if (cell == _selEnd) return;
        _selEnd = cell;
        _selectionDragged = _selectionDragged || cell != _selStart;
        RequestRepaint();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        try { ReleasePointerCapture(e.Pointer); } catch { }
        if (!_selectionDragged) ClearSelection(); // a plain click, not a drag - leave right-click free to paste
    }

    /// <summary>Right-click copy/paste, like PuTTY and MobaXterm: copies the active selection if there is one,
    /// otherwise pastes the clipboard - no context menu needed for the single most common action.</summary>
    private async void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        Focus(FocusState.Pointer);
        if (_selectionDragged)
        {
            CopySelection();
            ClearSelection();
        }
        else
        {
            await PasteAsync();
        }
    }

    private (int Row, int Col) CellAt(Windows.Foundation.Point p) => (
        Math.Clamp(_cellH > 0 ? (int)(p.Y / _cellH) : 0, 0, Math.Max(0, Rows - 1)),
        Math.Clamp(_cellW > 0 ? (int)(p.X / _cellW) : 0, 0, Math.Max(0, Cols - 1)));

    private void ClearSelection()
    {
        if (_selStart is null && !_selectionDragged) return;
        _selStart = _selEnd = null;
        _selectionDragged = false;
        RequestRepaint();
    }

    private static ((int Row, int Col) From, (int Row, int Col) To) NormalizeSelection((int Row, int Col) a, (int Row, int Col) b) =>
        a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col) ? (a, b) : (b, a);

    /// <summary>Reconstructs a row's plain text from its runs (which already omit trailing blank cells).</summary>
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
        return string.Join("\r\n", lines);
    }

    private void CopySelection()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        try { Clipboard.SetContent(package); } catch { }
    }

    // ---------- input ----------

    private static bool IsDown(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static TerminalKey? Map(VirtualKey k) => k switch
    {
        VirtualKey.Enter => TerminalKey.Enter,
        VirtualKey.Back => TerminalKey.Backspace,
        VirtualKey.Tab => TerminalKey.Tab,
        VirtualKey.Escape => TerminalKey.Escape,
        VirtualKey.Up => TerminalKey.Up,
        VirtualKey.Down => TerminalKey.Down,
        VirtualKey.Left => TerminalKey.Left,
        VirtualKey.Right => TerminalKey.Right,
        VirtualKey.Delete => TerminalKey.Delete,
        VirtualKey.Home => TerminalKey.Home,
        VirtualKey.End => TerminalKey.End,
        VirtualKey.PageUp => TerminalKey.PageUp,
        VirtualKey.PageDown => TerminalKey.PageDown,
        VirtualKey.Insert => TerminalKey.Insert,
        >= VirtualKey.F1 and <= VirtualKey.F12 => TerminalKey.F1 + (k - VirtualKey.F1),
        _ => null,
    };

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null) return;
        bool ctrl = IsDown(VirtualKey.Control), alt = IsDown(VirtualKey.Menu), shift = IsDown(VirtualKey.Shift);

        if ((ctrl && shift && e.Key == VirtualKey.V) || (shift && e.Key == VirtualKey.Insert))
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }

        // Cisco IOS's "abort a running ping/traceroute" escape is Ctrl+^ (Ctrl+Shift+6), sent as the single
        // byte 0x1E - not Ctrl+C, which IOS ignores mid-ping. Handled explicitly because WinUI doesn't reliably
        // turn that chord into a CharacterReceived event, which is exactly why people get stuck unable to exit.
        if (ctrl && e.Key == VirtualKey.Number6)
        {
            SendUser([0x1E]);
            e.Handled = true;
            return;
        }

        if (Map(e.Key) is { } tk)
        {
            var bytes = KeyEncoder.Encode(tk, _session.Engine.ApplicationCursorKeys, shift);
            if (bytes is not null) { SendUser(bytes); ClearSelection(); }
            e.Handled = true;
            return;
        }

        // Ctrl+N / Ctrl+W are app shortcuts (new session / close tab); let them bubble to the menu accelerators.
        // Ctrl+Shift+letter is reserved for app shortcuts (record macro, multi-exec, OML node).
        if (ctrl && !alt && !shift && e.Key is not (VirtualKey.N or VirtualKey.W) && e.Key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            var bytes = KeyEncoder.EncodeCtrlLetter((char)('A' + (e.Key - VirtualKey.A)));
            if (bytes is not null) { SendUser(bytes); ClearSelection(); e.Handled = true; }
            return;
        }

        if (alt && !ctrl && e.Key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            char ch = (char)('a' + (e.Key - VirtualKey.A));
            SendUser(KeyEncoder.EncodeText((shift ? char.ToUpperInvariant(ch) : ch).ToString(), alt: true));
            ClearSelection();
            e.Handled = true;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_session is null) return;
        char ch = e.Character;
        e.Handled = true;
        // Control characters are produced from KeyDown; only forward printable text here (avoids doubles).
        if (ch < 0x20 || ch == 0x7f) return;
        if (char.IsHighSurrogate(ch)) { _highSurrogate = ch; return; }
        string text = char.IsLowSurrogate(ch) && _highSurrogate != 0 ? new string([_highSurrogate, ch]) : ch.ToString();
        _highSurrogate = '\0';
        SendUser(KeyEncoder.EncodeText(text));
        ClearSelection();
    }

    private async Task PasteAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (_session is null || !view.Contains(StandardDataFormats.Text)) return;
            string text = await view.GetTextAsync();
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');
            SendUser(KeyEncoder.EncodeText(text));
            ClearSelection();
        }
        catch { }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        ClearSelection(); // row indices are viewport-relative and go stale the moment the view scrolls
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _session.Engine.ScrollViewport(delta > 0 ? -3 : 3);
        e.Handled = true;
    }
}
