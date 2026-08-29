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
