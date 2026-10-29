using System;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Text;

/// <summary>
/// A rectangle by its edges in PDF orientation (y up), with the semantics of PDFium's float
/// rectangle that the text page's rules are written against: a zero-width rectangle is a valid
/// point or line (not "nothing"), intersecting disjoint rectangles gives all zeros, and union and
/// containment normalize first.
/// </summary>
public struct PdfTextRect : IEquatable<PdfTextRect>
{
    public double Left, Bottom, Right, Top;

    public PdfTextRect(double left, double bottom, double right, double top)
    {
        Left = left; Bottom = bottom; Right = right; Top = top;
    }

    public readonly double Width => Right - Left;
    public readonly double Height => Top - Bottom;
    public readonly bool IsEmpty => Left >= Right || Bottom >= Top;

    public static PdfTextRect Point(double x, double y) => new(x, y, x, y);

    public void Normalize()
    {
        if (Left > Right) (Left, Right) = (Right, Left);
        if (Bottom > Top) (Bottom, Top) = (Top, Bottom);
    }

    public void Union(PdfTextRect other)
    {
        Normalize();
        other.Normalize();
        Left = Math.Min(Left, other.Left);
        Bottom = Math.Min(Bottom, other.Bottom);
        Right = Math.Max(Right, other.Right);
        Top = Math.Max(Top, other.Top);
    }

    public void Intersect(PdfTextRect other)
    {
        Normalize();
        other.Normalize();
        Left = Math.Max(Left, other.Left);
        Bottom = Math.Max(Bottom, other.Bottom);
        Right = Math.Min(Right, other.Right);
        Top = Math.Min(Top, other.Top);
        if (Left > Right || Bottom > Top)
            this = default;
    }

    public readonly bool Contains(PdfPoint p)
    {
        var n = this;
        n.Normalize();
        return p.X <= n.Right && p.X >= n.Left && p.Y <= n.Top && p.Y >= n.Bottom;
    }

    public void Translate(double dx, double dy)
    {
        Left += dx; Right += dx; Bottom += dy; Top += dy;
    }

    /// <summary>The bounds of the rectangle's four corners mapped by <paramref name="m"/>.</summary>
    public readonly PdfTextRect Transform(in PdfMatrix m)
    {
        var p1 = m.Transform(Left, Top);
        var p2 = m.Transform(Left, Bottom);
        var p3 = m.Transform(Right, Top);
        var p4 = m.Transform(Right, Bottom);
        return new PdfTextRect(
            Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X)),
            Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y)),
            Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X)),
            Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y)));
    }

    /// <summary>As a <see cref="PdfRect"/> (x, y at the lower left).</summary>
    public readonly PdfRect ToPdfRect() => new(Math.Min(Left, Right), Math.Min(Bottom, Top), Math.Abs(Width), Math.Abs(Height));

    public readonly bool Equals(PdfTextRect other) => Left == other.Left && Bottom == other.Bottom && Right == other.Right && Top == other.Top;
    public override readonly bool Equals(object? obj) => obj is PdfTextRect r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(Left, Bottom, Right, Top);
    public override readonly string ToString() => $"[{Left:0.##} {Bottom:0.##} {Right:0.##} {Top:0.##}]";
}

/// <summary>Matrix measures PDFium's text rules use.</summary>
internal static class TextMatrixMath
{
    private static double XUnit(in PdfMatrix m) => m.B == 0 ? Math.Abs(m.A) : Math.Sqrt(m.A * m.A + m.B * m.B);
    private static double YUnit(in PdfMatrix m) => m.C == 0 ? Math.Abs(m.D) : Math.Sqrt(m.C * m.C + m.D * m.D);

    /// <summary>A length mapped by the matrix's mean scale.</summary>
    public static double TransformDistance(this in PdfMatrix m, double distance) => distance * (XUnit(m) + YUnit(m)) / 2;

    /// <summary>The length of a horizontal step mapped by the matrix.</summary>
    public static double TransformXDistance(this in PdfMatrix m, double dx)
    {
        double fx = m.A * dx, fy = m.B * dx;
        return Math.Sqrt(fx * fx + fy * fy);
    }

    public static PdfMatrix Inverse(this in PdfMatrix m) => m.TryInvert(out var inv) ? inv : PdfMatrix.Identity;

    // PDFium computes text positions in single precision; where a rule compares two nearly equal
    // values (a step down exactly as long as the step along), double precision tips the other way.

    /// <summary>The value as PDFium holds it (single precision).</summary>
    public static double F(double v) => (float)v;

    public static PdfMatrix ToSingle(this in PdfMatrix m) => new(F(m.A), F(m.B), F(m.C), F(m.D), F(m.E), F(m.F));

    /// <summary><paramref name="l"/> then <paramref name="r"/>, in single precision.</summary>
    public static PdfMatrix MultiplyF(in PdfMatrix l, in PdfMatrix r)
    {
        float a = (float)l.A, b = (float)l.B, c = (float)l.C, d = (float)l.D, e = (float)l.E, f = (float)l.F;
        float ra = (float)r.A, rb = (float)r.B, rc = (float)r.C, rd = (float)r.D, re = (float)r.E, rf = (float)r.F;
        return new PdfMatrix(a * ra + b * rc, a * rb + b * rd, c * ra + d * rc, c * rb + d * rd, e * ra + f * rc + re, e * rb + f * rd + rf);
    }

    public static PdfMatrix InverseF(in PdfMatrix m)
    {
        float a = (float)m.A, b = (float)m.B, c = (float)m.C, d = (float)m.D, e = (float)m.E, f = (float)m.F;
        float i = a * d - b * c;
        if (Math.Abs(i) == 0) return PdfMatrix.Identity;
        float j = -i;
        return new PdfMatrix(d / i, b / j, c / j, a / i, (c * f - d * e) / i, (a * f - b * e) / j);
    }

    public static PdfPoint TransformF(in PdfMatrix m, PdfPoint p)
    {
        float x = (float)p.X, y = (float)p.Y;
        return new PdfPoint(x * (float)m.A + y * (float)m.C + (float)m.E, x * (float)m.B + y * (float)m.D + (float)m.F);
    }
}
