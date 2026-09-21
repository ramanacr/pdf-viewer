using System;

namespace PdfEngine.Geometry;

/// <summary>
/// Immutable 2D affine transformation matrix using PDF conventions:
/// [ A  B  0 ]
/// [ C  D  0 ]
/// [ E  F  1 ]
/// where (x', y') = (A*x + C*y + E, B*x + D*y + F).
/// </summary>
public readonly record struct PdfMatrix(
    double A,
    double B,
    double C,
    double D,
    double E,
    double F)
{
    public static readonly PdfMatrix Identity = new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);

    public bool IsIdentity =>
        Math.Abs(A - 1.0) < 1e-9 &&
        Math.Abs(B) < 1e-9 &&
        Math.Abs(C) < 1e-9 &&
        Math.Abs(D - 1.0) < 1e-9 &&
        Math.Abs(E) < 1e-9 &&
        Math.Abs(F) < 1e-9;

    public double Determinant => (A * D) - (B * C);

    public bool IsInvertible => Math.Abs(Determinant) > 1e-12;

    public static PdfMatrix CreateTranslation(double dx, double dy) =>
        new(1.0, 0.0, 0.0, 1.0, dx, dy);

    public static PdfMatrix CreateScale(double sx, double sy) =>
        new(sx, 0.0, 0.0, sy, 0.0, 0.0);

    public static PdfMatrix CreateRotation(double angleRadians)
    {
        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        return new(cos, sin, -sin, cos, 0.0, 0.0);
    }

    /// <summary>
    /// Multiplies this matrix by another: result = this * other.
    /// In PDF concatenation cm: CTM_new = M * CTM_old, so matrix multiplication matches standard row-vector convention.
    /// </summary>
    public PdfMatrix Multiply(in PdfMatrix m) =>
        new(
            (A * m.A) + (B * m.C),
            (A * m.B) + (B * m.D),
            (C * m.A) + (D * m.C),
            (C * m.B) + (D * m.D),
            (E * m.A) + (F * m.C) + m.E,
            (E * m.B) + (F * m.D) + m.F);

    public static PdfMatrix operator *(in PdfMatrix left, in PdfMatrix right) =>
        left.Multiply(right);

    public PdfPoint Transform(PdfPoint p) =>
        new((A * p.X) + (C * p.Y) + E, (B * p.X) + (D * p.Y) + F);

    public PdfPoint Transform(double x, double y) =>
        new((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    public PdfRect Transform(PdfRect rect)
    {
        if (rect.IsEmpty)
            return rect;

        PdfPoint p1 = Transform(rect.Left, rect.Top);
        PdfPoint p2 = Transform(rect.Right, rect.Top);
        PdfPoint p3 = Transform(rect.Left, rect.Bottom);
        PdfPoint p4 = Transform(rect.Right, rect.Bottom);

        double minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));

        return new PdfRect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    public bool TryInvert(out PdfMatrix inverse)
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-12)
        {
            inverse = Identity;
            return false;
        }

        double invDet = 1.0 / det;
        inverse = new PdfMatrix(
            D * invDet,
            -B * invDet,
            -C * invDet,
            A * invDet,
            ((C * F) - (D * E)) * invDet,
            ((B * E) - (A * F)) * invDet);
        return true;
    }
}
