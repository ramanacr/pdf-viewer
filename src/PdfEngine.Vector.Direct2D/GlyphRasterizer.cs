using System.Numerics;
using SharpGen.Runtime;
using Vortice.Direct2D1;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Coverage of a glyph outline on a small pixel grid, by exact area (the signed-area accumulation
/// of font-rs; FreeType's smooth rasterizer, which PDFium's glyphs come from, also computes exact
/// area). Nonzero winding with the coverage clamped to one, curves flattened to a twentieth of a
/// pixel. DirectWrite writes the outline straight into it as a geometry sink, so a glyph costs a few
/// microseconds instead of a Direct2D tessellation.
/// </summary>
internal sealed class GlyphRasterizer : CallbackBase, ID2D1SimplifiedGeometrySink
{
    private const float Tolerance = 0.05f;
    private float[] _area = new float[64 * 64];
    private int _width, _height, _stride;
    private Matrix3x2 _toGrid;
    private Vector2 _start, _current;
    // Measuring: only the bounds of the points (control points included) are collected.
    private bool _measuring;
    private Vector2 _min, _max;

    /// <summary>Starts measuring an outline's bounds through <paramref name="toDevice"/> (<see cref="MeasuredBounds"/>).</summary>
    public void Measure(Matrix3x2 toDevice)
    {
        _measuring = true;
        _toGrid = toDevice;
        _min = new Vector2(float.MaxValue);
        _max = new Vector2(float.MinValue);
    }

    /// <summary>The bounds of the outline measured, or null when it has no points (a space).</summary>
    public Vortice.RawRectF? MeasuredBounds =>
        _max.X >= _min.X && _max.Y >= _min.Y ? new Vortice.RawRectF(_min.X, _min.Y, _max.X, _max.Y) : null;

    private void Include(Vector2 p)
    {
        _min = Vector2.Min(_min, p);
        _max = Vector2.Max(_max, p);
    }

    /// <summary>Starts a grid of <paramref name="width"/> × <paramref name="height"/>; outline points map onto it through <paramref name="toGrid"/>.</summary>
    public void Reset(int width, int height, Matrix3x2 toGrid)
    {
        _measuring = false;
        _width = width;
        _height = height;
        _stride = width + 2; // a line's area can spill one cell past the right edge
        int needed = _stride * height;
        if (_area.Length < needed) _area = new float[Math.Max(needed, _area.Length * 2)];
        else Array.Clear(_area, 0, needed);
        _toGrid = toGrid;
    }

    /// <summary>The coverage as premultiplied white BGRA (each channel = coverage × 255), row by row.</summary>
    public void WriteBgra(byte[] destination, int pitch)
    {
        for (int y = 0; y < _height; y++)
        {
            float acc = 0;
            int row = y * _stride, o = y * pitch;
            for (int x = 0; x < _width; x++)
            {
                acc += _area[row + x];
                float c = MathF.Min(1f, MathF.Abs(acc));
                byte v = (byte)(c * 255f + 0.5f);
                destination[o++] = v;
                destination[o++] = v;
                destination[o++] = v;
                destination[o++] = v;
            }
        }
    }

    // ------------------------------------------------------------------ geometry sink

    public void SetFillMode(FillMode fillMode) { }

    public void SetSegmentFlags(PathSegment vertexFlags) { }

    public void BeginFigure(Vector2 startPoint, FigureBegin figureBegin)
    {
        _start = _current = Vector2.Transform(startPoint, _toGrid);
        if (_measuring) Include(_start);
    }

    public void AddLines(Vector2[] points) => AddLines(points.AsSpan());

    public void AddLines(ReadOnlySpan<Vector2> points)
    {
        foreach (var p in points)
        {
            var q = Vector2.Transform(p, _toGrid);
            if (_measuring) Include(q);
            else Line(_current, q);
            _current = q;
        }
    }

    public void AddBeziers(BezierSegment[] beziers) => AddBeziers(beziers.AsSpan());

    public void AddBeziers(ReadOnlySpan<BezierSegment> beziers)
    {
        foreach (var b in beziers)
        {
            var p0 = _current;
            var p1 = Vector2.Transform(b.Point1, _toGrid);
            var p2 = Vector2.Transform(b.Point2, _toGrid);
            var p3 = Vector2.Transform(b.Point3, _toGrid);
            if (_measuring)
            {
                Include(p1);
                Include(p2);
                Include(p3);
                _current = p3;
                continue;
            }
            // Segments from the curve's second differences (the flattening error bound of a cubic).
            float dd = MathF.Max((p0 - 2 * p1 + p2).Length(), (p1 - 2 * p2 + p3).Length());
            int n = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(0.75f * dd / Tolerance)), 1, 100);
            var previous = p0;
            for (int i = 1; i <= n; i++)
            {
                float t = i / (float)n, u = 1 - t;
                var q = i == n ? p3 : u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
                Line(previous, q);
                previous = q;
            }
            _current = p3;
        }
    }

    public void EndFigure(FigureEnd figureEnd)
    {
        if (!_measuring) Line(_current, _start); // every figure is filled closed
        _current = _start;
    }

    public void Close() { }

    /// <summary>Adds a line's signed area to the cells it crosses (font-rs's draw_line).</summary>
    private void Line(Vector2 a, Vector2 b)
    {
        if (MathF.Abs(a.Y - b.Y) <= float.Epsilon)
            return;
        float dir = 1;
        if (a.Y > b.Y)
        {
            (a, b) = (b, a);
            dir = -1;
        }
        float dxdy = (b.X - a.X) / (b.Y - a.Y);
        float x = a.X;
        int y0 = (int)MathF.Floor(a.Y);
        if (a.Y < 0)
        {
            x -= a.Y * dxdy;
            y0 = 0;
        }
        int yEnd = Math.Min(_height, (int)MathF.Ceiling(b.Y));
        var area = _area;
        for (int y = y0; y < yEnd; y++)
        {
            int row = y * _stride;
            float dy = MathF.Min(y + 1, b.Y) - MathF.Max(y, a.Y);
            float xNext = x + dxdy * dy;
            float d = dy * dir;
            float x0 = MathF.Min(x, xNext), x1 = MathF.Max(x, xNext);
            // Clamp to the grid: area left of it accumulates in its first cell, right of it is lost
            // (the grid has a margin, so outlines stay inside it).
            x0 = Math.Clamp(x0, 0, _width);
            x1 = Math.Clamp(x1, 0, _width);
            float x0Floor = MathF.Floor(x0);
            int x0i = (int)x0Floor;
            float x1Ceil = MathF.Ceiling(x1);
            int x1i = (int)x1Ceil;
            if (x1i <= x0i + 1)
            {
                float xmf = 0.5f * (x0 + x1) - x0Floor;
                area[row + x0i] += d - d * xmf;
                area[row + x0i + 1] += d * xmf;
            }
            else
            {
                float s = 1 / (x1 - x0);
                float x0f = x0 - x0Floor;
                float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
                float x1f = x1 - x1Ceil + 1;
                float am = 0.5f * s * x1f * x1f;
                area[row + x0i] += d * a0;
                if (x1i == x0i + 2)
                {
                    area[row + x0i + 1] += d * (1 - a0 - am);
                }
                else
                {
                    float a1 = s * (1.5f - x0f);
                    area[row + x0i + 1] += d * (a1 - a0);
                    for (int xi = x0i + 2; xi < x1i - 1; xi++)
                        area[row + xi] += d * s;
                    float a2 = a1 + (x1i - x0i - 3) * s;
                    area[row + x1i - 1] += d * (1 - a2 - am);
                }
                area[row + x1i] += d * am;
            }
            x = xNext;
        }
    }
}
