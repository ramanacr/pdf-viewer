using System.Collections.Concurrent;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfViewer.Core.Cache;
using PdfViewer.Core.Security;
using PdfViewer.Core.Session;

namespace PdfViewer.Core.Rendering;

/// <summary>
/// Priority-driven, deduplicating, cancellation-aware render scheduler for smooth large-document viewing.
/// </summary>
public sealed class RenderPriorityScheduler : IDisposable
{
    private readonly IPdfRenderer _renderer;
    private readonly MultiTierCache _cache;
    private readonly PdfSecurityPolicy _securityPolicy;
    private readonly ConcurrentDictionary<RenderCacheKey, Task<bool>> _inFlightTasks = new();
    private readonly CancellationTokenSource _globalCts = new();
    private volatile bool _isDisposed;

    public MultiTierCache Cache => _cache;

    public PdfSecurityPolicy SecurityPolicy => _securityPolicy;

    public RenderPriorityScheduler(
        IPdfRenderer renderer,
        MultiTierCache? cache = null,
        PdfSecurityPolicy? securityPolicy = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _cache = cache ?? new MultiTierCache();
        _securityPolicy = securityPolicy ?? PdfSecurityPolicy.DefaultStrict;
    }

    /// <summary>
    /// Returns a lease on the rendered page. The caller MUST dispose the lease; the page's
    /// unmanaged buffer stays alive for as long as any lease is outstanding, even if the
    /// cache evicts it in the meantime.
    /// </summary>
    public async Task<MultiTierCache.CachedPageLease> GetOrRenderPageAsync(
        DocumentSession session,
        RenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (session == null || !session.IsOpen || session.Document == null)
            throw new InvalidOperationException("Cannot render with closed or invalid document session.");

        if (_isDisposed)
            throw new ObjectDisposedException(nameof(RenderPriorityScheduler));

        cancellationToken.ThrowIfCancellationRequested();

        // Enforce the render ceiling before any work is scheduled or cached, so hostile page
        // geometry (or an absurd caller-supplied DPI) cannot drive an unbounded allocation.
        EnforceRenderLimits(session, request);

        int dpiBucket = RenderCacheKey.GetDpiBucket(request.Dpi);
        var cacheKey = new RenderCacheKey(
            session.Fingerprint,
            request.PageNumber,
            dpiBucket,
            request.Rotation,
            session.Revision);

        // 1. Check in-memory cache
        if (_cache.TryGet(cacheKey, out var cachedLease) && cachedLease != null)
        {
            return cachedLease;
        }

        // 2. Check or register in-flight deduplicated task.
        //    The shared task is deliberately NOT tied to any one caller's token: cancelling
        //    caller A used to cancel the shared render that caller B was still waiting on.
        //    Each caller instead awaits the shared task through its own token below.
        // The shared task renders and publishes into the cache, then releases its own
        // lease - leaving the cache holding the only reference. Each awaiting caller then
        // takes an independent lease, so no lease is ever disposed twice.
        var task = _inFlightTasks.GetOrAdd(cacheKey, key =>
            Task.Run(async () =>
            {
                var renderToken = _globalCts.Token;
                renderToken.ThrowIfCancellationRequested();
                var rendered = await _renderer.RenderPageAsync(session.Document, request, renderToken);
                using var publishLease = _cache.Put(key, rendered);
                return true;
            }, _globalCts.Token)
        );

        // Cleanup is attached to the STORED task rather than run inside the delegate. The
        // delegate's finally could run before GetOrAdd inserted the task (leaving a
        // completed task cached forever), and never ran at all when Task.Run was handed an
        // already-cancelled token - which permanently poisoned that key so the page could
        // never be rendered again.
        _ = task.ContinueWith(
            _ => _inFlightTasks.TryRemove(cacheKey, out Task<bool>? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Await the shared render under THIS caller's token, so one caller cancelling never
        // fails another caller that still wants the page.
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_cache.TryGet(cacheKey, out var ownLease) && ownLease != null)
        {
            return ownLease;
        }

        // Missed the cache (evicted immediately, or too large to cache at all): render a
        // copy owned solely by this caller.
        var fallback = await _renderer.RenderPageAsync(session.Document, request, cancellationToken).ConfigureAwait(false);
        return _cache.Put(cacheKey, fallback);
    }

    /// <summary>
    /// Applies the security policy's render ceiling to the request, resolving the effective
    /// pixel size the same way the renderer will (explicit target pixels, else DPI scaling).
    /// </summary>
    private void EnforceRenderLimits(DocumentSession session, RenderRequest request)
    {
        int width = request.TargetWidthPixels;
        int height = request.TargetHeightPixels;

        if (width <= 0 || height <= 0)
        {
            var pageInfo = session.Document!.GetPageInfoAsync(request.PageNumber).AsTask().GetAwaiter().GetResult();
            double scale = request.Dpi / 72.0;
            width = (int)Math.Round(pageInfo.WidthPoints * scale);
            height = (int)Math.Round(pageInfo.HeightPoints * scale);
        }

        _securityPolicy.EnsureRenderDimensionsAllowed(width, height);
    }

    public void InvalidateDocument(string fingerprint)
    {
        _cache.InvalidateFingerprint(fingerprint);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _globalCts.Cancel();

        // Wait for in-flight renders to observe cancellation before tearing down the cache
        // and the CTS underneath them. Disposing first let a task complete afterwards and
        // Put into a dead cache (leaking the page), and raced the linked tokens.
        try
        {
            var pending = _inFlightTasks.Values.ToArray();
            if (pending.Length > 0)
            {
                Task.WaitAll(pending, TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // Cancelled/faulted renders are expected here; nothing to report during teardown.
        }

        _cache.Dispose();
        _globalCts.Dispose();
    }
}
