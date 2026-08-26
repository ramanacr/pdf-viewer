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

    #endregion
}
