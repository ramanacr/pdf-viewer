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
    /// PDF text matrices are column-major affine matrices where (x', y') = (A*x + C*y + E, B*x + D*y + F).
    /// The text matrix encodes the glyph run origin in unflipped PDF page space (y increases upward).
    /// We transform each glyph's X offset through the text matrix to get page-space coordinates,
    /// then normalize into [0, 1] top-left space expected by the hit-test overlay.
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
            if (string.IsNullOrEmpty(run.FullText) || run.Glyphs.Count == 0) continue;

            // The text matrix encodes the run origin in PDF page space.
            // glyph OffsetX is in unscaled glyph space; scale by FontSize and HorizontalScaling.
            double hScale = run.HorizontalScaling / 100.0;
            double fontSize = run.FontSize;

            // Estimate glyph height from the font size; use FontSize as the cap-height approximation.
            // A reasonable ascent/descent split: ascent ~0.7, descent ~0.3 of fontSize.
            double ascentEst = fontSize * 0.75;
            double descentEst = fontSize * 0.25;

            double runningX = 0.0; // accumulates in glyph-space X

            for (int i = 0; i < run.Glyphs.Count; i++)
            {
                var glyph = run.Glyphs[i];
                if (string.IsNullOrEmpty(glyph.Unicode) || glyph.Unicode == " ")
                {
                    runningX += glyph.AdvanceX * fontSize * hScale + run.CharacterSpacing;
                    continue;
                }

                // Transform glyph left edge through text matrix into PDF page space
                // TextMatrix: x' = A*x + C*y + E,  y' = B*x + D*y + F
                // At baseline y=0, glyph left edge x=runningX
                var m = run.TextMatrix;
                double glyphPageX = m.A * runningX + m.E;
                double glyphPageY = m.B * runningX + m.F;

                // Glyph advance in page space (includes font size scaling and horizontal scaling)
                double advanceScaled = glyph.AdvanceX * fontSize * hScale;

                // Glyph right edge in page space
                double glyphPageX2 = m.A * (runningX + advanceScaled) + m.E;
                double glyphPageY2 = m.B * (runningX + advanceScaled) + m.F;

                // Bounding box top/bottom: ascent/descent applied along the matrix D/C axis
                double topY = glyphPageY + m.D * ascentEst - m.C * ascentEst;
                double bottomY = glyphPageY - m.D * descentEst + m.C * descentEst;

                // Build axis-aligned bounding box in PDF page space
                double minX = Math.Min(glyphPageX, glyphPageX2);
                double maxX = Math.Max(glyphPageX, glyphPageX2);
                double minY = Math.Min(bottomY, topY);
                double maxY = Math.Max(bottomY, topY);

                // Ensure minimum glyph width for hit-testing (single-pixel chars)
                if (maxX - minX < 0.5) maxX = minX + fontSize * 0.5;

                // Normalize to [0, 1]: X left-to-right, Y top-to-bottom (PDF y flipped)
                double normX = Math.Max(0, minX / pageW);
                double normY = Math.Max(0, 1.0 - (maxY / pageH));  // flip y: PDF bottom-left → screen top-left
                double normW = Math.Max(0.001, (maxX - minX) / pageW);
                double normH = Math.Max(0.001, (maxY - minY) / pageH);

                // Clamp to page bounds
                normX = Math.Min(normX, 1.0 - normW);
                normY = Math.Min(normY, 1.0 - normH);

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

                runningX += advanceScaled + run.CharacterSpacing;
                if (glyph.Unicode == " ") runningX += run.WordSpacing;
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
