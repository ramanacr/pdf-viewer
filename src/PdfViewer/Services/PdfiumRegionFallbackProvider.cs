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
/// Lives at the composition root so PdfEngine.Vector never references PDFium (ADR-007). The page is
/// rasterized once per (page, dpi) in its unrotated crop-box orientation and each token's bounds are
/// cropped from it; the vector renderer composites the crops over the vector output.
/// </remarks>
internal sealed class PdfiumRegionFallbackProvider : IPdfFallbackProvider
{
    private readonly PdfiumDocumentService _pdfium;
    private readonly Func<int, (PdfRect CropBox, int Rotation)?> _pageGeometry;
    private readonly object _sync = new();
    private (int Page, double Dpi, BitmapSource Bitmap)? _last;

    public PdfiumRegionFallbackProvider(PdfiumDocumentService pdfium, Func<int, (PdfRect CropBox, int Rotation)?> pageGeometry)
    {
        _pdfium = pdfium;
        _pageGeometry = pageGeometry;
    }

    /// <summary>Number of full-page PDFium rasterizations performed (tests / diagnostics).</summary>
    public int PageRasterizations { get; private set; }

    public ValueTask<RenderedPage?> RenderFallbackRegionAsync(PdfFallbackToken token, double dpi, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pageGeometry(token.PageNumber) is not (PdfRect crop, int intrinsicRotation))
            return ValueTask.FromResult<RenderedPage?>(null);

        BitmapSource? page;
        lock (_sync)
        {
            if (_last is { } last && last.Page == token.PageNumber && Math.Abs(last.Dpi - dpi) < 0.01)
            {
                page = last.Bitmap;
            }
            else
            {
                // Undo the page's /Rotate so pixels line up with unrotated crop-box coordinates.
                int undo = (360 - intrinsicRotation) % 360;
                page = _pdfium.RenderPage(token.PageNumber, (int)Math.Round(dpi), undo);
                if (page == null)
                    return ValueTask.FromResult<RenderedPage?>(null);
                PageRasterizations++;
                _last = (token.PageNumber, dpi, page);
            }
        }

        double sx = page.PixelWidth / crop.Width;
        double sy = page.PixelHeight / crop.Height;
        var b = token.Bounds;
        int x0 = (int)Math.Floor((b.X - crop.X) * sx);
        int y0 = (int)Math.Floor((crop.Y + crop.Height - (b.Y + b.Height)) * sy);
        int x1 = (int)Math.Ceiling((b.X + b.Width - crop.X) * sx);
        int y1 = (int)Math.Ceiling((crop.Y + crop.Height - b.Y) * sy);
        x0 = Math.Clamp(x0, 0, page.PixelWidth);
        y0 = Math.Clamp(y0, 0, page.PixelHeight);
        x1 = Math.Clamp(x1, 0, page.PixelWidth);
        y1 = Math.Clamp(y1, 0, page.PixelHeight);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0)
            return ValueTask.FromResult<RenderedPage?>(null);

        int stride = w * 4;
        var owner = new PooledOwner(stride * h);
        page.CopyPixels(new Int32Rect(x0, y0, w, h), owner.Array, stride, 0);
        return ValueTask.FromResult<RenderedPage?>(new RenderedPage(token.PageNumber, w, h, stride, dpi, PageRotation.Rotate0, owner));
    }

    public void Reset()
    {
        lock (_sync)
        {
            _last = null;
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
