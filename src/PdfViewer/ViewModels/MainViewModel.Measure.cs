using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>What the measurement info panel shows, live as the pointer moves.</summary>
public sealed partial class MeasurementInfo : ObservableObject
{
    [ObservableProperty] private string _tool = string.Empty;
    [ObservableProperty] private string _scale = string.Empty;
    [ObservableProperty] private string _segments = string.Empty;
    [ObservableProperty] private string _total = string.Empty;
    [ObservableProperty] private string _area = string.Empty;
    [ObservableProperty] private string _angle = string.Empty;
    [ObservableProperty] private string _cursor = string.Empty;
    [ObservableProperty] private string _snap = string.Empty;
    [ObservableProperty] private string _hint = string.Empty;
}

/// <summary>
/// Tools → Measure: distance, perimeter and area. Points are clicked on a page and snap to the
/// page's drawing (end points, middles, lines); the result shows live in the info panel, and a
/// finished measurement is a measurement annotation (undoable like any other). The scale is the
/// page's own where a viewport (/VP) with a measure covers the first point, else the one the
/// user set for this document (remembered on this computer), else 1 in = 1 in.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The measuring tool in use, or null.</summary>
    [ObservableProperty]
    private PdfMeasurementKind? _activeMeasureTool;

    /// <summary>Points snap to the page's lines and shapes.</summary>
    [ObservableProperty]
    private bool _snapToContent = true;

    /// <summary>The scale set for this document (when its pages give none).</summary>
    [ObservableProperty]
    private MeasureScale _measureScale = MeasureScale.Default;

    public MeasurementInfo Measurement { get; } = new();

    public bool IsMeasuring => ActiveMeasureTool != null;

    /// <summary>Asks for a scale (the current one shown); null when cancelled.</summary>
    public Func<MeasureScale, MeasureScale?>? ShowMeasureScaleFunc { get; set; }

    /// <summary>The colour measurements are drawn in.</summary>
    public const string MeasureColor = "#FF1F4FD8";

    private const double SnapTolerancePixels = 7;

    // The measurement being drawn: its page, the page's frame, its points and the pointer, in default user space.
    private int _measurePage;
    private readonly List<PdfPoint> _measurePoints = new();
    private PdfPoint? _measureCursor;
    private PdfMeasure? _activeMeasure;
    private bool _measureFromDocument;
    private PdfSnapKind _lastSnap;

    /// <summary>The points placed so far, normalized on <see cref="MeasurePageNumber"/> (the preview draws them).</summary>
    public IReadOnlyList<Point> MeasurePreviewPoints => _measurePage >= 1 && Frame(_measurePage) is { } f
        ? _measurePoints.Select(p => NativeDocumentServices.FromPage(p, f.Crop)).ToList()
        : Array.Empty<Point>();

    public int MeasurePageNumber => _measurePage;

    partial void OnActiveMeasureToolChanged(PdfMeasurementKind? value)
    {
        if (value != null)
        {
            ActiveAnnotationTool = null;
            IsPlacingSignature = false;
            IsMarkingRedaction = false;
            ActiveStamp = null;
            IsPanningEnabled = false;
            if (_docService.CurrentFilePath is { Length: > 0 } path && MeasureScaleStore.Load(path) is { } saved) MeasureScale = saved;
        }
        CancelMeasurement();
        OnPropertyChanged(nameof(IsMeasuring));
    }

    partial void OnMeasureScaleChanged(MeasureScale value) => RefreshMeasurement();

    /// <summary>Turns a measuring tool on (Distance, Perimeter or Area), or off when it is the one in use.</summary>
    [RelayCommand]
    public void StartMeasure(string kind)
    {
        if (!IsDocumentLoaded) return;
        if (!Enum.TryParse<PdfMeasurementKind>(kind, true, out var tool)) { ActiveMeasureTool = null; return; }
        if (ActiveMeasureTool == tool) { ActiveMeasureTool = null; StatusText = "Measuring stopped."; return; }
        if (!Permit(DocumentPermissions.CanAnnotate, "adding measurements")) return;
        ActiveMeasureTool = tool;
        StatusText = tool == PdfMeasurementKind.Distance
            ? "Click two points to measure the distance between them (Esc stops)."
            : "Click each corner; double-click or press Enter to finish (Backspace takes back a point, Esc stops).";
    }

    /// <summary>Asks for the scale and units this document is measured in, and remembers them for it.</summary>
    [RelayCommand]
    public void SetMeasureScale()
    {
        if (ShowMeasureScaleFunc?.Invoke(MeasureScale) is not { IsValid: true } scale) return;
        MeasureScale = scale;
        MeasureScaleStore.Save(_docService.CurrentFilePath, scale);
        StatusText = $"Measuring at {scale.ToMeasure().Ratio}.";
    }

    // ------------------------------------------------------------------ the page's frame, drawing and viewports

    private sealed record MeasureFrame(PdfRect Crop, PdfSnapIndex? Snaps, IReadOnlyList<PdfViewport> Viewports);

    private readonly Dictionary<int, Task<MeasureFrame>> _measureFrames = new();
    private byte[]? _measureFramesBytes;

    /// <summary>The page's frame when it has been read (for the current revision), else null.</summary>
    private MeasureFrame? Frame(int page)
    {
        if (!ReferenceEquals(_measureFramesBytes, _docService.CurrentBytes))
        {
            _measureFrames.Clear();
            _measureFramesBytes = _docService.CurrentBytes;
        }
        return _measureFrames.TryGetValue(page, out var t) && t.IsCompletedSuccessfully ? t.Result : null;
    }

    /// <summary>The page's crop box, drawing (for snapping) and viewports, read once per revision.</summary>
    internal async Task<bool> PrepareMeasurePageAsync(int page)
    {
        if (page < 1 || page > Pages.Count) return false;
        Frame(page);
        if (!_measureFrames.TryGetValue(page, out var task)) _measureFrames[page] = task = ReadMeasureFrameAsync(page);
        await task;
        return true;
    }

    private async Task<MeasureFrame> ReadMeasureFrameAsync(int page)
    {
        try
        {
            return await WithEditDocumentAsync((doc, _) =>
            {
                var crop = doc.PageTree.Pages[page - 1].CropBox;
                PdfSnapIndex? snaps = null;
                try { snaps = PdfSnapIndex.Build(doc.GetPageDisplayListAsync(page).AsTask().GetAwaiter().GetResult()); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* a page the engine cannot draw is measured without snapping */ }
                return new MeasureFrame(crop, snaps, PdfViewports.Read(doc, page));
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No engine document (or one it cannot read): the page's size from the view, no snapping or viewports.
            var p = Pages[page - 1];
            return new MeasureFrame(new PdfRect(0, 0, Math.Max(1, p.UnrotatedWidthPt), Math.Max(1, p.UnrotatedHeightPt)), null, Array.Empty<PdfViewport>());
        }
    }

    /// <summary>
    /// Where a point the pointer is at lands once snapped: <paramref name="norm"/> on the page, the
    /// tolerance <paramref name="tolerancePoints"/> in points. The point is unchanged while the
    /// page's drawing is still being read, or when snapping is off (or Shift held, by the window).
    /// </summary>
    public (Point Point, PdfSnapKind Kind) SnapMeasurePoint(int page, Point norm, double tolerancePoints, bool snap = true)
    {
        if (Frame(page) is not { } f)
        {
            _ = PrepareMeasurePageAsync(page);
            return (norm, PdfSnapKind.None);
        }
        if (!snap || !SnapToContent || f.Snaps == null) return (norm, PdfSnapKind.None);
        var hit = f.Snaps.Find(NativeDocumentServices.ToPage(norm, f.Crop), tolerancePoints);
        return hit.Kind == PdfSnapKind.None ? (norm, PdfSnapKind.None) : (NativeDocumentServices.FromPage(hit.Point, f.Crop), hit.Kind);
    }

    /// <summary>The snap tolerance in points for a page shown at <paramref name="displayScale"/> screen pixels per point.</summary>
    public static double SnapTolerance(double displayScale) => SnapTolerancePixels / Math.Max(0.05, displayScale);

    // ------------------------------------------------------------------ drawing a measurement

    /// <summary>The pointer moved to <paramref name="norm"/> (already snapped) on <paramref name="page"/>: the info panel follows.</summary>
    public void MeasureMove(int page, Point norm, PdfSnapKind snapped = PdfSnapKind.None)
    {
        if (ActiveMeasureTool == null || Frame(page) is not { } f) return;
        if (_measurePoints.Count > 0 && page != _measurePage) return;
        _measureCursor = NativeDocumentServices.ToPage(norm, f.Crop);
        _lastSnap = snapped;
        if (_measurePoints.Count == 0) ResolveMeasure(page, _measureCursor.Value);
        RefreshMeasurement();
    }

    /// <summary>Places a point; true when it finished a measurement (a distance's second point).</summary>
    public async Task<bool> MeasureClickAsync(int page, Point norm)
    {
        if (ActiveMeasureTool is not { } tool) return false;
        if (!await PrepareMeasurePageAsync(page) || Frame(page) is not { } f || ActiveMeasureTool != tool) return false;
        if (_measurePoints.Count > 0 && page != _measurePage) CancelMeasurement(); // a new measurement on another page
        var point = NativeDocumentServices.ToPage(norm, f.Crop);
        if (_measurePoints.Count == 0)
        {
            _measurePage = page;
            ResolveMeasure(page, point);
        }
        else if (Distance(_measurePoints[^1], point) < 0.01)
        {
            return false; // the same point twice (the first click of a double-click)
        }
        _measurePoints.Add(point);
        _measureCursor = point;
        OnPropertyChanged(nameof(MeasurePreviewPoints));
        RefreshMeasurement();
        if (tool == PdfMeasurementKind.Distance && _measurePoints.Count == 2) return FinishMeasurement();
        return false;
    }

    /// <summary>Finishes a perimeter or area (double-click or Enter); false when it has too few points.</summary>
    public bool FinishMeasurement()
    {
        if (ActiveMeasureTool is not { } tool || _activeMeasure is not { } measure || Frame(_measurePage) is not { } f) return false;
        int needed = tool == PdfMeasurementKind.Area ? 3 : 2;
        if (_measurePoints.Count < needed)
        {
            StatusText = tool == PdfMeasurementKind.Area ? "An area needs at least three points." : "Place at least two points.";
            return false;
        }
        var points = _measurePoints.ToList();
        var annotation = PdfMeasurements.Create(tool, _measurePage, points, measure);
        var model = new AnnotationModel
        {
            PageNumber = _measurePage,
            Type = AnnotationType.Measurement,
            MeasureKind = tool,
            Measure = measure,
            ColorHex = MeasureColor,
            Opacity = 1,
            StrokeThickness = 1,
            Author = SelectedAnnotationAuthor,
            Title = annotation.Subject ?? "Measurement",
            Contents = annotation.Contents ?? string.Empty,
        };
        var box = NativeDocumentServices.FromPage(annotation.Rect, f.Crop);
        model.SetGeometry(box, Array.Empty<Rect>());
        model.Vertices = points.Select(p => NativeDocumentServices.FromPage(p, f.Crop)).ToList();
        AddAnnotation(model);
        StatusText = $"{tool}: {model.Contents}";
        ResetMeasurementPoints();
        return true;
    }

    /// <summary>Takes back the last point placed (Backspace).</summary>
    public void UndoMeasurePoint()
    {
        if (_measurePoints.Count == 0) return;
        _measurePoints.RemoveAt(_measurePoints.Count - 1);
        OnPropertyChanged(nameof(MeasurePreviewPoints));
        RefreshMeasurement();
    }

    /// <summary>Drops the measurement being drawn; the tool stays on.</summary>
    public void CancelMeasurement()
    {
        ResetMeasurementPoints();
        _measureCursor = null;
        RefreshMeasurement();
    }

    private void ResetMeasurementPoints()
    {
        _measurePoints.Clear();
        _measurePage = 0;
        OnPropertyChanged(nameof(MeasurePreviewPoints));
    }

    /// <summary>The measure for a measurement starting at <paramref name="point"/>: the page's viewport there, else this document's scale.</summary>
    private void ResolveMeasure(int page, PdfPoint point)
    {
        var f = Frame(page);
        if (f != null && PdfViewports.At(f.Viewports, point) is { Measure: { } fromPage })
        {
            _activeMeasure = fromPage;
            _measureFromDocument = true;
        }
        else
        {
            _activeMeasure = MeasureScale.ToMeasure();
            _measureFromDocument = false;
        }
    }

    private static double Distance(PdfPoint a, PdfPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The info panel from the points placed and the pointer: segment lengths, total, area, the last segment's angle and the pointer's x and y.</summary>
    private void RefreshMeasurement()
    {
        var info = Measurement;
        if (ActiveMeasureTool is not { } tool)
        {
            info.Tool = info.Scale = info.Segments = info.Total = info.Area = info.Angle = info.Cursor = info.Snap = info.Hint = string.Empty;
            return;
        }
        var measure = _activeMeasure ?? MeasureScale.ToMeasure();
        info.Tool = tool switch { PdfMeasurementKind.Distance => "Distance", PdfMeasurementKind.Perimeter => "Perimeter", _ => "Area" };
        info.Scale = $"Scale {(string.IsNullOrEmpty(measure.Ratio) ? "set by the document" : measure.Ratio)}" + (_measureFromDocument ? " (from the page)" : string.Empty);

        var points = _measurePoints.ToList();
        if (_measureCursor is { } cursor && (points.Count == 0 || Distance(points[^1], cursor) > 1e-6) && !(tool == PdfMeasurementKind.Distance && points.Count >= 2))
            points.Add(cursor);

        var segments = new StringBuilder();
        for (int i = 1; i < points.Count; i++)
            segments.Append(CultureInfo.InvariantCulture, $"{i}: {measure.FormatDistance(measure.DistanceBetween(points[i - 1], points[i]))}\n");
        if (tool == PdfMeasurementKind.Area && points.Count >= 3)
            segments.Append(CultureInfo.InvariantCulture, $"{points.Count}: {measure.FormatDistance(measure.DistanceBetween(points[^1], points[0]))} (closing)\n");
        info.Segments = segments.ToString().TrimEnd();
        double total = measure.LengthOf(points, closed: tool == PdfMeasurementKind.Area);
        info.Total = points.Count >= 2 ? (tool == PdfMeasurementKind.Distance ? "Distance: " : "Perimeter: ") + measure.FormatDistance(total) : string.Empty;
        info.Area = tool == PdfMeasurementKind.Area && points.Count >= 3 ? "Area: " + measure.FormatArea(measure.AreaOf(points)) : string.Empty;
        info.Angle = points.Count >= 2 ? "Angle: " + measure.FormatAngle(measure.AngleOf(points[^2], points[^1])) : string.Empty;

        if (_measureCursor is { } c && Frame(_measurePage >= 1 ? _measurePage : CurrentPageNumber) is { } f)
        {
            // From the page's top-left corner, as the page is read; a viewport's own origin when it has one.
            var (x, y) = measure.Origin != default ? measure.CoordinatesOf(c) : measure.CoordinatesOf(new PdfPoint(c.X - f.Crop.Left, f.Crop.Bottom - c.Y));
            info.Cursor = $"X: {measure.FormatX(x)}   Y: {measure.FormatY(y)}";
        }
        else
        {
            info.Cursor = string.Empty;
        }
        info.Snap = _lastSnap switch
        {
            PdfSnapKind.Endpoint => "Snapped to an end point",
            PdfSnapKind.Midpoint => "Snapped to a midpoint",
            PdfSnapKind.OnPath => "Snapped to a line",
            _ => string.Empty,
        };
        info.Hint = tool == PdfMeasurementKind.Distance
            ? "Click two points. Shift: no snapping."
            : "Click each point; double-click or Enter finishes. Backspace: undo point. Shift: no snapping.";
    }
}
