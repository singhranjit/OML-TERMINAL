using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OmlTerminal.App.ViewModels;

namespace OmlTerminal.App.Controls;

/// <summary>One terminal inside a split tab: its session, its control, and the framed header around it.</summary>
public sealed class TerminalPane
{
    public TerminalTabViewModel Tab { get; }
    public TerminalControl Term { get; }
    internal Border Frame { get; }
    private readonly TextBlock _title;
    private readonly Ellipse _dot;
    private readonly Grid _header;

    /// <summary>What the pane header (and, for a single-pane tab, the tab itself) currently shows.</summary>
    public string Title => _title.Text;

    internal TerminalPane(TerminalTabViewModel tab, TerminalControl term, SplitTabView owner)
    {
        Tab = tab;
        Term = term;
        _title = new TextBlock { Text = tab.Profile.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Fill = (Brush)Application.Current.Resources["StatusConnectingBrush"] };

        var header = _header = new Grid { Height = 26, Padding = new Thickness(8, 0, 2, 0), ColumnSpacing = 6, Background = (Brush)Application.Current.Resources["CardBrush"] };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(HeaderButton("", "Split right", () => owner.RaiseSplit(this, Orientation.Horizontal)));
        buttons.Children.Add(HeaderButton("", "Split down", () => owner.RaiseSplit(this, Orientation.Vertical)));
        buttons.Children.Add(HeaderButton("", "Close pane", () => owner.RaiseClose(this)));
        Grid.SetColumn(buttons, 2);
        header.Children.Add(_dot);
        header.Children.Add(_title);
        header.Children.Add(buttons);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(term, 1);
        body.Children.Add(header);
        body.Children.Add(term);

        Frame = new Border { Child = body, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(2) };
        header.PointerPressed += (_, _) => term.Focus(FocusState.Pointer);
    }

    private static Button HeaderButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 10 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 4, 7, 4),
            IsTabStop = false, // keyboard focus belongs in the terminal; Enter must never "click" Close pane
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public void SetTitle(string text) => _title.Text = text;

    /// <summary>A lone pane looks exactly like a plain terminal tab: no header, no frame.</summary>
    internal void SetChrome(bool visible)
    {
        _header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Frame.BorderThickness = new Thickness(visible ? 1 : 0);
        Frame.Margin = new Thickness(visible ? 2 : 0);
    }

    public void SetState(bool connected, bool failed) =>
        _dot.Fill = (Brush)Application.Current.Resources[connected ? "StatusConnectedBrush" : failed ? "StatusErrorBrush" : "StatusConnectingBrush"];

    internal void SetActive(bool active, bool sync) =>
        Frame.BorderBrush = (Brush)Application.Current.Resources[sync ? "OmlOrangeBrush" : active ? "OmlSkyBrush" : "HairlineBrush"];
}

/// <summary>
/// Content of every terminal tab: one terminal, or several in a recursive split layout (Windows Terminal / tmux style). Panes can be split
/// right or down any number of times, resized by dragging the gutters, and optionally synchronized so keystrokes in
/// one pane are typed into all of them - the fastest way to run the same change on a pair of HA firewalls.
/// </summary>
public sealed class SplitTabView : UserControl
{
    private abstract class Node;
    private sealed class Leaf(TerminalPane pane) : Node { public TerminalPane Pane { get; } = pane; }
    private sealed class Branch(Orientation o, Node a, Node b) : Node
    {
        public Orientation Orientation { get; } = o;
        public Node A { get; set; } = a;
        public Node B { get; set; } = b;
        public double Ratio { get; set; } = 0.5;
    }

    private Node _root;
    private bool _syncInput;
    private readonly Grid _host = new();

    public TerminalPane? ActivePane { get; private set; }

    public event Action? ActivePaneChanged;
    public event Action<TerminalPane, Orientation>? SplitRequested;
    public event Action<TerminalPane>? PaneCloseRequested;

    public SplitTabView()
    {
        _root = null!;
        Content = _host;
    }

    public IReadOnlyList<TerminalPane> Panes
    {
        get
        {
            var list = new List<TerminalPane>();
            void Walk(Node n) { if (n is Leaf l) list.Add(l.Pane); else if (n is Branch b) { Walk(b.A); Walk(b.B); } }
            if (_root is not null) Walk(_root);
            return list;
        }
    }

    public bool SyncInput
    {
        get => _syncInput;
        set { _syncInput = value; RefreshHighlights(); }
    }

    public TerminalPane CreatePane(TerminalTabViewModel tab, TerminalControl term)
    {
        var pane = new TerminalPane(tab, term, this);
        term.GotFocus += (_, _) => SetActive(pane);
        term.UserInput += bytes => Mirror(pane, bytes);
        return pane;
    }

    public void SetFirst(TerminalPane pane)
    {
        _root = new Leaf(pane);
        ActivePane = pane;
        Rebuild();
    }

    /// <summary>Puts newPane beside (Horizontal) or below (Vertical) target, halving target's space.</summary>
    public void Split(TerminalPane target, TerminalPane newPane, Orientation orientation)
    {
        _root = Replace(_root, target, leaf => new Branch(orientation, leaf, new Leaf(newPane)));
        ActivePane = newPane;
        Rebuild();
    }

    /// <summary>Removes a pane; its sibling takes the freed space. Returns how many panes remain.</summary>
    public int Remove(TerminalPane pane)
    {
        Node? Prune(Node n)
        {
            if (n is Leaf l) return ReferenceEquals(l.Pane, pane) ? null : n;
            var b = (Branch)n;
            var a = Prune(b.A);
            var c = Prune(b.B);
            if (a is null) return c;
            if (c is null) return a;
            b.A = a; b.B = c;
            return b;
        }
        Detach(pane.Frame);
        _root = Prune(_root)!;
        var remaining = Panes;
        if (ReferenceEquals(ActivePane, pane)) ActivePane = remaining.FirstOrDefault();
        if (remaining.Count > 0) Rebuild();
        return remaining.Count;
    }

    private static Node Replace(Node n, TerminalPane target, Func<Leaf, Node> make) => n switch
    {
        Leaf l when ReferenceEquals(l.Pane, target) => make(l),
        Branch b => new Branch(b.Orientation, Replace(b.A, target, make), Replace(b.B, target, make)) { Ratio = b.Ratio },
        _ => n,
    };

    private void SetActive(TerminalPane pane)
    {
        if (ReferenceEquals(ActivePane, pane)) return;
        ActivePane = pane;
        RefreshHighlights();
        ActivePaneChanged?.Invoke();
    }

    private void RefreshHighlights()
    {
        var panes = Panes;
        foreach (var p in panes)
        {
            p.SetChrome(panes.Count > 1);
            p.SetActive(ReferenceEquals(p, ActivePane), _syncInput);
        }
    }

    private void Mirror(TerminalPane source, byte[] bytes)
    {
        if (!_syncInput) return;
        foreach (var p in Panes)
            if (!ReferenceEquals(p, source) && p.Tab.Session.IsConnected) p.Tab.Session.Send(bytes);
    }

    internal void RaiseSplit(TerminalPane pane, Orientation o) => SplitRequested?.Invoke(pane, o);
    internal void RaiseClose(TerminalPane pane) => PaneCloseRequested?.Invoke(pane);

    private static void Detach(FrameworkElement e)
    {
        if (VisualTreeHelper.GetParent(e) is Panel p) p.Children.Remove(e);
    }

    private void Rebuild()
    {
        foreach (var p in Panes) Detach(p.Frame);
        _host.Children.Clear();
        _host.Children.Add(Build(_root));
        RefreshHighlights();
        ActivePane?.Term.Focus(FocusState.Programmatic);
    }

    private FrameworkElement Build(Node n)
    {
        if (n is Leaf l) return l.Pane.Frame;
        var b = (Branch)n;
        var grid = new Grid();
        bool horizontal = b.Orientation == Orientation.Horizontal;
        var first = Build(b.A);
        var second = Build(b.B);
        var gutter = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };

        void Layout()
        {
            var defs = new[]
            {
                new GridLength(b.Ratio, GridUnitType.Star), new GridLength(6), new GridLength(1 - b.Ratio, GridUnitType.Star),
            };
            if (horizontal)
            {
                if (grid.ColumnDefinitions.Count == 0) foreach (var _ in defs) grid.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < 3; i++) grid.ColumnDefinitions[i].Width = defs[i];
            }
            else
            {
                if (grid.RowDefinitions.Count == 0) foreach (var _ in defs) grid.RowDefinitions.Add(new RowDefinition());
                for (int i = 0; i < 3; i++) grid.RowDefinitions[i].Height = defs[i];
            }
        }
        Layout();
        if (horizontal) { Grid.SetColumn(gutter, 1); Grid.SetColumn(second, 2); }
        else { Grid.SetRow(gutter, 1); Grid.SetRow(second, 2); }
        grid.Children.Add(first);
        grid.Children.Add(gutter);
        grid.Children.Add(second);

        var cursor = InputSystemCursor.Create(horizontal ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        bool dragging = false;
        gutter.PointerEntered += (_, _) => ProtectedCursor = cursor;
        gutter.PointerExited += (_, _) => { if (!dragging) ProtectedCursor = null; };
        gutter.PointerPressed += (_, e) => { dragging = gutter.CapturePointer(e.Pointer); e.Handled = true; };
        gutter.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pt = e.GetCurrentPoint(grid).Position;
            double total = horizontal ? grid.ActualWidth : grid.ActualHeight;
            if (total <= 0) return;
            b.Ratio = Math.Clamp((horizontal ? pt.X : pt.Y) / total, 0.12, 0.88);
            Layout();
        };
        void EndDrag(object s, PointerRoutedEventArgs e) { dragging = false; gutter.ReleasePointerCaptures(); ProtectedCursor = null; }
        gutter.PointerReleased += EndDrag;
        gutter.PointerCaptureLost += EndDrag;
        return grid;
    }
}
