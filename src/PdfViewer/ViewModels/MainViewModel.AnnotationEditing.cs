using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>A part of a selected annotation that the pointer can drag.</summary>
public enum AnnotationHandle
{
    None,
    /// <summary>The annotation itself: moves it.</summary>
    Body,
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
    /// <summary>Turns a stamp about its centre.</summary>
    Rotate,
    /// <summary>One point of a measurement or a drawing.</summary>
    Vertex,
}

/// <summary>
/// The annotation edit layer: a placed annotation is selected, then moved (dragged, or nudged
/// with the arrow keys), resized by its corners and edges, turned (stamps), or changed point by
/// point (measurements, and drawings with few points). The geometry is worked out here, in the
/// page's points, so it is the same at any zoom and page rotation; the window only passes the
/// pointer. A drag changes the annotation as it goes and is one step in the undo history when it
/// ends. A measurement is measured again as its points move, so its result and its box stay
/// right. Once a loaded annotation is changed, the page leaves out the file's drawing of it and
/// the overlay draws it until it is saved.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The annotation with edit handles, or null.</summary>
    [ObservableProperty]
    private AnnotationModel? _selectedAnnotation;

    /// <summary>Points a nudge moves; Shift moves this many times as far.</summary>
    public const double NudgePoints = 1, LargeNudgeFactor = 10;

    /// <summary>A drawing with at most this many points gets a handle on each point; a freehand one is sized as a whole.</summary>
    public const int MaxInkVertexHandles = 24;

    /// <summary>The smallest box a resize leaves, in points.</summary>
    private const double MinEditSize = 4;

    partial void OnSelectedAnnotationChanged(AnnotationModel? oldValue, AnnotationModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        CancelAnnotationDrag();
        if (newValue != null)
        {
            _ = PrepareMeasurePageAsync(newValue.PageNumber); // the page's frame and drawing, for points and snapping
            StatusText = $"{AnnotationTypeName(newValue.Type)} selected: drag to move, arrow keys nudge (Shift: more), Delete removes, Enter edits, Tab goes to the next.";
        }
    }

    /// <summary>Opens the comment editor for an annotation (set by the window; a double-click or Enter on the selected one).</summary>
    public Action<AnnotationModel>? EditAnnotationAction { get; set; }

    /// <summary>Opens the comment editor for the selected annotation.</summary>
    public void EditSelectedAnnotation()
    {
        if (SelectedAnnotation is { } a && AllAnnotations.Contains(a)) EditAnnotationAction?.Invoke(a);
    }

    /// <summary>Selects an annotation (null clears the selection).</summary>
    public void SelectAnnotation(AnnotationModel? annot) =>
        SelectedAnnotation = annot != null && AllAnnotations.Contains(annot) ? annot : null;

    // ------------------------------------------------------------------ what each kind offers

    /// <summary>Corner and edge handles: everything but notes (a fixed-size icon), measurements (moved point by point) and multi-line markup.</summary>
    public static bool CanResizeAnnotation(AnnotationModel a) =>
        a.Type is not (AnnotationType.Note or AnnotationType.Measurement) && !a.HasQuads && !HasVertexHandles(a);

    public static bool CanRotateAnnotation(AnnotationModel a) => a.Type == AnnotationType.Stamp;

    /// <summary>A resize keeps the shape unless Shift frees it: stamps do (a picture always does); other kinds keep it only with Shift.</summary>
    public static bool KeepsProportions(AnnotationModel a, bool shift) => a.Type switch
    {
        AnnotationType.Stamp when IsPictureStamp(a) => true,
        AnnotationType.Stamp => !shift || !IsQuarterTurn(a.StampRotation),
        _ => shift,
    };

    private static bool IsPictureStamp(AnnotationModel a) => a.StampImage != null || a.StampPdf != null || a.StampName == "Custom";

    private static bool IsQuarterTurn(double degrees)
    {
        double d = PdfStamps.NormalizeDegrees(degrees) % 90;
        return d < 1e-6 || 90 - d < 1e-6;
    }

    private static bool IsSideways(double degrees)
    {
        double d = PdfStamps.NormalizeDegrees(degrees);
        return Math.Abs(d - 90) < 1e-6 || Math.Abs(d - 270) < 1e-6;
    }

    public static bool HasVertexHandles(AnnotationModel a) =>
        a.Type == AnnotationType.Measurement && a.Vertices.Count >= 2
        || a.Type == AnnotationType.Ink && a.InkStrokes.Sum(s => s.Count) is > 1 and <= MaxInkVertexHandles;

    /// <summary>The points that get handles, normalized on the page: a measurement's vertices, or every point of a drawing in stroke order.</summary>
    public static IReadOnlyList<Point> VertexHandles(AnnotationModel a) =>
        !HasVertexHandles(a) ? Array.Empty<Point>()
        : a.Type == AnnotationType.Measurement ? a.Vertices.ToList()
        : a.InkStrokes.SelectMany(s => s).ToList();

    /// <summary>The stamp's own (turned) outline, normalized on the page, corner by corner.</summary>
    public IReadOnlyList<Point> StampOutline(AnnotationModel a)
    {
        if (a.Type != AnnotationType.Stamp) return Array.Empty<Point>();
        var crop = CropOf(a.PageNumber);
        var size = StampSizeOf(a, crop);
        var c = ToPage(new Point(a.X + a.Width / 2, a.Y + a.Height / 2), crop);
        double r = a.StampRotation * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
        return new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
            .Select(k => new PdfPoint(c.X + k.Item1 * size.Width / 2 * cos - k.Item2 * size.Height / 2 * sin, c.Y + k.Item1 * size.Width / 2 * sin + k.Item2 * size.Height / 2 * cos))
            .Select(p => FromPage(p, crop))
            .ToList();
    }

    // ------------------------------------------------------------------ dragging a handle

    private sealed record DragStart(AnnotationModel Annotation, AnnotationHandle Handle, int Vertex, Point Pointer, PdfRect Crop,
        Rect Box, List<Rect> Quads, List<List<Point>> Ink, List<Point> Vertices, double Rotation, Size StampSize, string Contents);

    private DragStart? _drag;

    /// <summary>A handle of the selected annotation is being dragged.</summary>
    public bool IsDraggingAnnotation => _drag != null;

    /// <summary>
    /// Starts dragging <paramref name="handle"/> (for a vertex, point <paramref name="vertex"/>)
    /// of the selected annotation from <paramref name="pointer"/>, normalized on its page. False
    /// when there is nothing to drag or the document does not allow changing comments.
    /// </summary>
    public bool BeginAnnotationDrag(AnnotationHandle handle, Point pointer, int vertex = -1)
    {
        if (SelectedAnnotation is not { } a || handle == AnnotationHandle.None || !AllAnnotations.Contains(a)) return false;
        if (handle is AnnotationHandle.Rotate && !CanRotateAnnotation(a)) return false;
        if (handle is AnnotationHandle.Vertex && (vertex < 0 || vertex >= VertexHandles(a).Count)) return false;
        if (handle is not (AnnotationHandle.Body or AnnotationHandle.Rotate or AnnotationHandle.Vertex) && !CanResizeAnnotation(a)) return false;
        if (!Permit(DocumentPermissions.CanAnnotate, "changing comments")) return false;
        var crop = CropOf(a.PageNumber);
        _drag = new DragStart(a, handle, vertex, pointer, crop, new Rect(a.X, a.Y, a.Width, a.Height), a.Quads.ToList(),
            a.InkStrokes.Select(s => s.ToList()).ToList(), a.Vertices.ToList(), a.StampRotation, StampSizeOf(a, crop), a.Contents);
        return true;
    }

    /// <summary>
    /// The pointer moved to <paramref name="pointer"/> (normalized on the page): the annotation
    /// follows. Shift keeps or frees the proportions, snaps a turn to 15 degrees, and places a
    /// measurement's point without snapping. Returns what a measurement's point snapped to.
    /// </summary>
    public PdfSnapKind UpdateAnnotationDrag(Point pointer, bool shift, double snapTolerancePoints = 0)
    {
        if (_drag is not { } d || !AllAnnotations.Contains(d.Annotation)) return PdfSnapKind.None;
        var a = d.Annotation;
        var delta = pointer - d.Pointer;
        switch (d.Handle)
        {
            case AnnotationHandle.Body:
                Translate(d, ClampMove(d.Box, delta));
                return PdfSnapKind.None;
            case AnnotationHandle.Rotate:
                Turn(d, pointer, shift);
                return PdfSnapKind.None;
            case AnnotationHandle.Vertex:
            {
                var target = VertexStart(d) + delta;
                var kind = PdfSnapKind.None;
                if (a.Type == AnnotationType.Measurement && snapTolerancePoints > 0)
                    (target, kind) = SnapMeasurePoint(a.PageNumber, target, snapTolerancePoints, snap: !shift);
                MoveVertex(d, Clamp01(target));
                return kind;
            }
            default:
                Resize(d, ResizedBox(d, delta, KeepsProportions(a, shift)));
                return PdfSnapKind.None;
        }
    }

    /// <summary>Ends the drag: one step in the undo history when the annotation changed. False when it did not.</summary>
    public bool EndAnnotationDrag()
    {
        if (_drag is not { } d) return false;
        _drag = null;
        if (!AllAnnotations.Contains(d.Annotation)) return false;
        string kind = AnnotationTypeName(d.Annotation.Type);
        string name = d.Handle switch
        {
            AnnotationHandle.Body => $"Move {kind}",
            AnnotationHandle.Rotate => $"Rotate {kind}",
            AnnotationHandle.Vertex => $"Move {kind} Point",
            _ => $"Resize {kind}",
        };
        bool changed = RecordInPlaceEdit(d.Annotation, name);
        if (changed) StatusText = d.Annotation.Type == AnnotationType.Measurement ? $"{name}: {d.Annotation.Contents}" : $"{name}.";
        return changed;
    }

    /// <summary>Puts the annotation back as it was when the drag started (Esc while dragging).</summary>
    public void CancelAnnotationDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        var a = d.Annotation;
        a.SetGeometry(d.Box, d.Quads);
        a.InkStrokes = d.Ink;
        a.Vertices = d.Vertices;
        a.StampRotation = d.Rotation;
        a.StampSize = d.StampSize;
        a.Contents = d.Contents;
    }

    // ------------------------------------------------------------------ the keyboard

    /// <summary>
    /// Moves the selected annotation by a step of the arrow keys, given as it looks on screen
    /// (<paramref name="screenX"/>, <paramref name="screenY"/>: -1, 0 or 1), so Right moves it right
    /// however the page is turned. Shift moves it further. One undo step per nudge.
    /// </summary>
    public bool NudgeSelectedAnnotation(int screenX, int screenY, bool large = false)
    {
        if (SelectedAnnotation is not { } a || !AllAnnotations.Contains(a) || (screenX == 0 && screenY == 0)) return false;
        if (!Permit(DocumentPermissions.CanAnnotate, "changing comments")) return false;
        double step = NudgePoints * (large ? LargeNudgeFactor : 1);
        int turn = a.PageNumber >= 1 && a.PageNumber <= Pages.Count ? (int)Math.Round(Pages[a.PageNumber - 1].OverlayRotationAngle / 90.0) : 0;
        // The overlay is turned clockwise by the page's rotation; a step on screen turned back is a step on the page.
        var (dx, dy) = (((turn % 4) + 4) % 4) switch
        {
            1 => (screenY, -screenX),
            2 => (-screenX, -screenY),
            3 => (-screenY, screenX),
            _ => (screenX, screenY),
        };
        var crop = CropOf(a.PageNumber);
        var d = new DragStart(a, AnnotationHandle.Body, -1, default, crop, new Rect(a.X, a.Y, a.Width, a.Height), a.Quads.ToList(),
            a.InkStrokes.Select(s => s.ToList()).ToList(), a.Vertices.ToList(), a.StampRotation, StampSizeOf(a, crop), a.Contents);
        var delta = ClampMove(d.Box, new Vector(dx * step / Width(crop), dy * step / Height(crop)));
        if (delta.Length < 1e-12) return false;
        Translate(d, delta);
        return RecordInPlaceEdit(a, $"Move {AnnotationTypeName(a.Type)}");
    }

    /// <summary>Deletes the selected annotation (Delete key); the selection goes with it.</summary>
    public bool DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation is not { } a || !AllAnnotations.Contains(a)) return false;
        if (!Permit(DocumentPermissions.CanAnnotate, "deleting comments")) return false;
        SelectedAnnotation = null;
        DeleteAnnotation(a);
        return true;
    }

    /// <summary>
    /// Selects the next annotation on the page (Tab), or the previous one (Shift+Tab), in the
    /// page's drawing order, starting from the first or last. Returns false past the last (or
    /// before the first), with the selection cleared, so Tab can go on to the rest of the window.
    /// </summary>
    public bool SelectNextAnnotation(bool backwards = false)
    {
        int pageNumber = SelectedAnnotation?.PageNumber ?? CurrentPageNumber;
        if (pageNumber < 1 || pageNumber > Pages.Count) return false;
        var onPage = Pages[pageNumber - 1].AnnotationsOnPage;
        if (onPage.Count == 0) { SelectedAnnotation = null; return false; }
        int at = SelectedAnnotation != null ? onPage.IndexOf(SelectedAnnotation) : -1;
        int next = at < 0 ? (backwards ? onPage.Count - 1 : 0) : at + (backwards ? -1 : 1);
        if (next < 0 || next >= onPage.Count) { SelectedAnnotation = null; return false; }
        SelectedAnnotation = onPage[next];
        return true;
    }

    // ------------------------------------------------------------------ the geometry, in the page's points

    private PdfRect CropOf(int page) =>
        Frame(page)?.Crop ?? (page >= 1 && page <= Pages.Count
            ? new PdfRect(0, 0, Math.Max(1, Pages[page - 1].UnrotatedWidthPt), Math.Max(1, Pages[page - 1].UnrotatedHeightPt))
            : new PdfRect(0, 0, 612, 792));

    private static double Width(PdfRect crop) => crop.Width > 0 ? crop.Width : 612;
    private static double Height(PdfRect crop) => crop.Height > 0 ? crop.Height : 792;
    private static PdfPoint ToPage(Point p, PdfRect crop) => NativeDocumentServices.ToPage(p, crop);
    private static Point FromPage(PdfPoint p, PdfRect crop) => NativeDocumentServices.FromPage(p, crop);
    private static Point Clamp01(Point p) => new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1));

    /// <summary>The stamp's size before it is turned, in points: as kept, else the box's (turned back when sideways).</summary>
    private static Size StampSizeOf(AnnotationModel a, PdfRect crop)
    {
        if (a.StampSize is { Width: > 0, Height: > 0 } s) return s;
        double w = a.Width * Width(crop), h = a.Height * Height(crop);
        return IsSideways(a.StampRotation) ? new Size(h, w) : new Size(w, h);
    }

    /// <summary>A move that keeps a box that is on the page on the page.</summary>
    private static Vector ClampMove(Rect box, Vector d)
    {
        double dx = d.X, dy = d.Y;
        if (box.Left >= 0 && box.Right <= 1) dx = Math.Clamp(dx, -box.Left, 1 - box.Right);
        if (box.Top >= 0 && box.Bottom <= 1) dy = Math.Clamp(dy, -box.Top, 1 - box.Bottom);
        return new Vector(dx, dy);
    }

    private static void Translate(DragStart d, Vector v)
    {
        var a = d.Annotation;
        var box = d.Box;
        box.Offset(v);
        a.SetGeometry(box, d.Quads.Select(q => { q.Offset(v); return q; }).ToList());
        if (d.Ink.Count > 0) a.InkStrokes = d.Ink.Select(s => s.Select(p => p + v).ToList()).ToList();
        if (d.Vertices.Count > 0) a.Vertices = d.Vertices.Select(p => p + v).ToList();
    }

    /// <summary>The box with the dragged corner or edge moved by <paramref name="delta"/>; the opposite one stays.</summary>
    private static Rect ResizedBox(DragStart d, Vector delta, bool proportional)
    {
        double W = Width(d.Crop), H = Height(d.Crop);
        // In points (y down), so proportions are the page's, not the normalized square's.
        double left = d.Box.Left * W, top = d.Box.Top * H, right = d.Box.Right * W, bottom = d.Box.Bottom * H;
        double w0 = Math.Max(1e-6, right - left), h0 = Math.Max(1e-6, bottom - top);
        double dx = delta.X * W, dy = delta.Y * H;
        var h = d.Handle;
        bool moveLeft = h is AnnotationHandle.TopLeft or AnnotationHandle.Left or AnnotationHandle.BottomLeft;
        bool moveRight = h is AnnotationHandle.TopRight or AnnotationHandle.Right or AnnotationHandle.BottomRight;
        bool moveTop = h is AnnotationHandle.TopLeft or AnnotationHandle.Top or AnnotationHandle.TopRight;
        bool moveBottom = h is AnnotationHandle.BottomLeft or AnnotationHandle.Bottom or AnnotationHandle.BottomRight;
        if (moveLeft) left = Math.Min(left + dx, right - MinEditSize);
        if (moveRight) right = Math.Max(right + dx, left + MinEditSize);
        if (moveTop) top = Math.Min(top + dy, bottom - MinEditSize);
        if (moveBottom) bottom = Math.Max(bottom + dy, top + MinEditSize);
        if (proportional)
        {
            bool horizontal = moveLeft || moveRight, vertical = moveTop || moveBottom;
            double k = horizontal && vertical ? Math.Max((right - left) / w0, (bottom - top) / h0)
                : horizontal ? (right - left) / w0 : (bottom - top) / h0;
            k = Math.Max(k, MinEditSize / Math.Min(w0, h0));
            double w = w0 * k, hh = h0 * k;
            // The fixed corner or edge stays; a free side grows about its middle.
            if (moveLeft) left = right - w; else if (moveRight) right = left + w; else { double cx = (left + right) / 2; left = cx - w / 2; right = cx + w / 2; }
            if (moveTop) top = bottom - hh; else if (moveBottom) bottom = top + hh; else { double cy = (top + bottom) / 2; top = cy - hh / 2; bottom = cy + hh / 2; }
        }
        return new Rect(left / W, top / H, (right - left) / W, (bottom - top) / H);
    }

    private static void Resize(DragStart d, Rect box)
    {
        var a = d.Annotation;
        var from = d.Box;
        double sx = from.Width > 0 ? box.Width / from.Width : 1, sy = from.Height > 0 ? box.Height / from.Height : 1;
        Point Map(Point p) => new(box.X + (p.X - from.X) * sx, box.Y + (p.Y - from.Y) * sy);
        if (a.Type == AnnotationType.Stamp)
        {
            // Its own size follows the box: scaled as a whole when turned, else side by side.
            var s = d.StampSize;
            if (!IsQuarterTurn(d.Rotation)) a.StampSize = new Size(s.Width * sx, s.Height * sx);
            else if (IsSideways(d.Rotation)) a.StampSize = new Size(s.Width * sy, s.Height * sx);
            else a.StampSize = new Size(s.Width * sx, s.Height * sy);
        }
        a.SetGeometry(box, d.Quads.Select(q => new Rect(Map(q.TopLeft), Map(q.BottomRight))).ToList());
        if (d.Ink.Count > 0) a.InkStrokes = d.Ink.Select(st => st.Select(Map).ToList()).ToList();
    }

    private void Turn(DragStart d, Point pointer, bool snap)
    {
        var a = d.Annotation;
        var centre = ToPage(new Point(d.Box.X + d.Box.Width / 2, d.Box.Y + d.Box.Height / 2), d.Crop);
        var from = ToPage(d.Pointer, d.Crop);
        var to = ToPage(pointer, d.Crop);
        if (Math.Abs(to.X - centre.X) + Math.Abs(to.Y - centre.Y) < 1e-6) return;
        // Counter-clockwise in default user space (y up), as /Matrix turns it.
        double turned = (Math.Atan2(to.Y - centre.Y, to.X - centre.X) - Math.Atan2(from.Y - centre.Y, from.X - centre.X)) * 180 / Math.PI;
        double degrees = d.Rotation + turned;
        if (snap) degrees = Math.Round(degrees / 15) * 15;
        degrees = PdfStamps.NormalizeDegrees(Math.Round(degrees, 3));
        a.StampSize = d.StampSize;
        a.StampRotation = degrees;
        var rect = PdfStamps.RotatedBounds(centre, d.StampSize.Width, d.StampSize.Height, degrees);
        a.SetGeometry(NativeDocumentServices.FromPage(rect, d.Crop), Array.Empty<Rect>());
    }

    private static Point VertexStart(DragStart d)
    {
        if (d.Annotation.Type == AnnotationType.Measurement) return d.Vertices[d.Vertex];
        int i = d.Vertex;
        foreach (var s in d.Ink)
        {
            if (i < s.Count) return s[i];
            i -= s.Count;
        }
        return default;
    }

    private void MoveVertex(DragStart d, Point target)
    {
        var a = d.Annotation;
        if (a.Type == AnnotationType.Measurement)
        {
            var points = d.Vertices.ToList();
            points[d.Vertex] = target;
            ApplyMeasurementPoints(a, points, d.Crop);
            return;
        }
        var strokes = d.Ink.Select(s => s.ToList()).ToList();
        int i = d.Vertex;
        foreach (var s in strokes)
        {
            if (i < s.Count) { s[i] = target; break; }
            i -= s.Count;
        }
        var all = strokes.SelectMany(s => s).ToList();
        double minX = all.Min(p => p.X), minY = all.Min(p => p.Y);
        var box = new Rect(minX, minY, Math.Max(1e-4, all.Max(p => p.X) - minX), Math.Max(1e-4, all.Max(p => p.Y) - minY));
        a.SetGeometry(box, Array.Empty<Rect>());
        a.InkStrokes = strokes;
    }

    /// <summary>A measurement with new points: measured again (its result becomes its label) and its box fitted to the points, arrow heads and label.</summary>
    private static void ApplyMeasurementPoints(AnnotationModel a, IReadOnlyList<Point> points, PdfRect crop)
    {
        var measure = a.Measure ?? PdfMeasure.FromScale(1, "in", 1, "in");
        var page = points.Select(p => ToPage(p, crop)).ToList();
        int needed = a.MeasureKind == PdfMeasurementKind.Area ? 3 : 2;
        if (page.Count < needed) return;
        var annotation = PdfMeasurements.Create(a.MeasureKind, a.PageNumber, page, measure, width: a.StrokeThickness > 0 ? a.StrokeThickness : 1);
        a.SetGeometry(NativeDocumentServices.FromPage(annotation.Rect, crop), Array.Empty<Rect>());
        a.Vertices = points.ToList();
        a.Contents = annotation.Contents ?? string.Empty;
    }

    // ------------------------------------------------------------------ the page's own drawing of loaded annotations

    /// <summary>
    /// After a change in the undo history: loaded annotations that changed are left off the page
    /// (the overlay draws them), unchanged ones are drawn by the page again; the pages that changed
    /// are drawn again; stamps the overlay draws get a preview at their present turn.
    /// </summary>
    private void SyncAnnotationDrawing()
    {
        if (SelectedAnnotation != null && !AllAnnotations.Contains(SelectedAnnotation)) SelectedAnnotation = null;
        IReadOnlyList<int> pages;
        try { pages = _docService.HideChangedAnnotations(AllAnnotations); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { pages = Array.Empty<int>(); }
        foreach (var a in AllAnnotations)
        {
            a.IsDrawnByPage = a.HasOwnAppearance && _docService.IsDrawnAsLoaded(a);
            if (a.Type == AnnotationType.Stamp && !a.IsDrawnByPage) _ = RefreshStampPreviewAsync(a);
        }
        if (pages.Count == 0) return;
        foreach (int n in pages.Where(n => n >= 1 && n <= Pages.Count))
        {
            Pages[n - 1].UnloadImage();
            if (n - 1 < Thumbnails.Count) Thumbnails[n - 1].UnloadThumbnail();
        }
        _cache.Clear();
        _ = RenderVisiblePagesAsync();
        _ = RenderThumbnailsAsync();
    }

    /// <summary>Draws a stamp's preview again when its turn changed since it was drawn (a kept appearance from the file, else the engine's own stamp).</summary>
    internal async Task RefreshStampPreviewAsync(AnnotationModel a)
    {
        bool kept = a.HasOwnAppearance && a.FileStampRotation != null;
        if (a.StampPreviewKey is { } key && key.Kept == kept && Math.Abs(key.Rotation - a.StampRotation) < 1e-6) return; // drawn, or being drawn
        double rotation = a.StampRotation;
        a.StampPreviewKey = (rotation, kept);
        System.Windows.Media.ImageSource? preview;
        if (kept)
        {
            double turn = PdfStamps.NormalizeDegrees(rotation - a.FileStampRotation!.Value);
            var service = _docService;
            preview = await RenderPreviewAsync(black => service.KeptAppearancePreview(a, turn, black));
        }
        else
        {
            var crop = CropOf(a.PageNumber);
            var rect = NativeDocumentServices.BoxOf(a, crop);
            preview = await RenderStampPreviewAsync(a, rect);
        }
        // A newer turn asked for its own preview meanwhile.
        if (a.StampPreviewKey is { } now && Math.Abs(now.Rotation - rotation) < 1e-6) a.StampPreview = preview;
    }
}
