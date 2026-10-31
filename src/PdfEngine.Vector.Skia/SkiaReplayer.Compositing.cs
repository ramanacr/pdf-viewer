using System.Numerics;
using PdfEngine.Geometry;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Transparency (ISO 32000-2 11.3–11.6): compositing groups with blend mode, group alpha and soft
/// mask drawn into offscreen bitmaps and composited with Skia's blend modes (the separable and
/// non-separable modes of the specification); non-isolated and knockout groups composited per
/// pixel, as the Direct2D backend composites them with effects; tiling patterns as a repeating
/// image of one period.
/// </summary>
internal sealed partial class SkiaReplayer
{
    /// <summary>Largest offscreen group side; larger groups are clamped to the visible target anyway.</summary>
    private const int MaxGroupPixels = 8192;

    /// <summary>A transparent offscreen surface (optionally starting as a copy of <paramref name="initial"/>, same size).</summary>
    private static Surface NewSurface(int w, int h, SKBitmap? initial = null)
    {
        var bitmap = SkiaVectorRenderer.NewBitmap(w, h);
        if (initial != null)
            initial.GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
        else
            bitmap.Erase(SKColors.Transparent);
        return new Surface { Canvas = new SKCanvas(bitmap), Bitmap = bitmap, Width = w, Height = h };
    }

    private static void Release(Surface surface)
    {
        surface.Canvas.Dispose();
        surface.Bitmap.Dispose();
    }

    /// <summary>Copies the pixels under a device rectangle of a surface (no Skia layer may be pending on it).</summary>
    private static SKBitmap CopyRegion(SKBitmap source, int x0, int y0, int w, int h)
    {
        var copy = SkiaVectorRenderer.NewBitmap(w, h);
        var src = source.GetPixelSpan();
        var dst = copy.GetPixelSpan();
        int srcStride = source.RowBytes, dstStride = copy.RowBytes;
        for (int y = 0; y < h; y++)
            src.Slice((y0 + y) * srcStride + x0 * 4, w * 4).CopyTo(dst.Slice(y * dstStride, w * 4));
        return copy;
    }

    private static SKBlendMode ToSk(PdfBlendMode mode) => mode switch
    {
        PdfBlendMode.Multiply => SKBlendMode.Multiply,
        PdfBlendMode.Screen => SKBlendMode.Screen,
        PdfBlendMode.Overlay => SKBlendMode.Overlay,
        PdfBlendMode.Darken => SKBlendMode.Darken,
        PdfBlendMode.Lighten => SKBlendMode.Lighten,
        PdfBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        PdfBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        PdfBlendMode.HardLight => SKBlendMode.HardLight,
        PdfBlendMode.SoftLight => SKBlendMode.SoftLight,
        PdfBlendMode.Difference => SKBlendMode.Difference,
        PdfBlendMode.Exclusion => SKBlendMode.Exclusion,
        PdfBlendMode.Hue => SKBlendMode.Hue,
        PdfBlendMode.Saturation => SKBlendMode.Saturation,
        PdfBlendMode.Color => SKBlendMode.Color,
        PdfBlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>
    /// Renders a group's content into an offscreen bitmap, applies the soft mask and group alpha,
    /// and composites it onto <paramref name="surface"/> with the blend mode (ISO 32000-2 11.3–11.6),
    /// under the surface's clips. A non-isolated group whose content reads the backdrop starts
    /// from a copy of it, which is removed again afterwards; other groups are composited as
    /// isolated, which is then the same.
    /// </summary>
    private void DrawGroup(Surface surface, PdfRect? bounds, double alpha, PdfBlendMode blend, PdfSoftMask? mask,
        bool isolated, bool knockout, bool containsBlend,
        int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage)
    {
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

        // A non-isolated group sees the backdrop: inner blend modes and knockout elements composite
        // against it (11.4.7). The backdrop is whatever the surface holds under the group, opaque
        // (the page) or not (inside an isolated group, a soft mask or a knockout element). Pixels
        // under a pending Skia layer cannot be read; the group is then composited as isolated.
        bool nonIsolated = !isolated && (containsBlend || knockout) && !_shapeMode && surface.OpacityLayers == 0;
        using var initialBackdrop = nonIsolated ? CopyRegion(surface.Bitmap, x0, y0, w, h) : null;

        Surface RenderContent(bool fromBackdrop)
        {
            var sub = NewSurface(w, h, fromBackdrop ? initialBackdrop : null);
            try
            {
                DrawRange(sub, start, end, groupPageToDevice, ctm, groupVisible,
                    knockout ? new Knockout(fromBackdrop ? initialBackdrop : null) : null);
            }
            catch
            {
                Release(sub);
                throw;
            }
            return sub;
        }

        var content = RenderContent(fromBackdrop: nonIsolated);
        try
        {
            var pixels = content.Bitmap.GetPixelSpan();
            if (nonIsolated)
            {
                // Group result with the backdrop removed (11.4.8): C = Cn + (Cn − C0)·(α0/αg − α0).
                // Premultiplied by αg that is R = Cn − C0·(1 − αg) for any backdrop alpha α0, where
                // Cn (premultiplied, alpha α0 ∪ αg) was rendered over the backdrop C0 (premultiplied)
                // and αg is the group's own alpha (an isolated render of the same content).
                var isolatedRender = RenderContent(fromBackdrop: false);
                try
                {
                    var own = isolatedRender.Bitmap.GetPixelSpan();
                    var c0 = initialBackdrop!.GetPixelSpan();
                    for (int p = 0; p + 3 < pixels.Length; p += 4)
                    {
                        int ag = own[p + 3];
                        for (int k = 0; k < 4; k++)
                            pixels[p + k] = (byte)Math.Clamp(pixels[p + k] - (c0[p + k] * (255 - ag) + 127) / 255, 0, 255);
                    }
                }
                finally
                {
                    Release(isolatedRender);
                }
            }

            if (mask != null && RenderSoftMask(mask, w, h, groupPageToDevice) is { } maskBitmap)
            {
                using (maskBitmap)
                    ApplySoftMask(pixels, maskBitmap.GetPixelSpan(), mask);
            }

            using var paint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha(ToByte(alpha)),
                BlendMode = ToSk(blend),
            };
            var canvas = surface.Canvas;
            canvas.ResetMatrix();
            canvas.DrawBitmap(content.Bitmap, x0, y0, paint);
        }
        finally
        {
            Release(content);
        }
    }

    /// <summary>
    /// Multiplies premultiplied group pixels by the soft mask: the luminosity of the mask group over
    /// its backdrop (0.30 R + 0.59 G + 0.11 B, as the Direct2D backend's colour matrix computes it) or
    /// its alpha, through the transfer function when there is one (11.6.5.2).
    /// </summary>
    private static void ApplySoftMask(Span<byte> pixels, ReadOnlySpan<byte> mask, PdfSoftMask softMask)
    {
        var table = new byte[256];
        var tr = softMask.Transfer is { Count: >= 2 } t ? t : null;
        for (int v = 0; v < 256; v++)
        {
            double value = v / 255.0;
            if (tr != null)
            {
                // Table transfer: linear between the samples, as Direct2D's TableTransfer reads them.
                double pos = value * (tr.Count - 1);
                int i0 = Math.Min((int)pos, tr.Count - 2);
                value = tr[i0] + (tr[i0 + 1] - tr[i0]) * (pos - i0);
            }
            table[v] = ToByte(value);
        }
        for (int p = 0; p + 3 < pixels.Length && p + 3 < mask.Length; p += 4)
        {
            int m = softMask.Luminosity
                ? (int)Math.Round(0.30 * mask[p + 2] + 0.59 * mask[p + 1] + 0.11 * mask[p])
                : mask[p + 3];
            int a = table[Math.Clamp(m, 0, 255)];
            if (a == 255) continue;
            for (int k = 0; k < 4; k++)
                pixels[p + k] = (byte)((pixels[p + k] * a + 127) / 255);
        }
    }

    /// <summary>
    /// One element of a knockout group (11.4.6.2, 11.4.8): it composites with the group's initial
    /// backdrop C0 instead of what earlier elements left, and replaces them by its shape f. With E
    /// the element rendered over C0 with its own blend mode and opacity (over nothing in an
    /// isolated group), the group becomes G·(1 − f) + E − C0·(1 − f).
    /// </summary>
    private void DrawKnockoutElement(Surface surface, Knockout knockout, int start, int end, Matrix3x2 pageToDevice, Matrix3x2 ctm, PdfRect visiblePage)
    {
        if (surface.OpacityLayers > 0)
        {
            // The surface's pixels are behind a Skia layer: draw the element as an ordinary one.
            DrawRange(surface, start, end, pageToDevice, ctm, visiblePage);
            return;
        }
        Surface Render(bool shape)
        {
            var sub = NewSurface(surface.Width, surface.Height, !shape ? knockout.Backdrop : null);
            bool savedShape = _shapeMode;
            _shapeMode = shape;
            try
            {
                // The element replaces pixels without the surface's clips, so it carries them itself.
                PushClipsOf(surface, sub, Matrix3x2.Identity);
                DrawRange(sub, start, end, pageToDevice, ctm, visiblePage);
                while (sub.Layers.Count > 0) PopLayer(sub);
            }
            catch
            {
                Release(sub);
                throw;
            }
            finally
            {
                _shapeMode = savedShape;
            }
            return sub;
        }

        var element = Render(shape: false);
        var shapeSurface = Render(shape: true);
        try
        {
            var g = surface.Bitmap.GetPixelSpan();
            var e = element.Bitmap.GetPixelSpan();
            var f = shapeSurface.Bitmap.GetPixelSpan();
            var c0 = knockout.Backdrop != null ? knockout.Backdrop.GetPixelSpan() : default;
            for (int p = 0; p + 3 < g.Length; p += 4)
            {
                int fa = f[p + 3];
                for (int k = 0; k < 4; k++)
                {
                    // E − C0·(1 − f): outside the shape E is C0, so this is 0 there.
                    int add = e[p + k];
                    if (!c0.IsEmpty) add -= c0[p + k] - (c0[p + k] * fa + 127) / 255;
                    int kept = (g[p + k] * (255 - fa) + 127) / 255; // destination-out by the shape
                    g[p + k] = (byte)Math.Clamp(kept + Math.Max(0, add), 0, 255);
                }
            }
        }
        finally
        {
            Release(element);
            Release(shapeSurface);
        }
    }

    private readonly Dictionary<PdfSoftMask, SkiaReplayer> _maskReplayers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The soft mask's group rendered over its backdrop colour (luminosity) or transparent (alpha)
    /// for the w × h device rectangle.
    /// </summary>
    private SKBitmap? RenderSoftMask(PdfSoftMask mask, int w, int h, Matrix3x2 pageToDevice)
    {
        if (!_maskReplayers.TryGetValue(mask, out var replayer))
        {
            replayer = new SkiaReplayer(_res, mask.Content, null, _ct);
            _maskReplayers[mask] = replayer;
        }
        var bitmap = SkiaVectorRenderer.NewBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        var bc = mask.Backdrop;
        canvas.Clear(mask.Luminosity ? new SKColor(ToByte(bc.R), ToByte(bc.G), ToByte(bc.B), 255) : SKColors.Transparent);
        var maskToDevice = M(mask.MaskToPage) * pageToDevice;
        replayer.Draw(bitmap, canvas, maskToDevice, new PdfRect(-1e6, -1e6, 2e6, 2e6));
        canvas.Flush();
        return bitmap;
    }

    // ------------------------------------------------------------------ tiling patterns

    /// <summary>Largest cell bitmap side; finer cells are resampled (the pattern stays seamless).</summary>
    private const int MaxCellPixels = 2048;

    private readonly Dictionary<PdfTilingPattern, (SkiaReplayer Replayer, Dictionary<(int W, int H), SKImage> Cells)> _tilingCells =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// One period of the pattern rasterized at device resolution (every tile whose BBox reaches into
    /// the period contributes), then used as a repeating image over the painted area.
    /// </summary>
    private void PaintTiling(SKCanvas canvas, DrawTilingPattern cmd, Matrix3x2 ctm, Matrix3x2 pageToDevice, PdfRect visiblePage)
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
            entry = (new SkiaReplayer(_res, pattern.Cell, null, _ct), new());
            _tilingCells[pattern] = entry;
        }
        if (!entry.Cells.TryGetValue((bw, bh), out var cell))
        {
            cell = RenderCell(entry.Replayer, pattern, bw, bh);
            entry.Cells[(bw, bh)] = cell;
        }

        var area = cmd.Bounds is PdfRect b ? Intersect(b, visiblePage) : visiblePage;
        if (area.IsEmpty || !Matrix3x2.Invert(ctm, out var pageToPattern))
            return;

        using var geom = Polygon(pageToPattern, area);
        canvas.SetMatrix(ToSk(full));
        if (_shapeMode)
        {
            // The shape of a pattern fill is the painted area, not the cell's coverage.
            using var shapePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawPath(geom, shapePaint);
            return;
        }
        var bitmapToPattern = Matrix3x2.CreateScale((float)(xs / bw), (float)(ys / bh)) *
                              Matrix3x2.CreateTranslation((float)pattern.BBox.X, (float)pattern.BBox.Y);
        using var shader = cell.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), ToSk(bitmapToPattern));
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawPath(geom, paint);
    }

    private static SKImage RenderCell(SkiaReplayer replayer, PdfTilingPattern pattern, int bw, int bh)
    {
        double xs = pattern.XStep, ys = pattern.YStep;
        var box = pattern.BBox;
        using var bitmap = SkiaVectorRenderer.NewBitmap(bw, bh);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
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
                replayer.Draw(bitmap, canvas, cellToBitmap, new PdfRect(-1e7, -1e7, 2e7, 2e7));
            }
            canvas.Flush();
        }
        return SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
    }
}
