using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// The line, arrow, polygon and connected-lines tools, as in the web viewer. A line or an arrow
/// is dragged from where it starts to where it ends. A polygon or connected lines gets a point
/// per click; a double-click or Enter finishes it, Backspace takes back the last point and Esc
/// drops it. Each one is a Line, Polygon or PolyLine annotation without /Measure (an arrow is a
/// line with an open arrow at its end), so it is not a measurement.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The shortest line the line and arrow tools add, in points (a click is not a line).</summary>
    public const double MinLineLengthPoints = 3;

    private readonly List<Point> _shapePoints = new();
    private int _shapePage;

    /// <summary>A tool that places a point per click: polygon or connected lines.</summary>
    public static bool IsClickShapeTool(AnnotationType? tool) => tool is AnnotationType.Polygon or AnnotationType.PolyLine;

    /// <summary>A tool that draws a line by a drag: line or arrow.</summary>
    public static bool IsLineTool(AnnotationType? tool) => tool is AnnotationType.Line or AnnotationType.Arrow;

    /// <summary>The points of the polygon or connected lines being drawn, normalized on <see cref="ShapePageNumber"/>.</summary>
    public IReadOnlyList<Point> ShapePreviewPoints => _shapePoints.ToList();

    /// <summary>The page the polygon or connected lines is being drawn on (0 when none is).</summary>
    public int ShapePageNumber => _shapePage;

    /// <summary>A polygon or connected lines has points placed and is not finished yet.</summary>
    public bool IsDrawingShapePoints => _shapePoints.Count > 0;

    /// <summary>
    /// The PDFium engine (developer builds) cannot save lines, arrows, polygons or connected
    /// lines, so their tools say so and stay off rather than drawing shapes a save would drop.
    /// </summary>
    private bool CanUseShapeTool(AnnotationType tool)
    {
        if (!IsLineTool(tool) && !IsClickShapeTool(tool) || UseNativeDocumentServices) return true;
        StatusText = "Lines, arrows, polygons and connected lines cannot be saved with the PDFium engine. Switch to the vector engine to draw them.";
        return false;
    }

    private void OnShapeToolChanged(AnnotationType? tool)
    {
        ResetShapePoints();
        if (IsLineTool(tool)) StatusText = "Drag from where the line starts to where it ends.";
        else if (IsClickShapeTool(tool)) StatusText = "Click each point. Double-click or press Enter to finish, Backspace removes the last point, Esc cancels.";
    }

    /// <summary>
    /// Adds a line (or an arrow, with the arrow tool) from <paramref name="start"/> to
    /// <paramref name="end"/>, normalized on <paramref name="page"/>. False when the drag was too
    /// short to be a line.
    /// </summary>
    public bool AddLineShape(int page, Point start, Point end)
    {
        if (ActiveAnnotationTool is not { } tool || !IsLineTool(tool)) return false;
        var crop = CropOf(page);
        if (Distance(ToPage(Clamp01(start), crop), ToPage(Clamp01(end), crop)) < MinLineLengthPoints) return false;
        return AddShape(tool, page, new[] { start, end }) != null;
    }

    /// <summary>Places a point of the polygon or connected lines; a click on another page starts a new one there.</summary>
    public void ShapeClick(int page, Point norm)
    {
        if (!IsClickShapeTool(ActiveAnnotationTool) || page < 1 || page > Pages.Count) return;
        if (_shapePoints.Count > 0 && page != _shapePage) ResetShapePoints();
        norm = Clamp01(norm);
        var crop = CropOf(page);
        // The same point twice (the first click of a double-click) is one point.
        if (_shapePoints.Count > 0 && Distance(ToPage(_shapePoints[^1], crop), ToPage(norm, crop)) < 0.5) return;
        if (_shapePoints.Count >= PdfAnnotationLimits.MaxVertices) return;
        _shapePage = page;
        _shapePoints.Add(norm);
        OnShapePointsChanged();
    }

    /// <summary>Finishes the polygon (three points at least) or connected lines (two at least): double-click or Enter.</summary>
    public bool FinishShape()
    {
        if (ActiveAnnotationTool is not { } tool || !IsClickShapeTool(tool)) return false;
        int needed = tool == AnnotationType.Polygon ? 3 : 2;
        if (_shapePoints.Count < needed)
        {
            StatusText = tool == AnnotationType.Polygon ? "A polygon needs at least three points." : "Place at least two points.";
            return false;
        }
        var added = AddShape(tool, _shapePage, _shapePoints.ToList());
        ResetShapePoints();
        return added != null;
    }

    /// <summary>Takes back the last point placed (Backspace).</summary>
    public void UndoShapePoint()
    {
        if (_shapePoints.Count == 0) return;
        _shapePoints.RemoveAt(_shapePoints.Count - 1);
        if (_shapePoints.Count == 0) _shapePage = 0;
        OnShapePointsChanged();
    }

    /// <summary>Drops the polygon or connected lines being drawn (Esc); the tool stays on.</summary>
    public void CancelShape()
    {
        if (_shapePoints.Count == 0) return;
        ResetShapePoints();
        StatusText = "Shape cancelled.";
    }

    private void ResetShapePoints()
    {
        if (_shapePoints.Count == 0 && _shapePage == 0) return;
        _shapePoints.Clear();
        _shapePage = 0;
        OnShapePointsChanged();
    }

    private void OnShapePointsChanged()
    {
        OnPropertyChanged(nameof(ShapePreviewPoints));
        OnPropertyChanged(nameof(ShapePageNumber));
        OnPropertyChanged(nameof(IsDrawingShapePoints));
    }

    /// <summary>
    /// A line, arrow, polygon or connected lines through <paramref name="points"/> (normalized),
    /// in the chosen colour and line width, added as one undo step. Its box holds the points and
    /// the arrow heads, as the saved rectangle will.
    /// </summary>
    private AnnotationModel? AddShape(AnnotationType type, int page, IReadOnlyList<Point> points)
    {
        if (page < 1 || page > Pages.Count || points.Count < 2) return null;
        var crop = CropOf(page);
        var vertices = points.Select(Clamp01).ToList();
        double width = SelectedAnnotationThickness > 0 ? SelectedAnnotationThickness : 1;
        var model = new AnnotationModel
        {
            PageNumber = page,
            Type = type,
            ColorHex = SelectedAnnotationColor,
            StrokeThickness = width,
            Opacity = 1,
            Author = SelectedAnnotationAuthor,
            Title = AnnotationTypeName(type),
            Contents = string.Empty,
            LineEndings = type == AnnotationType.Arrow ? new[] { "None", "OpenArrow" } : null,
        };
        var bounds = NativeDocumentServices.ShapeBounds(vertices.Select(p => ToPage(p, crop)).ToList(), width, type == AnnotationType.Arrow);
        var box = FromPage(bounds, crop);
        box.Intersect(new Rect(0, 0, 1, 1));
        if (box.IsEmpty) return null;
        model.SetGeometry(box, Array.Empty<Rect>());
        model.Vertices = vertices;
        AddAnnotation(model);
        StatusText = $"Added {AnnotationTypeName(type).ToLowerInvariant()} on page {page}.";
        return model;
    }

    private static Rect FromPage(PdfRect r, PdfRect crop) => NativeDocumentServices.FromPage(r, crop);

    /// <summary>
    /// Grows a line's or connected lines' box to hold its line endings, as the saved rectangle
    /// will, after its endings or line width changed. The box only grows.
    /// </summary>
    private void FitShapeBox(AnnotationModel a)
    {
        if (!a.HasLineEndings || a.Vertices.Count < 2 || a.PageNumber < 1 || a.PageNumber > Pages.Count) return;
        if (NativeDocumentServices.ShapeEndings(a)?.Any(e => e != "None") != true) return;
        var crop = CropOf(a.PageNumber);
        var current = new Rect(a.X, a.Y, a.Width, a.Height);
        var box = FromPage(NativeDocumentServices.ShapeBounds(a.Vertices.Select(p => ToPage(p, crop)).ToList(), a.StrokeThickness > 0 ? a.StrokeThickness : 1, true), crop);
        box.Union(current);
        box.Intersect(new Rect(0, 0, 1, 1));
        if (box.IsEmpty || box == current) return;
        a.SetGeometry(box, a.Quads);
    }
}
