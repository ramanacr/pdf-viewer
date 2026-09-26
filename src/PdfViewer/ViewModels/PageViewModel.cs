using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// ViewModel representing a single page in the PDF document viewport.
/// </summary>
public partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private int _pageNumber;

    [ObservableProperty]
    private double _widthPt = 612;

    [ObservableProperty]
    private double _heightPt = 792;

    [ObservableProperty]
    private BitmapSource? _renderedImage;

    /// <summary>
    /// Resolution-independent page drawing from the vector engine. When set, the page view shows
    /// it instead of <see cref="RenderedImage"/> and zooming never re-renders a bitmap.
    /// </summary>
    [ObservableProperty]
    private ImageSource? _vectorSurface;

    /// <summary>What the page view displays: the live vector surface, else the bitmap.</summary>
    public ImageSource? PageSurface => VectorSurface ?? RenderedImage;

    /// <summary>True once the page has something to show, vector or bitmap.</summary>
    public bool HasSurface => VectorSurface != null || RenderedImage != null;

    partial void OnRenderedImageChanged(BitmapSource? value)
    {
        OnPropertyChanged(nameof(PageSurface));
        OnPropertyChanged(nameof(HasSurface));
    }

    partial void OnVectorSurfaceChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(PageSurface));
        OnPropertyChanged(nameof(HasSurface));
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _displayScale = 1.0;

    [ObservableProperty]
    private int _rotationAngle = 0;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<SearchMatch> MatchesOnPage { get; } = new();
    public ObservableCollection<AnnotationModel> AnnotationsOnPage { get; } = new();
    public List<PageTextSegment> TextSegments { get; } = new();
    public ObservableCollection<PageTextSegment> SelectedSegments { get; } = new();

    [ObservableProperty]
    private bool _isTextExtracted;

    [ObservableProperty]
    private bool _isExtractingText;

    /// <summary>
    /// The page's size in points as it is currently oriented, with zoom left out. Anything
    /// that has to be a fixed size on the page rather than a fixed size on screen - a sticky
    /// note icon, for one - measures against this instead of against DisplayWidth.
    /// </summary>
    public double OrientedWidthPt => RotationAngle == 90 || RotationAngle == 270 ? HeightPt : WidthPt;
    public double OrientedHeightPt => RotationAngle == 90 || RotationAngle == 270 ? WidthPt : HeightPt;

    public double DisplayWidth => OrientedWidthPt * DisplayScale;
    public double DisplayHeight => OrientedHeightPt * DisplayScale;

    /// <summary>
    /// The page's own /Rotate entry, in degrees. Fixed for the life of the document, unlike
    /// <see cref="RotationAngle"/>, which is whatever the reader has turned the view to.
    /// </summary>
    public int IntrinsicRotation { get; init; }

    /// <summary>
    /// The page in unrotated space, scaled for the screen.
    ///
    /// Text boxes and annotation rectangles are all normalized against the page as the PDF
    /// stores it, before any rotation. Everything drawn over the page is laid out in this
    /// space and then turned by <see cref="OverlayRotationAngle"/> to sit on the rendered
    /// bitmap - which is why a highlight stays over its word however the page is turned.
    /// </summary>
    public double UnrotatedWidthPt => IntrinsicRotation == 90 || IntrinsicRotation == 270 ? HeightPt : WidthPt;
    public double UnrotatedHeightPt => IntrinsicRotation == 90 || IntrinsicRotation == 270 ? WidthPt : HeightPt;

    public double UnrotatedDisplayWidth => UnrotatedWidthPt * DisplayScale;
    public double UnrotatedDisplayHeight => UnrotatedHeightPt * DisplayScale;

    /// <summary>
    /// How far the overlay layers have to turn to line up with the page as it is drawn: the
    /// page's own rotation plus whatever the reader has added on top of it.
    /// </summary>
    public double OverlayRotationAngle => (IntrinsicRotation + RotationAngle) % 360;

    /// <summary>
    /// Moves a normalized point from unrotated page space into the page as it appears on
    /// screen, both measured 0..1 from the top-left of their own frame.
    ///
    /// This is the same turn the overlay layers get from their LayoutTransform, in arithmetic
    /// form, for the callers that need a number rather than a laid-out element - scrolling to
    /// a search match, for one, which otherwise aimed at where the match would have been if
    /// the page had never been turned.
    /// </summary>
    public (double X, double Y) ToDisplayNormalized(double x, double y)
    {
        // Anything that is not a quarter turn is not something this viewer can produce, and
        // guessing at one would move the point somewhere arbitrary. Leave it where it is.
        int quarterTurns = (int)Math.Round(OverlayRotationAngle / 90.0);
        if (Math.Abs(OverlayRotationAngle - quarterTurns * 90.0) > 0.01) return (x, y);

        return ((quarterTurns % 4 + 4) % 4) switch
        {
            1 => (1.0 - y, x),          // 90 clockwise: the top-left corner swings to the top-right
            2 => (1.0 - x, 1.0 - y),
            3 => (y, 1.0 - x),
            _ => (x, y)
        };
    }

    /// <summary>
    /// The document this page belongs to.
    ///
    /// Exists because a ContextMenu is hosted in its own popup, not in the window's visual
    /// tree, so a binding that walks up to an ancestor Window from inside one silently
    /// resolves to nothing. The page's context menu commands were bound that way and had
    /// therefore never worked: the menu opened and every item did nothing. Reaching the
    /// document through the page is the binding that actually connects.
    /// </summary>
    public MainViewModel? Owner { get; set; }

    public PageViewModel(int pageNumber, double widthPt, double heightPt)
    {
        PageNumber = pageNumber;
        WidthPt = widthPt > 0 ? widthPt : 612;
        HeightPt = heightPt > 0 ? heightPt : 792;
    }

    public void UpdateScale(double scale)
    {
        if (Math.Abs(DisplayScale - scale) > 1e-9)
            ClearDetail(); // the tile belongs to the previous zoom
        DisplayScale = scale;
        RaiseGeometryChanged();
    }

    public void UpdateRotation(int angle)
    {
        if (RotationAngle != angle)
            ClearDetail();
        RotationAngle = angle;
        RaiseGeometryChanged();
    }

    // ------------------------------------------------------------------ viewport detail tile

    /// <summary>
    /// The visible part of the page rendered at exact device resolution when the page bitmap is
    /// coarser than the screen (zoomed in). Positioned by <see cref="DetailRect"/> in page DIPs.
    /// </summary>
    [ObservableProperty]
    private BitmapSource? _detailImage;

    [ObservableProperty]
    private Rect _detailRect;

    private CancellationTokenSource? _detailCts;
    private (double PixelsPerPoint, int Rotation, bool Night) _detailKey;
    private Rect _detailPixels;

    /// <summary>Largest detail tile side in device pixels (memory bound: 64 MB at 4096²).</summary>
    public const double MaxDetailPixels = 4096;

    private void SetDetail(BitmapSource tile, (double, int, bool) key, Rect pixels, double devicePixelsPerDip)
    {
        DetailImage = tile;
        DetailRect = new Rect(pixels.X / devicePixelsPerDip, pixels.Y / devicePixelsPerDip, pixels.Width / devicePixelsPerDip, pixels.Height / devicePixelsPerDip);
        _detailKey = key;
        _detailPixels = pixels;
    }

    public void ClearDetail()
    {
        _detailCts?.Cancel();
        _detailCts = null;
        DetailImage = null;
        _detailPixels = Rect.Empty;
    }

    /// <summary>
    /// Ensures a detail tile covers <paramref name="visibleDips"/> (the page's visible rectangle in
    /// its own DIPs) at the current zoom. Renders visible area plus a margin so small scrolls reuse
    /// the tile; does nothing when the page bitmap is already sharp enough.
    /// </summary>
    public async Task UpdateDetailAsync(IPdfDocumentService service, Rect visibleDips, double devicePixelsPerDip, int rotation, bool nightMode)
    {
        if (VectorSurface != null || visibleDips.IsEmpty || visibleDips.Width < 1 || visibleDips.Height < 1)
        {
            ClearDetail();
            return;
        }

        double pxPerPt = DisplayScale * devicePixelsPerDip;
        var baseImage = RenderedImage;
        double basePxPerPt = baseImage != null ? baseImage.PixelWidth / Math.Max(1, OrientedWidthPt) : 0;
        if (baseImage != null && basePxPerPt >= pxPerPt * 0.95)
        {
            ClearDetail(); // the page bitmap already matches the screen
            return;
        }

        double fullW = DisplayWidth * devicePixelsPerDip, fullH = DisplayHeight * devicePixelsPerDip;
        var visiblePx = new Rect(visibleDips.X * devicePixelsPerDip, visibleDips.Y * devicePixelsPerDip,
            visibleDips.Width * devicePixelsPerDip, visibleDips.Height * devicePixelsPerDip);

        var key = (Math.Round(pxPerPt, 4), rotation, nightMode);
        if (DetailImage != null && _detailKey == key && _detailPixels.Contains(visiblePx))
            return;

        // Visible area plus half a viewport on every side, clamped to the page and the memory bound.
        double mx = Math.Min(visiblePx.Width / 2, Math.Max(0, (MaxDetailPixels - visiblePx.Width) / 2));
        double my = Math.Min(visiblePx.Height / 2, Math.Max(0, (MaxDetailPixels - visiblePx.Height) / 2));
        double x0 = Math.Max(0, Math.Floor(visiblePx.X - mx)), y0 = Math.Max(0, Math.Floor(visiblePx.Y - my));
        double x1 = Math.Min(fullW, Math.Ceiling(visiblePx.Right + mx)), y1 = Math.Min(fullH, Math.Ceiling(visiblePx.Bottom + my));
        int w = (int)Math.Min(MaxDetailPixels, x1 - x0), h = (int)Math.Min(MaxDetailPixels, y1 - y0);
        if (w <= 0 || h <= 0)
            return;

        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        try
        {
            // Visible area first: the margin quadruples the pixels, and the user is waiting for
            // what is on screen. The margin tile follows and replaces it.
            double vx0 = Math.Max(0, Math.Floor(visiblePx.X)), vy0 = Math.Max(0, Math.Floor(visiblePx.Y));
            int vw = (int)Math.Min(MaxDetailPixels, Math.Min(fullW, Math.Ceiling(visiblePx.Right)) - vx0);
            int vh = (int)Math.Min(MaxDetailPixels, Math.Min(fullH, Math.Ceiling(visiblePx.Bottom)) - vy0);
            if (vw > 0 && vh > 0 && (double)w * h > 1.5 * vw * vh && !(DetailImage != null && _detailKey == key))
            {
                var visibleTile = await service.RenderPageRegionAsync(PageNumber, rotation, pxPerPt, (int)vx0, (int)vy0, vw, vh, nightMode, cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                if (visibleTile != null)
                    SetDetail(visibleTile, key, new Rect(vx0, vy0, vw, vh), devicePixelsPerDip);
            }

            var tile = await service.RenderPageRegionAsync(PageNumber, rotation, pxPerPt, (int)x0, (int)y0, w, h, nightMode, cts.Token);
            if (cts.IsCancellationRequested || tile == null)
                return;
            SetDetail(tile, key, new Rect(x0, y0, w, h), devicePixelsPerDip);
        }
        catch (OperationCanceledException) { }
        catch (PdfEngine.Exceptions.PdfSecurityPolicyException) { }
        catch (Exception) when (!cts.IsCancellationRequested)
        {
            // A failed tile leaves the (coarser) page bitmap visible; never an empty page.
        }
    }

    private void RaiseGeometryChanged()
    {
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
        OnPropertyChanged(nameof(UnrotatedDisplayWidth));
        OnPropertyChanged(nameof(UnrotatedDisplayHeight));
        OnPropertyChanged(nameof(OverlayRotationAngle));
    }

    private int _renderedDpi;

    /// <summary>
    /// Why this page could not be rendered, or empty when it rendered fine. Bindable so the
    /// page surface can show the reason in place of a blank rectangle.
    /// </summary>
    [ObservableProperty]
    private string _renderErrorMessage = string.Empty;

    /// <summary>
    /// Raised when a render is refused by the active security policy. MainViewModel uses
    /// this to report the refusal once per document rather than leaving a silent blank page.
    /// </summary>
    public Action<int, string>? RenderRefused { get; set; }

    private bool _renderedNightMode;
    private int _surfaceRotation = -1;
    private bool _surfaceNightMode;

    public async Task LoadImageAsync(
        AsyncPageRenderer renderer, int dpi, int rotation, bool nightMode = false, CancellationToken ct = default)
    {
        // Live vector surface first (night mode gets a colour-inverted drawing); bitmap otherwise.
        {
            // A surface does not depend on zoom: a DPI change is not a reason to rebuild it.
            if (VectorSurface != null && _surfaceRotation == rotation && _surfaceNightMode == nightMode)
                return;

            IsLoading = true;
            try
            {
                var surface = await renderer.GetVectorSurfaceAsync(PageNumber, rotation, ct, nightMode);
                if (ct.IsCancellationRequested)
                    return;
                if (surface != null)
                {
                    VectorSurface = surface;
                    _surfaceRotation = rotation;
                    _surfaceNightMode = nightMode;
                    // Release the bitmap: the surface replaces it at every zoom.
                    RenderedImage = null;
                    _renderedDpi = 0;
                    RenderErrorMessage = string.Empty;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Any surface failure falls through to the bitmap path, which reports errors.
            }
            finally
            {
                IsLoading = false;
            }
        }

        VectorSurface = null;
        _surfaceRotation = -1;

        if (RenderedImage != null && RotationAngle == rotation && _renderedDpi == dpi
            && _renderedNightMode == nightMode)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var bitmap = await renderer.GetOrRenderPageAsync(PageNumber, dpi, rotation, ct);
            if (!ct.IsCancellationRequested && bitmap != null)
            {
                // Inverted here rather than in the renderer, so the shared page cache keeps
                // one copy of the page that both modes are derived from.
                RenderedImage = nightMode ? NightModeImage.Invert(bitmap) : bitmap;
                _renderedDpi = dpi;
                _renderedNightMode = nightMode;
                RenderErrorMessage = string.Empty;
            }
        }
        catch (OperationCanceledException) { }
        catch (PdfEngine.Exceptions.PdfSecurityPolicyException ex)
        {
            // Deliberately NOT swallowed: a policy refusal must be visible. Silently
            // leaving the page blank would look identical to a rendering bug.
            RenderErrorMessage = ex.Message;
            RenderRefused?.Invoke(PageNumber, ex.Message);
        }
        catch (Exception) { }
        finally
        {
            IsLoading = false;
        }
    }

    public void UnloadImage()
    {
        ClearDetail();
        VectorSurface = null;
        _surfaceRotation = -1;
        RenderedImage = null;
        _renderedDpi = 0;
        _renderedNightMode = false;
        IsLoading = false;
    }

    private Task? _extractTextTask;
    private readonly object _textExtractLock = new();

    public Task LoadTextSegmentsAsync(IPdfDocumentService docService, CancellationToken ct = default)
    {
        if (IsTextExtracted) return Task.CompletedTask;

        lock (_textExtractLock)
        {
            if (IsTextExtracted) return Task.CompletedTask;
            if (_extractTextTask != null && !_extractTextTask.IsCompleted)
            {
                return _extractTextTask;
            }

            _extractTextTask = ExtractInternalAsync(docService, ct);
            return _extractTextTask;
        }
    }

    private async Task ExtractInternalAsync(IPdfDocumentService docService, CancellationToken ct)
    {
        IsExtractingText = true;
        try
        {
            var segments = await docService.ExtractPageTextSegmentsAsync(PageNumber, ct);
            // A result that arrives after the page was already populated must not replace it.
            if (!ct.IsCancellationRequested && !IsTextExtracted)
            {
                TextSegments.Clear();
                TextSegments.AddRange(segments);
                IsTextExtracted = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            IsExtractingText = false;
        }
    }

    public void ClearTextSelection()
    {
        foreach (var seg in SelectedSegments)
        {
            seg.IsSelected = false;
        }
        SelectedSegments.Clear();
    }

    public void SelectAllText()
    {
        ClearTextSelection();
        foreach (var seg in TextSegments)
        {
            seg.IsSelected = true;
            SelectedSegments.Add(seg);
        }
    }

    public PageTextSegment? FindSegmentAt(Point normPoint)
    {
        foreach (var seg in TextSegments)
        {
            if (normPoint.X >= seg.X && normPoint.X <= seg.X + seg.Width &&
                normPoint.Y >= seg.Y && normPoint.Y <= seg.Y + seg.Height)
            {
                return seg;
            }
        }
        return null;
    }

    public PageTextSegment? FindClosestSegment(Point normPoint, double maxDistance = 0.06)
    {
        var exact = FindSegmentAt(normPoint);
        if (exact != null) return exact;

        PageTextSegment? closest = null;
        double minSqDist = double.MaxValue;

        foreach (var seg in TextSegments)
        {
            double centerX = seg.X + seg.Width / 2.0;
            double centerY = seg.Y + seg.Height / 2.0;
            double dx = centerX - normPoint.X;
            double dy = centerY - normPoint.Y;
            double sqDist = dx * dx + dy * dy;

            if (sqDist < minSqDist && sqDist <= maxDistance * maxDistance)
            {
                minSqDist = sqDist;
                closest = seg;
            }
        }

        return closest;
    }

    public void SelectRange(Point normStart, Point normEnd)
    {
        var startSeg = FindClosestSegment(normStart);
        var endSeg = FindClosestSegment(normEnd);

        ClearTextSelection();

        if (startSeg != null && endSeg != null)
        {
            int minIdx = Math.Min(startSeg.SegmentIndex, endSeg.SegmentIndex);
            int maxIdx = Math.Max(startSeg.SegmentIndex, endSeg.SegmentIndex);

            for (int i = minIdx; i <= maxIdx && i < TextSegments.Count; i++)
            {
                var seg = TextSegments[i];
                seg.IsSelected = true;
                SelectedSegments.Add(seg);
            }
        }
        else
        {
            double left = Math.Min(normStart.X, normEnd.X);
            double top = Math.Min(normStart.Y, normEnd.Y);
            double width = Math.Abs(normEnd.X - normStart.X);
            double height = Math.Abs(normEnd.Y - normStart.Y);
            var box = new Rect(left, top, width, height);

            foreach (var seg in TextSegments)
            {
                if (box.IntersectsWith(seg.NormalizedBounds))
                {
                    seg.IsSelected = true;
                    SelectedSegments.Add(seg);
                }
            }
        }
    }

    public void SelectWordAt(Point normPoint)
    {
        ClearTextSelection();
        var seg = FindClosestSegment(normPoint);
        if (seg != null)
        {
            seg.IsSelected = true;
            SelectedSegments.Add(seg);
        }
    }

    public string GetSelectedText()
    {
        if (SelectedSegments.Count == 0) return string.Empty;

        var sorted = SelectedSegments.OrderBy(s => s.SegmentIndex).ToList();
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < sorted.Count; i++)
        {
            var cur = sorted[i];
            if (i > 0)
            {
                var prev = sorted[i - 1];
                double yDiff = cur.Y - prev.Y;
                double lineThreshold = Math.Min(cur.Height, prev.Height) * 0.6;
                if (lineThreshold <= 0) lineThreshold = 0.01;

                if (yDiff > lineThreshold)
                {
                    sb.AppendLine();
                }
                else if (!prev.Text.EndsWith(" ") && !cur.Text.StartsWith(" "))
                {
                    sb.Append(' ');
                }
            }
            sb.Append(cur.Text);
        }

        return sb.ToString();
    }
}
