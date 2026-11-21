using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace OmlTerminal.App.Controls;

/// <summary>A thin vertical drag handle between two columns (the MobaXterm / SecureCRT session-panel divider):
/// west-east resize cursor, reports horizontal drag deltas, double-click raises <see cref="Toggled"/>.</summary>
public sealed class ColumnSplitter : Grid
{
    private readonly Border _line = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(1), Opacity = Idle };
    private const double Idle = 0.22;
    private double _lastX;
    private bool _dragging;

    /// <summary>Raised with the horizontal change in DIPs while dragging.</summary>
    public event Action<double>? Dragged;
    /// <summary>Raised when the drag finishes (persist the new width here).</summary>
    public event Action? DragCompleted;
    /// <summary>Raised on double-click: collapse/expand the panel.</summary>
    public event Action? Toggled;

    public ColumnSplitter()
    {
        _line.Background = (Brush)Application.Current.Resources["OmlSkyBrush"];
        // A transparent (not null) background so the whole strip is hit-testable, not just the 2 px line.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_line);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerEntered += (_, _) => { if (!_dragging) _line.Opacity = 0.55; };
        PointerExited += (_, _) => { if (!_dragging) _line.Opacity = Idle; };
        PointerPressed += (_, e) =>
        {
            _dragging = true; _lastX = e.GetCurrentPoint(null).Position.X; _line.Opacity = 1;
            CapturePointer(e.Pointer); e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            double x = e.GetCurrentPoint(null).Position.X;
            double d = x - _lastX; _lastX = x;
            if (d != 0) Dragged?.Invoke(d);
        };
        PointerReleased += (_, e) => End(e.Pointer);
        PointerCanceled += (_, e) => End(e.Pointer);
        PointerCaptureLost += (_, _) => { _dragging = false; _line.Opacity = Idle; };
        DoubleTapped += (_, e) => { Toggled?.Invoke(); e.Handled = true; };
    }

    private void End(Pointer p)
    {
        if (!_dragging) return;
        _dragging = false; _line.Opacity = Idle; ReleasePointerCapture(p);
        DragCompleted?.Invoke();
    }
}
