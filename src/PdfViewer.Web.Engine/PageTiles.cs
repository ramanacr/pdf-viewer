namespace PdfViewer.Web.Engine;

/// <summary>A rectangle of pixels of a whole page rendered at one scale; <see cref="Column"/> and <see cref="Row"/> name it in the tile grid.</summary>
public readonly record struct PageTile(int Column, int Row, int X, int Y, int Width, int Height);

/// <summary>
/// Tiles for zoom beyond one bitmap per page: at a deep zoom a page is millions of pixels, so
/// only the tiles that the view shows are rendered, at the zoom's own scale, over a smaller
/// whole-page bitmap that stands in until they are drawn. Pure geometry, so the tests run it.
/// </summary>
public static class PageTiles
{
    /// <summary>Tile edge in pixels: 512 × 512 × 4 bytes is 1 MiB per tile.</summary>
    public const int DefaultSize = 512;

    /// <summary>The page's size in pixels at <paramref name="scale"/>, rounded as the renderer rounds it.</summary>
    public static (int Width, int Height) PixelSize(ViewerPage page, double scale) =>
        ((int)Math.Max(1, Math.Round(page.Width * scale)), (int)Math.Max(1, Math.Round(page.Height * scale)));

    /// <summary>
    /// The scale of the whole-page bitmap: the view's scale, or less when the page would have more
    /// than <paramref name="maxPixels"/> pixels at it.
    /// </summary>
    public static double BaseScale(ViewerPage page, double scale, long maxPixels)
    {
        double pixels = page.Width * scale * page.Height * scale;
        return pixels <= maxPixels ? scale : scale * Math.Sqrt(maxPixels / pixels) * 0.999;
    }

    /// <summary>True when the whole-page bitmap at <paramref name="baseScale"/> is visibly softer than the view's scale, so tiles are worth rendering.</summary>
    public static bool NeedsTiles(double baseScale, double scale) => baseScale < scale * 0.98;

    /// <summary>
    /// The tiles of a page that a view shows: <paramref name="pageInView"/> is the page's rectangle
    /// in view pixels (at <paramref name="scale"/>), the view is <paramref name="viewWidth"/> ×
    /// <paramref name="viewHeight"/>, grown by <paramref name="margin"/> pixels on each side. Rows
    /// from the top, nearest the view's centre first within the list's order (row by row).
    /// </summary>
    public static IReadOnlyList<PageTile> Visible(ViewerPage page, double scale, LayoutRect pageInView, double viewWidth, double viewHeight,
        int tileSize = DefaultSize, double margin = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
        var (w, h) = PixelSize(page, scale);
        var tiles = new List<PageTile>();
        // The part of the view over the page, in page pixels.
        double x0 = Math.Max(0, -margin - pageInView.X), y0 = Math.Max(0, -margin - pageInView.Y);
        double x1 = Math.Min(w, viewWidth + margin - pageInView.X), y1 = Math.Min(h, viewHeight + margin - pageInView.Y);
        if (x1 <= x0 || y1 <= y0) return tiles;
        int c0 = (int)(x0 / tileSize), c1 = (int)Math.Ceiling(x1 / tileSize) - 1;
        int r0 = (int)(y0 / tileSize), r1 = (int)Math.Ceiling(y1 / tileSize) - 1;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                tiles.Add(Tile(w, h, c, r, tileSize));
        return tiles;
    }

    /// <summary>Tile (<paramref name="column"/>, <paramref name="row"/>) of a page <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> pixels; edge tiles are cut to the page.</summary>
    public static PageTile Tile(int pixelWidth, int pixelHeight, int column, int row, int tileSize = DefaultSize)
    {
        int x = column * tileSize, y = row * tileSize;
        return new PageTile(column, row, x, y, Math.Min(tileSize, pixelWidth - x), Math.Min(tileSize, pixelHeight - y));
    }

    /// <summary>Where a tile goes in the view: its pixels placed on the page's rectangle in view pixels.</summary>
    public static LayoutRect InView(PageTile tile, ViewerPage page, double scale, LayoutRect pageInView)
    {
        var (w, h) = PixelSize(page, scale);
        double sx = pageInView.Width / w, sy = pageInView.Height / h;
        return new LayoutRect(pageInView.X + tile.X * sx, pageInView.Y + tile.Y * sy, tile.Width * sx, tile.Height * sy);
    }
}

/// <summary>
/// Rendered tiles a host made (an Android <c>SKImage</c>, for example), least recently used first
/// out within a byte budget, disposed when dropped. Tiles of one scale only are worth keeping: a
/// new scale drops the others (<see cref="RetainScale"/>), since the whole-page bitmap stands in
/// while the new ones are drawn.
/// </summary>
/// <remarks>Not thread-safe: one viewer uses it from its UI thread.</remarks>
public sealed class TileCache<TImage> : IDisposable where TImage : class, IDisposable
{
    private readonly record struct Key(int Page, bool Night, long Scale, int Column, int Row);

    private sealed record Entry(Key Key, TImage Image, long Bytes);

    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _map = new();

    public TileCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
    public long Bytes { get; private set; }
    public int Count => _map.Count;

    /// <summary>The scale as the cache compares it: to a thousandth.</summary>
    public static long ScaleKey(double scale) => (long)Math.Round(scale * 1000);

    public bool Has(int page, bool night, double scale, int column, int row) => _map.ContainsKey(new Key(page, night, ScaleKey(scale), column, row));

    public bool TryGet(int page, bool night, double scale, int column, int row, out TImage image)
    {
        if (_map.TryGetValue(new Key(page, night, ScaleKey(scale), column, row), out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
        image = null!;
        return false;
    }

    /// <summary>Keeps a tile; the least recently used beyond the budget are disposed (the newest is always kept).</summary>
    public void Add(int page, bool night, double scale, int column, int row, TImage image, long bytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        var key = new Key(page, night, ScaleKey(scale), column, row);
        if (_map.Remove(key, out var old))
            Drop(old, dispose: !ReferenceEquals(old.Value.Image, image));
        _map[key] = _lru.AddFirst(new Entry(key, image, bytes));
        Bytes += bytes;
        while (Bytes > MaxBytes && _lru.Count > 1)
        {
            var last = _lru.Last!;
            _map.Remove(last.Value.Key);
            Drop(last, dispose: true);
        }
    }

    /// <summary>Disposes every tile not at <paramref name="scale"/> (the zoom changed).</summary>
    public void RetainScale(double scale)
    {
        long key = ScaleKey(scale);
        for (var node = _lru.First; node != null;)
        {
            var next = node.Next;
            if (node.Value.Key.Scale != key)
            {
                _map.Remove(node.Value.Key);
                Drop(node, dispose: true);
            }
            node = next;
        }
    }

    private void Drop(LinkedListNode<Entry> node, bool dispose)
    {
        _lru.Remove(node);
        Bytes -= node.Value.Bytes;
        if (dispose) node.Value.Image.Dispose();
    }

    public void Clear()
    {
        foreach (var entry in _lru) entry.Image.Dispose();
        _lru.Clear();
        _map.Clear();
        Bytes = 0;
    }

    public void Dispose() => Clear();
}
