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

public interface IPdfComparisonService
{
    ValueTask<DocumentComparisonResult> CompareDocumentsAsync(
        IPdfDocument documentA,
        IPdfDocument documentB,
        CancellationToken cancellationToken = default);

    ValueTask<RenderedPage> GenerateVisualDiffPageAsync(
        IPdfDocument documentA,
        IPdfDocument documentB,
        int pageNumber,
        double dpi = 150.0,
        CancellationToken cancellationToken = default);
}
