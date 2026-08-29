using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfViewer.Core.Commands;
using PdfViewer.Core.Licensing;
using PdfViewer.Core.Rendering;
using PdfViewer.Core.Session;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

public enum ViewLayoutMode
{
    Continuous,
    SinglePage
}

public enum PageFitMode
{
    Custom,
    FitWidth,
    FitPage
}

public partial class MainViewModel : ObservableObject
{
    private readonly IPdfDocumentService _docService;
    public IPdfDocumentService DocumentService => _docService;
    private readonly LruPageCache _cache;
    private readonly AsyncPageRenderer _renderer;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private bool _isDocumentLoaded;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _windowTitle = "PDF Viewer";

    [ObservableProperty]
    private DocumentMetadata? _metadata;

    [ObservableProperty]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    private int _pageCount = 0;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private ViewLayoutMode _viewMode = ViewLayoutMode.Continuous;

    [ObservableProperty]
    private PageFitMode _fitMode = PageFitMode.FitWidth;

    [ObservableProperty]
    private int _rotationAngle = 0;

    [ObservableProperty]
    private bool _isPanningEnabled = false;

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [ObservableProperty]
    private int _selectedSidebarTab = 0; // 0=Thumbnails, 1=Bookmarks, 2=Search

    [ObservableProperty]
    private bool _isSearchOpen = false;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching = false;

    [ObservableProperty]
    private bool _searchMatchCase = false;

    [ObservableProperty]
    private int _currentSearchMatchIndex = 0;

    [ObservableProperty]
    private SearchMatch? _selectedSearchMatch;

    [ObservableProperty]
    private string _searchSummaryText = string.Empty;

    [ObservableProperty]
    private string _selectedText = string.Empty;

    [ObservableProperty]
    private bool _hasTextSelection;

    [ObservableProperty]
    private PageViewModel? _singleCurrentPage;

    [ObservableProperty]
    private AppTheme _currentTheme = AppTheme.Light;

    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new();
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<SearchMatch> SearchMatches { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();
    public ObservableCollection<AnnotationModel> AllAnnotations { get; } = new();

    public string ZoomPercentageText => $"{(int)Math.Round(ZoomLevel * 100)}%";
    public string ApplicationVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _availableUpdateVersion = string.Empty;

    [ObservableProperty]
    private UpdateInfo? _latestUpdateInfo;

    [ObservableProperty]
    private AnnotationType? _activeAnnotationTool;

    public ObservableCollection<string> AnnotationColors { get; } = new()
    {
        "#FFFF00", // Yellow (Standard Highlighter)
        "#FFD700", // Gold
        "#00E676", // Mint / Light Green
        "#00E5FF", // Cyan
        "#2979FF", // Blue
        "#FF4081", // Pink / Magenta
        "#FF5252", // Coral Red
        "#FF9100", // Orange
        "#AA00FF", // Purple
        "#212121"  // Charcoal Black
    };

    [ObservableProperty]
    private string _selectedAnnotationColor = "#FFFF00"; // Classic Yellow default

    [ObservableProperty]
    private double _selectedAnnotationThickness = 2.0;

    [ObservableProperty]
    private string _selectedAnnotationAuthor = Environment.UserName;

    [RelayCommand]
    public void SelectAnnotationColor(string colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            SelectedAnnotationColor = colorHex;
        }
    }

    public async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var info = await UpdateService.CheckForUpdatesAsync();
            if (info.IsUpdateAvailable)
            {
                LatestUpdateInfo = info;
                AvailableUpdateVersion = info.LatestVersion;
                IsUpdateAvailable = true;
            }
        }
        catch
        {
            // Silent fallback on network failure or offline mode
        }
    }

    // Dialog & UI callback delegates
    public Func<string, Task<string?>>? RequestPasswordFunc { get; set; }
    public Action<DocumentMetadata>? ShowPropertiesAction { get; set; }
    public Func<DocumentMetadata, (bool Confirmed, string OutDir, string Prefix, int Start, int End, string Format, int Dpi)>? ShowExportDialogFunc { get; set; }
    public Func<DocumentMetadata, (bool Confirmed, string TargetPath, AnnotationSaveMode Mode)>? ShowSaveAnnotatedDialogFunc { get; set; }
    public Func<IPdfDocumentService, int, bool>? ShowPrintDialogFunc { get; set; }
    public Action<int>? ScrollToPageAction { get; set; }
    public Action<int, double, double>? ScrollToMatchAction { get; set; }
    public Func<(double ViewportWidth, double ViewportHeight)>? GetViewportSizeFunc { get; set; }

    public DocumentSession Session { get; } = new();
    public ICommandHistory CommandHistory { get; } = new CommandHistory();
    public IFeatureGate FeatureGate { get; } = new DefaultFeatureGate();

    public MainViewModel()
    {
        _docService = PdfDocumentServiceFactory.CreateService();
        _cache = new LruPageCache(60);
        _renderer = new AsyncPageRenderer(_docService, _cache);

        ReloadRecentFiles();
        CurrentTheme = ThemeManager.CurrentTheme;
        ThemeManager.ThemeChanged += theme => CurrentTheme = theme;
    }

    public void ReloadRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var file in RecentFilesService.LoadRecentFiles())
        {
            RecentFiles.Add(file);
        }
    }

    #region Document Loading & Handling

    [RelayCommand]
    public async Task OpenFileDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
            Title = "Open PDF Document"
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadDocumentAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    public async Task OpenSampleDocumentAsync()
    {
        string samplePath = FindSampleDocumentPath();
        if (!File.Exists(samplePath))
        {
            samplePath = SamplePdfGenerator.GenerateSamplePdf(samplePath);
        }
        await LoadDocumentAsync(samplePath);
    }

    private static string FindSampleDocumentPath()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")))
            {
                return Path.Combine(dir.FullName, "samples", "SampleDocument.pdf");
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "samples", "SampleDocument.pdf");
    }

    [RelayCommand]
    public async Task OpenRecentFileAsync(string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            await LoadDocumentAsync(filePath);
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            MessageBox.Show($"File no longer exists:\n{filePath}", "Open File Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ReloadRecentFiles();
        }
    }

    public async Task LoadDocumentAsync(string filePath, string? password = null)
    {
        StatusText = $"Opening {Path.GetFileName(filePath)}...";
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();

        try
        {
            DocumentMetadata meta;
            try
            {
                meta = await _docService.OpenDocumentAsync(filePath, password);
            }
            catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                      ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            {
                // Prompt user for password
                if (RequestPasswordFunc != null)
                {
                    string? userPass = await RequestPasswordFunc(Path.GetFileName(filePath));
                    if (!string.IsNullOrEmpty(userPass))
                    {
                        await LoadDocumentAsync(filePath, userPass);
                        return;
                    }
                }
                StatusText = "Password required to open document.";
                return;
            }

            Metadata = meta;
            PageCount = meta.PageCount;
            CurrentPageNumber = 1;
            RotationAngle = 0;
            IsDocumentLoaded = true;
            WindowTitle = $"{meta.FileName} - PDF Viewer";
            StatusText = $"Loaded {meta.FileName} ({meta.PageCount} pages)";

            RecentFilesService.AddRecentFile(filePath);
            ReloadRecentFiles();

            // Clear collections
            _cache.Clear();
            Pages.Clear();
            Thumbnails.Clear();
            Bookmarks.Clear();
            SearchMatches.Clear();
            AllAnnotations.Clear();
            SearchSummaryText = string.Empty;

            // Populate Bookmarks
            var bookmarks = _docService.ExtractBookmarks();
            foreach (var b in bookmarks)
            {
                Bookmarks.Add(b);
            }

            // Load Existing Document Annotations
            var existingAnnots = _docService.LoadExistingAnnotations();
            foreach (var a in existingAnnots)
            {
                AllAnnotations.Add(a);
            }

            // Build Page and Thumbnail ViewModels
            for (int i = 1; i <= meta.PageCount; i++)
            {
                var (w, h) = _docService.GetPageDimensions(i);
                var pageVm = new PageViewModel(i, w, h);
                pageVm.UpdateScale(ZoomLevel);

                // Attach annotations on this page
                foreach (var a in existingAnnots)
                {
                    if (a.PageNumber == i)
                    {
                        pageVm.AnnotationsOnPage.Add(a);
                    }
                }

                Pages.Add(pageVm);

                var thumbVm = new ThumbnailViewModel(i);
                if (i == 1) thumbVm.IsCurrentPage = true;
                Thumbnails.Add(thumbVm);
            }

            CurrentPageNumber = 1;
            UpdateSingleCurrentPage();

            // Calculate initial fit if requested
            if (FitMode != PageFitMode.Custom)
            {
                ApplyFitMode();
            }

            ScrollToPageAction?.Invoke(1);

            // Trigger asynchronous render
            await RenderVisiblePagesAsync();
            _ = RenderThumbnailsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open PDF document:\n{ex.Message}", "Error Loading PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CloseDocument()
    {
        _renderCts?.Cancel();
        _searchCts?.Cancel();
        _docService.CloseDocument();
        _cache.Clear();

        ClearSelection();
        SelectedText = string.Empty;
        HasTextSelection = false;

        Pages.Clear();
        Thumbnails.Clear();
        Bookmarks.Clear();
        SearchMatches.Clear();
        AllAnnotations.Clear();
        ActiveAnnotationTool = null;

        IsDocumentLoaded = false;
        Metadata = null;
        PageCount = 0;
        CurrentPageNumber = 1;
        SingleCurrentPage = null;
        WindowTitle = "PDF Viewer";
        StatusText = "Ready";
    }

    #endregion

    #region Page Navigation

    partial void OnCurrentPageNumberChanged(int value)
    {
        if (value < 1) CurrentPageNumber = 1;
        else if (value > PageCount && PageCount > 0) CurrentPageNumber = PageCount;

        // Update thumbnail active indicators
        for (int i = 0; i < Thumbnails.Count; i++)
        {
            Thumbnails[i].IsCurrentPage = (Thumbnails[i].PageNumber == CurrentPageNumber);
        }

        UpdateSingleCurrentPage();
    }

    public void SetCurrentPageFromScroll(int centerPage)
    {
        if (centerPage >= 1 && (PageCount == 0 || centerPage <= PageCount))
        {
            CurrentPageNumber = centerPage;
        }
    }

    public void NavigateToPage(int targetPage)
    {
        if (targetPage < 1 || (PageCount > 0 && targetPage > PageCount)) return;

        CurrentPageNumber = targetPage;
        ScrollToPageAction?.Invoke(targetPage);

        if (ViewMode == ViewLayoutMode.Continuous)
        {
            _ = RenderVisiblePagesAsync();
        }
    }

    private void UpdateSingleCurrentPage()
    {
        if (Pages.Count >= CurrentPageNumber && CurrentPageNumber >= 1)
        {
            SingleCurrentPage = Pages[CurrentPageNumber - 1];
            if (ViewMode == ViewLayoutMode.SinglePage)
            {
                _ = SingleCurrentPage.LoadImageAsync(_renderer, GetCurrentDpi(), RotationAngle, CancellationToken.None);
                _ = SingleCurrentPage.LoadTextSegmentsAsync(_docService, CancellationToken.None);
            }
        }
        else
        {
            SingleCurrentPage = null;
        }
    }

    [RelayCommand]
    public void NextPage() => NavigateToPage(CurrentPageNumber + 1);

    [RelayCommand]
    public void PreviousPage() => NavigateToPage(CurrentPageNumber - 1);

    [RelayCommand]
    public void FirstPage() => NavigateToPage(1);

    [RelayCommand]
    public void LastPage() => NavigateToPage(PageCount);

    [RelayCommand]
    public void GoToPage(int pageNumber) => NavigateToPage(pageNumber);

    [RelayCommand]
    public void NavigateBookmark(BookmarkItem bookmark)
    {
        if (bookmark != null && bookmark.TargetPageNumber >= 1 && bookmark.TargetPageNumber <= PageCount)
        {
            NavigateToPage(bookmark.TargetPageNumber);
        }
    }

    #endregion

    #region Zoom & View Modes

    partial void OnZoomLevelChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomPercentageText));

        foreach (var page in Pages)
        {
            page.UpdateScale(value);
        }

        _ = RenderVisiblePagesAsync();
    }

    [RelayCommand]
    public void ZoomIn()
    {
        FitMode = PageFitMode.Custom;
        ZoomLevel = Math.Min(5.0, Math.Round(ZoomLevel + 0.15, 2));
    }

    [RelayCommand]
    public void ZoomOut()
    {
        FitMode = PageFitMode.Custom;
        ZoomLevel = Math.Max(0.25, Math.Round(ZoomLevel - 0.15, 2));
    }

    [RelayCommand]
    public void SetZoom(double zoom)
    {
        FitMode = PageFitMode.Custom;
        ZoomLevel = Math.Clamp(zoom, 0.25, 5.0);
    }

    [RelayCommand]
    public void FitWidth()
    {
        FitMode = PageFitMode.FitWidth;
        ApplyFitMode();
    }

    [RelayCommand]
    public void FitPage()
    {
        FitMode = PageFitMode.FitPage;
        ApplyFitMode();
    }

    public void ApplyFitMode()
    {
        if (!IsDocumentLoaded || Pages.Count == 0 || GetViewportSizeFunc == null) return;

        var (viewportWidth, viewportHeight) = GetViewportSizeFunc();
        if (viewportWidth <= 50 || viewportHeight <= 50) return;

        var targetPage = SingleCurrentPage ?? Pages[0];
        double docPageWidth = RotationAngle == 90 || RotationAngle == 270 ? targetPage.HeightPt : targetPage.WidthPt;
        double docPageHeight = RotationAngle == 90 || RotationAngle == 270 ? targetPage.WidthPt : targetPage.HeightPt;

        if (FitMode == PageFitMode.FitWidth)
        {
            double availableWidth = viewportWidth - 40; // account for scrollbar & margins
            if (availableWidth > 0 && docPageWidth > 0)
            {
                ZoomLevel = Math.Clamp(availableWidth / docPageWidth, 0.25, 5.0);
            }
        }
        else if (FitMode == PageFitMode.FitPage)
        {
            double availableWidth = viewportWidth - 40;
            double availableHeight = viewportHeight - 40;
            if (availableWidth > 0 && availableHeight > 0 && docPageWidth > 0 && docPageHeight > 0)
            {
                double scaleX = availableWidth / docPageWidth;
                double scaleY = availableHeight / docPageHeight;
                ZoomLevel = Math.Clamp(Math.Min(scaleX, scaleY), 0.25, 5.0);
            }
        }
    }

    [RelayCommand]
    public void RotateClockwise()
    {
        RotationAngle = (RotationAngle + 90) % 360;
        OnRotationChanged();
    }

    [RelayCommand]
    public void RotateCounterClockwise()
    {
        RotationAngle = (RotationAngle + 270) % 360;
        OnRotationChanged();
    }

    private void OnRotationChanged()
    {
        foreach (var page in Pages)
        {
            page.UpdateRotation(RotationAngle);
            page.UnloadImage();
        }
        foreach (var thumb in Thumbnails)
        {
            thumb.UnloadThumbnail();
        }
        _cache.Clear();
        _ = RenderVisiblePagesAsync();
        _ = RenderThumbnailsAsync();
    }

    [RelayCommand]
    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ViewLayoutMode.Continuous ? ViewLayoutMode.SinglePage : ViewLayoutMode.Continuous;
        UpdateSingleCurrentPage();
        _ = RenderVisiblePagesAsync();
    }

    [RelayCommand]
    public void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
    }

    partial void OnIsSidebarOpenChanged(bool value)
    {
        if (IsDocumentLoaded && FitMode != PageFitMode.Custom)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ApplyFitMode();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    [RelayCommand]
    public void TogglePanTool()
    {
        IsPanningEnabled = !IsPanningEnabled;
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        ThemeManager.ToggleTheme();
    }

    #endregion

    #region Rendering

    private int GetCurrentDpi()
    {
        // Dynamically scale DPI with zoom level for crystal clear text
        if (ZoomLevel <= 1.0) return 150;
        if (ZoomLevel <= 2.0) return 200;
        return 300;
    }

    public void RenderPagesInViewport(double viewportTop, double viewportHeight)
    {
        if (!IsDocumentLoaded || Pages.Count == 0 || ViewMode == ViewLayoutMode.SinglePage) return;

        double buffer = Math.Max(viewportHeight * 1.5, 1200); // 1.5 screens buffer ahead & behind
        double minOffset = Math.Max(0, viewportTop - buffer);
        double maxOffset = viewportTop + viewportHeight + buffer;

        double accumulated = 0;
        int dpi = GetCurrentDpi();

        for (int i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            double pageTop = accumulated;
            double pageBottom = accumulated + page.DisplayHeight + 20;

            if (pageBottom >= minOffset && pageTop <= maxOffset)
            {
                if (page.RenderedImage == null && !page.IsLoading)
                {
                    _ = page.LoadImageAsync(_renderer, dpi, RotationAngle, CancellationToken.None);
                }
                if (!page.IsTextExtracted && !page.IsExtractingText)
                {
                    _ = page.LoadTextSegmentsAsync(_docService, CancellationToken.None);
                }
            }
            accumulated = pageBottom;
        }
    }

    public async Task RenderVisiblePagesAsync()
    {
        if (!IsDocumentLoaded) return;

        int dpi = GetCurrentDpi();

        if (ViewMode == ViewLayoutMode.SinglePage)
        {
            if (SingleCurrentPage != null)
            {
                await SingleCurrentPage.LoadImageAsync(_renderer, dpi, RotationAngle, CancellationToken.None);
                _ = SingleCurrentPage.LoadTextSegmentsAsync(_docService, CancellationToken.None);
            }
            return;
        }

        // For Continuous mode: Render current and nearby pages first
        int cur = CurrentPageNumber - 1;
        var priorityIndices = new List<int>();

        if (cur >= 0 && cur < Pages.Count) priorityIndices.Add(cur);
        for (int offset = 1; offset <= 5; offset++)
        {
            if (cur - offset >= 0) priorityIndices.Add(cur - offset);
            if (cur + offset < Pages.Count) priorityIndices.Add(cur + offset);
        }

        foreach (var idx in priorityIndices)
        {
            if (Pages[idx].RenderedImage == null && !Pages[idx].IsLoading)
            {
                _ = Pages[idx].LoadImageAsync(_renderer, dpi, RotationAngle, CancellationToken.None);
            }
            if (!Pages[idx].IsTextExtracted && !Pages[idx].IsExtractingText)
            {
                _ = Pages[idx].LoadTextSegmentsAsync(_docService, CancellationToken.None);
            }
        }

        // Start gentle background prefetch for remaining pages
        _ = PrefetchAllPagesAsync(dpi, RotationAngle);
    }

    private async Task PrefetchAllPagesAsync(int dpi, int rotation)
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            if (!IsDocumentLoaded || ViewMode == ViewLayoutMode.SinglePage) break;
            var page = Pages[i];
            if (page.RenderedImage == null && !page.IsLoading)
            {
                await page.LoadImageAsync(_renderer, dpi, rotation, CancellationToken.None);
                await Task.Delay(25); // Gentle yield to maintain smooth 60 FPS UI
            }
            if (!page.IsTextExtracted && !page.IsExtractingText)
            {
                _ = page.LoadTextSegmentsAsync(_docService, CancellationToken.None);
            }
        }
    }

    public async Task RenderThumbnailsAsync()
    {
        if (!IsDocumentLoaded) return;

        for (int i = 0; i < Thumbnails.Count; i++)
        {
            if (!IsDocumentLoaded) break;
            var thumb = Thumbnails[i];
            if (thumb.ThumbnailImage == null)
            {
                await thumb.LoadThumbnailAsync(_renderer, RotationAngle, CancellationToken.None);
                await Task.Delay(15);
            }
        }
    }

    #endregion

    #region Text Selection & Copy

    public void UpdateSelectionFromPages()
    {
        var sb = new System.Text.StringBuilder();
        bool hasAny = false;

        foreach (var page in Pages)
        {
            if (page.SelectedSegments.Count > 0)
            {
                hasAny = true;
                string pageTxt = page.GetSelectedText();
                if (!string.IsNullOrEmpty(pageTxt))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(pageTxt);
                }
            }
        }

        SelectedText = sb.ToString();
        HasTextSelection = hasAny && !string.IsNullOrEmpty(SelectedText);

        if (HasTextSelection)
        {
            StatusText = $"{SelectedText.Length} character(s) selected (Ctrl+C to copy)";
        }
    }

    [RelayCommand]
    public void CopySelectedText()
    {
        if (string.IsNullOrEmpty(SelectedText))
        {
            UpdateSelectionFromPages();
        }

        if (!string.IsNullOrEmpty(SelectedText))
        {
            try
            {
                Clipboard.SetText(SelectedText);
                StatusText = $"Copied {SelectedText.Length} character(s) to clipboard";
            }
            catch (Exception ex)
            {
                StatusText = $"Clipboard copy error: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public void SelectAllText()
    {
        if (!IsDocumentLoaded || Pages.Count == 0) return;

        int targetPageNum = (ViewMode == ViewLayoutMode.SinglePage && SingleCurrentPage != null)
            ? SingleCurrentPage.PageNumber
            : CurrentPageNumber;

        if (targetPageNum >= 1 && targetPageNum <= Pages.Count)
        {
            var page = Pages[targetPageNum - 1];
            if (!page.IsTextExtracted)
            {
                page.LoadTextSegmentsAsync(_docService).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        page.SelectAllText();
                        UpdateSelectionFromPages();
                    });
                });
            }
            else
            {
                page.SelectAllText();
                UpdateSelectionFromPages();
            }
        }
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var page in Pages)
        {
            page.ClearTextSelection();
        }
        SelectedText = string.Empty;
        HasTextSelection = false;
    }

    [RelayCommand]
    public void HighlightSelectedText()
    {
        if (!HasTextSelection && Pages.All(p => p.SelectedSegments.Count == 0)) return;

        foreach (var page in Pages)
        {
            if (page.SelectedSegments.Count > 0)
            {
                var sorted = page.SelectedSegments.OrderBy(s => s.SegmentIndex).ToList();
                double minX = sorted.Min(s => s.X);
                double minY = sorted.Min(s => s.Y);
                double maxX = sorted.Max(s => s.X + s.Width);
                double maxY = sorted.Max(s => s.Y + s.Height);

                var annot = new AnnotationModel
                {
                    PageNumber = page.PageNumber,
                    Type = AnnotationType.Highlight,
                    X = minX,
                    Y = minY,
                    Width = Math.Max(0.01, maxX - minX),
                    Height = Math.Max(0.01, maxY - minY),
                    ColorHex = SelectedAnnotationColor,
                    Opacity = 0.45,
                    Author = SelectedAnnotationAuthor,
                    Title = "Highlight",
                    Contents = page.GetSelectedText()
                };

                AddAnnotation(annot);
            }
        }

        ClearSelection();
    }

    #endregion

    #region Text Search

    [RelayCommand]
    public void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (IsSearchOpen)
        {
            SelectedSidebarTab = 2; // Switch to Search tab
        }
        else
        {
            // Clear highlights when closing search
            foreach (var page in Pages)
            {
                page.MatchesOnPage.Clear();
            }
            SearchMatches.Clear();
            SearchSummaryText = string.Empty;
        }
    }

    [RelayCommand]
    public async Task ExecuteSearchAsync()
    {
        // Clear previous highlights from all pages
        foreach (var page in Pages)
        {
            page.MatchesOnPage.Clear();
        }

        if (string.IsNullOrWhiteSpace(SearchQuery) || !IsDocumentLoaded)
        {
            SearchMatches.Clear();
            SearchSummaryText = string.Empty;
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        SearchSummaryText = "Searching document...";
        SearchMatches.Clear();

        try
        {
            var results = await _docService.SearchTextAsync(SearchQuery, SearchMatchCase, ct);
            if (!ct.IsCancellationRequested)
            {
                foreach (var match in results)
                {
                    SearchMatches.Add(match);
                    if (match.PageNumber >= 1 && match.PageNumber <= Pages.Count)
                    {
                        Pages[match.PageNumber - 1].MatchesOnPage.Add(match);
                    }
                }

                if (SearchMatches.Count > 0)
                {
                    CurrentSearchMatchIndex = 1;
                    SearchMatches[0].IsCurrentMatch = true;
                    SearchSummaryText = $"{SearchMatches.Count} matches found";
                    NavigateToMatch(SearchMatches[0]);
                }
                else
                {
                    SearchSummaryText = "No matches found.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            SearchSummaryText = "Search canceled.";
        }
        catch (Exception ex)
        {
            SearchSummaryText = $"Search error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public void NextSearchMatch()
    {
        if (SearchMatches.Count == 0) return;
        CurrentSearchMatchIndex = (CurrentSearchMatchIndex % SearchMatches.Count) + 1;
        NavigateToMatch(SearchMatches[CurrentSearchMatchIndex - 1]);
    }

    [RelayCommand]
    public void PreviousSearchMatch()
    {
        if (SearchMatches.Count == 0) return;
        CurrentSearchMatchIndex = CurrentSearchMatchIndex <= 1 ? SearchMatches.Count : CurrentSearchMatchIndex - 1;
        NavigateToMatch(SearchMatches[CurrentSearchMatchIndex - 1]);
    }

    partial void OnSelectedSearchMatchChanged(SearchMatch? value)
    {
        if (value != null)
        {
            SelectSearchMatch(value);
        }
    }

    [RelayCommand]
    public void SelectSearchMatch(SearchMatch match)
    {
        if (match != null)
        {
            int idx = SearchMatches.IndexOf(match);
            if (idx >= 0)
            {
                CurrentSearchMatchIndex = idx + 1;
                NavigateToMatch(match);
            }
        }
    }

    private void NavigateToMatch(SearchMatch match)
    {
        // Unmark previous matches
        foreach (var m in SearchMatches)
        {
            m.IsCurrentMatch = false;
        }
        match.IsCurrentMatch = true;

        if (SelectedSearchMatch != match)
        {
            SelectedSearchMatch = match;
        }

        NavigateToPage(match.PageNumber);
        ScrollToMatchAction?.Invoke(match.PageNumber, match.X, match.Y);
        SearchSummaryText = $"Match {CurrentSearchMatchIndex} of {SearchMatches.Count} (Page {match.PageNumber})";
    }

    #endregion

    #region Annotations & Multi-Mode Saving

    [RelayCommand]
    public void ToggleAnnotationTool(string toolName)
    {
        if (Enum.TryParse<AnnotationType>(toolName, true, out var tool))
        {
            if (ActiveAnnotationTool == tool)
                ActiveAnnotationTool = null; // Toggle off
            else
            {
                ActiveAnnotationTool = tool; // Toggle on
                IsPanningEnabled = false; // Disable pan tool when annotating
            }
        }
        else
        {
            ActiveAnnotationTool = null;
        }
    }

    [RelayCommand]
    public void AddAnnotation(AnnotationModel annot)
    {
        AllAnnotations.Add(annot);
        if (annot.PageNumber >= 1 && annot.PageNumber <= Pages.Count)
        {
            Pages[annot.PageNumber - 1].AnnotationsOnPage.Add(annot);
        }
        StatusText = $"Added {annot.Type} annotation on page {annot.PageNumber}";
    }

    [RelayCommand]
    public void DeleteAnnotation(AnnotationModel annot)
    {
        if (annot == null) return;
        AllAnnotations.Remove(annot);
        if (annot.PageNumber >= 1 && annot.PageNumber <= Pages.Count)
        {
            Pages[annot.PageNumber - 1].AnnotationsOnPage.Remove(annot);
        }
        StatusText = $"Removed annotation from page {annot.PageNumber}";
    }

    [RelayCommand]
    public void ClearAllAnnotations()
    {
        AllAnnotations.Clear();
        foreach (var page in Pages)
        {
            page.AnnotationsOnPage.Clear();
        }
        StatusText = "Cleared all annotations.";
    }

    [RelayCommand]
    public async Task SaveAnnotatedAsAsync()
    {
        if (!IsDocumentLoaded || Metadata == null) return;

        if (ShowSaveAnnotatedDialogFunc != null)
        {
            var result = ShowSaveAnnotatedDialogFunc(Metadata);
            if (result.Confirmed && !string.IsNullOrWhiteSpace(result.TargetPath))
            {
                try
                {
                    StatusText = "Saving annotated document...";
                    await _docService.SaveAnnotatedDocumentAsync(
                        result.TargetPath,
                        result.Mode,
                        AllAnnotations,
                        Metadata.FilePath);

                    StatusText = $"Saved annotated document ({result.Mode}): {Path.GetFileName(result.TargetPath)}";
                    MessageBox.Show(
                        $"Annotated document successfully saved ({result.Mode}):\n{result.TargetPath}",
                        "Save Successful",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    StatusText = $"Save error: {ex.Message}";
                    MessageBox.Show(
                        $"Failed to save annotated document:\n{ex.Message}",
                        "Save Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
    }

    #endregion

    #region Export, Print & Properties

    [RelayCommand]
    public void ShowProperties()
    {
        if (Metadata != null)
        {
            ShowPropertiesAction?.Invoke(Metadata);
        }
    }

    [RelayCommand]
    public void Print()
    {
        if (!IsDocumentLoaded) return;

        try
        {
            if (ShowPrintDialogFunc != null)
            {
                bool printed = ShowPrintDialogFunc(_docService, CurrentPageNumber);
                if (printed)
                {
                    StatusText = "Print job sent to printer.";
                }
                return;
            }

            var printDialog = new PrintDialog
            {
                UserPageRangeEnabled = true,
                MinPage = 1,
                MaxPage = (uint)PageCount
            };

            if (printDialog.ShowDialog() == true)
            {
                StatusText = "Printing document...";
                int start = 1;
                int end = PageCount;

                if (printDialog.PageRangeSelection == PageRangeSelection.UserPages)
                {
                    start = printDialog.PageRange.PageFrom;
                    end = printDialog.PageRange.PageTo;
                }

                _docService.PrintDocument(printDialog, start, end, RotationAngle);
                StatusText = "Print job sent to printer.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Print failed: {ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Print error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ExportImagesAsync()
    {
        if (!IsDocumentLoaded || Metadata == null || ShowExportDialogFunc == null) return;

        var (confirmed, outDir, prefix, start, end, format, dpi) = ShowExportDialogFunc(Metadata);
        if (!confirmed) return;

        StatusText = $"Exporting pages {start} to {end} as {format}...";

        try
        {
            var progress = new Progress<double>(pct =>
            {
                StatusText = $"Exporting: {pct:F0}%";
            });

            await _docService.ExportPagesToImagesAsync(outDir, prefix, start, end, format, dpi, progress);
            StatusText = $"Successfully exported {end - start + 1} images to {outDir}";
            MessageBox.Show($"Successfully exported {end - start + 1} pages to:\n{outDir}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Export error: {ex.Message}";
        }
    }

    #endregion
}
