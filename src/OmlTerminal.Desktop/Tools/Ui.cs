using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using OmlTerminal.Desktop.Controls;

namespace OmlTerminal.Desktop.Tools;

/// <summary>Small builders so tool views are written as plain C# with one consistent look - the Linux/macOS
/// counterpart of the Windows app's ToolCardStyle / SectionLabelStyle / MonoOutputStyle resources.</summary>
public static class Ui
{
    public static readonly FontFamily Mono = new(TerminalView.DefaultFontFamily);

    public static readonly IBrush Mint = Solid(0x34D399), Rose = Solid(0xFB7185), Amber = Solid(0xFBBF24),
        Violet = Solid(0xA78BFA), Sky = Solid(0x38BDF8), Orange = Solid(0xFB923C), Muted = Solid(0x8B949E),
        CardBack = Solid(0x15181D), CardBorder = Solid(0x262B33);

    public static IBrush Solid(int rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)).ToImmutable();

    public static TextBlock Title(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold };

    public static TextBlock Subtitle(string text) => new() { Text = text, FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Section(string text) => new() { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted };

    public static TextBlock Text(string text = "", double size = 13, bool mono = false, IBrush? color = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontFamily = mono ? Mono : FontFamily.Default };
        if (color is not null) t.Foreground = color; // a null Foreground hides the text rather than inheriting
        return t;
    }

    public static SelectableTextBlock Selectable(string text = "", double size = 13, bool mono = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontFamily = mono ? Mono : FontFamily.Default,
    };

    public static Border Card(Control child) => new()
    {
        Child = child, Background = CardBack, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
    };

    /// <summary>A control with a small caption above it.</summary>
    public static StackPanel Field(string header, Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Stretch;
        return new StackPanel { Spacing = 4, Children = { Section(header), c } };
    }

    public static TextBox Input(string text = "", string placeholder = "", bool mono = false) => new()
    {
        Text = text, PlaceholderText = placeholder, FontFamily = mono ? Mono : FontFamily.Default,
    };

    /// <summary>Read-only monospace text area for command-style output.</summary>
    public static TextBox Output(double height = double.NaN, string text = "") => new()
    {
        Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = Mono, FontSize = 12.5, Height = height,
        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    public static TextBox MultiInput(string text = "", double height = 120, string placeholder = "") => new()
    {
        Text = text, AcceptsReturn = true, FontFamily = Mono, FontSize = 12.5, Height = height, PlaceholderText = placeholder,
        TextWrapping = TextWrapping.NoWrap,
    };

    public static Button Button(string text, Action onClick, bool accent = false, string? tip = null)
    {
        var b = new Button { Content = text, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static CheckBox Check(string text, bool isChecked = false) => new() { Content = text, IsChecked = isChecked };

    public static ComboBox Combo(IEnumerable items, int selectedIndex = 0) => new()
    {
        ItemsSource = items, SelectedIndex = selectedIndex, HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public static NumericUpDown Number(double value, double min, double max, double increment = 1) => new()
    {
        Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)increment,
        FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch, ShowButtonSpinner = false,
    };

    public static int IntValue(NumericUpDown n, int fallback) => n.Value is { } v ? (int)v : fallback;

    public static StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) { c.VerticalAlignment = VerticalAlignment.Center; p.Children.Add(c); }
        return p;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A grid with the given column widths ("*,120,Auto") and one child per column, 12 px apart.
    /// Children default to bottom alignment so buttons line up with captioned fields.</summary>
    public static Grid Columns(string definitions, params Control[] children)
    {
        var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(definitions), ColumnSpacing = 12 };
        for (int i = 0; i < children.Length; i++)
        {
            var c = children[i];
            Grid.SetColumn(c, i);
            if (c is Avalonia.Controls.Button or CheckBox) c.VerticalAlignment = VerticalAlignment.Bottom;
            g.Children.Add(c);
        }
        return g;
    }

    /// <summary>A tool page: header and controls on top, an optional "fill" control taking the remaining height.</summary>
    public static Control Page(string title, string subtitle, Control? fill, params Control[] top)
    {
        var dock = new DockPanel { Margin = new Thickness(24, 20, 24, 16), LastChildFill = true };
        var header = Stack(12, [Stack(2, Title(title), Subtitle(subtitle)), .. top]);
        header.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        if (fill is not null) dock.Children.Add(fill);
        return dock;
    }

    /// <summary>A tool page that scrolls as a whole (for forms and card layouts).</summary>
    public static Control ScrollPage(string title, string subtitle, params Control[] body) =>
        new ScrollViewer
        {
            Content = Stack(14, [Stack(2, Title(title), Subtitle(subtitle)), .. body]),
            Padding = new Thickness(24, 20, 24, 20),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

    /// <summary>Coloured message strip (the WinUI InfoBar's job).</summary>
    public static Border Banner(out TextBlock text, IBrush? accent = null)
    {
        text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        return new Border
        {
            Child = text, IsVisible = false, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = accent ?? Amber, Background = Solid(0x1E2228),
        };
    }

    public static void Show(Border banner, TextBlock text, string? message, IBrush? accent = null)
    {
        banner.IsVisible = !string.IsNullOrEmpty(message);
        text.Text = message ?? "";
        if (accent is not null) banner.BorderBrush = accent;
    }

    // ---------- tables ----------

    public static DataGrid Table(params DataGridColumn[] columns)
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true, CanUserSortColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.None, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended, FontSize = 12.5, RowHeight = 26,
            BorderThickness = new Thickness(1), BorderBrush = CardBorder, Background = CardBack,
        };
        foreach (var c in columns) g.Columns.Add(c);
        // Right-click acts on the row under the pointer (keeping a multi-selection that already includes it).
        g.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(g).Properties.IsRightButtonPressed) return;
            if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is { } hit && !g.SelectedItems.Contains(hit))
                g.SelectedItem = hit;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return g;
    }

    /// <summary>A text column computed from the row object (no reflection bindings), optionally coloured, sortable by
    /// <paramref name="sortKey"/> (defaults to the text).</summary>
    public static DataGridColumn Col<T>(string header, Func<T, string> text, double width = double.NaN, Func<T, IBrush?>? color = null,
        bool mono = false, Func<T, IComparable?>? sortKey = null, bool fill = false)
    {
        var col = new DataGridTemplateColumn
        {
            Header = header,
            Width = fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : double.IsNaN(width) ? DataGridLength.Auto : new DataGridLength(width),
            CellTemplate = new FuncDataTemplate<T>((_, _) =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = mono ? Mono : FontFamily.Default,
                };
                LiveRow? watched = null;
                void Fill()
                {
                    if (tb.DataContext is not T row) { tb.Text = ""; return; }
                    tb.Text = text(row);
                    if (color is not null && color(row) is { } brush) tb.Foreground = brush;
                }
                void OnRowChanged() => Fill();
                tb.DataContextChanged += (_, _) =>
                {
                    // Live rows (updated in place, e.g. a running trace) re-read themselves when they change.
                    if (watched is not null) watched.Changed -= OnRowChanged;
                    watched = tb.DataContext as LiveRow;
                    if (watched is not null) watched.Changed += OnRowChanged;
                    Fill();
                };
                return tb;
            }),
            CustomSortComparer = new RowComparer<T>(sortKey ?? (r => text(r))),
            SortMemberPath = header,
        };
        return col;
    }

    /// <summary>A custom cell (bars, icons) built once per cell element; <paramref name="update"/> runs whenever the
    /// cell shows another row, or the same live row changes.</summary>
    public static DataGridColumn Custom<T, TCell>(string header, double width, Func<TCell> create, Action<TCell, T> update) where TCell : Control
    {
        return new DataGridTemplateColumn
        {
            Header = header,
            // 0 = take the remaining width
            Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star)
                : new DataGridLength(width, double.IsNaN(width) ? DataGridLengthUnitType.Auto : DataGridLengthUnitType.Pixel),
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<T>((_, _) =>
            {
                var cell = create();
                LiveRow? watched = null;
                void Fill() { if (cell.DataContext is T row) update(cell, row); }
                void OnRowChanged() => Fill();
                cell.DataContextChanged += (_, _) =>
                {
                    if (watched is not null) watched.Changed -= OnRowChanged;
                    watched = cell.DataContext as LiveRow;
                    if (watched is not null) watched.Changed += OnRowChanged;
                    Fill();
                };
                return cell;
            }),
        };
    }

    private sealed class RowComparer<T>(Func<T, IComparable?> key) : IComparer
    {
        public int Compare(object? x, object? y) =>
            x is T a && y is T b ? Comparer<IComparable?>.Default.Compare(key(a), key(b)) : 0;
    }
}

/// <summary>A table row whose values change in place (a running trace, a live monitor): call Touch() after updating and
/// every cell showing it re-reads - so selection and scroll position survive refreshes.</summary>
public abstract class LiveRow
{
    public event Action? Changed;
    public void Touch() => Changed?.Invoke();
}
