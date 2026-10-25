using Android.Content;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using SkiaSharp;

namespace PdfViewer.Android;

/// <summary>
/// The pages of one document in a vertical column: scrolling and flinging, pinch and double-tap
/// zoom, search highlights and text selection. Pages are rendered by the engine's Skia backend on
/// a worker thread, one job at a time, nearest the current page first. Each page has one bitmap of
/// at most <see cref="BaseMaxPixels"/> pixels; at a deeper zoom the view's part of the page is
/// drawn over it in tiles at the zoom's own scale (<see cref="PageTiles"/>), so 8× stays sharp.
/// </summary>
internal sealed class PageView : SkiaView
{
    /// <summary>Whole-page bitmaps kept for scrolling back (native Skia memory, not the Java heap).</summary>
    private const long ImageBudgetBytes = 96L * 1024 * 1024;

    /// <summary>Tiles kept: a phone screen needs about 15 tiles of 1 MiB.</summary>
    private const long TileBudgetBytes = 48L * 1024 * 1024;

    /// <summary>Largest whole-page bitmap (2.4 megapixels, about 9 MiB); a deeper zoom adds tiles.</summary>
    public const long BaseMaxPixels = 2_400_000;

    private readonly PageImageCache<SKImage> _images = new(ImageBudgetBytes);
    private readonly TileCache<SKImage> _tiles = new(TileBudgetBytes);
    private double _tileScale;
    private readonly HashSet<(int Page, long Scale, bool Night, int Column, int Row)> _failed = new();
    private readonly GestureDetector _gestures;
    private readonly ScaleGestureDetector _pinch;
    private readonly OverScroller _scroller;
    private readonly SKPaint _paper = new() { Color = SKColors.White };
    private readonly SKPaint _edge = new() { Color = new SKColor(0, 0, 0, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _hit = new() { Color = new SKColor(255, 200, 0, 90) };
    private readonly SKPaint _currentHit = new() { Color = new SKColor(255, 120, 0, 120) };
    private readonly SKPaint _selected = new() { Color = new SKColor(0x1F, 0x5F, 0xA8, 80) };
    private readonly SKPaint _handle = new() { Color = new SKColor(0x1F, 0x5F, 0xA8), IsAntialias = true };
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private ViewerDocument? _doc;
    private ContinuousPageLayout? _layout;
    private bool _night, _dark, _pinching, _flinging, _rendering, _toldMissingFonts;
    private IReadOnlyList<ViewerSearchHit> _hits = Array.Empty<ViewerSearchHit>();
    private int _currentHitIndex = -1;
    private int _lastReportedPage;
    private (PageAnchor TopLeft, double Zoom)? _pendingView;

    // Text selection: one page at a time; the handle being dragged (0 none, 1 start, 2 end).
    private ViewerTextSelection? _selection;
    private int _dragHandle;
    private readonly Dictionary<int, ViewerTextLayout> _textLayouts = new();

    public PageView(Context context) : base(context)
    {
        _scroller = new OverScroller(context);
        _gestures = new GestureDetector(context, new Gestures(this));
        _gestures.IsLongpressEnabled = true;
        _pinch = new ScaleGestureDetector(context, new Pinch(this));
        Focusable = true;
        ContentDescription = "Pages";
    }

    /// <summary>The page in view changed (page, page count).</summary>
    public event Action<int, int>? PageChanged;

    /// <summary>A page could not be drawn as it should, or an action is not allowed: a short message, never document text.</summary>
    public event Action<string>? Problem;

    /// <summary>A text selection started (true) or ended (false).</summary>
    public event Action<bool>? SelectionChanged;

    /// <summary>The page in view (before the view has its size: the page it will show).</summary>
    public int CurrentPage => _layout == null ? 0 : Width > 0 ? _layout.CurrentPage : _pendingView?.TopLeft.Page ?? 1;

    /// <summary>The document lets its text be copied; when it does not, a long press selects nothing.</summary>
    public bool CopyAllowed { get; set; } = true;

    /// <summary>Shows <paramref name="document"/> from its first page (null shows nothing).</summary>
    public void SetDocument(ViewerDocument? document)
    {
        _scroller.ForceFinished(true);
        _images.Clear();
        _tiles.Clear();
        _tileScale = 0;
        _failed.Clear();
        _hits = Array.Empty<ViewerSearchHit>();
        _currentHitIndex = -1;
        _toldMissingFonts = false;
        _pendingView = null;
        _textLayouts.Clear();
        ClearSelection();
        _doc = document;
        _layout = document == null ? null : new ContinuousPageLayout(document.Pages, gap: 12 * Density, margin: 8 * Density);
        if (Width > 0) _layout?.Resize(Width, Height);
        _lastReportedPage = 0;
        ReportPage();
        Invalidate();
        RequestRender();
    }

    /// <summary>The view to save: the page point at the top-left corner and the zoom.</summary>
    public (PageAnchor TopLeft, double Zoom)? SavedView =>
        _pendingView ?? (_layout is { } l && Width > 0 ? (l.AnchorAt(0, 0), l.Zoom) : null);

    /// <summary>Shows a saved view (after Android ended the process, or a page and zoom from the recent list).</summary>
    public void RestoreView(PageAnchor topLeft, double zoom)
    {
        if (_layout == null) return;
        if (Width <= 0)
        {
            _pendingView = (topLeft, zoom); // applied once the view has its size
            return;
        }
        _scroller.ForceFinished(true);
        _layout.Restore(topLeft, zoom);
        ReportPage();
        Invalidate();
        RequestRender();
    }

    /// <summary>The engine's night mode: pages inverted after drawing.</summary>
    public bool NightPages
    {
        get => _night;
        set
        {
            if (_night == value) return;
            _night = value;
            Invalidate();
            RequestRender();
        }
    }

    /// <summary>Dark chrome around the pages (the system's dark theme).</summary>
    public void SetDark(bool dark)
    {
        _dark = dark;
        _edge.Color = dark ? new SKColor(255, 255, 255, 50) : new SKColor(0, 0, 0, 40);
        var accent = dark ? new SKColor(0x8A, 0xB4, 0xF8) : new SKColor(0x1F, 0x5F, 0xA8);
        _handle.Color = accent;
        _selected.Color = accent.WithAlpha(dark ? (byte)90 : (byte)80);
        Invalidate();
    }

    public void SetSearchHits(IReadOnlyList<ViewerSearchHit> hits, int current)
    {
        _hits = hits;
        _currentHitIndex = current;
        if (_layout != null && current >= 0 && current < hits.Count)
        {
            var hit = hits[current];
            if (!IsHitInView(hit)) _layout.GoTo(hit.Page, hit.Rects.Count > 0 ? hit.Rects[0].Y : 0);
            ReportPage();
            RequestRender();
        }
        Invalidate();
    }

    public void GoToPage(int page)
    {
        if (_layout == null) return;
        _scroller.ForceFinished(true);
        _layout.GoTo(page);
        ReportPage();
        Invalidate();
        RequestRender();
    }

    private float Density => Resources?.DisplayMetrics?.Density ?? 1f;

    private bool IsHitInView(ViewerSearchHit hit)
    {
        if (_layout == null || hit.Rects.Count == 0) return false;
        var r = _layout.ToView(hit.Page, hit.Rects[0]);
        return r.Y >= 0 && r.Bottom <= _layout.ViewHeight && r.X >= 0 && r.Right <= _layout.ViewWidth;
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        _layout?.Resize(w, h);
        if (_pendingView is { } view && _layout != null && w > 0)
        {
            _pendingView = null;
            _layout.Restore(view.TopLeft, view.Zoom);
        }
        ReportPage();
        RequestRender();
    }

    // ------------------------------------------------------------------ drawing

    private double BaseScale(int page, double scale) => PageTiles.BaseScale(_doc!.Pages[page - 1], scale, BaseMaxPixels);

    protected override void OnPaintSurface(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(_dark ? new SKColor(0x1B, 0x1D, 0x21) : new SKColor(0xE3, 0xE6, 0xEA));
        if (_layout == null || _doc == null) return;
        _paper.Color = _night ? SKColors.Black : SKColors.White;
        double scale = _layout.PixelsPerPoint;
        foreach (int page in _layout.VisiblePages())
        {
            var r = _layout.PageRectInView(page);
            var dest = new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);
            if (_images.TryGet(page, _night, out var image, out _))
                canvas.DrawImage(image, dest, _sampling);
            else
                canvas.DrawRect(dest, _paper);
            // Tiles over the whole-page bitmap: those of the current scale, or (while a pinch runs)
            // the last scale's, stretched until the new ones are drawn.
            if (_tileScale > 0 && PageTiles.NeedsTiles(BaseScale(page, scale), scale))
                DrawTiles(canvas, page, r, sharp: PageImageCache<SKImage>.SameScale(_tileScale, scale));
            canvas.DrawRect(dest, _edge);
        }
        for (int i = 0; i < _hits.Count; i++)
        {
            var hit = _hits[i];
            foreach (var rect in hit.Rects)
            {
                var v = _layout.ToView(hit.Page, rect);
                if (v.Bottom < 0 || v.Y > _layout.ViewHeight) continue;
                canvas.DrawRect(new SKRect((float)v.X, (float)v.Y, (float)v.Right, (float)v.Bottom), i == _currentHitIndex ? _currentHit : _hit);
            }
        }
        DrawSelection(canvas);
    }

    private void DrawTiles(SKCanvas canvas, int page, LayoutRect pageInView, bool sharp)
    {
        var info = _doc!.Pages[page - 1];
        foreach (var tile in PageTiles.Visible(info, _tileScale, pageInView, Width, Height))
        {
            if (!_tiles.TryGet(page, _night, _tileScale, tile.Column, tile.Row, out var image)) continue;
            var v = PageTiles.InView(tile, info, _tileScale, pageInView);
            // At the tiles' own scale each tile pixel is one view pixel: whole pixels keep it sharp.
            var dest = sharp
                ? SKRect.Create((float)Math.Round(v.X), (float)Math.Round(v.Y), image.Width, image.Height)
                : new SKRect((float)v.X, (float)v.Y, (float)v.Right, (float)v.Bottom);
            canvas.DrawImage(image, dest, sharp ? new SKSamplingOptions(SKFilterMode.Nearest) : _sampling);
        }
    }

    private void DrawSelection(SKCanvas canvas)
    {
        if (_selection is not { IsEmpty: false } s || _layout == null) return;
        foreach (var band in s.Bands)
        {
            var v = _layout.ToView(s.Page, band);
            canvas.DrawRect(new SKRect((float)v.X, (float)v.Y, (float)v.Right, (float)v.Bottom), _selected);
        }
        if (HandlesInView() is not { } h) return;
        // Android's teardrops: a disc below the line, its corner at the selection's corner.
        float radius = HandleRadius;
        foreach (var (point, start) in new[] { (h.Start, true), (h.End, false) })
        {
            var (cx, cy) = HandleCentre(point, start);
            canvas.DrawCircle(cx, cy, radius, _handle);
            canvas.DrawRect(start ? new SKRect(cx, (float)point.Y, cx + radius, cy) : new SKRect(cx - radius, (float)point.Y, cx, cy), _handle);
        }
    }

    private float HandleRadius => 11 * Density;

    /// <summary>The handle's disc: left of and below the selection's start, right of and below its end.</summary>
    private (float X, float Y) HandleCentre((double X, double Y) corner, bool start) =>
        ((float)corner.X + (start ? -HandleRadius : HandleRadius), (float)corner.Y + HandleRadius);

    /// <summary>The selection handles' points (the bottom corners of the selection) in view pixels.</summary>
    private ((double X, double Y) Start, (double X, double Y) End)? HandlesInView()
    {
        if (_selection?.HandlePositions() is not { } p || _layout == null) return null;
        var page = _layout.PageRectInView(_selection.Page);
        double s = _layout.PixelsPerPoint;
        return ((page.X + p.Start.X * s, page.Y + p.Start.Y * s), (page.X + p.End.X * s, page.Y + p.End.Y * s));
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Starts the render loop unless it runs; it picks the next job itself, so a request while it runs needs nothing more.</summary>
    private void RequestRender()
    {
        if (_rendering || _doc == null) return;
        _ = RenderLoopAsync();
    }

    private readonly record struct Job(ViewerDocument Doc, int Page, double Scale, bool Night, PageTile? Tile);

    private async Task RenderLoopAsync()
    {
        _rendering = true;
        try
        {
            while (NextJob() is { } job)
            {
                ViewerBitmap bitmap;
                try
                {
                    // The engine interprets and Skia draws on a worker thread; the UI thread only scrolls.
                    bitmap = await Task.Run(() => job.Tile is { } tile
                        ? job.Doc.RenderRegionAsync(job.Page, job.Scale, tile, job.Night)
                        : job.Doc.RenderPageAsync(job.Page, job.Scale, job.Night));
                }
                catch (ObjectDisposedException)
                {
                    break; // another document was opened meanwhile
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _failed.Add(FailKey(job));
                    Problem?.Invoke($"Page {job.Page} could not be drawn ({ex.GetType().Name}).");
                    continue;
                }
                if (!ReferenceEquals(job.Doc, _doc)) break;
                if (bitmap.TextRegionsWithoutFont > 0 && !_toldMissingFonts)
                {
                    _toldMissingFonts = true;
                    Problem?.Invoke($"Page {job.Page}: some text uses a font that is neither embedded nor on this device.");
                }
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var image = SKImage.FromPixelCopy(info, bitmap.Pixels.AsSpan(), bitmap.Width * 4);
                if (image == null)
                {
                    _failed.Add(FailKey(job));
                    continue;
                }
                if (job.Tile is { } t)
                {
                    // A tile of a zoom that is no longer the view's: not worth keeping.
                    if (_layout == null || !PageImageCache<SKImage>.SameScale(job.Scale, _layout.PixelsPerPoint))
                    {
                        image.Dispose();
                        continue;
                    }
                    if (!PageImageCache<SKImage>.SameScale(job.Scale, _tileScale))
                    {
                        _tiles.RetainScale(job.Scale);
                        _tileScale = job.Scale;
                    }
                    _tiles.Add(job.Page, job.Night, job.Scale, t.Column, t.Row, image, bitmap.ByteLength);
                }
                else
                {
                    _images.Add(job.Page, job.Night, job.Scale, image, bitmap.ByteLength);
                }
                Invalidate();
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private static (int, long, bool, int, int) FailKey(Job job) =>
        (job.Page, TileCache<SKImage>.ScaleKey(job.Scale), job.Night, job.Tile?.Column ?? -1, job.Tile?.Row ?? -1);

    /// <summary>
    /// The next job at the current zoom: whole-page bitmaps of the pages in view (nearest the
    /// current page first), then their tiles when the zoom is deeper than a bitmap holds (nearest
    /// the view's centre first), then bitmaps one view's height above and below. Nothing while a
    /// pinch or fling is running.
    /// </summary>
    private Job? NextJob()
    {
        if (_doc == null || _layout == null || _pinching || _flinging) return null;
        var doc = _doc;
        double scale = _layout.PixelsPerPoint;
        int current = _layout.CurrentPage;
        var visible = _layout.VisiblePages().OrderBy(p => Math.Abs(p - current)).ToList();

        foreach (int page in visible)
            if (BaseJob(page) is { } job) return job;

        double cx = Width / 2.0, cy = Height / 2.0;
        foreach (int page in visible)
        {
            if (!PageTiles.NeedsTiles(BaseScale(page, scale), scale)) continue;
            var info = doc.Pages[page - 1];
            var rect = _layout.PageRectInView(page);
            var tiles = PageTiles.Visible(info, scale, rect, Width, Height)
                .OrderBy(t => DistanceSquared(PageTiles.InView(t, info, scale, rect), cx, cy));
            foreach (var tile in tiles)
            {
                if (_tiles.Has(page, _night, scale, tile.Column, tile.Row)) continue;
                var job = new Job(doc, page, scale, _night, tile);
                if (!_failed.Contains(FailKey(job))) return job;
            }
        }

        foreach (int page in _layout.VisiblePages(_layout.ViewHeight).Except(visible).OrderBy(p => Math.Abs(p - current)))
            if (BaseJob(page) is { } job) return job;
        return null;

        Job? BaseJob(int page)
        {
            double bs = BaseScale(page, scale);
            if (_images.Has(page, _night, bs)) return null;
            var job = new Job(doc, page, bs, _night, null);
            return _failed.Contains(FailKey(job)) ? null : job;
        }
    }

    private static double DistanceSquared(LayoutRect r, double x, double y)
    {
        double dx = r.X + r.Width / 2 - x, dy = r.Y + r.Height / 2 - y;
        return dx * dx + dy * dy;
    }

    // ------------------------------------------------------------------ text selection

    public bool HasSelection => _selection is { IsEmpty: false };

    /// <summary>The selected text, as the engine extracts it (line breaks between lines, line-end hyphens removed).</summary>
    public string SelectedText => _selection?.Text ?? "";

    public void ClearSelection()
    {
        bool had = _selection != null;
        _selection = null;
        _dragHandle = 0;
        if (had)
        {
            Invalidate();
            SelectionChanged?.Invoke(false);
        }
    }

    /// <summary>Selects all text of the selection's page.</summary>
    public void SelectPage()
    {
        if (_selection == null) return;
        _selection.SelectAll();
        Invalidate();
    }

    /// <summary>A view point as a point on <paramref name="page"/> (shown-page points; outside the page when the point is).</summary>
    private ViewerPoint ToPage(int page, float x, float y)
    {
        var r = _layout!.PageRectInView(page);
        double s = _layout.PixelsPerPoint;
        return new ViewerPoint((x - r.X) / s, (y - r.Y) / s);
    }

    private async Task SelectAtAsync(float x, float y)
    {
        var doc = _doc;
        if (doc == null || _layout == null) return;
        if (!CopyAllowed)
        {
            Problem?.Invoke("This document does not allow copying its text.");
            return;
        }
        int page = _layout.AnchorAt(x, y).Page;
        if (!_textLayouts.TryGetValue(page, out var layout))
        {
            try
            {
                layout = await Task.Run(() => doc.GetTextLayoutAsync(page));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Problem?.Invoke($"The text of page {page} could not be read ({ex.GetType().Name}).");
                return;
            }
            if (!ReferenceEquals(doc, _doc) || _layout == null) return;
            if (_textLayouts.Count >= 8) _textLayouts.Clear();
            _textLayouts[page] = layout;
        }
        var selection = new ViewerTextSelection(page, layout);
        // Within about a finger's width of the text (in points at this zoom).
        if (!selection.SelectWordAt(ToPage(page, x, y), slack: 16 * Density / _layout.PixelsPerPoint))
        {
            if (layout.IsEmpty) Problem?.Invoke("There is no text to select on this page.");
            return;
        }
        PerformHapticFeedback(FeedbackConstants.LongPress);
        bool started = _selection == null;
        _selection = selection;
        Invalidate();
        if (started) SelectionChanged?.Invoke(true);
    }

    /// <summary>Which handle, if any, a touch at (x, y) grabs: 1 start, 2 end, 0 none.</summary>
    private int HandleAt(float x, float y)
    {
        if (HandlesInView() is not { } h) return 0;
        double Dist((float X, float Y) c) => Math.Sqrt(Math.Pow(x - c.X, 2) + Math.Pow(y - c.Y, 2));
        double ds = Dist(HandleCentre(h.Start, true)), de = Dist(HandleCentre(h.End, false)), grab = 2.5 * HandleRadius;
        if (ds > grab && de > grab) return 0;
        return ds <= de ? 1 : 2;
    }

    // ------------------------------------------------------------------ touch

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _layout == null) return base.OnTouchEvent(e);
        if (e.ActionMasked == MotionEventActions.Down && _selection != null && HandleAt(e.GetX(), e.GetY()) is int handle and > 0)
        {
            _dragHandle = handle;
            Parent?.RequestDisallowInterceptTouchEvent(true);
        }
        if (_dragHandle != 0)
        {
            if (e.ActionMasked == MotionEventActions.Move && _selection != null)
            {
                // The handle hangs below the line it marks: aim at the line above the finger.
                var p = ToPage(_selection.Page, e.GetX(), e.GetY() - HandleRadius * 2f);
                if (_selection.MoveHandle(_dragHandle == 1, p)) _dragHandle = 3 - _dragHandle;
                Invalidate();
            }
            else if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                _dragHandle = 0;
            }
            return true;
        }
        _pinch.OnTouchEvent(e);
        if (!_pinch.IsInProgress) _gestures.OnTouchEvent(e);
        if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel && !_flinging)
            RequestRender();
        return true;
    }

    public override void ComputeScroll()
    {
        base.ComputeScroll();
        if (_layout == null) return;
        if (_scroller.ComputeScrollOffset())
        {
            _layout.ScrollTo(_scroller.CurrX, _scroller.CurrY);
            ReportPage();
            Invalidate();
            PostInvalidateOnAnimation();
        }
        else if (_flinging)
        {
            _flinging = false;
            RequestRender();
        }
    }

    private void ScrollPages(float dx, float dy)
    {
        _layout!.ScrollBy(dx, dy);
        ReportPage();
        Invalidate();
    }

    private void Fling(float vx, float vy)
    {
        var l = _layout!;
        _scroller.ForceFinished(true);
        _scroller.Fling((int)l.ScrollX, (int)l.ScrollY, (int)-vx, (int)-vy, 0, (int)l.MaxScrollX, 0, (int)l.MaxScrollY);
        _flinging = true;
        PostInvalidateOnAnimation();
    }

    private void ZoomAt(double zoom, float x, float y)
    {
        _layout!.ZoomAt(zoom, x, y);
        ReportPage();
        Invalidate();
    }

    /// <summary>Zooms around the view's centre (keyboard, tests, accessibility).</summary>
    public void ZoomTo(double zoom)
    {
        if (_layout == null) return;
        ZoomAt(zoom, Width / 2f, Height / 2f);
        RequestRender();
    }

    public double Zoom => _layout?.Zoom ?? 1;

    private void ReportPage()
    {
        int page = CurrentPage;
        if (page == _lastReportedPage) return;
        _lastReportedPage = page;
        PageChanged?.Invoke(page, _layout?.PageCount ?? 0);
    }

    private sealed class Gestures(PageView view) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e)
        {
            if (view._flinging)
            {
                view._scroller.ForceFinished(true);
                view._flinging = false;
            }
            return true;
        }

        public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
        {
            view.ScrollPages(distanceX, distanceY);
            return true;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
        {
            view.Fling(velocityX, velocityY);
            return true;
        }

        /// <summary>A tap ends a selection.</summary>
        public override bool OnSingleTapConfirmed(MotionEvent e)
        {
            view.ClearSelection();
            return true;
        }

        /// <summary>Long press: select the word under the finger.</summary>
        public override void OnLongPress(MotionEvent e) => _ = view.SelectAtAsync(e.GetX(), e.GetY());

        /// <summary>Double tap: zoom in to 2.5× at the finger, or back to fit width.</summary>
        public override bool OnDoubleTap(MotionEvent e)
        {
            var l = view._layout!;
            view.ZoomAt(l.Zoom > 1.05 ? 1 : 2.5, e.GetX(), e.GetY());
            view.RequestRender();
            return true;
        }
    }

    private sealed class Pinch(PageView view) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScaleBegin(ScaleGestureDetector detector)
        {
            view._pinching = true;
            return true;
        }

        public override bool OnScale(ScaleGestureDetector detector)
        {
            view.ZoomAt(view._layout!.Zoom * detector.ScaleFactor, detector.FocusX, detector.FocusY);
            return true;
        }

        public override void OnScaleEnd(ScaleGestureDetector detector)
        {
            view._pinching = false;
            view.RequestRender();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _images.Dispose();
            _tiles.Dispose();
            _paper.Dispose();
            _edge.Dispose();
            _hit.Dispose();
            _currentHit.Dispose();
            _selected.Dispose();
            _handle.Dispose();
        }
        base.Dispose(disposing);
    }
}
