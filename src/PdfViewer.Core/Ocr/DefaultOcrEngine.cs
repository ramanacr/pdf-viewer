using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Ocr;
using PdfEngine.Text;

namespace PdfViewer.Core.Ocr;

/// <summary>
/// Text acquisition engine that prefers a page's embedded text layer and falls back to a
/// real optical recognition engine for pages that have none (scanned images).
///
/// The embedded text layer is exact, not recognized, so it is reported with
/// <see cref="OcrPageResult.UsedOpticalRecognition"/> false and full confidence. When a page
/// has no text layer and no optical engine was supplied, this returns an EMPTY result with
/// zero confidence and an explanatory note - it does not invent a confidence score for text
/// it never recognized.
/// </summary>
public sealed class DefaultOcrEngine : IOcrEngine
{
    private readonly IPdfTextService _textService;
    private readonly IOcrEngine? _opticalEngine;

    public string EngineName => _opticalEngine == null
        ? "Embedded Text Layer Reader (no optical recognition configured)"
        : $"Embedded Text Layer Reader with {_opticalEngine.EngineName} fallback";

    public IReadOnlyList<string> SupportedLanguages =>
        _opticalEngine?.SupportedLanguages ?? new[] { "en", "de", "fr", "es", "it" };

    /// <param name="textService">Reads the page's embedded text layer.</param>
    /// <param name="opticalEngine">
    /// Optional real OCR engine used for pages with no embedded text. Without it, scanned
    /// pages yield nothing - which is reported honestly rather than masked.
    /// </param>
    public DefaultOcrEngine(IPdfTextService textService, IOcrEngine? opticalEngine = null)
    {
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _opticalEngine = opticalEngine;
    }

    /// <summary>
    /// Matches on the primary language subtag, so a plain "en" is satisfied by an installed
    /// "en-US" or "en-GB" recognizer. Exact-string matching rejected the default language
    /// outright on any machine whose OCR packs are region-tagged.
    /// </summary>
    private bool IsLanguageSupported(string language)
    {
        string requested = PrimarySubtag(language);
        foreach (string supported in SupportedLanguages)
        {
            if (string.Equals(supported, language, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(PrimarySubtag(supported), requested, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string PrimarySubtag(string languageTag)
    {
        int dash = languageTag.IndexOf('-');
        return dash < 0 ? languageTag : languageTag.Substring(0, dash);
    }

    public async ValueTask<OcrPageResult> RecognizePageAsync(
        IPdfDocument document,
        int pageNumber,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        if (!string.IsNullOrWhiteSpace(language) && !IsLanguageSupported(language))
        {
            throw new ArgumentException(
                $"Language '{language}' is not supported. Supported languages: {string.Join(", ", SupportedLanguages)}.",
                nameof(language));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var segments = await _textService.ExtractTextSegmentsAsync(document, pageNumber, cancellationToken);
        if (segments.Count > 0)
        {
            var words = segments.Select(s => new OcrWord
            {
                Text = s.Text,
                Bounds = new PdfRect(s.X, s.Y, s.Width, s.Height),
                // Text read from the embedded layer is exact, not a recognition estimate.
                Confidence = 1.0
            }).ToList();

            return new OcrPageResult
            {
                PageNumber = pageNumber,
                FullText = string.Join(" ", words.Select(w => w.Text)),
                Words = words,
                Language = language,
                Confidence = 1.0,
                UsedOpticalRecognition = false,
                Notes = "Text read from the page's embedded text layer."
            };
        }

        // No embedded text: this is where real optical recognition is required.
        if (_opticalEngine != null)
        {
            return await _opticalEngine.RecognizePageAsync(document, pageNumber, language, cancellationToken);
        }

        return new OcrPageResult
        {
            PageNumber = pageNumber,
            FullText = string.Empty,
            Words = Array.Empty<OcrWord>(),
            Language = language,
            Confidence = 0.0,
            UsedOpticalRecognition = false,
            Notes = "The page has no embedded text layer and no optical recognition engine is configured, " +
                    "so no text could be produced."
        };
    }
}
