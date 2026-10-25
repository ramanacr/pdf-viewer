using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Rendering;

namespace PdfEngine.Comparison;

public enum DiffType
{
    Unchanged,
    Added,
    Deleted,
    Modified
}

public record TextDiffChunk
{
    public DiffType Type { get; init; }
    public string Text { get; init; } = string.Empty;
    public int PageNumber { get; init; }
    public PdfRect? Bounds { get; init; }
}

public record DocumentComparisonResult
{
    public int PageCountA { get; init; }
    public int PageCountB { get; init; }
    public double VisualSimilarityScore { get; init; } // 0.0 to 1.0
    public IReadOnlyList<TextDiffChunk> TextDifferences { get; init; } = Array.Empty<TextDiffChunk>();
    public IReadOnlyList<int> PagesWithVisualDifferences { get; init; } = Array.Empty<int>();
}

/// <summary>Compares two documents by text and by pixels.</summary>
public interface IPdfComparisonService
{
    /// <summary>Compares the text of two documents, page by page.</summary>
    /// <param name="documentA">The first (older) document.</param>
    /// <param name="documentB">The second (newer) document.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<DocumentComparisonResult> CompareDocumentsAsync(
        IPdfDocument documentA,
        IPdfDocument documentB,
        CancellationToken cancellationToken = default);

    /// <summary>Renders one page of both documents and returns an image that marks the pixels that differ.</summary>
    /// <param name="documentA">The first document.</param>
    /// <param name="documentB">The second document.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="dpi">The resolution of the comparison.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<RenderedPage> GenerateVisualDiffPageAsync(
        IPdfDocument documentA,
        IPdfDocument documentB,
        int pageNumber,
        double dpi = 150.0,
        CancellationToken cancellationToken = default);
}
