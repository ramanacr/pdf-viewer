using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.PrintProduction;
using PdfEngine.Vector.Skia;

namespace PdfViewer.Android;

/// <summary>
/// Draws display lists for print production (separation plates for Output Preview, total ink for
/// preflight) with the engine's Skia renderer, as the Windows app does with its Direct2D one
/// (<c>Direct2DDisplayListRasterizer</c>): the page's crop box filling exactly the size asked for, its
/// /Rotate applied, over white, as tight BGRA rows. There is no fallback renderer: areas the engine
/// cannot draw exactly are drawn by its best effort.
/// </summary>
internal sealed class SkiaDisplayListRasterizer : IPdfDisplayListRasterizer, IDisposable
{
    /// <summary>A plate is drawn many times (once per plate, page after page): a small image cache is enough.</summary>
    private const long ImageCacheBytes = 32L * 1024 * 1024;

    private readonly SkiaVectorRenderer _renderer = new(ImageCacheBytes);

    public async Task<byte[]> RenderAsync(IPdfDisplayList list, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        bool sideways = ((list.RotationDegrees % 360) + 360) % 360 is 90 or 270;
        double widthPoints = sideways ? crop.Height : crop.Width;
        var request = new RenderRequest
        {
            PageNumber = list.PageNumber,
            Dpi = widthPoints > 0 ? width * 72.0 / widthPoints : 72,
            TargetWidthPixels = width,
            TargetHeightPixels = height,
        };
        var result = await _renderer.RenderAsync(list, request, null, null, invertColors: false, cancellationToken).ConfigureAwait(false);
        using var page = result.Page;
        // The renderer's rows are already tight (stride = width × 4) at exactly the size asked for; copied otherwise.
        var bgra = new byte[width * height * 4];
        var source = page.Pixels.Span;
        int rows = Math.Min(height, page.HeightPixels), rowBytes = Math.Min(width, page.WidthPixels) * 4;
        for (int y = 0; y < rows; y++)
            source.Slice(y * page.Stride, rowBytes).CopyTo(bgra.AsSpan(y * width * 4));
        return bgra;
    }

    public void Dispose() => _renderer.Dispose();
}
