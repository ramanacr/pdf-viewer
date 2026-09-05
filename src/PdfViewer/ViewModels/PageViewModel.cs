using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

    public double DisplayWidth => (RotationAngle == 90 || RotationAngle == 270 ? HeightPt : WidthPt) * DisplayScale;
    public double DisplayHeight => (RotationAngle == 90 || RotationAngle == 270 ? WidthPt : HeightPt) * DisplayScale;

    public PageViewModel(int pageNumber, double widthPt, double heightPt)
    {
        PageNumber = pageNumber;
        WidthPt = widthPt > 0 ? widthPt : 612;
        HeightPt = heightPt > 0 ? heightPt : 792;
    }

    public void UpdateScale(double scale)
    {
        DisplayScale = scale;
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public void UpdateRotation(int angle)
    {
        RotationAngle = angle;
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
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

    public async Task LoadImageAsync(AsyncPageRenderer renderer, int dpi, int rotation, CancellationToken ct = default)
    {
        if (RenderedImage != null && RotationAngle == rotation && _renderedDpi == dpi) return;

        IsLoading = true;
        try
        {
            var bitmap = await renderer.GetOrRenderPageAsync(PageNumber, dpi, rotation, ct);
            if (!ct.IsCancellationRequested && bitmap != null)
            {
                RenderedImage = bitmap;
                _renderedDpi = dpi;
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
        RenderedImage = null;
        _renderedDpi = 0;
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
            if (!ct.IsCancellationRequested)
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
