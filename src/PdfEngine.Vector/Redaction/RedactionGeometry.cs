using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Redaction;

/// <summary>
/// Geometry for removing what lies under redaction rectangles: paths flattened to polygons,
/// fills cut to the area outside the rectangles, strokes cut into the pieces outside them. All in
/// page space (default user space).
/// </summary>
internal static class RedactionGeometry
{
    public readonly record struct P(double X, double Y);

    /// <summary>
    /// A polygon (one flattened subpath) minus a rectangle: the parts in each of the four convex
    /// strips around it (left, right, below and above the middle). Clipping to a convex window
    /// keeps a polygon's winding, so the pieces filled with the original fill rule cover exactly
    /// the original area outside the rectangle, holes included.
    /// </summary>
    public static List<List<P>> Subtract(List<P> polygon, PdfRect r)
    {
        const double Far = 1e7;
        double x0 = r.X, x1 = r.X + r.Width, y0 = r.Y, y1 = r.Y + r.Height;
        var windows = new (double X0, double Y0, double X1, double Y1)[]
        {
            (-Far, -Far, x0, Far),   // left
            (x1, -Far, Far, Far),    // right
            (x0, -Far, x1, y0),      // below
            (x0, y1, x1, Far),       // above
        };
        var pieces = new List<List<P>>();
        foreach (var w in windows)
        {
            var clipped = ClipToBox(polygon, w.X0, w.Y0, w.X1, w.Y1);
            if (clipped.Count >= 3 && Math.Abs(Area(clipped)) > 1e-9) pieces.Add(clipped);
        }
        return pieces;
    }

    /// <summary>Several rectangles: each piece of the first subtraction minus the next rectangle, and so on.</summary>
    public static List<List<P>> Subtract(List<P> polygon, IReadOnlyList<PdfRect> rects)
    {
        var current = new List<List<P>> { polygon };
        foreach (var r in rects)
        {
            var next = new List<List<P>>();
            foreach (var piece in current)
            {
                if (!Bounds(piece).IntersectsWith(r)) { next.Add(piece); continue; }
                next.AddRange(Subtract(piece, r));
            }
            current = next;
        }
        return current;
    }

    /// <summary>Sutherland–Hodgman against an axis-aligned box.</summary>
    private static List<P> ClipToBox(List<P> poly, double x0, double y0, double x1, double y1)
    {
        var output = poly;
        output = ClipEdge(output, p => p.X >= x0, (a, b) => Lerp(a, b, (x0 - a.X) / (b.X - a.X)));
        output = ClipEdge(output, p => p.X <= x1, (a, b) => Lerp(a, b, (x1 - a.X) / (b.X - a.X)));
        output = ClipEdge(output, p => p.Y >= y0, (a, b) => Lerp(a, b, (y0 - a.Y) / (b.Y - a.Y)));
        output = ClipEdge(output, p => p.Y <= y1, (a, b) => Lerp(a, b, (y1 - a.Y) / (b.Y - a.Y)));
        return output;
    }

    private static List<P> ClipEdge(List<P> input, Func<P, bool> inside, Func<P, P, P> cross)
    {
        var output = new List<P>(input.Count + 4);
        if (input.Count == 0) return output;
        var prev = input[^1];
        bool prevIn = inside(prev);
        foreach (var cur in input)
        {
            bool curIn = inside(cur);
            if (curIn)
            {
                if (!prevIn) output.Add(cross(prev, cur));
                output.Add(cur);
            }
            else if (prevIn)
            {
                output.Add(cross(prev, cur));
            }
            prev = cur;
            prevIn = curIn;
        }
        return output;
    }

    private static P Lerp(P a, P b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static double Area(List<P> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    public static PdfRect Bounds(IEnumerable<P> points)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in points)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
        }
        return x0 > x1 ? default : new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// A polyline minus rectangles: the runs of it outside every rectangle (Liang–Barsky per
    /// segment). A stroke drawn along these runs shows exactly the original stroke outside.
    /// </summary>
    public static List<List<P>> CutPolyline(List<P> line, IReadOnlyList<PdfRect> rects, double pad)
    {
        var runs = new List<List<P>>();
        List<P>? current = null;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var intervals = new List<(double T0, double T1)> { (0, 1) };
            foreach (var r in rects)
            {
                var inside = SegmentInside(line[i], line[i + 1], r, pad);
                if (inside == null) continue;
                var next = new List<(double, double)>();
                foreach (var (a, b) in intervals)
                {
                    if (inside.Value.T1 <= a || inside.Value.T0 >= b) { next.Add((a, b)); continue; }
                    if (inside.Value.T0 > a) next.Add((a, inside.Value.T0));
                    if (inside.Value.T1 < b) next.Add((inside.Value.T1, b));
                }
                intervals = next;
            }
            foreach (var (a, b) in intervals)
            {
                var pa = Lerp(line[i], line[i + 1], a);
                var pb = Lerp(line[i], line[i + 1], b);
                if (current != null && a <= 1e-12 && current.Count > 0 && Same(current[^1], pa))
                    current.Add(pb);
                else
                {
                    if (current is { Count: >= 2 }) runs.Add(current);
                    current = new List<P> { pa, pb };
                }
            }
            if (intervals.Count == 0 || intervals[^1].T1 < 1 - 1e-12)
            {
                if (current is { Count: >= 2 }) runs.Add(current);
                current = null;
            }
        }
        if (current is { Count: >= 2 }) runs.Add(current);
        return runs;
    }

    private static bool Same(P a, P b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    /// <summary>The parameter interval of segment a→b inside the rectangle grown by <paramref name="pad"/>, or null.</summary>
    private static (double T0, double T1)? SegmentInside(P a, P b, PdfRect r, double pad)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        double[] p = { -dx, dx, -dy, dy };
        double[] q = { a.X - (r.X - pad), r.X + r.Width + pad - a.X, a.Y - (r.Y - pad), r.Y + r.Height + pad - a.Y };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-15)
            {
                if (q[i] < 0) return null;
                continue;
            }
            double t = q[i] / p[i];
            if (p[i] < 0) t0 = Math.Max(t0, t);
            else t1 = Math.Min(t1, t);
            if (t0 > t1) return null;
        }
        return (t0, t1);
    }

    /// <summary>A cubic Bézier flattened to within about 0.05 pt.</summary>
    public static void Flatten(P p0, P p1, P p2, P p3, List<P> into)
    {
        double d = Math.Abs(p0.X - 2 * p1.X + p2.X) + Math.Abs(p0.Y - 2 * p1.Y + p2.Y)
                 + Math.Abs(p1.X - 2 * p2.X + p3.X) + Math.Abs(p1.Y - 2 * p2.Y + p3.Y);
        int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(d * 10)), 2, 256);
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            into.Add(new P(
                u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    /// <summary>Whether a convex quadrilateral (a transformed box) overlaps a rectangle, and by how much of its own area.</summary>
    public static double OverlapFraction(P[] quad, PdfRect r)
    {
        var poly = ClipToBox(quad.ToList(), r.X, r.Y, r.X + r.Width, r.Y + r.Height);
        double whole = Math.Abs(Area(quad.ToList()));
        if (poly.Count < 3) return 0;
        double part = Math.Abs(Area(poly));
        return whole <= 1e-12 ? (part > 0 ? 1 : 0) : part / whole;
    }

    public static bool Contains(PdfRect r, P p) => p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;
}
