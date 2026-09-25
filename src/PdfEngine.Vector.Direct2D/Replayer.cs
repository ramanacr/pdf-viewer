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

    private enum Push { Layer }

    public void Dispose()
    {
        ResetDeviceResources();
        foreach (var g in _geometries.Values) g?.Dispose();
        _geometries.Clear();
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
            _prepared = true;
        }

        var ctx1 = UseRealizations ? ctx.QueryInterfaceOrNull<ID2D1DeviceContext1>() : null;
        float renderScale = D2D1.D2D1ComputeMaximumScaleFactor(ref pageToDevice);
        if (Math.Abs(renderScale - _realizationScale) > 1e-4f)
        {
            ResetDeviceResources();
            _realizationScale = renderScale;
        }

        var pushes = new Stack<Push>();
        var frames = new Stack<(int Depth, Matrix3x2 Ctm)>();
        var ctm = Matrix3x2.Identity; // user → page
        int index = 0;

        foreach (var cmd in _list.Commands)
        {
            if ((++index & 0x3FF) == 0)
                _ct.ThrowIfCancellationRequested();

            if (cmd.Bounds is PdfRect b && !b.IsEmpty && !b.IntersectsWith(visiblePage) &&
                cmd is FillPath or StrokePath or DrawGlyphRun or DrawImage or DrawShading)
            {
                continue;
            }

            var full = ctm * pageToDevice;
            switch (cmd)
            {
                case SaveState:
                    frames.Push((pushes.Count, ctm));
                    break;
                case RestoreState:
                    if (frames.Count > 0)
                    {
                        var (depth, saved) = frames.Pop();
                        while (pushes.Count > depth) { pushes.Pop(); ctx.PopLayer(); }
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
                    PushLayer(ctx, Geometry(pc.Path, pc.Rule), full, 1f);
                    pushes.Push(Push.Layer);
                    break;
                case PopClip:
                    if (pushes.Count > 0) { pushes.Pop(); ctx.PopLayer(); }
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
                case BeginTransparencyGroup g:
                    PushLayer(ctx, null, full, (float)Math.Clamp(g.Alpha, 0, 1));
                    pushes.Push(Push.Layer);
                    break;
                case EndTransparencyGroup:
                    if (pushes.Count > 0) { pushes.Pop(); ctx.PopLayer(); }
                    break;
            }
        }
        while (pushes.Count > 0) { pushes.Pop(); ctx.PopLayer(); }
        ctx.Transform = Matrix3x2.Identity;
        ctx1?.Dispose();
    }

    private Color4 Color(PdfColor c, double alpha) => InvertColors
        ? new(1 - c.R, 1 - c.G, 1 - c.B, (float)Math.Clamp(alpha, 0, 1))
        : new(c.R, c.G, c.B, (float)Math.Clamp(alpha, 0, 1));

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
    private (IDWriteFontFace Face, ushort[] Indices, float[] Offsets)? ResolveText(DrawGlyphRun cmd, out bool byGlyphId)
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
        var offsets = new List<float>(run.Glyphs.Count);
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
            offsets.Add((float)(g.OffsetX / th * sign));
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
            Offsets = r.Offsets.Select(o => new GlyphOffset { AdvanceOffset = o, AscenderOffset = 0 }).ToArray(),
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
