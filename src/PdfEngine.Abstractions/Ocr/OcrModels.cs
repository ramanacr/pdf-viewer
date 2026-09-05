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

public interface IOcrEngine
{
    string EngineName { get; }
    IReadOnlyList<string> SupportedLanguages { get; }

    ValueTask<OcrPageResult> RecognizePageAsync(
        IPdfDocument document,
        int pageNumber,
        string language = "en",
        CancellationToken cancellationToken = default);
}
