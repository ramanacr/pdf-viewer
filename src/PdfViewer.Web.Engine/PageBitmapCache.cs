namespace PdfViewer.Web.Engine;

/// <summary>
/// Rendered pages kept for scrolling back, least recently used first out, within a byte budget.
/// A bitmap is keyed by page, scale (to a thousandth) and night mode. One bitmap larger than the
/// whole budget is still kept, alone, so the page in view never has to be rendered twice.
/// </summary>
/// <remarks>Not thread-safe: one viewer uses it from its UI thread.</remarks>
public sealed class PageBitmapCache
{
    private readonly record struct Key(int Page, long Scale, bool Night);

    private readonly LinkedList<(Key Key, ViewerBitmap Bitmap)> _lru = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, ViewerBitmap Bitmap)>> _map = new();

    public PageBitmapCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
    public long Bytes { get; private set; }
    public int Count => _map.Count;

    private static Key KeyOf(int page, double scale, bool night) => new(page, (long)Math.Round(scale * 1000), night);

    public bool TryGet(int page, double scale, bool night, out ViewerBitmap bitmap)
    {
        if (_map.TryGetValue(KeyOf(page, scale, night), out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }
        bitmap = null!;
        return false;
    }

    /// <summary>Keeps <paramref name="bitmap"/> as the page at <paramref name="scale"/> (the scale asked for, which a capped render may not reach).</summary>
    public void Add(int page, double scale, bool night, ViewerBitmap bitmap)
    {
        var key = KeyOf(page, scale, night);
        if (_map.Remove(key, out var old))
        {
            _lru.Remove(old);
            Bytes -= old.Value.Bitmap.ByteLength;
        }
        _map[key] = _lru.AddFirst((key, bitmap));
        Bytes += bitmap.ByteLength;
        while (Bytes > MaxBytes && _lru.Count > 1)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            Bytes -= last.Value.Bitmap.ByteLength;
        }
    }

    /// <summary>Drops every bitmap (another document was opened).</summary>
    public void Clear()
    {
        _lru.Clear();
        _map.Clear();
        Bytes = 0;
    }
}
