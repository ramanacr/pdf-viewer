using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector;

namespace PdfViewer.Services;

/// <summary>
/// Supplies PDFium pixels for vector fallback regions (06_PDFIUM_FALLBACK "object/region fallback").
/// </summary>
/// <remarks>
/// Lives at the composition root so PdfEngine.Vector never references PDFium (ADR-007). Each token's
/// bounds are rendered on their own (a pixel window of the page in its unrotated crop-box
/// orientation), so memory is bounded by the region, not the page, at any zoom.
/// </remarks>
internal sealed class PdfiumRegionFallbackProvider : IPdfFallbackProvider
{
    private readonly PdfiumDocumentService _pdfium;
    private readonly Func<int, (PdfRect CropBox, int Rotation)?> _pageGeometry;
    private readonly object _sync = new();

    public PdfiumRegionFallbackProvider(PdfiumDocumentService pdfium, Func<int, (PdfRect CropBox, int Rotation)?> pageGeometry)
    {
        _pdfium = pdfium;
        _pageGeometry = pageGeometry;
    }

    /// <summary>Number of PDFium region rasterizations performed (tests / diagnostics).</summary>
    public int PageRasterizations { get; private set; }

    /// <summary>Largest fallback crop, per side; beyond it the region is rendered at reduced resolution.</summary>
    internal const int MaxRegionPixels = 4096;

    public ValueTask<RenderedPage?> RenderFallbackRegionAsync(PdfFallbackToken token, double dpi, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pageGeometry(token.PageNumber) is not (PdfRect crop, int intrinsicRotation))
            return ValueTask.FromResult<RenderedPage?>(null);

        // Bound memory at high zoom: a region larger than MaxRegionPixels is rendered coarser and
        // stretched into place (localized raster, never a full-page raster).
        var b = token.Bounds;
        double longest = Math.Max(b.Width, b.Height) * dpi / 72.0;
        double effectiveDpi = longest > MaxRegionPixels ? dpi * MaxRegionPixels / longest : dpi;
        double s = effectiveDpi / 72.0;

        int x0 = (int)Math.Floor((b.X - crop.X) * s);
        int y0 = (int)Math.Floor((crop.Y + crop.Height - (b.Y + b.Height)) * s);
        int x1 = (int)Math.Ceiling((b.X + b.Width - crop.X) * s);
        int y1 = (int)Math.Ceiling((crop.Y + crop.Height - b.Y) * s);
        int fullW = (int)Math.Round(crop.Width * s), fullH = (int)Math.Round(crop.Height * s);
        x0 = Math.Clamp(x0, 0, fullW); x1 = Math.Clamp(x1, 0, fullW);
        y0 = Math.Clamp(y0, 0, fullH); y1 = Math.Clamp(y1, 0, fullH);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0)
            return ValueTask.FromResult<RenderedPage?>(null);

        // Undo the page's /Rotate so pixels line up with unrotated crop-box coordinates.
        int undo = (360 - intrinsicRotation) % 360;
        var region = _pdfium.RenderPageRegion(token.PageNumber, effectiveDpi, undo, x0, y0, w, h);
        if (region == null)
            return ValueTask.FromResult<RenderedPage?>(null);
        lock (_sync) PageRasterizations++;

        int stride = w * 4;
        var owner = new PooledOwner(stride * h);
        region.CopyPixels(new Int32Rect(0, 0, w, h), owner.Array, stride, 0);
        return ValueTask.FromResult<RenderedPage?>(new RenderedPage(token.PageNumber, w, h, stride, effectiveDpi, PageRotation.Rotate0, owner));
    }

    public void Reset()
    {
        lock (_sync)
        {
            PageRasterizations = 0;
        }
    }

    private sealed class PooledOwner : IMemoryOwner<byte>
    {
        private readonly int _length;
        public byte[] Array { get; private set; }

        public PooledOwner(int length)
        {
            _length = length;
            Array = ArrayPool<byte>.Shared.Rent(length);
        }

        public Memory<byte> Memory => new(Array, 0, _length);

        public void Dispose()
        {
            if (Array.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Array);
                Array = System.Array.Empty<byte>();
            }
        }
    }
}
