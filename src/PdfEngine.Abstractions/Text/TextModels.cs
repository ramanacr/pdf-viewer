using PdfEngine.Geometry;

namespace PdfEngine.Text;

/// <summary>
/// A granular text segment with character/word bounds and normalized coordinates.
/// </summary>
public record TextSegment
{
    public string Text { get; init; } = string.Empty;
    public int PageNumber { get; init; }
    public int StartIndex { get; init; }
    public int Length { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double FontSize { get; init; }
    public string FontName { get; init; } = string.Empty;

    public PdfRect Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// A search match hit across a PDF document with geometry and surrounding context.
/// </summary>
public class SearchMatch
{
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int CharacterIndex { get; set; }
    public int MatchLength { get; set; }
    public string ContextSnippet { get; set; } = string.Empty;
    public bool IsCurrentMatch { get; set; }

    public PdfRect Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// Search query options.
/// </summary>
public record SearchOptions
{
    public bool MatchCase { get; init; } = false;
    public bool MatchWholeWord { get; init; } = false;
    public int MaxResults { get; init; } = 1000;
}

/// <summary>
/// Text extraction and text search contract.
/// </summary>
public interface IPdfTextService
{
    ValueTask<IReadOnlyList<TextSegment>> ExtractTextSegmentsAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    ValueTask<string> ExtractPageTextAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SearchMatch>> SearchTextAsync(
        IPdfDocument document,
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default);
}
