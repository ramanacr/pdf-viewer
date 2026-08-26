using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace PdfViewer.Services;

/// <summary>
/// Thread-safe Least-Recently-Used (LRU) memory cache for rendered page bitmaps.
/// Prevents excessive memory growth when browsing large documents.
/// </summary>
public class LruPageCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache = new();
    private readonly LinkedList<CacheItem> _lruList = new();
    private readonly object _lock = new();

    private record CacheItem(string Key, BitmapSource Image);

    public LruPageCache(int capacity = 50)
    {
        _capacity = Math.Max(1, capacity);
    }

    public static string CreateKey(int pageNumber, int dpi, int rotation) =>
        $"{pageNumber}_{dpi}_{rotation}";

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
        string key = CreateKey(pageNumber, dpi, rotation);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
            }
            else if (_cache.Count >= _capacity)
            {
                var last = _lruList.Last;
                if (last != null)
                {
                    _lruList.RemoveLast();
                    _cache.Remove(last.Value.Key);
                }
            }

            var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, image));
            _lruList.AddFirst(newNode);
            _cache[key] = newNode;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
    }
}
