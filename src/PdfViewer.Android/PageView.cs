using Android.Content;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using SkiaSharp;

namespace PdfViewer.Android;

/// <summary>
/// The pages of one document in a vertical column: scrolling and flinging, pinch and double-tap
/// zoom, search highlights. Pages are rendered by the engine's Skia backend on a worker thread,
/// one at a time, nearest the current page first; this view only draws the finished bitmaps with
/// Skia (a stretched old bitmap stands in while a new zoom is rendered).
/// </summary>
internal sealed class PageView : SkiaView
{
    /// <summary>Rendered pages kept for scrolling back (native Skia memory, not the Java heap).</summary>
    private const long ImageBudgetBytes = 160L * 1024 * 1024;

    private readonly PageImageCache<SKImage> _images = new(ImageBudgetBytes);
    private readonly HashSet<(int Page, long Scale, bool Night)> _failed = new();
    private readonly GestureDetector _gestures;
    private readonly ScaleGestureDetector _pinch;
    private readonly OverScroller _scroller;
    private readonly SKPaint _paper = new() { Color = SKColors.White };
    private readonly SKPaint _edge = new() { Color = new SKColor(0, 0, 0, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _hit = new() { Color = new SKColor(255, 200, 0, 90) };
    private readonly SKPaint _currentHit = new() { Color = new SKColor(255, 120, 0, 120) };
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private ViewerDocument? _doc;
    private ContinuousPageLayout? _layout;
    private bool _night, _dark, _pinching, _flinging, _rendering, _toldMissingFonts;
    private IReadOnlyList<ViewerSearchHit> _hits = Array.Empty<ViewerSearchHit>();
    private int _currentHitIndex = -1;
    private int _lastReportedPage;

    public PageView(Context context) : base(context)
    {
        _scroller = new OverScroller(context);
        _gestures = new GestureDetector(context, new Gestures(this));
        _pinch = new ScaleGestureDetector(context, new Pinch(this));
        Focusable = true;
        ContentDescription = "Pages";
    }

    /// <summary>The page in view changed (page, page count).</summary>
    public event Action<int, int>? PageChanged;

    /// <summary>A page could not be drawn as it should: a short message, never document text.</summary>
    public event Action<string>? Problem;

    public int CurrentPage => _layout?.CurrentPage ?? 0;

    /// <summary>Shows <paramref name="document"/> from its first page (null shows nothing).</summary>
    public void SetDocument(ViewerDocument? document)
    {
        _scroller.ForceFinished(true);
        _images.Clear();
        _failed.Clear();
        _hits = Array.Empty<ViewerSearchHit>();
        _currentHitIndex = -1;
        _toldMissingFonts = false;
        _doc = document;
        _layout = document == null ? null : new ContinuousPageLayout(document.Pages, gap: 12 * Density, margin: 8 * Density);
        _layout?.Resize(Width, Height);
        _lastReportedPage = 0;
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
        ReportPage();
        RequestRender();
    }

    protected override void OnPaintSurface(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(_dark ? new SKColor(0x1B, 0x1D, 0x21) : new SKColor(0xE3, 0xE6, 0xEA));
        if (_layout == null) return;
        _paper.Color = _night ? SKColors.Black : SKColors.White;
        foreach (int page in _layout.VisiblePages())
        {
            var r = _layout.PageRectInView(page);
            var dest = new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);
            if (_images.TryGet(page, _night, out var image, out _))
                canvas.DrawImage(image, dest, _sampling);
            else
                canvas.DrawRect(dest, _paper);
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
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Starts the render loop unless it runs; it picks the next page itself, so a request while it runs needs nothing more.</summary>
    private void RequestRender()
    {
        if (_rendering || _doc == null) return;
        _ = RenderLoopAsync();
    }

    private async Task RenderLoopAsync()
    {
        _rendering = true;
        try
        {
            while (NextPageToRender() is var (doc, page, scale, night) && doc != null)
            {
                ViewerBitmap bitmap;
                try
                {
                    // The engine interprets and Skia draws on a worker thread; the UI thread only scrolls.
                    bitmap = await Task.Run(() => doc.RenderPageAsync(page, scale, night));
                }
                catch (ObjectDisposedException)
                {
                    break; // another document was opened meanwhile
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _failed.Add((page, Quantize(scale), night));
                    Problem?.Invoke($"Page {page} could not be drawn ({ex.GetType().Name}).");
                    continue;
                }
                if (!ReferenceEquals(doc, _doc)) break;
                if (bitmap.TextRegionsWithoutFont > 0 && !_toldMissingFonts)
                {
                    _toldMissingFonts = true;
                    Problem?.Invoke($"Page {page}: some text uses a font that is neither embedded nor on this device.");
                }
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var image = SKImage.FromPixelCopy(info, bitmap.Pixels.AsSpan(), bitmap.Width * 4);
                if (image == null)
                {
                    _failed.Add((page, Quantize(scale), night));
                    continue;
                }
                _images.Add(page, night, scale, image, bitmap.ByteLength);
                Invalidate();
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private static long Quantize(double scale) => (long)Math.Round(scale * 1000);

    /// <summary>
    /// The page to render next at the current zoom: the pages in view, nearest the current page
    /// first, then one view's height above and below. Nothing while a pinch or fling is running.
    /// </summary>
    private (ViewerDocument? Doc, int Page, double Scale, bool Night) NextPageToRender()
    {
        if (_doc == null || _layout == null || _pinching || _flinging) return default;
        double scale = _layout.PixelsPerPoint;
        int current = _layout.CurrentPage;
        foreach (int page in _layout.VisiblePages().OrderBy(p => Math.Abs(p - current))
                     .Concat(_layout.VisiblePages(_layout.ViewHeight).OrderBy(p => Math.Abs(p - current))))
            if (!_images.Has(page, _night, scale) && !_failed.Contains((page, Quantize(scale), _night)))
                return (_doc, page, scale, _night);
        return default;
    }

    // ------------------------------------------------------------------ touch

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _layout == null) return base.OnTouchEvent(e);
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

    private void ReportPage()
    {
        int page = _layout?.CurrentPage ?? 0;
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
            _paper.Dispose();
            _edge.Dispose();
            _hit.Dispose();
            _currentHit.Dispose();
        }
        base.Dispose(disposing);
    }
}
