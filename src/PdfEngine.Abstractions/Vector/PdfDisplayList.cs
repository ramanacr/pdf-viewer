using System;
using System.Collections.Generic;
using PdfEngine.Geometry;

namespace PdfEngine.Vector;

/// <summary>
/// Classified features encountered during page interpretation.
/// </summary>
[Flags]
public enum PdfFeatureSet
{
    /// <summary>No content.</summary>
    None = 0,
    /// <summary>Filled or stroked paths.</summary>
    Paths = 1 << 0,
    /// <summary>Text.</summary>
    Text = 1 << 1,
    /// <summary>Images.</summary>
    Images = 1 << 2,
    /// <summary>Shadings (gradients).</summary>
    Shading = 1 << 3,
    /// <summary>Pattern fills or strokes.</summary>
    Patterns = 1 << 4,
    /// <summary>Transparency groups, soft masks or blend modes.</summary>
    Transparency = 1 << 5,
    /// <summary>Form XObjects.</summary>
    Forms = 1 << 6,
    /// <summary>Clipping paths.</summary>
    Clipping = 1 << 7,
    /// <summary>Regions that need a fallback renderer (see <see cref="IPdfDisplayList.FallbackTokens"/>).</summary>
    Fallback = 1 << 8,
    /// <summary>
    /// Damaged or unsupported content was drawn by best effort rather than handed to another
    /// renderer: an unknown operator ignored, a stream that could not be decoded skipped, the
    /// commands before a parse error kept (as PDFium does).
    /// </summary>
    Recovered = 1 << 9
}

/// <summary>
/// Immutable, device-independent display list representing all vector graphics on a PDF page.
/// Replayable at any zoom level, DPI, or rotation transform.
/// </summary>
public interface IPdfDisplayList
{
    /// <summary>The page number, starting at 1.</summary>
    int PageNumber { get; }
    /// <summary>The page size in points, before the page's /Rotate is applied.</summary>
    PdfSize PageSize { get; }
    /// <summary>The media box in PDF user space (points).</summary>
    PdfRect MediaBox { get; }
    /// <summary>The crop box in PDF user space (points): the visible area of the page.</summary>
    PdfRect CropBox { get; }
    /// <summary>The page's /Rotate value: 0, 90, 180 or 270 degrees clockwise.</summary>
    int RotationDegrees { get; }
    /// <summary>The drawing commands, in painting order.</summary>
    IReadOnlyList<PdfDrawCommand> Commands { get; }
    /// <summary>The kinds of content found on the page.</summary>
    PdfFeatureSet Features { get; }
    /// <summary>True when <see cref="FallbackTokens"/> is not empty.</summary>
    bool HasFallback { get; }
    /// <summary>The regions the engine could not express as vector commands, with the reason for each.</summary>
    IReadOnlyList<PdfFallbackToken> FallbackTokens { get; }
    /// <summary>The fraction of the crop box (0 to 1) that the fallback regions cover.</summary>
    double ComputeFallbackAreaRatio();
}

/// <summary>
/// Canonical implementation of immutable display list.
/// </summary>
public sealed class PdfDisplayList : IPdfDisplayList
{
    /// <inheritdoc/>
    public int PageNumber { get; }
    /// <inheritdoc/>
    public PdfSize PageSize { get; }
    /// <inheritdoc/>
    public PdfRect MediaBox { get; }
    /// <inheritdoc/>
    public PdfRect CropBox { get; }
    /// <inheritdoc/>
    public int RotationDegrees { get; }
    /// <inheritdoc/>
    public IReadOnlyList<PdfDrawCommand> Commands { get; }
    /// <inheritdoc/>
    public PdfFeatureSet Features { get; }
    /// <inheritdoc/>
    public bool HasFallback => FallbackTokens.Count > 0;
    /// <inheritdoc/>
    public IReadOnlyList<PdfFallbackToken> FallbackTokens { get; }

    /// <summary>Creates a display list. The engine builds display lists; hosts rarely create one.</summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="pageSize">The page size in points.</param>
    /// <param name="mediaBox">The media box.</param>
    /// <param name="cropBox">The crop box.</param>
    /// <param name="rotationDegrees">The page's /Rotate value.</param>
    /// <param name="commands">The drawing commands in painting order; null gives an empty list.</param>
    /// <param name="features">The kinds of content in <paramref name="commands"/>.</param>
    /// <param name="fallbackTokens">The regions that need a fallback renderer; null gives none.</param>
    public PdfDisplayList(
        int pageNumber,
        PdfSize pageSize,
        PdfRect mediaBox,
        PdfRect cropBox,
        int rotationDegrees,
        IReadOnlyList<PdfDrawCommand> commands,
        PdfFeatureSet features,
        IReadOnlyList<PdfFallbackToken>? fallbackTokens = null)
    {
        PageNumber = pageNumber;
        PageSize = pageSize;
        MediaBox = mediaBox;
        CropBox = cropBox;
        RotationDegrees = rotationDegrees;
        Commands = commands ?? Array.Empty<PdfDrawCommand>();
        Features = features;
        FallbackTokens = fallbackTokens ?? Array.Empty<PdfFallbackToken>();
    }

    /// <summary>
    /// Fraction of the crop box covered by fallback regions. Overlapping regions are counted once
    /// (sampled on a 64×64 grid) so many small tokens over the same object do not inflate the metric.
    /// </summary>
    public double ComputeFallbackAreaRatio()
    {
        var crop = CropBox.IsEmpty ? new PdfRect(0, 0, PageSize.Width, PageSize.Height) : CropBox;
        if (crop.IsEmpty || FallbackTokens.Count == 0)
            return 0.0;

        const int Grid = 64;
        int covered = 0;
        for (int gy = 0; gy < Grid; gy++)
        {
            double y = crop.Y + (gy + 0.5) * crop.Height / Grid;
            for (int gx = 0; gx < Grid; gx++)
            {
                double x = crop.X + (gx + 0.5) * crop.Width / Grid;
                foreach (var fb in FallbackTokens)
                {
                    if (fb.Bounds.Contains(x, y))
                    {
                        covered++;
                        break;
                    }
                }
            }
        }

        return covered / (double)(Grid * Grid);
    }
}
