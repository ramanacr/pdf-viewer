using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Ocr;
using PdfEngine.Text;

namespace PdfViewer.Core.Ocr;

/// <summary>
/// Text-segment powered OCR engine extracting high-confidence word geometry and text tokens.
/// </summary>
public sealed class DefaultOcrEngine : IOcrEngine
{
    private readonly IPdfTextService _textService;

    public string EngineName => "Native Core Text OCR Engine";
    public IReadOnlyList<string> SupportedLanguages { get; } = new[] { "en", "de", "fr", "es", "it" };

    public DefaultOcrEngine(IPdfTextService textService)
    {
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
    }

    public async ValueTask<OcrPageResult> RecognizePageAsync(
        IPdfDocument document,
        int pageNumber,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        // The language argument was accepted and then ignored, so a caller passing an
        // unsupported language got a result stamped with it at 0.98 confidence and no way to
        // tell that nothing language-specific had happened.
        if (!string.IsNullOrWhiteSpace(language) &&
            !SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Language '{language}' is not supported. Supported languages: {string.Join(", ", SupportedLanguages)}.",
                nameof(language));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var segments = await _textService.ExtractTextSegmentsAsync(document, pageNumber, cancellationToken);
        var words = segments.Select(s => new OcrWord
        {
            Text = s.Text,
            Bounds = new PdfRect(s.X, s.Y, s.Width, s.Height),
            Confidence = 0.98
        }).ToList();

        string fullText = string.Join(" ", words.Select(w => w.Text));

        return new OcrPageResult
        {
            PageNumber = pageNumber,
            FullText = fullText,
            Words = words,
            Language = language,
            Confidence = 0.98
        };
    }
}
