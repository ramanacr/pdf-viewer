using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfViewer.Core.Commands;
using PdfEngine.Ocr;
using PdfViewer.Core.Licensing;
using PdfViewer.Core.Ocr;
using PdfViewer.Core.Rendering;
using PdfViewer.Core.Security;
using PdfViewer.Core.Session;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

public enum ViewLayoutMode
{
    Continuous,
    SinglePage,

    /// <summary>Two pages side by side, scrolling vertically. Reading a book, not a scroll.</summary>
    Facing
}

/// <summary>
/// One row of the laid-out document: which pages sit in it, where it starts, and how tall it
/// is. Everything that needs to know where a page lives on screen - scrolling to it, deciding
/// which page the viewport is centred on, choosing what to render - asks for these rather
/// than walking the page list itself.
/// </summary>
public readonly record struct PageRowMetrics(int FirstPageIndex, int PageCount, double Top, double Height)
{
    public double Bottom => Top + Height;

    public bool Contains(int pageIndex) =>
        pageIndex >= FirstPageIndex && pageIndex < FirstPageIndex + PageCount;
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
    public Action<string, string, MessageBoxButton, MessageBoxImage>? ShowMessageBoxAction { get; set; }

    /// <summary>
    /// Shows the page organizer for a document of the given page count, and returns the
    /// arrangement the user built, or null when cancelled.
    /// </summary>
    public Func<int, string, IReadOnlyList<PdfEngine.Pages.PageArrangementEntry>?>? ShowOrganizePagesFunc { get; set; }

    /// <summary>
    /// Lists the document's embedded files and returns the one the user chose to extract,
    /// or null when nothing was chosen.
    /// </summary>
    public Func<IReadOnlyList<PdfEngine.Safety.EmbeddedFileInfo>, string, PdfEngine.Safety.EmbeddedFileInfo?>? ShowAttachmentsFunc { get; set; }

    /// <summary>Asks a yes/no question. Returns true only on an explicit yes.</summary>
    public Func<string, string, bool>? ConfirmFunc { get; set; }

    private bool Confirm(string message, string caption) => ConfirmFunc?.Invoke(message, caption) ?? false;

    // Set while a security-policy refusal has already been reported for the current
    // document, so a 500-page document cannot produce 500 dialogs.
    private bool _renderRefusalReported;

    /// <summary>
    /// Reports a render refused by the security policy. The status bar always reflects it;
    /// the modal alert is shown only once per opened document.
    /// </summary>
    private void OnRenderRefusedByPolicy(int pageNumber, string message)
    {
        StatusText = $"Page {pageNumber} could not be displayed: {message}";

        if (_renderRefusalReported) return;
        _renderRefusalReported = true;

        ShowAlert(
            $"Page {pageNumber} cannot be displayed at the current zoom level.\n\n{message}\n\n" +
            "Reduce the zoom level to view this page.",
            "Render Blocked by Security Policy",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowAlert(string message, string caption, MessageBoxButton button, MessageBoxImage image)
    {
        if (ShowMessageBoxAction != null)
        {
            ShowMessageBoxAction(message, caption, button, image);
        }
        else if (Application.Current != null && Application.Current.MainWindow != null)
        {
            MessageBox.Show(message, caption, button, image);
        }
    }

    /// <summary>
    /// Composition root for the security policy and the licence gate. Both are constructed
    /// here and threaded into every component that enforces them, so there is exactly one
    /// place to change the application's posture.
    /// </summary>
    public PdfSecurityPolicy SecurityPolicy { get; } = PdfSecurityPolicy.DefaultStrict;

    /// <summary>
    /// The desktop application ships every feature it exposes - redaction, page operations,
    /// merge/split, forms, OCR and signatures are all reachable from its menus - so its gate
    /// is constructed at the tier that reflects what is actually shipped. Running it at
    /// Community would advertise menu items that refuse to work.
    ///
    /// The gate still does real work for SDK and embedding scenarios, where the host
    /// constructs its own IFeatureGate and CommandHistory enforces it.
    /// </summary>
    public IFeatureGate FeatureGate { get; } = new DefaultFeatureGate(LicenseTier.Enterprise);

    public DocumentSession Session { get; }
    public ICommandHistory CommandHistory { get; }

    /// <summary>
    /// How much rendered-page memory this document may retain. Set by whoever is dividing the
    /// shared budget between open documents.
    /// </summary>
    public void SetPageCacheCeiling(long bytes) => _cache.SetByteCeiling(bytes);

    /// <summary>Rendered-page memory this document is currently holding. Diagnostics only.</summary>
    public long PageCacheBytes => _cache.CurrentBytes;

    /// <summary>
    /// Text acquisition for a page: reads the embedded text layer, and falls back to real
    /// Windows optical recognition for scanned pages that have none. Null only when the
    /// Windows OCR runtime is unavailable on this machine, in which case scanned pages
    /// report that no text could be produced rather than silently returning nothing.
    /// </summary>
    public IOcrEngine OcrEngine { get; }

    /// <summary>True when genuine optical recognition is available on this machine.</summary>
    public bool IsOpticalRecognitionAvailable { get; }

    // Bindable licence state so the UI can disable or badge gated features rather than
    // offering them and failing at execution time.
    public bool IsRedactionAvailable => FeatureGate.IsFeatureEnabled(FeatureId.Redaction);
    public bool IsOcrAvailable => FeatureGate.IsFeatureEnabled(FeatureId.Ocr);
    public bool IsMergeSplitAvailable => FeatureGate.IsFeatureEnabled(FeatureId.MergeSplit);
    public bool IsPageOperationsAvailable => FeatureGate.IsFeatureEnabled(FeatureId.PageOperations);
    public bool IsFormsAvailable => FeatureGate.IsFeatureEnabled(FeatureId.Forms);
    public bool IsSignaturesAvailable => FeatureGate.IsFeatureEnabled(FeatureId.Signatures);
    public string LicenseTierName => FeatureGate.CurrentTier.ToString();

    public MainViewModel()
    {
        Session = new DocumentSession(SecurityPolicy);
        CommandHistory = new CommandHistory(featureGate: FeatureGate);

        _docService = PdfDocumentServiceFactory.CreateService(SecurityPolicy);
        // Bounded by bytes, not just by entry count: a rendered page runs from a few megabytes
        // at 150 DPI to around 33 MB at the 300 DPI used past 2x zoom, so sixty entries alone
        // said nothing useful about how much memory this would hold.
        _cache = new LruPageCache(60, LruPageCache.DefaultTotalByteCeiling);
        _renderer = new AsyncPageRenderer(_docService, _cache);

        // Compose text acquisition: embedded text layer first, real Windows OCR for pages
        // that have none. If the OCR runtime is missing, DefaultOcrEngine reports that
        // honestly instead of returning an empty result that looks like a blank page.
        var engine = new PdfEngine.Pdfium.PdfiumEngine();
        IOcrEngine? optical = null;
        if (WindowsOcrEngine.IsAvailable)
        {
            optical = new WindowsOcrEngine(engine.Renderer);
        }
        IsOpticalRecognitionAvailable = optical != null;
        OcrEngine = new DefaultOcrEngine(engine.TextService, optical);

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

    /// <summary>
    /// Where a file the user asked to open should go. The shell sets this so an open lands in
    /// a tab rather than replacing the document already in this one. Left null outside the
    /// shell - in tests and anywhere holding a single view model, loading in place is right.
    /// </summary>
    public Func<string, Task>? RequestOpenDocumentAsync { get; set; }

    private Task OpenRequestedDocumentAsync(string filePath) =>
        RequestOpenDocumentAsync?.Invoke(filePath) ?? LoadDocumentAsync(filePath);

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
            await OpenRequestedDocumentAsync(dialog.FileName);
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
        await OpenRequestedDocumentAsync(samplePath);
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
            await OpenRequestedDocumentAsync(filePath);
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            ShowAlert($"File no longer exists:\n{filePath}", "Open File Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ReloadRecentFiles();
        }
    }

    public async Task LoadDocumentAsync(string filePath, string? password = null)
    {
        // Opening another document replaces this one, so unsaved work would go with it.
        // Only ask on the first attempt: a password retry re-enters this method.
        if (password == null && !await ConfirmDiscardChangesAsync()) return;

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
            HasUnsavedChanges = false;
            UpdateWindowTitle();
            StatusText = $"Loaded {meta.FileName} ({meta.PageCount} pages)";

            RecentFilesService.AddRecentFile(filePath);
            ReloadRecentFiles();

            // History belongs to a document, not to the window.
            ClearNavigationHistory();

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

            // A refusal reported for a previous document must not suppress the alert for this one.
            _renderRefusalReported = false;

            // Build Page and Thumbnail ViewModels
            for (int i = 1; i <= meta.PageCount; i++)
            {
                var (w, h) = _docService.GetPageDimensions(i);
                var pageVm = new PageViewModel(i, w, h);
                pageVm.UpdateScale(ZoomLevel);
                pageVm.RenderRefused = OnRenderRefusedByPolicy;
                pageVm.Owner = this;

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
            RaiseLayoutChanged();

            // Calculate initial fit if requested
            if (FitMode != PageFitMode.Custom)
            {
                ApplyFitMode();
            }

            ScrollToPageAction?.Invoke(1);

            // Trigger asynchronous render
            await RenderVisiblePagesAsync();
            _ = RenderThumbnailsAsync();

            // Inspect what the document carries. Done after the first render so opening
            // stays responsive, and reported rather than acted upon.
            await InspectDocumentSafetyAsync(filePath);
            if (HasElevatedRisk)
            {
                StatusText = $"Loaded {meta.FileName} - {SafetySummary}";
            }
        }
        catch (Exception ex)
        {
            ShowAlert($"Failed to open PDF document:\n{ex.Message}", "Error Loading PDF", MessageBoxButton.OK, MessageBoxImage.Error);
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
        DocumentSafety = null;
        HasUnsavedChanges = false;
        ClearNavigationHistory();
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

    // Deliberate jumps only - a bookmark, a search hit, a page box entry. Scrolling moves
    // CurrentPageNumber directly and is not recorded, so "back" returns you to where you
    // jumped from rather than undoing your reading.
    private readonly List<int> _backHistory = new();
    private readonly List<int> _forwardHistory = new();
    private const int MaxHistoryDepth = 50;

    public bool CanGoBack => _backHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private void ClearNavigationHistory()
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
        RaiseHistoryChanged();
    }

    private static void PushBounded(List<int> stack, int value)
    {
        stack.Add(value);
        if (stack.Count > MaxHistoryDepth) stack.RemoveAt(0);
    }

    public void NavigateToPage(int targetPage) => NavigateToPage(targetPage, recordHistory: true);

    private void NavigateToPage(int targetPage, bool recordHistory)
    {
        if (targetPage < 1 || (PageCount > 0 && targetPage > PageCount)) return;

        if (recordHistory && CurrentPageNumber != targetPage && IsDocumentLoaded)
        {
            PushBounded(_backHistory, CurrentPageNumber);
            _forwardHistory.Clear();
            RaiseHistoryChanged();
        }

        CurrentPageNumber = targetPage;
        ScrollToPageAction?.Invoke(targetPage);

        if (ViewMode == ViewLayoutMode.Continuous)
        {
            _ = RenderVisiblePagesAsync();
        }
    }

    /// <summary>Returns to the page you jumped away from.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public void GoBack()
    {
        if (_backHistory.Count == 0) return;

        int target = _backHistory[^1];
        _backHistory.RemoveAt(_backHistory.Count - 1);
        PushBounded(_forwardHistory, CurrentPageNumber);

        NavigateToPage(target, recordHistory: false);
        RaiseHistoryChanged();
    }

    /// <summary>Undoes a "back".</summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    public void GoForward()
    {
        if (_forwardHistory.Count == 0) return;

        int target = _forwardHistory[^1];
        _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
        PushBounded(_backHistory, CurrentPageNumber);

        NavigateToPage(target, recordHistory: false);
        RaiseHistoryChanged();
    }

    private void UpdateSingleCurrentPage()
    {
        if (Pages.Count >= CurrentPageNumber && CurrentPageNumber >= 1)
        {
            SingleCurrentPage = Pages[CurrentPageNumber - 1];
            if (ViewMode == ViewLayoutMode.SinglePage)
            {
                _ = SingleCurrentPage.LoadImageAsync(_renderer, GetCurrentDpi(), RotationAngle, IsNightMode, CancellationToken.None);
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

        // Two pages across have to share the viewport, and the gap between them is real
        // width that the pages do not get. Fitting one page's width in facing mode would put
        // the second one off screen, which is the opposite of what "fit" means.
        int across = PagesPerRow;
        double requiredWidth = (docPageWidth * across) + (PageGap * (across - 1));

        if (FitMode == PageFitMode.FitWidth)
        {
            double availableWidth = viewportWidth - 40; // account for scrollbar & margins
            if (availableWidth > 0 && requiredWidth > 0)
            {
                ZoomLevel = Math.Clamp(availableWidth / requiredWidth, 0.25, 5.0);
            }
        }
        else if (FitMode == PageFitMode.FitPage)
        {
            double availableWidth = viewportWidth - 40;
            double availableHeight = viewportHeight - 40;
            if (availableWidth > 0 && availableHeight > 0 && requiredWidth > 0 && docPageHeight > 0)
            {
                double scaleX = availableWidth / requiredWidth;
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

    /// <summary>
    /// Inverts the pages themselves, not just the window around them.
    ///
    /// Separate from the light/dark theme on purpose: plenty of people want dark chrome with
    /// a normal page for accurate colour, and plenty want an inverted page while working in a
    /// light desktop. Tying the two together would take that choice away.
    /// </summary>
    [ObservableProperty]
    private bool _isNightMode;

    partial void OnIsNightModeChanged(bool value)
    {
        StatusText = value ? "Night mode on." : "Night mode off.";

        // Nothing about the page geometry changes, only its pixels, so this is the same
        // reload rotation already does.
        ReloadRenderedPages();
    }

    [RelayCommand]
    public void ToggleNightMode() => IsNightMode = !IsNightMode;

    private void OnRotationChanged()
    {
        foreach (var page in Pages)
        {
            page.UpdateRotation(RotationAngle);
        }

        RaiseLayoutChanged();
        ReloadRenderedPages();
    }

    /// <summary>
    /// Throws away every rendered page and thumbnail and asks for them again. Used whenever
    /// what the pixels should look like changes but the document has not.
    /// </summary>
    private void ReloadRenderedPages()
    {
        foreach (var page in Pages)
        {
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

    /// <summary>
    /// Cycles the toolbar button through the layouts, in the order a reader is likely to want
    /// them: scrolling, one page at a time, then two up.
    /// </summary>
    [RelayCommand]
    public void ToggleViewMode() => SetViewMode(ViewMode switch
    {
        ViewLayoutMode.Continuous => ViewLayoutMode.SinglePage,
        ViewLayoutMode.SinglePage => ViewLayoutMode.Facing,
        _ => ViewLayoutMode.Continuous
    });

    [RelayCommand]
    public void SetContinuousView() => SetViewMode(ViewLayoutMode.Continuous);

    [RelayCommand]
    public void SetSinglePageView() => SetViewMode(ViewLayoutMode.SinglePage);

    [RelayCommand]
    public void SetFacingView() => SetViewMode(ViewLayoutMode.Facing);

    public void SetViewMode(ViewLayoutMode mode)
    {
        if (ViewMode == mode) return;

        ViewMode = mode;
        RaiseLayoutChanged();
        UpdateSingleCurrentPage();

        // The page area changes width, so a fit that was calculated for one page across is
        // wrong the moment two are.
        if (FitMode != PageFitMode.Custom) ApplyFitMode();

        ScrollToPageAction?.Invoke(CurrentPageNumber);
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

    #region Page Layout

    /// <summary>Gap below and between pages, in device-independent pixels.</summary>
    public const double PageGap = 20;

    /// <summary>True while pages are laid out as a scrolling list rather than one at a time.</summary>
    public bool IsMultiPageLayout => ViewMode != ViewLayoutMode.SinglePage;

    /// <summary>How many pages sit side by side in a row.</summary>
    public int PagesPerRow => ViewMode == ViewLayoutMode.Facing ? 2 : 1;

    /// <summary>
    /// Whether the first page stands alone in facing mode. A book's cover has no page facing
    /// it, so pairing from page 1 puts every spread one page out of step with the printed
    /// original.
    /// </summary>
    [ObservableProperty]
    private bool _showCoverPageAlone = true;

    partial void OnShowCoverPageAloneChanged(bool value)
    {
        if (ViewMode == ViewLayoutMode.Facing)
        {
            RebuildPageRows();
            ScrollToPageAction?.Invoke(CurrentPageNumber);
            _ = RenderVisiblePagesAsync();
        }
    }

    /// <summary>
    /// The rows the document is drawn as. The view binds to this, so what is on screen and
    /// what the scroll maths believes are the same grouping by construction rather than by
    /// two pieces of code agreeing to behave the same way.
    /// </summary>
    public ObservableCollection<PageRowViewModel> PageRows { get; } = new();

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(IsMultiPageLayout));
        OnPropertyChanged(nameof(PagesPerRow));
        RebuildPageRows();
    }

    private void RebuildPageRows()
    {
        PageRows.Clear();
        foreach (var (start, count) in BuildRowGrouping())
        {
            PageRows.Add(new PageRowViewModel(Pages.Skip(start).Take(count).ToList()));
        }
    }

    /// <summary>
    /// Which pages share a row, as (first index, count) pairs. The one rule that decides the
    /// document's shape; both the visual rows and the geometry below are built from it.
    /// </summary>
    private List<(int Start, int Count)> BuildRowGrouping()
    {
        var grouping = new List<(int, int)>();
        if (Pages.Count == 0) return grouping;

        int perRow = PagesPerRow;
        int index = 0;

        // In facing mode the cover optionally stands alone, which shifts every later spread
        // so that the pairs match the printed document.
        if (perRow > 1 && ShowCoverPageAlone)
        {
            grouping.Add((0, 1));
            index = 1;
        }

        while (index < Pages.Count)
        {
            int count = Math.Min(perRow, Pages.Count - index);
            grouping.Add((index, count));
            index += count;
        }

        return grouping;
    }

    /// <summary>
    /// Where each row sits vertically, at the current zoom. Scrolling, hit-testing the
    /// viewport and render scheduling all read this, so they cannot disagree with each other
    /// or with what is drawn.
    /// </summary>
    public IReadOnlyList<PageRowMetrics> BuildPageRows()
    {
        var rows = new List<PageRowMetrics>();
        double top = 0;

        foreach (var (start, count) in BuildRowGrouping())
        {
            double tallest = 0;
            for (int i = start; i < start + count; i++)
            {
                if (Pages[i].DisplayHeight > tallest) tallest = Pages[i].DisplayHeight;
            }

            double height = tallest + PageGap;
            rows.Add(new PageRowMetrics(start, count, top, height));
            top += height;
        }

        return rows;
    }

    /// <summary>Vertical offset of the row holding the given 1-based page.</summary>
    public double GetPageOffset(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > Pages.Count) return 0;

        int pageIndex = pageNumber - 1;
        foreach (var row in BuildPageRows())
        {
            if (row.Contains(pageIndex)) return row.Top;
        }
        return 0;
    }

    /// <summary>
    /// The 1-based page the given vertical offset falls on. Offsets above the document
    /// resolve to the first page and offsets past the end to the last, so a caller always
    /// gets a page that exists.
    /// </summary>
    public int GetPageAtOffset(double offset)
    {
        if (Pages.Count == 0) return 1;

        var rows = BuildPageRows();
        if (rows.Count == 0) return 1;
        if (offset < 0) return 1;

        foreach (var row in rows)
        {
            if (offset >= row.Top && offset < row.Bottom) return row.FirstPageIndex + 1;
        }

        return rows[^1].FirstPageIndex + 1;
    }

    #endregion

    public void RenderPagesInViewport(double viewportTop, double viewportHeight)
    {
        if (!IsDocumentLoaded || Pages.Count == 0 || !IsMultiPageLayout) return;

        double buffer = Math.Max(viewportHeight * 1.5, 1200); // 1.5 screens buffer ahead & behind
        double minOffset = Math.Max(0, viewportTop - buffer);
        double maxOffset = viewportTop + viewportHeight + buffer;

        int dpi = GetCurrentDpi();

        foreach (var row in BuildPageRows())
        {
            if (row.Bottom < minOffset || row.Top > maxOffset) continue;

            for (int i = row.FirstPageIndex; i < row.FirstPageIndex + row.PageCount; i++)
            {
                var page = Pages[i];
                if (page.RenderedImage == null && !page.IsLoading)
                {
                    _ = page.LoadImageAsync(_renderer, dpi, RotationAngle, IsNightMode, CancellationToken.None);
                }
                if (!page.IsTextExtracted && !page.IsExtractingText)
                {
                    _ = page.LoadTextSegmentsAsync(_docService, CancellationToken.None);
                }
            }
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
                await SingleCurrentPage.LoadImageAsync(_renderer, dpi, RotationAngle, IsNightMode, CancellationToken.None);
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
                _ = Pages[idx].LoadImageAsync(_renderer, dpi, RotationAngle, IsNightMode, CancellationToken.None);
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
                await page.LoadImageAsync(_renderer, dpi, rotation, IsNightMode, CancellationToken.None);
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
                await thumb.LoadThumbnailAsync(_renderer, RotationAngle, IsNightMode, CancellationToken.None);
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
                    void Apply()
                    {
                        page.SelectAllText();
                        UpdateSelectionFromPages();
                    }

                    // Marshalling through Application.Current dropped the selection entirely
                    // whenever there was no Application to marshal through - the null-conditional
                    // swallowed the whole continuation. Fall back to running it here instead.
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(Apply);
                    else Apply();
                }, TaskScheduler.Default);
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

    /// <summary>
    /// Builds one redaction rectangle per selected text segment, so redaction follows the
    /// exact glyph boxes rather than one loose box around the whole selection.
    /// </summary>
    public List<PdfEngine.Redaction.RedactionArea> BuildRedactionAreasFromSelection()
    {
        var areas = new List<PdfEngine.Redaction.RedactionArea>();
        foreach (var page in Pages)
        {
            foreach (var segment in page.SelectedSegments)
            {
                areas.Add(new PdfEngine.Redaction.RedactionArea
                {
                    PageNumber = page.PageNumber,
                    Bounds = new PdfEngine.Geometry.PdfRect(
                        segment.X, segment.Y, segment.Width, segment.Height)
                });
            }
        }
        return areas;
    }

    /// <summary>
    /// Permanently redacts the selected text and writes the result to a new document.
    ///
    /// Redaction removes the underlying content, so it deliberately never overwrites the
    /// open file - the user always keeps the unredacted original.
    /// </summary>
    [RelayCommand]
    public async Task RedactSelectionAsync()
    {
        if (string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        var areas = BuildRedactionAreasFromSelection();

        if (areas.Count == 0)
        {
            ShowAlert("Select the text you want to redact first.", "Redact",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBoxResult.Yes;
        if (ShowMessageBoxAction == null)
        {
            confirm = MessageBox.Show(
                $"Permanently remove the selected text from {areas.Count} area(s)?\n\n" +
                "The text is deleted from the saved copy and cannot be recovered from it. " +
                "Your open document is not modified.",
                "Confirm Redaction", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        }
        if (confirm != MessageBoxResult.Yes) return;

        var target = new SaveFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Save redacted document as",
            FileName = $"{Path.GetFileNameWithoutExtension(_docService.CurrentFilePath)}_redacted.pdf"
        };
        if (target.ShowDialog() != true) return;

        StatusText = $"Redacting {areas.Count} area(s)...";
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath);
            await engine.RedactionService.ApplyRedactionsAsync(doc, target.FileName, areas);

            ClearSelection();
            StatusText = $"Redacted {areas.Count} area(s) into {Path.GetFileName(target.FileName)}.";
            ShowAlert(
                $"Redacted document saved to:\n{target.FileName}\n\n" +
                "The redacted text has been removed from the file, not merely covered.",
                "Redact", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Redaction failed: {ex.Message}";
            ShowAlert($"Could not redact the document:\n\n{ex.Message}", "Redact",
                MessageBoxButton.OK, MessageBoxImage.Error);
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

    /// <summary>
    /// True when the document has annotation changes that are not on disk. Drives Save, the
    /// title bar marker, and the prompt on closing.
    /// </summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    partial void OnHasUnsavedChangesChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        if (Metadata == null)
        {
            WindowTitle = "PDF Viewer";
            OnPropertyChanged(nameof(TabTitle));
            return;
        }

        // The asterisk is the only signal a user gets that closing will lose something.
        WindowTitle = HasUnsavedChanges
            ? $"*{Metadata.FileName} - PDF Viewer"
            : $"{Metadata.FileName} - PDF Viewer";

        OnPropertyChanged(nameof(TabTitle));
    }

    /// <summary>What this document is called on its tab.</summary>
    public string TabTitle
    {
        get
        {
            if (Metadata == null) return "No document";
            return HasUnsavedChanges ? $"*{Metadata.FileName}" : Metadata.FileName;
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
        HasUnsavedChanges = true;
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
        HasUnsavedChanges = true;
        StatusText = $"Removed annotation from page {annot.PageNumber}";
    }

    /// <summary>
    /// Records that an existing annotation was edited in place.
    ///
    /// Editing a comment used to change only the model: the title bar showed no asterisk,
    /// Save stayed greyed out, and closing asked nothing - so a rewritten comment was thrown
    /// away without a word. Only adding and removing counted as work.
    /// </summary>
    public void NoteAnnotationEdited(AnnotationModel annot)
    {
        if (annot == null) return;

        annot.ModifiedDate = DateTime.Now;
        HasUnsavedChanges = true;
        StatusText = $"Edited annotation on page {annot.PageNumber}";
    }

    [RelayCommand]
    public void ClearAllAnnotations()
    {
        if (AllAnnotations.Count > 0) HasUnsavedChanges = true;

        AllAnnotations.Clear();
        foreach (var page in Pages)
        {
            page.AnnotationsOnPage.Clear();
        }
        StatusText = "Cleared all annotations.";
    }

    /// <summary>
    /// Saves the document in place, keeping annotations editable.
    ///
    /// The write goes to a temporary file beside the original and only then replaces it, so a
    /// failure part-way through leaves the original intact rather than truncated. The document
    /// is held in memory rather than kept open on disk, so nothing has to be closed first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        if (!CanSave()) return;

        string originalPath = Metadata!.FilePath;
        string directory = Path.GetDirectoryName(Path.GetFullPath(originalPath)) ?? string.Empty;
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(originalPath)}.saving");
        string backupPath = temporaryPath + ".bak";

        StatusText = "Saving...";

        try
        {
            await _docService.SaveAnnotatedDocumentAsync(
                temporaryPath, AnnotationSaveMode.Embedded, AllAnnotations, originalPath);

            ReplaceOriginal(temporaryPath, originalPath, backupPath);

            HasUnsavedChanges = false;
            StatusText = $"Saved {Path.GetFileName(originalPath)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
            ShowAlert($"The document was not saved:\n\n{ex.Message}\n\nYour file on disk is unchanged.",
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TryDeleteQuietly(temporaryPath);
            TryDeleteQuietly(backupPath);
        }
    }

    public bool CanSave() => IsDocumentLoaded && HasUnsavedChanges && Metadata != null
                             && !string.IsNullOrEmpty(Metadata.FilePath);

    /// <summary>
    /// Swaps the freshly written file in for the original. File.Replace keeps a backup and
    /// does the swap in one step, so the original is never left half-written.
    /// </summary>
    private static void ReplaceOriginal(string temporaryPath, string originalPath, string backupPath)
    {
        if (!File.Exists(temporaryPath))
            throw new IOException("The document was not written, so nothing was replaced.");

        if (new FileInfo(temporaryPath).Length == 0)
            throw new IOException("The document came out empty, so the original was left alone.");

        try
        {
            File.Replace(temporaryPath, originalPath, backupPath, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            // File.Replace needs both paths on one volume and a real file system; fall back to
            // a move, which is still better than writing over the original directly.
            File.Move(temporaryPath, originalPath, overwrite: true);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover scratch file is untidy, not harmful.
        }
    }

    /// <summary>
    /// Asks what to do about unsaved work. Returns false to cancel whatever prompted it.
    /// </summary>
    public Func<string, bool?>? ConfirmSaveBeforeClosingFunc { get; set; }

    /// <summary>
    /// Runs the save-or-discard prompt. Returns false when the user cancels, in which case the
    /// caller must not close or replace the document.
    /// </summary>
    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!HasUnsavedChanges || Metadata == null) return true;

        // No prompt wired means there is nobody to ask, which is not the same as being told
        // to stop. Treating it as a cancellation made the window impossible to close at all -
        // every close would be refused by a question that was never put to anyone.
        if (ConfirmSaveBeforeClosingFunc == null) return true;

        bool? answer = ConfirmSaveBeforeClosingFunc(Metadata.FileName);

        if (answer == null) return false;      // cancelled
        if (answer == false) return true;      // discard

        await SaveAsync();

        // A failed save must not be treated as permission to throw the work away.
        return !HasUnsavedChanges;
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

                    // Only an embedded save captures the annotations in the document itself;
                    // an XFDF export leaves the PDF as it was, so the work is still unsaved.
                    if (result.Mode == AnnotationSaveMode.Embedded) HasUnsavedChanges = false;

                    StatusText = $"Saved annotated document ({result.Mode}): {Path.GetFileName(result.TargetPath)}";
                    ShowAlert(
                        $"Annotated document successfully saved ({result.Mode}):\n{result.TargetPath}",
                        "Save Successful",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    StatusText = $"Save error: {ex.Message}";
                    ShowAlert(
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

    /// <summary>
    /// Shows the document's form fields for editing, then writes the edited values to a new
    /// document. Returns the fields the dialog produced, or null when it was cancelled.
    /// </summary>
    public Func<IReadOnlyList<PdfEngine.Forms.FormFieldModel>, string, IReadOnlyList<PdfEngine.Forms.FormFieldModel>?>? ShowFormFieldsFunc { get; set; }

    [RelayCommand]
    public async Task EditFormFieldsAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        StatusText = "Reading form fields...";
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();

            var allFields = new List<PdfEngine.Forms.FormFieldModel>();
            await using (var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath))
            {
                for (int p = 1; p <= doc.PageCount; p++)
                {
                    allFields.AddRange(await engine.FormService.GetFormFieldsAsync(doc, p));
                }
            }

            if (allFields.Count == 0)
            {
                StatusText = "This document has no form fields.";
                ShowAlert("This document contains no form fields.", "Form Fields",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = Path.GetFileName(_docService.CurrentFilePath);
            var edited = ShowFormFieldsFunc?.Invoke(allFields, name);
            if (edited == null)
            {
                StatusText = $"{allFields.Count} form field(s).";
                return;
            }

            var target = new SaveFileDialog
            {
                Filter = "PDF Files (*.pdf)|*.pdf",
                Title = "Save filled document as",
                FileName = $"{Path.GetFileNameWithoutExtension(_docService.CurrentFilePath)}_filled.pdf"
            };
            if (target.ShowDialog() != true) return;

            StatusText = "Writing form values...";
            await using (var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath))
            {
                int written = 0;
                foreach (var field in edited.Where(f => !f.IsReadOnly && !string.IsNullOrEmpty(f.Name)))
                {
                    try
                    {
                        await engine.FormService.SetFieldValueAsync(doc, field.Name, field.Value ?? string.Empty);
                        written++;
                    }
                    catch (KeyNotFoundException)
                    {
                        // A field the document no longer exposes; skip rather than abort the save.
                    }
                }

                await engine.SaveService.SaveAsync(doc, target.FileName);
                StatusText = $"Wrote {written} field value(s) to {Path.GetFileName(target.FileName)}.";
            }

            ShowAlert($"Filled document saved to:\n{target.FileName}", "Form Fields",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Form editing failed: {ex.Message}";
            ShowAlert($"Could not edit the form fields:\n\n{ex.Message}", "Form Fields",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// What the open document carries that could act on the user's machine. Null until a
    /// document has been inspected.
    /// </summary>
    [ObservableProperty]
    private PdfEngine.Safety.DocumentSafetyReport? _documentSafety;

    /// <summary>True when the document carries script, a launch action, or an embedded file.</summary>
    public bool HasElevatedRisk => DocumentSafety is { IsClean: false };

    /// <summary>Short status-bar text describing what the document carries.</summary>
    public string SafetySummary
    {
        get
        {
            if (DocumentSafety == null) return string.Empty;
            if (DocumentSafety.IsClean) return "No active content";

            var kinds = DocumentSafety.Findings
                .Where(f => f.Severity == PdfEngine.Safety.RiskSeverity.Elevated)
                .Select(f => f.Kind switch
                {
                    PdfEngine.Safety.DocumentRiskKind.JavaScript => "script",
                    PdfEngine.Safety.DocumentRiskKind.LaunchAction => "launch action",
                    PdfEngine.Safety.DocumentRiskKind.EmbeddedFile => "embedded file",
                    _ => f.Kind.ToString().ToLowerInvariant()
                });

            return "Contains " + string.Join(", ", kinds) + " (not run)";
        }
    }

    partial void OnDocumentSafetyChanged(PdfEngine.Safety.DocumentSafetyReport? value)
    {
        OnPropertyChanged(nameof(HasElevatedRisk));
        OnPropertyChanged(nameof(SafetySummary));
    }

    /// <summary>
    /// Inspects the open document and records what it carries. Never throws into the open
    /// path: a document that cannot be inspected must still open, with the inspection
    /// reported as unavailable rather than silently implying the document is clean.
    /// </summary>
    private async Task InspectDocumentSafetyAsync(string filePath)
    {
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(filePath);

            var inspector = new PdfEngine.Pdfium.Adapters.PdfiumSafetyInspector();
            DocumentSafety = await inspector.InspectAsync(doc);
        }
        catch (Exception ex)
        {
            DocumentSafety = new PdfEngine.Safety.DocumentSafetyReport
            {
                InspectionWasLimited = true,
                LimitationReason = $"The document could not be inspected: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Shows what the document carries, in full.
    /// </summary>
    [RelayCommand]
    public void ShowDocumentSafety()
    {
        if (!IsDocumentLoaded) return;

        if (DocumentSafety == null)
        {
            ShowAlert("This document has not been inspected.", "Document Safety",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var report = new StringBuilder();

        if (DocumentSafety.IsClean)
        {
            report.AppendLine("No active content was found in this document.");
            report.AppendLine();
            report.AppendLine("It carries no embedded script, no launch actions and no embedded files.");
        }
        else
        {
            report.AppendLine("This document carries active content. None of it has been run.");
            report.AppendLine();
        }

        foreach (var finding in DocumentSafety.Findings)
        {
            report.AppendLine($"• {finding.Description}");
            foreach (var detail in finding.Details)
            {
                report.AppendLine($"      {detail}");
            }
        }

        if (DocumentSafety.InspectionWasLimited)
        {
            report.AppendLine();
            report.AppendLine($"Note: {DocumentSafety.LimitationReason}");
        }

        report.AppendLine();
        report.AppendLine("This reader has no JavaScript engine and never executes document script,");
        report.AppendLine("follows launch actions, or opens embedded files on your behalf.");

        if (!DocumentSafety.IsClean)
        {
            report.AppendLine();
            report.AppendLine("To keep the pages without the active content, use");
            report.AppendLine("Tools → Save Clean Copy. The original is never modified.");
        }

        ShowAlert(report.ToString().TrimEnd(), "Document Safety", MessageBoxButton.OK,
            DocumentSafety.IsClean ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>
    /// Writes a copy of the open document with its executable content removed.
    ///
    /// Deliberately not behind the feature gate. Both major competitors put sanitization in a
    /// paid tier; being able to defuse a document you have been sent is a safety property of
    /// this reader, not an upsell.
    /// </summary>
    [RelayCommand]
    public async Task SaveCleanCopyAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        string sourcePath = _docService.CurrentFilePath;
        var target = new SaveFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Save clean copy as",
            FileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_clean.pdf",
            InitialDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty
        };
        if (target.ShowDialog() != true) return;

        StatusText = "Removing active content...";
        try
        {
            PdfEngine.Safety.SanitizationResult result;
            using (var engine = new PdfEngine.Pdfium.PdfiumEngine())
            await using (var doc = await engine.OpenDocumentAsync(sourcePath))
            {
                var sanitizer = new PdfEngine.Pdfium.Adapters.PdfiumSanitizer();
                result = await sanitizer.SanitizeAsync(doc, target.FileName);
            }

            var summary = new StringBuilder();
            summary.AppendLine(result.RemovedAnything
                ? $"A clean copy of {result.PageCount} page(s) was saved to:"
                : $"This document carried no active content. A copy of {result.PageCount} page(s) was saved to:");
            summary.AppendLine(result.OutputPath);

            if (result.RemovedAnything)
            {
                summary.AppendLine();
                foreach (var change in result.Changes)
                {
                    summary.AppendLine($"• {change.Description}");
                }
            }

            if (result.SideEffects.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("What the copy does not carry across:");
                foreach (var effect in result.SideEffects)
                {
                    summary.AppendLine($"• {effect}");
                }
            }

            summary.AppendLine();
            summary.AppendLine("The copy was re-inspected after writing and reports no active content.");
            summary.AppendLine("Your original file is unchanged.");

            StatusText = result.RemovedAnything
                ? $"Clean copy saved - removed {result.TotalRemoved} item(s)."
                : "Clean copy saved - nothing needed removing.";

            ShowAlert(summary.ToString().TrimEnd(), "Save Clean Copy",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // Sanitization fails loudly, including when the copy it produced failed its own
            // verification. Reporting partial success here would be the one outcome worse
            // than not offering the feature.
            StatusText = $"Clean copy failed: {ex.Message}";
            ShowAlert($"No clean copy was produced.\n\n{ex.Message}\n\nYour original file is unchanged.",
                "Save Clean Copy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #region Read Aloud

    private ITextReader? _reader;
    private CancellationTokenSource? _readingCts;

    /// <summary>True while the document is being read out loud.</summary>
    [ObservableProperty]
    private bool _isReadingAloud;

    [ObservableProperty]
    private bool _isReadingPaused;

    /// <summary>
    /// Whether the optional Read Aloud component is present. Checked on demand rather than
    /// cached, so installing it does not require restarting the application.
    /// </summary>
    public bool IsReadAloudInstalled => TextReaderFactory.IsComponentInstalled();

    /// <summary>Asks whether to fetch the optional component. Returns true only on an explicit yes.</summary>
    public Func<PdfViewer.Core.Components.OptionalComponent, bool>? ConfirmComponentDownloadFunc { get; set; }

    /// <summary>
    /// Starts reading from the current page, or stops if already reading.
    /// </summary>
    [RelayCommand]
    public async Task ToggleReadAloudAsync()
    {
        if (IsReadingAloud)
        {
            StopReadingAloud();
            return;
        }

        if (!IsDocumentLoaded) return;

        if (!await EnsureReaderAsync()) return;

        _readingCts = new CancellationTokenSource();
        IsReadingAloud = true;
        IsReadingPaused = false;

        try
        {
            await ReadFromCurrentPageAsync(_readingCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user.
        }
        catch (Exception ex)
        {
            StatusText = $"Read aloud failed: {ex.Message}";
            ShowAlert($"Reading stopped:\n\n{ex.Message}", "Read Aloud",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsReadingAloud = false;
            IsReadingPaused = false;
            _readingCts?.Dispose();
            _readingCts = null;
        }
    }

    /// <summary>
    /// Reads each page in turn, following along in the viewer. Pages with no text layer are
    /// skipped and counted rather than passed over in silence, so a scanned document does not
    /// look like a broken feature.
    /// </summary>
    private async Task ReadFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        int startPage = CurrentPageNumber;
        int silentPages = 0;

        for (int page = startPage; page <= PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segments = await _docService.ExtractPageTextSegmentsAsync(page, cancellationToken);
            string text = string.Join(" ", segments.Select(s => s.Text)).Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                silentPages++;
                continue;
            }

            if (CurrentPageNumber != page) NavigateToPage(page);
            StatusText = $"Reading page {page} of {PageCount}...";

            if (_reader != null && !await _reader.SpeakAsync(text, cancellationToken))
            {
                StatusText = $"Reading stopped on page {page}.";
                return;
            }
        }

        StatusText = silentPages == PageCount - startPage + 1
            ? "Nothing to read: no text layer on these pages. Use Tools → Recognise Text on Page (OCR)."
            : silentPages > 0
                ? $"Finished reading. {silentPages} page(s) had no text layer and were skipped."
                : "Finished reading.";
    }

    /// <summary>
    /// Makes sure a reader exists, offering to fetch the optional component if it is missing.
    /// </summary>
    private async Task<bool> EnsureReaderAsync()
    {
        _reader ??= TextReaderFactory.TryCreate();
        if (_reader != null) return true;

        var component = PdfViewer.Core.Components.OptionalComponents.ReadAloud;

        if (ConfirmComponentDownloadFunc?.Invoke(component) != true)
        {
            StatusText = "Read Aloud is not installed.";
            return false;
        }

        try
        {
            StatusText = $"Downloading {component.DisplayName}...";

            var downloader = new PdfViewer.Core.Components.ComponentDownloader();
            await downloader.InstallAsync(component, AppVersion, AppContext.BaseDirectory);

            OnPropertyChanged(nameof(IsReadAloudInstalled));
            _reader = TextReaderFactory.TryCreate();

            if (_reader == null)
            {
                StatusText = "Read Aloud could not be started after installing.";
                ShowAlert("The component installed but could not be loaded. Reinstalling the application should fix it.",
                    "Read Aloud", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            StatusText = $"{component.DisplayName} installed.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Read Aloud install failed: {ex.Message}";
            ShowAlert($"{ex.Message}", "Read Aloud", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Version this build is published as; picks the matching component.</summary>
    private static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Stops and releases the reader. Called when the window closes: without it the speech
    /// keeps talking into a closing application and the process lingers while the synthesizer
    /// finishes whatever sentence it was on.
    /// </summary>
    public void ShutdownReadAloud()
    {
        _readingCts?.Cancel();

        var reader = _reader;
        _reader = null;

        try
        {
            reader?.Stop();
            reader?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Nothing useful to do while shutting down.
        }
    }

    [RelayCommand]
    public void StopReadingAloud()
    {
        _readingCts?.Cancel();
        _reader?.Stop();
        IsReadingPaused = false;
        StatusText = "Reading stopped.";
    }

    [RelayCommand]
    public void PauseOrResumeReading()
    {
        if (!IsReadingAloud || _reader == null) return;

        if (IsReadingPaused)
        {
            _reader.Resume();
            IsReadingPaused = false;
            StatusText = "Reading resumed.";
        }
        else
        {
            _reader.Pause();
            IsReadingPaused = true;
            StatusText = "Reading paused.";
        }
    }

    #endregion

    /// <summary>
    /// Reorders, rotates and removes pages, writing the result to a new document.
    /// </summary>
    [RelayCommand]
    public async Task OrganizePagesAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        if (ShowOrganizePagesFunc == null)
        {
            ShowAlert("The page organizer is unavailable.", "Organize Pages",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string sourcePath = _docService.CurrentFilePath;
        var arrangement = ShowOrganizePagesFunc(PageCount, Path.GetFileName(sourcePath));
        if (arrangement == null || arrangement.Count == 0) return;

        var target = new SaveFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Save organized document as",
            FileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_organized.pdf",
            InitialDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty
        };
        if (target.ShowDialog() != true) return;

        StatusText = "Writing organized document...";
        try
        {
            using (var engine = new PdfEngine.Pdfium.PdfiumEngine())
            await using (var doc = await engine.OpenDocumentAsync(sourcePath))
            {
                await engine.PageOrganizer.ArrangePagesAsync(doc, arrangement, target.FileName);
            }

            int removed = PageCount - arrangement.Count;
            string removedNote = removed > 0 ? $"\n{removed} page(s) were left out." : string.Empty;

            StatusText = $"Organized document saved ({arrangement.Count} pages).";
            ShowAlert(
                $"A {arrangement.Count}-page document was saved to:\n{target.FileName}{removedNote}\n\n" +
                "Your original file is unchanged.",
                "Organize Pages", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Organize failed: {ex.Message}";
            ShowAlert($"The organized document was not written.\n\n{ex.Message}\n\nYour original file is unchanged.",
                "Organize Pages", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Lists the document's embedded files and extracts the one the user picks.
    ///
    /// Extraction is always explicit. Nothing is unpacked on open, and the extracted file is
    /// written as data and never launched - if it is an executable the user is told so before
    /// anything is written.
    /// </summary>
    [RelayCommand]
    public async Task ShowAttachmentsAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        string sourcePath = _docService.CurrentFilePath;

        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(sourcePath);

            var service = new PdfEngine.Pdfium.Adapters.PdfiumAttachmentService();
            var files = await service.GetAttachmentsAsync(doc);

            if (files.Count == 0)
            {
                StatusText = "No embedded files.";
                ShowAlert("This document carries no embedded files.", "Embedded Files",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var chosen = ShowAttachmentsFunc?.Invoke(files, Path.GetFileName(sourcePath));
            if (chosen == null)
            {
                StatusText = $"{files.Count} embedded file(s).";
                return;
            }

            if (chosen.HasExecutableExtension &&
                !Confirm(
                    $"\"{chosen.Name}\" is a type Windows can execute.\n\n" +
                    "It will be written to disk as data and this application will not run it, " +
                    "but nothing else on your machine knows that.\n\nExtract it anyway?",
                    "Executable Attachment"))
            {
                StatusText = "Extraction cancelled.";
                return;
            }

            var target = new SaveFileDialog
            {
                Title = "Extract embedded file as",
                FileName = SanitizeSuggestedFileName(chosen.Name),
                Filter = "All Files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty
            };
            if (target.ShowDialog() != true) return;

            long written = await service.ExtractAttachmentAsync(doc, chosen.Index, target.FileName);

            StatusText = $"Extracted {chosen.Name} ({written:N0} bytes).";
            ShowAlert($"\"{chosen.Name}\" was written to:\n{target.FileName}\n\n{written:N0} bytes. " +
                      "It was not opened or run.",
                "Embedded Files", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Embedded files failed: {ex.Message}";
            ShowAlert($"Could not read the embedded files:\n\n{ex.Message}", "Embedded Files",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Strips a document-supplied file name down to something safe to put in a save dialog.
    /// The name comes from the PDF, so it can contain path separators or traversal segments
    /// aimed at writing outside the folder the user picked.
    /// </summary>
    internal static string SanitizeSuggestedFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "attachment";

        // Take the last segment regardless of which separator the document used, so
        // "..\..\Startup\evil.exe" suggests "evil.exe" and nothing more.
        string leaf = name.Replace('\\', '/');
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            leaf = leaf.Replace(invalid, '_');
        }

        leaf = leaf.Trim('.', ' ');
        return string.IsNullOrWhiteSpace(leaf) ? "attachment" : leaf;
    }

    /// <summary>
    /// Writes the document's text to a plain text file.
    /// </summary>
    [RelayCommand]
    public async Task ExportTextAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        string sourcePath = _docService.CurrentFilePath;
        var target = new SaveFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt",
            Title = "Export text as",
            FileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}.txt",
            InitialDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty
        };
        if (target.ShowDialog() != true) return;

        StatusText = "Extracting text...";
        try
        {
            var text = new StringBuilder();
            int pagesWithText = 0;

            using (var engine = new PdfEngine.Pdfium.PdfiumEngine())
            await using (var doc = await engine.OpenDocumentAsync(sourcePath))
            {
                for (int page = 1; page <= doc.PageCount; page++)
                {
                    string pageText = await engine.TextService.ExtractPageTextAsync(doc, page);
                    if (!string.IsNullOrWhiteSpace(pageText)) pagesWithText++;

                    text.AppendLine($"--- Page {page} ---");
                    text.AppendLine(pageText);
                    text.AppendLine();
                }
            }

            await File.WriteAllTextAsync(target.FileName, text.ToString(), Encoding.UTF8);

            // A scanned document has no text layer, and a file full of page headers and
            // nothing else is a confusing result to hand back without explanation.
            string note = pagesWithText == 0
                ? "\n\nNo text layer was found on any page. This document is probably scanned - " +
                  "use Tools → Recognise Text on Page (OCR) to read it."
                : pagesWithText < PageCount
                    ? $"\n\n{PageCount - pagesWithText} page(s) had no text layer and came out empty."
                    : string.Empty;

            StatusText = $"Text exported ({pagesWithText}/{PageCount} pages had text).";
            ShowAlert($"Text was written to:\n{target.FileName}{note}", "Export Text",
                MessageBoxButton.OK, pagesWithText == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Export text failed: {ex.Message}";
            ShowAlert($"Could not export the text:\n\n{ex.Message}", "Export Text",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Compares the open document against another and reports where they differ.
    /// </summary>
    [RelayCommand]
    public async Task CompareWithDocumentAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        var picker = new OpenFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Select the document to compare against"
        };
        if (picker.ShowDialog() != true) return;

        StatusText = "Comparing documents...";
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            var comparer = new PdfViewer.Core.Comparison.PdfComparisonService(engine.Renderer, engine.TextService);

            await using var docA = await engine.OpenDocumentAsync(_docService.CurrentFilePath);
            await using var docB = await engine.OpenDocumentAsync(picker.FileName);

            var result = await comparer.CompareDocumentsAsync(docA, docB);

            var report = new StringBuilder();
            report.AppendLine($"This document: {result.PageCountA} page(s)");
            report.AppendLine($"Compared with: {result.PageCountB} page(s)");
            report.AppendLine($"Visual similarity: {result.VisualSimilarityScore:P1}");
            report.AppendLine();

            if (result.PagesWithVisualDifferences.Count == 0 && result.TextDifferences.Count == 0)
            {
                report.AppendLine("The documents are identical.");
            }
            else
            {
                if (result.PagesWithVisualDifferences.Count > 0)
                {
                    report.AppendLine($"Pages that differ visually: " +
                        string.Join(", ", result.PagesWithVisualDifferences.Take(30)) +
                        (result.PagesWithVisualDifferences.Count > 30 ? ", ..." : string.Empty));
                }

                if (result.TextDifferences.Count > 0)
                {
                    report.AppendLine();
                    report.AppendLine($"Text differences ({result.TextDifferences.Count}):");
                    foreach (var diff in result.TextDifferences.Take(10))
                    {
                        report.AppendLine($"  Page {diff.PageNumber} [{diff.Type}]");
                    }
                    if (result.TextDifferences.Count > 10) report.AppendLine("  ...");
                }
            }

            StatusText = result.PagesWithVisualDifferences.Count == 0 && result.TextDifferences.Count == 0
                ? "The documents are identical."
                : $"{result.PagesWithVisualDifferences.Count} page(s) differ visually.";

            ShowAlert(report.ToString().TrimEnd(), "Compare Documents",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Comparison failed: {ex.Message}";
            ShowAlert($"Could not compare the documents:\n\n{ex.Message}", "Compare Documents",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Combines several PDFs into one file.
    /// </summary>
    [RelayCommand]
    public async Task MergeDocumentsAsync()
    {
        var picker = new OpenFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Select PDF documents to merge (in order)",
            Multiselect = true
        };

        if (picker.ShowDialog() != true || picker.FileNames.Length < 2)
        {
            if (picker.FileNames.Length == 1)
            {
                ShowAlert("Select at least two documents to merge.", "Merge PDFs",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        var target = new SaveFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Save merged document as",
            FileName = "Merged.pdf"
        };
        if (target.ShowDialog() != true) return;

        StatusText = $"Merging {picker.FileNames.Length} documents...";
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await engine.PageOrganizer.MergeDocumentsAsync(picker.FileNames, target.FileName);

            StatusText = $"Merged {picker.FileNames.Length} documents into {Path.GetFileName(target.FileName)}.";
            ShowAlert($"Merged {picker.FileNames.Length} documents into:\n{target.FileName}",
                "Merge PDFs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Merge failed: {ex.Message}";
            ShowAlert($"Could not merge the documents:\n\n{ex.Message}", "Merge PDFs",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Splits the open document into one file per page.
    /// </summary>
    [RelayCommand]
    public async Task SplitDocumentAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        var folder = new OpenFolderDialog { Title = "Choose a folder for the split pages" };
        if (folder.ShowDialog() != true) return;

        string prefix = Path.GetFileNameWithoutExtension(_docService.CurrentFilePath);
        StatusText = "Splitting document...";

        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath);

            // One page per output file.
            var perSplit = Enumerable.Repeat(1, doc.PageCount).ToList();
            await engine.PageOrganizer.SplitDocumentAsync(doc, perSplit, folder.FolderName, prefix);

            StatusText = $"Split into {doc.PageCount} files in {folder.FolderName}.";
            ShowAlert($"Split into {doc.PageCount} single-page documents in:\n{folder.FolderName}",
                "Split PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Split failed: {ex.Message}";
            ShowAlert($"Could not split the document:\n\n{ex.Message}", "Split PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Extracts the current page into its own PDF.
    /// </summary>
    [RelayCommand]
    public async Task ExtractCurrentPageAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        int pageNumber = CurrentPageNumber;
        var target = new SaveFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = $"Save page {pageNumber} as",
            FileName = $"{Path.GetFileNameWithoutExtension(_docService.CurrentFilePath)}_page{pageNumber}.pdf"
        };
        if (target.ShowDialog() != true) return;

        StatusText = $"Extracting page {pageNumber}...";
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath);
            await engine.PageOrganizer.ExtractPagesAsync(doc, new[] { pageNumber }, target.FileName);

            StatusText = $"Page {pageNumber} saved to {Path.GetFileName(target.FileName)}.";
            ShowAlert($"Page {pageNumber} saved to:\n{target.FileName}", "Extract Page",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Extract failed: {ex.Message}";
            ShowAlert($"Could not extract page {pageNumber}:\n\n{ex.Message}", "Extract Page",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// The text produced by the most recent page text recognition. Exposed so the result is
    /// available even when the clipboard could not be written.
    /// </summary>
    [ObservableProperty]
    private string _recognizedPageText = string.Empty;

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            // Clipboard locked by another process, or no STA thread available.
            return false;
        }
    }

    /// <summary>
    /// Verifies every digital signature in the open document and reports the result.
    /// </summary>
    [RelayCommand]
    public async Task VerifySignaturesAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        StatusText = "Verifying digital signatures...";
        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath);
            var signatures = await engine.SignatureService.GetSignaturesAsync(doc);

            if (signatures.Count == 0)
            {
                StatusText = "This document is not digitally signed.";
                ShowAlert("This document contains no digital signatures.",
                    "Digital Signatures", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var report = new StringBuilder();
            foreach (var s in signatures)
            {
                report.AppendLine($"{s.FieldName}: {s.Status}");
                if (!string.IsNullOrWhiteSpace(s.SignerName)) report.AppendLine($"    Signer: {s.SignerName}");
                if (s.SigningTime.HasValue) report.AppendLine($"    Signed: {s.SigningTime:yyyy-MM-dd HH:mm:ss} UTC");
                if (!string.IsNullOrWhiteSpace(s.Reason)) report.AppendLine($"    Reason: {s.Reason}");
                if (!string.IsNullOrWhiteSpace(s.StatusMessage)) report.AppendLine($"    {s.StatusMessage}");
                report.AppendLine();
            }

            bool allValid = signatures.All(s => s.Status == PdfEngine.Signatures.SignatureStatus.Valid);
            StatusText = allValid
                ? $"{signatures.Count} signature(s) verified successfully."
                : "One or more signatures could not be validated.";

            ShowAlert(report.ToString().TrimEnd(), "Digital Signatures", MessageBoxButton.OK,
                allValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText = $"Signature verification failed: {ex.Message}";
            ShowAlert($"Could not verify signatures:\n\n{ex.Message}",
                "Digital Signatures", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Extracts the text of the current page, using real optical recognition when the page
    /// has no embedded text layer (a scanned image).
    /// </summary>
    [RelayCommand]
    public async Task RecognizeTextOnPageAsync()
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(_docService.CurrentFilePath)) return;

        int pageNumber = CurrentPageNumber;
        StatusText = $"Reading text on page {pageNumber}...";

        try
        {
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(_docService.CurrentFilePath);

            var result = await OcrEngine.RecognizePageAsync(doc, pageNumber);

            if (string.IsNullOrWhiteSpace(result.FullText))
            {
                StatusText = $"No text found on page {pageNumber}.";
                ShowAlert(
                    $"No text could be read from page {pageNumber}.\n\n{result.Notes}",
                    "Text Recognition", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // The clipboard can legitimately fail - another process may hold it open, and it
            // is unavailable off the UI thread. Losing the clipboard copy must not be
            // reported as losing the recognized text.
            bool copied = TryCopyToClipboard(result.FullText);

            string how = result.UsedOpticalRecognition
                ? "recognized optically"
                : "read from the page's text layer";

            RecognizedPageText = result.FullText;

            StatusText = copied
                ? $"Page {pageNumber} text {how} and copied to the clipboard ({result.FullText.Length} characters)."
                : $"Page {pageNumber} text {how} ({result.FullText.Length} characters); the clipboard was unavailable.";

            ShowAlert(
                copied
                    ? $"Text {how} and copied to the clipboard.\n\n{result.Notes}"
                    : $"Text {how}, but the clipboard could not be updated.\n\n{result.Notes}",
                "Text Recognition", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Text recognition failed: {ex.Message}";
            ShowAlert($"Could not read text from page {pageNumber}:\n\n{ex.Message}",
                "Text Recognition", MessageBoxButton.OK, MessageBoxImage.Error);
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
            ShowAlert($"Print failed: {ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            ShowAlert($"Successfully exported {end - start + 1} pages to:\n{outDir}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowAlert($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Export error: {ex.Message}";
        }
    }

    #endregion
}
