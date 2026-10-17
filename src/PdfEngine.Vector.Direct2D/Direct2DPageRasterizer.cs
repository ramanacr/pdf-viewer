using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PdfA;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Draws pages for transparency flattening with the vector engine's Direct2D renderer. A page the
/// engine cannot draw exactly (it reports fallback regions) is drawn by <c>fallback</c> when one
/// is given (tests pass PDFium, which this project does not reference); otherwise it is drawn by
/// the engine's best effort when <c>bestEffort</c> is set (the viewer: the same pixels the screen
/// shows, never placeholders), and refused when it is not.
/// </summary>
public sealed class Direct2DPageRasterizer : IPdfPageRasterizer, IDisposable
{
    private readonly Direct2DVectorRenderer _renderer;
    private readonly bool _ownsRenderer;
    private readonly Func<byte[], int, int, int, PdfPixelWindow, PdfRasterImage?>? _fallback;
    private readonly bool _bestEffort;

    /// <param name="fallback">Draws a window of a page (number) of a file drawn at width × height pixels, for pages with fallback regions.</param>
    /// <param name="bestEffort">Without <paramref name="fallback"/>, draw pages with fallback regions as the engine draws them instead of refusing them.</param>
    public Direct2DPageRasterizer(Func<byte[], int, int, int, PdfPixelWindow, PdfRasterImage?>? fallback = null, Direct2DVectorRenderer? renderer = null,
        bool bestEffort = false)
    {
        _fallback = fallback;
        _bestEffort = bestEffort;
        _ownsRenderer = renderer == null;
        _renderer = renderer ?? new Direct2DVectorRenderer();
    }

    public PdfRasterImage? Render(PdfVectorDocument document, byte[] bytes, int pageNumber, int pageWidthPixels, int pageHeightPixels, PdfPixelWindow window)
    {
        // The converter runs off the UI thread; the renderer's own work is on the thread pool.
        return Task.Run(async () =>
        {
            var list = await document.GetPageDisplayListAsync(pageNumber).ConfigureAwait(false);
            var tokens = await _renderer.AnalyzeAsync(list).ConfigureAwait(false);
            if (tokens.Count > 0 && (_fallback != null || !_bestEffort))
                return _fallback?.Invoke(bytes, pageNumber, pageWidthPixels, pageHeightPixels, window);
            var crop = list.CropBox.IsEmpty ? new Geometry.PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
            var request = new RenderRequest
            {
                PageNumber = pageNumber,
                Dpi = crop.Width > 0 ? pageWidthPixels * 72.0 / crop.Width : 72,
                TargetWidthPixels = pageWidthPixels,
                TargetHeightPixels = pageHeightPixels,
                RenderAnnotations = false,
                RenderForms = false,
            };
            var result = await _renderer.RenderAsync(list, request, null, new PixelRegion(window.X, window.Y, window.Width, window.Height),
                invertColors: false, CancellationToken.None).ConfigureAwait(false);
            using var page = result.Page;
            if (page.WidthPixels != window.Width || page.HeightPixels != window.Height) return null;
            return new PdfRasterImage(window.Width, window.Height, Crop(page, 0, 0, window.Width, window.Height), "vector engine (Direct2D)");
        }).GetAwaiter().GetResult();
    }

    /// <summary>A window of rendered pixels as tight BGRA rows.</summary>
    public static byte[] Crop(RenderedPage page, int x, int y, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var source = page.Pixels.Span;
        for (int row = 0; row < height; row++)
            source.Slice((y + row) * page.Stride + x * 4, width * 4).CopyTo(bgra.AsSpan(row * width * 4));
        return bgra;
    }

    public void Dispose()
    {
        if (_ownsRenderer) _renderer.Dispose();
    }
}
