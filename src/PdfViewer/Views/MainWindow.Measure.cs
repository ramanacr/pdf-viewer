using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfEngine.Vector.Annotations;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The page canvas while measuring: the points placed and a rubber band to the pointer, a marker
/// where the pointer snaps (square: end point, triangle: midpoint, circle: on a line).
/// </summary>
public partial class MainWindow
{
    private bool _isPlacingStampBox;
    private Canvas? _measureCanvas;
    private (PageViewModel Page, Point Norm, PdfSnapKind Kind)? _measureCursor;
    private Polyline? _measureLine;
    private Shape? _snapMarker;

    /// <summary>The pointer on the page, normalized and snapped (Shift places it freely).</summary>
    private Point MeasurePointer(Canvas canvas, PageViewModel page, MouseEventArgs e, out PdfSnapKind kind)
    {
        var pos = e.GetPosition(canvas);
        var norm = new Point(pos.X / Math.Max(1, page.UnrotatedDisplayWidth), pos.Y / Math.Max(1, page.UnrotatedDisplayHeight));
        bool snap = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        var (point, snapped) = _vm.SnapMeasurePoint(page.PageNumber, norm, MainViewModel.SnapTolerance(page.DisplayScale), snap);
        kind = snapped;
        return point;
    }

    private async Task PlaceMeasurePointAsync(int page, Point norm)
    {
        try
        {
            await _vm.MeasureClickAsync(page, norm);
        }
        finally
        {
            RefreshMeasurePreview();
        }
    }

    /// <summary>Redraws the measurement being drawn on its page's canvas (and removes it when there is none).</summary>
    private void RefreshMeasurePreview()
    {
        if (_measureLine != null && VisualTreeHelper.GetParent(_measureLine) is Canvas oldLine) oldLine.Children.Remove(_measureLine);
        if (_snapMarker != null && VisualTreeHelper.GetParent(_snapMarker) is Canvas oldMarker) oldMarker.Children.Remove(_snapMarker);
        _measureLine = null;
        _snapMarker = null;
        if (_shell.ActiveDocument == null || !_vm.IsMeasuring || _measureCanvas?.Tag is not PageViewModel page) return;

        double w = page.UnrotatedDisplayWidth, h = page.UnrotatedDisplayHeight;
        var brush = (Brush)new BrushConverter().ConvertFrom(MainViewModel.MeasureColor)!;
        var points = _vm.MeasurePageNumber == page.PageNumber ? _vm.MeasurePreviewPoints.Select(p => new Point(p.X * w, p.Y * h)).ToList() : new();
        var cursor = _measureCursor is { } c && ReferenceEquals(c.Page, page) ? new Point(c.Norm.X * w, c.Norm.Y * h) : (Point?)null;
        bool distanceDone = _vm.ActiveMeasureTool == PdfMeasurementKind.Distance && points.Count >= 2;
        if (cursor is { } p0 && points.Count > 0 && !distanceDone) points.Add(p0);
        if (_vm.ActiveMeasureTool == PdfMeasurementKind.Area && points.Count >= 3) points.Add(points[0]);

        if (points.Count >= 2)
        {
            _measureLine = new Polyline
            {
                Points = new PointCollection(points),
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            _measureCanvas.Children.Add(_measureLine);
        }

        if (cursor is { } at && _measureCursor is { Kind: not PdfSnapKind.None } snapped)
        {
            const double r = 5;
            _snapMarker = snapped.Kind switch
            {
                PdfSnapKind.Endpoint => new Rectangle { Width = 2 * r, Height = 2 * r },
                PdfSnapKind.Midpoint => new Polygon { Points = new PointCollection { new(r, 0), new(2 * r, 2 * r), new(0, 2 * r) } },
                _ => new Ellipse { Width = 2 * r, Height = 2 * r },
            };
            _snapMarker.Stroke = brush;
            _snapMarker.StrokeThickness = 1.5;
            _snapMarker.Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x1F, 0x4F, 0xD8));
            _snapMarker.IsHitTestVisible = false;
            Canvas.SetLeft(_snapMarker, at.X - r);
            Canvas.SetTop(_snapMarker, at.Y - r);
            _measureCanvas.Children.Add(_snapMarker);
        }
    }
}
