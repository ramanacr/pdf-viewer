using System.Numerics;
using System.Runtime.InteropServices;
using PdfEngine.Geometry;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Smooth shadings: axial and focal radial ones as Skia gradients, general two-circle radials,
/// function-based and mesh shadings evaluated per device pixel on the CPU and drawn under the
/// current clip, as the Direct2D backend draws them.
/// </summary>
internal sealed partial class SkiaReplayer
{
    private void PaintShading(Surface surface, DrawShading ds, Matrix3x2 ctm, Matrix3x2 pageToDevice)
    {
        var canvas = surface.Canvas;
        var full = ctm * pageToDevice;
        var area = ds.Bounds is PdfRect b && !b.IsEmpty ? b : _list.CropBox;
        if (!Matrix3x2.Invert(ctm, out var pageToUser))
            return;

        (SKColor[] Colors, float[] Positions) Stops(IReadOnlyList<PdfGradientStop>? stops, PdfColor start, PdfColor end)
        {
            if (stops is not { Count: > 0 })
                return (new[] { Color(start, 1), Color(end, 1) }, new[] { 0f, 1f });
            var colors = new SKColor[stops.Count];
            var positions = new float[stops.Count];
            float last = 0;
            for (int i = 0; i < stops.Count; i++)
            {
                colors[i] = Color(stops[i].Color, 1);
                // Skia wants non-decreasing positions.
                last = positions[i] = Math.Max(last, (float)Math.Clamp(stops[i].Offset, 0, 1));
            }
            return (colors, positions);
        }

        switch (ds.Shading)
        {
            case PdfAxialShading axial:
            {
                using var areaPath = Polygon(pageToUser, area);
                var (colors, positions) = Stops(axial.Stops, axial.StartColor, axial.EndColor);
                using var shader = SKShader.CreateLinearGradient(P(axial.StartPoint), P(axial.EndPoint), colors, positions, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                bool band = !axial.ExtendStart || !axial.ExtendEnd;
                canvas.Save();
                canvas.SetMatrix(ToSk(full));
                if (band)
                {
                    using var bandPath = AxialBand(axial, pageToUser, area);
                    canvas.ClipPath(bandPath, SKClipOperation.Intersect, antialias: true);
                }
                canvas.DrawPath(areaPath, paint);
                canvas.Restore();
                break;
            }
            case PdfRadialShading { IsGeneral: true } general:
                PaintGeneralRadial(surface, general, area, full, pageToDevice);
                break;
            case PdfMeshShading mesh:
                PaintMesh(surface, mesh, area, full, pageToDevice);
                break;
            case PdfFunctionShading function:
                PaintFunctionShading(surface, function, area, full, pageToDevice);
                break;
            case PdfRadialShading radial:
            {
                // A focal radial: the Direct2D brush's gradient origin at the start centre, the
                // circle of the end centre and radius (the stops are already over that span).
                using var areaPath = Polygon(pageToUser, area);
                var (colors, positions) = Stops(radial.Stops, radial.StartColor, radial.EndColor);
                var center = P(radial.EndCenter);
                float r1 = (float)Math.Max(1e-6, radial.EndRadius);
                using var shader = SKShader.CreateTwoPointConicalGradient(P(radial.StartCenter), 0f, center, r1, colors, positions, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                canvas.Save();
                canvas.SetMatrix(ToSk(full));
                if (!radial.ExtendEnd)
                {
                    using var circle = new SKPath();
                    circle.AddCircle(center.X, center.Y, r1);
                    canvas.ClipPath(circle, SKClipOperation.Intersect, antialias: true);
                }
                canvas.DrawPath(areaPath, paint);
                canvas.Restore();
                break;
            }
        }
    }

    /// <summary>The axis band of an axial shading that does not extend at one or both ends, in user space.</summary>
    private static SKPath AxialBand(PdfAxialShading axial, Matrix3x2 pageToUser, PdfRect area)
    {
        var s = new Vector2((float)axial.StartPoint.X, (float)axial.StartPoint.Y);
        var d = new Vector2((float)axial.EndPoint.X, (float)axial.EndPoint.Y) - s;
        float len = d.Length();
        var path = new SKPath();
        if (len > 1e-6f)
        {
            var u = d / len;
            var v = new Vector2(-u.Y, u.X);
            var a = Vector2.Transform(new Vector2((float)area.Left, (float)area.Top), pageToUser);
            var c = Vector2.Transform(new Vector2((float)area.Right, (float)area.Bottom), pageToUser);
            float far = (a - c).Length() * 4 + len * 4 + 1;
            float u0 = axial.ExtendStart ? -far : 0, u1 = axial.ExtendEnd ? len + far : len;
            SKPoint Q(float uu, float vv) => Pt(s + u * uu + v * vv);
            path.MoveTo(Q(u0, -far));
            path.LineTo(Q(u1, -far));
            path.LineTo(Q(u1, far));
            path.LineTo(Q(u0, far));
            path.Close();
        }
        return path;
    }

    /// <summary>Opaque premultiplied BGRA (the uint layout of <see cref="SKColorType.Bgra8888"/>) of a colour.</summary>
    private uint Opaque(PdfColor c)
    {
        var col = Color(c, 1);
        return 0xFF000000u | ((uint)col.Red << 16) | ((uint)col.Green << 8) | col.Blue;
    }

    /// <summary>Draws premultiplied BGRA pixels at a device rectangle, under the current clip.</summary>
    private static void DrawPixels(SKCanvas canvas, uint[] pixels, int w, int h, SKRect destination, SKSamplingOptions sampling)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromPixels(new SKPixmap(info, handle.AddrOfPinnedObject(), w * 4));
            if (image == null)
                return;
            canvas.ResetMatrix();
            using var paint = new SKPaint();
            canvas.DrawImage(image, new SKRect(0, 0, w, h), destination, sampling, paint);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Radial shading between two arbitrary circles, evaluated per device pixel: the colour is that
    /// of the largest t with r(t) ≥ 0 whose circle passes through the pixel (8.7.4.5.4).
    /// </summary>
    private void PaintGeneralRadial(Surface surface, PdfRadialShading sh, PdfRect area, Matrix3x2 userToDevice, Matrix3x2 pageToDevice)
    {
        if (!DeviceRect(surface, area, pageToDevice, out int x0, out int y0, out int w, out int h) || !Matrix3x2.Invert(userToDevice, out var deviceToUser))
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
            table[i] = Opaque(c);
        }

        double cx = sh.StartCenter.X, cy = sh.StartCenter.Y, r0 = sh.StartRadius;
        double dx = sh.EndCenter.X - cx, dy = sh.EndCenter.Y - cy, dr = sh.EndRadius - r0;
        double qa = dx * dx + dy * dy - dr * dr;
        bool e0 = sh.ExtendStart, e1 = sh.ExtendEnd;
        var pixels = new uint[w * h];
        var ct = _ct;
        Parallel.For(0, h, row =>
        {
            ct.ThrowIfCancellationRequested();
            for (int col = 0; col < w; col++)
            {
                // One sample at the pixel centre (PDFium does not antialias shading edges either).
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
                if (!double.IsNaN(best))
                    pixels[row * w + col] = table[(int)Math.Round(Math.Clamp(best, 0, 1) * (table.Length - 1))];
            }
        });
        DrawPixels(surface.Canvas, pixels, w, h, new SKRect(x0, y0, x0 + w, y0 + h), new SKSamplingOptions(SKFilterMode.Nearest));
    }

    /// <summary>
    /// Mesh shadings (types 4–7): Gouraud triangles and device-resolution patch subdivision on the
    /// CPU, drawn under the current clip. With a function, t is interpolated and then mapped.
    /// </summary>
    private void PaintMesh(Surface surface, PdfMeshShading mesh, PdfRect area, Matrix3x2 userToDevice, Matrix3x2 pageToDevice)
    {
        if (!DeviceRect(surface, area, pageToDevice, out int x0, out int y0, out int w, out int h))
            return;
        var target = new SkiaShadingRasterizer.Target(x0, y0, w, h);
        uint[]? lut = null;
        if (mesh.FunctionLut is { Count: > 0 } fl)
        {
            lut = new uint[fl.Count];
            for (int i = 0; i < lut.Length; i++) lut[i] = Opaque(fl[i]);
        }
        uint ToPixel(SkiaShadingRasterizer.Shade s) => lut != null
            ? lut[(int)Math.Round(Math.Clamp(s.T, 0, 1) * (lut.Length - 1))]
            : Opaque(new PdfColor(s.R, s.G, s.B));
        static SkiaShadingRasterizer.Shade ShadeOf(PdfColor c, double t) => new(c.R, c.G, c.B, (float)t);

        var tris = mesh.Triangles;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            if ((i & 0xFFF) == 0) _ct.ThrowIfCancellationRequested();
            var a = tris[i]; var b = tris[i + 1]; var c = tris[i + 2];
            SkiaShadingRasterizer.FillTriangle(target,
                Vector2.Transform(new Vector2((float)a.X, (float)a.Y), userToDevice),
                Vector2.Transform(new Vector2((float)b.X, (float)b.Y), userToDevice),
                Vector2.Transform(new Vector2((float)c.X, (float)c.Y), userToDevice),
                ShadeOf(a.Color, a.T), ShadeOf(b.Color, b.T), ShadeOf(c.Color, c.T), ToPixel);
        }
        var corners = new SkiaShadingRasterizer.Shade[4];
        for (int p = 0; p < mesh.Patches.Count; p++)
        {
            if ((p & 0x3F) == 0) _ct.ThrowIfCancellationRequested();
            var patch = mesh.Patches[p];
            for (int k = 0; k < 4; k++) corners[k] = ShadeOf(patch.Colors[k], patch.T[k]);
            SkiaShadingRasterizer.FillPatch(target, patch, userToDevice, corners, ToPixel);
        }
        DrawPixels(surface.Canvas, target.Pixels, w, h, new SKRect(x0, y0, x0 + w, y0 + h), new SKSamplingOptions(SKFilterMode.Nearest));
    }

    /// <summary>Largest sample grid side for a function shading (bilinear between samples beyond it).</summary>
    private const int MaxFunctionSamples = 768;

    /// <summary>
    /// Type 1 shading: f(x, y) sampled at device pixel centres (up to 768 per side, then bilinear),
    /// transparent outside /Domain.
    /// </summary>
    private void PaintFunctionShading(Surface surface, PdfFunctionShading sh, PdfRect area, Matrix3x2 userToDevice, Matrix3x2 pageToDevice)
    {
        if (!DeviceRect(surface, area, pageToDevice, out int x0, out int y0, out int w, out int h))
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
        DrawPixels(surface.Canvas, pixels, sw, sh2, new SKRect(x0, y0, x0 + w, y0 + h),
            new SKSamplingOptions(sw == w && sh2 == h ? SKFilterMode.Nearest : SKFilterMode.Linear));
    }
}
