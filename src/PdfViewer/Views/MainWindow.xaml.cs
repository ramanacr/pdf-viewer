using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using PdfViewer.Views.Dialogs;

namespace PdfViewer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Point _panStartPoint;
    private double _panStartHOffset;
    private double _panStartVOffset;
    private bool _isMousePanning;
    private GridLength _savedSidebarWidth = new GridLength(280);

    public MainWindow()
    {
        InitializeComponent();

        _vm = (MainViewModel)DataContext;
        _vm.RequestPasswordFunc = PromptForPasswordAsync;
        _vm.ShowPropertiesAction = ShowPropertiesDialog;
        _vm.ShowExportDialogFunc = ShowExportImagesDialog;
        _vm.ShowSaveAnnotatedDialogFunc = ShowSaveAnnotatedDialog;
        _vm.ScrollToPageAction = ScrollToPage;
        _vm.GetViewportSizeFunc = () => (DocumentScrollViewer.ActualWidth, DocumentScrollViewer.ActualHeight);

        Loaded += MainWindow_Loaded;
        SizeChanged += (s, e) =>
        {
            if (_vm.IsDocumentLoaded && _vm.FitMode != PageFitMode.Custom)
            {
                _vm.ApplyFitMode();
            }
        };

        DocumentScrollViewer.SizeChanged += (s, e) =>
        {
            if (_vm.IsDocumentLoaded && _vm.FitMode != PageFitMode.Custom)
            {
                _vm.ApplyFitMode();
            }
        };

        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSidebarOpen))
            {
                UpdateSidebarColumnVisibility(_vm.IsSidebarOpen);
            }
            else if (e.PropertyName == nameof(MainViewModel.ActiveAnnotationTool) ||
                     e.PropertyName == nameof(MainViewModel.IsPanningEnabled))
            {
                ResetPageCanvasCursors();
            }
        };
    }

    private void UpdateSidebarColumnVisibility(bool isOpen)
    {
        if (isOpen)
        {
            SidebarColumn.MinWidth = 180;
            SidebarColumn.Width = _savedSidebarWidth.Value >= 180 ? _savedSidebarWidth : new GridLength(280);
            SidebarSplitterColumn.Width = GridLength.Auto;
        }
        else
        {
            if (SidebarColumn.ActualWidth >= 100)
            {
                _savedSidebarWidth = new GridLength(SidebarColumn.ActualWidth);
            }
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
        }

        if (_vm.IsDocumentLoaded && _vm.FitMode != PageFitMode.Custom)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _vm.ApplyFitMode();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(App.StartupPdfPath) && File.Exists(App.StartupPdfPath))
        {
            await _vm.LoadDocumentAsync(App.StartupPdfPath);
        }

        // Non-blocking background update check
        CheckForUpdatesOnStartup();
    }

    #region Dialog Helpers

    private Task<string?> PromptForPasswordAsync(string fileName)
    {
        var tcs = new TaskCompletionSource<string?>();
        var dialog = new PasswordDialog(fileName)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            tcs.SetResult(dialog.Password);
        }
        else
        {
            tcs.SetResult(null);
        }

        return tcs.Task;
    }

    private void ShowPropertiesDialog(DocumentMetadata metadata)
    {
        var dialog = new PropertiesDialog(metadata)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private (bool Confirmed, string OutDir, string Prefix, int Start, int End, string Format, int Dpi) ShowExportImagesDialog(DocumentMetadata metadata)
    {
        var dialog = new ExportImagesDialog(metadata, _vm.CurrentPageNumber)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            return (true, dialog.OutputDirectory, dialog.FileNamePrefix, dialog.StartPage, dialog.EndPage, dialog.SelectedFormat, dialog.SelectedDpi);
        }

        return (false, string.Empty, string.Empty, 1, 1, "PNG", 300);
    }

    #endregion

    #region Viewport & Page Scrolling

    private void ScrollToPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > _vm.Pages.Count) return;

        if (_vm.ViewMode == ViewLayoutMode.Continuous)
        {
            double accumulatedHeight = 0;
            for (int i = 0; i < pageNumber - 1; i++)
            {
                accumulatedHeight += _vm.Pages[i].DisplayHeight + 20; // 20 is bottom margin
            }

            DocumentScrollViewer.ScrollToVerticalOffset(accumulatedHeight);
            _vm.RenderPagesInViewport(accumulatedHeight, DocumentScrollViewer.ViewportHeight);
        }

        ScrollThumbnailIntoView(pageNumber);
    }

    private void DocumentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_vm.IsDocumentLoaded || _vm.Pages.Count == 0) return;

        if (_vm.ViewMode == ViewLayoutMode.Continuous)
        {
            // Determine current visible page from the center of the viewport
            double viewportTop = DocumentScrollViewer.VerticalOffset;
            double viewportHeight = DocumentScrollViewer.ViewportHeight;
            double centerOffset = viewportTop + (viewportHeight / 2.0);

            double accumulated = 0;
            int centerPage = 1;
            bool found = false;

            for (int i = 0; i < _vm.Pages.Count; i++)
            {
                double pageH = _vm.Pages[i].DisplayHeight + 20;
                if (centerOffset >= accumulated && centerOffset < accumulated + pageH)
                {
                    centerPage = i + 1;
                    found = true;
                    break;
                }
                accumulated += pageH;
            }

            if (!found)
            {
                if (centerOffset < 0)
                {
                    centerPage = 1;
                }
                else if (centerOffset >= accumulated && _vm.Pages.Count > 0)
                {
                    centerPage = _vm.Pages.Count;
                }
            }

            _vm.SetCurrentPageFromScroll(centerPage);
            _vm.RenderPagesInViewport(viewportTop, viewportHeight);

            ScrollThumbnailIntoView(centerPage);
        }
    }

    private void ScrollThumbnailIntoView(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > _vm.Thumbnails.Count || ThumbnailsScrollViewer == null) return;

        double total = _vm.Thumbnails.Count;
        if (total == 0) return;

        double extent = ThumbnailsScrollViewer.ExtentHeight;
        if (extent <= 0)
        {
            extent = total * 190.0;
        }

        double itemHeight = extent / total;
        double targetTop = (pageNumber - 1) * itemHeight;
        double viewportHeight = ThumbnailsScrollViewer.ViewportHeight;
        double targetOffset = targetTop - (viewportHeight / 2.0) + (itemHeight / 2.0);

        ThumbnailsScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset));
    }

    #endregion

    #region Mouse Pan & Dynamic Zoom

    private void DocumentScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (e.Delta > 0)
            {
                _vm.ZoomIn();
            }
            else if (e.Delta < 0)
            {
                _vm.ZoomOut();
            }
        }
    }

    private void DocumentScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed || (_vm.IsPanningEnabled && e.LeftButton == MouseButtonState.Pressed))
        {
            _isMousePanning = true;
            _panStartPoint = e.GetPosition(DocumentScrollViewer);
            _panStartHOffset = DocumentScrollViewer.HorizontalOffset;
            _panStartVOffset = DocumentScrollViewer.VerticalOffset;
            DocumentScrollViewer.Cursor = Cursors.SizeAll;
            DocumentScrollViewer.CaptureMouse();
            e.Handled = true;
        }
    }

    private void DocumentScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isMousePanning)
        {
            var currentPoint = e.GetPosition(DocumentScrollViewer);
            var delta = currentPoint - _panStartPoint;

            DocumentScrollViewer.ScrollToHorizontalOffset(_panStartHOffset - delta.X);
            DocumentScrollViewer.ScrollToVerticalOffset(_panStartVOffset - delta.Y);
            e.Handled = true;
        }
    }

    private void DocumentScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMousePanning)
        {
            _isMousePanning = false;
            DocumentScrollViewer.ReleaseMouseCapture();
            DocumentScrollViewer.Cursor = _vm.IsPanningEnabled ? Cursors.Hand : Cursors.Arrow;
            e.Handled = true;
        }
    }

    #endregion

    #region Drag & Drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && files[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                string firstPdf = files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) ?? files[0];
                if (File.Exists(firstPdf))
                {
                    await _vm.LoadDocumentAsync(firstPdf);
                }
            }
        }
    }

    #endregion

    #region Navigation Event Handlers

    private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ThumbnailViewModel thumb)
        {
            _vm.NavigateToPage(thumb.PageNumber);
        }
    }

    private void BookmarkTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BookmarkItem bookmark)
        {
            _vm.NavigateBookmark(bookmark);
        }
    }

    private void SearchMatch_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is SearchMatch match)
        {
            _vm.SelectSearchMatch(match);
        }
    }

    private void PageInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is TextBox tb && int.TryParse(tb.Text, out int page))
            {
                _vm.GoToPage(page);
            }
        }
    }

    private async void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await _vm.ExecuteSearchAsync();
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var aboutDialog = new AboutDialog
        {
            Owner = this
        };
        aboutDialog.ShowDialog();
    }

    private void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var updateDialog = new UpdateDialog(_vm.LatestUpdateInfo)
        {
            Owner = this
        };
        updateDialog.ShowDialog();
    }

    private void GitHubRepoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Services.UpdateService.OpenBrowser(Services.UpdateService.GitHubRepoUrl);
    }

    private void ViewReleasesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Services.UpdateService.OpenBrowser(Services.UpdateService.GitHubReleasesUrl);
    }

    private void UpdateBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var updateDialog = new UpdateDialog(_vm.LatestUpdateInfo)
        {
            Owner = this
        };
        updateDialog.ShowDialog();
    }

    /// <summary>
    /// Runs a non-blocking background check for updates after the window is fully loaded.
    /// </summary>
    private async void CheckForUpdatesOnStartup()
    {
        await Task.Delay(3000); // Allow the UI to fully initialize first
        await _vm.CheckForUpdatesInBackgroundAsync();
    }

    private (bool Confirmed, string TargetPath, AnnotationSaveMode Mode) ShowSaveAnnotatedDialog(DocumentMetadata metadata)
    {
        var dialog = new SaveAnnotatedDialog(metadata.FilePath)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Confirmed)
        {
            return (true, dialog.TargetPath, dialog.SelectedMode);
        }

        return (false, string.Empty, AnnotationSaveMode.Embedded);
    }

    #endregion

    #region Interactive Annotation Drawing

    private Point _annotStartPoint;
    private bool _isDrawingAnnotation;
    private System.Windows.Shapes.Shape? _previewShape;
    private readonly System.Collections.Generic.List<Point> _currentInkPoints = new();

    private bool _isSelectingText;
    private Point _textSelectStartPoint;

    private void PageCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not Canvas canvas || canvas.Tag is not PageViewModel page) return;

        // Mode 1: Active Annotation Tool is drawing
        if (_vm.ActiveAnnotationTool != null)
        {
            _annotStartPoint = e.GetPosition(canvas);
            _isDrawingAnnotation = true;
            _currentInkPoints.Clear();
            _currentInkPoints.Add(_annotStartPoint);
            canvas.CaptureMouse();

            // If Sticky Note click: Open Comment Textarea Dialog immediately
            if (_vm.ActiveAnnotationTool == AnnotationType.Note)
            {
                double normX = Math.Max(0, Math.Min(1, _annotStartPoint.X / page.DisplayWidth));
                double normY = Math.Max(0, Math.Min(1, _annotStartPoint.Y / page.DisplayHeight));

                var annot = new AnnotationModel
                {
                    PageNumber = page.PageNumber,
                    Type = AnnotationType.Note,
                    X = normX,
                    Y = normY,
                    Width = 24.0 / page.DisplayWidth,
                    Height = 24.0 / page.DisplayHeight,
                    ColorHex = _vm.SelectedAnnotationColor,
                    Author = _vm.SelectedAnnotationAuthor,
                    Title = "Sticky Note",
                    Contents = string.Empty
                };

                _isDrawingAnnotation = false;
                canvas.ReleaseMouseCapture();

                var dialog = new EditCommentDialog(annot, isNew: true) { Owner = this };
                if (dialog.ShowDialog() == true && dialog.IsConfirmed)
                {
                    _vm.AddAnnotation(annot);
                }
                return;
            }

            // Create preview shape
            if (_vm.ActiveAnnotationTool == AnnotationType.Highlight)
            {
                var brush = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!;
                _previewShape = new System.Windows.Shapes.Rectangle
                {
                    Fill = brush,
                    Stroke = brush,
                    Opacity = 0.45,
                    StrokeThickness = 1
                };
            }
            else if (_vm.ActiveAnnotationTool == AnnotationType.Rectangle)
            {
                _previewShape = new System.Windows.Shapes.Rectangle
                {
                    Stroke = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!,
                    StrokeThickness = _vm.SelectedAnnotationThickness
                };
            }
            else if (_vm.ActiveAnnotationTool == AnnotationType.Ellipse)
            {
                _previewShape = new System.Windows.Shapes.Ellipse
                {
                    Stroke = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!,
                    StrokeThickness = _vm.SelectedAnnotationThickness
                };
            }
            else if (_vm.ActiveAnnotationTool == AnnotationType.Underline)
            {
                _previewShape = new System.Windows.Shapes.Rectangle
                {
                    Fill = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!,
                    Height = 3
                };
            }
            else if (_vm.ActiveAnnotationTool == AnnotationType.FreeText)
            {
                _previewShape = new System.Windows.Shapes.Rectangle
                {
                    Stroke = (Brush)new BrushConverter().ConvertFrom(_vm.SelectedAnnotationColor)!,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    StrokeThickness = 1
                };
            }

            if (_previewShape != null)
            {
                Canvas.SetLeft(_previewShape, _annotStartPoint.X);
                Canvas.SetTop(_previewShape, _annotStartPoint.Y);
                _previewShape.Width = 0;
                _previewShape.Height = 0;
                canvas.Children.Add(_previewShape);
            }
            return;
        }

        // Mode 2: Normal Selection Mode (No annotation tool and not panning)
        if (!_vm.IsPanningEnabled)
        {
            var clickPos = e.GetPosition(canvas);
            double normClickX = clickPos.X / page.DisplayWidth;
            double normClickY = clickPos.Y / page.DisplayHeight;

            // Check if user clicked an existing annotation to open its editor.
            // (This interactive canvas sits above the annotation layer, so
            // Annotation_MouseLeftButtonDown on the annotation Grid never fires;
            // hit-test explicitly here for every annotation type instead.)
            var hitAnnotation = page.AnnotationsOnPage.FirstOrDefault(a =>
                normClickX >= a.X && normClickX <= a.X + a.Width &&
                normClickY >= a.Y && normClickY <= a.Y + a.Height);

            if (hitAnnotation != null)
            {
                var dialog = new EditCommentDialog(hitAnnotation, isNew: false) { Owner = this };
                dialog.ShowDialog();
                e.Handled = true;
                return;
            }

            // Double-click word selection
            if (e.ClickCount == 2)
            {
                page.SelectWordAt(new Point(normClickX, normClickY));
                _vm.UpdateSelectionFromPages();
                return;
            }

            // Clear text selection on other pages
            foreach (var otherPage in _vm.Pages)
            {
                if (otherPage != page)
                {
                    otherPage.ClearTextSelection();
                }
            }

            _isSelectingText = true;
            _textSelectStartPoint = clickPos;
            canvas.CaptureMouse();

            page.SelectWordAt(new Point(normClickX, normClickY));
            _vm.UpdateSelectionFromPages();
        }
    }

    private void PageCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas || canvas.Tag is not PageViewModel page) return;

        // 1. Updating annotation drawing preview
        if (_isDrawingAnnotation)
        {
            var currentPoint = e.GetPosition(canvas);
            _currentInkPoints.Add(currentPoint);

            if (_previewShape != null)
            {
                double left = Math.Min(_annotStartPoint.X, currentPoint.X);
                double top = Math.Min(_annotStartPoint.Y, currentPoint.Y);
                double width = Math.Abs(currentPoint.X - _annotStartPoint.X);
                double height = Math.Abs(currentPoint.Y - _annotStartPoint.Y);

                Canvas.SetLeft(_previewShape, left);
                Canvas.SetTop(_previewShape, top);
                _previewShape.Width = width;
                _previewShape.Height = height;
            }
            return;
        }

        // 2. Updating text selection range during mouse drag
        if (_isSelectingText)
        {
            var currentPoint = e.GetPosition(canvas);
            double startNormX = Math.Max(0, Math.Min(1, _textSelectStartPoint.X / page.DisplayWidth));
            double startNormY = Math.Max(0, Math.Min(1, _textSelectStartPoint.Y / page.DisplayHeight));
            double currNormX = Math.Max(0, Math.Min(1, currentPoint.X / page.DisplayWidth));
            double currNormY = Math.Max(0, Math.Min(1, currentPoint.Y / page.DisplayHeight));

            page.SelectRange(new Point(startNormX, startNormY), new Point(currNormX, currNormY));
            _vm.UpdateSelectionFromPages();
            return;
        }

        // 3. Hovering over text segments: change cursor to I-beam
        if (_vm.ActiveAnnotationTool == null && !_vm.IsPanningEnabled)
        {
            var hoverPt = e.GetPosition(canvas);
            double hoverNormX = hoverPt.X / page.DisplayWidth;
            double hoverNormY = hoverPt.Y / page.DisplayHeight;
            var hitSegment = page.FindClosestSegment(new Point(hoverNormX, hoverNormY), maxDistance: 0.025);

            canvas.Cursor = hitSegment != null ? Cursors.IBeam : null;
        }
        else
        {
            canvas.Cursor = null;
        }
    }

    private void PageCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Canvas canvas && !_isSelectingText && !_isDrawingAnnotation)
        {
            canvas.Cursor = null;
        }
    }

    private void ResetPageCanvasCursors()
    {
        foreach (var page in _vm.Pages)
        {
            var canvas = FindPageCanvas(this, page);
            if (canvas != null)
            {
                canvas.Cursor = null;
            }
        }
    }

    private static Canvas? FindPageCanvas(DependencyObject root, PageViewModel page)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Canvas canvas && ReferenceEquals(canvas.Tag, page))
            {
                return canvas;
            }

            var found = FindPageCanvas(child, page);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private void PageCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || canvas.Tag is not PageViewModel page) return;

        // 1. Finalizing annotation creation
        if (_isDrawingAnnotation)
        {
            _isDrawingAnnotation = false;
            canvas.ReleaseMouseCapture();

            if (_previewShape != null)
            {
                canvas.Children.Remove(_previewShape);
                _previewShape = null;
            }

            var endPoint = e.GetPosition(canvas);
            double left = Math.Min(_annotStartPoint.X, endPoint.X);
            double top = Math.Min(_annotStartPoint.Y, endPoint.Y);
            double width = Math.Max(10, Math.Abs(endPoint.X - _annotStartPoint.X));
            double height = Math.Max(10, Math.Abs(endPoint.Y - _annotStartPoint.Y));

            double normX = Math.Max(0, left / page.DisplayWidth);
            double normY = Math.Max(0, top / page.DisplayHeight);
            double normW = Math.Min(1 - normX, width / page.DisplayWidth);
            double normH = Math.Min(1 - normY, height / page.DisplayHeight);

            if (_vm.ActiveAnnotationTool.HasValue && width > 4 && height > 4)
            {
                var annot = new AnnotationModel
                {
                    PageNumber = page.PageNumber,
                    Type = _vm.ActiveAnnotationTool.Value,
                    X = normX,
                    Y = normY,
                    Width = normW,
                    Height = normH,
                    ColorHex = _vm.SelectedAnnotationColor,
                    StrokeThickness = _vm.SelectedAnnotationThickness,
                    Author = _vm.SelectedAnnotationAuthor,
                    Title = _vm.ActiveAnnotationTool.Value.ToString(),
                    Contents = string.Empty
                };

                if (_vm.ActiveAnnotationTool.Value == AnnotationType.Ink && _currentInkPoints.Count > 1)
                {
                    annot.InkPoints = _currentInkPoints
                        .Select(p => new Point(p.X / page.DisplayWidth, p.Y / page.DisplayHeight))
                        .ToList();
                }

                if (_vm.ActiveAnnotationTool.Value == AnnotationType.FreeText)
                {
                    var dialog = new EditCommentDialog(annot, isNew: true) { Owner = this };
                    if (dialog.ShowDialog() == true && dialog.IsConfirmed)
                    {
                        _vm.AddAnnotation(annot);
                    }
                }
                else
                {
                    _vm.AddAnnotation(annot);
                }
            }
            return;
        }

        // 2. Finalizing text selection
        if (_isSelectingText)
        {
            _isSelectingText = false;
            canvas.ReleaseMouseCapture();

            var currentPoint = e.GetPosition(canvas);
            double startNormX = Math.Max(0, Math.Min(1, _textSelectStartPoint.X / page.DisplayWidth));
            double startNormY = Math.Max(0, Math.Min(1, _textSelectStartPoint.Y / page.DisplayHeight));
            double currNormX = Math.Max(0, Math.Min(1, currentPoint.X / page.DisplayWidth));
            double currNormY = Math.Max(0, Math.Min(1, currentPoint.Y / page.DisplayHeight));

            // If user simply clicked on an empty area without dragging, clear selection
            double dx = Math.Abs(currentPoint.X - _textSelectStartPoint.X);
            double dy = Math.Abs(currentPoint.Y - _textSelectStartPoint.Y);
            if (dx < 4 && dy < 4)
            {
                var hit = page.FindClosestSegment(new Point(startNormX, startNormY));
                if (hit == null)
                {
                    page.ClearTextSelection();
                }
            }

            _vm.UpdateSelectionFromPages();
        }
    }

    private void Annotation_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is AnnotationModel annot)
        {
            // If user is currently drawing with an active tool, let canvas handle it
            if (_vm.ActiveAnnotationTool != null) return;

            var dialog = new EditCommentDialog(annot, isNew: false) { Owner = this };
            dialog.ShowDialog();
            e.Handled = true;
        }
    }

    private void AnnotationItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is AnnotationModel annot)
        {
            _vm.CurrentPageNumber = annot.PageNumber;
            ScrollToPage(annot.PageNumber);
            var dialog = new EditCommentDialog(annot, isNew: false) { Owner = this };
            dialog.ShowDialog();
            e.Handled = true;
        }
    }

    private void EditAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is AnnotationModel annot)
        {
            _vm.CurrentPageNumber = annot.PageNumber;
            ScrollToPage(annot.PageNumber);
            var dialog = new EditCommentDialog(annot, isNew: false) { Owner = this };
            dialog.ShowDialog();
            e.Handled = true;
        }
    }

    #endregion
}
