using System.Numerics;
using PdfEngine.Geometry;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Replays one display list on a Skia canvas, with the semantics of the Direct2D backend's
/// replayer. In analysis mode it only resolves fonts and images and reports what the backend
/// cannot draw (so the host can fetch fallback pixels before drawing).
/// </summary>
/// <remarks>
/// Every command's geometry is in the user space the <see cref="ConcatTransform"/> commands build;
/// the replayer keeps that transform itself and sets <c>user → device</c> on the canvas before each
/// draw, as Direct2D's replayer sets its context transform. Clips are canvas saves (they cost no
/// offscreen pixels); opacity groups are canvas layers; groups that blend, mask or knock out are
/// drawn into offscreen bitmaps (SkiaReplayer.Compositing.cs).
/// </remarks>
internal sealed partial class SkiaReplayer : IDisposable
{
    // Paths built once per replayer and reused by every render of the page. PdfPath has reference
    // equality, so (path, rule) keys identify one command's geometry.
    private readonly Dictionary<(PdfPath Path, PdfFillRule Rule), SKPath?> _paths = new();
    private bool _prepared;

    private readonly SkiaResources _res;
    private readonly IPdfDisplayList _list;
    private readonly List<PdfFallbackToken>? _backendFallbacks;
    private CancellationToken _ct;
    private readonly HashSet<DrawGlyphRun> _unsupportedRuns = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DrawImage> _unsupportedImages = new(ReferenceEqualityComparer.Instance);

    public SkiaReplayer(SkiaResources res, IPdfDisplayList list, List<PdfFallbackToken>? backendFallbacks, CancellationToken ct)
    {
        _res = res;
        _list = list;
        _backendFallbacks = backendFallbacks;
        _ct = ct;
    }

    /// <summary>
    /// A replayer kept across renders (the renderer caches one per page) takes each render's
    /// cancellation token, and so do the nested replayers it created.
    /// </summary>
    public void SetCancellation(CancellationToken ct)
    {
        _ct = ct;
        foreach (var m in _maskReplayers.Values) m.SetCancellation(ct);
        foreach (var (replayer, _) in _tilingCells.Values) replayer.SetCancellation(ct);
    }

    internal static Matrix3x2 M(PdfMatrix m) => new((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);

    /// <summary>A row-vector <see cref="Matrix3x2"/> (as the Direct2D backend uses) as Skia's column-vector matrix.</summary>
    internal static SKMatrix ToSk(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    // ------------------------------------------------------------------ analysis

    public void Analyze()
    {
        MatchGroups();
        foreach (var cmd in _list.Commands)
        {
            _ct.ThrowIfCancellationRequested();
            switch (cmd)
            {
                case DrawGlyphRun run when !run.Run.IsInvisible:
                    if (ResolveText(run.Run) == null)
                        Report(cmd, PdfFallbackReason.UnsupportedFontType, _lastTextProblem);
                    break;
                case DrawImage image:
                    if (!TryDecode(image.Image, out _, out _, out _, out var reason))
                        Report(cmd, reason, "Image could not be decoded by the Skia backend");
                    break;
                case PushTextClip clip:
                    foreach (var run in clip.Runs)
                    {
                        if (ResolveText(run) == null)
                        {
                            Report(cmd, PdfFallbackReason.TextClipping, "Clip glyphs could not be outlined: " + _lastTextProblem);
                            break;
                        }
                    }
                    break;
            }
        }
    }

    private void Report(PdfDrawCommand cmd, PdfFallbackReason reason, string description)
    {
        if (cmd.Bounds is not PdfRect b || b.IsEmpty || _backendFallbacks == null)
            return;
        _backendFallbacks.Add(new PdfFallbackToken(_list.PageNumber, new PdfRect(b.X - 1, b.Y - 1, b.Width + 2, b.Height + 2), reason, description,
            new Dictionary<string, string> { ["origin"] = "skia" }));
    }

    // ------------------------------------------------------------------ drawing

    public void Dispose()
    {
        foreach (var m in _maskReplayers.Values) m.Dispose();
        _maskReplayers.Clear();
        foreach (var (replayer, cells) in _tilingCells.Values)
        {
            replayer.Dispose();
            foreach (var c in cells.Values) c.Dispose();
        }
        _tilingCells.Clear();
        foreach (var p in _paths.Values) p?.Dispose();
        _paths.Clear();
        foreach (var p in _strokeOutlines.Values) p?.Dispose();
        _strokeOutlines.Clear();
        foreach (var p in _textOutlines.Values) p.Dispose();
        _textOutlines.Clear();
        foreach (var (image, _) in _uncachedImages.Values) image.Dispose();
        _uncachedImages.Clear();
    }

    private SKPath? PathOf(PdfPath path, PdfFillRule rule)
    {
        var key = (path, rule);
        if (!_paths.TryGetValue(key, out var p))
        {
            p = BuildPath(path, rule);
            _paths[key] = p;
        }
        return p;
    }

    /// <summary>
    /// A drawing surface: a canvas over a bitmap, with the clips and opacity layers currently pushed
    /// on it. Clips are recorded in user space with their transform so an offscreen surface of the
    /// same size (a knockout element) can apply them again.
    /// </summary>
    private sealed class Surface
    {
        public required SKCanvas Canvas { get; init; }
        /// <summary>The pixels the canvas draws into; read back only while <see cref="OpacityLayers"/> is 0.</summary>
        public required SKBitmap Bitmap { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>Clips (path set) and opacity layers (path null) in push order.</summary>
        public readonly List<(SKPath? Clip, Matrix3x2 Full, float Opacity)> Layers = new();
        /// <summary>Opacity layers pending on the canvas: drawing then goes to a Skia layer, not to <see cref="Bitmap"/>.</summary>
        public int OpacityLayers { get; set; }
    }

    // Matching End index for each group Begin, and whether a group contains something that reads
    // its backdrop: a blend group or a non-isolated knockout group. Such a Normal transparency
    // group is composited offscreen, because an opacity layer cannot be split, and its content
    // must see the group's own pixels as the backdrop rather than the page under the layer.
    private int[]? _groupEnd;
    private bool[]? _containsBlend;
    // Knockout groups with more than MaxKnockoutElements elements: drawn as ordinary groups.
    private HashSet<int>? _knockoutOverLimit;

    /// <summary>
    /// Most elements a knockout group may have to be composited natively. Each element costs two
    /// offscreen renders of the group's rectangle (the element and its shape); above the limit
    /// the group is drawn without knockout. The same limit as the Direct2D backend's.
    /// </summary>
    internal const int MaxKnockoutElements = 256;

    private void MatchGroups()
    {
        var cmds = _list.Commands;
        _groupEnd = new int[cmds.Count];
        _containsBlend = new bool[cmds.Count];
        _knockoutOverLimit = new HashSet<int>();
        var open = new Stack<int>();
        // Per open group: its top-level element count (knockout groups only count).
        var elements = new Stack<int>();
        for (int i = 0; i < cmds.Count; i++)
        {
            _groupEnd[i] = -1;
            var cmd = cmds[i];
            if (open.Count > 0 && cmds[open.Peek()] is BeginCompositingGroup { Knockout: true }
                && cmd is FillPath or StrokePath or DrawGlyphRun or DrawImage or DrawShading or DrawTilingPattern
                    or BeginCompositingGroup or BeginTransparencyGroup)
            {
                int n = elements.Pop() + 1;
                elements.Push(n);
                if (n > MaxKnockoutElements) _knockoutOverLimit.Add(open.Peek());
            }
            switch (cmd)
            {
                case BeginCompositingGroup g:
                    if (g.Blend != PdfBlendMode.Normal || g.Knockout && !g.Isolated)
                        foreach (int o in open) _containsBlend[o] = true;
                    open.Push(i);
                    elements.Push(0);
                    break;
                case BeginTransparencyGroup:
                    open.Push(i);
                    elements.Push(0);
                    break;
                case EndCompositingGroup or EndTransparencyGroup:
                    if (open.Count > 0)
                    {
                        _groupEnd[open.Pop()] = i;
                        elements.Pop();
                    }
                    break;
            }
        }
        while (open.Count > 0) _groupEnd[open.Pop()] = cmds.Count; // unterminated: runs to the end
    }

    private void Prepare()
    {
        if (_prepared)
            return;
        // Backend-unsupported content is drawn by the overlay; skip it here.
        foreach (var cmd in _list.Commands)
        {
            if (cmd is DrawGlyphRun r && !r.Run.IsInvisible && ResolveText(r.Run) == null) _unsupportedRuns.Add(r);
            if (cmd is DrawImage im && !TryDecode(im.Image, out _, out _, out _, out _)) _unsupportedImages.Add(im);
        }
        MatchGroups();
        _prepared = true;
    }

    /// <summary>Replays the whole list onto <paramref name="canvas"/>, which draws into <paramref name="target"/>.</summary>
    public void Draw(SKBitmap target, SKCanvas canvas, Matrix3x2 pageToDevice, PdfRect visiblePage)
    {
        Prepare();
        var surface = new Surface { Canvas = canvas, Bitmap = target, Width = target.Width, Height = target.Height };
        int saved = canvas.Save();
        try
        {
            DrawRange(surface, 0, _list.Commands.Count, pageToDevice, Matrix3x2.Identity, visiblePage);
        }
        finally
        {
            canvas.RestoreToCount(saved);
            canvas.ResetMatrix();
        }
    }

    /// <summary>Knockout state of the group whose range is being drawn: its initial backdrop (null when isolated).</summary>
    private sealed record Knockout(SKBitmap? Backdrop);

    /// <summary>Shape rendering (knockout): every paint opaque, images as their full rectangle, groups without alpha or masks.</summary>
    private bool _shapeMode;

    private void DrawRange(Surface surface, int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage, Knockout? knockout = null)
    {
        var canvas = surface.Canvas;
        var cmds = _list.Commands;
        int baseLayers = surface.Layers.Count;
        var frames = new Stack<(int Depth, Matrix3x2 Ctm)>();

        for (int i = start; i < end; i++)
        {
            var cmd = cmds[i];
            if ((i & 0x3FF) == 0)
                _ct.ThrowIfCancellationRequested();

            if (cmd.Bounds is PdfRect b && !b.IsEmpty && !b.IntersectsWith(visiblePage))
            {
                if (cmd is FillPath or StrokePath or DrawGlyphRun or DrawImage or DrawShading or DrawTilingPattern)
                    continue;
                if (cmd is BeginCompositingGroup && _groupEnd![i] > i)
                {
                    i = _groupEnd[i];
                    continue;
                }
            }

            var full = ctm * pageToDevice;
            if (knockout != null && cmd is FillPath or StrokePath or DrawGlyphRun or DrawImage or DrawShading or DrawTilingPattern
                    or BeginCompositingGroup or BeginTransparencyGroup)
            {
                // Knockout (11.4.8): each element replaces what earlier elements painted under its shape.
                int elementEnd = cmd is BeginCompositingGroup or BeginTransparencyGroup
                    ? Math.Min(end, (_groupEnd![i] < 0 ? end : _groupEnd[i]) + 1)
                    : i + 1;
                DrawKnockoutElement(surface, knockout, i, elementEnd, pageToDevice, ctm, visiblePage);
                i = elementEnd - 1;
                continue;
            }
            switch (cmd)
            {
                case SaveState:
                    frames.Push((surface.Layers.Count, ctm));
                    break;
                case RestoreState:
                    if (frames.Count > 0)
                    {
                        var (depth, saved) = frames.Pop();
                        while (surface.Layers.Count > Math.Max(depth, baseLayers)) PopLayer(surface);
                        ctm = saved;
                    }
                    break;
                case ConcatTransform c:
                    ctm = M(c.Matrix) * ctm;
                    break;
                case FillPath fp:
                    if (PathOf(fp.Path, fp.Rule) is { } fillPath)
                    {
                        using var paint = FillPaint(fp.Paint.Color, fp.Paint.Alpha);
                        canvas.SetMatrix(ToSk(full));
                        canvas.DrawPath(fillPath, paint);
                    }
                    break;
                case StrokePath sp:
                    if (PathOf(sp.Path, PdfFillRule.NonZero) is { } strokePath)
                        Stroke(canvas, strokePath, Color(sp.Paint.Color, sp.Paint.Alpha), sp.Stroke, full);
                    break;
                case PushClip pc:
                    PushClipLayer(surface, PathOf(pc.Path, pc.Rule) ?? EmptyPath, full);
                    break;
                case PushStrokeClip psc:
                    PushClipLayer(surface, StrokeOutline(psc, full) ?? EmptyPath, full);
                    break;
                case PushTextClip ptc:
                    PushClipLayer(surface, TextOutline(ptc, ctm), full);
                    break;
                case PopClip:
                    if (surface.Layers.Count > baseLayers) PopLayer(surface);
                    break;
                case DrawGlyphRun dgr:
                    if (!dgr.Run.IsInvisible && !_unsupportedRuns.Contains(dgr))
                        DrawText(canvas, dgr, full);
                    break;
                case DrawImage di:
                    if (!_unsupportedImages.Contains(di))
                        PaintImage(canvas, di, full);
                    break;
                case DrawShading ds:
                    PaintShading(surface, ds, ctm, pageToDevice);
                    break;
                case DrawTilingPattern tp:
                    PaintTiling(canvas, tp, ctm, pageToDevice, visiblePage);
                    break;
                case BeginCompositingGroup cg:
                {
                    int groupEnd = _groupEnd![i] < 0 ? end : Math.Min(_groupEnd[i], end);
                    // A knockout group with more elements than the compositor takes is drawn as an
                    // ordinary group: overlapping elements then show through each other where
                    // knockout would have hidden them, but nothing is left out.
                    bool knock = cg.Knockout && !_knockoutOverLimit!.Contains(i);
                    DrawGroup(surface, cg.Bounds, cg.Alpha, cg.Blend, cg.SoftMask, cg.Isolated, knock, _containsBlend![i],
                        i + 1, groupEnd, pageToDevice, ctm, visiblePage);
                    i = groupEnd;
                    break;
                }
                case BeginTransparencyGroup tg when _containsBlend![i]:
                {
                    int groupEnd = _groupEnd![i] < 0 ? end : Math.Min(_groupEnd[i], end);
                    DrawGroup(surface, tg.Bounds, tg.Alpha, PdfBlendMode.Normal, null, tg.Isolated, false, true,
                        i + 1, groupEnd, pageToDevice, ctm, visiblePage);
                    i = groupEnd;
                    break;
                }
                case BeginTransparencyGroup g:
                    PushOpacityLayer(surface, _shapeMode ? 1f : (float)Math.Clamp(g.Alpha, 0, 1), full);
                    break;
                case EndTransparencyGroup:
                    if (surface.Layers.Count > baseLayers) PopLayer(surface);
                    break;
            }
        }
        while (surface.Layers.Count > baseLayers) PopLayer(surface);
    }

    private static readonly SKPath EmptyPath = new();

    // ------------------------------------------------------------------ clips and layers

    private static void PushClipLayer(Surface surface, SKPath clip, Matrix3x2 full)
    {
        var canvas = surface.Canvas;
        canvas.Save();
        canvas.SetMatrix(ToSk(full));
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        surface.Layers.Add((clip, full, 1f));
    }

    private static void PushOpacityLayer(Surface surface, float opacity, Matrix3x2 full)
    {
        using var paint = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(opacity * 255)) };
        surface.Canvas.SaveLayer(paint);
        surface.Layers.Add((null, full, opacity));
        surface.OpacityLayers++;
    }

    private static void PopLayer(Surface surface)
    {
        int last = surface.Layers.Count - 1;
        if (surface.Layers[last].Clip == null) surface.OpacityLayers--;
        surface.Layers.RemoveAt(last);
        surface.Canvas.Restore();
    }

    /// <summary>Applies another surface's clips (in the same device frame) to <paramref name="sub"/>.</summary>
    private static void PushClipsOf(Surface from, Surface sub, Matrix3x2 offset)
    {
        foreach (var (clip, full, _) in from.Layers)
            if (clip != null) PushClipLayer(sub, clip, full * offset);
    }

    // ------------------------------------------------------------------ paths and paint

    private static SKPath? BuildPath(PdfPath path, PdfFillRule rule)
    {
        if (path.Segments.Count == 0)
            return null;
        var p = new SKPath { FillType = rule == PdfFillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        bool open = false;
        SKPoint start = default, current = default;
        foreach (var seg in path.Segments)
        {
            switch (seg)
            {
                case PdfMoveTo m:
                    start = current = P(m.Point);
                    p.MoveTo(start);
                    open = true;
                    break;
                case PdfLineTo l:
                    if (!open) { p.MoveTo(current); start = current; open = true; }
                    current = P(l.Point);
                    p.LineTo(current);
                    break;
                case PdfCubicBezierTo c:
                    if (!open) { p.MoveTo(current); start = current; open = true; }
                    current = P(c.EndPoint);
                    p.CubicTo(P(c.Control1), P(c.Control2), current);
                    break;
                case PdfCloseSubpath:
                    if (open)
                    {
                        p.Close();
                        open = false;
                        current = start; // after h the current point is the subpath start (8.5.2.1)
                    }
                    break;
            }
        }
        return p;
    }

    private static SKPoint P(PdfPoint p) => new((float)p.X, (float)p.Y);

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private SKColor Color(PdfColor c, double alpha) => _shapeMode
        ? new SKColor(0, 0, 0, (byte)(alpha > 0 ? 255 : 0))
        : new SKColor(ToByte(c.R), ToByte(c.G), ToByte(c.B), ToByte(alpha));

    private SKPaint FillPaint(PdfColor c, double alpha) => new() { Color = Color(c, alpha), IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKStrokeCap Cap(PdfLineCap cap) => cap switch
    {
        PdfLineCap.Round => SKStrokeCap.Round,
        PdfLineCap.Square => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt,
    };

    private static SKStrokeJoin Join(PdfLineJoin join) => join switch
    {
        PdfLineJoin.Round => SKStrokeJoin.Round,
        PdfLineJoin.Bevel => SKStrokeJoin.Bevel,
        _ => SKStrokeJoin.Miter, // beyond the miter limit Skia bevels, as PDF requires
    };

    /// <summary>
    /// The dash pattern in user space (an odd-length array repeats, as PDF and Direct2D read it);
    /// null for a solid line or a pattern Skia rejects (no positive length).
    /// </summary>
    private static SKPathEffect? Dash(PdfStroke stroke)
    {
        if (stroke.DashArray is not { Count: > 0 } d)
            return null;
        int reps = d.Count % 2 == 1 ? 2 : 1;
        var intervals = new float[d.Count * reps];
        double sum = 0;
        for (int rep = 0, k = 0; rep < reps; rep++)
            foreach (var v in d)
            {
                if (v < 0 || !double.IsFinite(v)) return null;
                intervals[k++] = (float)v;
                sum += v;
            }
        return sum > 0 ? SKPathEffect.CreateDash(intervals, (float)stroke.DashPhase) : null;
    }

    /// <summary>
    /// PDF strokes: width in user space; width 0 — and any width that maps below one device pixel —
    /// draws the thinnest line the device can (8.4.3.2): a Skia hairline, one device pixel wide.
    /// </summary>
    private static void Stroke(SKCanvas canvas, SKPath path, SKColor color, PdfStroke stroke, Matrix3x2 full)
    {
        float scale = MathF.Sqrt(MathF.Abs(full.GetDeterminant()));
        bool hairline = stroke.Width * scale < 1.0;
        using var dash = Dash(stroke);
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = hairline ? 0f : (float)stroke.Width,
            StrokeCap = Cap(stroke.Cap),
            StrokeJoin = Join(stroke.Join),
            StrokeMiter = (float)Math.Max(1, stroke.MiterLimit),
            PathEffect = dash,
        };
        canvas.SetMatrix(ToSk(full));
        canvas.DrawPath(path, paint);
    }

    private readonly Dictionary<(PushStrokeClip Cmd, float Scale), SKPath?> _strokeOutlines = new();

    /// <summary>The area a stroke covers, in user space (hairlines: one device pixel wide).</summary>
    private SKPath? StrokeOutline(PushStrokeClip cmd, Matrix3x2 full)
    {
        float scale = MathF.Sqrt(MathF.Abs(full.GetDeterminant()));
        var key = (cmd, MathF.Round(scale, 3));
        if (_strokeOutlines.TryGetValue(key, out var cached))
            return cached;
        SKPath? outline = null;
        if (PathOf(cmd.Path, PdfFillRule.NonZero) is { } path && scale > 0)
        {
            var stroke = cmd.Stroke;
            using var dash = Dash(stroke);
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(stroke.Width, 1.0 / scale),
                StrokeCap = Cap(stroke.Cap),
                StrokeJoin = Join(stroke.Join),
                StrokeMiter = (float)Math.Max(1, stroke.MiterLimit),
                PathEffect = dash,
            };
            outline = new SKPath();
            // resScale: flatten curves finely enough for the device resolution.
            if (!paint.GetFillPath(path, outline, scale))
            {
                outline.Dispose();
                outline = null;
            }
            else
            {
                outline.FillType = SKPathFillType.Winding;
            }
        }
        _strokeOutlines[key] = outline;
        return outline;
    }

    private static PdfRect Intersect(PdfRect a, PdfRect b)
    {
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        double x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        return x1 > x0 && y1 > y0 ? new PdfRect(x0, y0, x1 - x0, y1 - y0) : PdfRect.Empty;
    }

    /// <summary>A page-space rectangle as a closed polygon in the space <paramref name="pageToUser"/> maps it to.</summary>
    private static SKPath Polygon(Matrix3x2 pageToUser, PdfRect r)
    {
        var p = new SKPath();
        p.MoveTo(Pt(Vector2.Transform(new Vector2((float)r.Left, (float)r.Top), pageToUser)));
        p.LineTo(Pt(Vector2.Transform(new Vector2((float)r.Right, (float)r.Top), pageToUser)));
        p.LineTo(Pt(Vector2.Transform(new Vector2((float)r.Right, (float)r.Bottom), pageToUser)));
        p.LineTo(Pt(Vector2.Transform(new Vector2((float)r.Left, (float)r.Bottom), pageToUser)));
        p.Close();
        return p;
    }

    private static SKPoint Pt(Vector2 v) => new(v.X, v.Y);

    /// <summary>Device rectangle of a page-space area, clamped to the surface.</summary>
    private static bool DeviceRect(Surface surface, PdfRect area, Matrix3x2 pageToDevice, out int x0, out int y0, out int w, out int h)
    {
        var c = new[]
        {
            Vector2.Transform(new Vector2((float)area.X, (float)area.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)area.Right, (float)area.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)area.X, (float)area.Bottom), pageToDevice),
            Vector2.Transform(new Vector2((float)area.Right, (float)area.Bottom), pageToDevice),
        };
        x0 = Math.Max(0, (int)Math.Floor(c.Min(p => p.X)));
        y0 = Math.Max(0, (int)Math.Floor(c.Min(p => p.Y)));
        int x1 = Math.Min(surface.Width, (int)Math.Ceiling(c.Max(p => p.X)));
        int y1 = Math.Min(surface.Height, (int)Math.Ceiling(c.Max(p => p.Y)));
        w = x1 - x0;
        h = y1 - y0;
        return w > 0 && h > 0;
    }
}
