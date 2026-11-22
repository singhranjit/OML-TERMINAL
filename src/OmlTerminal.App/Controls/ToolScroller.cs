using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OmlTerminal.App.Controls;

/// <summary>Hosts a tool tab so it can never render clipped or squashed: the tool always gets at least
/// <see cref="MinContentWidth"/> x <see cref="MinContentHeight"/> DIPs (the size the layouts were designed for) and,
/// when the tab area is smaller than that, the user scrolls - like any desktop app - instead of fields overlapping
/// and buttons falling off the edge. When the tab area is larger the tool simply fills it.</summary>
public sealed class ToolScroller : UserControl
{
    public const double MinContentWidth = 960;
    public const double MinContentHeight = 520;

    public UserControl Tool { get; }
    private readonly ScrollViewer _sv = new();

    public ToolScroller(UserControl tool)
    {
        Tool = tool;
        _sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _sv.HorizontalScrollMode = ScrollMode.Auto;
        _sv.VerticalScrollMode = ScrollMode.Auto;
        _sv.ZoomMode = ZoomMode.Disabled;
        tool.HorizontalAlignment = HorizontalAlignment.Left;
        tool.VerticalAlignment = VerticalAlignment.Top;
        _sv.Content = tool;
        Content = _sv;
        SizeChanged += (_, _) => Fit();
        Loaded += (_, _) => Fit();
    }

    private void Fit()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double nw = Math.Max(MinContentWidth, w), nh = Math.Max(MinContentHeight, h);
        if (Math.Abs(Tool.Width - nw) > 0.5 || double.IsNaN(Tool.Width)) Tool.Width = nw;
        if (Math.Abs(Tool.Height - nh) > 0.5 || double.IsNaN(Tool.Height)) Tool.Height = nh;
    }
}
