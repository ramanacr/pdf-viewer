using System;
using System.Collections.Generic;
using System.Threading;
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
/// <param name="GlyphId">
/// Glyph index inside the run's font program when <see cref="PdfGlyphRun.Face"/> carries one;
/// otherwise the character code / CID (kept for diagnostics).
/// </param>
/// <param name="AdvanceX">Horizontal displacement in text space (font size, Tc, Tw and Tz applied).</param>
/// <param name="OffsetX">Glyph origin relative to the run origin, in text space.</param>
/// <param name="Unicode">Semantic text for selection/search/copy. Never used to lay out glyphs (ADR-006).</param>
/// <param name="CharCode">Original PDF character code (or CID for composite fonts); -1 when unknown.</param>
public sealed record PdfGlyph(
    ushort GlyphId,
    double AdvanceX,
    double AdvanceY,
    double OffsetX = 0.0,
    double OffsetY = 0.0,
    string? Unicode = null,
    int CharCode = -1);

/// <summary>Font program container format a backend may load directly.</summary>
public enum PdfFontProgramFormat
{
    /// <summary>No embedded program usable by a backend; render with a metric-compatible substitute.</summary>
    None = 0,
    /// <summary>sfnt with TrueType outlines (FontFile2, or FontFile3/OpenType with glyf).</summary>
    TrueType = 1,
    /// <summary>sfnt with CFF outlines (FontFile3/OpenType).</summary>
    OpenTypeCff = 2,
    /// <summary>Embedded but in a container the backend cannot load (bare Type1 / CFF). Backends must classify.</summary>
    Unsupported = 3,
}

/// <summary>
/// Backend-neutral identity of the font used by a glyph run. Carries the embedded program so a
/// backend can render the author's glyphs; carries style hints for substitution when it cannot.
/// </summary>
public sealed record PdfFontFace(
    string Key,
    string? PostScriptName,
    PdfFontProgramFormat Format,
    ReadOnlyMemory<byte> ProgramData,
    bool IsBold = false,
    bool IsItalic = false,
    bool IsSerif = false,
    bool IsFixedPitch = false,
    bool IsSymbolic = false,
    double Ascent = 0.8,
    double Descent = -0.2);

/// <summary>
/// Glyph run preserving exact PDF positioning, font identity, and text matrix.
/// </summary>
/// <remarks>
/// <see cref="TextMatrix"/> maps text space to the current user space (the renderer's CTM stack
/// supplies user→page). <see cref="TextToPage"/> is the full text space → default user space
/// transform, for semantic consumers (selection, search hit boxes) that do not replay the stack.
/// </remarks>
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
    string? FullText = null)
{
    /// <summary>Text space → default user (page) space. Identity-based when produced by older builders.</summary>
    public PdfMatrix TextToPage { get; init; } = TextMatrix;

    /// <summary>Font program and style hints; null for runs built without font resolution.</summary>
    public PdfFontFace? Face { get; init; }

    /// <summary>Stroke paint for rendering modes that stroke (1, 2, 5, 6).</summary>
    public PdfPaint? StrokePaint { get; init; }

    /// <summary>Stroke parameters (line width etc.) for stroking rendering modes.</summary>
    public PdfStroke? Stroke { get; init; }

    /// <summary>True when mode 3/7 (invisible): kept for selection/search, never painted.</summary>
    public bool IsInvisible => RenderingMode == 3 || RenderingMode == 7;
}

/// <summary>
/// Reference to an embedded image or image XObject.
/// </summary>
/// <remarks>
/// Pixels are produced lazily by <see cref="Source"/> so a display list does not pin decoded
/// bitmaps (08_PERFORMANCE_MEMORY_BUDGETS: decoded images are separately budgeted).
/// <see cref="ImageData"/> is retained for builders/tests that hand over raw RGB24.
/// </remarks>
public sealed record PdfImageRef(
    string ImageId,
    int Width,
    int Height,
    int BitsPerComponent,
    string ColorSpaceName,
    bool HasAlpha,
    ReadOnlyMemory<byte> ImageData,
    string? Filter = null)
{
    public IPdfImageSource? Source { get; init; }
    public bool Interpolate { get; init; }

    /// <summary>Constant alpha (ExtGState /ca) applied when the image is painted.</summary>
    public double Opacity { get; init; } = 1.0;
}

/// <summary>Pixel layout of a decoded image.</summary>
public enum PdfDecodedImageFormat
{
    /// <summary>Straight (non-premultiplied) 8-bit B,G,R,A per pixel, top row first.</summary>
    Bgra32 = 0,
    /// <summary>Still-encoded JPEG (DCTDecode) bytes; the backend's codec decodes them.</summary>
    Jpeg = 1,
}

/// <summary>Decoded image pixels plus an optional separate alpha plane (SMask / Mask).</summary>
/// <param name="Alpha">Width×Height 8-bit alpha applied on top of <see cref="Data"/>; null when opaque or already in Bgra32.</param>
/// <param name="InvertCmykJpeg">JPEG is Adobe-style inverted CMYK that the backend must un-invert.</param>
public sealed record PdfDecodedImage(
    int Width,
    int Height,
    PdfDecodedImageFormat Format,
    ReadOnlyMemory<byte> Data,
    ReadOnlyMemory<byte>? Alpha = null,
    bool InvertCmykJpeg = false)
{
    public long ByteSize => Data.Length + (Alpha?.Length ?? 0);
}

/// <summary>Lazy, cacheable producer of image pixels. Implementations must be thread-safe.</summary>
public interface IPdfImageSource
{
    /// <summary>Stable identity (document + object) used by backend image caches.</summary>
    string CacheKey { get; }
    PdfDecodedImage Decode(CancellationToken cancellationToken = default);
}

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
    bool ExtendEnd = true) : PdfShading("Axial")
{
    /// <summary>Colour stops sampled from the shading function (offset 0..1 along the axis).</summary>
    public IReadOnlyList<PdfGradientStop>? Stops { get; init; }
}

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
    bool ExtendEnd = true) : PdfShading("Radial")
{
    /// <summary>Colour stops sampled from the shading function (offset 0..1 from start to end circle).</summary>
    public IReadOnlyList<PdfGradientStop>? Stops { get; init; }

    /// <summary>
    /// Two arbitrary circles (ISO 32000-2 8.7.4.5.4), not expressible as a focal/centre radial
    /// brush: <see cref="Stops"/> are over the shading parameter t (offset 0 = start circle) and the
    /// backend evaluates the largest t per pixel. Backends that cannot must classify the area.
    /// </summary>
    public bool IsGeneral { get; init; }
}

/// <summary>Gradient colour stop.</summary>
public readonly record struct PdfGradientStop(double Offset, PdfColor Color);
