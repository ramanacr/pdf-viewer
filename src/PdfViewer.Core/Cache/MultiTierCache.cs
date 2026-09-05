using System.Collections.Concurrent;
using PdfEngine.Rendering;

namespace PdfViewer.Core.Cache;

/// <summary>
/// Cache key representing a deterministic rendering output state.
/// </summary>
public readonly record struct RenderCacheKey(
    string Fingerprint,
    int PageNumber,
    int DpiBucket,
    PageRotation Rotation,
    long Revision)
{
    public static int GetDpiBucket(double rawDpi) => rawDpi switch
    {
        <= 72 => 72,
        <= 96 => 96,
        <= 120 => 120,
        <= 150 => 150,
        <= 180 => 180,
        <= 220 => 220,
        <= 300 => 300,
        _ => (int)(Math.Round(rawDpi / 50.0) * 50)
    };
}

/// <summary>
/// Memory-budgeted multi-tier cache managing rendered bitmap memory, thumbnails, and cache metrics.
/// </summary>
public sealed class MultiTierCache : IDisposable
{
    private readonly long _maxMemoryBytes;
    private long _currentMemoryBytes;
    private readonly object _lock = new();
    private bool _isDisposed;

    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly Dictionary<RenderCacheKey, LinkedListNode<CacheEntry>> _cache = new();

    // Cache Metrics
    private long _hitCount;
    private long _missCount;
    private long _evictionCount;

    public long MaxMemoryBytes => _maxMemoryBytes;
    public long CurrentMemoryBytes => Interlocked.Read(ref _currentMemoryBytes);
    public long HitCount => Interlocked.Read(ref _hitCount);
    public long MissCount => Interlocked.Read(ref _missCount);
    public long EvictionCount => Interlocked.Read(ref _evictionCount);
    public int Count
    {
        get
        {
            lock (_lock) return _cache.Count;
        }
    }

    public MultiTierCache(long maxMemoryBytes = 256 * 1024 * 1024) // 256 MB default budget
    {
        _maxMemoryBytes = Math.Max(1024, maxMemoryBytes);
    }

    /// <summary>
    /// Borrows a cached page. The returned lease keeps the underlying RenderedPage alive
    /// even if the cache evicts it in the meantime; the page's unmanaged pixel buffer is
    /// released only once the cache AND every outstanding lease are done with it.
    /// Callers MUST dispose the lease.
    /// </summary>
    public bool TryGet(RenderCacheKey key, out CachedPageLease? lease)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                Interlocked.Increment(ref _hitCount);
                lease = node.Value.Acquire();
                return true;
            }

            Interlocked.Increment(ref _missCount);
            lease = null;
            return false;
        }
    }

    /// <summary>
    /// Stores a page and returns a lease for the caller's own continued use.
    /// Ownership of <paramref name="page"/> transfers to the cache/lease pair - the caller
    /// must use the returned lease and not the raw page reference afterwards.
    /// </summary>
    public CachedPageLease Put(RenderCacheKey key, RenderedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        lock (_lock)
        {
            // A render that completes after the cache was disposed must not be stored into
            // a dead cache (it would never be released). Hand ownership to the lease instead.
            if (_isDisposed)
            {
                return CacheEntry.CreateUncached(key, page).Acquire();
            }

            long entryBytes = page.ByteLength;

            if (_cache.TryGetValue(key, out var existingNode))
            {
                // Re-putting the very same instance must not dispose it and then store the
                // corpse; just refresh LRU position and hand back a new lease.
                if (ReferenceEquals(existingNode.Value.Page, page))
                {
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                    return existingNode.Value.Acquire();
                }

                _lruList.Remove(existingNode);
                _currentMemoryBytes -= existingNode.Value.Page.ByteLength;
                existingNode.Value.ReleaseCacheReference();
                _cache.Remove(key);
            }

            // An entry larger than the entire budget can never fit, and the old loop would
            // therefore evict and dispose EVERY other entry and then store it anyway,
            // leaving the cache permanently over budget with a 0% hit rate. Don't cache it:
            // hand back a standalone lease that owns the page on its own.
            if (entryBytes > _maxMemoryBytes)
            {
                return CacheEntry.CreateUncached(key, page).Acquire();
            }

            // Evict until within budget
            while (_currentMemoryBytes + entryBytes > _maxMemoryBytes && _lruList.Count > 0)
            {
                var oldest = _lruList.Last;
                if (oldest == null) break;

                _lruList.RemoveLast();
                _cache.Remove(oldest.Value.Key);
                _currentMemoryBytes -= oldest.Value.Page.ByteLength;
                // Drops the CACHE's reference only. If a caller still holds a lease, the
                // page stays alive until they dispose it - this is what previously caused
                // reads of freed unmanaged pixel memory.
                oldest.Value.ReleaseCacheReference();
                Interlocked.Increment(ref _evictionCount);
            }

            var entry = new CacheEntry(key, page);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lruList.AddFirst(newNode);
            _cache[key] = newNode;
            _currentMemoryBytes += entryBytes;
            return entry.Acquire();
        }
    }

    public void InvalidateFingerprint(string fingerprint)
    {
        lock (_lock)
        {
            var nodesToRemove = _cache.Where(kv => kv.Key.Fingerprint == fingerprint).ToList();
            foreach (var kv in nodesToRemove)
            {
                _lruList.Remove(kv.Value);
                _cache.Remove(kv.Key);
                _currentMemoryBytes -= kv.Value.Value.Page.ByteLength;
                kv.Value.Value.ReleaseCacheReference();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var node in _lruList)
            {
                node.ReleaseCacheReference();
            }
            _lruList.Clear();
            _cache.Clear();
            _currentMemoryBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
        }
        Clear();
    }

    /// <summary>
    /// A borrowed reference to a cached page. Disposing releases the borrow; the page is
    /// destroyed only when the cache and all leases have released it.
    /// </summary>
    public sealed class CachedPageLease : IDisposable
    {
        private CacheEntry? _entry;
        public RenderedPage Page { get; }

        internal CachedPageLease(CacheEntry entry)
        {
            _entry = entry;
            Page = entry.Page;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            entry?.ReleaseLease();
        }
    }

    internal sealed class CacheEntry
    {
        public RenderCacheKey Key { get; }
        public RenderedPage Page { get; }

        // Starts at 1 for the cache's own reference (0 for an uncached, lease-only entry).
        private int _refCount;

        public CacheEntry(RenderCacheKey key, RenderedPage page)
        {
            Key = key;
            Page = page;
            _refCount = 1;
        }

        private CacheEntry(RenderCacheKey key, RenderedPage page, int initialRefCount)
        {
            Key = key;
            Page = page;
            _refCount = initialRefCount;
        }

        public static CacheEntry CreateUncached(RenderCacheKey key, RenderedPage page)
            => new(key, page, 0);

        public CachedPageLease Acquire()
        {
            Interlocked.Increment(ref _refCount);
            return new CachedPageLease(this);
        }

        public void ReleaseCacheReference() => Release();
        public void ReleaseLease() => Release();

        private void Release()
        {
            if (Interlocked.Decrement(ref _refCount) <= 0)
            {
                Page.Dispose();
            }
        }
    }
}
