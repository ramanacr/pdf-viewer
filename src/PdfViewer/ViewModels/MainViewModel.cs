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
    private readonly PdfDocumentService _docService;
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
    private string _searchSummaryText = string.Empty;

    [ObservableProperty]
    private PageViewModel? _singleCurrentPage;

    [ObservableProperty]
    private AppTheme _currentTheme = AppTheme.Light;

    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new();
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<SearchMatch> SearchMatches { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();

    public string ZoomPercentageText => $"{(int)Math.Round(ZoomLevel * 100)}%";
    public string ApplicationVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // Dialog & UI callback delegates
    public Func<string, Task<string?>>? RequestPasswordFunc { get; set; }
    public Action<DocumentMetadata>? ShowPropertiesAction { get; set; }
    public Func<DocumentMetadata, (bool Confirmed, string OutDir, string Prefix, int Start, int End, string Format, int Dpi)>? ShowExportDialogFunc { get; set; }
    public Action<int>? ScrollToPageAction { get; set; }
    public Func<(double ViewportWidth, double ViewportHeight)>? GetViewportSizeFunc { get; set; }

    public MainViewModel()
    {
        _docService = new PdfDocumentService();
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
            if (File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")) || File.Exists(Path.Combine(dir.FullName, "Aspose.Total.lic")))
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
            SearchSummaryText = string.Empty;

            // Populate Bookmarks
            var bookmarks = _docService.ExtractBookmarks();
            foreach (var b in bookmarks)
            {
                Bookmarks.Add(b);
            }

            // Build Page and Thumbnail ViewModels
            for (int i = 1; i <= meta.PageCount; i++)
            {
                var (w, h) = _docService.GetPageDimensions(i);
                var pageVm = new PageViewModel(i, w, h);
                pageVm.UpdateScale(ZoomLevel);
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

        Pages.Clear();
        Thumbnails.Clear();
        Bookmarks.Clear();
        SearchMatches.Clear();

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

    #region Text Search

    [RelayCommand]
    public void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (IsSearchOpen)
        {
            SelectedSidebarTab = 2; // Switch to Search tab
        }
    }

    [RelayCommand]
    public async Task ExecuteSearchAsync()
    {
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
                }

                if (SearchMatches.Count > 0)
                {
                    CurrentSearchMatchIndex = 1;
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
        NavigateToPage(match.PageNumber);
        SearchSummaryText = $"Match {CurrentSearchMatchIndex} of {SearchMatches.Count} (Page {match.PageNumber})";
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

                _docService.PrintDocument(printDialog, start, end);
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
