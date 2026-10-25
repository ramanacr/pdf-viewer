using System;
using System.Collections.Generic;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Annotations;

/// <summary>What a measuring point snapped to.</summary>
public enum PdfSnapKind
{
    None,
    /// <summary>The end of a line or curve of the page's drawing.</summary>
    Endpoint,
    /// <summary>The middle of a straight line.</summary>
    Midpoint,
    /// <summary>The nearest point on a line or curve.</summary>
    OnPath,
}

/// <summary>A snapped point in default user space, and what it snapped to.</summary>
public readonly record struct PdfSnap(PdfPoint Point, PdfSnapKind Kind);

/// <summary>
/// The page's vector geometry for snapping a measuring tool: every filled and stroked path of a
/// display list in default user space (transforms applied), as segments with their end points and
/// the middles of straight lines; curves are followed as chords. Points are found through a
/// uniform grid, so a query looks at the few cells near the pointer. End points win over
/// middles, and middles over the nearest point on a path, as in Acrobat.
/// </summary>
public sealed class PdfSnapIndex
{
    private const double Cell = 24;
    private const int CurveChords = 12;

    private readonly List<(PdfPoint A, PdfPoint B)> _segments = new();
    private readonly List<(PdfPoint P, PdfSnapKind Kind)> _points = new();
    private readonly Dictionary<(int, int), List<int>> _segmentCells = new();
    private readonly Dictionary<(int, int), List<int>> _pointCells = new();
    private readonly List<int> _longSegments = new();

    private PdfSnapIndex() { }

    public int SegmentCount => _segments.Count;
    public int PointCount => _points.Count;

    /// <summary>Indexes a display list; stops adding after <paramref name="maxSegments"/> segments (a huge map stays responsive).</summary>
    public static PdfSnapIndex Build(IPdfDisplayList list, int maxSegments = 250_000)
    {
        ArgumentNullException.ThrowIfNull(list);
        var index = new PdfSnapIndex();
        var stack = new Stack<PdfMatrix>();
        var ctm = PdfMatrix.Identity;
        foreach (var command in list.Commands)
        {
            if (index._segments.Count >= maxSegments) break;
            switch (command)
            {
                case SaveState: stack.Push(ctm); break;
                case RestoreState: if (stack.Count > 0) ctm = stack.Pop(); break;
                case ConcatTransform t: ctm = t.Matrix.Multiply(ctm); break;
                case FillPath f: index.AddPath(f.Path, ctm, maxSegments); break;
                case StrokePath s: index.AddPath(s.Path, ctm, maxSegments); break;
            }
        }
        return index;
    }

    private void AddPath(PdfPath path, PdfMatrix ctm, int maxSegments)
    {
        PdfPoint current = default, start = default;
        bool open = false;
        foreach (var segment in path.Segments)
        {
            if (_segments.Count >= maxSegments) return;
            switch (segment)
            {
                case PdfMoveTo m:
                    current = start = ctm.Transform(m.Point);
                    open = true;
                    AddPoint(current, PdfSnapKind.Endpoint);
                    break;
                case PdfLineTo l when open:
                {
                    var p = ctm.Transform(l.Point);
                    AddLine(current, p);
                    current = p;
                    break;
                }
                case PdfCubicBezierTo c when open:
                {
                    var c1 = ctm.Transform(c.Control1);
                    var c2 = ctm.Transform(c.Control2);
                    var p3 = ctm.Transform(c.EndPoint);
                    var previous = current;
                    for (int i = 1; i <= CurveChords; i++)
                    {
                        double t = (double)i / CurveChords, u = 1 - t;
                        var q = new PdfPoint(
                            u * u * u * current.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p3.X,
                            u * u * u * current.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p3.Y);
                        AddSegment(previous, q);
                        previous = q;
                    }
                    AddPoint(p3, PdfSnapKind.Endpoint);
                    current = p3;
                    break;
                }
                case PdfCloseSubpath when open:
                    AddLine(current, start);
                    current = start;
                    break;
            }
        }
    }

    private void AddLine(PdfPoint a, PdfPoint b)
    {
        if (!AddSegment(a, b)) return;
        AddPoint(b, PdfSnapKind.Endpoint);
        AddPoint(new PdfPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2), PdfSnapKind.Midpoint);
    }

    private bool AddSegment(PdfPoint a, PdfPoint b)
    {
        if (!Finite(a) || !Finite(b) || Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 1e-6) return false;
        int i = _segments.Count;
        _segments.Add((a, b));
        int x0 = CellOf(Math.Min(a.X, b.X)), x1 = CellOf(Math.Max(a.X, b.X));
        int y0 = CellOf(Math.Min(a.Y, b.Y)), y1 = CellOf(Math.Max(a.Y, b.Y));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 256)
        {
            _longSegments.Add(i); // a long diagonal would fill many cells: checked on every query
            return true;
        }
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                Bucket(_segmentCells, x, y).Add(i);
        return true;
    }

    private void AddPoint(PdfPoint p, PdfSnapKind kind)
    {
        if (!Finite(p)) return;
        Bucket(_pointCells, CellOf(p.X), CellOf(p.Y)).Add(_points.Count);
        _points.Add((p, kind));
    }

    private static List<int> Bucket(Dictionary<(int, int), List<int>> cells, int x, int y)
    {
        if (!cells.TryGetValue((x, y), out var list)) cells[(x, y)] = list = new List<int>();
        return list;
    }

    private static int CellOf(double v) => (int)Math.Floor(Math.Clamp(v, -1e7, 1e7) / Cell);

    private static bool Finite(PdfPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    /// <summary>
    /// The point to snap <paramref name="p"/> to within <paramref name="tolerance"/> (default
    /// user space units): the nearest end point, else the nearest middle, else the nearest point
    /// on a path; <see cref="PdfSnapKind.None"/> with <paramref name="p"/> when nothing is near.
    /// </summary>
    public PdfSnap Find(PdfPoint p, double tolerance)
    {
        if (!(tolerance > 0) || !Finite(p)) return new PdfSnap(p, PdfSnapKind.None);
        int cx0 = CellOf(p.X - tolerance), cx1 = CellOf(p.X + tolerance), cy0 = CellOf(p.Y - tolerance), cy1 = CellOf(p.Y + tolerance);
        double t2 = tolerance * tolerance;

        PdfSnap? best = null;
        double bestDistance = double.MaxValue;
        foreach (var kind in new[] { PdfSnapKind.Endpoint, PdfSnapKind.Midpoint })
        {
            for (int x = cx0; x <= cx1; x++)
                for (int y = cy0; y <= cy1; y++)
                {
                    if (!_pointCells.TryGetValue((x, y), out var ids)) continue;
                    foreach (int id in ids)
                    {
                        var (q, k) = _points[id];
                        if (k != kind) continue;
                        double d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                        if (d <= t2 && d < bestDistance) { bestDistance = d; best = new PdfSnap(q, k); }
                    }
                }
            if (best is { } found) return found;
        }

        var seen = new HashSet<int>();
        void Try(int id)
        {
            if (!seen.Add(id)) return;
            var (a, b) = _segments[id];
            var q = Nearest(a, b, p);
            double d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
            if (d <= t2 && d < bestDistance) { bestDistance = d; best = new PdfSnap(q, PdfSnapKind.OnPath); }
        }
        for (int x = cx0; x <= cx1; x++)
            for (int y = cy0; y <= cy1; y++)
                if (_segmentCells.TryGetValue((x, y), out var ids))
                    foreach (int id in ids) Try(id);
        foreach (int id in _longSegments) Try(id);
        return best ?? new PdfSnap(p, PdfSnapKind.None);
    }

    private static PdfPoint Nearest(PdfPoint a, PdfPoint b, PdfPoint p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        return new PdfPoint(a.X + t * dx, a.Y + t * dy);
    }
}
