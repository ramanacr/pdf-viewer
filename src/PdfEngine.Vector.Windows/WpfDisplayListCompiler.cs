using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// Compiles an immutable display list into a frozen WPF <see cref="DrawingGroup"/> in
/// top-left-origin page points (crop box size, before /Rotate). The drawing is resolution
/// independent: rasterizing it at any zoom replays vectors, it never scales a bitmap.
/// </summary>
/// <remarks>
/// WPF's retained drawing stack (milcore, Direct3D-backed) is the interim Windows backend — see
/// ADR-011 in docs/pdf-viewer-vector-migration/14_ADRS.md. Everything the backend cannot draw
/// faithfully is reported through <see cref="CompiledPage.BackendFallbacks"/> rather than guessed.
/// </remarks>
internal sealed class WpfDisplayListCompiler
{
    private readonly WpfFontCache _fonts;
    private readonly WpfImageCache _images;

    public WpfDisplayListCompiler(WpfFontCache fonts, WpfImageCache images)
    {
        _fonts = fonts;
        _images = images;
    }

    internal sealed record CompiledPage(
        DrawingGroup Drawing,
        double WidthPoints,
        double HeightPoints,
        IReadOnlyList<PdfFallbackToken> Fallbacks,
        IReadOnlyList<PdfFallbackToken> BackendFallbacks);

    private enum PushKind { Transform, Clip, Opacity }

    public CompiledPage Compile(IPdfDisplayList list, PdfRect? visibleRegion, CancellationToken ct)
    {
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        var backendFallbacks = new List<PdfFallbackToken>();
        var fallbacks = new List<PdfFallbackToken>(list.FallbackTokens);

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // PDF default user space (y-up, crop origin) → page points (y-down, top-left origin).
            var root = new PdfMatrix(1, 0, 0, -1, -crop.X, crop.Y + crop.Height);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            dc.PushTransform(new MatrixTransform(ToWpf(root)));

            var pushes = new Stack<PushKind>();
            var frames = new Stack<(int Depth, PdfMatrix Ctm)>();
            // user space → default user space (page); mirrors the ConcatTransform stack.
            var ctm = PdfMatrix.Identity;
            var ctmStack = new Stack<PdfMatrix>();
            int index = 0;

            foreach (var cmd in list.Commands)
            {
                if ((++index & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();

                // Conservative culling: bounds are page space; unknown bounds always draw.
                if (visibleRegion is PdfRect vis && cmd.Bounds is PdfRect b && !b.IsEmpty && !b.IntersectsWith(vis)
                    && cmd is FillPath or StrokePath or DrawGlyphRun or DrawImage or DrawShading)
                {
                    continue;
                }

                switch (cmd)
                {
                    case SaveState:
                        frames.Push((pushes.Count, ctm));
                        break;

                    case RestoreState:
                        if (frames.Count > 0)
                        {
                            var (depth, savedCtm) = frames.Pop();
                            while (pushes.Count > depth)
                            {
                                if (pushes.Pop() == PushKind.Transform && ctmStack.Count > 0)
                                    ctmStack.Pop();
                                dc.Pop();
                            }
                            ctm = savedCtm;
                        }
                        break;

                    case ConcatTransform concat:
                        dc.PushTransform(new MatrixTransform(ToWpf(concat.Matrix)));
                        pushes.Push(PushKind.Transform);
                        ctmStack.Push(ctm);
                        ctm = concat.Matrix * ctm;
                        break;

                    case FillPath fp:
                        if (BuildGeometry(fp.Path, fp.Rule) is Geometry fillGeometry)
                            dc.DrawGeometry(Brush(fp.Paint.Color, fp.Paint.Alpha), null, fillGeometry);
                        break;

                    case StrokePath sp:
                        if (BuildGeometry(sp.Path, PdfFillRule.NonZero) is Geometry strokeGeometry)
                            dc.DrawGeometry(null, CreatePen(sp.Stroke, sp.Paint, ctm), strokeGeometry);
                        break;

                    case PushClip pc:
                        dc.PushClip(BuildGeometry(pc.Path, pc.Rule) ?? Geometry.Empty);
                        pushes.Push(PushKind.Clip);
                        break;

                    case PopClip:
                        if (pushes.Count > 0 && pushes.Peek() == PushKind.Clip)
                        {
                            pushes.Pop();
                            dc.Pop();
                        }
                        break;

                    case DrawGlyphRun dgr:
                        if (!dgr.Run.IsInvisible)
                            DrawText(dc, dgr, ctm, list.PageNumber, backendFallbacks);
                        break;

                    case DrawImage di:
                        DrawImage(dc, di, list.PageNumber, backendFallbacks, ct);
                        break;

                    case DrawShading ds:
                        DrawShading(dc, ds, ctm, crop);
                        break;

                    case BeginTransparencyGroup btg:
                        dc.PushOpacity(Math.Clamp(btg.Alpha, 0.0, 1.0));
                        pushes.Push(PushKind.Opacity);
                        break;

                    case EndTransparencyGroup:
                        if (pushes.Count > 0 && pushes.Peek() == PushKind.Opacity)
                        {
                            pushes.Pop();
                            dc.Pop();
                        }
                        break;

                    case DrawFallbackRegion:
                        break; // composited on top by the rasterizer (placeholder or fallback pixels)
                }
            }

            while (pushes.Count > 0)
            {
                pushes.Pop();
                dc.Pop();
            }

            dc.Pop(); // root transform
            dc.Pop(); // crop clip
        }

        group.Freeze();
        fallbacks.AddRange(backendFallbacks);
        return new CompiledPage(group, crop.Width, crop.Height, fallbacks, backendFallbacks);
    }

    private static Matrix ToWpf(PdfMatrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);

    private static double Scale(PdfMatrix m) => Math.Sqrt(Math.Abs(m.Determinant));

    // ------------------------------------------------------------------ geometry & paint

    internal static StreamGeometry? BuildGeometry(PdfPath path, PdfFillRule rule)
    {
        if (path.Segments.Count == 0)
            return null;

        var geometry = new StreamGeometry
        {
            FillRule = rule == PdfFillRule.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero
        };

        using (var ctx = geometry.Open())
        {
            bool open = false;
            Point start = default, current = default;
            var pending = new List<PdfPathSegment>();

            void Flush(bool closed)
            {
                if (!open) return;
                ctx.BeginFigure(start, isFilled: true, isClosed: closed);
                foreach (var seg in pending)
                {
                    switch (seg)
                    {
                        case PdfLineTo l:
                            ctx.LineTo(new Point(l.Point.X, l.Point.Y), isStroked: true, isSmoothJoin: false);
                            break;
                        case PdfCubicBezierTo c:
                            ctx.BezierTo(new Point(c.Control1.X, c.Control1.Y), new Point(c.Control2.X, c.Control2.Y),
                                new Point(c.EndPoint.X, c.EndPoint.Y), isStroked: true, isSmoothJoin: false);
                            break;
                    }
                }
                pending.Clear();
                open = false;
            }

            foreach (var seg in path.Segments)
            {
                switch (seg)
                {
                    case PdfMoveTo m:
                        Flush(false);
                        start = current = new Point(m.Point.X, m.Point.Y);
                        open = true;
                        break;
                    case PdfLineTo l:
                        if (!open) { start = current; open = true; }
                        pending.Add(l);
                        current = new Point(l.Point.X, l.Point.Y);
                        break;
                    case PdfCubicBezierTo c:
                        if (!open) { start = current; open = true; }
                        pending.Add(c);
                        current = new Point(c.EndPoint.X, c.EndPoint.Y);
                        break;
                    case PdfCloseSubpath:
                        if (open)
                        {
                            Flush(true);
                            // After h the current point is the subpath start (8.5.2.1).
                            current = start;
                        }
                        break;
                }
            }
            Flush(false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Brush(PdfColor color, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            ToByte(alpha), ToByte(color.R), ToByte(color.G), ToByte(color.B)));
        brush.Freeze();
        return brush;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    /// <summary>
    /// PDF line width is in user space; width 0 means the thinnest device line (8.4.3.2).
    /// Hairlines are approximated as 0.5 page points, independent of the CTM scale.
    /// </summary>
    private static Pen CreatePen(PdfStroke stroke, PdfPaint paint, PdfMatrix ctm)
    {
        double scale = Math.Max(Scale(ctm), 1e-9);
        double minWidth = 0.5 / scale;
        double width = Math.Max(stroke.Width, minWidth);

        var cap = stroke.Cap switch
        {
            PdfLineCap.Round => PenLineCap.Round,
            PdfLineCap.Square => PenLineCap.Square,
            _ => PenLineCap.Flat
        };

        var pen = new Pen(Brush(paint.Color, paint.Alpha), width)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            DashCap = cap,
            LineJoin = stroke.Join switch
            {
                PdfLineJoin.Round => PenLineJoin.Round,
                PdfLineJoin.Bevel => PenLineJoin.Bevel,
                _ => PenLineJoin.Miter
            },
            MiterLimit = Math.Max(1.0, stroke.MiterLimit),
        };

        if (stroke.DashArray is { Count: > 0 } dashArray)
        {
            // WPF dashes are multiples of the pen thickness; PDF dashes are user-space lengths.
            var dashes = new DoubleCollection(dashArray.Count % 2 == 1 ? dashArray.Count * 2 : dashArray.Count);
            for (int rep = 0; rep < (dashArray.Count % 2 == 1 ? 2 : 1); rep++)
            {
                foreach (var d in dashArray)
                    dashes.Add(d / width);
            }
            pen.DashStyle = new DashStyle(dashes, stroke.DashPhase / width);
        }

        pen.Freeze();
        return pen;
    }

    // ------------------------------------------------------------------ text

    private void DrawText(DrawingContext dc, DrawGlyphRun cmd, PdfMatrix ctm, int pageNumber, List<PdfFallbackToken> backendFallbacks)
    {
        var run = cmd.Run;
        if (run.Glyphs.Count == 0 || run.FontSize == 0)
            return;

        var face = run.Face;
        GlyphTypeface? typeface = face != null ? _fonts.GetEmbedded(face) : null;
        bool byGlyphId = typeface != null;
        if (face?.Format == PdfFontProgramFormat.Unsupported && typeface == null)
        {
            ReportUnsupported(cmd, pageNumber, backendFallbacks, PdfFallbackReason.UnsupportedFontType,
                "Embedded font program format is not loadable by the Windows backend");
            return;
        }

        if (typeface == null)
        {
            typeface = _fonts.GetSubstitute(face, run.FontFamilyName ?? run.FontResourceName);
            if (typeface == null)
            {
                ReportUnsupported(cmd, pageNumber, backendFallbacks, PdfFallbackReason.UnsupportedFontType,
                    "No system substitute for font");
                return;
            }
        }

        double sign = run.FontSize < 0 ? -1 : 1;
        bool symbolSubstitute = !byGlyphId && typeface.Win32FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var fam) && fam == "Symbol";
        double th = run.HorizontalScaling / 100.0;
        if (Math.Abs(th) < 1e-6) return;

        var indices = new List<ushort>(run.Glyphs.Count);
        var advances = new List<double>(run.Glyphs.Count);
        var offsets = new List<Point>(run.Glyphs.Count);
        var map = typeface.CharacterToGlyphMap;

        foreach (var glyph in run.Glyphs)
        {
            ushort index;
            if (byGlyphId)
            {
                index = glyph.GlyphId;
                if (index >= typeface.GlyphCount) index = 0;
            }
            else
            {
                string? text = glyph.Unicode;
                if (string.IsNullOrEmpty(text) || text == " " || text == " ")
                    continue; // spacing only; position is carried by the next glyph's offset

                int cp = symbolSubstitute && glyph.CharCode >= 0
                    ? 0xF000 + glyph.CharCode
                    : char.ConvertToUtf32(text, 0);
                if (!map.TryGetValue(cp, out index))
                {
                    if (char.IsWhiteSpace(text, 0) || char.IsControl(text, 0))
                        continue;
                    // A substitute lacking the glyph would paint .notdef boxes: classify instead.
                    ReportUnsupported(cmd, pageNumber, backendFallbacks, PdfFallbackReason.UnsupportedFontType,
                        "Substitute font lacks a glyph used by this text");
                    return;
                }
            }

            indices.Add(index);
            advances.Add(0);
            // All positioning is absolute from the run origin: PDF glyph placement is authored (ADR-006).
            offsets.Add(new Point(glyph.OffsetX / th * sign, 0));
        }

        if (indices.Count == 0)
            return;

        // Glyph drawing space (y-down baseline at the origin, em = |Tfs|) → text space → user space:
        // (x, y) ↦ (Th·x, Trise − y), sign-flipped for a negative font size, then Tm.
        var tm = run.TextMatrix;
        var combined = new Matrix(sign * th, 0, 0, -sign, 0, run.TextRise);
        combined = Matrix.Multiply(combined, ToWpf(tm));

        GlyphRun glyphRun;
        try
        {
            glyphRun = new GlyphRun(
                typeface,
                bidiLevel: 0,
                isSideways: false,
                renderingEmSize: Math.Abs(run.FontSize),
                pixelsPerDip: 1.0f,
                glyphIndices: indices,
                baselineOrigin: new Point(0, 0),
                advanceWidths: advances,
                glyphOffsets: offsets,
                characters: null,
                deviceFontName: null,
                clusterMap: null,
                caretStops: null,
                language: null);
        }
        catch (ArgumentException)
        {
            ReportUnsupported(cmd, pageNumber, backendFallbacks, PdfFallbackReason.UnsupportedFontType, "Glyph run rejected by the backend");
            return;
        }

        dc.PushTransform(new MatrixTransform(combined));
        int mode = run.RenderingMode;
        bool fill = mode is 0 or 2 or 4 or 6;
        bool stroke = mode is 1 or 2 or 5 or 6;

        if (fill)
        {
            dc.DrawGlyphRun(Brush(cmd.Paint.Color, cmd.Paint.Alpha), glyphRun);
        }
        if (stroke && run.StrokePaint is PdfPaint strokePaint && run.Stroke is PdfStroke strokeParams)
        {
            // Stroke width is user space; this drawing space is text space scaled by Tm and Th.
            double textScale = Math.Max(Math.Sqrt(Math.Abs(tm.Determinant * th)), 1e-9);
            var geometry = glyphRun.BuildGeometry();
            dc.DrawGeometry(null, CreatePen(strokeParams with { Width = strokeParams.Width / textScale, DashArray = null }, strokePaint, ctm), geometry);
        }
        dc.Pop();
    }

    private static void ReportUnsupported(DrawGlyphRun cmd, int pageNumber, List<PdfFallbackToken> list, PdfFallbackReason reason, string description)
    {
        if (cmd.Bounds is not PdfRect bounds || bounds.IsEmpty)
            return;
        var inflated = new PdfRect(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2);
        list.Add(new PdfFallbackToken(pageNumber, inflated, reason, description,
            new Dictionary<string, string> { ["font"] = cmd.Run.Face?.PostScriptName ?? cmd.Run.FontFamilyName ?? cmd.Run.FontResourceName, ["origin"] = "backend" }));
    }

    // ------------------------------------------------------------------ images

    private void DrawImage(DrawingContext dc, DrawImage di, int pageNumber, List<PdfFallbackToken> backendFallbacks, CancellationToken ct)
    {
        var image = di.Image;
        BitmapSource? bitmap = null;

        if (image.Source != null)
        {
            try
            {
                bitmap = _images.GetOrDecode(image.Source, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is Diagnostics.PdfVectorException or System.IO.InvalidDataException
                                           or System.IO.IOException or NotSupportedException or FileFormatException
                                           or ArgumentException or OverflowException)
            {
                var reason = ex is Diagnostics.PdfUnsupportedFeatureException u ? u.Reason
                           : ex is Diagnostics.PdfResourceLimitException ? PdfFallbackReason.ResourceLimit
                           : PdfFallbackReason.ImageDecode;
                if (di.Bounds is PdfRect b && !b.IsEmpty)
                    backendFallbacks.Add(new PdfFallbackToken(pageNumber, b, reason, "Image could not be decoded by the vector path"));
                return;
            }
        }
        else if (!image.ImageData.IsEmpty && image.Width > 0 && image.Height > 0 &&
                 image.ImageData.Length >= (long)image.Width * image.Height * 3)
        {
            // Legacy builders hand over raw RGB24.
            bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24, null,
                image.ImageData.ToArray(), image.Width * 3);
            bitmap.Freeze();
        }

        if (bitmap == null)
            return;

        // The image occupies the unit square of its space with the first row at the TOP (y = 1)
        // (8.9.4), so flip before drawing a top-down bitmap into y-up user space.
        var t = di.Transform;
        dc.PushTransform(new MatrixTransform(ToWpf(new PdfMatrix(1, 0, 0, -1, 0, 1) * t)));
        bool fade = image.Opacity < 1.0;
        if (fade) dc.PushOpacity(Math.Clamp(image.Opacity, 0, 1));

        var imageGroup = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(imageGroup, image.Interpolate || bitmap.PixelWidth > 64
            ? BitmapScalingMode.HighQuality
            : BitmapScalingMode.NearestNeighbor);
        using (var idc = imageGroup.Open())
        {
            idc.DrawImage(bitmap, new Rect(0, 0, 1, 1));
        }
        imageGroup.Freeze();
        dc.DrawDrawing(imageGroup);

        if (fade) dc.Pop();
        dc.Pop();
    }

    // ------------------------------------------------------------------ shading

    private static void DrawShading(DrawingContext dc, DrawShading ds, PdfMatrix ctm, PdfRect crop)
    {
        // Fill the command's page-space bounds (the clip), expressed in current user space.
        var pageArea = ds.Bounds is PdfRect b && !b.IsEmpty ? b : crop;
        if (!ctm.TryInvert(out var inv))
            return;
        var area = PolygonGeometry(inv, pageArea);

        GradientStopCollection Stops(IReadOnlyList<PdfGradientStop>? stops, PdfColor start, PdfColor end)
        {
            var collection = new GradientStopCollection();
            if (stops is { Count: > 0 })
            {
                foreach (var s in stops)
                    collection.Add(new GradientStop(Color.FromArgb(255, ToByte(s.Color.R), ToByte(s.Color.G), ToByte(s.Color.B)), Math.Clamp(s.Offset, 0, 1)));
            }
            else
            {
                collection.Add(new GradientStop(Color.FromArgb(255, ToByte(start.R), ToByte(start.G), ToByte(start.B)), 0));
                collection.Add(new GradientStop(Color.FromArgb(255, ToByte(end.R), ToByte(end.G), ToByte(end.B)), 1));
            }
            return collection;
        }

        switch (ds.Shading)
        {
            case PdfAxialShading axial:
            {
                var brush = new LinearGradientBrush(Stops(axial.Stops, axial.StartColor, axial.EndColor))
                {
                    StartPoint = new Point(axial.StartPoint.X, axial.StartPoint.Y),
                    EndPoint = new Point(axial.EndPoint.X, axial.EndPoint.Y),
                    MappingMode = BrushMappingMode.Absolute,
                    SpreadMethod = GradientSpreadMethod.Pad,
                };
                brush.Freeze();

                // Pad spreads both ends; a non-extended end must not paint past its perpendicular.
                bool clip = !axial.ExtendStart || !axial.ExtendEnd;
                if (clip)
                    dc.PushClip(AxialBand(axial, inv, pageArea));
                dc.DrawGeometry(brush, null, area);
                if (clip)
                    dc.Pop();
                break;
            }

            case PdfRadialShading radial:
            {
                var brush = new RadialGradientBrush(Stops(radial.Stops, radial.StartColor, radial.EndColor))
                {
                    Center = new Point(radial.EndCenter.X, radial.EndCenter.Y),
                    GradientOrigin = new Point(radial.StartCenter.X, radial.StartCenter.Y),
                    RadiusX = Math.Max(1e-6, radial.EndRadius),
                    RadiusY = Math.Max(1e-6, radial.EndRadius),
                    MappingMode = BrushMappingMode.Absolute,
                    SpreadMethod = GradientSpreadMethod.Pad,
                };
                brush.Freeze();
                if (!radial.ExtendEnd)
                    dc.PushClip(new EllipseGeometry(new Point(radial.EndCenter.X, radial.EndCenter.Y), radial.EndRadius, radial.EndRadius));
                dc.DrawGeometry(brush, null, area);
                if (!radial.ExtendEnd)
                    dc.Pop();
                break;
            }
        }
    }

    private static Geometry PolygonGeometry(PdfMatrix pageToUser, PdfRect pageRect)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var p0 = pageToUser.Transform(pageRect.Left, pageRect.Top);
            ctx.BeginFigure(new Point(p0.X, p0.Y), true, true);
            var p1 = pageToUser.Transform(pageRect.Right, pageRect.Top);
            var p2 = pageToUser.Transform(pageRect.Right, pageRect.Bottom);
            var p3 = pageToUser.Transform(pageRect.Left, pageRect.Bottom);
            ctx.LineTo(new Point(p1.X, p1.Y), false, false);
            ctx.LineTo(new Point(p2.X, p2.Y), false, false);
            ctx.LineTo(new Point(p3.X, p3.Y), false, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Region between the perpendiculars through the axis ends (only on non-extended sides).</summary>
    private static Geometry AxialBand(PdfAxialShading axial, PdfMatrix pageToUser, PdfRect pageArea)
    {
        double dx = axial.EndPoint.X - axial.StartPoint.X, dy = axial.EndPoint.Y - axial.StartPoint.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12)
            return Geometry.Empty;
        double ux = dx / len, uy = dy / len;   // along the axis
        double vx = -uy, vy = ux;              // perpendicular

        // "Far" = beyond anything visible: the diagonal of the area in user space.
        var a = pageToUser.Transform(pageArea.Left, pageArea.Top);
        var c = pageToUser.Transform(pageArea.Right, pageArea.Bottom);
        double far = Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y)) * 4 + len * 4 + 1;

        double u0 = axial.ExtendStart ? -far : 0;
        double u1 = axial.ExtendEnd ? len + far : len;

        Point P(double u, double v) => new(axial.StartPoint.X + ux * u + vx * v, axial.StartPoint.Y + uy * u + vy * v);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(u0, -far), true, true);
            ctx.LineTo(P(u1, -far), false, false);
            ctx.LineTo(P(u1, far), false, false);
            ctx.LineTo(P(u0, far), false, false);
        }
        g.Freeze();
        return g;
    }
}
