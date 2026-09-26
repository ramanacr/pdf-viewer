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

    /// <summary>Tessellate once and reuse across tiles; only worth it when a render spans several tiles.</summary>
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
    private readonly CancellationToken _ct;
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
        public readonly List<(ID2D1Geometry? Mask, Matrix3x2 Full, float Opacity)> Layers = new();
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
        var surface = new Surface { Ctx = ctx, Ctx1 = ctx1, Target = target, Width = size.Width, Height = size.Height };
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

    private void DrawRange(Surface surface, int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage)
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
                    PushLayer(surface, Geometry(pc.Path, pc.Rule), full, 1f);
                    break;
                case PushStrokeClip psc:
                    PushLayer(surface, StrokeOutline(psc, full), full, 1f);
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
                    DrawGroup(surface, cg.Bounds, cg.Alpha, cg.Blend, cg.SoftMask, i + 1, groupEnd, pageToDevice, ctm, visiblePage);
                    i = groupEnd;
                    break;
                }
                case BeginTransparencyGroup tg when _containsBlend![i]:
                {
                    int groupEnd = _groupEnd![i] < 0 ? end : Math.Min(_groupEnd[i], end);
                    DrawGroup(surface, tg.Bounds, tg.Alpha, PdfBlendMode.Normal, null, i + 1, groupEnd, pageToDevice, ctm, visiblePage);
                    i = groupEnd;
                    break;
                }
                case BeginTransparencyGroup g:
                    PushLayer(surface, null, full, (float)Math.Clamp(g.Alpha, 0, 1));
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
        int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage)
    {
        if (_res.Device is not { } device)
            return;

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

        using var content = surface.Ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, OffscreenProps);
        using (var gctx = device.CreateDeviceContext(DeviceContextOptions.None))
        {
            gctx.Target = content;
            gctx.BeginDraw();
            gctx.AntialiasMode = AntialiasMode.PerPrimitive;
            gctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            gctx.Clear(new Color4(0, 0, 0, 0));
            using var gctx1 = surface.Ctx1 != null ? gctx.QueryInterfaceOrNull<ID2D1DeviceContext1>() : null;
            var sub = new Surface { Ctx = gctx, Ctx1 = gctx1, Target = content, Width = w, Height = h };
            try
            {
                // A blended group replaces its rectangle on the surface, so its content must carry
                // the enclosing clips itself.
                if (blends)
                    foreach (var layer in surface.Layers)
                        PushLayer(sub, layer.Mask, layer.Full * offset, 1f);
                DrawRange(sub, start, end, groupPageToDevice, ctm, groupVisible);
            }
            finally
            {
                while (sub.Layers.Count > 0) PopLayer(sub);
                gctx.Transform = Matrix3x2.Identity;
                gctx.EndDraw();
                gctx.Target = null;
            }
        }

        var disposables = new List<IDisposable>();
        try
        {
            ID2D1Image image = content;
            var ctx = surface.Ctx;

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
                PushLayer(surface, layer.Mask, layer.Full, layer.Opacity);
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i].Dispose();
        }
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

    private Color4 Color(PdfColor c, double alpha) => InvertColors
        ? new(1 - c.R, 1 - c.G, 1 - c.B, (float)Math.Clamp(alpha, 0, 1))
        : new(c.R, c.G, c.B, (float)Math.Clamp(alpha, 0, 1));

    private void PushLayer(Surface surface, ID2D1Geometry? mask, Matrix3x2 full, float opacity)
    {
        PushLayer(surface.Ctx, mask, full, opacity);
        surface.Layers.Add((mask, full, opacity));
    }

    private static void PopLayer(Surface surface)
    {
        surface.Layers.RemoveAt(surface.Layers.Count - 1);
        surface.Ctx.PopLayer();
    }

    private void PushLayer(ID2D1DeviceContext ctx, ID2D1Geometry? mask, Matrix3x2 full, float opacity)
    {
        // The mask is given in user space; with an identity world transform MaskTransform is the
        // complete user → device mapping, which keeps the semantics unambiguous.
        ctx.Transform = Matrix3x2.Identity;
        var p = new LayerParameters1
        {
            ContentBounds = new RawRectF(-1e7f, -1e7f, 1e7f, 1e7f),
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
        ctx.DrawBitmap(bitmap, new RawRectF(0, 0, 1, 1), (float)Math.Clamp(image.Opacity, 0, 1), mode, null, null);
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
