using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;

namespace PdfViewer.Services;

/// <summary>
/// <see cref="IPdfRenderer"/> over the vector engine, for the components that take a renderer
/// (optical recognition): a page is drawn by Direct2D from its display list, with no fallback
/// provider. A vector document is drawn directly; any other document (one PDFium opened for its
/// text) is drawn from the file it was opened from.
/// </summary>
public sealed class VectorPdfRenderer : IPdfRenderer, IDisposable
{
    /// <summary>One instance for the whole viewer (its Direct2D device is created on first use).</summary>
    public static VectorPdfRenderer Shared { get; } = new();

    private readonly Lazy<Direct2DVectorRenderer> _renderer = new(() => new Direct2DVectorRenderer(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Path, DateTime Written, PdfVectorDocument Document)? _opened;

    public async ValueTask<RenderedPage> RenderPageAsync(IPdfDocument document, RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is IPdfVectorDocument vector)
            return await RenderAsync(vector, request, cancellationToken).ConfigureAwait(false);

        string path = document.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new NotSupportedException("The page can be drawn only from a vector document or from the file its document was opened from.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (_opened is not { } open || open.Path != path || open.Written != written)
            {
                _opened?.Document.Dispose();
                _opened = null;
                _opened = (path, written, await PdfVectorDocument.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            return await RenderAsync(_opened.Value.Document, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RenderedPage> RenderAsync(IPdfVectorDocument document, RenderRequest request, CancellationToken ct)
    {
        var list = await document.GetPageDisplayListAsync(request.PageNumber, ct).ConfigureAwait(false);
        var result = await _renderer.Value.RenderAsync(list, request, null, ct).ConfigureAwait(false);
        return result.Page;
    }

    public void Dispose()
    {
        _opened?.Document.Dispose();
        _opened = null;
        if (_renderer.IsValueCreated) _renderer.Value.Dispose();
        _gate.Dispose();
    }
}
