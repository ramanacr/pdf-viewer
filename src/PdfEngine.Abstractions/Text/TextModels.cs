using PdfEngine.Geometry;

namespace PdfEngine.Text;

/// <summary>
/// A granular text segment with character/word bounds and normalized coordinates.
/// </summary>
public record TextSegment
{
    /// <summary>The text of the segment (one word for the vector engine).</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>The page number, starting at 1.</summary>
    public int PageNumber { get; init; }
    /// <summary>The index of the first character in the page's character list.</summary>
    public int StartIndex { get; init; }
    /// <summary>The number of characters.</summary>
    public int Length { get; init; }
    /// <summary>The left edge, as a fraction of the displayed page width (0 to 1, from the left).</summary>
    public double X { get; init; }
    /// <summary>The top edge, as a fraction of the displayed page height (0 to 1, from the top).</summary>
    public double Y { get; init; }
    /// <summary>The width, as a fraction of the displayed page width.</summary>
    public double Width { get; init; }
    /// <summary>The height, as a fraction of the displayed page height.</summary>
    public double Height { get; init; }
    /// <summary>The approximate font size in points.</summary>
    public double FontSize { get; init; }
    /// <summary>The font name, or an empty string when the engine does not report it.</summary>
    public string FontName { get; init; } = string.Empty;

    /// <summary>The bounds as one rectangle in the same normalized coordinates.</summary>
    public PdfRect Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// A search match hit across a PDF document with geometry and surrounding context.
/// </summary>
public class SearchMatch
{
    /// <summary>The page of the match, starting at 1.</summary>
    public int PageNumber { get; set; }
    /// <summary>The text that was searched for.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>The left edge, as a fraction of the displayed page width (0 to 1, from the left).</summary>
    public double X { get; set; }
    /// <summary>The top edge, as a fraction of the displayed page height (0 to 1, from the top).</summary>
    public double Y { get; set; }
    /// <summary>The width, as a fraction of the displayed page width.</summary>
    public double Width { get; set; }
    /// <summary>The height, as a fraction of the displayed page height.</summary>
    public double Height { get; set; }
    /// <summary>The index of the first matched character in the page's character list.</summary>
    public int CharacterIndex { get; set; }
    /// <summary>The number of matched characters.</summary>
    public int MatchLength { get; set; }
    /// <summary>The page text around the match, on one line, for a result list.</summary>
    public string ContextSnippet { get; set; } = string.Empty;
    /// <summary>Set by a viewer for the match it shows now. The engine leaves it false.</summary>
    public bool IsCurrentMatch { get; set; }

    /// <summary>The bounds as one rectangle in the same normalized coordinates.</summary>
    public PdfRect Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// Search query options.
/// </summary>
public record SearchOptions
{
    /// <summary>Matches upper and lower case exactly (default false).</summary>
    public bool MatchCase { get; init; } = false;
    /// <summary>Matches only whole words (default false).</summary>
    public bool MatchWholeWord { get; init; } = false;
    /// <summary>Stops the search after this number of matches (default 1000).</summary>
    public int MaxResults { get; init; } = 1000;
}

/// <summary>
/// Text extraction and text search contract.
/// </summary>
public interface IPdfTextService
{
    /// <summary>Gets the words of a page with their positions.</summary>
    /// <param name="document">An open document.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The segments in the page's character order; an empty list for a page without text or a page number out of range.</returns>
    ValueTask<IReadOnlyList<TextSegment>> ExtractTextSegmentsAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the text of a page as one string, in the page's character order.</summary>
    /// <param name="document">An open document.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The text; an empty string for a page without text or a page number out of range.</returns>
    ValueTask<string> ExtractPageTextAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Finds every occurrence of <paramref name="query"/> in the document, page by page.</summary>
    /// <param name="document">An open document.</param>
    /// <param name="query">The text to find. An empty query finds nothing.</param>
    /// <param name="options">Case, whole-word and result-count options; null uses the defaults.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The matches in page order, at most <see cref="SearchOptions.MaxResults"/>.</returns>
    ValueTask<IReadOnlyList<SearchMatch>> SearchTextAsync(
        IPdfDocument document,
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default);
}
