using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Devices;
using Aspose.Pdf.Text;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// Core service managing PDF document loading, rendering, text search, bookmark extraction, and printing via Aspose.Pdf.
/// </summary>
public class PdfDocumentService : IDisposable
{
    private Document? _document;
    private readonly object _docLock = new();
    private string _currentFilePath = string.Empty;
    private bool _disposed;

    public bool IsDocumentLoaded => _document != null;
    public string CurrentFilePath => _currentFilePath;
    public int PageCount => _document?.Pages?.Count ?? 0;

    /// <summary>
    /// Opens a PDF document from file path, optionally with password.
    /// </summary>
    public async Task<DocumentMetadata> OpenDocumentAsync(string filePath, string? password = null)
    {
        return await Task.Run(() =>
        {
            lock (_docLock)
            {
                CloseDocument();

                if (!File.Exists(filePath))
                    throw new FileNotFoundException($"File not found: {filePath}", filePath);

                Document doc;
                if (!string.IsNullOrEmpty(password))
                {
                    doc = new Document(filePath, password);
                }
                else
                {
                    doc = new Document(filePath);
                }

                _document = doc;
                _currentFilePath = filePath;

                return ExtractMetadata(filePath, doc);
            }
        });
    }

    /// <summary>
    /// Closes the currently active document and frees resources.
    /// </summary>
    public void CloseDocument()
    {
        lock (_docLock)
        {
            if (_document != null)
            {
                try
                {
                    _document.Dispose();
                }
                catch { }
                _document = null;
            }
            _currentFilePath = string.Empty;
        }
    }

    /// <summary>
    /// Extracts document metadata and properties.
    /// </summary>
    public DocumentMetadata? GetMetadata()
    {
        lock (_docLock)
        {
            if (_document == null) return null;
            return ExtractMetadata(_currentFilePath, _document);
        }
    }

    private static DocumentMetadata ExtractMetadata(string filePath, Document doc)
    {
        var fileInfo = new FileInfo(filePath);
        double width = 0, height = 0;

        if (doc.Pages.Count > 0)
        {
            var firstPage = doc.Pages[1];
            width = firstPage.Rect.Width;
            height = firstPage.Rect.Height;
        }

        return new DocumentMetadata
        {
            FileName = fileInfo.Name,
            FilePath = fileInfo.FullName,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            PageCount = doc.Pages.Count,
            Title = doc.Info?.Title ?? string.Empty,
            Author = doc.Info?.Author ?? string.Empty,
            Subject = doc.Info?.Subject ?? string.Empty,
            Keywords = doc.Info?.Keywords ?? string.Empty,
            Creator = doc.Info?.Creator ?? string.Empty,
            Producer = doc.Info?.Producer ?? string.Empty,
            CreationDate = doc.Info?.CreationDate,
            ModDate = doc.Info?.ModDate,
            PdfFormatVersion = doc.Version ?? "1.7",
            IsEncrypted = doc.IsEncrypted,
            IsLinearized = doc.IsLinearized,
            DefaultPageWidthPt = width,
            DefaultPageHeightPt = height,
            LicenseStatus = LicenseService.LicenseStatusMessage
        };
    }

    /// <summary>
    /// Gets original dimensions (points) for a given page.
    /// </summary>
    public (double Width, double Height) GetPageDimensions(int pageNumber)
    {
        lock (_docLock)
        {
            if (_document == null || pageNumber < 1 || pageNumber > _document.Pages.Count)
                return (612, 792); // Standard letter fallback

            var page = _document.Pages[pageNumber];
            return (page.Rect.Width, page.Rect.Height);
        }
    }

    /// <summary>
    /// Renders a single PDF page into a frozen WPF BitmapSource.
    /// Thread-safe and safe to execute on background worker threads.
    /// </summary>
    public BitmapSource? RenderPage(int pageNumber, int dpi = 150, int rotationAngle = 0)
    {
        lock (_docLock)
        {
            if (_document == null || pageNumber < 1 || pageNumber > _document.Pages.Count)
                return null;

            var page = _document.Pages[pageNumber];
            
            // Adjust rotation if specified
            var originalRotation = page.Rotate;
            if (rotationAngle != 0)
            {
                var combinedRotation = ((int)originalRotation * 90 + rotationAngle) % 360;
                page.Rotate = combinedRotation switch
                {
                    90 => Aspose.Pdf.Rotation.on90,
                    180 => Aspose.Pdf.Rotation.on180,
                    270 => Aspose.Pdf.Rotation.on270,
                    _ => Aspose.Pdf.Rotation.None
                };
            }

            try
            {
                var resolution = new Resolution(dpi);
                var pngDevice = new PngDevice(resolution);

                using var stream = new MemoryStream();
                pngDevice.Process(page, stream);
                stream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); // Must be frozen for cross-thread access

                return bitmap;
            }
            finally
            {
                if (rotationAngle != 0)
                {
                    page.Rotate = originalRotation; // Restore
                }
            }
        }
    }

    /// <summary>
    /// Asynchronously renders a page.
    /// </summary>
    public async Task<BitmapSource?> RenderPageAsync(int pageNumber, int dpi = 150, int rotationAngle = 0, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return RenderPage(pageNumber, dpi, rotationAngle);
        }, ct);
    }

    /// <summary>
    /// Extracts the hierarchical bookmarks / outline tree.
    /// </summary>
    public ObservableCollection<BookmarkItem> ExtractBookmarks()
    {
        var roots = new ObservableCollection<BookmarkItem>();
        lock (_docLock)
        {
            if (_document?.Outlines == null || _document.Outlines.Count == 0)
                return roots;

            foreach (OutlineItemCollection item in _document.Outlines)
            {
                var node = ConvertOutlineItem(item);
                if (node != null)
                    roots.Add(node);
            }
        }
        return roots;
    }

    private BookmarkItem? ConvertOutlineItem(OutlineItemCollection item)
    {
        if (item == null) return null;

        int pageNum = 1;
        try
        {
            if (item.Destination is ExplicitDestination expDest)
            {
                pageNum = expDest.PageNumber;
            }
            else if (item.Action is GoToAction goAction && goAction.Destination is ExplicitDestination actionDest)
            {
                pageNum = actionDest.PageNumber;
            }
        }
        catch
        {
            pageNum = 1;
        }

        if (pageNum < 1) pageNum = 1;

        var bookmark = new BookmarkItem
        {
            Title = item.Title ?? "Untitled",
            TargetPageNumber = pageNum,
            IsBold = item.Bold,
            IsItalic = item.Italic
        };

        try
        {
            foreach (OutlineItemCollection child in item)
            {
                var childNode = ConvertOutlineItem(child);
                if (childNode != null)
                    bookmark.Children.Add(childNode);
            }
        }
        catch { }

        return bookmark;
    }

    /// <summary>
    /// Searches the document for text query.
    /// </summary>
    public async Task<List<SearchMatch>> SearchTextAsync(string query, bool matchCase = false, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<SearchMatch>();
            if (string.IsNullOrWhiteSpace(query))
                return results;

            lock (_docLock)
            {
                if (_document == null) return results;

                var textSearchOptions = new TextSearchOptions(matchCase);
                int matchIndex = 1;

                for (int p = 1; p <= _document.Pages.Count; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = _document.Pages[p];
                    var absorber = new TextFragmentAbsorber(query, textSearchOptions);
                    page.Accept(absorber);

                    double pageWidth = page.Rect.Width;
                    double pageHeight = page.Rect.Height;

                    foreach (TextFragment fragment in absorber.TextFragments)
                    {
                        ct.ThrowIfCancellationRequested();
                        var rect = fragment.Rectangle;
                        
                        // Calculate snippet around text
                        string snippet = fragment.Text;
                        
                        // Normalized coordinates relative to top-left
                        double normX = rect.LLX / pageWidth;
                        double normY = 1.0 - (rect.URY / pageHeight);
                        double normW = (rect.URX - rect.LLX) / pageWidth;
                        double normH = (rect.URY - rect.LLY) / pageHeight;

                        results.Add(new SearchMatch
                        {
                            MatchIndex = matchIndex++,
                            PageNumber = p,
                            Text = fragment.Text,
                            ContextSnippet = snippet,
                            X = Math.Max(0, normX),
                            Y = Math.Max(0, normY),
                            Width = Math.Max(0.01, normW),
                            Height = Math.Max(0.01, normH)
                        });
                    }
                }
            }

            return results;
        }, ct);
    }

    /// <summary>
    /// Exports pages to image files (PNG/JPEG).
    /// </summary>
    public async Task ExportPagesToImagesAsync(
        string outputDirectory,
        string fileNamePrefix,
        int startPage,
        int endPage,
        string format = "PNG",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            lock (_docLock)
            {
                if (_document == null) throw new InvalidOperationException("No document is open.");

                Directory.CreateDirectory(outputDirectory);
                startPage = Math.Max(1, startPage);
                endPage = Math.Min(_document.Pages.Count, endPage);
                int totalToExport = endPage - startPage + 1;

                var resolution = new Resolution(dpi);
                ImageDevice device = format.Equals("JPEG", StringComparison.OrdinalIgnoreCase) || format.Equals("JPG", StringComparison.OrdinalIgnoreCase)
                    ? new JpegDevice(resolution, 95)
                    : new PngDevice(resolution);

                string ext = format.ToLowerInvariant();
                if (ext == "jpeg") ext = "jpg";

                for (int p = startPage; p <= endPage; p++)
                {
                    ct.ThrowIfCancellationRequested();

                    string outPath = Path.Combine(outputDirectory, $"{fileNamePrefix}_page_{p:D3}.{ext}");
                    using var fileStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
                    device.Process(_document.Pages[p], fileStream);

                    double pct = (double)(p - startPage + 1) / totalToExport * 100.0;
                    progress?.Report(pct);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Prints the document pages using standard WPF PrintDialog.
    /// </summary>
    public void PrintDocument(PrintDialog printDialog, int fromPage = 1, int toPage = -1)
    {
        lock (_docLock)
        {
            if (_document == null) return;

            int totalPages = _document.Pages.Count;
            int start = Math.Max(1, fromPage);
            int end = toPage < 1 ? totalPages : Math.Min(totalPages, toPage);

            var docPaginator = new AsposePdfPaginator(this, start, end, printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight);
            printDialog.PrintDocument(docPaginator, $"Printing {Path.GetFileName(_currentFilePath)}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CloseDocument();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Custom DocumentPaginator for high-quality printing of Aspose PDF pages.
/// </summary>
public class AsposePdfPaginator : System.Windows.Documents.DocumentPaginator
{
    private readonly PdfDocumentService _service;
    private readonly int _startPage;
    private readonly int _endPage;
    private readonly Size _pageSize;

    public AsposePdfPaginator(PdfDocumentService service, int startPage, int endPage, double pageWidth, double pageHeight)
    {
        _service = service;
        _startPage = startPage;
        _endPage = endPage;
        _pageSize = new Size(pageWidth, pageHeight);
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => Math.Max(0, _endPage - _startPage + 1);
    public override Size PageSize
    {
        get => _pageSize;
        set { }
    }
    public override System.Windows.Documents.IDocumentPaginatorSource? Source => null;

    public override System.Windows.Documents.DocumentPage GetPage(int pageNumber)
    {
        int actualPageNum = _startPage + pageNumber;
        var bitmap = _service.RenderPage(actualPageNum, dpi: 300);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (bitmap != null)
            {
                // Scale to fit printable area preserving aspect ratio
                double scaleX = _pageSize.Width / bitmap.PixelWidth;
                double scaleY = _pageSize.Height / bitmap.PixelHeight;
                double scale = Math.Min(scaleX, scaleY);

                double drawWidth = bitmap.PixelWidth * scale;
                double drawHeight = bitmap.PixelHeight * scale;
                double offsetX = (_pageSize.Width - drawWidth) / 2.0;
                double offsetY = (_pageSize.Height - drawHeight) / 2.0;

                dc.DrawImage(bitmap, new Rect(offsetX, offsetY, drawWidth, drawHeight));
            }
        }

        return new System.Windows.Documents.DocumentPage(visual, _pageSize, new Rect(_pageSize), new Rect(_pageSize));
    }
}
