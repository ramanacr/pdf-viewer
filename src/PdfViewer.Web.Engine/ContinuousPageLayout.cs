namespace PdfViewer.Web.Engine;

/// <summary>A rectangle in a viewer's content (or view) pixels.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>A point on a page as a fraction of its shown width and height (0 to 1 from the top-left corner).</summary>
public readonly record struct PageAnchor(int Page, double U, double V);

/// <summary>How a <see cref="ContinuousPageLayout"/> arranges the pages (the desktop's view modes).</summary>
public enum PageLayoutMode
{
    /// <summary>One vertical column, every page fitted to the view's width.</summary>
    Continuous,

    /// <summary>One page at a time, fitted to the view's width and height (fit page).</summary>
    SinglePage,

    /// <summary>Rows of two pages side by side (a spread), fitted to the width of the widest row.</summary>
    Facing,
}

/// <summary>
/// Pages as a touch viewer scrolls them. <see cref="PageLayoutMode.Continuous"/> (the default): one
/// vertical column, every page as wide as the view at zoom 1 (fit width, the widest page decides),
/// centred, with a fixed gap between pages. <see cref="PageLayoutMode.Facing"/>: rows of two pages
/// side by side (with <see cref="CoverAlone"/>, page 1 alone, then 2–3, 4–5…), each row centred and
/// as tall as its taller page, the widest row fitted to the view's width. <see cref="PageLayoutMode.SinglePage"/>:
/// only one page (<see cref="CurrentPage"/>) in the content, fitted to the view's width and height;
/// <see cref="Turn"/> shows the next or previous one, and <see cref="SetSlide"/> moves the page aside
/// as a swipe drags it, with its neighbour coming in next to it. All lengths are view pixels; the
/// content scrolls under a view of <see cref="ViewWidth"/> × <see cref="ViewHeight"/>. Free of any UI
/// toolkit, so that the Android viewer and the tests share it.
/// </summary>
/// <remarks>
/// Not thread-safe: one viewer uses it from its UI thread. <see cref="Rotation"/> turns every page
/// in the view (the desktop's Rotate View) without changing the document: pages keep their shown-page
/// points (search, selection, annotations), and <see cref="ToView(int, ViewerRect)"/>,
/// <see cref="ToViewPoint"/> and <see cref="ToPagePoint"/> turn them. In single page mode the other
/// pages have a place too, beside the shown page along the slide's axis (one view apart), so that
/// every mapping still works for them; only the shown page and a neighbour slid into view are visible.
/// </remarks>
public sealed class ContinuousPageLayout
{
    private readonly IReadOnlyList<ViewerPage> _pages;
    // Per page (index): its left and top in the content. Per row: its first page (with one more entry
    // after the last row), top and height. In continuous mode every page is a row of its own.
    private readonly double[] _lefts, _tops, _rowTops, _rowHeights;
    private readonly int[] _rowStart;
    private int _rowCount;
    private double _maxWidthPoints;
    // Single page mode: the shown page (index), and whether its neighbours sit below and above it
    // (after a vertical slide) rather than right and left.
    private int _shown;
    private bool _slideVertical;

    public ContinuousPageLayout(IReadOnlyList<ViewerPage> pages, double gap = 16, double margin = 8)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("A layout needs at least one page.", nameof(pages));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        _pages = pages;
        _lefts = new double[pages.Count];
        _tops = new double[pages.Count];
        _rowTops = new double[pages.Count];
        _rowHeights = new double[pages.Count];
        _rowStart = new int[pages.Count + 1];
        _maxWidthPoints = pages.Max(p => p.Width);
        Gap = gap;
        Margin = margin;
        Resize(1, 1);
    }

    public const double MinZoom = 0.5, MaxZoom = 8;

    /// <summary>How the pages are arranged; <see cref="SetMode"/> changes it.</summary>
    public PageLayoutMode Mode { get; private set; }

    /// <summary>Facing mode only: page 1 is a row of its own (a book's cover), so spreads start at page 2.</summary>
    public bool CoverAlone { get; private set; }

    /// <summary>
    /// Arranges the pages as <paramref name="mode"/> (<paramref name="coverAlone"/> counts in facing
    /// mode only) at zoom 1; the page in view stays in view, from its top.
    /// </summary>
    public void SetMode(PageLayoutMode mode, bool coverAlone = false)
    {
        coverAlone &= mode == PageLayoutMode.Facing;
        if (mode == Mode && coverAlone == CoverAlone) return;
        int page = CurrentPage;
        Mode = mode;
        CoverAlone = coverAlone;
        Zoom = 1;
        _shown = page - 1;
        ClearSlide();
        Relayout();
        GoTo(page);
    }

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

    /// <summary>1 is fit width (fit page in single page mode).</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>View pixels per PDF point at the current zoom: the scale to render pages at.</summary>
    public double PixelsPerPoint { get; private set; }

    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }

    /// <summary>The view's top-left corner in the content, kept within the content by <see cref="ScrollTo"/>.</summary>
    public double ScrollX { get; private set; }
    public double ScrollY { get; private set; }

    /// <summary>
    /// Single page mode: how far (view pixels) a swipe has moved the shown page aside, right or down
    /// when positive; always 0 in the other modes. See <see cref="SetSlide"/>.
    /// </summary>
    public double SlideX { get; private set; }

    /// <inheritdoc cref="SlideX"/>
    public double SlideY { get; private set; }

    /// <summary>
    /// Single page mode: moves the shown page aside by (<paramref name="dx"/>, <paramref name="dy"/>)
    /// view pixels, as a swipe drags it, so that the neighbour on that side comes into view next to it
    /// (the previous page when the page moves right or down, the next one when it moves left or up).
    /// The slide is along one axis, the longer one; it is held at 0 towards a side with no page. In
    /// the other modes there is no slide.
    /// </summary>
    public void SetSlide(double dx, double dy)
    {
        if (Mode != PageLayoutMode.SinglePage || !double.IsFinite(dx) || !double.IsFinite(dy))
        {
            ClearSlide();
            return;
        }
        if (Math.Abs(dy) > Math.Abs(dx)) dx = 0;
        else dy = 0;
        if (_shown == 0) { dx = Math.Min(dx, 0); dy = Math.Min(dy, 0); }
        if (_shown == _pages.Count - 1) { dx = Math.Max(dx, 0); dy = Math.Max(dy, 0); }
        SlideX = dx;
        SlideY = dy;
        if (dx != 0 || dy != 0) _slideVertical = dy != 0;
    }

    private void ClearSlide() => SlideX = SlideY = 0;

    /// <summary>
    /// Single page mode: shows the page <paramref name="step"/>'s sign away (the next one when positive)
    /// at the same zoom, from its start when going forward and from its end (its right edge, or its
    /// bottom after a <paramref name="vertical"/> turn) when going back, as if the pages lay side by
    /// side. False, and nothing changes, at the first or last page or in the other modes.
    /// </summary>
    public bool Turn(int step, bool vertical = false)
    {
        int target = _shown + Math.Sign(step);
        if (Mode != PageLayoutMode.SinglePage || step == 0 || target < 0 || target >= _pages.Count) return false;
        _shown = target;
        ClearSlide();
        Relayout();
        if (step > 0) ScrollTo(0, 0);
        else ScrollTo(vertical ? 0 : MaxScrollX, vertical ? MaxScrollY : 0);
        return true;
    }

    /// <summary>The view was resized; the page at the top of the view stays there.</summary>
    public void Resize(double viewWidth, double viewHeight)
    {
        var anchor = ViewWidth > 0 ? AnchorAt(0, 0) : new PageAnchor(1, 0, 0);
        ClearSlide();
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
        ClearSlide();
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
        var anchor = topLeft with
        {
            Page = Math.Clamp(topLeft.Page, 1, _pages.Count),
            U = double.IsFinite(topLeft.U) ? Math.Clamp(topLeft.U, 0, 1) : 0,
            V = double.IsFinite(topLeft.V) ? Math.Clamp(topLeft.V, 0, 1) : 0,
        };
        Zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, MinZoom, MaxZoom);
        _shown = anchor.Page - 1;
        ClearSlide();
        Relayout();
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
        int i = page - 1;
        double w = WidthOf(i) * PixelsPerPoint, h = HeightOf(i) * PixelsPerPoint;
        if (Mode == PageLayoutMode.SinglePage && i != _shown) return NeighbourRect(i - _shown, w, h);
        return new LayoutRect(_lefts[i], _tops[i], w, h);
    }

    /// <summary>
    /// Single page mode: the place of the page <paramref name="d"/> pages after the shown one, in a
    /// slot one view in size per page beside the content (right or left; below or above after a
    /// vertical slide), centred in the slot or, when larger, against the slot's side nearer the content.
    /// </summary>
    private LayoutRect NeighbourRect(int d, double w, double h)
    {
        double padW = Math.Max(0, (ViewWidth - w) / 2), padH = Math.Max(0, (ViewHeight - h) / 2);
        if (_slideVertical)
        {
            double top = d > 0 ? ContentHeight + Gap + (d - 1) * (ViewHeight + Gap) : d * (ViewHeight + Gap);
            double y = d > 0 ? top + padH : top + ViewHeight - padH - h;
            return new LayoutRect(ScrollX + padW, y, w, h);
        }
        double left = d > 0 ? ContentWidth + Gap + (d - 1) * (ViewWidth + Gap) : d * (ViewWidth + Gap);
        double x = d > 0 ? left + padW : left + ViewWidth - padW - w;
        return new LayoutRect(x, ScrollY + padH, w, h);
    }

    /// <summary>Page <paramref name="page"/> in view pixels (content minus scroll, plus the slide).</summary>
    public LayoutRect PageRectInView(int page)
    {
        var r = PageRect(page);
        return r with { X = r.X - ScrollX + SlideX, Y = r.Y - ScrollY + SlideY };
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

    /// <summary>
    /// The pages that the view, grown by <paramref name="extra"/> pixels above and below, shows; in
    /// page order. In facing mode a page beside the view (zoomed in on its row's other page) is not
    /// shown; in single page mode the view grows on every side, so that the neighbours count as near.
    /// </summary>
    public IEnumerable<int> VisiblePages(double extra = 0)
    {
        if (Mode == PageLayoutMode.SinglePage)
        {
            for (int i = Math.Max(0, _shown - 1); i <= Math.Min(_pages.Count - 1, _shown + 1); i++)
            {
                var v = PageRectInView(i + 1);
                if (v.Right > -extra && v.X < ViewWidth + extra && v.Bottom > -extra && v.Y < ViewHeight + extra)
                    yield return i + 1;
            }
            yield break;
        }
        double top = ScrollY - extra, bottom = ScrollY + ViewHeight + extra;
        for (int row = RowAt(top); row < _rowCount && _rowTops[row] < bottom; row++)
        {
            if (_rowTops[row] + _rowHeights[row] <= top) continue;
            for (int i = _rowStart[row]; i < _rowStart[row + 1]; i++)
            {
                if (Mode == PageLayoutMode.Facing && !Overlaps(i, top, bottom)) continue;
                yield return i + 1;
            }
        }
    }

    /// <summary>Whether page index <paramref name="i"/> is between <paramref name="top"/> and <paramref name="bottom"/> and across the view's width (content pixels).</summary>
    private bool Overlaps(int i, double top, double bottom)
    {
        double w = WidthOf(i) * PixelsPerPoint, h = HeightOf(i) * PixelsPerPoint;
        return _tops[i] < bottom && _tops[i] + h > top && _lefts[i] < ScrollX + ViewWidth && _lefts[i] + w > ScrollX;
    }

    /// <summary>
    /// The page a page indicator shows: the one that covers the middle of the view, or the nearest
    /// one; in a facing row, the page under the view's middle, else the row's first page; in single
    /// page mode, the shown page.
    /// </summary>
    public int CurrentPage
    {
        get
        {
            if (Mode == PageLayoutMode.SinglePage) return _shown + 1;
            double middle = ScrollY + ViewHeight / 2;
            int row = Math.Min(RowAt(middle), _rowCount - 1);
            // In the gap after a row: the nearer of it and the next one.
            double end = _rowTops[row] + _rowHeights[row];
            if (row + 1 < _rowCount && middle > end && middle - end >= _rowTops[row + 1] - middle)
                row++;
            return PageInRow(row, ScrollX + ViewWidth / 2, nearest: false) + 1;
        }
    }

    /// <summary>
    /// Scrolls so that page <paramref name="page"/> starts at the top of the view, or the point
    /// <paramref name="y"/> points down it (in single page mode: shows that page). In facing mode
    /// a page beside the view is scrolled into it.
    /// </summary>
    public void GoTo(int page, double y = 0)
    {
        page = Math.Clamp(page, 1, _pages.Count);
        ClearSlide();
        if (Mode == PageLayoutMode.SinglePage && page - 1 != _shown)
        {
            _shown = page - 1;
            Relayout();
        }
        // A point down the shown page is down the view when the view is upright or upside down; turned
        // a quarter, the page is shown from its top.
        double down = Rotation switch
        {
            0 => y,
            180 => y > 0 ? _pages[page - 1].Height - y : 0,
            _ => 0,
        };
        var r = PageRect(page);
        double x = ScrollX;
        if (Mode == PageLayoutMode.Facing && (r.X < ScrollX || r.Right > ScrollX + ViewWidth))
            x = r.Width <= ViewWidth ? r.X + (r.Width - ViewWidth) / 2 : r.X;
        ScrollTo(x, r.Y + down * PixelsPerPoint - (down > 0 ? ViewHeight / 3 : Gap / 2));
    }

    /// <summary>The page point under a view point (the nearest page, clamped to it, when the point is in a gap or margin).</summary>
    public PageAnchor AnchorAt(double viewX, double viewY)
    {
        double x = ScrollX + viewX - SlideX, y = ScrollY + viewY - SlideY;
        int i = _shown;
        if (Mode != PageLayoutMode.SinglePage)
        {
            int row = Math.Min(RowAt(y), _rowCount - 1);
            double end = _rowTops[row] + _rowHeights[row];
            if (y > end && row + 1 < _rowCount && y - end > _rowTops[row + 1] - y) row++;
            i = PageInRow(row, x, nearest: true);
        }
        var r = PageRect(i + 1);
        return new PageAnchor(i + 1, Math.Clamp((x - r.X) / r.Width, 0, 1), Math.Clamp((y - r.Y) / r.Height, 0, 1));
    }

    /// <summary>
    /// The page (index) of a row under content x: the one across it, else the nearest one
    /// (<paramref name="nearest"/>) or the row's first.
    /// </summary>
    private int PageInRow(int row, double x, bool nearest)
    {
        int first = _rowStart[row], best = first;
        double bestDistance = double.MaxValue;
        for (int i = first; i < _rowStart[row + 1]; i++)
        {
            double left = _lefts[i], right = left + WidthOf(i) * PixelsPerPoint;
            double distance = x < left ? left - x : x > right ? x - right : 0;
            if (distance == 0) return i;
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return nearest ? best : first;
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
        switch (Mode)
        {
            case PageLayoutMode.SinglePage: LayoutSinglePage(); break;
            case PageLayoutMode.Facing: LayoutFacing(); break;
            default: LayoutContinuous(); break;
        }
        ScrollTo(ScrollX, ScrollY);
    }

    private void LayoutContinuous()
    {
        PixelsPerPoint = Math.Max(1e-3, (ViewWidth - 2 * Margin) / _maxWidthPoints) * Zoom;
        ContentWidth = Math.Max(ViewWidth, _maxWidthPoints * PixelsPerPoint + 2 * Margin);
        double y = Margin;
        for (int i = 0; i < _pages.Count; i++)
        {
            double h = HeightOf(i) * PixelsPerPoint;
            _rowStart[i] = i;
            _rowTops[i] = _tops[i] = y;
            _rowHeights[i] = h;
            _lefts[i] = (ContentWidth - WidthOf(i) * PixelsPerPoint) / 2;
            y += h + (i + 1 < _pages.Count ? Gap : 0);
        }
        _rowCount = _pages.Count;
        _rowStart[_rowCount] = _pages.Count;
        ContentHeight = y + Margin;
    }

    private void LayoutFacing()
    {
        int rows = 0, start = 0;
        if (CoverAlone) _rowStart[rows++] = start++;
        for (; start < _pages.Count; start += 2) _rowStart[rows++] = start;
        _rowCount = rows;
        _rowStart[rows] = _pages.Count;

        // Zoom 1 fits the widest row (its pages and the gap between them) to the view's width.
        double fit = double.MaxValue;
        for (int row = 0; row < rows; row++)
            fit = Math.Min(fit, (ViewWidth - 2 * Margin - Gaps(row)) / RowPoints(row));
        PixelsPerPoint = Math.Max(1e-3, fit) * Zoom;
        double widest = 0;
        for (int row = 0; row < rows; row++) widest = Math.Max(widest, RowPoints(row) * PixelsPerPoint + Gaps(row));
        ContentWidth = Math.Max(ViewWidth, widest + 2 * Margin);

        double y = Margin;
        for (int row = 0; row < rows; row++)
        {
            double height = 0;
            for (int i = _rowStart[row]; i < _rowStart[row + 1]; i++) height = Math.Max(height, HeightOf(i) * PixelsPerPoint);
            double x = (ContentWidth - RowPoints(row) * PixelsPerPoint - Gaps(row)) / 2;
            for (int i = _rowStart[row]; i < _rowStart[row + 1]; i++)
            {
                // A shorter page sits in the middle of its row's height.
                _lefts[i] = x;
                _tops[i] = y + (height - HeightOf(i) * PixelsPerPoint) / 2;
                x += WidthOf(i) * PixelsPerPoint + Gap;
            }
            _rowTops[row] = y;
            _rowHeights[row] = height;
            y += height + (row + 1 < rows ? Gap : 0);
        }
        ContentHeight = y + Margin;

        double RowPoints(int row)
        {
            double sum = 0;
            for (int i = _rowStart[row]; i < _rowStart[row + 1]; i++) sum += WidthOf(i);
            return sum;
        }

        double Gaps(int row) => (_rowStart[row + 1] - _rowStart[row] - 1) * Gap;
    }

    private void LayoutSinglePage()
    {
        _shown = Math.Clamp(_shown, 0, _pages.Count - 1);
        int i = _shown;
        double w = WidthOf(i), h = HeightOf(i);
        PixelsPerPoint = Math.Max(1e-3, Math.Min((ViewWidth - 2 * Margin) / w, (ViewHeight - 2 * Margin) / h)) * Zoom;
        ContentWidth = Math.Max(ViewWidth, w * PixelsPerPoint + 2 * Margin);
        ContentHeight = Math.Max(ViewHeight, h * PixelsPerPoint + 2 * Margin);
        _lefts[i] = (ContentWidth - w * PixelsPerPoint) / 2;
        _tops[i] = (ContentHeight - h * PixelsPerPoint) / 2;
        _rowCount = 1;
        _rowStart[0] = i;
        _rowStart[1] = i + 1;
        _rowTops[0] = _tops[i];
        _rowHeights[0] = h * PixelsPerPoint;
    }

    /// <summary>The index of the last row whose top is at or above <paramref name="y"/> (0 when none is).</summary>
    private int RowAt(double y)
    {
        int i = Array.BinarySearch(_rowTops, 0, _rowCount, y);
        if (i < 0) i = ~i - 1;
        return Math.Max(0, i);
    }
}
