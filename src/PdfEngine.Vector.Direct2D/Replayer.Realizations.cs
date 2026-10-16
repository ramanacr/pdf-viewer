using System.Numerics;
using PdfEngine.Geometry;
using SharpGen.Runtime;
using Vortice.Direct2D1;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Windowed tessellation. A realization tessellates the whole geometry, so at deep zoom a
/// page-spanning path costs its full length at device resolution although a tile shows a sliver
/// of it. A path that reaches far beyond the region being drawn is realized only near it: the
/// region plus half its width on each side and one and a half its height above and below (where
/// scrolling goes next). A later render outside that window rebuilds it.
/// </summary>
internal sealed partial class Replayer
{
    /// <summary>Off: every realization covers its whole geometry.</summary>
    public bool WindowedRealizations { get; set; } = true;

    /// <summary>Draw geometries smaller than <see cref="DirectDrawPixels"/> on the device without a realization.</summary>
    public bool DrawSmallGeometriesDirectly { get; set; } = true;
    private const float DirectDrawPixels = 1024f;

    /// <summary>Realizations built so far, how many of them windowed, and how many rebuilt for a region beyond their window.</summary>
    public int RealizationsBuilt { get; private set; }
    public int WindowedRealizationsBuilt { get; private set; }
    public int RealizationsRebuilt { get; private set; }

    // The current Draw's page-space region and transform, and the page → device transform of the
    // range being drawn (a group draws in its own offscreen space).
    private PdfRect _drawVisible = new(-1e7, -1e7, 2e7, 2e7);
    private Matrix3x2 _drawPageToDevice = Matrix3x2.Identity;
    private Matrix3x2 _rangePageToDevice = Matrix3x2.Identity;
    private PdfRect? _drawWindow;

    // Page-space window each windowed realization is valid in; absent: the whole geometry.
    private readonly Dictionary<PdfDrawCommand, PdfRect> _realizedWindows = new(ReferenceEqualityComparer.Instance);

    private const float WindowSides = 0.5f, WindowAboveBelow = 1.5f, WindowPadPixels = 16f;
    private const float MinPiecePixels = 1024f;
    private const int MaxSplitDepth = 6;

    private void BeginWindow(Matrix3x2 pageToDevice, PdfRect visiblePage)
    {
        _drawVisible = visiblePage;
        _drawPageToDevice = pageToDevice;
        _rangePageToDevice = pageToDevice;
        _drawWindow = null;
    }

    private readonly record struct Box(float L, float T, float R, float B)
    {
        public float Area => MathF.Max(0, R - L) * MathF.Max(0, B - T);
        public bool Contains(Box o) => o.L >= L && o.T >= T && o.R <= R && o.B <= B;
        public bool Disjoint(Box o) => o.R < L || o.L > R || o.B < T || o.T > B;
        public Box Inflate(float dx, float dy) => new(L - dx, T - dy, R + dx, B + dy);
        public Box Intersect(Box o) => new(MathF.Max(L, o.L), MathF.Max(T, o.T), MathF.Min(R, o.R), MathF.Min(B, o.B));
        public Vector2 Clamp(Vector2 p) => new(Math.Clamp(p.X, L, R), Math.Clamp(p.Y, T, B));

        public static Box Of(Matrix3x2 m, PdfRect r)
        {
            var t = Direct2DVectorRenderer.TransformRect(m, new Vector2((float)r.X, (float)r.Y), new Vector2((float)r.Right, (float)r.Bottom));
            return new((float)t.X, (float)t.Y, (float)t.Right, (float)t.Bottom);
        }

        public static Box Of(ReadOnlySpan<Vector2> points)
        {
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var p in points)
            {
                l = MathF.Min(l, p.X); t = MathF.Min(t, p.Y);
                r = MathF.Max(r, p.X); b = MathF.Max(b, p.Y);
            }
            return new(l, t, r, b);
        }
    }

    /// <summary>Page-space window for the current Draw, widened in device space.</summary>
    private PdfRect DrawWindow()
    {
        if (_drawWindow is { } w)
            return w;
        var v = Box.Of(_drawPageToDevice, _drawVisible);
        float dx = WindowSides * (v.R - v.L) + WindowPadPixels, dy = WindowAboveBelow * (v.B - v.T) + WindowPadPixels;
        Matrix3x2.Invert(_drawPageToDevice, out var deviceToPage);
        var d = v.Inflate(dx, dy);
        var page = Direct2DVectorRenderer.TransformRect(deviceToPage, new Vector2(d.L, d.T), new Vector2(d.R, d.B));
        _drawWindow = page;
        return page;
    }

    private static bool Covers(PdfRect window, PdfRect r) =>
        window.X <= r.X && window.Y <= r.Y && window.Right >= r.Right && window.Bottom >= r.Bottom;

    /// <summary>
    /// The realization for <paramref name="cmd"/> at this scale: cached while its window covers the
    /// region being drawn, else built (windowed when the path reaches far beyond the region).
    /// </summary>
    private ID2D1GeometryRealization? RealizeWindowed(ID2D1DeviceContext1 ctx1, PdfDrawCommand cmd, ID2D1Geometry geom, Matrix3x2 full,
        (float Width, ID2D1StrokeStyle1 Style)? stroke)
    {
        if (_realizations.TryGetValue(cmd, out var cached))
        {
            if (!_realizedWindows.TryGetValue(cmd, out var covered) || Covers(covered, _drawVisible))
                return cached;
            cached?.Dispose();
            _realizations.Remove(cmd);
            RealizationsRebuilt++;
        }
        _ct.ThrowIfCancellationRequested(); // tessellation is the cost: stop between geometries

        // A geometry that covers little of the device is drawn directly: it shows in a tile or two,
        // so a realization is rarely drawn twice, and building thousands of them (a map with 9,000
        // small symbols) cost seconds for the first tile at a new zoom where drawing them costs milliseconds.
        if (DrawSmallGeometriesDirectly && cmd.Bounds is PdfRect sb && Math.Max(sb.Width, sb.Height) * D2D1.D2D1ComputeMaximumScaleFactor(ref _drawPageToDevice) < DirectDrawPixels)
            return null;
        float scale = D2D1.D2D1ComputeMaximumScaleFactor(ref full);
        // Default tolerance (0.25 device px) expressed in user space for this transform.
        float tolerance = 0.25f / Math.Max(scale, 1e-6f);
        var windowed = WindowedRealizations ? Windowed(cmd, full, stroke, scale) : null;
        ID2D1GeometryRealization? r;
        try
        {
            ID2D1Geometry source = windowed?.Geometry ?? geom;
            r = stroke is { } s
                ? ctx1.CreateStrokedGeometryRealization(source, tolerance, s.Width, s.Style)
                : ctx1.CreateFilledGeometryRealization(source, tolerance);
        }
        catch (SharpGenException)
        {
            r = null;
            windowed?.Geometry.Dispose();
            windowed = null;
        }
        windowed?.Geometry.Dispose();
        _realizations[cmd] = r;
        if (windowed is { } w)
            _realizedWindows[cmd] = w.Window;
        else
            _realizedWindows.Remove(cmd);
        RealizationsBuilt++;
        if (windowed != null) WindowedRealizationsBuilt++;
        return r;
    }

    /// <summary>The part of the path near the current window, or null when the whole path is as cheap.</summary>
    private (ID2D1PathGeometry1 Geometry, PdfRect Window)? Windowed(PdfDrawCommand cmd, Matrix3x2 full,
        (float Width, ID2D1StrokeStyle1 Style)? stroke, float scale)
    {
        // Dash phase runs along the whole path, so dashed strokes keep it.
        var (path, rule, pdfStroke) = cmd switch
        {
            FillPath f when stroke == null => (f.Path, f.Rule, (PdfStroke?)null),
            StrokePath s when stroke != null && s.Stroke.DashArray is not { Count: > 0 } => (s.Path, PdfFillRule.NonZero, s.Stroke),
            _ => (null, default, null),
        };
        // Soft masks and pattern cells draw everything at once.
        if (path == null || path.Segments.Count == 0 || _drawVisible.Width >= 1e5 || _drawVisible.Height >= 1e5)
            return null;
        if (!Matrix3x2.Invert(full, out var deviceToUser))
            return null;

        var window = DrawWindow();
        // Beyond this distance nothing of the path reaches the window: stroke half-width, joins, caps, antialiasing.
        float margin = 4;
        if (pdfStroke != null && stroke is { } s2)
        {
            float reach = pdfStroke.Join == PdfLineJoin.Miter ? (float)Math.Max(pdfStroke.MiterLimit, 1.5) : 1.5f;
            margin += 0.5f * s2.Width * scale * reach;
        }
        var keep = Box.Of(_rangePageToDevice, window).Inflate(margin, margin);

        var b = path.Bounds;
        var bounds = Box.Of(full, b).Inflate(1, 1);
        if (keep.Contains(bounds) || bounds.Intersect(keep).Area * 2 >= bounds.Area)
            return null;
        return (BuildWindowedGeometry(path, rule, full, deviceToUser, keep, pdfStroke != null), window);
    }

    private readonly record struct Piece(Vector2 A, Vector2 C1, Vector2 C2, Vector2 B, bool Curve, bool Kept);

    /// <summary>
    /// The path with every piece whose device bounds lie outside <paramref name="keep"/> removed.
    /// A stroke is broken there (the ends fall outside by more than the stroke reaches). A fill
    /// replaces the piece by its projection onto the keep rectangle: a piece beyond one side
    /// projects onto that side, the straight path between them never enters the rectangle, so
    /// the winding number of every point inside is unchanged.
    /// </summary>
    private ID2D1PathGeometry1 BuildWindowedGeometry(PdfPath path, PdfFillRule rule, Matrix3x2 full, Matrix3x2 deviceToUser, Box keep, bool stroke)
    {
        var geom = _res.Factory.CreatePathGeometry();
        using var sink = geom.Open();
        sink.SetFillMode(rule == PdfFillRule.EvenOdd ? FillMode.Alternate : FillMode.Winding);

        var pieces = new List<Piece>();
        bool open = false, closed = false;
        Vector2 start = default, current = default;

        void Classify(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, bool curve, int depth = 0)
        {
            Span<Vector2> d = stackalloc Vector2[4];
            d[0] = Vector2.Transform(a, full); d[1] = Vector2.Transform(curve ? c1 : a, full);
            d[2] = Vector2.Transform(curve ? c2 : b, full); d[3] = Vector2.Transform(b, full);
            var box = Box.Of(d);
            if (keep.Disjoint(box)) { pieces.Add(new Piece(a, c1, c2, b, curve, false)); return; }
            float length = Vector2.Distance(d[0], d[1]) + Vector2.Distance(d[1], d[2]) + Vector2.Distance(d[2], d[3]);
            if (!curve || keep.Contains(box) || depth == MaxSplitDepth || length < MinPiecePixels)
            {
                pieces.Add(new Piece(a, c1, c2, b, curve, true));
                return;
            }
            // Straddles the window: halve it. Every piece adds flattening overhead, so the parts
            // inside stay as large as they can.
            var ab = (a + c1) / 2; var bc = (c1 + c2) / 2; var cd = (c2 + b) / 2;
            var abc = (ab + bc) / 2; var bcd = (bc + cd) / 2; var mid = (abc + bcd) / 2;
            Classify(a, ab, abc, mid, true, depth + 1);
            Classify(mid, bcd, cd, b, true, depth + 1);
        }

        Vector2 ClampToKeep(Vector2 user) =>
            Vector2.Transform(keep.Clamp(Vector2.Transform(user, full)), deviceToUser);

        void Flush()
        {
            if (!open)
                return;
            if (!stroke && current != start)
                Classify(current, default, default, start, false); // a fill closes every figure
            if (pieces.Count == 0)
            {
                sink.BeginFigure(start, FigureBegin.Filled);
                sink.EndFigure(closed ? FigureEnd.Closed : FigureEnd.Open);
            }
            else if (stroke)
                EmitStroke(sink, pieces, closed);
            else
                EmitFill(sink, pieces, ClampToKeep);
            pieces.Clear();
            open = closed = false;
        }

        foreach (var seg in path.Segments)
        {
            switch (seg)
            {
                case PdfMoveTo m:
                    Flush();
                    start = current = V(m.Point);
                    open = true;
                    break;
                case PdfLineTo l:
                    if (!open) { start = current; open = true; }
                    Classify(current, default, default, V(l.Point), false);
                    current = V(l.Point);
                    break;
                case PdfCubicBezierTo c:
                    if (!open) { start = current; open = true; }
                    Classify(current, V(c.Control1), V(c.Control2), V(c.EndPoint), true);
                    current = V(c.EndPoint);
                    break;
                case PdfCloseSubpath:
                    if (open)
                    {
                        if (stroke && current != start)
                            Classify(current, default, default, start, false);
                        closed = true;
                        Flush();
                        current = start; // after h the current point is the subpath start (8.5.2.1)
                    }
                    break;
            }
        }
        Flush();
        sink.Close();
        return geom;
    }

    private static void Add(ID2D1GeometrySink sink, Piece p)
    {
        if (p.Curve)
            sink.AddBezier(new BezierSegment { Point1 = p.C1, Point2 = p.C2, Point3 = p.B });
        else
            sink.AddLine(p.B);
    }

    /// <summary>Runs of kept pieces as open figures; a closed figure starts after a gap so its runs wrap around.</summary>
    private static void EmitStroke(ID2D1GeometrySink sink, List<Piece> pieces, bool closed)
    {
        int gap = pieces.FindIndex(p => !p.Kept);
        if (gap < 0)
        {
            sink.BeginFigure(pieces[0].A, FigureBegin.Filled);
            foreach (var p in pieces) Add(sink, p);
            sink.EndFigure(closed ? FigureEnd.Closed : FigureEnd.Open);
            return;
        }
        int first = closed ? gap + 1 : 0;
        bool inRun = false;
        for (int k = 0; k < pieces.Count; k++)
        {
            var p = pieces[(first + k) % pieces.Count];
            if (!p.Kept)
            {
                if (inRun) sink.EndFigure(FigureEnd.Open);
                inRun = false;
                continue;
            }
            if (!inRun) sink.BeginFigure(p.A, FigureBegin.Filled);
            inRun = true;
            Add(sink, p);
        }
        if (inRun) sink.EndFigure(FigureEnd.Open);
    }

    /// <summary>One closed figure: kept pieces as they are, removed ones along the keep rectangle's edge.</summary>
    private static void EmitFill(ID2D1GeometrySink sink, List<Piece> pieces, Func<Vector2, Vector2> clamp)
    {
        bool begun = false, lastDropped = false;
        Vector2 at = default;
        void LineTo(Vector2 p)
        {
            if (p != at) sink.AddLine(p);
            at = p;
        }
        foreach (var p in pieces)
        {
            if (p.Kept)
            {
                if (!begun) { sink.BeginFigure(p.A, FigureBegin.Filled); at = p.A; begun = true; }
                else if (lastDropped) LineTo(p.A);
                Add(sink, p);
                at = p.B;
                lastDropped = false;
            }
            else
            {
                var a = clamp(p.A);
                if (!begun) { sink.BeginFigure(a, FigureBegin.Filled); at = a; begun = true; }
                else if (!lastDropped) LineTo(a);
                LineTo(clamp(p.B));
                lastDropped = true;
            }
        }
        sink.EndFigure(FigureEnd.Closed);
    }
}
