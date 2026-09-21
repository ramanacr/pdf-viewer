using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Windows;
using PdfViewer.Core.Security;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// Hybrid PDF document service orchestrating vector-first parsing and rendering
/// with transparent, safe fallback to PDFium.
/// </summary>
public sealed class HybridVectorDocumentService : IPdfDocumentService
{
    private readonly PdfiumDocumentService _pdfiumService;
    private readonly WindowsVectorRenderer _vectorRenderer;
    private readonly ConcurrentDictionary<int, PageEngineReport> _pageEngineReports = new();
    private PdfVectorDocument? _vectorDoc;
    private PdfEngineMode _mode;
    private bool _disposed;

    public PdfEngineMode EngineMode
    {
        get => _mode;
        set => _mode = value;
    }

    public PageEngineReport? GetPageEngineReport(int pageNumber)
    {
        if (_pageEngineReports.TryGetValue(pageNumber, out var report))
        {
            return report;
        }

        if (_mode == PdfEngineMode.Pdfium)
        {
            return new PageEngineReport(
                pageNumber,
                PdfEngineMode.Pdfium,
                IsFallback: false,
                FallbackReasons: Array.Empty<string>(),
                BadgeText: "🖼️ PDFium",
                Tooltip: "Rendered via Google PDFium software rasterizer [Mode: PDFium]");
        }

        return new PageEngineReport(
            pageNumber,
            _mode,
            IsFallback: false,
            FallbackReasons: Array.Empty<string>(),
            BadgeText: _mode == PdfEngineMode.Vector ? "⚡ Vector" : "⚡ Vector (Auto)",
            Tooltip: "DirectX/WPF Vector Engine (Pending rendering or cached)");
    }

    public bool IsDocumentLoaded => _vectorDoc != null || _pdfiumService.IsDocumentLoaded;
    public string CurrentFilePath => _pdfiumService.CurrentFilePath;
    public int PageCount => _vectorDoc?.PageCount ?? _pdfiumService.PageCount;

    public HybridVectorDocumentService(
        PdfSecurityPolicy securityPolicy,
        PdfEngineMode mode = PdfEngineMode.Auto)
    {
        _pdfiumService = new PdfiumDocumentService(securityPolicy);
        _vectorRenderer = new WindowsVectorRenderer();
        _mode = mode;
    }

    public async Task<DocumentMetadata> OpenDocumentAsync(
        string filePath,
        string? password = null,
        CancellationToken ct = default)
    {
        // Always open PDFium for non-rendering services (search, annotations, bookmarks)
        var meta = await _pdfiumService.OpenDocumentAsync(filePath, password, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(password) && _mode != PdfEngineMode.Pdfium)
        {
            try
            {
                _vectorDoc = await PdfVectorDocument.OpenAsync(filePath, cancellationToken: ct).ConfigureAwait(false);
            }
            catch when (_mode != PdfEngineMode.Vector)
            {
                // In hybrid/auto mode, if vector parser encounters unsupported constructs at doc level,
                // fallback gracefully to PDFium
                _vectorDoc = null;
            }
        }

        return meta;
    }

    public void CloseDocument()
    {
        _pageEngineReports.Clear();
        _vectorDoc?.Dispose();
        _vectorDoc = null;
        _pdfiumService.CloseDocument();
    }

    public DocumentMetadata? GetMetadata() => _pdfiumService.GetMetadata();

    public (double Width, double Height) GetPageDimensions(int pageNumber)
    {
        if (_vectorDoc != null && pageNumber >= 1 && pageNumber <= _vectorDoc.PageCount)
        {
            var pageNode = _vectorDoc.PageTree.Pages[pageNumber - 1];
            return (pageNode.PageSize.Width, pageNode.PageSize.Height);
        }
        return _pdfiumService.GetPageDimensions(pageNumber);
    }

    public int GetPageIntrinsicRotation(int pageNumber)
    {
        if (_vectorDoc != null && pageNumber >= 1 && pageNumber <= _vectorDoc.PageCount)
        {
            var pageNode = _vectorDoc.PageTree.Pages[pageNumber - 1];
            return pageNode.RotationDegrees;
        }
        return _pdfiumService.GetPageIntrinsicRotation(pageNumber);
    }

    public BitmapSource? RenderPage(int pageNumber, int dpi = 150, int rotationAngle = 0)
    {
        return RenderPageAsync(pageNumber, dpi, rotationAngle).GetAwaiter().GetResult();
    }

    public async Task<BitmapSource?> RenderPageAsync(
        int pageNumber,
        int dpi = 150,
        int rotationAngle = 0,
        CancellationToken ct = default)
    {
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium)
        {
            IPdfDisplayList? displayList = null;
            try
            {
                displayList = await _vectorDoc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);

                // If strict vector mode or page has zero fallback constructs and valid commands, render natively!
                if (_mode == PdfEngineMode.Vector || (!displayList.HasFallback && displayList.Commands.Count > 0))
                {
                    var rot = (PageRotation)rotationAngle;
                    var request = new RenderRequest
                    {
                        PageNumber = pageNumber,
                        Dpi = dpi,
                        Rotation = rot
                    };

                    using var renderedPage = await _vectorRenderer.RenderDisplayListAsync(displayList, request, null, ct).ConfigureAwait(false);
                    if (renderedPage != null)
                    {
                        _pageEngineReports[pageNumber] = new PageEngineReport(
                            pageNumber,
                            PdfEngineMode.Vector,
                            IsFallback: false,
                            FallbackReasons: Array.Empty<string>(),
                            BadgeText: "⚡ Vector",
                            Tooltip: $"Rendered natively with Windows Vector Engine (DirectX / WPF DirectWrite)\nCommands: {displayList.Commands.Count}");
                        return CreateBitmapSource(renderedPage);
                    }
                }
            }
            catch (Exception ex) when (_mode != PdfEngineMode.Vector)
            {
                // Fallback to PDFium on rendering failure in hybrid mode
                _pageEngineReports[pageNumber] = new PageEngineReport(
                    pageNumber,
                    PdfEngineMode.Pdfium,
                    IsFallback: true,
                    FallbackReasons: new[] { $"Vector rendering error: {ex.Message}" },
                    BadgeText: "🖼️ PDFium (Fallback)",
                    Tooltip: $"Rendered with Google PDFium (Raster) due to error in vector renderer:\n• {ex.Message}");
                return await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
            }

            if (displayList != null && displayList.HasFallback)
            {
                var reasons = displayList.FallbackTokens
                    .Select(t => $"• {t.Reason}: {t.Description}")
                    .Distinct()
                    .ToList();

                _pageEngineReports[pageNumber] = new PageEngineReport(
                    pageNumber,
                    PdfEngineMode.Pdfium,
                    IsFallback: true,
                    FallbackReasons: reasons,
                    BadgeText: "🖼️ PDFium (Fallback)",
                    Tooltip: $"Rendered with Google PDFium (Raster) due to fallback:\n{string.Join("\n", reasons)}");
            }
            else if (displayList != null && displayList.Commands.Count == 0)
            {
                _pageEngineReports[pageNumber] = new PageEngineReport(
                    pageNumber,
                    PdfEngineMode.Pdfium,
                    IsFallback: true,
                    FallbackReasons: new[] { "Empty vector display list" },
                    BadgeText: "🖼️ PDFium (Fallback)",
                    Tooltip: "Rendered with Google PDFium (Raster) because page content stream produced 0 vector commands.");
            }
        }
        else if (_mode == PdfEngineMode.Pdfium)
        {
            _pageEngineReports[pageNumber] = new PageEngineReport(
                pageNumber,
                PdfEngineMode.Pdfium,
                IsFallback: false,
                FallbackReasons: Array.Empty<string>(),
                BadgeText: "🖼️ PDFium",
                Tooltip: "Rendered via Google PDFium software rasterizer [Mode: PDFium]");
        }

        // PDFium fallback / default path
        return await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
    }

    private static BitmapSource CreateBitmapSource(RenderedPage renderedPage)
    {
        var bitmap = BitmapSource.Create(
            renderedPage.WidthPixels,
            renderedPage.HeightPixels,
            renderedPage.Dpi,
            renderedPage.Dpi,
            PixelFormats.Pbgra32,
            null,
            renderedPage.Pixels.ToArray(),
            renderedPage.Stride);

        bitmap.Freeze();
        return bitmap;
    }

    public ObservableCollection<BookmarkItem> ExtractBookmarks()
    {
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium)
        {
            try
            {
                var vectorBookmarks = _vectorDoc.GetBookmarksAsync().GetAwaiter().GetResult();
                if (vectorBookmarks.Count > 0)
                {
                    var col = new ObservableCollection<BookmarkItem>();
                    foreach (var b in vectorBookmarks)
                    {
                        col.Add(ConvertBookmark(b));
                    }
                    return col;
                }
            }
            catch
            {
                // Fallback to PDFium
            }
        }
        return _pdfiumService.ExtractBookmarks();
    }

    private static BookmarkItem ConvertBookmark(PdfEngine.Documents.BookmarkItem item)
    {
        var model = new BookmarkItem
        {
            Title = item.Title,
            TargetPageNumber = item.TargetPageNumber
        };
        foreach (var child in item.Children)
        {
            model.Children.Add(ConvertBookmark(child));
        }
        return model;
    }

    public Task<List<SearchMatch>> SearchTextAsync(string query, bool matchCase = false, CancellationToken ct = default) =>
        _pdfiumService.SearchTextAsync(query, matchCase, ct);

    public List<PageTextSegment> ExtractPageTextSegments(int pageNumber)
    {
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium && IsPageVectorRendered(pageNumber))
        {
            try
            {
                var displayList = _vectorDoc.GetPageDisplayListAsync(pageNumber).GetAwaiter().GetResult();
                if (!displayList.HasFallback && displayList.Features.HasFlag(PdfFeatureSet.Text))
                {
                    var segments = ExtractCharSegmentsFromDisplayList(displayList);
                    if (segments.Count > 0) return segments;
                }
            }
            catch { }
        }
        return _pdfiumService.ExtractPageTextSegments(pageNumber);
    }

    public async Task<List<PageTextSegment>> ExtractPageTextSegmentsAsync(int pageNumber, CancellationToken ct = default)
    {
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium && IsPageVectorRendered(pageNumber))
        {
            try
            {
                var displayList = await _vectorDoc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
                if (!displayList.HasFallback && displayList.Features.HasFlag(PdfFeatureSet.Text))
                {
                    var segments = ExtractCharSegmentsFromDisplayList(displayList);
                    if (segments.Count > 0) return segments;
                }
            }
            catch { }
        }
        return await _pdfiumService.ExtractPageTextSegmentsAsync(pageNumber, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when the given page has an existing engine report confirming it was
    /// rendered natively by the vector pipeline (not a PDFium fallback).
    /// In Vector-only mode we always trust the display list; in Auto/Hybrid mode we require
    /// an explicit render to have happened so we know the page is vector-clean.
    /// </summary>
    private bool IsPageVectorRendered(int pageNumber)
    {
        if (_mode == PdfEngineMode.Vector)
            return true; // always use vector char extraction in strict vector mode

        if (_pageEngineReports.TryGetValue(pageNumber, out var report))
            return report.Engine == PdfEngineMode.Vector && !report.IsFallback;

        return false; // page not yet rendered — use PDFium word-level segments for safety
    }

    /// <summary>
    /// Extracts one <see cref="PageTextSegment"/> per glyph from the vector display list,
    /// giving true per-character selection precision in vector/hybrid mode.
    ///
    /// Coordinate conventions:
    ///   - <see cref="PdfGlyph.OffsetX"/>  is the cumulative glyph-left position in text space,
    ///     already fully scaled by (width/1000 * fontSize * hScale) inside PdfContentInterpreter.
    ///   - <see cref="PdfGlyph.AdvanceX"/> is also in text space, already fully scaled.
    ///   - <see cref="PdfGlyphRun.TextMatrix"/> transforms text-space coordinates into PDF
    ///     page space (y-up, origin at bottom-left of the page MediaBox).
    ///   - We normalize to [0, 1] screen space by dividing by page size and flipping y.
    /// </summary>
    private static List<PageTextSegment> ExtractCharSegmentsFromDisplayList(IPdfDisplayList displayList)
    {
        var list = new List<PageTextSegment>();
        double pageW = displayList.PageSize.Width;
        double pageH = displayList.PageSize.Height;
        if (pageW <= 0 || pageH <= 0) return list;

        int segIndex = 0;

        foreach (var cmd in displayList.Commands)
        {
            if (cmd is not DrawGlyphRun dgr) continue;

            var run = dgr.Run;
            if (run.Glyphs.Count == 0) continue;

            var m = run.TextMatrix;
            double fontSize = run.FontSize;

            // Ascent / descent in text-space units (already scaled by fontSize)
            double ascent  = fontSize * 0.75;
            double descent = fontSize * 0.25;

            foreach (var glyph in run.Glyphs)
            {
                if (string.IsNullOrEmpty(glyph.Unicode) || glyph.Unicode == " ")
                    continue;

                // glyph.OffsetX is the glyph's left edge in text space (fully scaled).
                // glyph.AdvanceX is the glyph width in text space (fully scaled).
                // Transform left and right edges through the text matrix to PDF page space.
                double leftX  = glyph.OffsetX;
                double rightX = glyph.OffsetX + glyph.AdvanceX;

                // Page-space corners (y=0 is baseline, ascent goes up, descent goes down)
                double pxLeft  = m.A * leftX  + m.E;
                double pyLeft  = m.B * leftX  + m.F;
                double pxRight = m.A * rightX + m.E;

                // Vertical extent: apply ascent/descent along the text matrix's y-axis (column C, D)
                double pxTop    = pxLeft + m.C * ascent;
                double pyTop    = pyLeft + m.D * ascent;
                double pyBottom = pyLeft - m.D * descent;

                // Axis-aligned bounding box in PDF page space
                double minX = Math.Min(pxLeft, pxRight);
                double maxX = Math.Max(pxLeft, pxRight);
                double minY = Math.Min(pyBottom, pyTop);
                double maxY = Math.Max(pyBottom, pyTop);

                // Guard: ensure non-zero width for hit-testing of narrow glyphs (e.g. 'i', '1', '.')
                if (maxX - minX < 1.0) maxX = minX + Math.Max(1.0, fontSize * 0.25);

                // Normalize to [0, 1] screen coordinates (flip y: PDF y-up → screen y-down)
                double normX = Math.Max(0.0, minX / pageW);
                double normY = Math.Max(0.0, 1.0 - maxY / pageH);
                double normW = Math.Max(0.001, (maxX - minX) / pageW);
                double normH = Math.Max(0.001, (maxY - minY) / pageH);

                // Clamp inside page
                if (normX + normW > 1.0) normX = Math.Max(0.0, 1.0 - normW);
                if (normY + normH > 1.0) normY = Math.Max(0.0, 1.0 - normH);

                list.Add(new PageTextSegment
                {
                    PageNumber = displayList.PageNumber,
                    Text = glyph.Unicode!,
                    X = normX,
                    Y = normY,
                    Width = normW,
                    Height = normH,
                    SegmentIndex = segIndex++
                });
            }
        }

        return list;
    }

    public Task ExportPagesToImagesAsync(
        string outputDirectory,
        string fileNamePrefix,
        int startPage,
        int endPage,
        string format = "PNG",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        _pdfiumService.ExportPagesToImagesAsync(outputDirectory, fileNamePrefix, startPage, endPage, format, dpi, progress, ct);

    public void PrintDocument(PrintDialog printDialog, int fromPage = 1, int toPage = -1, int rotationAngle = 0) =>
        _pdfiumService.PrintDocument(printDialog, fromPage, toPage, rotationAngle);

    public List<AnnotationModel> LoadExistingAnnotations() =>
        _pdfiumService.LoadExistingAnnotations();

    public Task SaveAnnotatedDocumentAsync(
        string targetPath,
        AnnotationSaveMode mode,
        IEnumerable<AnnotationModel> annotations,
        string? originalPath = null,
        CancellationToken ct = default) =>
        _pdfiumService.SaveAnnotatedDocumentAsync(targetPath, mode, annotations, originalPath, ct);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _vectorDoc?.Dispose();
            _vectorRenderer.Dispose();
            _pdfiumService.Dispose();
        }
    }
}
