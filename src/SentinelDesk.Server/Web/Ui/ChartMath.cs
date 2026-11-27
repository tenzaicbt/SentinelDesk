using System.Globalization;
using System.Text;

namespace SentinelDesk.Server.Web.Ui;

/// <summary>Series colors from the validated palette tokens (tailwind.css). Download = brand orange, upload = indigo.</summary>
public static class Series
{
    public const string Down = "var(--series-1)";
    public const string Up = "var(--series-2)";
    public const string Active = "var(--series-1)";
    public const string Idle = "var(--axis)";
}

public sealed record ChartSeries(string Key, string Name, string Color);

/// <summary>Curve geometry for the console's SVG charts.</summary>
public static class ChartMath
{
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public readonly record struct Pt(double X, double Y);
    private readonly record struct Seg(Pt C1, Pt C2, Pt To);

    /// <summary>Monotone cubic interpolation in x (d3 curveMonotoneX): smooth, never overshoots below zero or between points.</summary>
    private static List<Seg> Monotone(IReadOnlyList<Pt> p)
    {
        var segs = new List<Seg>();
        var n = p.Count;
        if (n < 2) return segs;
        if (n == 2)
        {
            segs.Add(new Seg(Lerp(p[0], p[1], 1 / 3.0), Lerp(p[0], p[1], 2 / 3.0), p[1]));
            return segs;
        }
        var t = new double[n];
        for (var i = 1; i < n - 1; i++) t[i] = Slope3(p[i - 1], p[i], p[i + 1]);
        t[0] = Slope2(p[0], p[1], t[1]);
        t[n - 1] = Slope2(p[n - 1], p[n - 2], t[n - 2], reverse: true);
        for (var i = 0; i < n - 1; i++)
        {
            var dx = (p[i + 1].X - p[i].X) / 3;
            segs.Add(new Seg(new Pt(p[i].X + dx, p[i].Y + dx * t[i]), new Pt(p[i + 1].X - dx, p[i + 1].Y - dx * t[i + 1]), p[i + 1]));
        }
        return segs;
    }

    private static Pt Lerp(Pt a, Pt b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

    private static double Sign(double x) => x < 0 ? -1 : 1;

    private static double Slope3(Pt p0, Pt p1, Pt p2)
    {
        double h0 = p1.X - p0.X, h1 = p2.X - p1.X;
        double s0 = h0 != 0 ? (p1.Y - p0.Y) / h0 : 0, s1 = h1 != 0 ? (p2.Y - p1.Y) / h1 : 0;
        var pp = (h0 + h1) != 0 ? (s0 * h1 + s1 * h0) / (h0 + h1) : 0;
        return (Sign(s0) + Sign(s1)) * Math.Min(Math.Min(Math.Abs(s0), Math.Abs(s1)), 0.5 * Math.Abs(pp));
    }

    private static double Slope2(Pt p, Pt q, double t, bool reverse = false)
    {
        var h = reverse ? p.X - q.X : q.X - p.X;
        var s = h != 0 ? (reverse ? (p.Y - q.Y) : (q.Y - p.Y)) / h : t;
        return (3 * s - t) / 2;
    }

    public static string Line(IReadOnlyList<Pt> pts)
    {
        if (pts.Count == 0) return "";
        var sb = new StringBuilder($"M{N(pts[0].X)},{N(pts[0].Y)}");
        foreach (var s in Monotone(pts)) sb.Append($" C{N(s.C1.X)},{N(s.C1.Y)} {N(s.C2.X)},{N(s.C2.Y)} {N(s.To.X)},{N(s.To.Y)}");
        return sb.ToString();
    }

    /// <summary>Closed band between an upper and a lower line (both left to right).</summary>
    public static string Band(IReadOnlyList<Pt> upper, IReadOnlyList<Pt> lower)
    {
        if (upper.Count == 0) return "";
        var sb = new StringBuilder(Line(upper));
        var low = Monotone(lower);
        sb.Append($" L{N(lower[^1].X)},{N(lower[^1].Y)}");
        for (var i = low.Count - 1; i >= 0; i--)
        {
            var from = i == 0 ? lower[0] : low[i - 1].To;
            sb.Append($" C{N(low[i].C2.X)},{N(low[i].C2.Y)} {N(low[i].C1.X)},{N(low[i].C1.Y)} {N(from.X)},{N(from.Y)}");
        }
        sb.Append(" Z");
        return sb.ToString();
    }

    /// <summary>A rounded axis maximum (1, 2, 2.5, 5 × 10^n) at or above the data maximum.</summary>
    public static double NiceMax(double max)
    {
        if (max <= 0 || !double.IsFinite(max)) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var f in new[] { 1, 2, 2.5, 5, 10 })
            if (f * exp >= max) return f * exp;
        return 10 * exp;
    }

    /// <summary>Catmull-Rom sparkline path in a 100 × height box (as the original Sparkline).</summary>
    public static string Spark(IReadOnlyList<double> values, double height, double max)
    {
        var w = 100.0;
        var pts = values.Select((v, i) => new Pt(i / (double)(values.Count - 1) * w, height - 2 - v / max * (height - 4))).ToList();
        var sb = new StringBuilder($"M{N(pts[0].X)},{N(pts[0].Y)}");
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            sb.Append($" C{N(p1.X + (p2.X - p0.X) / 6)},{N(p1.Y + (p2.Y - p0.Y) / 6)} {N(p2.X - (p3.X - p1.X) / 6)},{N(p2.Y - (p3.Y - p1.Y) / 6)} {N(p2.X)},{N(p2.Y)}");
        }
        return sb.ToString();
    }
}
