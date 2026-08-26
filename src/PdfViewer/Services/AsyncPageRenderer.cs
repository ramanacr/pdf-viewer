using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PdfViewer.Services;

/// <summary>
/// Handles cached background page rendering requests with cancellation support.
/// </summary>
public class AsyncPageRenderer
{
    private readonly PdfDocumentService _documentService;
    private readonly LruPageCache _cache;

    public AsyncPageRenderer(PdfDocumentService documentService, LruPageCache cache)
    {
        _documentService = documentService;
        _cache = cache;
    }

    public async Task<BitmapSource?> GetOrRenderPageAsync(int pageNumber, int dpi, int rotationAngle, CancellationToken ct = default)
    {
        if (_cache.TryGet(pageNumber, dpi, rotationAngle, out var cachedImage) && cachedImage != null)
        {
            return cachedImage;
        }

        var rendered = await _documentService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct);
        if (rendered != null && !ct.IsCancellationRequested)
        {
            _cache.Add(pageNumber, dpi, rotationAngle, rendered);
        }

        return rendered;
    }

    public void ClearCache()
    {
        _cache.Clear();
    }
}
