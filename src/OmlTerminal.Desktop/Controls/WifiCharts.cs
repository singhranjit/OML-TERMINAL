using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OmlTerminal.Core.Wifi;

namespace OmlTerminal.Desktop.Controls;

/// <summary>One stable colour per network name, shared by the list swatches, the signal graph and the spectrum.</summary>
public static class WifiColors
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(56, 189, 248), Color.FromRgb(249, 115, 22), Color.FromRgb(52, 211, 153), Color.FromRgb(167, 139, 250),
        Color.FromRgb(251, 191, 36), Color.FromRgb(251, 113, 133), Color.FromRgb(45, 212, 191), Color.FromRgb(232, 121, 249),
        Color.FromRgb(163, 230, 53), Color.FromRgb(96, 165, 250), Color.FromRgb(244, 114, 182), Color.FromRgb(250, 204, 21),
    ];

    public static Color For(WifiNetwork n)
    {
        int h = 0;
        foreach (char c in n.Ssid.Length > 0 ? n.Ssid : n.Bssid) h = h * 31 + c;
        return Palette[Math.Abs(h % Palette.Length)];
    }

    internal static readonly IPen Axis = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)), 1);
    internal static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    internal static FormattedText Text(string s, IBrush? brush = null, double size = 10.5) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush ?? Label);
}

/// <summary>Signal level of a few access points over the last five minutes.</summary>
public sealed class WifiSignalGraph : Control
{
    private IReadOnlyList<(WifiNetwork Network, IReadOnlyList<(DateTime At, int Rssi)> Points)> _series = [];
    private static readonly IPen Target = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x4B)), 1.5, new DashStyle([4, 3], 0));

    public void SetSeries(IReadOnlyList<(WifiNetwork, IReadOnlyList<(DateTime, int)>)> series)
    {
        _series = series;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 220 || h < 50) return;
        const double left = 44, labelRoom = 120; // right margin keeps the SSID tags clear of the lines
        double right = w - labelRoom;
        double Y(int dbm) => 6 + (h - 12) * (-25 - Math.Clamp(dbm, -95, -25)) / 70.0;
        foreach (var dbm in new[] { -30, -50, -67, -80, -95 })
        {
            double y = Y(dbm);
            ctx.DrawLine(dbm == -67 ? Target : WifiColors.Axis, new Point(left, y), new Point(right, y));
            ctx.DrawText(WifiColors.Text($"{dbm}"), new Point(0, y - 8));
        }
        var now = DateTime.Now;
        var window = TimeSpan.FromMinutes(5);
        var tags = new List<(FormattedText Tag, double X, double Y)>();
        foreach (var (n, all) in _series)
        {
            var pts = all.Where(s => now - s.At <= window).ToList();
            if (pts.Count == 0) continue;
            var brush = new SolidColorBrush(WifiColors.For(n));
            var pen = new Pen(brush, 2);
            Point Pt((DateTime At, int Rssi) s) => new(left + (right - left) * (1 - (now - s.At).TotalSeconds / window.TotalSeconds), Y(s.Rssi));
            var last = Pt(pts[0]);
            if (pts.Count == 1) ctx.DrawLine(pen, last, new Point(last.X + 2, last.Y));
            for (int i = 1; i < pts.Count; i++)
            {
                var p = Pt(pts[i]);
                ctx.DrawLine(pen, last, p);
                last = p;
            }
            tags.Add((WifiColors.Text($"{n.DisplaySsid} {pts[^1].Rssi}", brush), last.X, last.Y - 7));
        }
        // Lines from APs at similar levels end at the same spot - stack their labels instead of overprinting them.
        double nextFree = double.MinValue;
        foreach (var (tag, x, y) in tags.OrderBy(t => t.Y))
        {
            double top = Math.Min(Math.Max(y, nextFree), h - 14);
            nextFree = top + 13;
            ctx.DrawText(tag, new Point(x + 6, top));
        }
    }
}

/// <summary>Each radio drawn as a hump spanning the channels it occupies, as tall as its signal - overlap is visible at a glance.</summary>
public sealed class WifiSpectrum : Control
{
    private IReadOnlyList<WifiNetwork> _nets = [];
    private int[] _channels = [1];
    private bool _is24;
    private readonly List<(Rect Box, WifiNetwork Network)> _humps = new();

    public WifiSpectrum()
    {
        PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(this);
            // Topmost (strongest, drawn last) hump under the pointer.
            var hit = _humps.LastOrDefault(x => x.Box.Contains(p));
            ToolTip.SetTip(this, hit.Network is { } n ? $"{n.DisplaySsid} ({n.Bssid})\nch {n.Channel}, {n.ChannelWidth} MHz, {n.Rssi} dBm" : null);
        };
    }

    public void Set(IEnumerable<WifiNetwork> nets, int[] channels, bool is24)
    {
        _nets = nets.OrderBy(n => n.Rssi).ToList();
        _channels = channels;
        _is24 = is24;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        _humps.Clear();
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 100) return;
        const double bottom = 22, top = 8, left = 36;
        int min = _channels.Min(), max = _channels.Max();
        double X(double ch) => left + (w - left - 10) * (ch - min) / Math.Max(1, max - min);
        double Y(int dbm) => top + (h - bottom - top) * (-25 - Math.Clamp(dbm, -95, -25)) / 70.0;
        ctx.DrawLine(WifiColors.Axis, new Point(left, h - bottom), new Point(w - 10, h - bottom));
        foreach (var dbm in new[] { -30, -50, -70, -90 }) ctx.DrawText(WifiColors.Text(dbm.ToString(), size: 10), new Point(0, Y(dbm) - 7));
        int step = _is24 ? 1 : _channels.Length > 30 ? 8 : 1;
        var dim = new SolidColorBrush(Color.FromArgb(90, 0x8B, 0x94, 0x9E));
        foreach (var ch in _channels.Where((x, i) => x >= 1 && (step == 1 || i % step == 0) && (!_is24 || x <= 14)))
            ctx.DrawText(WifiColors.Text(ch.ToString(), WifiMath.IsDfs(ch) && !_is24 ? dim : null, 10), new Point(X(ch) - 6, h - bottom + 3));

        var labels = new List<(FormattedText Label, double X, double Y)>();
        foreach (var n in _nets)
        {
            double lo = n.CoveredChannels.Min() - 2, hi = n.CoveredChannels.Max() + 2;
            var color = WifiColors.For(n);
            double baseY = h - bottom, peak = Y(n.Rssi);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(lo), baseY), true);
                g.LineTo(new Point(X(lo + (hi - lo) * 0.2), peak));
                g.LineTo(new Point(X(hi - (hi - lo) * 0.2), peak));
                g.LineTo(new Point(X(hi), baseY));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)), new Pen(new SolidColorBrush(color), 1.6), geo);
            _humps.Add((new Rect(new Point(X(lo), peak), new Point(X(hi), baseY)), n));
            var label = WifiColors.Text(n.DisplaySsid, new SolidColorBrush(color));
            labels.Add((label, X((lo + hi) / 2) - label.Width / 2, peak - 15));
        }
        // APs on the same channel at similar levels would print their names on top of each other - nudge later ones up.
        var placed = new List<Rect>();
        foreach (var (label, x, y) in labels.OrderByDescending(l => l.Y))
        {
            var r = new Rect(Math.Clamp(x, left, Math.Max(left, w - label.Width - 4)), Math.Max(0, y), label.Width, 13);
            while (placed.Any(p => p.Intersects(r)) && r.Y > 0) r = r.WithY(Math.Max(0, r.Y - 13));
            placed.Add(r);
            ctx.DrawText(label, r.TopLeft);
        }
        if (_nets.Count == 0) ctx.DrawText(WifiColors.Text("No networks heard in this band.", size: 13), new Point(left + 10, h / 2 - 10));
    }
}
