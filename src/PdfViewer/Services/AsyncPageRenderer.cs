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
    private readonly IPdfDocumentService _documentService;
    private readonly LruPageCache _cache;

    public AsyncPageRenderer(IPdfDocumentService documentService, LruPageCache cache)
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

    /// <summary>
    /// Live vector surface for the page, or null when it must be shown as a bitmap. Surfaces are
    /// zoom-independent, so they are cached by the document service per page and rotation only.
    /// </summary>
    public Task<System.Windows.Media.ImageSource?> GetVectorSurfaceAsync(int pageNumber, int rotationAngle, CancellationToken ct = default, bool nightMode = false) =>
        _documentService.GetVectorPageSurfaceAsync(pageNumber, rotationAngle, ct, nightMode);

    public void ClearCache()
    {
        _cache.Clear();
    }
}
