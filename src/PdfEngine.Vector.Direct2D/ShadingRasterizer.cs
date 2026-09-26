using System.Numerics;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// CPU rasterization of smooth shadings Direct2D has no primitive for: Gouraud triangle meshes,
/// tensor-product patches subdivided at device resolution, and function-based shadings. Output
/// is a premultiplied BGRA buffer for one device rectangle; pixels are sampled at their centres.
/// </summary>
internal static class ShadingRasterizer
{
    /// <summary>A colour source: RGB per vertex, or a parametric value mapped through a table.</summary>
    internal readonly record struct Shade(float R, float G, float B, float T);

    internal sealed class Target
    {
        public readonly int X0, Y0, Width, Height;
        public readonly uint[] Pixels;
        public Target(int x0, int y0, int width, int height)
        {
            X0 = x0; Y0 = y0; Width = width; Height = height;
            Pixels = new uint[width * height];
        }
    }

    /// <summary>Fills one triangle, interpolating the shade barycentrically (later triangles paint over earlier ones).</summary>
    public static void FillTriangle(Target t, Vector2 a, Vector2 b, Vector2 c, Shade sa, Shade sb, Shade sc, Func<Shade, uint> toPixel)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-12f)
            return;
        int minX = Math.Max(t.X0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(t.X0 + t.Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(t.Y0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(t.Y0 + t.Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
            return;
        float inv = 1f / area;
        // Tiny tolerance: shared edges of adjacent triangles are covered by both (no seams).
        const float Eps = -1e-4f;
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            int row = (y - t.Y0) * t.Width - t.X0;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inv;
                float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inv;
                float w2 = 1f - w0 - w1;
                if (w0 < Eps || w1 < Eps || w2 < Eps)
                    continue;
                var s = new Shade(
                    w0 * sa.R + w1 * sb.R + w2 * sc.R,
                    w0 * sa.G + w1 * sb.G + w2 * sc.G,
                    w0 * sa.B + w1 * sb.B + w2 * sc.B,
                    w0 * sa.T + w1 * sb.T + w2 * sc.T);
                t.Pixels[row + x] = toPixel(s);
            }
        }
    }

    private static double B(int i, double t) => i switch
    {
        0 => (1 - t) * (1 - t) * (1 - t),
        1 => 3 * t * (1 - t) * (1 - t),
        2 => 3 * t * t * (1 - t),
        _ => t * t * t,
    };

    /// <summary>Point of the tensor surface S(u, v) = Σ p[i,j] B_i(u) B_j(v).</summary>
    public static PdfPoint Surface(PdfPoint[] p, double u, double v)
    {
        double x = 0, y = 0;
        for (int i = 0; i < 4; i++)
        {
            double bu = B(i, u);
            for (int j = 0; j < 4; j++)
            {
                double w = bu * B(j, v);
                x += w * p[i * 4 + j].X;
                y += w * p[i * 4 + j].Y;
            }
        }
        return new PdfPoint(x, y);
    }

    /// <summary>
    /// Subdivides a patch into an n × n grid (n from its device size, so curved edges stay smooth at
    /// any zoom) and fills it in the order the specification makes visible where it folds: larger
    /// v over smaller, then larger u over smaller (8.7.4.5.7).
    /// </summary>
    public static void FillPatch(Target t, PdfMeshPatch patch, Matrix3x2 toDevice, Shade[] corners, Func<Shade, uint> toPixel)
    {
        var dev = new Vector2[16];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int k = 0; k < 16; k++)
        {
            dev[k] = Vector2.Transform(new Vector2((float)patch.Points[k].X, (float)patch.Points[k].Y), toDevice);
            minX = MathF.Min(minX, dev[k].X); maxX = MathF.Max(maxX, dev[k].X);
            minY = MathF.Min(minY, dev[k].Y); maxY = MathF.Max(maxY, dev[k].Y);
        }
        if (maxX < t.X0 || maxY < t.Y0 || minX > t.X0 + t.Width || minY > t.Y0 + t.Height)
            return;
        float size = MathF.Max(maxX - minX, maxY - minY);
        int n = Math.Clamp((int)MathF.Ceiling(size / 3f), 2, 192);

        var grid = new Vector2[(n + 1) * (n + 1)];
        var shades = new Shade[(n + 1) * (n + 1)];
        var devPts = new PdfPoint[16];
        for (int k = 0; k < 16; k++) devPts[k] = new PdfPoint(dev[k].X, dev[k].Y);
        for (int iu = 0; iu <= n; iu++)
        {
            double u = iu / (double)n;
            for (int iv = 0; iv <= n; iv++)
            {
                double v = iv / (double)n;
                var s = Surface(devPts, u, v); // affine maps commute with Bézier evaluation
                grid[iu * (n + 1) + iv] = new Vector2((float)s.X, (float)s.Y);
                // Bilinear corner values: c00 (0,0), c03 (0,1), c33 (1,1), c30 (1,0).
                float w00 = (float)((1 - u) * (1 - v)), w03 = (float)((1 - u) * v), w33 = (float)(u * v), w30 = (float)(u * (1 - v));
                shades[iu * (n + 1) + iv] = new Shade(
                    w00 * corners[0].R + w03 * corners[1].R + w33 * corners[2].R + w30 * corners[3].R,
                    w00 * corners[0].G + w03 * corners[1].G + w33 * corners[2].G + w30 * corners[3].G,
                    w00 * corners[0].B + w03 * corners[1].B + w33 * corners[2].B + w30 * corners[3].B,
                    w00 * corners[0].T + w03 * corners[1].T + w33 * corners[2].T + w30 * corners[3].T);
            }
        }
        for (int iv = 0; iv < n; iv++)
        for (int iu = 0; iu < n; iu++)
        {
            int a = iu * (n + 1) + iv, b = (iu + 1) * (n + 1) + iv, c = (iu + 1) * (n + 1) + iv + 1, d = iu * (n + 1) + iv + 1;
            FillTriangle(t, grid[a], grid[b], grid[c], shades[a], shades[b], shades[c], toPixel);
            FillTriangle(t, grid[a], grid[c], grid[d], shades[a], shades[c], shades[d], toPixel);
        }
    }
}
