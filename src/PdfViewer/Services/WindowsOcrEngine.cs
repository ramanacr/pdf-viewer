using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Ocr;
using PdfEngine.Rendering;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
// Both namespaces define OcrWord; alias the engine-neutral one we return.
using PdfOcrWord = PdfEngine.Ocr.OcrWord;

namespace PdfViewer.Services;

/// <summary>
/// Real optical character recognition backed by the OCR engine built into Windows
/// (Windows.Media.Ocr). Requires no external dependency, licence or network access.
///
/// The page is rasterized through the engine's own renderer and the resulting BGRA buffer
/// is handed to the platform recognizer; word geometry is mapped back into normalized page
/// coordinates so callers get the same coordinate space as embedded-text extraction.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly IPdfRenderer _renderer;
    private readonly double _renderDpi;

    public string EngineName => "Windows Optical Character Recognition";

    /// <summary>
    /// The languages the OCR runtime is actually able to recognize on THIS machine, which
    /// depends on the installed language packs - not a hardcoded list.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages { get; }

    public WindowsOcrEngine(IPdfRenderer renderer, double renderDpi = 300.0)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _renderDpi = renderDpi > 0 ? renderDpi : 300.0;

        SupportedLanguages = OcrEngine.AvailableRecognizerLanguages
            .Select(l => l.LanguageTag)
            .ToList();
    }

    /// <summary>
    /// True when the Windows OCR runtime has at least one usable recognizer installed.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception)
            {
                // The runtime is absent or blocked (e.g. Server Core without the media pack).
                return false;
            }
        }
    }

    public async ValueTask<OcrPageResult> RecognizePageAsync(
        IPdfDocument document,
        int pageNumber,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        OcrEngine? engine = CreateEngine(language);
        if (engine == null)
        {
            throw new NotSupportedException(
                $"No Windows OCR recognizer is installed for '{language}'. Available: " +
                (SupportedLanguages.Count > 0 ? string.Join(", ", SupportedLanguages) : "none") +
                ". Install the corresponding Windows language pack to enable recognition.");
        }

        // Rasterize the page. OCR quality depends heavily on resolution, hence 300 DPI.
        using var rendered = await _renderer.RenderPageAsync(document, new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = _renderDpi,
            HighQuality = true
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        using SoftwareBitmap bitmap = CreateSoftwareBitmap(rendered);
        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

        double pixelWidth = rendered.WidthPixels;
        double pixelHeight = rendered.HeightPixels;

        var words = new List<PdfOcrWord>();
        foreach (OcrLine line in result.Lines)
        {
            foreach (Windows.Media.Ocr.OcrWord word in line.Words)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rect = word.BoundingRect;
                words.Add(new PdfOcrWord
                {
                    Text = word.Text,
                    // Normalize to 0..1 top-down, matching embedded-text extraction.
                    Bounds = new PdfRect(
                        pixelWidth > 0 ? rect.X / pixelWidth : 0,
                        pixelHeight > 0 ? rect.Y / pixelHeight : 0,
                        pixelWidth > 0 ? rect.Width / pixelWidth : 0,
                        pixelHeight > 0 ? rect.Height / pixelHeight : 0),
                    // Windows OCR does not expose a per-word confidence score, so report the
                    // absence honestly rather than inventing one.
                    Confidence = double.NaN
                });
            }
        }

        string fullText = result.Text ?? string.Join(
            " ", result.Lines.Select(l => l.Text));

        return new OcrPageResult
        {
            PageNumber = pageNumber,
            FullText = fullText,
            Words = words,
            Language = engine.RecognizerLanguage.LanguageTag,
            // No aggregate confidence is exposed by the platform engine either.
            Confidence = double.NaN,
            UsedOpticalRecognition = true,
            Notes = $"Recognized by {EngineName} at {_renderDpi:0} DPI. " +
                    "The platform engine does not report confidence scores."
        };
    }

    private OcrEngine? CreateEngine(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }

        try
        {
            var lang = new Language(language);
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine != null) return engine;

            // A plain primary subtag such as "en" has no recognizer of its own; resolve it
            // to an installed regional variant like "en-US" before giving up.
            string primary = language.Split('-')[0];
            foreach (var candidate in OcrEngine.AvailableRecognizerLanguages)
            {
                if (candidate.LanguageTag.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.LanguageTag, primary, StringComparison.OrdinalIgnoreCase))
                {
                    engine = OcrEngine.TryCreateFromLanguage(candidate);
                    if (engine != null) return engine;
                }
            }

            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (ArgumentException)
        {
            // Malformed BCP-47 tag.
            return null;
        }
    }

    /// <summary>
    /// Copies the rendered BGRA8 buffer into a SoftwareBitmap the OCR runtime accepts.
    /// The renderer's stride may exceed width*4, so rows are copied individually.
    /// </summary>
    private static SoftwareBitmap CreateSoftwareBitmap(RenderedPage page)
    {
        int width = page.WidthPixels;
        int height = page.HeightPixels;
        int packedStride = width * 4;

        byte[] packed = new byte[packedStride * height];
        ReadOnlySpan<byte> source = page.Pixels.Span;

        for (int y = 0; y < height; y++)
        {
            source.Slice(y * page.Stride, packedStride)
                  .CopyTo(packed.AsSpan(y * packedStride, packedStride));
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(packed.AsBuffer());
        return bitmap;
    }
}
