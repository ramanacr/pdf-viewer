using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Fonts;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>What a painting operation painted.</summary>
public enum PdfPaintKind
{
    Fill,
    Stroke,
    /// <summary>Filled text (or invisible text, see <see cref="PdfPaintEvent.Invisible"/>).</summary>
    Text,
    Image,
    /// <summary>A stencil mask painted in the fill colour.</summary>
    ImageMask,
    Shading,
    /// <summary>A form XObject that is a transparency group.</summary>
    TransparencyGroup,
}

/// <summary>
/// One painting operation as the interpreter executed it, for print production checks: its colour, where it
/// is on the page, and the measures preflight looks at. Carries no text of the document.
/// </summary>
/// <param name="Bounds">Conservative bounds in default user space (page points, y up).</param>
public sealed record PdfPaintEvent(PdfPaintKind Kind, PdfRect Bounds)
{
    /// <summary>The colour space painted with (the document's, never a plate space); null for groups and JPEG 2000 images in their own colours.</summary>
    public PdfColorSpace? ColorSpace { get; init; }

    /// <summary>The colour's components for fills, strokes, text and stencil masks; null otherwise.</summary>
    public double[]? Components { get; init; }

    public double Alpha { get; init; } = 1;
    public PdfBlendMode Blend { get; init; }
    public bool SoftMask { get; init; }
    public bool Overprint { get; init; }
    public int OverprintMode { get; init; }

    /// <summary>Stroke width in page points along the thinnest direction (0 for a zero-width line).</summary>
    public double LineWidth { get; init; }

    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    /// <summary>The lower of the image's horizontal and vertical resolution as placed, in pixels per inch.</summary>
    public double ImagePpi { get; init; }

    /// <summary>The image has a soft mask (/SMask or /SMaskInData).</summary>
    public bool ImageSoftMask { get; init; }

    /// <summary>Object number of the image XObject, or −1 (inline images, unknown).</summary>
    public int ObjectNumber { get; init; } = -1;

    /// <summary>The font of text.</summary>
    public PdfFont? Font { get; init; }

    /// <summary>Text in rendering mode 3 or 7: kept for search, never painted.</summary>
    public bool Invisible { get; init; }
}

/// <summary>Receives every painting operation of a page build that observes the page.</summary>
public interface IPdfPaintObserver
{
    void OnPaint(PdfPaintEvent paint);
}
