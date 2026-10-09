using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OmlTerminal.Core.Snmp;

namespace OmlTerminal.Desktop.Controls;

/// <summary>MRTG's look: incoming as a filled area, outgoing as a line, the interface speed marked, time along the bottom.
/// Gaps (app closed, device unreachable) stay empty rather than being bridged.</summary>
public sealed class TrafficChart : Control
{
    private IReadOnlyList<TrafficPoint> _points = [];
    private TrafficPeriod _period = TrafficPeriod.Daily;
    private long _speed;

    private static readonly IBrush InFill = new SolidColorBrush(Color.FromArgb(115, 0x34, 0xD3, 0x99));
    private static readonly IPen InPen = new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), 1.2);
    private static readonly IPen OutPen = new Pen(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), 1.8);
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)), 1);
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.FromArgb(90, 0x26, 0x2B, 0x33)), 1);
    private static readonly IPen SpeedPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0xFB, 0x71, 0x85)), 1, new DashStyle([4, 3], 0));
    private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    /// <summary>Compact = the overview card's thumbnail: no axis labels.</summary>
    public bool Compact { get; set; }

    public void Set(IReadOnlyList<TrafficPoint> points, TrafficPeriod period, long speedBps)
    {
        (_points, _period, _speed) = (points, period, speedBps);
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 30) return;
        double left = Compact ? 0 : 64, bottom = Compact ? 0 : 18;
        double plotW = w - left, plotH = h - bottom;
        var end = DateTime.Now;
        var start = end - TrafficArchive.Span(_period);
        double X(DateTime t) => left + plotW * (t - start).TotalSeconds / (end - start).TotalSeconds;
        double peak = _points.Count == 0 ? 1000 : _points.Max(p => Math.Max(p.In, p.Out));
        double max = NiceCeiling(Math.Max(peak * 1.1, 1000));
        double Y(double v) => plotH - plotH * Math.Min(v, max) / max;

        for (int k = 0; k <= 4; k++)
        {
            double y = plotH * k / 4.0;
            ctx.DrawLine(Grid, new Point(left, y), new Point(w, y));
            if (!Compact) ctx.DrawText(Text(TrafficArchive.Bits(max * (4 - k) / 4.0)), new Point(0, Math.Clamp(y - 7, 0, plotH - 12)));
        }
        if (!Compact)
            foreach (var (at, label) in Ticks(start, end))
            {
                double x = X(at);
                ctx.DrawLine(Tick, new Point(x, 0), new Point(x, plotH));
                ctx.DrawText(Text(label), new Point(x - 14, plotH + 2));
            }
        if (_points.Count == 0) return;

        double gap = _period switch { TrafficPeriod.Live or TrafficPeriod.Daily => 600, TrafficPeriod.Weekly => 3 * 1800, TrafficPeriod.Monthly => 3 * 7200, _ => 3 * 86400 };
        using (ctx.PushClip(new Rect(left, 0, plotW, plotH)))
        {
            foreach (var run in Runs(_points, gap))
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(X(run[0].At), plotH), true);
                    foreach (var p in run) g.LineTo(new Point(X(p.At), Y(p.In)));
                    g.LineTo(new Point(X(run[^1].At), plotH));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(InFill, null, area);
                ctx.DrawGeometry(null, InPen, Line(run, p => new Point(X(p.At), Y(p.In))));
                ctx.DrawGeometry(null, OutPen, Line(run, p => new Point(X(p.At), Y(p.Out))));
            }
        }
        if (_speed > 0 && _speed <= max) ctx.DrawLine(SpeedPen, new Point(left, Y(_speed)), new Point(w, Y(_speed)));
    }

    private static StreamGeometry Line(List<TrafficPoint> run, Func<TrafficPoint, Point> at)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(at(run[0]), false);
        foreach (var p in run.Skip(1)) g.LineTo(at(p));
        g.EndFigure(false);
        return geo;
    }

    /// <summary>Time ticks: 5 minutes live, every 3 hours daily, days weekly, Mondays monthly, months yearly.</summary>
    private IEnumerable<(DateTime At, string Label)> Ticks(DateTime start, DateTime end)
    {
        switch (_period)
        {
            case TrafficPeriod.Live:
                for (var t = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute / 5 * 5, 0).AddMinutes(5); t < end; t = t.AddMinutes(5)) yield return (t, t.ToString("HH:mm"));
                break;
            case TrafficPeriod.Daily:
                for (var t = start.Date.AddHours(start.Hour + 1); t < end; t = t.AddHours(1)) if (t.Hour % 3 == 0) yield return (t, t.ToString("HH:00"));
                break;
            case TrafficPeriod.Weekly:
                for (var t = start.Date.AddDays(1); t < end; t = t.AddDays(1)) yield return (t, t.ToString("ddd"));
                break;
            case TrafficPeriod.Monthly:
                for (var t = start.Date.AddDays(1); t < end; t = t.AddDays(1)) if (t.DayOfWeek == DayOfWeek.Monday) yield return (t, t.ToString("dd MMM"));
                break;
            default:
                for (var t = new DateTime(start.Year, start.Month, 1).AddMonths(1); t < end; t = t.AddMonths(1)) yield return (t, t.ToString("MMM"));
                break;
        }
    }

    private static List<List<TrafficPoint>> Runs(IReadOnlyList<TrafficPoint> pts, double gapSeconds)
    {
        var runs = new List<List<TrafficPoint>>();
        foreach (var p in pts)
        {
            if (runs.Count == 0 || p.Time - runs[^1][^1].Time > gapSeconds) runs.Add([]);
            runs[^1].Add(p);
        }
        return runs;
    }

    private static double NiceCeiling(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 4, 5, 8, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }

    private static FormattedText Text(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, Label);
}
