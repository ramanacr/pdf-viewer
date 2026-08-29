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

    public bool TryGet(RenderCacheKey key, out RenderedPage? page)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                Interlocked.Increment(ref _hitCount);
                page = node.Value.Page;
                return true;
            }

            Interlocked.Increment(ref _missCount);
            page = null;
            return false;
        }
    }

    public void Put(RenderCacheKey key, RenderedPage page)
    {
        if (page == null) return;

        lock (_lock)
        {
            long entryBytes = page.ByteLength;

            if (_cache.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
                _currentMemoryBytes -= existingNode.Value.Page.ByteLength;
                existingNode.Value.Page.Dispose();
                _cache.Remove(key);
            }

            // Evict until within budget
            while (_currentMemoryBytes + entryBytes > _maxMemoryBytes && _lruList.Count > 0)
            {
                var oldest = _lruList.Last;
                if (oldest == null) break;

                _lruList.RemoveLast();
                _cache.Remove(oldest.Value.Key);
                _currentMemoryBytes -= oldest.Value.Page.ByteLength;
                oldest.Value.Page.Dispose();
                Interlocked.Increment(ref _evictionCount);
            }

            var entry = new CacheEntry(key, page);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lruList.AddFirst(newNode);
            _cache[key] = newNode;
            _currentMemoryBytes += entryBytes;
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
                kv.Value.Value.Page.Dispose();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var node in _lruList)
            {
                node.Page.Dispose();
            }
            _lruList.Clear();
            _cache.Clear();
            _currentMemoryBytes = 0;
        }
    }

    public void Dispose()
    {
        Clear();
    }

    private sealed record CacheEntry(RenderCacheKey Key, RenderedPage Page);
}
