using System;
using System.Collections.Generic;
using PdfEngine.Geometry;

namespace PdfEngine.Vector;

/// <summary>
/// Color representation in PDF vector operations, normalized to [0.0, 1.0].
/// </summary>
public readonly record struct PdfColor(float R, float G, float B, float A = 1.0f)
{
    public static readonly PdfColor Black = new(0f, 0f, 0f, 1f);
    public static readonly PdfColor White = new(1f, 1f, 1f, 1f);
    public static readonly PdfColor Transparent = new(0f, 0f, 0f, 0f);

    public static PdfColor FromGray(float gray, float alpha = 1.0f)
    {
        float clamped = Math.Clamp(gray, 0f, 1f);
        return new PdfColor(clamped, clamped, clamped, Math.Clamp(alpha, 0f, 1f));
    }

    public static PdfColor FromRgb(float r, float g, float b, float alpha = 1.0f) =>
        new(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), Math.Clamp(alpha, 0f, 1f));

    public static PdfColor FromCmyk(float c, float m, float y, float k, float alpha = 1.0f)
    {
        c = Math.Clamp(c, 0f, 1f);
        m = Math.Clamp(m, 0f, 1f);
        y = Math.Clamp(y, 0f, 1f);
        k = Math.Clamp(k, 0f, 1f);

        float r = (1f - c) * (1f - k);
        float g = (1f - m) * (1f - k);
        float b = (1f - y) * (1f - k);
        return new PdfColor(r, g, b, Math.Clamp(alpha, 0f, 1f));
    }
}

/// <summary>
/// Fill rule for path rasterization/tessellation.
/// </summary>
public enum PdfFillRule
{
    NonZero = 0,
    EvenOdd = 1
}

/// <summary>
/// Line cap styles matching PDF specification (ISO 32000-2 Table 106).
/// </summary>
public enum PdfLineCap
{
    Butt = 0,
    Round = 1,
    Square = 2
}

/// <summary>
/// Line join styles matching PDF specification (ISO 32000-2 Table 107).
/// </summary>
public enum PdfLineJoin
{
    Miter = 0,
    Round = 1,
    Bevel = 2
}

/// <summary>
/// Stroke parameters for path stroking.
/// </summary>
public sealed record PdfStroke(
    double Width = 1.0,
    PdfLineCap Cap = PdfLineCap.Butt,
    PdfLineJoin Join = PdfLineJoin.Miter,
    double MiterLimit = 10.0,
    IReadOnlyList<double>? DashArray = null,
    double DashPhase = 0.0);

/// <summary>
/// Paint specification for fills and strokes.
/// </summary>
public sealed record PdfPaint(
    PdfColor Color,
    double Alpha = 1.0,
    string? PatternName = null);

/// <summary>
/// Segments forming a vector path.
/// </summary>
public abstract record PdfPathSegment;

public sealed record PdfMoveTo(PdfPoint Point) : PdfPathSegment;
public sealed record PdfLineTo(PdfPoint Point) : PdfPathSegment;
public sealed record PdfCubicBezierTo(PdfPoint Control1, PdfPoint Control2, PdfPoint EndPoint) : PdfPathSegment;
public sealed record PdfCloseSubpath() : PdfPathSegment;

/// <summary>
/// Immutable vector path representation preserving exact Bézier curves and lines.
/// </summary>
public sealed class PdfPath
{
    public static readonly PdfPath Empty = new([]);

    public IReadOnlyList<PdfPathSegment> Segments { get; }
    public PdfRect Bounds { get; }

    public PdfPath(IReadOnlyList<PdfPathSegment> segments)
    {
        Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        Bounds = ComputeBounds(segments);
    }

    private static PdfRect ComputeBounds(IReadOnlyList<PdfPathSegment> segments)
    {
        if (segments.Count == 0)
            return PdfRect.Empty;

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        bool hasPoints = false;

        void IncludePoint(PdfPoint pt)
        {
            hasPoints = true;
            if (pt.X < minX) minX = pt.X;
            if (pt.X > maxX) maxX = pt.X;
            if (pt.Y < minY) minY = pt.Y;
            if (pt.Y > maxY) maxY = pt.Y;
        }

        foreach (var seg in segments)
        {
            switch (seg)
            {
                case PdfMoveTo m:
                    IncludePoint(m.Point);
                    break;
                case PdfLineTo l:
                    IncludePoint(l.Point);
                    break;
                case PdfCubicBezierTo c:
                    IncludePoint(c.Control1);
                    IncludePoint(c.Control2);
                    IncludePoint(c.EndPoint);
                    break;
            }
        }

        return hasPoints
            ? new PdfRect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY))
            : PdfRect.Empty;
    }
}

/// <summary>
/// Individual positioned glyph in a glyph run.
/// </summary>
public sealed record PdfGlyph(
    ushort GlyphId,
    double AdvanceX,
    double AdvanceY,
    double OffsetX = 0.0,
    double OffsetY = 0.0,
    string? Unicode = null);

/// <summary>
/// Glyph run preserving exact PDF positioning, font identity, and text matrix.
/// </summary>
public sealed record PdfGlyphRun(
    string FontResourceName,
    double FontSize,
    IReadOnlyList<PdfGlyph> Glyphs,
    PdfMatrix TextMatrix,
    double HorizontalScaling = 100.0,
    double CharacterSpacing = 0.0,
    double WordSpacing = 0.0,
    double TextRise = 0.0,
    int RenderingMode = 0,
    string? FontFamilyName = null,
    string? FullText = null);

/// <summary>
/// Reference to an embedded image or image XObject.
/// </summary>
public sealed record PdfImageRef(
    string ImageId,
    int Width,
    int Height,
    int BitsPerComponent,
    string ColorSpaceName,
    bool HasAlpha,
    ReadOnlyMemory<byte> ImageData,
    string? Filter = null);

/// <summary>
/// Base record for smooth shading specifications (ISO 32000-2 Clause 8.7).
/// </summary>
public abstract record PdfShading(string ShadingType);

/// <summary>
/// Type 2 Axial (Linear) Shading defined along an axis between two points.
/// </summary>
public sealed record PdfAxialShading(
    PdfPoint StartPoint,
    PdfPoint EndPoint,
    PdfColor StartColor,
    PdfColor EndColor,
    bool ExtendStart = true,
    bool ExtendEnd = true) : PdfShading("Axial");

/// <summary>
/// Type 3 Radial Shading defined between two circles with centers and radii.
/// </summary>
public sealed record PdfRadialShading(
    PdfPoint StartCenter,
    double StartRadius,
    PdfPoint EndCenter,
    double EndRadius,
    PdfColor StartColor,
    PdfColor EndColor,
    bool ExtendStart = true,
    bool ExtendEnd = true) : PdfShading("Radial");
