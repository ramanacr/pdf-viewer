namespace PdfViewer.Web.Engine;

/// <summary>
/// Page images a host made from rendered pages (an Android <c>SKImage</c>, for example), kept
/// least recently used first out within a byte budget and disposed when dropped. Each page keeps
/// only its newest image per night mode: an older zoom's image is a placeholder only until the new
/// one arrives, so it is replaced, not kept beside it. One image larger than the whole budget is
/// still kept, alone, so the page in view never has to be rendered twice.
/// </summary>
/// <remarks>Not thread-safe: one viewer uses it from its UI thread.</remarks>
public sealed class PageImageCache<TImage> : IDisposable where TImage : class, IDisposable
{
    private readonly record struct Key(int Page, bool Night);

    private sealed record Entry(Key Key, TImage Image, double Scale, long Bytes);

    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _map = new();

    public PageImageCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
    public long Bytes { get; private set; }
    public int Count => _map.Count;

    /// <summary>The page's newest image and the scale (pixels per point) it was rendered at.</summary>
    public bool TryGet(int page, bool night, out TImage image, out double scale)
    {
        if (_map.TryGetValue(new Key(page, night), out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            (image, scale) = (node.Value.Image, node.Value.Scale);
            return true;
        }
        (image, scale) = (null!, 0);
        return false;
    }

    /// <summary>True when the page has an image at <paramref name="scale"/> (to a thousandth).</summary>
    public bool Has(int page, bool night, double scale) =>
        _map.TryGetValue(new Key(page, night), out var node) && SameScale(node.Value.Scale, scale);

    public static bool SameScale(double a, double b) => Math.Round(a * 1000) == Math.Round(b * 1000);

    /// <summary>Keeps <paramref name="image"/> as the page's image; the page's previous one and the least recently used beyond the budget are disposed.</summary>
    public void Add(int page, bool night, double scale, TImage image, long bytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        var key = new Key(page, night);
        if (_map.Remove(key, out var old))
            Drop(old, dispose: !ReferenceEquals(old.Value.Image, image));
        _map[key] = _lru.AddFirst(new Entry(key, image, scale, bytes));
        Bytes += bytes;
        while (Bytes > MaxBytes && _lru.Count > 1)
        {
            var last = _lru.Last!;
            _map.Remove(last.Value.Key);
            Drop(last, dispose: true);
        }
    }

    private void Drop(LinkedListNode<Entry> node, bool dispose)
    {
        _lru.Remove(node);
        Bytes -= node.Value.Bytes;
        if (dispose) node.Value.Image.Dispose();
    }

    /// <summary>Disposes every image (another document was opened).</summary>
    public void Clear()
    {
        foreach (var entry in _lru) entry.Image.Dispose();
        _lru.Clear();
        _map.Clear();
        Bytes = 0;
    }

    public void Dispose() => Clear();
}
