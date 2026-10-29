using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The page canvas while the line, arrow, polygon or connected-lines tool is on: a line follows
/// the pointer from where the drag started, and a polygon or connected lines shows its points with
/// a rubber band to the pointer.
/// </summary>
public partial class MainWindow
{
    private Canvas? _shapeCanvas;
    private Point? _shapeCursor;
    private Polyline? _shapeLine;

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
        canvas.CaptureMouse();
        _previewShape = new Line
        {
            X1 = _annotStartPoint.X, Y1 = _annotStartPoint.Y, X2 = _annotStartPoint.X, Y2 = _annotStartPoint.Y,
            Stroke = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!,
            StrokeThickness = Math.Max(1, _vm.SelectedAnnotationThickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(_previewShape);
        return true;
    }

    /// <summary>The line being dragged follows the pointer; a polygon's rubber band follows it too.</summary>
    private bool TryMoveShape(Canvas canvas, PageViewModel page, MouseEventArgs e)
    {
        if (_isDrawingAnnotation && _previewShape is Line line)
        {
            var at = e.GetPosition(canvas);
            line.X2 = at.X;
            line.Y2 = at.Y;
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
        if (!_isDrawingAnnotation || _previewShape is not Line) return false;
        EndLineDrag(canvas);
        e.Handled = true;
        _vm.AddLineShape(page.PageNumber, Normalized(page, _annotStartPoint), Normalized(page, e.GetPosition(canvas)));
        return true;
    }

    private void EndLineDrag(Canvas canvas)
    {
        _isDrawingAnnotation = false;
        canvas.ReleaseMouseCapture();
        if (_previewShape != null && VisualTreeHelper.GetParent(_previewShape) is Canvas parent) parent.Children.Remove(_previewShape);
        _previewShape = null;
    }

    /// <summary>Esc, Enter and Backspace while a shape tool is on. True when the key was used.</summary>
    private bool TryShapeKey(Key key)
    {
        if (_isDrawingAnnotation && _previewShape is Line && key == Key.Escape)
        {
            // Esc drops the line being dragged.
            if (VisualTreeHelper.GetParent(_previewShape) is Canvas canvas) EndLineDrag(canvas);
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

        double w = page.UnrotatedDisplayWidth, h = page.UnrotatedDisplayHeight;
        var points = _vm.ShapePreviewPoints.Select(p => new Point(p.X * w, p.Y * h)).ToList();
        if (_shapeCursor is { } c) points.Add(new Point(c.X * w, c.Y * h));
        if (_vm.ActiveAnnotationTool == Models.AnnotationType.Polygon && points.Count >= 3) points.Add(points[0]);
        if (points.Count < 2)
        {
            // One point and no pointer yet: a dot where it is.
            points.Add(new Point(points[0].X + 0.01, points[0].Y));
        }
        _shapeLine = new Polyline
        {
            Points = new PointCollection(points),
            Stroke = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!,
            StrokeThickness = Math.Max(1, _vm.SelectedAnnotationThickness),
            StrokeDashArray = new DoubleCollection { 4, 2 },
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        _shapeCanvas.Children.Add(_shapeLine);
    }
}
