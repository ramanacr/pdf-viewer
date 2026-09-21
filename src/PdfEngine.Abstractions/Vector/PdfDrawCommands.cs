using PdfEngine.Geometry;

namespace PdfEngine.Vector;

/// <summary>
/// Base class for all immutable drawing commands within a PDF display list.
/// </summary>
public abstract record PdfDrawCommand
{
    public PdfRect? Bounds { get; init; }

    protected PdfDrawCommand()
    {
        Bounds = null;
    }

    protected PdfDrawCommand(PdfRect? bounds)
    {
        Bounds = bounds;
    }
}

/// <summary>Pushes graphics state onto the graphics state stack (q).</summary>
public sealed record SaveState() : PdfDrawCommand;

/// <summary>Pops graphics state from the graphics state stack (Q).</summary>
public sealed record RestoreState() : PdfDrawCommand;

/// <summary>Concatenates a matrix into the current transformation matrix (cm).</summary>
public sealed record ConcatTransform(PdfMatrix Matrix) : PdfDrawCommand;

/// <summary>Fills a vector path with the specified paint and fill rule.</summary>
public sealed record FillPath(PdfPath Path, PdfPaint Paint, PdfFillRule Rule, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);

/// <summary>Strokes a vector path with the specified stroke and paint.</summary>
public sealed record StrokePath(PdfPath Path, PdfStroke Stroke, PdfPaint Paint, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);

/// <summary>Pushes a clipping path onto the clip stack (W / W*).</summary>
public sealed record PushClip(PdfPath Path, PdfFillRule Rule, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);

/// <summary>Pops a clipping path from the clip stack (via Q or explicit restore).</summary>
public sealed record PopClip() : PdfDrawCommand;

/// <summary>Draws a run of positioned glyphs preserving original font identity and coordinates.</summary>
public sealed record DrawGlyphRun(PdfGlyphRun Run, PdfPaint Paint, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);

/// <summary>Draws an embedded bitmap image transformed by the current CTM and image matrix.</summary>
public sealed record DrawImage(PdfImageRef Image, PdfMatrix Transform, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);

/// <summary>Draws a bounded fallback region resolved by the fallback provider.</summary>
public sealed record DrawFallbackRegion(PdfFallbackToken Token, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);

/// <summary>Begins an isolated/knockout transparency group.</summary>
public sealed record BeginTransparencyGroup(
    PdfRect? Bounds = null,
    double Alpha = 1.0,
    string? BlendMode = null,
    bool Isolated = false,
    bool Knockout = false) : PdfDrawCommand(Bounds);

/// <summary>Ends a transparency group and composites it onto the parent surface.</summary>
public sealed record EndTransparencyGroup() : PdfDrawCommand;

/// <summary>Draws a smooth shading (axial or radial gradient) filling the bounds or current clip.</summary>
public sealed record DrawShading(PdfShading Shading, PdfRect? Bounds = null)
    : PdfDrawCommand(Bounds);
