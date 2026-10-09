using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OmlTerminal.Desktop.Controls;

/// <summary>A latency line with lost samples drawn as red bars - the sparkline on each Ping Monitor tile (compact) and
/// the large per-host graph (with axis labels). Drawn directly in Render, so hundreds of tiles redrawing twice a
/// second cost almost nothing.</summary>
public sealed class LatencyChart : Control
{
    private IReadOnlyList<(DateTime At, double? Rtt)> _samples = [];
    private static readonly IBrush Lost = new SolidColorBrush(Color.FromArgb(140, 0xFB, 0x71, 0x85));
    private static readonly IPen Axis = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    public IBrush Stroke { get; set; } = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
    /// <summary>Compact sparklines use a fixed slot width (the latest N pings fill from the right).</summary>
    public int Slots { get; set; } = 120;
    public bool ShowAxis { get; set; }
    /// <summary>Optional reference level (e.g. the hop's average) drawn as a dashed amber line.</summary>
    public double Mean { get; set; } = double.NaN;
    private static readonly IPen MeanPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0xFB, 0xBF, 0x24)), 1, new DashStyle([4, 3], 0));

    public void SetSamples(IReadOnlyList<(DateTime At, double? Rtt)> samples, IBrush? stroke = null)
    {
        _samples = samples;
        if (stroke is not null) Stroke = stroke;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10) return;
        double left = ShowAxis ? 44 : 0;
        var s = _samples;
        int slots = ShowAxis ? Math.Max(60, (int)((w - left) / 1.5)) : Slots;
        if (s.Count > slots) s = s.Skip(s.Count - slots).ToList();
        double max = Math.Max(5, s.Where(x => x.Rtt is not null).Select(x => x.Rtt!.Value).DefaultIfEmpty(5).Max() * 1.15);
        double Y(double v) => 2 + (h - 4) * (1 - v / max);

        if (ShowAxis)
        {
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                double y = Y(max * f);
                ctx.DrawLine(Axis, new Point(left, y), new Point(w, y));
                ctx.DrawText(Text($"{max * f:0}"), new Point(4, Math.Clamp(y - 8, 0, h - 14)));
            }
            if (s.Count > 0) ctx.DrawText(Text(s[0].At.ToString("HH:mm:ss")), new Point(left + 2, h - 14));
        }
        if (s.Count == 0) return;

        // A short run fills the graph from the left (axis view) or right-aligns into its slots (sparkline).
        double step = ShowAxis ? (w - left) / Math.Max(s.Count - 1, 60) : (w - left) / slots;
        double x0 = ShowAxis ? left : w - s.Count * step;
        var pen = new Pen(Stroke, 1.4);
        StreamGeometry? geo = null;
        StreamGeometryContext? g = null;
        void Flush() { if (g is not null) { g.EndFigure(false); g.Dispose(); ctx.DrawGeometry(null, pen, geo!); g = null; geo = null; } }
        for (int i = 0; i < s.Count; i++)
        {
            double x = x0 + i * step;
            if (s[i].Rtt is { } r)
            {
                var p = new Point(x, Y(r));
                if (g is null) { geo = new StreamGeometry(); g = geo.Open(); g.BeginFigure(p, false); }
                else g.LineTo(p);
            }
            else
            {
                Flush();
                ctx.FillRectangle(Lost, new Rect(x, 0, Math.Max(1.5, step), h));
            }
        }
        Flush();
        if (!double.IsNaN(Mean) && Mean <= max) ctx.DrawLine(MeanPen, new Point(left, Y(Mean)), new Point(w, Y(Mean)));
    }

    private static FormattedText Text(string s) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, Label);
}
