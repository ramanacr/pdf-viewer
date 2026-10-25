using Android.Content;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using SkiaSharp;

namespace PdfViewer.Android;

/// <summary>
/// The document's pages as a grid of thumbnails (<see cref="ThumbnailGridLayout"/>): scroll, fling,
/// and tap a page to go to it. Thumbnails are rendered by the engine on a worker thread, the pages
/// in view first, and kept within a small budget.
/// </summary>
internal sealed class ThumbnailView : SkiaView
{
    private const long ThumbnailBudgetBytes = 24L * 1024 * 1024;

    private readonly PageImageCache<SKImage> _images = new(ThumbnailBudgetBytes);
    private readonly HashSet<int> _failed = new();
    private readonly GestureDetector _gestures;
    private readonly OverScroller _scroller;
    private readonly SKPaint _paper = new() { Color = SKColors.White };
    private readonly SKPaint _edge = new() { Color = new SKColor(0, 0, 0, 50), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _current = new() { Color = new SKColor(0x1F, 0x5F, 0xA8), Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _label = new() { Color = new SKColor(0x1A, 0x1C, 0x1E), IsAntialias = true };
    private readonly SKFont _font = new(SKTypeface.Default, 13);
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private ViewerDocument? _doc;
    private ThumbnailGridLayout? _grid;
    private int _currentPage;
    private bool _night, _dark, _rendering, _flinging;

    public ThumbnailView(Context context) : base(context)
    {
        _scroller = new OverScroller(context);
        _gestures = new GestureDetector(context, new Gestures(this));
        Focusable = true;
        ContentDescription = "Page thumbnails";
        _font.Size = 13 * Density;
        _current.StrokeWidth = 3 * Density;
    }

    /// <summary>A page was tapped.</summary>
    public event Action<int>? PageChosen;

    private float Density => Resources?.DisplayMetrics?.Density ?? 1f;

    /// <summary>Shows the grid of <paramref name="document"/> with <paramref name="currentPage"/> marked and in view.</summary>
    public void Show(ViewerDocument document, int currentPage, bool night)
    {
        if (!ReferenceEquals(document, _doc) || night != _night)
        {
            _images.Clear();
            _failed.Clear();
            _doc = document;
            _night = night;
            _grid = new ThumbnailGridLayout(document.Pages, minCellWidth: 104 * Density, gap: 12 * Density, labelHeight: 22 * Density);
        }
        _currentPage = currentPage;
        if (Width > 0)
        {
            _grid!.Resize(Width, Height);
            _grid.Reveal(currentPage);
        }
        Invalidate();
        RequestRender();
    }

    /// <summary>Lets go of the document (another one was opened, or the grid closed for good).</summary>
    public void Release()
    {
        _images.Clear();
        _failed.Clear();
        _doc = null;
        _grid = null;
    }

    public void SetDark(bool dark)
    {
        _dark = dark;
        _label.Color = dark ? new SKColor(0xE6, 0xE8, 0xEB) : new SKColor(0x1A, 0x1C, 0x1E);
        _current.Color = dark ? new SKColor(0x8A, 0xB4, 0xF8) : new SKColor(0x1F, 0x5F, 0xA8);
        _edge.Color = dark ? new SKColor(255, 255, 255, 60) : new SKColor(0, 0, 0, 50);
        Invalidate();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (_grid == null) return;
        _grid.Resize(w, h);
        _grid.Reveal(_currentPage);
        RequestRender();
    }

    protected override void OnPaintSurface(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(_dark ? new SKColor(0x1B, 0x1D, 0x21) : new SKColor(0xE3, 0xE6, 0xEA));
        if (_grid == null) return;
        _paper.Color = _night ? SKColors.Black : SKColors.White;
        foreach (int page in _grid.VisiblePages())
        {
            var p = _grid.Picture(page);
            var dest = new SKRect((float)p.X, (float)p.Y, (float)p.Right, (float)p.Bottom);
            if (_images.TryGet(page, _night, out var image, out _))
                canvas.DrawImage(image, dest, _sampling);
            else
                canvas.DrawRect(dest, _paper);
            canvas.DrawRect(dest, page == _currentPage ? _current : _edge);
            var cell = _grid.Cell(page);
            string label = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
            float w = _font.MeasureText(label);
            canvas.DrawText(label, (float)(cell.X + (cell.Width - w) / 2), (float)(cell.Bottom - 6 * Density), _font, _label);
        }
    }

    // ------------------------------------------------------------------ rendering

    private void RequestRender()
    {
        if (_rendering || _doc == null || _grid == null || Width <= 0) return;
        _ = RenderLoopAsync();
    }

    private async Task RenderLoopAsync()
    {
        _rendering = true;
        try
        {
            while (Visibility == ViewStates.Visible && !_flinging && _doc is { } doc && _grid is { } grid
                   && grid.VisiblePages(grid.ViewHeight / 2).FirstOrDefault(p => !_images.Has(p, _night, grid.Scale(p)) && !_failed.Contains(p)) is int page and > 0)
            {
                double scale = grid.Scale(page);
                bool night = _night;
                ViewerBitmap bitmap;
                try
                {
                    bitmap = await Task.Run(() => doc.RenderPageAsync(page, scale, night));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _failed.Add(page);
                    continue;
                }
                if (!ReferenceEquals(doc, _doc)) break;
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var image = SKImage.FromPixelCopy(info, bitmap.Pixels.AsSpan(), bitmap.Width * 4);
                if (image == null)
                {
                    _failed.Add(page);
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

    // ------------------------------------------------------------------ touch

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _grid == null) return base.OnTouchEvent(e);
        _gestures.OnTouchEvent(e);
        if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel && !_flinging) RequestRender();
        return true;
    }

    public override void ComputeScroll()
    {
        base.ComputeScroll();
        if (_grid == null) return;
        if (_scroller.ComputeScrollOffset())
        {
            _grid.ScrollTo(_scroller.CurrY);
            Invalidate();
            PostInvalidateOnAnimation();
        }
        else if (_flinging)
        {
            _flinging = false;
            RequestRender();
        }
    }

    private sealed class Gestures(ThumbnailView view) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e)
        {
            view._scroller.ForceFinished(true);
            view._flinging = false;
            return true;
        }

        public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
        {
            view._grid!.ScrollBy(distanceY);
            view.Invalidate();
            return true;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
        {
            var g = view._grid!;
            view._scroller.Fling(0, (int)g.ScrollY, 0, (int)-velocityY, 0, 0, 0, (int)g.MaxScrollY);
            view._flinging = true;
            view.PostInvalidateOnAnimation();
            return true;
        }

        public override bool OnSingleTapUp(MotionEvent e)
        {
            int page = view._grid!.PageAt(e.GetX(), e.GetY());
            if (page > 0) view.PageChosen?.Invoke(page);
            return true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _images.Dispose();
            _paper.Dispose();
            _edge.Dispose();
            _current.Dispose();
            _label.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
