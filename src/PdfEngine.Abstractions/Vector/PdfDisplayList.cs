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
    None = 0,
    Paths = 1 << 0,
    Text = 1 << 1,
    Images = 1 << 2,
    Shading = 1 << 3,
    Patterns = 1 << 4,
    Transparency = 1 << 5,
    Forms = 1 << 6,
    Clipping = 1 << 7,
    Fallback = 1 << 8
}

/// <summary>
/// Immutable, device-independent display list representing all vector graphics on a PDF page.
/// Replayable at any zoom level, DPI, or rotation transform.
/// </summary>
public interface IPdfDisplayList
{
    int PageNumber { get; }
    PdfSize PageSize { get; }
    PdfRect MediaBox { get; }
    PdfRect CropBox { get; }
    int RotationDegrees { get; }
    IReadOnlyList<PdfDrawCommand> Commands { get; }
    PdfFeatureSet Features { get; }
    bool HasFallback { get; }
    IReadOnlyList<PdfFallbackToken> FallbackTokens { get; }
    double ComputeFallbackAreaRatio();
}

/// <summary>
/// Canonical implementation of immutable display list.
/// </summary>
public sealed class PdfDisplayList : IPdfDisplayList
{
    public int PageNumber { get; }
    public PdfSize PageSize { get; }
    public PdfRect MediaBox { get; }
    public PdfRect CropBox { get; }
    public int RotationDegrees { get; }
    public IReadOnlyList<PdfDrawCommand> Commands { get; }
    public PdfFeatureSet Features { get; }
    public bool HasFallback => FallbackTokens.Count > 0;
    public IReadOnlyList<PdfFallbackToken> FallbackTokens { get; }

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
