using PdfEngine.Documents;
using PdfEngine.Geometry;

namespace PdfEngine.Ocr;

public record OcrWord
{
    public string Text { get; init; } = string.Empty;
    public PdfRect Bounds { get; init; }
    public double Confidence { get; init; } = 1.0;
}

public record OcrPageResult
{
    public int PageNumber { get; init; }
    public string FullText { get; init; } = string.Empty;
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();
    public string Language { get; init; } = "en";
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// True when the text came from actual optical recognition of the rendered page.
    /// False when it was read from the page's embedded text layer, which is exact rather
    /// than recognized. Callers must be able to tell these apart: a scanned page with no
    /// text layer yields nothing at all unless a real optical engine ran.
    /// </summary>
    public bool UsedOpticalRecognition { get; init; }

    /// <summary>
    /// Human-readable note about how the result was produced, including why it may be empty.
    /// </summary>
    public string Notes { get; init; } = string.Empty;
}

/// <summary>Recognizes the text of a page.</summary>
public interface IOcrEngine
{
    /// <summary>A display name of the OCR engine.</summary>
    string EngineName { get; }
    /// <summary>The language codes that <see cref="RecognizePageAsync"/> accepts.</summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>Recognizes the words of a page with their positions.</summary>
    /// <param name="document">An open document.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="language">A language code from <see cref="SupportedLanguages"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<OcrPageResult> RecognizePageAsync(
        IPdfDocument document,
        int pageNumber,
        string language = "en",
        CancellationToken cancellationToken = default);
}
