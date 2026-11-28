using Android.Content;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using SkiaSharp;

namespace PdfViewer.Android;

/// <summary>
/// The pages of one document in a vertical column: scrolling and flinging, pinch and double-tap
/// zoom, search highlights and text selection. Pages are rendered by the engine's Skia backend on
/// a worker thread, one job at a time, nearest the current page first. Each page has one bitmap of
/// at most <see cref="BaseMaxPixels"/> pixels; at a deeper zoom the view's part of the page is
/// drawn over it in tiles at the zoom's own scale (<see cref="PageTiles"/>), so 8× stays sharp.
/// With an edit session, a tap fills a form field or picks an annotation, the ink tool draws with
/// one finger (two still zoom), the rectangle, ellipse, line and arrow tools draw with a drag, and
/// the note and text tools place a note or a text box with a tap; the engine draws the edits into
/// the pages, which are drawn again after each change (<see cref="Refresh"/>). The chosen
/// annotation shows handles: a drag on it moves it, a corner resizes a box, a point moves one point
/// of a line or polygon. Polygons, connected lines and measurements take points one tap at a time,
/// and redaction marks are drawn over the pages as red boxes.
/// </summary>
internal sealed class PageView : SkiaView
{
    /// <summary>
    /// What one finger does on a page: browse (scroll, tap, long press), draw (ink, rectangle,
    /// ellipse, line, arrow: a drag), or place a note, a text box, a stamp or a callout (a tap; a
    /// callout takes two: the spot it points to, then its text box), or a tap that says where the
    /// chosen annotation goes (Place: moving it). Polygon, connected lines and measuring place
    /// points one at a time (a tap, or a drag that aims with a rubber band and places the point where
    /// the finger lifts; a double tap finishes); Redact marks an area with a drag (a tap on a mark
    /// takes it away).
    /// </summary>
    public enum PageTool { Browse, Ink, Note, Rectangle, Ellipse, Line, Arrow, Text, Stamp, Callout, Place, Polygon, PolyLine, Measure, Redact }

    /// <summary>A tool that draws with a drag of one finger (the page does not scroll under it).</summary>
    public static bool Drags(PageTool tool) => tool is PageTool.Ink or PageTool.Rectangle or PageTool.Ellipse or PageTool.Line or PageTool.Arrow
        || TakesPoints(tool) || tool == PageTool.Redact;

    /// <summary>A tool that places points one at a time: polygon, connected lines, measuring.</summary>
    public static bool TakesPoints(PageTool tool) => tool is PageTool.Polygon or PageTool.PolyLine or PageTool.Measure;

    /// <summary>A tool that works without an edit session: measuring and marking for redaction (a copy is written).</summary>
    private static bool NeedsNoSession(PageTool tool) => tool is PageTool.Measure or PageTool.Redact;

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
    private readonly int _touchSlop;
    private readonly SKPaint _paper = new() { Color = SKColors.White };
    private readonly SKPaint _edge = new() { Color = new SKColor(0, 0, 0, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _hit = new() { Color = new SKColor(255, 200, 0, 90) };
    private readonly SKPaint _currentHit = new() { Color = new SKColor(255, 120, 0, 120) };
    private readonly SKPaint _selected = new() { Color = new SKColor(0x1F, 0x5F, 0xA8, 80) };
    private readonly SKPaint _handle = new() { Color = new SKColor(0x1F, 0x5F, 0xA8), IsAntialias = true };
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private readonly SKPaint _field = new() { Color = new SKColor(0x1F, 0x5F, 0xA8, 28) };
    private readonly SKPaint _picked = new() { Color = new SKColor(0x1F, 0x5F, 0xA8), Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _ink = new() { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
    private readonly SKPaint _markEdge = new() { Color = new SKColor(0xD0, 0x1F, 0x1F), Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _markFill = new() { Color = new SKColor(0xD0, 0x1F, 0x1F, 46) };
    private readonly SKPaint _handleRing = new() { Color = SKColors.White, Style = SKPaintStyle.Stroke, IsAntialias = true };

    // Edits: each change starts a new generation; a page image of an older one is shown only until
    // the page is drawn again. Strokes stay drawn over the page until it is drawn with them.
    private int _generation;
    private readonly Dictionary<(int Page, bool Night), int> _imageGeneration = new();
    private List<ViewerPoint>? _stroke;
    private int _strokePage;
    private readonly List<(int Page, int Generation, List<ViewerPoint> Points, SKColor Color)> _pendingInk = new();
    private SKColor _strokeColor = SKColor.Parse(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Ink));

    /// <summary>
    /// The colour ("#RRGGBB") the stroke or shape outline being drawn is shown in: the colour the
    /// reader chose for the tool. A stroke keeps the colour it was drawn in until the page is drawn with it.
    /// </summary>
    public string StrokeColor
    {
        set
        {
            if (!SKColor.TryParse(value, out var color) || color == _strokeColor) return;
            _strokeColor = color;
            Invalidate();
        }
    }

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
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 0;
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

    /// <summary>The reader's edits of the document shown, or null when it cannot be edited.</summary>
    public ViewerEditSession? Session { get; set; }

    /// <summary>A tap on a fillable form field.</summary>
    public event Action<ViewerFormWidget>? FieldTapped;

    /// <summary>A tap on an annotation (the browse tool).</summary>
    public event Action<ViewerAnnotation>? AnnotationTapped;

    /// <summary>A tap on a page away from fields and annotations.</summary>
    public event Action? EmptyTapped;

    /// <summary>A tap on a link (the browse tool), away from fields and annotations.</summary>
    public event Action<ViewerLink>? LinkTapped;

    // The links of pages in view, read once per page and document (ViewerLinks, shared with the web viewer).
    private readonly Dictionary<int, IReadOnlyList<ViewerLink>> _links = new();
    private readonly HashSet<int> _linksLoading = new();

    private void EnsureLinks(int page)
    {
        var doc = _doc;
        if (doc == null || _links.ContainsKey(page) || !_linksLoading.Add(page)) return;
        _ = LoadLinksAsync(doc, page);
    }

    private async Task LoadLinksAsync(ViewerDocument doc, int page)
    {
        IReadOnlyList<ViewerLink> links;
        try
        {
            links = await Task.Run(() => doc.GetLinksAsync(page));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            links = Array.Empty<ViewerLink>(); // a damaged annotation array: no links on the page
        }
        if (_doc != doc) return;
        _linksLoading.Remove(page);
        _links[page] = links;
    }

    /// <summary>The topmost link at a point of a page (shown-page points), with a finger's slack; null when none or not read yet.</summary>
    private ViewerLink? LinkAt(int page, ViewerPoint point)
    {
        if (_doc == null || _layout == null || page < 1 || page > _doc.Geometry.Count) return null;
        if (!_links.TryGetValue(page, out var links))
        {
            EnsureLinks(page);
            return null;
        }
        return ViewerLinks.At(links, _doc.Geometry[page - 1], point, slack: 8 * Density / _layout.PixelsPerPoint);
    }

    /// <summary>Scrolls to a place a link goes to: the page, and the destination's point at the top of the view.</summary>
    public void GoToLocation(ViewerLocation location)
    {
        if (_layout == null || _doc == null) return;
        _scroller.ForceFinished(true);
        int page = Math.Clamp(location.Page, 1, _doc.PageCount);
        if (location.X == null && location.Y == null)
        {
            _layout.GoTo(page);
        }
        else
        {
            var info = _doc.Pages[page - 1];
            double u = info.Width > 0 && location.X is { } x ? Math.Clamp(x / info.Width, 0, 1) : _layout.AnchorAt(0, 0).U;
            double v = info.Height > 0 ? Math.Clamp((location.Y ?? 0) / info.Height, 0, 1) : 0;
            _layout.Restore(new PageAnchor(page, location.X == null ? 0 : u, v), _layout.Zoom);
        }
        ReportPage();
        Invalidate();
        RequestRender();
    }

    /// <summary>The ink tool drew a stroke: its page and its points on the shown page.</summary>
    public event Action<int, IReadOnlyList<ViewerPoint>>? InkDrawn;

    /// <summary>The note tool was tapped on a page: the page and the point on it.</summary>
    public event Action<int, ViewerPoint>? NoteRequested;

    /// <summary>The text tool was tapped on a page: the page and the point on it.</summary>
    public event Action<int, ViewerPoint>? TextRequested;

    /// <summary>The stamp tool was tapped on a page: the page and the point the stamp is centred on.</summary>
    public event Action<int, ViewerPoint>? StampRequested;

    /// <summary>The callout tool was tapped on a page: the spot it points to, then where its text box goes.</summary>
    public event Action<int, ViewerPoint>? CalloutRequested;

    /// <summary>The place tool was tapped on a page: where the chosen annotation (or its anchor) goes.</summary>
    public event Action<int, ViewerPoint>? PlaceRequested;

    private (int Page, ViewerPoint Point)? _calloutMark;

    /// <summary>The spot a callout being placed points to, marked on the page until its box is placed; null for none.</summary>
    public (int Page, ViewerPoint Point)? CalloutMark
    {
        get => _calloutMark;
        set
        {
            _calloutMark = value;
            Invalidate();
        }
    }

    /// <summary>
    /// A shape tool was dragged: the tool, the page, where the drag started and ended (shown-page
    /// points), and the outline drawn over the page until it is drawn with the shape (drop it with
    /// <see cref="DropPendingInk(IReadOnlyList{ViewerPoint})"/> when no shape is kept).
    /// </summary>
    public event Action<PageTool, int, ViewerPoint, ViewerPoint, IReadOnlyList<ViewerPoint>>? ShapeDrawn;

    private PageTool _tool;

    public PageTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            _stroke = null;
            _aim = null;
            _lastPathUp = null;
            _grab = Grab.None;
            if (value != PageTool.Callout) _calloutMark = null;
            if (value != PageTool.Browse) ClearSelection();
            Invalidate();
        }
    }

    private string? _pickedAnnotation;

    /// <summary>The annotation outlined as chosen, or null.</summary>
    public string? PickedAnnotation
    {
        get => _pickedAnnotation;
        set
        {
            _pickedAnnotation = value;
            Invalidate();
        }
    }

    /// <summary>The text selection, for marking it up.</summary>
    public ViewerTextSelection? Selection => _selection is { IsEmpty: false } s ? s : null;

    /// <summary>
    /// The document has the latest edits: every page is drawn again. Until a page is, its last
    /// image stays (no blank flash), with the strokes not yet in it drawn over.
    /// </summary>
    public void Refresh()
    {
        _generation++;
        _tiles.Clear();
        _tileScale = 0;
        _failed.Clear();
        _textLayouts.Clear();
        Invalidate();
        RequestRender();
    }

    /// <summary>The strokes waiting to be drawn into the pages are dropped (writing them failed).</summary>
    public void DropPendingInk()
    {
        _pendingInk.Clear();
        Invalidate();
    }

    /// <summary>One stroke is dropped (it was not kept as a drawing); the others are still being written.</summary>
    public void DropPendingInk(IReadOnlyList<ViewerPoint> stroke)
    {
        _pendingInk.RemoveAll(s => ReferenceEquals(s.Points, stroke));
        Invalidate();
    }

    /// <summary>Scrolls to an annotation's place.</summary>
    public void GoToAnnotation(ViewerAnnotation a)
    {
        if (_layout == null || _doc == null || a.Page < 1 || a.Page > _doc.Geometry.Count) return;
        _scroller.ForceFinished(true);
        _layout.GoTo(a.Page, Math.Max(1, _doc.Geometry[a.Page - 1].ToShown(a.Box).Y));
        ReportPage();
        Invalidate();
        RequestRender();
    }

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
        _links.Clear();
        _linksLoading.Clear();
        _imageGeneration.Clear();
        _pendingInk.Clear();
        _stroke = null;
        _pickedAnnotation = null;
        _calloutMark = null;
        _path = null;
        _aim = null;
        _marks = Array.Empty<(int, ViewerRect)>();
        _grab = Grab.None;
        _tool = PageTool.Browse;
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

    /// <summary>
    /// How far the view turns every page clockwise (0, 90, 180 or 270), as the desktop's Rotate View:
    /// only the view turns, the document does not change. A new document starts upright.
    /// </summary>
    public int ViewRotation
    {
        get => _layout?.Rotation ?? 0;
        set
        {
            if (_layout == null || Width <= 0 || value == _layout.Rotation) return;
            _scroller.ForceFinished(true);
            _flinging = false;
            _layout.SetRotation(value);
            // Tiles are cut from the upright page and stay valid; the view moved, so the ones it needs are asked for.
            ReportPage();
            Invalidate();
            RequestRender();
        }
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
        _picked.Color = accent;
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

    /// <summary>How far (dp) a tap may miss an annotation and still choose it.</summary>
    private const float TapSlackDp = 8;

    /// <summary>The smallest tap target (dp) of a sticky note: Android's 48 dp touch-target guideline.</summary>
    private const float NoteTargetDp = 48;

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
            // The page and its tiles are drawn upright in the page's frame, which the view's rotation turns.
            var frame = Frame(page);
            canvas.Save();
            canvas.Concat(PageMatrix(page));
            if (_images.TryGet(page, _night, out var image, out _))
                canvas.DrawImage(image, Sk(frame), _sampling);
            else
                canvas.DrawRect(Sk(frame), _paper);
            // Tiles over the whole-page bitmap: those of the current scale, or (while a pinch runs)
            // the last scale's, stretched until the new ones are drawn.
            if (_tileScale > 0 && PageTiles.NeedsTiles(BaseScale(page, scale), scale))
                DrawTiles(canvas, page, sharp: PageImageCache<SKImage>.SameScale(_tileScale, scale));
            canvas.Restore();
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
        DrawEdits(canvas);
        DrawMarks(canvas);
        DrawPath(canvas);
        DrawSelection(canvas);
    }

    /// <summary>The fillable fields tinted (as Acrobat shows them), the chosen annotation outlined, and strokes not yet in the pages.</summary>
    private void DrawEdits(SKCanvas canvas)
    {
        if (Session is not { } session || _layout == null) return;
        var visible = _layout.VisiblePages().ToHashSet();
        foreach (var w in session.Widgets)
        {
            if (w.ReadOnly || !visible.Contains(w.Page)) continue;
            canvas.DrawRect(Sk(_layout.ToView(w.Page, w.Rect)), _field);
        }
        if (_pickedAnnotation != null && session.Store.Find(_pickedAnnotation) is { } a && visible.Contains(a.Page))
        {
            var r = Sk(_layout.ToView(a.Page, session.Document.Geometry[a.Page - 1].ToShown(a.Box)));
            float grow = 3 * Density;
            _picked.StrokeWidth = 2 * Density;
            canvas.DrawRect(new SKRect(r.Left - grow, r.Top - grow, r.Right + grow, r.Bottom + grow), _picked);
            // A callout's anchor: the spot it points to.
            if (a.IsCallout)
            {
                var (ax, ay) = session.Document.Geometry[a.Page - 1].ToShown(a.Callout[0], a.Callout[1]);
                DrawMark(canvas, a.Page, new ViewerPoint(ax, ay));
            }
            if (CanEdit(a)) DrawHandles(canvas, session, a);
        }
        if (_calloutMark is var (markPage, markPoint) && visible.Contains(markPage)) DrawMark(canvas, markPage, markPoint);
        _ink.StrokeWidth = (float)(ViewerEditSession.InkWidth * _layout.PixelsPerPoint);
        foreach (var (page, _, points, color) in _pendingInk)
        {
            _ink.Color = color;
            if (visible.Contains(page)) DrawStroke(canvas, page, points);
        }
        _ink.Color = _strokeColor;
        if (_stroke != null && _tool != PageTool.Redact)
            DrawStroke(canvas, _strokePage, _tool == PageTool.Ink || _stroke.Count < 2 ? _stroke : Outline(_tool, _stroke[0], _stroke[^1]));
    }

    /// <summary>
    /// A shape's outline as one path of shown-page points, to show it while it is dragged and until
    /// the page is drawn with it: a rectangle's corners, an ellipse's points, a line, or a line with
    /// an open arrowhead (nine line widths long, as the engine draws it).
    /// </summary>
    internal static List<ViewerPoint> Outline(PageTool tool, ViewerPoint a, ViewerPoint b)
    {
        switch (tool)
        {
            case PageTool.Rectangle:
                return [a, new(b.X, a.Y), b, new(a.X, b.Y), a];
            case PageTool.Ellipse:
            {
                double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                return Enumerable.Range(0, 49).Select(i => new ViewerPoint(cx + rx * Math.Cos(i * Math.PI / 24), cy + ry * Math.Sin(i * Math.PI / 24))).ToList();
            }
            case PageTool.Arrow:
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6) return [a, b];
                double head = 9 * ViewerEditSession.ShapeWidth, ux = dx / length, uy = dy / length;
                double cos = Math.Cos(Math.PI / 6), sin = Math.Sin(Math.PI / 6);
                // Two strokes back from the tip, 30 degrees either side of the line.
                ViewerPoint Side(double sign) => new(b.X - head * (ux * cos - sign * uy * sin), b.Y - head * (uy * cos + sign * ux * sin));
                return [a, b, Side(1), b, Side(-1)];
            }
            default:
                return [a, b];
        }
    }

    /// <summary>A ring at a point of a page: a callout's anchor.</summary>
    private void DrawMark(SKCanvas canvas, int page, ViewerPoint point)
    {
        var (x, y) = V(page, point);
        _picked.StrokeWidth = 2 * Density;
        canvas.DrawCircle(x, y, 7 * Density, _picked);
        canvas.DrawCircle(x, y, 2.5f * Density, _handle);
    }

    private void DrawStroke(SKCanvas canvas, int page, IReadOnlyList<ViewerPoint> points)
    {
        if (points.Count == 0) return;
        using var path = new SKPath();
        var (x0, y0) = V(page, points[0]);
        path.MoveTo(x0, y0);
        for (int i = 1; i < points.Count; i++)
        {
            var (x, y) = V(page, points[i]);
            path.LineTo(x, y);
        }
        if (points.Count == 1) path.LineTo(x0 + 0.1f, y0);
        canvas.DrawPath(path, _ink);
    }

    /// <summary>A point of a page (shown-page points) in view pixels, turned by the view's rotation.</summary>
    private (float X, float Y) V(int page, ViewerPoint point)
    {
        var (x, y) = _layout!.ToViewPoint(page, point);
        return ((float)x, (float)y);
    }

    private static SKRect Sk(LayoutRect r) => new((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);

    /// <summary>
    /// A page's frame: its shown page, upright, in view pixels at the layout's scale, from its
    /// top-left corner. <see cref="PageMatrix"/> places it in the view.
    /// </summary>
    private LayoutRect Frame(int page)
    {
        var info = _doc!.Pages[page - 1];
        double s = _layout!.PixelsPerPoint;
        return new LayoutRect(0, 0, info.Width * s, info.Height * s);
    }

    /// <summary>The matrix from a page's <see cref="Frame"/> to the view: a move, turned by the view's rotation.</summary>
    private SKMatrix PageMatrix(int page)
    {
        var (ox, oy) = _layout!.ToViewPoint(page, new ViewerPoint(0, 0));
        float x = (float)ox, y = (float)oy;
        return _layout.Rotation switch
        {
            90 => new SKMatrix(0, -1, x, 1, 0, y, 0, 0, 1),
            180 => new SKMatrix(-1, 0, x, 0, -1, y, 0, 0, 1),
            270 => new SKMatrix(0, 1, x, -1, 0, y, 0, 0, 1),
            _ => SKMatrix.CreateTranslation(x, y),
        };
    }

    /// <summary>
    /// The tiles at <paramref name="scale"/> that the view shows of a page, each with its rectangle
    /// in the page's frame: the view is turned back into the frame, so a turned page tiles as an upright one.
    /// </summary>
    private IEnumerable<(PageTile Tile, LayoutRect InFrame)> VisibleTiles(int page, double scale)
    {
        var info = _doc!.Pages[page - 1];
        var frame = Frame(page);
        if (!PageMatrix(page).TryInvert(out var inverse)) yield break;
        var a = inverse.MapPoint(0, 0);
        var b = inverse.MapPoint(Width, Height);
        float x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        // The view, in the frame, is the visible window; the page sits at -window corner.
        var pageInWindow = new LayoutRect(-x0, -y0, frame.Width, frame.Height);
        foreach (var tile in PageTiles.Visible(info, scale, pageInWindow, x1 - x0, y1 - y0))
            yield return (tile, PageTiles.InView(tile, info, scale, frame));
    }

    private void DrawTiles(SKCanvas canvas, int page, bool sharp)
    {
        foreach (var (tile, v) in VisibleTiles(page, _tileScale))
        {
            if (!_tiles.TryGet(page, _night, _tileScale, tile.Column, tile.Row, out var image)) continue;
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
        return (_layout.ToViewPoint(_selection.Page, new ViewerPoint(p.Start.X, p.Start.Y)), _layout.ToViewPoint(_selection.Page, new ViewerPoint(p.End.X, p.End.Y)));
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Starts the render loop unless it runs; it picks the next job itself, so a request while it runs needs nothing more.</summary>
    private void RequestRender()
    {
        if (_rendering || _doc == null) return;
        _ = RenderLoopAsync();
    }

    private readonly record struct Job(ViewerDocument Doc, int Page, double Scale, bool Night, PageTile? Tile, int Generation);

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
                    // Another document was opened meanwhile: go on with its pages (NextJob reads the current one).
                    if (ReferenceEquals(job.Doc, _doc)) break;
                    continue;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _failed.Add(FailKey(job));
                    Problem?.Invoke(ViewerRenderFailure.Describe(job.Page, ex));
                    continue;
                }
                // Drawn for a document that was replaced: dropped, and the loop goes on with the new one
                // (its RequestRender returned early while this loop ran).
                if (!ReferenceEquals(job.Doc, _doc)) continue;
                if (job.Generation != _generation) continue; // drawn before the latest edit
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
                    _imageGeneration[(job.Page, job.Night)] = job.Generation;
                    // The page now has its strokes (a stroke drawn meanwhile is a newer edit, drawn by the next refresh).
                    _pendingInk.RemoveAll(s => s.Page == job.Page && s.Generation < job.Generation);
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
        foreach (int page in visible) EnsureLinks(page);

        foreach (int page in visible)
            if (BaseJob(page) is { } job) return job;

        double cx = Width / 2.0, cy = Height / 2.0;
        foreach (int page in visible)
        {
            if (!PageTiles.NeedsTiles(BaseScale(page, scale), scale)) continue;
            var matrix = PageMatrix(page);
            var tiles = VisibleTiles(page, scale)
                .OrderBy(t =>
                {
                    var centre = matrix.MapPoint((float)(t.InFrame.X + t.InFrame.Width / 2), (float)(t.InFrame.Y + t.InFrame.Height / 2));
                    return (centre.X - cx) * (centre.X - cx) + (centre.Y - cy) * (centre.Y - cy);
                })
                .Select(t => t.Tile);
            foreach (var tile in tiles)
            {
                if (_tiles.Has(page, _night, scale, tile.Column, tile.Row)) continue;
                var job = new Job(doc, page, scale, _night, tile, _generation);
                if (!_failed.Contains(FailKey(job))) return job;
            }
        }

        foreach (int page in _layout.VisiblePages(_layout.ViewHeight).Except(visible).OrderBy(p => Math.Abs(p - current)))
            if (BaseJob(page) is { } job) return job;
        return null;

        Job? BaseJob(int page)
        {
            double bs = BaseScale(page, scale);
            if (_images.Has(page, _night, bs) && _imageGeneration.GetValueOrDefault((page, _night)) == _generation) return null;
            var job = new Job(doc, page, bs, _night, null, _generation);
            return _failed.Contains(FailKey(job)) ? null : job;
        }
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
    private ViewerPoint ToPage(int page, float x, float y) => _layout!.ToPagePoint(page, x, y);

    /// <summary>A tap with the browse, note, text or stamp tool: a field, an annotation, a place for a note, text box or stamp, or nothing.</summary>
    private void Tap(float x, float y)
    {
        if (_layout?.PageAt(x, y) is not var (page, point)) return;
        if (MarkAt(page, point) is int mark)
        {
            RedactionMarkTapped?.Invoke(mark);
            return;
        }
        if (_tool is PageTool.Note or PageTool.Text or PageTool.Stamp or PageTool.Callout or PageTool.Place)
        {
            (_tool switch
            {
                PageTool.Note => NoteRequested,
                PageTool.Text => TextRequested,
                PageTool.Callout => CalloutRequested,
                PageTool.Place => PlaceRequested,
                _ => StampRequested,
            })?.Invoke(page, point);
            return;
        }
        var session = Session;
        if (session?.WidgetAt(page, point) is { } widget)
            FieldTapped?.Invoke(widget);
        // A sticky note's icon is 20 points, small at fit width: its tap target is grown to 48 dp
        // square at any zoom (32 dp, then the 8 dp slack on each side), its box in the PDF unchanged.
        else if (session?.AnnotationAt(page, point, slack: TapSlackDp * Density / _layout.PixelsPerPoint,
                     minNoteSize: (NoteTargetDp - 2 * TapSlackDp) * Density / _layout.PixelsPerPoint) is { } annotation)
            AnnotationTapped?.Invoke(annotation);
        else if (_tool == PageTool.Browse && LinkAt(page, point) is { } link)
            LinkTapped?.Invoke(link);
        else if (session != null)
            EmptyTapped?.Invoke();
    }

    /// <summary>
    /// The ink and shape tools: one finger draws on the page it starts on (a stroke, or a shape
    /// from where it went down to where it is); a second finger ends the drawing unfinished and
    /// zooms instead. True when the event was the drawing's.
    /// </summary>
    private bool DrawInk(MotionEvent e)
    {
        if (TakesPoints(_tool)) return PlacePoint(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                if (_layout!.PageAt(e.GetX(), e.GetY()) is not var (page, point)) return false;
                _scroller.ForceFinished(true);
                _strokePage = page;
                _stroke = [point];
                Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;
            case MotionEventActions.PointerDown:
                _stroke = null;
                Invalidate();
                return false;
            case MotionEventActions.Move when _stroke != null:
                if (_tool != PageTool.Ink)
                {
                    // A shape needs only where the drag started and where it is.
                    _stroke = [_stroke[0], ToPage(_strokePage, e.GetX(), e.GetY())];
                    Invalidate();
                    return true;
                }
                for (int i = 0; i < e.HistorySize; i++) _stroke.Add(ToPage(_strokePage, e.GetHistoricalX(i), e.GetHistoricalY(i)));
                _stroke.Add(ToPage(_strokePage, e.GetX(), e.GetY()));
                Invalidate();
                return true;
            case MotionEventActions.Up when _stroke != null:
                var stroke = _stroke;
                _stroke = null;
                if (_tool == PageTool.Redact)
                {
                    Invalidate();
                    var to = ToPage(_strokePage, e.GetX(), e.GetY());
                    // Hardly a drag: a tap, which takes away the mark under it.
                    double moved = Math.Max(Math.Abs(to.X - stroke[0].X), Math.Abs(to.Y - stroke[0].Y)) * _layout!.PixelsPerPoint;
                    if (moved < TouchSlop)
                    {
                        if (MarkAt(_strokePage, to) is int mark) RedactionMarkTapped?.Invoke(mark);
                    }
                    else
                    {
                        RedactionAreaDrawn?.Invoke(_strokePage, stroke[0], to);
                    }
                    return true;
                }
                if (_tool != PageTool.Ink)
                {
                    var end = ToPage(_strokePage, e.GetX(), e.GetY());
                    var outline = Outline(_tool, stroke[0], end);
                    _pendingInk.Add((_strokePage, _generation, outline, _strokeColor));
                    Invalidate();
                    ShapeDrawn?.Invoke(_tool, _strokePage, stroke[0], end, outline);
                    return true;
                }
                _pendingInk.Add((_strokePage, _generation, stroke, _strokeColor));
                Invalidate();
                InkDrawn?.Invoke(_strokePage, stroke);
                return true;
            case MotionEventActions.Cancel:
                _stroke = null;
                Invalidate();
                return false;
        }
        return _stroke != null;
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
        if (EditDrag(e)) return true;
        _pinch.OnTouchEvent(e);
        bool drags = Drags(_tool);
        if (drags && (Session != null || NeedsNoSession(_tool)) && !_pinch.IsInProgress && e.PointerCount == 1 && DrawInk(e)) return true;
        if (drags && e.ActionMasked is MotionEventActions.PointerDown) DrawInk(e);
        if (!_pinch.IsInProgress && !drags) _gestures.OnTouchEvent(e);
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

    // ------------------------------------------------------------------ redaction marks

    private IReadOnlyList<(int Page, ViewerRect Rect)> _marks = Array.Empty<(int, ViewerRect)>();

    /// <summary>
    /// Areas marked for redaction (shown-page points), drawn as red boxes over the pages; a tap on
    /// one raises <see cref="RedactionMarkTapped"/>. Empty draws nothing.
    /// </summary>
    public IReadOnlyList<(int Page, ViewerRect Rect)> RedactionMarks
    {
        set
        {
            _marks = value;
            Invalidate();
        }
    }

    /// <summary>The redact tool was dragged across an area: its page and two corners (shown-page points).</summary>
    public event Action<int, ViewerPoint, ViewerPoint>? RedactionAreaDrawn;

    /// <summary>A redaction mark was tapped: its index in <see cref="RedactionMarks"/>.</summary>
    public event Action<int>? RedactionMarkTapped;

    /// <summary>The last mark (drawn on top) under a point of a page, with a finger's slack; null when none.</summary>
    private int? MarkAt(int page, ViewerPoint point)
    {
        if (_marks.Count == 0 || _layout == null) return null;
        double slack = TapSlackDp * Density / _layout.PixelsPerPoint;
        for (int i = _marks.Count - 1; i >= 0; i--)
        {
            var (p, r) = _marks[i];
            if (p == page && point.X >= r.X - slack && point.X <= r.X + r.Width + slack && point.Y >= r.Y - slack && point.Y <= r.Y + r.Height + slack)
                return i;
        }
        return null;
    }

    /// <summary>The marks (a red outline over a light red tint) and the area being dragged.</summary>
    private void DrawMarks(SKCanvas canvas)
    {
        if (_layout == null) return;
        _markEdge.StrokeWidth = 2 * Density;
        if (_marks.Count > 0)
        {
            var visible = _layout.VisiblePages().ToHashSet();
            foreach (var (page, rect) in _marks)
            {
                if (!visible.Contains(page)) continue;
                var r = Sk(_layout.ToView(page, rect));
                canvas.DrawRect(r, _markFill);
                canvas.DrawRect(r, _markEdge);
            }
        }
        if (_tool == PageTool.Redact && _stroke is { Count: >= 2 } s)
        {
            var a = s[0];
            var b = s[^1];
            var r = Sk(_layout.ToView(_strokePage, new ViewerRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
            canvas.DrawRect(r, _markFill);
            canvas.DrawRect(r, _markEdge);
        }
    }

    // ------------------------------------------------------------------ points: polygon, connected lines, measuring

    private (int Page, IReadOnlyList<ViewerPoint> Points, bool Closed)? _path;

    /// <summary>
    /// The points placed so far (shown-page points), drawn joined in the tool's colour (closed for a
    /// polygon or an area), with a rubber band to the finger while it is down; null for none.
    /// </summary>
    public (int Page, IReadOnlyList<ViewerPoint> Points, bool Closed)? PathPreview
    {
        get => _path;
        set
        {
            _path = value;
            Invalidate();
        }
    }

    private ViewerPoint? _aim;
    private int _aimPage;
    private float _aimDownX, _aimDownY;
    private (long Time, float X, float Y)? _lastPathUp;

    /// <summary>A point was placed with a points tool (a tap, or where a drag lifted): its page and the point.</summary>
    public event Action<int, ViewerPoint>? PathPointPlaced;

    /// <summary>The finger aims a points tool at a point of a page (while it is down), or lifted (null): for a live result.</summary>
    public event Action<int, ViewerPoint?>? PathAimed;

    /// <summary>A double tap with a points tool: the shape or measurement is finished.</summary>
    public event Action? PathFinished;

    private float TouchSlop => _touchSlop > 0 ? _touchSlop : 8 * Density;

    /// <summary>
    /// A points tool: the finger aims (a rubber band from the last point) and the point goes where it
    /// lifts; a second tap close to the first within the double-tap time finishes instead.
    /// </summary>
    private bool PlacePoint(MotionEvent e)
    {
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                if (_layout!.PageAt(e.GetX(), e.GetY()) is not var (page, point)) return false;
                _scroller.ForceFinished(true);
                Parent?.RequestDisallowInterceptTouchEvent(true);
                // While points are placed, the finger aims on their page.
                _aimPage = _path is { } path && path.Points.Count > 0 ? path.Page : page;
                _aim = _aimPage == page ? point : ToPage(_aimPage, e.GetX(), e.GetY());
                _aimDownX = e.GetX();
                _aimDownY = e.GetY();
                PathAimed?.Invoke(_aimPage, _aim);
                Invalidate();
                return true;
            case MotionEventActions.Move when _aim != null:
                _aim = ToPage(_aimPage, e.GetX(), e.GetY());
                PathAimed?.Invoke(_aimPage, _aim);
                Invalidate();
                return true;
            case MotionEventActions.Up when _aim != null:
            {
                _aim = null;
                PathAimed?.Invoke(_aimPage, null);
                Invalidate();
                float x = e.GetX(), y = e.GetY();
                bool tap = Math.Abs(x - _aimDownX) < TouchSlop && Math.Abs(y - _aimDownY) < TouchSlop;
                double near = 32 * Density;
                if (tap && _lastPathUp is { } last && e.EventTime - last.Time <= ViewConfiguration.DoubleTapTimeout
                    && Math.Abs(x - last.X) <= near && Math.Abs(y - last.Y) <= near)
                {
                    _lastPathUp = null;
                    PathFinished?.Invoke();
                    return true;
                }
                _lastPathUp = tap ? (e.EventTime, x, y) : null;
                PathPointPlaced?.Invoke(_aimPage, ToPage(_aimPage, x, y));
                return true;
            }
            case MotionEventActions.PointerDown or MotionEventActions.Cancel:
                if (_aim != null) PathAimed?.Invoke(_aimPage, null);
                _aim = null;
                Invalidate();
                return false;
        }
        return _aim != null;
    }

    /// <summary>The points placed so far and the rubber band to the finger.</summary>
    private void DrawPath(SKCanvas canvas)
    {
        if (_layout == null || !TakesPoints(_tool)) return;
        var points = _path is { } p ? p.Points : Array.Empty<ViewerPoint>();
        bool closed = _path?.Closed == true;
        int page = _path?.Page ?? _aimPage;
        if (points.Count == 0 && _aim == null) return;
        _ink.Color = _strokeColor;
        _ink.StrokeWidth = (float)Math.Max(ViewerEditSession.ShapeWidth * _layout.PixelsPerPoint, 2 * Density);
        if (points.Count >= 2) DrawStroke(canvas, page, closed && points.Count >= 3 && _aim == null ? [.. points, points[0]] : points);
        foreach (var point in points)
        {
            var (px, py) = V(page, point);
            canvas.DrawCircle(px, py, 4 * Density, _handle);
        }
        if (_aim is not { } aim) return;
        // The rubber band: dashed, from the last point to the finger (and back to the first for a closed shape).
        using var dash = SKPathEffect.CreateDash([6 * Density, 4 * Density], 0);
        _ink.PathEffect = dash;
        if (points.Count > 0)
        {
            List<ViewerPoint> band = [points[^1], aim];
            if (closed && points.Count >= 2) band.Add(points[0]);
            DrawStroke(canvas, page, band);
        }
        _ink.PathEffect = null;
        _picked.StrokeWidth = 2 * Density;
        var (ax, ay) = V(page, aim);
        canvas.DrawCircle(ax, ay, 9 * Density, _picked);
    }

    // ------------------------------------------------------------------ dragging the chosen annotation

    private enum Grab { None, Pending, Body, Corner, Vertex }

    private Grab _grab;
    private int _grabIndex;
    private string? _grabId;
    private float _grabX, _grabY;
    private ViewerPoint _grabStart, _grabNow;

    /// <summary>The chosen annotation was dragged by (dx, dy) shown-page points.</summary>
    public event Action<string, double, double>? AnnotationDragged;

    /// <summary>A corner handle of the chosen rectangle, ellipse, text box or stamp was dragged: its new box (shown-page points).</summary>
    public event Action<string, ViewerRect>? AnnotationResized;

    /// <summary>A point handle of the chosen line, polygon or connected lines was dragged: the point's index and where it goes.</summary>
    public event Action<string, int, ViewerPoint>? VertexDragged;

    /// <summary>Half the touch target of a handle (dp): 48 dp square.</summary>
    private const float HandleGrabDp = 24;

    /// <summary>A rectangle, ellipse, text box (a callout's too) or stamp: it has corner handles.</summary>
    internal static bool Resizable(ViewerAnnotation a) =>
        a.Kind is ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle or ViewerAnnotationKind.FreeText or ViewerAnnotationKind.Stamp;

    /// <summary>The chosen annotation can be dragged here: the browse tool, an edit session that may annotate, not a reply.</summary>
    private bool CanEdit(ViewerAnnotation a) =>
        _tool == PageTool.Browse && Session is { CanAnnotate: true } && a.Kind != ViewerAnnotationKind.Reply && _selection == null;

    /// <summary>The handles of an annotation (shown-page points): its points, or its box's corners (top-left, top-right, bottom-right, bottom-left).</summary>
    private static List<ViewerPoint> HandlesOf(ViewerEditSession session, ViewerAnnotation a)
    {
        var g = session.Document.Geometry[a.Page - 1];
        if (ViewerEditSession.HasVertexHandles(a))
        {
            var list = new List<ViewerPoint>(a.Vertices.Count / 2);
            for (int i = 0; i + 1 < a.Vertices.Count; i += 2)
            {
                var (x, y) = g.ToShown(a.Vertices[i], a.Vertices[i + 1]);
                list.Add(new ViewerPoint(x, y));
            }
            return list;
        }
        if (!Resizable(a)) return [];
        var r = g.ToShown(a.Box);
        return [new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height)];
    }

    /// <summary>
    /// Drags of the chosen annotation with the browse tool: a handle (within 24 dp) resizes it or
    /// moves one of its points at once; a drag that starts on it moves it once it passes the touch
    /// slop (a tap still taps, and a drag elsewhere scrolls). True when the event was the drag's.
    /// </summary>
    private bool EditDrag(MotionEvent e)
    {
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            {
                _grab = Grab.None;
                if (Session is not { } session || _pickedAnnotation == null || session.Store.Find(_pickedAnnotation) is not { } a || !CanEdit(a)) return false;
                if (_layout == null || a.Page < 1 || a.Page > session.Document.Geometry.Count) return false;
                float x = e.GetX(), y = e.GetY();
                double best = HandleGrabDp * Density;
                var handles = HandlesOf(session, a);
                int found = -1;
                for (int i = 0; i < handles.Count; i++)
                {
                    var (hx, hy) = V(a.Page, handles[i]);
                    double d = Math.Sqrt(Math.Pow(hx - x, 2) + Math.Pow(hy - y, 2));
                    if (d <= best)
                    {
                        best = d;
                        found = i;
                    }
                }
                _grabId = a.Id;
                _grabX = x;
                _grabY = y;
                _grabStart = _grabNow = ToPage(a.Page, x, y);
                if (found >= 0)
                {
                    _grab = ViewerEditSession.HasVertexHandles(a) ? Grab.Vertex : Grab.Corner;
                    _grabIndex = found;
                    _scroller.ForceFinished(true);
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    Invalidate();
                    return true;
                }
                var box = session.Document.Geometry[a.Page - 1].ToShown(a.Box);
                double slack = TapSlackDp * Density / _layout.PixelsPerPoint;
                if (_grabStart.X >= box.X - slack && _grabStart.X <= box.X + box.Width + slack && _grabStart.Y >= box.Y - slack && _grabStart.Y <= box.Y + box.Height + slack)
                    _grab = Grab.Pending;
                return false;
            }
            case MotionEventActions.PointerDown:
                if (_grab != Grab.None) Invalidate();
                _grab = Grab.None;
                return false;
            case MotionEventActions.Move when _grab != Grab.None && e.PointerCount == 1:
            {
                if (_grab == Grab.Pending)
                {
                    if (Math.Abs(e.GetX() - _grabX) <= TouchSlop && Math.Abs(e.GetY() - _grabY) <= TouchSlop) return false;
                    // Past the slop on the chosen annotation: a move, not a scroll (the gestures see a cancel).
                    _grab = Grab.Body;
                    using (var cancel = MotionEvent.Obtain(e)!)
                    {
                        cancel.Action = MotionEventActions.Cancel;
                        _gestures.OnTouchEvent(cancel);
                    }
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                }
                if (Session?.Store.Find(_grabId ?? "") is not { } a)
                {
                    _grab = Grab.None;
                    return false;
                }
                _grabNow = ToPage(a.Page, e.GetX(), e.GetY());
                Invalidate();
                return true;
            }
            case MotionEventActions.Up:
            {
                var grab = _grab;
                _grab = Grab.None;
                if (grab is Grab.None or Grab.Pending || _grabId is not { } id || Session is not { } session || session.Store.Find(id) is not { } a) return false;
                Invalidate();
                var now = ToPage(a.Page, e.GetX(), e.GetY());
                double dx = now.X - _grabStart.X, dy = now.Y - _grabStart.Y;
                double ppp = _layout!.PixelsPerPoint;
                if (Math.Abs(dx) * ppp < 1 && Math.Abs(dy) * ppp < 1) return true;
                switch (grab)
                {
                    case Grab.Body:
                        AnnotationDragged?.Invoke(id, dx, dy);
                        break;
                    case Grab.Vertex:
                        VertexDragged?.Invoke(id, _grabIndex, now);
                        break;
                    default:
                        AnnotationResized?.Invoke(id, ResizedBox(session, a, _grabIndex, now));
                        break;
                }
                return true;
            }
            case MotionEventActions.Cancel:
                if (_grab != Grab.None) Invalidate();
                _grab = Grab.None;
                return false;
        }
        return _grab is Grab.Body or Grab.Corner or Grab.Vertex;
    }

    /// <summary>The box with corner <paramref name="corner"/> at <paramref name="to"/> and the opposite corner where it was.</summary>
    private static ViewerRect ResizedBox(ViewerEditSession session, ViewerAnnotation a, int corner, ViewerPoint to)
    {
        var r = session.Document.Geometry[a.Page - 1].ToShown(a.Box);
        var opposite = corner switch
        {
            0 => new ViewerPoint(r.X + r.Width, r.Y + r.Height),
            1 => new ViewerPoint(r.X, r.Y + r.Height),
            2 => new ViewerPoint(r.X, r.Y),
            _ => new ViewerPoint(r.X + r.Width, r.Y),
        };
        return new ViewerRect(Math.Min(opposite.X, to.X), Math.Min(opposite.Y, to.Y), Math.Abs(to.X - opposite.X), Math.Abs(to.Y - opposite.Y));
    }

    /// <summary>The handles of the chosen annotation (filled dots with a white ring), and the drag's outline while one runs.</summary>
    private void DrawHandles(SKCanvas canvas, ViewerEditSession session, ViewerAnnotation a)
    {
        var handles = HandlesOf(session, a);
        if (_grab is Grab.Body or Grab.Corner or Grab.Vertex && _grabId == a.Id)
        {
            double dx = _grabNow.X - _grabStart.X, dy = _grabNow.Y - _grabStart.Y;
            List<ViewerPoint> outline;
            if (_grab == Grab.Vertex)
            {
                handles = handles.Select((h, i) => i == _grabIndex ? _grabNow : h).ToList();
                outline = a.Kind == ViewerAnnotationKind.Polygon ? [.. handles, handles[0]] : handles;
            }
            else
            {
                var r = _grab == Grab.Corner ? ResizedBox(session, a, _grabIndex, _grabNow) : session.Document.Geometry[a.Page - 1].ToShown(a.Box);
                if (_grab == Grab.Body) r = r with { X = r.X + dx, Y = r.Y + dy };
                outline = [new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height), new(r.X, r.Y)];
                handles = _grab == Grab.Body ? handles.Select(h => new ViewerPoint(h.X + dx, h.Y + dy)).ToList() : outline.Take(4).ToList();
            }
            using var path = new SKPath();
            var (ox, oy) = V(a.Page, outline[0]);
            path.MoveTo(ox, oy);
            foreach (var p in outline.Skip(1))
            {
                var (px, py) = V(a.Page, p);
                path.LineTo(px, py);
            }
            using var dash = SKPathEffect.CreateDash([6 * Density, 4 * Density], 0);
            _picked.PathEffect = dash;
            _picked.StrokeWidth = 2 * Density;
            canvas.DrawPath(path, _picked);
            _picked.PathEffect = null;
        }
        _handleRing.StrokeWidth = 2 * Density;
        foreach (var h in handles)
        {
            var (x, y) = V(a.Page, h);
            canvas.DrawCircle(x, y, 6 * Density, _handle);
            canvas.DrawCircle(x, y, 6 * Density, _handleRing);
        }
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

        /// <summary>A tap ends a selection; otherwise it fills a field, picks an annotation or places a note.</summary>
        public override bool OnSingleTapConfirmed(MotionEvent e)
        {
            if (view.HasSelection) view.ClearSelection();
            else view.Tap(e.GetX(), e.GetY());
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
            _field.Dispose();
            _picked.Dispose();
            _ink.Dispose();
            _markEdge.Dispose();
            _markFill.Dispose();
            _handleRing.Dispose();
        }
        base.Dispose(disposing);
    }
}
