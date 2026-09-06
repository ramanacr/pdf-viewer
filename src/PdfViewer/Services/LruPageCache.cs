using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace PdfViewer.Services;

/// <summary>
/// Thread-safe least-recently-used cache for rendered page bitmaps.
///
/// Bounded by bytes as well as by entry count. Counting entries alone does not bound memory:
/// a rendered page is a few megabytes at 150 DPI and around 33 MB at the 300 DPI this
/// application switches to past 2x zoom, so a sixty-entry cache could hold anything from
/// 60 MB to about 2 GB depending only on how far the user had zoomed in. The byte ceiling is
/// what actually keeps that in hand.
/// </summary>
public class LruPageCache
{
    /// <summary>
    /// How much rendered-page memory the application will hold across all open documents.
    /// Generous enough that ordinary reading never evicts, small enough to leave the machine
    /// usable. Split between documents as they are opened.
    /// </summary>
    public const long DefaultTotalByteCeiling = 384L * 1024 * 1024;

    /// <summary>
    /// No document's share of the shared budget drops below this, however many are open.
    /// Applied by <see cref="ShareOfTotal"/> when the budget is divided - not by the
    /// constructor, which honours exactly what it is given.
    /// </summary>
    public const long MinimumByteCeiling = 48L * 1024 * 1024;

    /// <summary>
    /// One document's share when <paramref name="documentCount"/> are open. Below the floor
    /// the cache stops being useful and every scroll re-renders, so the total is allowed to
    /// drift above <see cref="DefaultTotalByteCeiling"/> rather than starving each document.
    /// </summary>
    public static long ShareOfTotal(int documentCount, long total = DefaultTotalByteCeiling) =>
        Math.Max(MinimumByteCeiling, total / Math.Max(1, documentCount));

    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache = new();
    private readonly LinkedList<CacheItem> _lruList = new();
    private readonly object _lock = new();

    private long _byteCeiling;
    private long _currentBytes;

    private record CacheItem(string Key, BitmapSource Image, long Bytes);

    public LruPageCache(int capacity = 50, long byteCeiling = DefaultTotalByteCeiling)
    {
        _capacity = Math.Max(1, capacity);

        // Honour exactly what the caller asked for. Silently raising it to a floor made the
        // cache hold more than a test - or a caller dividing a budget - had asked it to.
        _byteCeiling = Math.Max(1, byteCeiling);
    }

    /// <summary>Bytes currently held.</summary>
    public long CurrentBytes
    {
        get { lock (_lock) { return _currentBytes; } }
    }

    public long ByteCeiling
    {
        get { lock (_lock) { return _byteCeiling; } }
    }

    public int Count
    {
        get { lock (_lock) { return _cache.Count; } }
    }

    /// <summary>
    /// Changes the ceiling and evicts down to it immediately. Used when the number of open
    /// documents changes and the shared budget is re-divided.
    /// </summary>
    public void SetByteCeiling(long byteCeiling)
    {
        lock (_lock)
        {
            _byteCeiling = Math.Max(1, byteCeiling);

            // Only the byte limit is being changed; the entry count is still whatever the
            // caller asked for at construction.
            EvictToFit(incoming: 0, enforceCountLimit: false);
        }
    }

    public static string CreateKey(int pageNumber, int dpi, int rotation) =>
        $"{pageNumber}_{dpi}_{rotation}";

    /// <summary>Bytes a decoded bitmap occupies, from its own dimensions and pixel format.</summary>
    public static long MeasureBytes(BitmapSource image)
    {
        if (image == null) return 0;

        try
        {
            long bytesPerPixel = Math.Max(1, (image.Format.BitsPerPixel + 7) / 8);
            return (long)image.PixelWidth * image.PixelHeight * bytesPerPixel;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return 0;
        }
    }

    public bool TryGet(int pageNumber, int dpi, int rotation, out BitmapSource? image)
    {
        string key = CreateKey(pageNumber, dpi, rotation);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    public void Add(int pageNumber, int dpi, int rotation, BitmapSource image)
    {
        if (image == null) return;

        string key = CreateKey(pageNumber, dpi, rotation);
        long bytes = MeasureBytes(image);

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
                _cache.Remove(key);
                _currentBytes -= existingNode.Value.Bytes;
            }

            // A single page bigger than the whole budget would evict everything and then not
            // fit anyway. It is handed back to the caller and simply not retained.
            if (bytes > _byteCeiling) return;

            EvictToFit(bytes, enforceCountLimit: true);

            var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, image, bytes));
            _lruList.AddFirst(newNode);
            _cache[key] = newNode;
            _currentBytes += bytes;
        }
    }

    /// <summary>
    /// Drops least-recently-used entries until the cache can take <paramref name="incoming"/>
    /// more bytes without exceeding its limits. Caller must hold the lock.
    /// </summary>
    private void EvictToFit(long incoming, bool enforceCountLimit)
    {
        while (_lruList.Last != null)
        {
            bool overCount = enforceCountLimit && _cache.Count >= _capacity;
            bool overBytes = _currentBytes + incoming > _byteCeiling;
            if (!overCount && !overBytes) break;

            var last = _lruList.Last;
            _lruList.RemoveLast();
            _cache.Remove(last.Value.Key);
            _currentBytes -= last.Value.Bytes;
        }

        // The list is the source of truth; if it emptied, so did the byte total.
        if (_cache.Count == 0) _currentBytes = 0;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
            _currentBytes = 0;
        }
    }
}
