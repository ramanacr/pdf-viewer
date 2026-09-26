using PdfEngine.Geometry;

namespace PdfEngine.Vector;

/// <summary>
/// Base class for all immutable drawing commands within a PDF display list.
/// </summary>
/// <remarks>
/// Geometry inside a command is in the current user space established by the preceding
/// <see cref="ConcatTransform"/> commands. <see cref="Bounds"/>, when present, is a conservative
/// box in default user space (page space) so renderers can cull without replaying transforms.
/// Unknown bounds (null) means "always draw".
/// </remarks>
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
/// <summary>
/// Intersects the clip with the area a stroke of <paramref name="Path"/> would cover (pattern-painted
/// strokes: the pattern fills the stroke outline). Popped like <see cref="PushClip"/>.
/// </summary>
public sealed record PushStrokeClip(PdfPath Path, PdfStroke Stroke, PdfRect? Bounds = null) : PdfDrawCommand(Bounds);

/// <summary>
/// Intersects the clip with the outlines of glyph runs (text rendering modes 4–7 at ET, and
/// pattern-filled text). Runs are in the current user space. Popped like <see cref="PushClip"/>.
/// </summary>
public sealed record PushTextClip(IReadOnlyList<PdfGlyphRun> Runs, PdfRect? Bounds = null) : PdfDrawCommand(Bounds);

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

/// <summary>PDF blend modes (ISO 32000-2 11.3.5, Tables 134–135).</summary>
public enum PdfBlendMode
{
    Normal = 0,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

/// <summary>
/// A soft mask (ISO 32000-2 11.6.5.2): the mask group's content, recorded as its own display list
/// in the user space in effect when the ExtGState was set, plus how its values become alpha.
/// </summary>
/// <param name="Luminosity">True: mask = luminosity of the group over <paramref name="Backdrop"/>; false: mask = group alpha.</param>
/// <param name="Content">Mask group content; coordinates map to page space through <paramref name="MaskToPage"/>.</param>
/// <param name="Transfer">Optional /TR transfer function sampled at 256 points (0..1), applied to mask values.</param>
public sealed record PdfSoftMask(
    bool Luminosity,
    IPdfDisplayList Content,
    PdfMatrix MaskToPage,
    PdfColor Backdrop,
    IReadOnlyList<float>? Transfer = null);

/// <summary>
/// Begins a group composited as a unit with the parent: group alpha, blend mode and soft mask
/// (ISO 32000-2 11.4, 11.6). Objects painted under a non-Normal blend mode or a soft mask are
/// wrapped in a group of their own, which is the specified semantics for a single object.
/// Backends that cannot composite these must report the group's bounds as fallback.
/// </summary>
public sealed record BeginCompositingGroup(
    PdfRect? Bounds,
    double Alpha,
    PdfBlendMode Blend,
    PdfSoftMask? SoftMask,
    bool Isolated,
    bool Knockout) : PdfDrawCommand(Bounds);

/// <summary>Ends the innermost <see cref="BeginCompositingGroup"/>.</summary>
public sealed record EndCompositingGroup() : PdfDrawCommand;

/// <summary>
/// A tiling pattern (ISO 32000-2 8.7.3): one cell recorded as its own display list in pattern
/// space (already clipped to <paramref name="BBox"/>), repeated every <paramref name="XStep"/> ×
/// <paramref name="YStep"/>. Uncoloured patterns carry their colour baked into the cell.
/// </summary>
public sealed record PdfTilingPattern(IPdfDisplayList Cell, PdfRect BBox, double XStep, double YStep);

/// <summary>
/// Fills the current clip with a tiling pattern. The current user space is pattern space; the
/// bounds are the painted area in page space.
/// </summary>
public sealed record DrawTilingPattern(PdfTilingPattern Pattern, PdfRect? Bounds) : PdfDrawCommand(Bounds);
