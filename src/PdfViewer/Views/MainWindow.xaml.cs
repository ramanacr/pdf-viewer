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
    private readonly ShellViewModel _shell;
    private Point _panStartPoint;
    private double _panStartHOffset;
    private double _panStartVOffset;
    private bool _isMousePanning;
    private GridLength _savedSidebarWidth = new GridLength(280);

    /// <summary>
    /// The document currently on screen. A property rather than a field because the window
    /// now shows one of several open documents, and every handler below should act on
    /// whichever that is.
    /// </summary>
    private MainViewModel _vm => _shell.ActiveDocument!;

    public MainWindow()
    {
        InitializeComponent();

        _shell = (ShellViewModel)DataContext;

        // Each tab needs its own wiring to this window's dialogs, so a document opened in a
        // new tab is not left unable to ask for a password or show a save dialog.
        foreach (var document in _shell.Documents) AttachToWindow(document);
        _shell.Documents.CollectionChanged += (s, e) =>
        {
            foreach (var added in e.NewItems?.OfType<MainViewModel>() ?? Enumerable.Empty<MainViewModel>())
            {
                AttachToWindow(added);
            }
        };

        _shell.ActiveDocumentChanged += OnActiveDocumentChanged;

        Loaded += MainWindow_Loaded;
        SizeChanged += (s, e) => ReapplyFitMode();
        DocumentScrollViewer.SizeChanged += (s, e) => ReapplyFitMode();
    }

    private void ReapplyFitMode()
    {
        if (_shell.ActiveDocument is { IsDocumentLoaded: true, FitMode: not PageFitMode.Custom } document)
        {
            document.ApplyFitMode();
        }
    }

    /// <summary>Gives one document the callbacks it needs to reach this window.</summary>
    private void AttachToWindow(MainViewModel document)
    {
        document.RequestPasswordFunc = PromptForPasswordAsync;
        document.ShowPropertiesAction = ShowPropertiesDialog;
        document.ShowFormFieldsFunc = ShowFormFieldsDialog;
        document.ShowExportDialogFunc = ShowExportImagesDialog;
        document.ShowSaveAnnotatedDialogFunc = ShowSaveAnnotatedDialog;
        document.ShowPrintDialogFunc = ShowPrintPreviewDialog;
        document.ShowOrganizePagesFunc = ShowOrganizePagesDialog;
        document.ShowAttachmentsFunc = ShowAttachmentsDialog;
        document.ConfirmFunc = ConfirmDialog;
        document.ConfirmComponentDownloadFunc = ConfirmComponentDownload;
        document.ConfirmSaveBeforeClosingFunc = ConfirmSaveBeforeClosing;
        document.ScrollToPageAction = ScrollToPage;
        document.ScrollToMatchAction = ScrollToMatch;
        document.GetViewportSizeFunc = () => (DocumentScrollViewer.ActualWidth, DocumentScrollViewer.ActualHeight);

        document.PropertyChanged += (s, e) =>
        {
            // Only the document on screen may drive the window's chrome; a background tab
            // toggling its sidebar must not move the visible one's.
            if (!ReferenceEquals(s, _shell.ActiveDocument)) return;

            if (e.PropertyName == nameof(MainViewModel.IsSidebarOpen))
            {
                UpdateSidebarColumnVisibility(document.IsSidebarOpen);
            }
            else if (e.PropertyName == nameof(MainViewModel.ActiveAnnotationTool) ||
                     e.PropertyName == nameof(MainViewModel.IsPanningEnabled))
            {
                ResetPageCanvasCursors();
            }
            else if (e.PropertyName == nameof(MainViewModel.WindowTitle))
            {
                Title = document.WindowTitle;
            }
        };
    }

    /// <summary>
    /// Remembers where the outgoing document was scrolled to and restores the incoming one's
    /// position, so switching tabs returns you to where you were rather than to page one.
    /// </summary>
    private void OnActiveDocumentChanged(MainViewModel? outgoing, MainViewModel? incoming)
    {
        if (outgoing != null) _scrollOffsets[outgoing] = DocumentScrollViewer.VerticalOffset;

        if (incoming == null) return;

        Title = incoming.WindowTitle;
        UpdateSidebarColumnVisibility(incoming.IsSidebarOpen);

        // The new document's pages have to be laid out before an offset means anything.
        Dispatcher.InvokeAsync(() =>
        {
            double offset = _scrollOffsets.TryGetValue(incoming, out double saved) ? saved : 0;
            DocumentScrollViewer.ScrollToVerticalOffset(offset);
            incoming.RenderPagesInViewport(offset, DocumentScrollViewer.ViewportHeight);
            ResetPageCanvasCursors();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private readonly Dictionary<MainViewModel, double> _scrollOffsets = new();

    private void DocumentTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MainViewModel document })
        {
            _shell.ActiveDocument = document;
        }
    }

    private async void CloseDocumentTab_Click(object sender, RoutedEventArgs e)
    {
        // Stops the click also selecting the tab that is on its way out.
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: MainViewModel document })
        {
            await _shell.CloseDocumentAsync(document);
        }
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
            await _shell.OpenDocumentAsync(App.StartupPdfPath);
        }

        // The first launch asks before anything touches the network; afterwards the stored
        // answer is honoured. A privacy-first reader must not phone home unasked.
        if (Services.PrivacySettings.AutomaticUpdateChecksEnabled is null)
        {
            ShowPrivacyDialog();
        }

        if (Services.PrivacySettings.AutomaticUpdateChecksEnabled == true)
        {
            CheckForUpdatesOnStartup();
        }
    }

    private void ShowPrivacyDialog()
    {
        new PrivacyDialog { Owner = this }.ShowDialog();
    }

    private void PrivacyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowPrivacyDialog();
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

    /// <summary>
    /// Shows the form field editor. Returns the edited fields, or null when cancelled.
    /// </summary>
    private IReadOnlyList<PdfEngine.Forms.FormFieldModel>? ShowFormFieldsDialog(
        IReadOnlyList<PdfEngine.Forms.FormFieldModel> fields, string documentName)
    {
        var dialog = new FormFieldsDialog(fields, documentName) { Owner = this };
        bool? result = dialog.ShowDialog();

        return result == true && dialog.SaveRequested ? dialog.Fields.ToList() : null;
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

    private bool ShowPrintPreviewDialog(IPdfDocumentService docService, int currentPage)
    {
        var dialog = new PrintPreviewDialog(docService, currentPage)
        {
            Owner = this
        };
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Shows the page organizer. Returns the arrangement the user built, or null when
    /// cancelled or when nothing was actually changed.
    /// </summary>
    private IReadOnlyList<PdfEngine.Pages.PageArrangementEntry>? ShowOrganizePagesDialog(
        int pageCount, string documentName)
    {
        var dialog = new OrganizePagesDialog(pageCount, documentName) { Owner = this };
        bool? result = dialog.ShowDialog();

        return result == true && dialog.SaveRequested ? dialog.BuildArrangement() : null;
    }

    private PdfEngine.Safety.EmbeddedFileInfo? ShowAttachmentsDialog(
        IReadOnlyList<PdfEngine.Safety.EmbeddedFileInfo> files, string documentName)
    {
        var dialog = new AttachmentsDialog(files, documentName) { Owner = this };
        dialog.ShowDialog();
        return dialog.Chosen;
    }

    private bool ConfirmDialog(string message, string caption) =>
        MessageBox.Show(this, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;

    /// <summary>
    /// Asks before fetching an optional component. This is the only network request the
    /// application makes besides the update check, so it says plainly what it will download,
    /// how big it is, and where from.
    /// </summary>
    private bool ConfirmComponentDownload(PdfViewer.Core.Components.OptionalComponent component) =>
        MessageBox.Show(this,
            $"{component.DisplayName} is not installed.\n\n" +
            $"It can be downloaded now ({component.SizeText}) from this project's GitHub releases page. " +
            "The file is checked against a checksum built into this application and discarded if it does not match.\n\n" +
            "Download and install it?",
            "Read Aloud",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>
    /// Presentation mode: the document fills the screen and every chrome element gets out of
    /// the way. Escape and F11 both return, because a full-screen window with no visible way
    /// out is the kind of thing users have to kill from Task Manager.
    /// </summary>
    private WindowState _preFullScreenState = WindowState.Normal;
    private bool _isFullScreen;

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            _isFullScreen = false;
            MainMenuBar.Visibility = Visibility.Visible;
            MainToolBar.Visibility = Visibility.Visible;
            MainStatusBar.Visibility = Visibility.Visible;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullScreenState;
        }
        else
        {
            _preFullScreenState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _isFullScreen = true;
            MainMenuBar.Visibility = Visibility.Collapsed;
            MainToolBar.Visibility = Visibility.Collapsed;
            MainStatusBar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Toggling through Normal forces WPF to re-measure, otherwise a window that was
            // already maximized keeps the taskbar-sized bounds it had with a title bar.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }

        _vm.StatusText = _isFullScreen
            ? "Full screen - press Esc or F11 to exit."
            : "Ready";
    }

    private void FullScreenMenuItem_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>
    /// Yes / No / Cancel on unsaved work. Null means cancel, which leaves the document open.
    /// </summary>
    private bool? ConfirmSaveBeforeClosing(string fileName)
    {
        var answer = MessageBox.Show(this,
            $"\"{fileName}\" has annotation changes that are not saved.\n\nSave them?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return answer switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null
        };
    }

    private bool _closeConfirmed;

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // Every open document is asked about, not just the one on screen - closing the window
        // takes them all with it.
        if (_closeConfirmed || !_shell.Documents.Any(d => d.HasUnsavedChanges)) return;

        // The prompt is async and Closing is not, so the first pass always cancels the close
        // and the answer decides whether to ask the window to close again.
        e.Cancel = true;

        if (await _shell.ConfirmCloseAllAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Silence Read Aloud and release every open document, so the application does not
        // carry on talking through its own shutdown or leave native buffers held.
        _shell.ShutdownAll();
        base.OnClosed(e);
    }

    /// <summary>
    /// Window-level keys. Handled in preview so they work wherever focus happens to be, but
    /// skipped while a text box has focus so typing a page number or a search term is never
    /// swallowed.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        switch (e.Key)
        {
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;

            case Key.Escape when _isFullScreen:
                ToggleFullScreen();
                e.Handled = true;
                break;

            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt:
            case Key.System when e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt:
                if (_vm.GoBackCommand.CanExecute(null)) _vm.GoBackCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right when Keyboard.Modifiers == ModifierKeys.Alt:
            case Key.System when e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt:
                if (_vm.GoForwardCommand.CanExecute(null)) _vm.GoForwardCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region Viewport & Page Scrolling

    private void ScrollToPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > _vm.Pages.Count) return;

        if (_vm.IsMultiPageLayout)
        {
            // The view model owns the layout, so scrolling can never disagree with where the
            // pages actually are - including when two sit side by side.
            double offset = _vm.GetPageOffset(pageNumber);

            DocumentScrollViewer.ScrollToVerticalOffset(offset);
            _vm.RenderPagesInViewport(offset, DocumentScrollViewer.ViewportHeight);
        }

        ScrollThumbnailIntoView(pageNumber);
    }

    private void ScrollToMatch(int pageNumber, double normX, double normY)
    {
        if (pageNumber < 1 || pageNumber > _vm.Pages.Count) return;

        if (_vm.IsMultiPageLayout)
        {
            var page = _vm.Pages[pageNumber - 1];
            double matchTop = _vm.GetPageOffset(pageNumber) + (normY * page.DisplayHeight);
            double targetVOffset = Math.Max(0, matchTop - (DocumentScrollViewer.ViewportHeight / 3.0));

            double matchLeft = normX * page.DisplayWidth;
            double targetHOffset = Math.Max(0, matchLeft - (DocumentScrollViewer.ViewportWidth / 4.0));

            DocumentScrollViewer.ScrollToVerticalOffset(targetVOffset);
            DocumentScrollViewer.ScrollToHorizontalOffset(targetHOffset);
            _vm.RenderPagesInViewport(targetVOffset, DocumentScrollViewer.ViewportHeight);
        }
        else
        {
            if (_vm.SingleCurrentPage != null)
            {
                double matchTop = normY * _vm.SingleCurrentPage.DisplayHeight;
                double targetVOffset = Math.Max(0, matchTop - (DocumentScrollViewer.ViewportHeight / 3.0));

                double matchLeft = normX * _vm.SingleCurrentPage.DisplayWidth;
                double targetHOffset = Math.Max(0, matchLeft - (DocumentScrollViewer.ViewportWidth / 4.0));

                DocumentScrollViewer.ScrollToVerticalOffset(targetVOffset);
                DocumentScrollViewer.ScrollToHorizontalOffset(targetHOffset);
            }
        }

        ScrollThumbnailIntoView(pageNumber);
    }

    private void DocumentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_vm.IsDocumentLoaded || _vm.Pages.Count == 0) return;

        if (_vm.IsMultiPageLayout)
        {
            // Determine current visible page from the center of the viewport
            double viewportTop = DocumentScrollViewer.VerticalOffset;
            double viewportHeight = DocumentScrollViewer.ViewportHeight;
            double centerOffset = viewportTop + (viewportHeight / 2.0);

            int centerPage = _vm.GetPageAtOffset(centerOffset);

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
                    await _shell.OpenDocumentAsync(firstPdf);
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

    private void SearchResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is SearchMatch match)
        {
            _vm.SelectSearchMatch(match);
        }
    }

    private void SearchResultItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is SearchMatch match)
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
        // Close, not Application.Shutdown. Shutdown tears the application down without
        // raising the window's Closing event, so File > Exit skipped the unsaved-changes
        // prompt entirely and annotations went with it, silently. Closing the window runs
        // the same path as the title bar's close button.
        Close();
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
    /// Only ever reached when the user has explicitly opted in; see <see cref="PrivacySettings"/>.
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
    /// <summary>A sticky note's footprint on the page, in PDF points - about a 24pt square.</summary>
    private const double NoteSizePoints = 24.0;

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
                double normX = Math.Max(0, Math.Min(1, _annotStartPoint.X / page.UnrotatedDisplayWidth));
                double normY = Math.Max(0, Math.Min(1, _annotStartPoint.Y / page.UnrotatedDisplayHeight));

                var annot = new AnnotationModel
                {
                    PageNumber = page.PageNumber,
                    Type = AnnotationType.Note,
                    X = normX,
                    Y = normY,

                    // A sticky note is a fixed size on the page, not a fixed size on screen.
                    // Dividing 24 screen pixels by the zoomed width baked the current zoom
                    // into the stored box: a note dropped at 400% ended up a quarter of the
                    // size, with a hit area far smaller than the icon drawn over it.
                    Width = NoteSizePoints / Math.Max(1.0, page.UnrotatedWidthPt),
                    Height = NoteSizePoints / Math.Max(1.0, page.UnrotatedHeightPt),
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
            double normClickX = clickPos.X / page.UnrotatedDisplayWidth;
            double normClickY = clickPos.Y / page.UnrotatedDisplayHeight;

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
            double startNormX = Math.Max(0, Math.Min(1, _textSelectStartPoint.X / page.UnrotatedDisplayWidth));
            double startNormY = Math.Max(0, Math.Min(1, _textSelectStartPoint.Y / page.UnrotatedDisplayHeight));
            double currNormX = Math.Max(0, Math.Min(1, currentPoint.X / page.UnrotatedDisplayWidth));
            double currNormY = Math.Max(0, Math.Min(1, currentPoint.Y / page.UnrotatedDisplayHeight));

            page.SelectRange(new Point(startNormX, startNormY), new Point(currNormX, currNormY));
            _vm.UpdateSelectionFromPages();
            return;
        }

        // 3. Hovering over text segments: change cursor to I-beam
        if (_vm.ActiveAnnotationTool == null && !_vm.IsPanningEnabled)
        {
            var hoverPt = e.GetPosition(canvas);
            double hoverNormX = hoverPt.X / page.UnrotatedDisplayWidth;
            double hoverNormY = hoverPt.Y / page.UnrotatedDisplayHeight;
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
            bool isInk = _vm.ActiveAnnotationTool == AnnotationType.Ink;

            // A freehand stroke is not the rectangle between where the drag started and where
            // it ended - a loop starts and finishes in nearly the same place. Its box has to
            // come from the stroke itself, or the drawing sits outside the box that carries it
            // and a horizontal line gets thrown away for being less than 4 pixels tall.
            double left, top, width, height;
            if (isInk && _currentInkPoints.Count > 1)
            {
                left = _currentInkPoints.Min(p => p.X);
                top = _currentInkPoints.Min(p => p.Y);
                width = Math.Max(1, _currentInkPoints.Max(p => p.X) - left);
                height = Math.Max(1, _currentInkPoints.Max(p => p.Y) - top);
            }
            else
            {
                left = Math.Min(_annotStartPoint.X, endPoint.X);
                top = Math.Min(_annotStartPoint.Y, endPoint.Y);
                width = Math.Max(10, Math.Abs(endPoint.X - _annotStartPoint.X));
                height = Math.Max(10, Math.Abs(endPoint.Y - _annotStartPoint.Y));
            }

            double normX = Math.Max(0, left / page.UnrotatedDisplayWidth);
            double normY = Math.Max(0, top / page.UnrotatedDisplayHeight);
            double normW = Math.Min(1 - normX, width / page.UnrotatedDisplayWidth);
            double normH = Math.Min(1 - normY, height / page.UnrotatedDisplayHeight);

            bool bigEnough = isInk
                ? _currentInkPoints.Count > 1 && (width > 4 || height > 4)
                : width > 4 && height > 4;

            if (_vm.ActiveAnnotationTool.HasValue && bigEnough)
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
                    Contents = string.Empty,

                    // A highlight has to let the text through; a pen stroke or a shape does
                    // not. The model's 0.5 default was drawing both at half strength, and it
                    // is also the alpha that gets written into the file.
                    Opacity = _vm.ActiveAnnotationTool.Value == AnnotationType.Highlight ? 0.4 : 1.0
                };

                if (_vm.ActiveAnnotationTool.Value == AnnotationType.Ink && _currentInkPoints.Count > 1)
                {
                    annot.InkStrokes = new System.Collections.Generic.List<System.Collections.Generic.List<Point>>
                    {
                        _currentInkPoints
                            .Select(p => new Point(p.X / page.UnrotatedDisplayWidth, p.Y / page.UnrotatedDisplayHeight))
                            .ToList()
                    };
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
            double startNormX = Math.Max(0, Math.Min(1, _textSelectStartPoint.X / page.UnrotatedDisplayWidth));
            double startNormY = Math.Max(0, Math.Min(1, _textSelectStartPoint.Y / page.UnrotatedDisplayHeight));
            double currNormX = Math.Max(0, Math.Min(1, currentPoint.X / page.UnrotatedDisplayWidth));
            double currNormY = Math.Max(0, Math.Min(1, currentPoint.Y / page.UnrotatedDisplayHeight));

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

            EditExistingAnnotation(annot);
            e.Handled = true;
        }
    }

    private void AnnotationItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is AnnotationModel annot)
        {
            _vm.CurrentPageNumber = annot.PageNumber;
            ScrollToPage(annot.PageNumber);
            EditExistingAnnotation(annot);
            e.Handled = true;
        }
    }

    private void EditAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is AnnotationModel annot)
        {
            _vm.CurrentPageNumber = annot.PageNumber;
            ScrollToPage(annot.PageNumber);
            EditExistingAnnotation(annot);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the comment editor and, if the edit is confirmed, tells the document that it now
    /// has work that is not on disk. Every route into this dialog used to skip that, so an
    /// edited comment left no asterisk, no enabled Save, and no prompt on the way out.
    /// </summary>
    private void EditExistingAnnotation(AnnotationModel annot)
    {
        var dialog = new EditCommentDialog(annot, isNew: false) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.IsConfirmed)
        {
            _vm.NoteAnnotationEdited(annot);
        }
    }

    #endregion
}
