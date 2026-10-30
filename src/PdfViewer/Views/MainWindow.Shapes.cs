using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfViewer.Converters;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The page canvas while the line, arrow, polygon or connected-lines tool is on: a line follows
/// the pointer from where the drag started, and a polygon or connected lines shows its points with
/// a rubber band to the pointer. Both are drawn as the annotation will be: its line width at the
/// page's zoom, its dash and its line endings.
/// </summary>
public partial class MainWindow
{
    private Canvas? _shapeCanvas;
    private Point? _shapeCursor;
    private Path? _shapeLine;
    private bool _isDraggingLine;

    private static Point Normalized(PageViewModel page, Point pos) =>
        new(pos.X / Math.Max(1, page.UnrotatedDisplayWidth), pos.Y / Math.Max(1, page.UnrotatedDisplayHeight));

    /// <summary>A press with the line or arrow tool starts the drag; with the polygon or connected-lines tool it places a point (a double-click finishes).</summary>
    private bool TryStartShape(Canvas canvas, PageViewModel page, MouseButtonEventArgs e)
    {
        var tool = _vm.ActiveAnnotationTool;
        if (MainViewModel.IsClickShapeTool(tool))
        {
            e.Handled = true;
            bool samePage = _vm.ShapePageNumber == page.PageNumber;
            if (e.ClickCount >= 2 && !_vm.IsDrawingShapePoints)
                return true; // the rest of the double- or triple-click that finished a shape
            _shapeCanvas = canvas;
            // A double-click finishes the shape on its own page; on another page it is a click there.
            if (e.ClickCount >= 2 && samePage) _vm.FinishShape();
            else _vm.ShapeClick(page.PageNumber, Normalized(page, e.GetPosition(canvas)));
            RefreshShapePreview();
            return true;
        }
        if (!MainViewModel.IsLineTool(tool)) return false;
        e.Handled = true;
        _annotStartPoint = e.GetPosition(canvas);
        _isDrawingAnnotation = true;
        _isDraggingLine = true;
        canvas.CaptureMouse();
        _previewShape = ShapePreview(page, new[] { Normalized(page, _annotStartPoint) });
        if (_previewShape != null) canvas.Children.Add(_previewShape);
        return true;
    }

    /// <summary>The line being dragged follows the pointer; a polygon's rubber band follows it too.</summary>
    private bool TryMoveShape(Canvas canvas, PageViewModel page, MouseEventArgs e)
    {
        if (_isDrawingAnnotation && _isDraggingLine)
        {
            if (_previewShape is Path path)
                path.Data = ShapePreview(page, new[] { Normalized(page, _annotStartPoint), Normalized(page, e.GetPosition(canvas)) })?.Data;
            return true;
        }
        if (!MainViewModel.IsClickShapeTool(_vm.ActiveAnnotationTool)) return false;
        canvas.Cursor = Cursors.Cross;
        if (_vm.IsDrawingShapePoints && _vm.ShapePageNumber == page.PageNumber)
        {
            _shapeCanvas = canvas;
            _shapeCursor = Normalized(page, e.GetPosition(canvas));
            RefreshShapePreview();
        }
        return true;
    }

    /// <summary>The end of a line or arrow drag adds it.</summary>
    private bool TryFinishLine(Canvas canvas, PageViewModel page, MouseButtonEventArgs e)
    {
        if (!_isDrawingAnnotation || !_isDraggingLine) return false;
        EndLineDrag(canvas);
        e.Handled = true;
        _vm.AddLineShape(page.PageNumber, Normalized(page, _annotStartPoint), Normalized(page, e.GetPosition(canvas)));
        return true;
    }

    private void EndLineDrag(Canvas canvas)
    {
        _isDrawingAnnotation = false;
        _isDraggingLine = false;
        canvas.ReleaseMouseCapture();
        if (_previewShape != null && VisualTreeHelper.GetParent(_previewShape) is Canvas parent) parent.Children.Remove(_previewShape);
        _previewShape = null;
    }

    /// <summary>Esc, Enter and Backspace while a shape tool is on. True when the key was used.</summary>
    private bool TryShapeKey(Key key)
    {
        if (_isDrawingAnnotation && _isDraggingLine && key == Key.Escape)
        {
            // Esc drops the line being dragged.
            if (_previewShape != null && VisualTreeHelper.GetParent(_previewShape) is Canvas canvas) EndLineDrag(canvas);
            else { _isDrawingAnnotation = false; _isDraggingLine = false; _previewShape = null; }
            _vm.StatusText = "Line cancelled.";
            return true;
        }
        if (!MainViewModel.IsClickShapeTool(_vm.ActiveAnnotationTool)) return false;
        switch (key)
        {
            case Key.Escape when _vm.IsDrawingShapePoints:
                _vm.CancelShape();
                break;
            case Key.Escape:
                _vm.ActiveAnnotationTool = null;
                _vm.StatusText = "Drawing stopped.";
                break;
            case Key.Enter:
                _vm.FinishShape();
                break;
            case Key.Back:
                _vm.UndoShapePoint();
                break;
            default:
                return false;
        }
        RefreshShapePreview();
        return true;
    }

    /// <summary>Redraws the polygon or connected lines being drawn (and removes it when there is none).</summary>
    private void RefreshShapePreview()
    {
        if (_shapeLine != null && VisualTreeHelper.GetParent(_shapeLine) is Canvas old) old.Children.Remove(_shapeLine);
        _shapeLine = null;
        if (_shell.ActiveDocument == null || !_vm.IsDrawingShapePoints || _vm.ShapePageNumber < 1 || _vm.ShapePageNumber > _vm.Pages.Count)
        {
            if (!_vm.IsDrawingShapePoints) _shapeCursor = null;
            return;
        }
        // After a zoom or a scroll the page may have a new canvas: find the one shown now.
        var page = _vm.Pages[_vm.ShapePageNumber - 1];
        if (!ReferenceEquals(_shapeCanvas?.Tag, page) || _shapeCanvas is not { IsVisible: true })
            _shapeCanvas = FindPageCanvas(this, page);
        if (_shapeCanvas == null) return;

        var points = _vm.ShapePreviewPoints.ToList();
        if (_shapeCursor is { } c) points.Add(c);
        _shapeLine = ShapePreview(page, points);
        if (_shapeLine != null) _shapeCanvas.Children.Add(_shapeLine);
    }

    /// <summary>
    /// The line, arrow, polygon or connected lines through <paramref name="normPoints"/> (normalized
    /// on <paramref name="page"/>) as it will look once added: the annotation's own line width at
    /// the page's zoom, its dash and its line endings, so what is drawn while drawing matches what
    /// is saved. Null when no shape tool is on.
    /// </summary>
    private Path? ShapePreview(PageViewModel page, IReadOnlyList<Point> normPoints)
    {
        var points = normPoints.ToList();
        if (points.Count == 0) return null;
        // One point and no pointer yet: a dot where it is.
        if (points.Count < 2) points.Add(new Point(points[0].X + 0.01 / Math.Max(1, page.UnrotatedDisplayWidth), points[0].Y));
        if (_vm.ShapePreviewModel(points) is not { } model) return null;
        double scale = page.DisplayScale;
        return new Path
        {
            Data = model.ShapeGeometry(page.UnrotatedDisplayWidth, page.UnrotatedDisplayHeight, scale: scale),
            Stroke = (Brush)new BrushConverter().ConvertFrom(model.ColorHex)!,
            StrokeThickness = ScaledStrokeConverter.Scaled(model.StrokeThickness, scale),
            StrokeDashArray = model.StrokeDashArray ?? new DoubleCollection(),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
    }
}
