using System.Numerics;
using System.Runtime.InteropServices;
using PdfEngine.Geometry;
using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Replays one display list on a Direct2D device context. In analysis mode it only resolves
/// fonts and images and reports what the backend cannot draw (so the host can fetch fallback
/// pixels before drawing).
/// </summary>
internal sealed class Replayer : IDisposable
{
    // Device-independent geometry, built once per render and shared by every tile.
    // PdfPath has reference equality, so (path, rule) keys identify one command's geometry.
    private readonly Dictionary<(PdfPath Path, PdfFillRule Rule), ID2D1PathGeometry1?> _geometries = new();
    private bool _prepared;

    // Device-dependent tessellations at this render's scale: every tile of a large render only
    // translates, so a fill or stroke is flattened once instead of once per tile.
    private readonly Dictionary<PdfDrawCommand, ID2D1GeometryRealization?> _realizations = new(ReferenceEqualityComparer.Instance);
    private float _realizationScale = -1;

    /// <summary>Night mode: every paint, gradient stop and image is colour-inverted.</summary>
    public bool InvertColors { get; set; }

    /// <summary>Tessellate once and reuse across tiles and, for a cached replayer, across renders at the same scale.</summary>
    public bool UseRealizations { get; set; }

    /// <summary>Drops device-dependent caches (after device loss).</summary>
    public void ResetDeviceResources()
    {
        foreach (var m in _maskReplayers.Values) m.Dispose();
        _maskReplayers.Clear();
        foreach (var (replayer, cells) in _tilingCells.Values)
        {
            replayer.Dispose();
            foreach (var c in cells.Values) c.Dispose();
        }
        _tilingCells.Clear();
        foreach (var r in _realizations.Values) r?.Dispose();
        _realizations.Clear();
        _realizationScale = -1;
    }
    private readonly D2DResources _res;
    private readonly IPdfDisplayList _list;
    private readonly List<PdfFallbackToken>? _backendFallbacks;
    private readonly bool _analyzeOnly;
    private CancellationToken _ct;
    private readonly HashSet<DrawGlyphRun> _unsupportedRuns = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DrawImage> _unsupportedImages = new(ReferenceEqualityComparer.Instance);

    public Replayer(D2DResources res, IPdfDisplayList list, List<PdfFallbackToken>? backendFallbacks, bool analyzeOnly, CancellationToken ct)
    {
        _res = res;
        _list = list;
        _backendFallbacks = backendFallbacks;
        _analyzeOnly = analyzeOnly;
        _ct = ct;
    }

    /// <summary>
    /// A replayer kept across renders (the renderer caches one per page and scale) takes each
    /// render's cancellation token, and so do the nested replayers it created.
    /// </summary>
    public void SetCancellation(CancellationToken ct)
    {
        _ct = ct;
        foreach (var m in _maskReplayers.Values) m.SetCancellation(ct);
        foreach (var (replayer, _) in _tilingCells.Values) replayer.SetCancellation(ct);
    }

    private static Matrix3x2 M(PdfMatrix m) => new((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);

    // ------------------------------------------------------------------ analysis

    public void Analyze()
    {
        foreach (var cmd in _list.Commands)
        {
            _ct.ThrowIfCancellationRequested();
            switch (cmd)
            {
                case DrawGlyphRun run when !run.Run.IsInvisible:
                    if (ResolveText(run, out _) == null)
                        Report(cmd, PdfFallbackReason.UnsupportedFontType, _lastTextProblem);
                    break;
                case DrawImage image:
                    if (!TryDecode(image.Image, out _, out _, out _, out var reason))
                        Report(cmd, reason, "Image could not be decoded by the Direct2D backend");
                    break;
                case PushTextClip clip:
                    foreach (var run in clip.Runs)
                    {
                        if (ResolveText(new DrawGlyphRun(run, default!, clip.Bounds), out _) == null)
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
            new Dictionary<string, string> { ["origin"] = "direct2d" }));
    }

    // ------------------------------------------------------------------ drawing

    public void Dispose()
    {
        foreach (var m in _maskReplayers.Values) m.Dispose();
        _maskReplayers.Clear();
        ResetDeviceResources();
        foreach (var g in _geometries.Values) g?.Dispose();
        _geometries.Clear();
        foreach (var g in _strokeOutlines.Values) g?.Dispose();
        _strokeOutlines.Clear();
        foreach (var g in _textOutlines.Values) g?.Dispose();
        _textOutlines.Clear();
    }

    private ID2D1PathGeometry1? Geometry(PdfPath path, PdfFillRule rule)
    {
        var key = (path, rule);
        if (!_geometries.TryGetValue(key, out var g))
        {
            g = BuildGeometry(path, rule);
            _geometries[key] = g;
        }
        return g;
    }

    /// <summary>
    /// A drawing surface: a device context with its target and the layers currently pushed on it.
    /// The layer list lets a blend group pop every layer (so the target holds the composited
    /// backdrop), composite, and push the same layers again.
    /// </summary>
    private sealed class Surface
    {
        public required ID2D1DeviceContext Ctx { get; init; }
        public ID2D1DeviceContext1? Ctx1 { get; init; }
        public required ID2D1Bitmap1 Target { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>Every pixel is opaque (the page, or a group initialised with an opaque backdrop).</summary>
        public bool Opaque { get; init; }
        /// <summary>
        /// Clip and opacity layers in push order. <c>Rect</c> is set for rectangular clips (user
        /// space); under an axis-preserving transform they become an axis-aligned clip instead of a
        /// layer, which costs no offscreen surface.
        /// </summary>
        public readonly List<(ID2D1Geometry? Mask, Matrix3x2 Full, float Opacity, PdfRect? Rect)> Layers = new();
        /// <summary>Per layer: true when it was pushed as an axis-aligned clip.</summary>
        public readonly List<bool> AxisAligned = new();
    }

    // Matching End index for each group Begin, and whether a Normal transparency group contains a
    // blend group (then it is composited offscreen, because an opacity layer cannot be split).
    private int[]? _groupEnd;
    private bool[]? _containsBlend;

    private void MatchGroups()
    {
        var cmds = _list.Commands;
        _groupEnd = new int[cmds.Count];
        _containsBlend = new bool[cmds.Count];
        var open = new Stack<int>();
        for (int i = 0; i < cmds.Count; i++)
        {
            _groupEnd[i] = -1;
            switch (cmds[i])
            {
                case BeginCompositingGroup g:
                    if (g.Blend != PdfBlendMode.Normal)
                        foreach (int o in open) _containsBlend[o] = true;
                    open.Push(i);
                    break;
                case BeginTransparencyGroup:
                    open.Push(i);
                    break;
                case EndCompositingGroup or EndTransparencyGroup:
                    if (open.Count > 0) _groupEnd[open.Pop()] = i;
                    break;
            }
        }
        while (open.Count > 0) _groupEnd[open.Pop()] = cmds.Count; // unterminated: runs to the end
    }

    public void Draw(ID2D1DeviceContext ctx, Matrix3x2 pageToDevice, PdfRect visiblePage)
    {
        if (!_prepared)
        {
            // Backend-unsupported content is drawn by the overlay; skip it here.
            foreach (var cmd in _list.Commands)
            {
                if (cmd is DrawGlyphRun r && !r.Run.IsInvisible && ResolveText(r, out _) == null) _unsupportedRuns.Add(r);
                if (cmd is DrawImage im && !TryDecode(im.Image, out _, out _, out _, out _)) _unsupportedImages.Add(im);
            }
            MatchGroups();
            _prepared = true;
        }

        var ctx1 = UseRealizations ? ctx.QueryInterfaceOrNull<ID2D1DeviceContext1>() : null;
        float renderScale = D2D1.D2D1ComputeMaximumScaleFactor(ref pageToDevice);
        if (Math.Abs(renderScale - _realizationScale) > 1e-4f)
        {
            ResetDeviceResources();
            _realizationScale = renderScale;
        }

        using var target = ctx.Target.QueryInterface<ID2D1Bitmap1>();
        var size = target.PixelSize;
        var surface = new Surface { Ctx = ctx, Ctx1 = ctx1, Target = target, Width = size.Width, Height = size.Height, Opaque = true };
        try
        {
            DrawRange(surface, 0, _list.Commands.Count, pageToDevice, Matrix3x2.Identity, visiblePage);
        }
        finally
        {
            while (surface.Layers.Count > 0) PopLayer(surface);
            ctx.Transform = Matrix3x2.Identity;
            ctx1?.Dispose();
        }
    }


    /// <summary>Knockout state of the group whose range is being drawn: its initial backdrop (null when isolated).</summary>
    private sealed record Knockout(ID2D1Bitmap1? Backdrop);

    /// <summary>Shape rendering (knockout): every paint opaque, images as their full rectangle, groups without alpha or masks.</summary>
    private bool _shapeMode;

    private void DrawRange(Surface surface, int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage, Knockout? knockout = null)
    {
        var ctx = surface.Ctx;
        var ctx1 = surface.Ctx1;
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
                    if (Geometry(fp.Path, fp.Rule) is { } fillGeom)
                    {
                        using var brush = ctx.CreateSolidColorBrush(Color(fp.Paint.Color, fp.Paint.Alpha));
                        ctx.Transform = full;
                        if (ctx1 != null && Realize(ctx1, cmd, fillGeom, full, null) is { } fillMesh)
                            ctx1.DrawGeometryRealization(fillMesh, brush);
                        else
                            ctx.FillGeometry(fillGeom, brush);
                    }
                    break;
                case StrokePath sp:
                    if (Geometry(sp.Path, PdfFillRule.NonZero) is { } strokeGeom)
                    {
                        using var brush = ctx.CreateSolidColorBrush(Color(sp.Paint.Color, sp.Paint.Alpha));
                        Stroke(ctx, strokeGeom, brush, sp.Stroke, full, ctx1, cmd);
                    }
                    break;
                case PushClip pc:
                    PushLayer(surface, Geometry(pc.Path, pc.Rule), full, 1f, AxisRectangle(pc.Path));
                    break;
                case PushStrokeClip psc:
                    PushLayer(surface, StrokeOutline(psc, full), full, 1f);
                    break;
                case PushTextClip ptc:
                    PushLayer(surface, TextOutline(ptc, ctm), full, 1f);
                    break;
                case PopClip:
                    if (surface.Layers.Count > baseLayers) PopLayer(surface);
                    break;
                case DrawGlyphRun dgr:
                    if (!dgr.Run.IsInvisible && !_unsupportedRuns.Contains(dgr))
                        DrawText(ctx, dgr, ctm, full);
                    break;
                case DrawImage di:
                    if (!_unsupportedImages.Contains(di))
                        PaintImage(ctx, di, full);
                    break;
                case DrawShading ds:
                    PaintShading(ctx, ds, ctm, pageToDevice);
                    break;
                case DrawTilingPattern tp:
                    PaintTiling(ctx, tp, ctm, pageToDevice, visiblePage);
                    break;
                case BeginCompositingGroup cg:
                {
                    int groupEnd = _groupEnd![i] < 0 ? end : Math.Min(_groupEnd[i], end);
                    DrawGroup(surface, cg.Bounds, cg.Alpha, cg.Blend, cg.SoftMask, cg.Isolated, cg.Knockout, _containsBlend![i],
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
                    PushLayer(surface, null, full, _shapeMode ? 1f : (float)Math.Clamp(g.Alpha, 0, 1));
                    break;
                case EndTransparencyGroup:
                    if (surface.Layers.Count > baseLayers) PopLayer(surface);
                    break;
            }
        }
        while (surface.Layers.Count > baseLayers) PopLayer(surface);
    }

    // ------------------------------------------------------------------ compositing groups

    /// <summary>Largest offscreen group side; larger groups are clamped to the visible target anyway.</summary>
    private const int MaxGroupPixels = 8192;

    private static readonly BitmapProperties1 OffscreenProps =
        new(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);

    /// <summary>
    /// Renders a group's content into an offscreen bitmap, applies the soft mask and group alpha,
    /// and composites it onto <paramref name="surface"/> with the blend mode (ISO 32000-2 11.3–11.6).
    /// Groups are composited as isolated; a non-Normal blend reads the backdrop from the surface
    /// after popping its layers, so every enclosing clip has been applied to it.
    /// </summary>
    private void DrawGroup(Surface surface, PdfRect? bounds, double alpha, PdfBlendMode blend, PdfSoftMask? mask,
        bool isolated, bool knockout, bool containsBlend,
        int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage)
    {
        if (_res.Device is not { } device)
            return;
        if (_shapeMode)
        {
            // Shape of a group = union of its elements' shapes; opacity and masks do not contribute.
            alpha = 1;
            mask = null;
            blend = PdfBlendMode.Normal;
        }

        // Device rectangle of the group, clamped to the surface.
        var r = bounds ?? visiblePage;
        var corners = new[]
        {
            Vector2.Transform(new Vector2((float)r.X, (float)r.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)r.Right, (float)r.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)r.X, (float)r.Bottom), pageToDevice),
            Vector2.Transform(new Vector2((float)r.Right, (float)r.Bottom), pageToDevice),
        };
        int x0 = (int)Math.Floor(corners.Min(c => c.X)) - 1, y0 = (int)Math.Floor(corners.Min(c => c.Y)) - 1;
        int x1 = (int)Math.Ceiling(corners.Max(c => c.X)) + 1, y1 = (int)Math.Ceiling(corners.Max(c => c.Y)) + 1;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(surface.Width, x1); y1 = Math.Min(surface.Height, y1);
        int w = Math.Min(MaxGroupPixels, x1 - x0), h = Math.Min(MaxGroupPixels, y1 - y0);
        if (w <= 0 || h <= 0 || alpha <= 0)
            return;

        var offset = Matrix3x2.CreateTranslation(-x0, -y0);
        var groupPageToDevice = pageToDevice * offset;
        var groupVisible = bounds is PdfRect gb ? Intersect(visiblePage, gb) : visiblePage;
        bool blends = blend != PdfBlendMode.Normal;

        // A non-isolated group sees the backdrop: inner blend modes and knockout elements composite
        // against it (11.4.7). Exact on an opaque backdrop; on a transparent one the group is isolated.
        bool nonIsolated = !isolated && (containsBlend || knockout) && surface.Opaque && !_shapeMode;
        ID2D1Bitmap1? initialBackdrop = nonIsolated ? CopyBackdrop(surface, x0, y0, w, h) : null;

        ID2D1Bitmap1 RenderContent(bool fromBackdrop)
        {
            var bmp = surface.Ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, OffscreenProps);
            using var gctx = device.CreateDeviceContext(DeviceContextOptions.None);
            gctx.Target = bmp;
            gctx.BeginDraw();
            gctx.AntialiasMode = AntialiasMode.PerPrimitive;
            gctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            gctx.Clear(new Color4(0, 0, 0, 0));
            if (fromBackdrop)
                gctx.DrawImage(initialBackdrop!, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            using var gctx1 = surface.Ctx1 != null ? gctx.QueryInterfaceOrNull<ID2D1DeviceContext1>() : null;
            var sub = new Surface { Ctx = gctx, Ctx1 = gctx1, Target = bmp, Width = w, Height = h, Opaque = fromBackdrop };
            try
            {
                // A blended group replaces its rectangle on the surface, so its content must carry
                // the enclosing clips itself.
                if (blends)
                    foreach (var layer in surface.Layers)
                        PushLayer(sub, layer.Mask, layer.Full * offset, 1f, layer.Rect);
                DrawRange(sub, start, end, groupPageToDevice, ctm, groupVisible,
                    knockout ? new Knockout(fromBackdrop ? initialBackdrop : null) : null);
            }
            finally
            {
                while (sub.Layers.Count > 0) PopLayer(sub);
                gctx.Transform = Matrix3x2.Identity;
                gctx.EndDraw();
                gctx.Target = null;
            }
            return bmp;
        }

        using var content = RenderContent(fromBackdrop: nonIsolated);
        using var isolatedAlpha = nonIsolated ? RenderContent(fromBackdrop: false) : null;

        var disposables = new List<IDisposable>();
        if (initialBackdrop != null) disposables.Add(initialBackdrop);
        try
        {
            ID2D1Image image = content;
            var ctx = surface.Ctx;

            if (nonIsolated)
            {
                // Group result with the backdrop removed (11.4.8, α0 = 1): R = Cn − C0 · (1 − αg), where
                // Cn was rendered over the backdrop and αg is the group's own alpha (isolated render).
                var alphaRgb = new Vortice.Direct2D1.Effects.ColorMatrix(ctx)
                {
                    Matrix = new Matrix5x4(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0),
                };
                disposables.Add(alphaRgb);
                alphaRgb.SetInput(0, isolatedAlpha!, true); // premultiplied (αg, αg, αg, αg)
                var ag = alphaRgb.Output; disposables.Add(ag);
                var k = new Vortice.Direct2D1.Effects.ArithmeticComposite(ctx) { Coefficients = new Vector4(-1, 1, 0, 0), ClampOutput = true };
                disposables.Add(k);
                k.SetInput(0, initialBackdrop!, true); // C0
                k.SetInput(1, ag, true);               // αg → C0 − αg·C0
                var kOut = k.Output; disposables.Add(kOut);
                var rEffect = new Vortice.Direct2D1.Effects.ArithmeticComposite(ctx) { Coefficients = new Vector4(0, 1, -1, 0), ClampOutput = true };
                disposables.Add(rEffect);
                rEffect.SetInput(0, content, true); // Cn
                rEffect.SetInput(1, kOut, true);    // − C0 (1 − αg)
                image = rEffect.Output;
                disposables.Add(image);
            }

            if (mask != null && RenderSoftMask(mask, w, h, groupPageToDevice, surface.Ctx1 != null) is { } maskImage)
            {
                disposables.Add(maskImage);
                ID2D1Image maskAlpha = maskImage;
                if (mask.Luminosity)
                {
                    // Mask value = luminosity of the group composited over its backdrop (11.6.5.2).
                    var lum = new Vortice.Direct2D1.Effects.ColorMatrix(ctx)
                    {
                        Matrix = new Matrix5x4(
                            0, 0, 0, 0.30f,
                            0, 0, 0, 0.59f,
                            0, 0, 0, 0.11f,
                            0, 0, 0, 0,
                            0, 0, 0, 0),
                    };
                    disposables.Add(lum);
                    lum.SetInput(0, maskAlpha, true);
                    maskAlpha = lum.Output;
                    disposables.Add(maskAlpha);
                }
                if (mask.Transfer is { Count: >= 2 } tr)
                {
                    var table = new Vortice.Direct2D1.Effects.TableTransfer(ctx)
                    {
                        AlphaTable = tr.ToArray(),
                        RedDisable = true,
                        GreenDisable = true,
                        BlueDisable = true,
                        ClampOutput = true,
                    };
                    disposables.Add(table);
                    table.SetInput(0, maskAlpha, true);
                    maskAlpha = table.Output;
                    disposables.Add(maskAlpha);
                }
                var apply = new Vortice.Direct2D1.Effects.AlphaMask(ctx);
                disposables.Add(apply);
                apply.SetInput(0, image, true);
                apply.SetInput(1, maskAlpha, true);
                image = apply.Output;
                disposables.Add(image);
            }

            if (alpha < 1)
            {
                var opacity = new Vortice.Direct2D1.Effects.Opacity(ctx) { Value = (float)alpha };
                disposables.Add(opacity);
                opacity.SetInput(0, image, true);
                image = opacity.Output;
                disposables.Add(image);
            }

            if (!blends)
            {
                ctx.Transform = Matrix3x2.Identity;
                ctx.DrawImage(image, new Vector2(x0, y0), null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
                return;
            }

            // Blend: composite the surface's layers so the target holds the backdrop, blend, write
            // the rectangle back, and push the layers again for what follows.
            var layers = surface.Layers.ToArray();
            while (surface.Layers.Count > 0) PopLayer(surface);
            ctx.Flush(out _, out _);
            var backdrop = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            disposables.Add(backdrop);
            backdrop.CopyFromBitmap(new System.Drawing.Point(0, 0), surface.Target, new System.Drawing.Rectangle(x0, y0, w, h));

            ID2D1Image dest = backdrop, src = image;
            if (InvertColors)
            {
                // Night mode inverts every paint; blend modes do not commute with inversion, so
                // blend the true colours and invert the result.
                dest = Invert(ctx, dest, disposables);
                src = Invert(ctx, src, disposables);
            }
            var blendEffect = new Vortice.Direct2D1.Effects.Blend(ctx) { Mode = ToD2D(blend) };
            disposables.Add(blendEffect);
            blendEffect.SetInput(0, dest, true);
            blendEffect.SetInput(1, src, true);
            ID2D1Image result = blendEffect.Output;
            disposables.Add(result);
            if (InvertColors)
                result = Invert(ctx, result, disposables);

            ctx.Transform = Matrix3x2.Identity;
            // SourceCopy replaces everything inside the clip, including where the image is empty:
            // bound it to the group rectangle.
            ctx.PushAxisAlignedClip(new RawRectF(x0, y0, x0 + w, y0 + h), AntialiasMode.Aliased);
            ctx.DrawImage(result, new Vector2(x0, y0), null, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            ctx.PopAxisAlignedClip();
            foreach (var layer in layers)
                PushLayer(surface, layer.Mask, layer.Full, layer.Opacity, layer.Rect);
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i].Dispose();
        }
    }

    /// <summary>Copies the composited pixels under a rectangle (layers popped and pushed again).</summary>
    private ID2D1Bitmap1 CopyBackdrop(Surface surface, int x0, int y0, int w, int h)
    {
        var layers = surface.Layers.ToArray();
        while (surface.Layers.Count > 0) PopLayer(surface);
        surface.Ctx.Flush(out _, out _);
        var copy = surface.Ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        copy.CopyFromBitmap(new System.Drawing.Point(0, 0), surface.Target, new System.Drawing.Rectangle(x0, y0, w, h));
        foreach (var layer in layers)
            PushLayer(surface, layer.Mask, layer.Full, layer.Opacity, layer.Rect);
        return copy;
    }

    /// <summary>
    /// One element of a knockout group: G = G·(1 − f) + E + (f − αE)·C0, where E is the element
    /// rendered alone (with the current clips), f its shape and C0 the group's initial backdrop
    /// (absent for isolated groups).
    /// </summary>
    private void DrawKnockoutElement(Surface surface, Knockout knockout, int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage)
    {
        if (_res.Device is not { } device)
            return;
        ID2D1Bitmap1 Render(bool shape)
        {
            var bmp = surface.Ctx.CreateBitmap(new SizeI(surface.Width, surface.Height), IntPtr.Zero, 0, OffscreenProps);
            using var ectx = device.CreateDeviceContext(DeviceContextOptions.None);
            ectx.Target = bmp;
            ectx.BeginDraw();
            ectx.AntialiasMode = AntialiasMode.PerPrimitive;
            ectx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            ectx.Clear(new Color4(0, 0, 0, 0));
            using var ectx1 = surface.Ctx1 != null ? ectx.QueryInterfaceOrNull<ID2D1DeviceContext1>() : null;
            var sub = new Surface { Ctx = ectx, Ctx1 = ectx1, Target = bmp, Width = surface.Width, Height = surface.Height };
            bool savedShape = _shapeMode;
            _shapeMode = shape;
            try
            {
                foreach (var layer in surface.Layers)
                    PushLayer(sub, layer.Mask, layer.Full, 1f, layer.Rect);
                DrawRange(sub, start, end, pageToDevice, ctm, visiblePage);
            }
            finally
            {
                _shapeMode = savedShape;
                while (sub.Layers.Count > 0) PopLayer(sub);
                ectx.Transform = Matrix3x2.Identity;
                ectx.EndDraw();
                ectx.Target = null;
            }
            return bmp;
        }

        using var element = Render(shape: false);
        using var shapeBmp = Render(shape: true);
        var ctx = surface.Ctx;
        var disposables = new List<IDisposable>();
        var layers = surface.Layers.ToArray();
        while (surface.Layers.Count > 0) PopLayer(surface);
        try
        {
            ID2D1Image add = element;
            if (knockout.Backdrop is { } c0)
            {
                // (f − αE) broadcast to all channels, times C0, plus E.
                var fRgb = AlphaToRgb(ctx, shapeBmp, disposables);
                var eRgb = AlphaToRgb(ctx, element, disposables);
                var diff = Arithmetic(ctx, fRgb, eRgb, new Vector4(0, 1, -1, 0), disposables);   // f − αE
                var times = Arithmetic(ctx, c0, diff, new Vector4(1, 0, 0, 0), disposables);    // · C0
                add = Arithmetic(ctx, element, times, new Vector4(0, 1, 1, 0), disposables);   // + E
            }
            ctx.Transform = Matrix3x2.Identity;
            ctx.DrawImage(shapeBmp, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.DestinationOut);
            ctx.DrawImage(add, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.Plus);
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i].Dispose();
            foreach (var layer in layers)
                PushLayer(surface, layer.Mask, layer.Full, layer.Opacity, layer.Rect);
        }
    }

    private static ID2D1Image AlphaToRgb(ID2D1DeviceContext ctx, ID2D1Image input, List<IDisposable> disposables)
    {
        var m = new Vortice.Direct2D1.Effects.ColorMatrix(ctx) { Matrix = new Matrix5x4(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0) };
        disposables.Add(m);
        m.SetInput(0, input, true);
        var o = m.Output;
        disposables.Add(o);
        return o;
    }

    /// <summary>ArithmeticComposite: C1·I0·I1 + C2·I0 + C3·I1 + C4 (I0 = input 0, I1 = input 1; premultiplied, per channel).</summary>
    private static ID2D1Image Arithmetic(ID2D1DeviceContext ctx, ID2D1Image destination, ID2D1Image source, Vector4 k, List<IDisposable> disposables)
    {
        var a = new Vortice.Direct2D1.Effects.ArithmeticComposite(ctx) { Coefficients = k, ClampOutput = true };
        disposables.Add(a);
        a.SetInput(0, destination, true);
        a.SetInput(1, source, true);
        var o = a.Output;
        disposables.Add(o);
        return o;
    }

    private static ID2D1Image Invert(ID2D1DeviceContext ctx, ID2D1Image input, List<IDisposable> disposables)
    {
        var inv = new Vortice.Direct2D1.Effects.Invert(ctx);
        disposables.Add(inv);
        inv.SetInput(0, input, true);
        var output = inv.Output;
        disposables.Add(output);
        return output;
    }

    private static Vortice.Direct2D1.BlendMode ToD2D(PdfBlendMode mode) => mode switch
    {
        PdfBlendMode.Multiply => Vortice.Direct2D1.BlendMode.Multiply,
        PdfBlendMode.Screen => Vortice.Direct2D1.BlendMode.Screen,
        PdfBlendMode.Overlay => Vortice.Direct2D1.BlendMode.Overlay,
        PdfBlendMode.Darken => Vortice.Direct2D1.BlendMode.Darken,
        PdfBlendMode.Lighten => Vortice.Direct2D1.BlendMode.Lighten,
        PdfBlendMode.ColorDodge => Vortice.Direct2D1.BlendMode.ColorDodge,
        PdfBlendMode.ColorBurn => Vortice.Direct2D1.BlendMode.ColorBurn,
        PdfBlendMode.HardLight => Vortice.Direct2D1.BlendMode.HardLight,
        PdfBlendMode.SoftLight => Vortice.Direct2D1.BlendMode.SoftLight,
        PdfBlendMode.Difference => Vortice.Direct2D1.BlendMode.Difference,
        PdfBlendMode.Exclusion => Vortice.Direct2D1.BlendMode.Exclusion,
        PdfBlendMode.Hue => Vortice.Direct2D1.BlendMode.Hue,
        PdfBlendMode.Saturation => Vortice.Direct2D1.BlendMode.Saturation,
        PdfBlendMode.Color => Vortice.Direct2D1.BlendMode.Color,
        PdfBlendMode.Luminosity => Vortice.Direct2D1.BlendMode.Luminosity,
        _ => Vortice.Direct2D1.BlendMode.Multiply,
    };

    private readonly Dictionary<PdfSoftMask, Replayer> _maskReplayers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The soft mask's group rendered over its backdrop colour (luminosity) or transparent (alpha)
    /// for the w × h device rectangle. Mask content is drawn in true colours even in night mode:
    /// the mask shapes opacity, and inverting it would invert the mask.
    /// </summary>
    private ID2D1Bitmap1? RenderSoftMask(PdfSoftMask mask, int w, int h, Matrix3x2 pageToDevice, bool realize)
    {
        if (_res.Device is not { } device)
            return null;
        if (!_maskReplayers.TryGetValue(mask, out var replayer))
        {
            replayer = new Replayer(_res, mask.Content, null, false, _ct) { InvertColors = false };
            _maskReplayers[mask] = replayer;
        }
        replayer.UseRealizations = realize;

        var bitmap = _res.Context!.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, OffscreenProps);
        using var mctx = device.CreateDeviceContext(DeviceContextOptions.None);
        mctx.Target = bitmap;
        mctx.BeginDraw();
        mctx.AntialiasMode = AntialiasMode.PerPrimitive;
        mctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        try
        {
            var bc = mask.Backdrop;
            mctx.Clear(mask.Luminosity ? new Color4(bc.R, bc.G, bc.B, 1) : new Color4(0, 0, 0, 0));
            var maskToDevice = M(mask.MaskToPage) * pageToDevice;
            replayer.Draw(mctx, maskToDevice, new PdfRect(-1e6, -1e6, 2e6, 2e6));
        }
        finally
        {
            mctx.EndDraw();
            mctx.Target = null;
        }
        return bitmap;
    }

    // ------------------------------------------------------------------ tiling patterns

    /// <summary>Largest cell bitmap side; finer cells are resampled (the pattern stays seamless).</summary>
    private const int MaxCellPixels = 2048;

    private readonly Dictionary<PdfTilingPattern, (Replayer Replayer, Dictionary<(int W, int H), ID2D1Bitmap1> Cells)> _tilingCells =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// One period of the pattern rasterized at device resolution (every tile whose BBox reaches into
    /// the period contributes), then used as a wrapping bitmap brush over the painted area.
    /// </summary>
    private void PaintTiling(ID2D1DeviceContext ctx, DrawTilingPattern cmd, Matrix3x2 ctm, Matrix3x2 pageToDevice, PdfRect visiblePage)
    {
        var pattern = cmd.Pattern;
        var full = ctm * pageToDevice; // pattern space → device
        double sx = Math.Sqrt(full.M11 * full.M11 + full.M12 * full.M12);
        double sy = Math.Sqrt(full.M21 * full.M21 + full.M22 * full.M22);
        double xs = pattern.XStep, ys = pattern.YStep;
        int bw = (int)Math.Clamp(Math.Ceiling(xs * sx), 1, MaxCellPixels);
        int bh = (int)Math.Clamp(Math.Ceiling(ys * sy), 1, MaxCellPixels);

        if (!_tilingCells.TryGetValue(pattern, out var entry))
        {
            entry = (new Replayer(_res, pattern.Cell, null, false, _ct) { InvertColors = InvertColors }, new());
            _tilingCells[pattern] = entry;
        }
        if (!entry.Cells.TryGetValue((bw, bh), out var cell))
        {
            cell = RenderCell(entry.Replayer, pattern, bw, bh);
            if (cell == null)
                return;
            entry.Cells[(bw, bh)] = cell;
        }

        var area = cmd.Bounds is PdfRect b ? Intersect(b, visiblePage) : visiblePage;
        if (area.IsEmpty || !Matrix3x2.Invert(ctm, out var pageToPattern))
            return;

        using var geom = Polygon(pageToPattern, area);
        if (_shapeMode)
        {
            // The shape of a pattern fill is the painted area, not the cell's coverage.
            using var shapeBrush = ctx.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
            ctx.Transform = full;
            ctx.FillGeometry(geom, shapeBrush);
            return;
        }
        var bitmapToPattern = Matrix3x2.CreateScale((float)(xs / bw), (float)(ys / bh)) *
                              Matrix3x2.CreateTranslation((float)pattern.BBox.X, (float)pattern.BBox.Y);
        using var brush = ctx.CreateBitmapBrush(cell, new BitmapBrushProperties1(ExtendMode.Wrap, ExtendMode.Wrap, InterpolationMode.Linear),
            new BrushProperties(1f, bitmapToPattern));
        ctx.Transform = full;
        ctx.FillGeometry(geom, brush);
    }

    private ID2D1Bitmap1? RenderCell(Replayer replayer, PdfTilingPattern pattern, int bw, int bh)
    {
        if (_res.Device is not { } device)
            return null;
        double xs = pattern.XStep, ys = pattern.YStep;
        var box = pattern.BBox;
        var bitmap = _res.Context!.CreateBitmap(new SizeI(bw, bh), IntPtr.Zero, 0, OffscreenProps);
        using var cctx = device.CreateDeviceContext(DeviceContextOptions.None);
        cctx.Target = bitmap;
        cctx.BeginDraw();
        cctx.AntialiasMode = AntialiasMode.PerPrimitive;
        cctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        try
        {
            cctx.Clear(new Color4(0, 0, 0, 0));
            var toBitmap = Matrix3x2.CreateScale((float)(bw / xs), (float)(bh / ys));
            // Tiles (i, j) whose BBox, shifted by (i·XStep, j·YStep), reaches into the period [box.X, +XStep) × [box.Y, +YStep).
            int ni = (int)Math.Min(8, Math.Ceiling(box.Width / xs)), nj = (int)Math.Min(8, Math.Ceiling(box.Height / ys));
            for (int i = -ni; i <= ni; i++)
            for (int j = -nj; j <= nj; j++)
            {
                double ox = box.X + i * xs, oy = box.Y + j * ys;
                if (ox >= box.X + xs || ox + box.Width <= box.X || oy >= box.Y + ys || oy + box.Height <= box.Y)
                    continue;
                var cellToBitmap = Matrix3x2.CreateTranslation((float)(i * xs - box.X), (float)(j * ys - box.Y)) * toBitmap;
                replayer.Draw(cctx, cellToBitmap, new PdfRect(-1e7, -1e7, 2e7, 2e7));
            }
        }
        finally
        {
            cctx.EndDraw();
            cctx.Target = null;
        }
        return bitmap;
    }

    private static PdfRect Intersect(PdfRect a, PdfRect b)
    {
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        double x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        return x1 > x0 && y1 > y0 ? new PdfRect(x0, y0, x1 - x0, y1 - y0) : PdfRect.Empty;
    }

    private Color4 Color(PdfColor c, double alpha) => _shapeMode
        ? new(0, 0, 0, alpha > 0 ? 1 : 0)
        : InvertColors
            ? new(1 - c.R, 1 - c.G, 1 - c.B, (float)Math.Clamp(alpha, 0, 1))
            : new(c.R, c.G, c.B, (float)Math.Clamp(alpha, 0, 1));

    private void PushLayer(Surface surface, ID2D1Geometry? mask, Matrix3x2 full, float opacity, PdfRect? rect = null)
    {
        // A rectangle under an axis-preserving transform is an axis-aligned clip: D2D clips it in
        // the rasterizer, while a geometric-mask layer renders its content to an offscreen surface
        // and composites it back (per nested clip, per tile — the dominant cost on text pages).
        RawRectF? device = rect is { } r && opacity >= 1f ? DeviceRect(r, full) : null;
        bool axis = device.HasValue;
        if (axis)
        {
            surface.Ctx.Transform = Matrix3x2.Identity;
            surface.Ctx.PushAxisAlignedClip(device!.Value, AntialiasMode.PerPrimitive);
        }
        else
        {
            PushLayer(surface.Ctx, mask, full, opacity);
        }
        surface.Layers.Add((mask, full, opacity, rect));
        surface.AxisAligned.Add(axis);
    }

    private static void PopLayer(Surface surface)
    {
        int last = surface.Layers.Count - 1;
        bool axis = surface.AxisAligned[last];
        surface.Layers.RemoveAt(last);
        surface.AxisAligned.RemoveAt(last);
        if (axis) surface.Ctx.PopAxisAlignedClip();
        else surface.Ctx.PopLayer();
    }

    /// <summary>The path's rectangle when it is exactly one axis-aligned rectangle (the <c>re W n</c> idiom).</summary>
    internal static PdfRect? AxisRectangle(PdfPath path)
    {
        var segs = path.Segments;
        if (segs.Count is < 4 or > 6 || segs[0] is not PdfMoveTo m)
            return null;
        Span<PdfPoint> pts = stackalloc PdfPoint[5];
        pts[0] = m.Point;
        int n = 1;
        for (int i = 1; i < segs.Count; i++)
        {
            switch (segs[i])
            {
                case PdfLineTo l when n < 5:
                    pts[n++] = l.Point;
                    break;
                case PdfCloseSubpath when i == segs.Count - 1:
                    break;
                default:
                    return null;
            }
        }
        if (n == 5)
        {
            if (pts[4] != pts[0]) return null; // explicit return to the start
            n = 4;
        }
        if (n != 4)
            return null;
        // Each edge horizontal or vertical, alternating.
        bool hFirst = pts[0].Y == pts[1].Y;
        for (int i = 0; i < 4; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % 4];
            bool horizontal = (i % 2 == 0) == hFirst;
            if (horizontal ? a.Y != b.Y : a.X != b.X)
                return null;
        }
        double x0 = Math.Min(pts[0].X, pts[2].X), x1 = Math.Max(pts[0].X, pts[2].X);
        double y0 = Math.Min(pts[0].Y, pts[2].Y), y1 = Math.Max(pts[0].Y, pts[2].Y);
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Device rectangle of a user-space rectangle, or null when the transform rotates or skews it.</summary>
    private static RawRectF? DeviceRect(PdfRect r, Matrix3x2 full)
    {
        const float eps = 1e-5f;
        bool axisPreserving = (MathF.Abs(full.M12) < eps && MathF.Abs(full.M21) < eps) ||
                              (MathF.Abs(full.M11) < eps && MathF.Abs(full.M22) < eps);
        if (!axisPreserving)
            return null;
        var a = Vector2.Transform(new Vector2((float)r.X, (float)r.Y), full);
        var b = Vector2.Transform(new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height)), full);
        return new RawRectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    private void PushLayer(ID2D1DeviceContext ctx, ID2D1Geometry? mask, Matrix3x2 full, float opacity)
    {
        // The mask is given in user space; with an identity world transform MaskTransform is the
        // complete user → device mapping, which keeps the semantics unambiguous.
        ctx.Transform = Matrix3x2.Identity;
        var bounds = new RawRectF(-1e7f, -1e7f, 1e7f, 1e7f);
        if (mask != null)
        {
            var b = mask.GetBounds(full);
            if (b.Right >= b.Left && b.Bottom >= b.Top)
                bounds = new RawRectF(MathF.Floor(b.Left) - 1, MathF.Floor(b.Top) - 1, MathF.Ceiling(b.Right) + 1, MathF.Ceiling(b.Bottom) + 1);
        }
        var p = new LayerParameters1
        {
            ContentBounds = bounds,
            GeometricMask = mask!,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = full,
            Opacity = opacity,
            OpacityBrush = null!,
            LayerOptions = LayerOptions1.None,
        };
        ctx.PushLayer(p, null!); // D2D 1.1 accepts a null layer (it manages one internally)
    }

    private ID2D1PathGeometry1? BuildGeometry(PdfPath path, PdfFillRule rule)
    {
        if (path.Segments.Count == 0)
            return null;
        var geom = _res.Factory.CreatePathGeometry();
        using var sink = geom.Open();
        sink.SetFillMode(rule == PdfFillRule.EvenOdd ? FillMode.Alternate : FillMode.Winding);
        bool open = false;
        Vector2 start = default, current = default;
        foreach (var seg in path.Segments)
        {
            switch (seg)
            {
                case PdfMoveTo m:
                    if (open) sink.EndFigure(FigureEnd.Open);
                    start = current = V(m.Point);
                    sink.BeginFigure(start, FigureBegin.Filled);
                    open = true;
                    break;
                case PdfLineTo l:
                    if (!open) { sink.BeginFigure(current, FigureBegin.Filled); start = current; open = true; }
                    current = V(l.Point);
                    sink.AddLine(current);
                    break;
                case PdfCubicBezierTo c:
                    if (!open) { sink.BeginFigure(current, FigureBegin.Filled); start = current; open = true; }
                    current = V(c.EndPoint);
                    sink.AddBezier(new BezierSegment { Point1 = V(c.Control1), Point2 = V(c.Control2), Point3 = current });
                    break;
                case PdfCloseSubpath:
                    if (open)
                    {
                        sink.EndFigure(FigureEnd.Closed);
                        open = false;
                        current = start; // after h the current point is the subpath start (8.5.2.1)
                    }
                    break;
            }
        }
        if (open) sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geom;
    }

    private static Vector2 V(PdfPoint p) => new((float)p.X, (float)p.Y);

    /// <summary>
    /// PDF strokes: width in user space; width 0 — and any width that maps below one device
    /// pixel — draws the thinnest line the device can (8.4.3.2), which Direct2D renders natively.
    /// </summary>
    private ID2D1GeometryRealization? Realize(ID2D1DeviceContext1 ctx1, PdfDrawCommand cmd, ID2D1Geometry geom, Matrix3x2 full,
        (float Width, ID2D1StrokeStyle1 Style)? stroke)
    {
        if (_realizations.TryGetValue(cmd, out var cached))
            return cached;
        ID2D1GeometryRealization? r = null;
        try
        {
            // Default tolerance (0.25 device px) expressed in user space for this transform.
            float scale = D2D1.D2D1ComputeMaximumScaleFactor(ref full);
            float tolerance = 0.25f / Math.Max(scale, 1e-6f);
            r = stroke is { } s
                ? ctx1.CreateStrokedGeometryRealization(geom, tolerance, s.Width, s.Style)
                : ctx1.CreateFilledGeometryRealization(geom, tolerance);
        }
        catch (SharpGenException)
        {
            r = null;
        }
        _realizations[cmd] = r;
        return r;
    }

    private readonly Dictionary<PushTextClip, ID2D1Geometry?> _textOutlines = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Union of the runs' glyph outlines in user space (nonzero). A run whose font cannot be
    /// outlined contributes its box instead (the region is classified by <see cref="Analyze"/>).
    /// </summary>
    private ID2D1Geometry? TextOutline(PushTextClip cmd, Matrix3x2 ctm)
    {
        if (_textOutlines.TryGetValue(cmd, out var cached))
            return cached;
        var parts = new List<ID2D1Geometry>();
        foreach (var run in cmd.Runs)
        {
            if (run.FontSize == 0 || run.Glyphs.Count == 0)
                continue;
            var resolved = ResolveText(new DrawGlyphRun(run, default!, cmd.Bounds), out _);
            if (resolved is not { } r)
            {
                if (cmd.Bounds is PdfRect b && Matrix3x2.Invert(ctm, out var pageToUser))
                    parts.Add(Polygon(pageToUser, b));
                continue;
            }
            double th = run.HorizontalScaling / 100.0;
            float sign = run.FontSize < 0 ? -1 : 1;
            var glyphToUser = new Matrix3x2((float)(sign * th), 0, 0, -sign, 0, (float)run.TextRise) * M(run.TextMatrix);
            using var outline = _res.Factory.CreatePathGeometry();
            using (var sink = outline.Open())
            {
                sink.SetFillMode(FillMode.Winding);
                r.Face.GetGlyphRunOutline((float)Math.Abs(run.FontSize), r.Indices, new float[r.Indices.Length],
                    r.Offsets.Select(o => new GlyphOffset { AdvanceOffset = o.X, AscenderOffset = o.Y }).ToArray(), false, false, sink);
                sink.Close();
            }
            parts.Add(_res.Factory.CreateTransformedGeometry(outline, glyphToUser));
        }
        ID2D1Geometry? result = parts.Count switch
        {
            0 => _res.Factory.CreatePathGeometry(), // empty clip: nothing shows through
            1 => parts[0],
            _ => _res.Factory.CreateGeometryGroup(FillMode.Winding, parts.ToArray()),
        };
        if (result is ID2D1PathGeometry empty && parts.Count == 0)
        {
            using var s = empty.Open();
            s.Close();
        }
        if (parts.Count > 1)
            foreach (var p in parts) p.Dispose();
        _textOutlines[cmd] = result;
        return result;
    }

    private readonly Dictionary<(PushStrokeClip Cmd, float Scale), ID2D1PathGeometry1?> _strokeOutlines = new();

    /// <summary>The area a stroke covers, in user space (hairlines: one device pixel wide).</summary>
    private ID2D1PathGeometry1? StrokeOutline(PushStrokeClip cmd, Matrix3x2 full)
    {
        float scale = MathF.Sqrt(MathF.Abs(full.GetDeterminant()));
        var key = (cmd, MathF.Round(scale, 3));
        if (_strokeOutlines.TryGetValue(key, out var cached))
            return cached;
        ID2D1PathGeometry1? outline = null;
        if (Geometry(cmd.Path, PdfFillRule.NonZero) is { } geom && scale > 0)
        {
            var stroke = cmd.Stroke;
            float width = (float)Math.Max(stroke.Width, 1.0 / scale);
            var cap = stroke.Cap switch { PdfLineCap.Round => CapStyle.Round, PdfLineCap.Square => CapStyle.Square, _ => CapStyle.Flat };
            float[]? dashes = stroke.DashArray is { Count: > 0 } d
                ? Enumerable.Range(0, d.Count % 2 == 1 ? 2 : 1).SelectMany(_ => d).Select(v => (float)(v / width)).ToArray()
                : null;
            var style = _res.StrokeStyle(new StrokeStyleProperties1
            {
                StartCap = cap,
                EndCap = cap,
                DashCap = cap,
                LineJoin = stroke.Join switch { PdfLineJoin.Round => LineJoin.Round, PdfLineJoin.Bevel => LineJoin.Bevel, _ => LineJoin.MiterOrBevel },
                MiterLimit = (float)Math.Max(1, stroke.MiterLimit),
                DashStyle = dashes != null ? DashStyle.Custom : DashStyle.Solid,
                DashOffset = dashes != null ? (float)(stroke.DashPhase / width) : 0,
                TransformType = StrokeTransformType.Normal,
            }, dashes);
            outline = _res.Factory.CreatePathGeometry();
            using var sink = outline.Open();
            sink.SetFillMode(FillMode.Winding);
            geom.Widen(width, style, null, 0.25f / scale, sink);
            sink.Close();
        }
        _strokeOutlines[key] = outline;
        return outline;
    }

    private void Stroke(ID2D1DeviceContext ctx, ID2D1Geometry geom, ID2D1Brush brush, PdfStroke stroke, Matrix3x2 full,
        ID2D1DeviceContext1? ctx1 = null, PdfDrawCommand? cmd = null)
    {
        float scale = MathF.Sqrt(MathF.Abs(full.GetDeterminant()));
        bool hairline = stroke.Width * scale < 1.0;
        var cap = stroke.Cap switch { PdfLineCap.Round => CapStyle.Round, PdfLineCap.Square => CapStyle.Square, _ => CapStyle.Flat };
        float width = hairline ? 1f : (float)stroke.Width;
        float[]? dashes = null;
        if (stroke.DashArray is { Count: > 0 } d)
        {
            // Direct2D dashes are multiples of the stroke width (device pixels for hairlines).
            float unit = hairline ? 1f / Math.Max(scale, 1e-6f) : width;
            var list = new List<float>();
            for (int rep = 0; rep < (d.Count % 2 == 1 ? 2 : 1); rep++)
                foreach (var v in d) list.Add((float)(v / unit));
            dashes = list.ToArray();
        }
        var props = new StrokeStyleProperties1
        {
            StartCap = cap,
            EndCap = cap,
            DashCap = cap,
            LineJoin = stroke.Join switch { PdfLineJoin.Round => LineJoin.Round, PdfLineJoin.Bevel => LineJoin.Bevel, _ => LineJoin.MiterOrBevel },
            MiterLimit = (float)Math.Max(1, stroke.MiterLimit),
            DashStyle = dashes != null ? DashStyle.Custom : DashStyle.Solid,
            DashOffset = dashes != null ? (float)(stroke.DashPhase / (hairline ? 1f / Math.Max(scale, 1e-6f) : width)) : 0,
            TransformType = hairline ? StrokeTransformType.Hairline : StrokeTransformType.Normal,
        };
        var style = _res.StrokeStyle(props, dashes);
        ctx.Transform = full;
        // Hairlines depend on the device transform, so they are not realized.
        if (!hairline && ctx1 != null && cmd != null && Realize(ctx1, cmd, geom, full, (width, style)) is { } mesh)
            ctx1.DrawGeometryRealization(mesh, brush);
        else
            ctx.DrawGeometry(geom, brush, width, style);
    }

    // ------------------------------------------------------------------ text

    private string _lastTextProblem = string.Empty;

    /// <summary>Font face and glyph indices for a run; null when the backend cannot draw it faithfully.</summary>
    private (IDWriteFontFace Face, ushort[] Indices, Vector2[] Offsets)? ResolveText(DrawGlyphRun cmd, out bool byGlyphId)
    {
        var run = cmd.Run;
        byGlyphId = false;
        var face = run.Face;
        IDWriteFontFace? dw = null;
        if (face != null && !face.ProgramData.IsEmpty && face.Format is PdfFontProgramFormat.TrueType or PdfFontProgramFormat.OpenTypeCff)
        {
            dw = _res.GetEmbeddedFace(face);
            if (dw == null)
            {
                _lastTextProblem = "Embedded font program could not be loaded by DirectWrite";
                return null;
            }
            byGlyphId = true;
        }
        else if (face?.Format == PdfFontProgramFormat.Unsupported)
        {
            _lastTextProblem = "Embedded font program format is not loadable";
            return null;
        }

        bool symbol = false;
        if (dw == null)
        {
            var (family, bold, italic, isSymbol) = SubstituteFamily(face, run.FontFamilyName ?? run.FontResourceName);
            if (family == null)
            {
                _lastTextProblem = "No system substitute for font";
                return null;
            }
            dw = _res.GetSystemFace(family, bold, italic);
            if (dw == null)
            {
                _lastTextProblem = "Substitute font is not installed";
                return null;
            }
            symbol = isSymbol;
        }

        double th = run.HorizontalScaling / 100.0;
        double sign = run.FontSize < 0 ? -1 : 1;
        var indices = new List<ushort>(run.Glyphs.Count);
        var offsets = new List<Vector2>(run.Glyphs.Count);
        int glyphCount = dw.GlyphCount;
        foreach (var g in run.Glyphs)
        {
            ushort index;
            if (byGlyphId)
            {
                index = g.GlyphId < glyphCount ? g.GlyphId : (ushort)0;
            }
            else
            {
                string? text = g.Unicode;
                if (string.IsNullOrEmpty(text) || text == " " || text == " ")
                    continue;
                uint cp = symbol && g.CharCode >= 0 ? (uint)(0xF000 + g.CharCode) : (uint)char.ConvertToUtf32(text, 0);
                index = dw.GetGlyphIndices(new[] { cp })[0];
                if (index == 0)
                {
                    if (char.IsWhiteSpace(text, 0) || char.IsControl(text, 0))
                        continue;
                    _lastTextProblem = "Substitute font lacks a glyph used by this text";
                    return null;
                }
            }
            indices.Add(index);
            offsets.Add(new Vector2((float)(g.OffsetX / th * sign), (float)(g.OffsetY * sign)));
        }
        return (dw, indices.ToArray(), offsets.ToArray());
    }

    private static (string? Family, bool Bold, bool Italic, bool Symbol) SubstituteFamily(PdfFontFace? face, string? fontName)
    {
        string name = face?.PostScriptName ?? fontName ?? string.Empty;
        int plus = name.IndexOf('+');
        if (plus >= 0 && plus < name.Length - 1) name = name[(plus + 1)..];
        bool bold = face?.IsBold == true || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = face?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        if (name.Contains("Dingbat", StringComparison.OrdinalIgnoreCase))
            return (null, false, false, false);
        if (name.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase))
            return ("Symbol", false, false, true);
        if (face?.IsFixedPitch == true || name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase))
            return ("Courier New", bold, italic, false);
        if (face?.IsSerif == true || name.Contains("Times", StringComparison.OrdinalIgnoreCase) || (name.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !name.Contains("Sans", StringComparison.OrdinalIgnoreCase)))
            return ("Times New Roman", bold, italic, false);
        return ("Arial", bold, italic, false); // metric-compatible with Helvetica
    }

    private void DrawText(ID2D1DeviceContext ctx, DrawGlyphRun cmd, Matrix3x2 ctm, Matrix3x2 full)
    {
        var resolved = ResolveText(cmd, out _);
        if (resolved is not { } r || r.Indices.Length == 0 || cmd.Run.FontSize == 0)
            return;
        var run = cmd.Run;
        double th = run.HorizontalScaling / 100.0;
        float sign = run.FontSize < 0 ? -1 : 1;

        // Glyph space (y-down baseline at the origin, em = |Tfs|) → text space → user → device.
        var glyphToText = new Matrix3x2((float)(sign * th), 0, 0, -sign, 0, (float)run.TextRise);
        var transform = glyphToText * M(run.TextMatrix) * full;

        var glyphRun = new GlyphRun
        {
            FontFace = r.Face,
            FontEmSize = (float)Math.Abs(run.FontSize),
            Indices = r.Indices,
            Advances = new float[r.Indices.Length],
            Offsets = r.Offsets.Select(o => new GlyphOffset { AdvanceOffset = o.X, AscenderOffset = o.Y }).ToArray(),
        };

        int mode = run.RenderingMode;
        bool fill = mode is 0 or 2 or 4 or 6, stroke = mode is 1 or 2 or 5 or 6;
        ctx.Transform = transform;
        if (fill)
        {
            using var brush = ctx.CreateSolidColorBrush(Color(cmd.Paint.Color, cmd.Paint.Alpha));
            ctx.DrawGlyphRun(Vector2.Zero, glyphRun, brush, MeasuringMode.Natural);
        }
        if (stroke && run.StrokePaint is PdfPaint sp && run.Stroke is PdfStroke st)
        {
            using var outline = _res.Factory.CreatePathGeometry();
            using (var sink = outline.Open())
            {
                r.Face.GetGlyphRunOutline(glyphRun.FontEmSize, glyphRun.Indices, glyphRun.Advances, glyphRun.Offsets, false, false, sink);
                sink.Close();
            }
            using var brush = ctx.CreateSolidColorBrush(Color(sp.Color, sp.Alpha));
            // Stroke width is user space: express it in glyph space.
            float textScale = MathF.Sqrt(MathF.Abs((glyphToText * M(run.TextMatrix)).GetDeterminant()));
            Stroke(ctx, outline, brush, st with { Width = st.Width / Math.Max(textScale, 1e-6), DashArray = null }, transform);
        }
    }

    // ------------------------------------------------------------------ images

    private bool TryDecode(PdfImageRef image, out byte[] pixels, out int width, out int height, out PdfFallbackReason reason)
    {
        pixels = Array.Empty<byte>();
        width = height = 0;
        reason = PdfFallbackReason.ImageDecode;
        if (image.Source == null)
        {
            if (!image.ImageData.IsEmpty && image.Width > 0 && image.Height > 0 && image.ImageData.Length >= (long)image.Width * image.Height * 3)
            {
                width = image.Width; height = image.Height;
                pixels = new byte[width * height * 4];
                var rgb = image.ImageData.Span;
                for (int i = 0, p = 0; i < width * height; i++, p += 4)
                {
                    pixels[p] = rgb[i * 3 + 2]; pixels[p + 1] = rgb[i * 3 + 1]; pixels[p + 2] = rgb[i * 3]; pixels[p + 3] = 255;
                }
                return true;
            }
            return false;
        }

        if (_decoded.TryGetValue(image.Source.CacheKey, out var cached))
        {
            (pixels, width, height) = cached;
            return pixels.Length > 0;
        }

        try
        {
            var decoded = image.Source.Decode(_ct);
            width = decoded.Width;
            height = decoded.Height;
            pixels = decoded.Format == PdfDecodedImageFormat.Bgra32 ? decoded.Data.ToArray() : DecodeJpeg(decoded, out width, out height);
            if (decoded.Format == PdfDecodedImageFormat.Jpeg && decoded.Alpha is ReadOnlyMemory<byte> alpha)
                ApplyAlpha(pixels, width, height, alpha.Span, decoded.Width, decoded.Height);
            _decoded[image.Source.CacheKey] = (pixels, width, height);
            return true;
        }
        catch (Exception ex) when (ex is PdfEngine.Vector.Diagnostics.PdfVectorException or SharpGenException or InvalidDataException or IOException
                                       or ArgumentException or OverflowException or NotSupportedException)
        {
            reason = ex is PdfEngine.Vector.Diagnostics.PdfUnsupportedFeatureException u ? u.Reason
                   : ex is PdfEngine.Vector.Diagnostics.PdfResourceLimitException ? PdfFallbackReason.ResourceLimit
                   : PdfFallbackReason.ImageDecode;
            _decoded[image.Source.CacheKey] = (Array.Empty<byte>(), 0, 0);
            return false;
        }
    }

    // Decoded pixels are shared by analysis and drawing within one replay; bitmaps are cached on the device.
    private readonly Dictionary<string, (byte[] Pixels, int Width, int Height)> _decoded = new(StringComparer.Ordinal);

    private byte[] DecodeJpeg(PdfDecodedImage decoded, out int width, out int height)
    {
        using var stream = new MemoryStream(decoded.Data.ToArray(), writable: false);
        using var decoder = _res.Wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        width = frame.Size.Width;
        height = frame.Size.Height;
        var result = new byte[width * height * 4];
        if (frame.PixelFormat == Vortice.WIC.PixelFormat.Format32bppCMYK)
        {
            var cmyk = new byte[width * height * 4];
            frame.CopyPixels((uint)(width * 4), cmyk);
            for (int i = 0; i < cmyk.Length; i += 4)
            {
                int c = cmyk[i], m = cmyk[i + 1], y = cmyk[i + 2], k = cmyk[i + 3];
                if (decoded.InvertCmykJpeg) { c = 255 - c; m = 255 - m; y = 255 - y; k = 255 - k; }
                int kk = 255 - k;
                result[i] = (byte)((255 - y) * kk / 255);
                result[i + 1] = (byte)((255 - m) * kk / 255);
                result[i + 2] = (byte)((255 - c) * kk / 255);
                result[i + 3] = 255;
            }
            return result;
        }
        using var converter = _res.Wic.CreateFormatConverter();
        converter.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        converter.CopyPixels((uint)(width * 4), result);
        return result;
    }

    private static void ApplyAlpha(byte[] bgra, int w, int h, ReadOnlySpan<byte> alpha, int aw, int ah)
    {
        for (int y = 0; y < h; y++)
        {
            int sy = (int)((long)y * ah / h);
            for (int x = 0; x < w; x++)
            {
                int sx = (int)((long)x * aw / w);
                int ai = sy * aw + sx;
                if (ai < alpha.Length)
                {
                    int p = (y * w + x) * 4 + 3;
                    bgra[p] = (byte)(bgra[p] * alpha[ai] / 255);
                }
            }
        }
    }

    private void PaintImage(ID2D1DeviceContext ctx, DrawImage di, Matrix3x2 full)
    {
        var image = di.Image;
        if (_shapeMode && image.ColorSpaceName != "ImageMask")
        {
            // An image's shape is its whole parallelogram; soft masks are opacity (11.6.5.3).
            ctx.Transform = new Matrix3x2(1, 0, 0, -1, 0, 1) * M(di.Transform) * full;
            using var shapeBrush = ctx.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
            ctx.FillRectangle(new RawRectF(0, 0, 1, 1), shapeBrush);
            return;
        }
        string key = (image.Source?.CacheKey ?? image.ImageId) + (InvertColors ? "|inverted" : "");
        var bitmap = _res.GetBitmap(key);
        if (bitmap == null)
        {
            if (!TryDecode(image, out var pixels, out int w, out int h, out _))
                return;
            if (InvertColors)
            {
                pixels = (byte[])pixels.Clone();
                for (int p = 0; p + 3 < pixels.Length; p += 4)
                {
                    pixels[p] = (byte)(255 - pixels[p]);
                    pixels[p + 1] = (byte)(255 - pixels[p + 1]);
                    pixels[p + 2] = (byte)(255 - pixels[p + 2]);
                }
            }
            else
            {
                pixels = (byte[])pixels.Clone(); // the decoded copy is shared with analysis
            }
            // Direct2D draws premultiplied alpha.
            for (int p = 0; p < pixels.Length; p += 4)
            {
                int a = pixels[p + 3];
                if (a == 255) continue;
                pixels[p] = (byte)(pixels[p] * a / 255);
                pixels[p + 1] = (byte)(pixels[p + 1] * a / 255);
                pixels[p + 2] = (byte)(pixels[p + 2] * a / 255);
            }
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap = ctx.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(), (uint)(w * 4),
                    new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));
            }
            finally
            {
                handle.Free();
            }
            _res.AddBitmap(key, bitmap, (long)w * h * 4);
        }

        // The image fills the unit square of its space with the first row at the top (8.9.4).
        var imageToDevice = new Matrix3x2(1, 0, 0, -1, 0, 1) * M(di.Transform) * full;
        ctx.Transform = imageToDevice;
        var size = bitmap.PixelSize;
        float devicePerSourcePixel = MathF.Sqrt(MathF.Abs(imageToDevice.GetDeterminant()) / Math.Max(1, (float)size.Width * size.Height));
        var mode = devicePerSourcePixel < 1f ? Vortice.Direct2D1.InterpolationMode.HighQualityCubic
                 : !image.Interpolate && devicePerSourcePixel > 1.5f ? Vortice.Direct2D1.InterpolationMode.NearestNeighbor
                 : Vortice.Direct2D1.InterpolationMode.Linear;
        ctx.DrawBitmap(bitmap, new RawRectF(0, 0, 1, 1), _shapeMode ? 1f : (float)Math.Clamp(image.Opacity, 0, 1), mode, null, null);
    }

    // ------------------------------------------------------------------ shading

    private void PaintShading(ID2D1DeviceContext ctx, DrawShading ds, Matrix3x2 ctm, Matrix3x2 pageToDevice)
    {
        var full = ctm * pageToDevice;
        var area = ds.Bounds is PdfRect b && !b.IsEmpty ? b : _list.CropBox;
        if (!Matrix3x2.Invert(ctm, out var pageToUser))
            return;

        using var areaGeom = Polygon(pageToUser, area);
        GradientStop[] Stops(IReadOnlyList<PdfGradientStop>? stops, PdfColor start, PdfColor end) =>
            stops is { Count: > 0 }
                ? stops.Select(s => new GradientStop((float)Math.Clamp(s.Offset, 0, 1), Color(s.Color, 1))).ToArray()
                : new[] { new GradientStop(0, Color(start, 1)), new GradientStop(1, Color(end, 1)) };

        switch (ds.Shading)
        {
            case PdfAxialShading axial:
            {
                using var stops = ctx.CreateGradientStopCollection(Stops(axial.Stops, axial.StartColor, axial.EndColor), ExtendMode.Clamp);
                using var brush = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(V(axial.StartPoint), V(axial.EndPoint)), stops);
                bool band = !axial.ExtendStart || !axial.ExtendEnd;
                if (band)
                {
                    using var bandGeom = AxialBand(axial, pageToUser, area);
                    PushLayer(ctx, bandGeom, full, 1f);
                }
                ctx.Transform = full;
                ctx.FillGeometry(areaGeom, brush);
                if (band) ctx.PopLayer();
                break;
            }
            case PdfRadialShading { IsGeneral: true } general:
                PaintGeneralRadial(ctx, general, area, full, pageToDevice);
                break;
            case PdfMeshShading mesh:
                PaintMesh(ctx, mesh, area, full, pageToDevice);
                break;
            case PdfFunctionShading function:
                PaintFunctionShading(ctx, function, area, full, pageToDevice);
                break;
            case PdfRadialShading radial:
            {
                using var stops = ctx.CreateGradientStopCollection(Stops(radial.Stops, radial.StartColor, radial.EndColor), ExtendMode.Clamp);
                var center = V(radial.EndCenter);
                var offset = V(radial.StartCenter) - center;
                float r1 = (float)Math.Max(1e-6, radial.EndRadius);
                using var brush = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(center, offset, r1, r1), stops);
                if (!radial.ExtendEnd)
                {
                    using var circle = _res.Factory.CreateEllipseGeometry(new Ellipse(center, r1, r1));
                    PushLayer(ctx, circle, full, 1f);
                }
                ctx.Transform = full;
                ctx.FillGeometry(areaGeom, brush);
                if (!radial.ExtendEnd) ctx.PopLayer();
                break;
            }
        }
    }

    /// <summary>
    /// Radial shading between two arbitrary circles, evaluated per device pixel: the colour is
    /// that of the largest t with r(t) ≥ 0 whose circle passes through the pixel (8.7.4.5.4).
    /// Drawn under the current clip layers.
    /// </summary>
    private void PaintGeneralRadial(ID2D1DeviceContext ctx, PdfRadialShading sh, PdfRect area, Matrix3x2 userToDevice, Matrix3x2 pageToDevice)
    {
        using var target = ctx.Target.QueryInterface<ID2D1Bitmap1>();
        var size = target.PixelSize;
        var corners = new[]
        {
            Vector2.Transform(new Vector2((float)area.X, (float)area.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)area.Right, (float)area.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)area.X, (float)area.Bottom), pageToDevice),
            Vector2.Transform(new Vector2((float)area.Right, (float)area.Bottom), pageToDevice),
        };
        int x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X)));
        int y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y)));
        int x1 = Math.Min(size.Width, (int)Math.Ceiling(corners.Max(c => c.X)));
        int y1 = Math.Min(size.Height, (int)Math.Ceiling(corners.Max(c => c.Y)));
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0 || !Matrix3x2.Invert(userToDevice, out var deviceToUser))
            return;

        // 1024-entry colour table over t ∈ [0, 1] (premultiplied BGRA, opaque).
        var table = new uint[1024];
        var stops = sh.Stops is { Count: > 0 } st ? st : new[] { new PdfGradientStop(0, sh.StartColor), new PdfGradientStop(1, sh.EndColor) };
        for (int i = 0, s = 0; i < table.Length; i++)
        {
            double t = i / (double)(table.Length - 1);
            while (s < stops.Count - 2 && stops[s + 1].Offset < t) s++;
            PdfColor c;
            if (stops.Count == 1 || t <= stops[0].Offset) c = stops[0].Color;
            else if (t >= stops[^1].Offset) c = stops[^1].Color;
            else
            {
                var a = stops[s]; var b = stops[s + 1];
                double f = b.Offset > a.Offset ? (t - a.Offset) / (b.Offset - a.Offset) : 0;
                c = new PdfColor((float)(a.Color.R + (b.Color.R - a.Color.R) * f), (float)(a.Color.G + (b.Color.G - a.Color.G) * f), (float)(a.Color.B + (b.Color.B - a.Color.B) * f));
            }
            var col = Color(c, 1);
            table[i] = 0xFF000000u | ((uint)Math.Round(col.R * 255) << 16) | ((uint)Math.Round(col.G * 255) << 8) | (uint)Math.Round(col.B * 255);
        }

        double cx = sh.StartCenter.X, cy = sh.StartCenter.Y, r0 = sh.StartRadius;
        double dx = sh.EndCenter.X - cx, dy = sh.EndCenter.Y - cy, dr = sh.EndRadius - r0;
        double qa = dx * dx + dy * dy - dr * dr;
        bool e0 = sh.ExtendStart, e1 = sh.ExtendEnd;
        var pixels = new uint[w * h];
        System.Threading.Tasks.Parallel.For(0, h, row =>
        {
            _ct.ThrowIfCancellationRequested();
            for (int col = 0; col < w; col++)
            {
                // One sample at the pixel centre (PDFium does not antialias shading edges either).
                uint sb = 0, sg = 0, sr = 0, sa = 0;
                for (int k = 0; k < 1; k++)
                {
                    var u = Vector2.Transform(new Vector2(x0 + col + 0.5f, y0 + row + 0.5f), deviceToUser);
                    double px = u.X - cx, py = u.Y - cy;
                    double qb = px * dx + py * dy + r0 * dr;
                    double qc = px * px + py * py - r0 * r0;
                    double best = double.NaN;
                    void Try(double t)
                    {
                        if (double.IsNaN(t) || r0 + t * dr < 0) return;
                        if (t < 0 && !e0) return;
                        if (t > 1 && !e1) return;
                        if (double.IsNaN(best) || t > best) best = t;
                    }
                    if (Math.Abs(qa) < 1e-12)
                    {
                        if (Math.Abs(qb) > 1e-12) Try(qc / (2 * qb));
                    }
                    else
                    {
                        double disc = qb * qb - qa * qc;
                        if (disc >= 0)
                        {
                            double sq = Math.Sqrt(disc);
                            Try((qb + sq) / qa);
                            Try((qb - sq) / qa);
                        }
                    }
                    if (double.IsNaN(best))
                        continue;
                    uint c = table[(int)Math.Round(Math.Clamp(best, 0, 1) * (table.Length - 1))];
                    sb += c & 0xFF; sg += (c >> 8) & 0xFF; sr += (c >> 16) & 0xFF; sa += 255;
                }
                if (sa != 0)
                    pixels[row * w + col] = (sa << 24) | (sr << 16) | (sg << 8) | sb;
            }
        });

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bmp = ctx.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(), (uint)(w * 4),
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            ctx.Transform = Matrix3x2.Identity;
            ctx.DrawImage(bmp, new Vector2(x0, y0), null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Device rectangle of a page-space area, clamped to the current target.</summary>
    private static bool DeviceRect(ID2D1DeviceContext ctx, PdfRect area, Matrix3x2 pageToDevice, out int x0, out int y0, out int w, out int h)
    {
        using var target = ctx.Target.QueryInterface<ID2D1Bitmap1>();
        var size = target.PixelSize;
        var c = new[]
        {
            Vector2.Transform(new Vector2((float)area.X, (float)area.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)area.Right, (float)area.Y), pageToDevice),
            Vector2.Transform(new Vector2((float)area.X, (float)area.Bottom), pageToDevice),
            Vector2.Transform(new Vector2((float)area.Right, (float)area.Bottom), pageToDevice),
        };
        x0 = Math.Max(0, (int)Math.Floor(c.Min(p => p.X)));
        y0 = Math.Max(0, (int)Math.Floor(c.Min(p => p.Y)));
        int x1 = Math.Min(size.Width, (int)Math.Ceiling(c.Max(p => p.X)));
        int y1 = Math.Min(size.Height, (int)Math.Ceiling(c.Max(p => p.Y)));
        w = x1 - x0;
        h = y1 - y0;
        return w > 0 && h > 0;
    }

    private static void DrawPixels(ID2D1DeviceContext ctx, uint[] pixels, int w, int h, RawRectF destination, InterpolationMode interpolation)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bmp = ctx.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(), (uint)(w * 4),
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            ctx.Transform = Matrix3x2.Identity;
            ctx.DrawBitmap(bmp, destination, 1f, interpolation, null, null);
        }
        finally
        {
            handle.Free();
        }
    }

    private uint Opaque(PdfColor c)
    {
        var col = Color(c, 1);
        return 0xFF000000u | ((uint)Math.Round(Math.Clamp(col.R, 0, 1) * 255) << 16) |
               ((uint)Math.Round(Math.Clamp(col.G, 0, 1) * 255) << 8) | (uint)Math.Round(Math.Clamp(col.B, 0, 1) * 255);
    }

    /// <summary>
    /// Mesh shadings (types 4–7): Gouraud triangles and device-resolution patch subdivision on the
    /// CPU, drawn under the current clip. With a function, t is interpolated and then mapped.
    /// </summary>
    private void PaintMesh(ID2D1DeviceContext ctx, PdfMeshShading mesh, PdfRect area, Matrix3x2 userToDevice, Matrix3x2 pageToDevice)
    {
        if (!DeviceRect(ctx, area, pageToDevice, out int x0, out int y0, out int w, out int h))
            return;
        var target = new ShadingRasterizer.Target(x0, y0, w, h);
        uint[]? lut = null;
        if (mesh.FunctionLut is { Count: > 0 } fl)
        {
            lut = new uint[fl.Count];
            for (int i = 0; i < lut.Length; i++) lut[i] = Opaque(fl[i]);
        }
        uint ToPixel(ShadingRasterizer.Shade s) => lut != null
            ? lut[(int)Math.Round(Math.Clamp(s.T, 0, 1) * (lut.Length - 1))]
            : Opaque(new PdfColor(s.R, s.G, s.B));
        ShadingRasterizer.Shade ShadeOf(PdfColor c, double t) => new(c.R, c.G, c.B, (float)t);

        var tris = mesh.Triangles;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            if ((i & 0xFFF) == 0) _ct.ThrowIfCancellationRequested();
            var a = tris[i]; var b = tris[i + 1]; var c = tris[i + 2];
            ShadingRasterizer.FillTriangle(target,
                Vector2.Transform(new Vector2((float)a.X, (float)a.Y), userToDevice),
                Vector2.Transform(new Vector2((float)b.X, (float)b.Y), userToDevice),
                Vector2.Transform(new Vector2((float)c.X, (float)c.Y), userToDevice),
                ShadeOf(a.Color, a.T), ShadeOf(b.Color, b.T), ShadeOf(c.Color, c.T), ToPixel);
        }
        var corners = new ShadingRasterizer.Shade[4];
        for (int p = 0; p < mesh.Patches.Count; p++)
        {
            if ((p & 0x3F) == 0) _ct.ThrowIfCancellationRequested();
            var patch = mesh.Patches[p];
            for (int k = 0; k < 4; k++) corners[k] = ShadeOf(patch.Colors[k], patch.T[k]);
            ShadingRasterizer.FillPatch(target, patch, userToDevice, corners, ToPixel);
        }
        DrawPixels(ctx, target.Pixels, w, h, new RawRectF(x0, y0, x0 + w, y0 + h), InterpolationMode.NearestNeighbor);
    }

    /// <summary>Largest sample grid side for a function shading (bilinear between samples beyond it).</summary>
    private const int MaxFunctionSamples = 768;

    /// <summary>
    /// Type 1 shading: f(x, y) sampled at device pixel centres (up to 768 per side, then bilinear),
    /// transparent outside /Domain.
    /// </summary>
    private void PaintFunctionShading(ID2D1DeviceContext ctx, PdfFunctionShading sh, PdfRect area, Matrix3x2 userToDevice, Matrix3x2 pageToDevice)
    {
        if (!DeviceRect(ctx, area, pageToDevice, out int x0, out int y0, out int w, out int h))
            return;
        var shadingToDevice = M(sh.Matrix) * userToDevice;
        if (!Matrix3x2.Invert(shadingToDevice, out var deviceToShading))
            return;
        int sw = Math.Min(w, MaxFunctionSamples), sh2 = Math.Min(h, MaxFunctionSamples);
        var pixels = new uint[sw * sh2];
        for (int j = 0; j < sh2; j++)
        {
            if ((j & 31) == 0) _ct.ThrowIfCancellationRequested();
            float dy = y0 + (j + 0.5f) * h / sh2;
            for (int i = 0; i < sw; i++)
            {
                float dx = x0 + (i + 0.5f) * w / sw;
                var s = Vector2.Transform(new Vector2(dx, dy), deviceToShading);
                var c = sh.Evaluate(s.X, s.Y);
                if (c.A <= 0) continue;
                pixels[j * sw + i] = Opaque(c);
            }
        }
        DrawPixels(ctx, pixels, sw, sh2, new RawRectF(x0, y0, x0 + w, y0 + h),
            sw == w && sh2 == h ? InterpolationMode.NearestNeighbor : InterpolationMode.Linear);
    }

    private ID2D1PathGeometry1 Polygon(Matrix3x2 pageToUser, PdfRect r)
    {
        var geom = _res.Factory.CreatePathGeometry();
        using var sink = geom.Open();
        sink.BeginFigure(Vector2.Transform(new Vector2((float)r.Left, (float)r.Top), pageToUser), FigureBegin.Filled);
        sink.AddLine(Vector2.Transform(new Vector2((float)r.Right, (float)r.Top), pageToUser));
        sink.AddLine(Vector2.Transform(new Vector2((float)r.Right, (float)r.Bottom), pageToUser));
        sink.AddLine(Vector2.Transform(new Vector2((float)r.Left, (float)r.Bottom), pageToUser));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geom;
    }

    private ID2D1PathGeometry1 AxialBand(PdfAxialShading axial, Matrix3x2 pageToUser, PdfRect area)
    {
        var s = V(axial.StartPoint);
        var d = V(axial.EndPoint) - s;
        float len = d.Length();
        var geom = _res.Factory.CreatePathGeometry();
        using var sink = geom.Open();
        if (len > 1e-6f)
        {
            var u = d / len;
            var v = new Vector2(-u.Y, u.X);
            var a = Vector2.Transform(new Vector2((float)area.Left, (float)area.Top), pageToUser);
            var c = Vector2.Transform(new Vector2((float)area.Right, (float)area.Bottom), pageToUser);
            float far = (a - c).Length() * 4 + len * 4 + 1;
            float u0 = axial.ExtendStart ? -far : 0, u1 = axial.ExtendEnd ? len + far : len;
            Vector2 P(float uu, float vv) => s + u * uu + v * vv;
            sink.BeginFigure(P(u0, -far), FigureBegin.Filled);
            sink.AddLine(P(u1, -far));
            sink.AddLine(P(u1, far));
            sink.AddLine(P(u0, far));
            sink.EndFigure(FigureEnd.Closed);
        }
        sink.Close();
        return geom;
    }
}
