using System.Collections.Concurrent;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfViewer.Core.Cache;
using PdfViewer.Core.Session;

namespace PdfViewer.Core.Rendering;

/// <summary>
/// Priority-driven, deduplicating, cancellation-aware render scheduler for smooth large-document viewing.
/// </summary>
public sealed class RenderPriorityScheduler : IDisposable
{
    private readonly IPdfRenderer _renderer;
    private readonly MultiTierCache _cache;
    private readonly ConcurrentDictionary<RenderCacheKey, Task<RenderedPage>> _inFlightTasks = new();
    private readonly CancellationTokenSource _globalCts = new();
    private bool _isDisposed;

    public MultiTierCache Cache => _cache;

    public RenderPriorityScheduler(IPdfRenderer renderer, MultiTierCache? cache = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _cache = cache ?? new MultiTierCache();
    }

    public async Task<RenderedPage> GetOrRenderPageAsync(
        DocumentSession session,
        RenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (session == null || !session.IsOpen || session.Document == null)
            throw new InvalidOperationException("Cannot render with closed or invalid document session.");

        if (_isDisposed)
            throw new ObjectDisposedException(nameof(RenderPriorityScheduler));

        int dpiBucket = RenderCacheKey.GetDpiBucket(request.Dpi);
        var cacheKey = new RenderCacheKey(
            session.Fingerprint,
            request.PageNumber,
            dpiBucket,
            request.Rotation,
            session.Revision);

        // 1. Check in-memory cache
        if (_cache.TryGet(cacheKey, out var cachedPage) && cachedPage != null)
        {
            return cachedPage;
        }

        // 2. Check or register in-flight deduplicated task
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token, cancellationToken);
        var inFlightToken = linkedCts.Token;

        var task = _inFlightTasks.GetOrAdd(cacheKey, key =>
            Task.Run(async () =>
            {
                try
                {
                    inFlightToken.ThrowIfCancellationRequested();
                    var rendered = await _renderer.RenderPageAsync(session.Document, request, inFlightToken);
                    _cache.Put(key, rendered);
                    return rendered;
                }
                finally
                {
                    _inFlightTasks.TryRemove(key, out _);
                }
            }, inFlightToken)
        );

        return await task;
    }

    public void InvalidateDocument(string fingerprint)
    {
        _cache.InvalidateFingerprint(fingerprint);
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _globalCts.Cancel();
            _globalCts.Dispose();
            _cache.Dispose();
        }
    }
}
