namespace PdfViewer.Web.Engine;

/// <summary>A rectangle in a viewer's content (or view) pixels.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>A point on a page as a fraction of its shown width and height (0 to 1 from the top-left corner).</summary>
public readonly record struct PageAnchor(int Page, double U, double V);

/// <summary>
/// Pages in one vertical column, as a touch viewer scrolls them: every page as wide as the view
/// at zoom 1 (fit width, the widest page decides), centred, with a fixed gap between pages. All
/// lengths are view pixels; the content scrolls under a view of <see cref="ViewWidth"/> ×
/// <see cref="ViewHeight"/>. Free of any UI toolkit, so that the Android viewer and the tests share it.
/// </summary>
/// <remarks>
/// Not thread-safe: one viewer uses it from its UI thread. <see cref="Rotation"/> turns every page
/// in the view (the desktop's Rotate View) without changing the document: pages keep their shown-page
/// points (search, selection, annotations), and <see cref="ToView(int, ViewerRect)"/>,
/// <see cref="ToViewPoint"/> and <see cref="ToPagePoint"/> turn them.
/// </remarks>
public sealed class ContinuousPageLayout
{
    private readonly IReadOnlyList<ViewerPage> _pages;
    private readonly double[] _tops;
    private double _maxWidthPoints;

    public ContinuousPageLayout(IReadOnlyList<ViewerPage> pages, double gap = 16, double margin = 8)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("A layout needs at least one page.", nameof(pages));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        _pages = pages;
        _tops = new double[pages.Count];
        _maxWidthPoints = pages.Max(p => p.Width);
        Gap = gap;
        Margin = margin;
        Resize(1, 1);
    }

    public const double MinZoom = 0.5, MaxZoom = 8;

    /// <summary>How far the view turns every page clockwise: 0, 90, 180 or 270 degrees.</summary>
    public int Rotation { get; private set; }

    /// <summary>
    /// Turns the view to <paramref name="degrees"/> clockwise (any multiple of 90; others are
    /// rounded to one); the page in view stays in view, from its top.
    /// </summary>
    public void SetRotation(int degrees)
    {
        int turn = ((int)Math.Round(degrees / 90.0) * 90 % 360 + 360) % 360;
        if (turn == Rotation) return;
        int page = CurrentPage;
        Rotation = turn;
        _maxWidthPoints = Enumerable.Range(0, _pages.Count).Max(WidthOf);
        Relayout();
        GoTo(page);
    }

    /// <summary>A page's width and height in the view, in points: its shown size, turned by <see cref="Rotation"/>.</summary>
    private double WidthOf(int index) => Rotation is 90 or 270 ? _pages[index].Height : _pages[index].Width;

    private double HeightOf(int index) => Rotation is 90 or 270 ? _pages[index].Width : _pages[index].Height;

    public double Gap { get; }
    public double Margin { get; }
    public int PageCount => _pages.Count;
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    /// <summary>1 is fit width.</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>View pixels per PDF point at the current zoom: the scale to render pages at.</summary>
    public double PixelsPerPoint { get; private set; }

    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }

    /// <summary>The view's top-left corner in the content, kept within the content by <see cref="ScrollTo"/>.</summary>
    public double ScrollX { get; private set; }
    public double ScrollY { get; private set; }

    /// <summary>The view was resized; the page at the top of the view stays there.</summary>
    public void Resize(double viewWidth, double viewHeight)
    {
        var anchor = ViewWidth > 0 ? AnchorAt(0, 0) : new PageAnchor(1, 0, 0);
        ViewWidth = Math.Max(1, viewWidth);
        ViewHeight = Math.Max(1, viewHeight);
        Relayout();
        ScrollToAnchor(anchor, 0, 0);
    }

    /// <summary>
    /// Sets the zoom (clamped to <see cref="MinZoom"/> … <see cref="MaxZoom"/>) so that the content
    /// point under (<paramref name="focusX"/>, <paramref name="focusY"/>) in the view stays under it,
    /// as a pinch does.
    /// </summary>
    public void ZoomAt(double zoom, double focusX, double focusY)
    {
        var anchor = AnchorAt(focusX, focusY);
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Relayout();
        ScrollToAnchor(anchor, focusX, focusY);
    }

    /// <summary>
    /// Shows the view saved as <paramref name="topLeft"/> (<see cref="AnchorAt"/> of the view's
    /// top-left corner) and <paramref name="zoom"/>: after a restart, a rotation or a new view size.
    /// </summary>
    public void Restore(PageAnchor topLeft, double zoom)
    {
        Zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, MinZoom, MaxZoom);
        Relayout();
        var anchor = topLeft with
        {
            Page = Math.Clamp(topLeft.Page, 1, _pages.Count),
            U = double.IsFinite(topLeft.U) ? Math.Clamp(topLeft.U, 0, 1) : 0,
            V = double.IsFinite(topLeft.V) ? Math.Clamp(topLeft.V, 0, 1) : 0,
        };
        ScrollToAnchor(anchor, 0, 0);
    }

    public void ScrollBy(double dx, double dy) => ScrollTo(ScrollX + dx, ScrollY + dy);

    public void ScrollTo(double x, double y)
    {
        ScrollX = Math.Clamp(x, 0, MaxScrollX);
        ScrollY = Math.Clamp(y, 0, MaxScrollY);
    }

    public double MaxScrollX => Math.Max(0, ContentWidth - ViewWidth);
    public double MaxScrollY => Math.Max(0, ContentHeight - ViewHeight);

    /// <summary>Page <paramref name="page"/> (1-based) in content pixels.</summary>
    public LayoutRect PageRect(int page)
    {
        double w = WidthOf(page - 1) * PixelsPerPoint, h = HeightOf(page - 1) * PixelsPerPoint;
        return new LayoutRect((ContentWidth - w) / 2, _tops[page - 1], w, h);
    }

    /// <summary>Page <paramref name="page"/> in view pixels (content minus scroll).</summary>
    public LayoutRect PageRectInView(int page)
    {
        var r = PageRect(page);
        return r with { X = r.X - ScrollX, Y = r.Y - ScrollY };
    }

    /// <summary>A rectangle on a page (points from the shown page's top-left corner, as search returns) in view pixels.</summary>
    public LayoutRect ToView(int page, ViewerRect rect)
    {
        if (Rotation == 0)
        {
            var r = PageRectInView(page);
            return new LayoutRect(r.X + rect.X * PixelsPerPoint, r.Y + rect.Y * PixelsPerPoint, rect.Width * PixelsPerPoint, rect.Height * PixelsPerPoint);
        }
        var (x1, y1) = ToViewPoint(page, new ViewerPoint(rect.X, rect.Y));
        var (x2, y2) = ToViewPoint(page, new ViewerPoint(rect.X + rect.Width, rect.Y + rect.Height));
        return new LayoutRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    /// <summary>A point on a page (points from the shown page's top-left corner) in view pixels, turned by <see cref="Rotation"/>.</summary>
    public (double X, double Y) ToViewPoint(int page, ViewerPoint point)
    {
        var r = PageRectInView(page);
        double s = PixelsPerPoint, x = point.X * s, y = point.Y * s;
        double w = _pages[page - 1].Width * s, h = _pages[page - 1].Height * s;
        return Rotation switch
        {
            90 => (r.X + h - y, r.Y + x),
            180 => (r.X + w - x, r.Y + h - y),
            270 => (r.X + y, r.Y + w - x),
            _ => (r.X + x, r.Y + y),
        };
    }

    /// <summary>The pages that the view, grown by <paramref name="extra"/> pixels above and below, shows; in page order.</summary>
    public IEnumerable<int> VisiblePages(double extra = 0)
    {
        double top = ScrollY - extra, bottom = ScrollY + ViewHeight + extra;
        for (int i = IndexAt(top); i < _pages.Count && _tops[i] < bottom; i++)
            if (_tops[i] + HeightOf(i) * PixelsPerPoint > top)
                yield return i + 1;
    }

    /// <summary>The page a page indicator shows: the one that covers the middle of the view, or the nearest one.</summary>
    public int CurrentPage
    {
        get
        {
            double middle = ScrollY + ViewHeight / 2;
            int i = Math.Min(IndexAt(middle), _pages.Count - 1);
            // In the gap after page i: the nearer of i and i + 1.
            if (i + 1 < _pages.Count && middle > _tops[i] + HeightOf(i) * PixelsPerPoint)
                return middle - (_tops[i] + HeightOf(i) * PixelsPerPoint) < _tops[i + 1] - middle ? i + 1 : i + 2;
            return i + 1;
        }
    }

    /// <summary>Scrolls so that page <paramref name="page"/> starts at the top of the view, or the point <paramref name="y"/> points down it.</summary>
    public void GoTo(int page, double y = 0)
    {
        page = Math.Clamp(page, 1, _pages.Count);
        // A point down the shown page is down the view when the view is upright or upside down; turned
        // a quarter, the page is shown from its top.
        double down = Rotation switch
        {
            0 => y,
            180 => y > 0 ? _pages[page - 1].Height - y : 0,
            _ => 0,
        };
        ScrollTo(ScrollX, PageRect(page).Y + down * PixelsPerPoint - (down > 0 ? ViewHeight / 3 : Gap / 2));
    }

    /// <summary>The page point under a view point (the nearest page, clamped to it, when the point is in a gap or margin).</summary>
    public PageAnchor AnchorAt(double viewX, double viewY)
    {
        double x = ScrollX + viewX, y = ScrollY + viewY;
        int i = Math.Min(IndexAt(y), _pages.Count - 1);
        var r = PageRect(i + 1);
        if (y > r.Bottom && i + 1 < _pages.Count && y - r.Bottom > _tops[i + 1] - y)
            r = PageRect(++i + 1);
        return new PageAnchor(i + 1, Math.Clamp((x - r.X) / r.Width, 0, 1), Math.Clamp((y - r.Y) / r.Height, 0, 1));
    }

    /// <summary>A view point as a point on <paramref name="page"/>: points from the shown page's top-left corner, outside the page when the view point is.</summary>
    public ViewerPoint ToPagePoint(int page, double viewX, double viewY)
    {
        var r = PageRectInView(page);
        double s = PixelsPerPoint, dx = viewX - r.X, dy = viewY - r.Y;
        double w = _pages[page - 1].Width * s, h = _pages[page - 1].Height * s;
        var (x, y) = Rotation switch
        {
            90 => (dy, h - dx),
            180 => (w - dx, h - dy),
            270 => (w - dy, dx),
            _ => (dx, dy),
        };
        return new ViewerPoint(x / s, y / s);
    }

    /// <summary>The page under a view point and the point on it, or null in a gap or margin (a touch there is on no page).</summary>
    public (int Page, ViewerPoint Point)? PageAt(double viewX, double viewY)
    {
        if (_pages.Count == 0) return null;
        int page = AnchorAt(viewX, viewY).Page;
        var r = PageRectInView(page);
        if (viewX < r.X || viewX > r.Right || viewY < r.Y || viewY > r.Bottom) return null;
        return (page, ToPagePoint(page, viewX, viewY));
    }

    private void ScrollToAnchor(PageAnchor anchor, double viewX, double viewY)
    {
        var r = PageRect(anchor.Page);
        ScrollTo(r.X + anchor.U * r.Width - viewX, r.Y + anchor.V * r.Height - viewY);
    }

    private void Relayout()
    {
        PixelsPerPoint = Math.Max(1e-3, (ViewWidth - 2 * Margin) / _maxWidthPoints) * Zoom;
        ContentWidth = Math.Max(ViewWidth, _maxWidthPoints * PixelsPerPoint + 2 * Margin);
        double y = Margin;
        for (int i = 0; i < _pages.Count; i++)
        {
            _tops[i] = y;
            y += HeightOf(i) * PixelsPerPoint + (i + 1 < _pages.Count ? Gap : 0);
        }
        ContentHeight = y + Margin;
        ScrollTo(ScrollX, ScrollY);
    }

    /// <summary>The index of the last page whose top is at or above <paramref name="y"/> (0 when none is).</summary>
    private int IndexAt(double y)
    {
        int i = Array.BinarySearch(_tops, y);
        if (i < 0) i = ~i - 1;
        return Math.Max(0, i);
    }
}
