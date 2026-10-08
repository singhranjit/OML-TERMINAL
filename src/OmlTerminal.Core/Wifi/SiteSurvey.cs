using System.Text;
using System.Text.Json;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Wifi;

public sealed record SurveyReading(string Ssid, string Bssid, int Rssi, int Channel, WifiBand Band);

/// <summary>A spot on the floor plan where a scan was taken. X/Y are 0-1 fractions of the image size, so a survey
/// keeps working if the plan is shown at a different zoom.</summary>
public sealed class SurveyPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public List<SurveyReading> Readings { get; set; } = new();
}

public enum HeatmapMetric
{
    /// <summary>Strongest signal from the chosen network (or any network).</summary>
    Signal,
    /// <summary>Second-strongest access point of the chosen network - where roaming has somewhere to go (voice/VoIP needs ≥ -67 dBm).</summary>
    SecondaryCoverage,
    /// <summary>How many access points are heard above -80 dBm - co-channel interference pressure.</summary>
    NetworksHeard,
    /// <summary>Signal-to-interference: chosen network's signal minus the strongest other radio on the same channel.</summary>
    SignalToInterference,
}

public sealed class SurveyProject
{
    public string Name { get; set; } = "Site survey";
    /// <summary>The floor plan image, embedded so the survey file is self-contained.</summary>
    public string FloorPlanBase64 { get; set; } = "";
    public string FloorPlanFileName { get; set; } = "";
    /// <summary>Optional scale: metres per full image width, set by the user from a known distance.</summary>
    public double? WidthMeters { get; set; }
    public List<SurveyPoint> Points { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> Ssids => Points.SelectMany(p => p.Readings).Where(r => r.Ssid.Length > 0).Select(r => r.Ssid)
        .GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonFile.Options));

    public static SurveyProject Load(string path) =>
        JsonSerializer.Deserialize<SurveyProject>(File.ReadAllText(path), JsonFile.Options) ?? throw new InvalidDataException("Not a survey file.");

    public string ToCsv()
    {
        var sb = new StringBuilder("Point,X,Y,Time,SSID,BSSID,RSSI dBm,Channel,Band\n");
        for (int i = 0; i < Points.Count; i++)
            foreach (var r in Points[i].Readings.OrderByDescending(r => r.Rssi))
                sb.AppendLine($"{i + 1},{Points[i].X:0.000},{Points[i].Y:0.000},{Points[i].Time:yyyy-MM-dd HH:mm:ss},\"{r.Ssid.Replace("\"", "\"\"")}\",{r.Bssid},{r.Rssi},{r.Channel},{(r.Band == WifiBand.Band2_4 ? "2.4" : r.Band == WifiBand.Band5 ? "5" : "6")}");
        return sb.ToString();
    }
}

public static class Heatmap
{
    /// <summary>The metric's value at one survey point (dBm for signal metrics, a count for NetworksHeard), or null
    /// when the chosen network wasn't heard there at all.</summary>
    public static double? Value(SurveyPoint p, HeatmapMetric metric, string? ssid)
    {
        var mine = p.Readings.Where(r => ssid is null || r.Ssid == ssid).OrderByDescending(r => r.Rssi).ToList();
        switch (metric)
        {
            case HeatmapMetric.Signal:
                return mine.Count > 0 ? mine[0].Rssi : null;
            case HeatmapMetric.SecondaryCoverage:
                return mine.Count > 1 ? mine[1].Rssi : mine.Count == 1 ? -95 : null;
            case HeatmapMetric.NetworksHeard:
                return p.Readings.Count(r => r.Rssi >= -80);
            case HeatmapMetric.SignalToInterference:
                if (mine.Count == 0) return null;
                var best = mine[0];
                var rival = p.Readings.Where(r => r.Bssid != best.Bssid && r.Channel == best.Channel && r.Band == best.Band && (ssid is null || r.Ssid != ssid))
                    .Select(r => (int?)r.Rssi).Max();
                return rival is null ? 40 : best.Rssi - rival.Value;
            default:
                return null;
        }
    }

    /// <summary>Inverse-distance-weighted interpolation onto a width×height grid (0-1 coordinates). Cells farther than
    /// <paramref name="reach"/> from every sample are NaN - the map shouldn't invent coverage for rooms nobody walked.</summary>
    public static double[,] Interpolate(IReadOnlyList<(double X, double Y, double V)> samples, int width, int height, double aspect = 1, double reach = 0.18, double power = 2)
    {
        var grid = new double[height, width];
        double reach2 = reach * reach;
        for (int gy = 0; gy < height; gy++)
            for (int gx = 0; gx < width; gx++)
            {
                double x = (gx + 0.5) / width, y = (gy + 0.5) / height;
                double num = 0, den = 0, nearest = double.MaxValue, exact = double.NaN;
                foreach (var (sx, sy, v) in samples)
                {
                    double dx = (x - sx) * aspect, dy = y - sy;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < 1e-9) { exact = v; break; }
                    nearest = Math.Min(nearest, d2);
                    double w = 1 / Math.Pow(d2, power / 2);
                    num += w * v;
                    den += w;
                }
                grid[gy, gx] = !double.IsNaN(exact) ? exact : nearest > reach2 || den == 0 ? double.NaN : num / den;
            }
        return grid;
    }

    /// <summary>ARGB colour for a value: green (excellent) → yellow → orange → red (unusable), transparent for NaN.
    /// Signal metrics use dBm thresholds; NetworksHeard is inverted (more radios = worse).</summary>
    public static uint Color(double v, HeatmapMetric metric)
    {
        if (double.IsNaN(v)) return 0;
        double t = metric switch
        {
            HeatmapMetric.NetworksHeard => Math.Clamp(1 - (v - 1) / 14.0, 0, 1),
            HeatmapMetric.SignalToInterference => Math.Clamp(v / 30.0, 0, 1),
            _ => Math.Clamp((v + 90) / 45.0, 0, 1), // -90 dBm → 0, -45 dBm → 1
        };
        (byte r, byte g, byte b) = t switch
        {
            >= 0.75 => Lerp((52, 211, 153), (22, 163, 74), (t - 0.75) / 0.25),
            >= 0.5 => Lerp((250, 204, 21), (52, 211, 153), (t - 0.5) / 0.25),
            >= 0.25 => Lerp((249, 115, 22), (250, 204, 21), (t - 0.25) / 0.25),
            _ => Lerp((153, 27, 27), (239, 68, 68), t / 0.25),
        };
        return 0xB0000000u | (uint)r << 16 | (uint)g << 8 | b;
    }

    private static (byte, byte, byte) Lerp((int R, int G, int B) a, (int R, int G, int B) b, double t) =>
        ((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Share of the walked area (non-NaN cells) where the value meets the threshold - "92% of the floor has ≥ -67 dBm".</summary>
    public static double Coverage(double[,] grid, double threshold, bool higherIsBetter = true)
    {
        int total = 0, ok = 0;
        foreach (var v in grid)
        {
            if (double.IsNaN(v)) continue;
            total++;
            if (higherIsBetter ? v >= threshold : v <= threshold) ok++;
        }
        return total == 0 ? 0 : ok * 100.0 / total;
    }
}
