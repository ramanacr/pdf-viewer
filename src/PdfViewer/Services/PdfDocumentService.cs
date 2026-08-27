using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
using AnnotationType = PdfViewer.Models.AnnotationType;

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

    #region Annotations & Multi-Mode Saving

    /// <summary>
    /// Converts a Hex color code into an Aspose.Pdf.Color.
    /// </summary>
    public static Aspose.Pdf.Color AsposeColorFromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Aspose.Pdf.Color.FromRgb(System.Drawing.Color.LimeGreen);
        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Aspose.Pdf.Color.FromRgb(r, g, b);
            }
            if (hex.Length == 8)
            {
                // Format: AARRGGBB -> extract RGB
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Aspose.Pdf.Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return Aspose.Pdf.Color.FromRgb(System.Drawing.Color.LimeGreen);
    }

    /// <summary>
    /// Loads existing annotations from the current document.
    /// </summary>
    public List<AnnotationModel> LoadExistingAnnotations()
    {
        var list = new List<AnnotationModel>();
        lock (_docLock)
        {
            if (_document == null) return list;

            for (int p = 1; p <= _document.Pages.Count; p++)
            {
                var page = _document.Pages[p];
                double pageWidth = page.Rect.Width;
                double pageHeight = page.Rect.Height;

                foreach (Annotation annot in page.Annotations)
                {
                    // Only process actual markup/content annotations, ignore LinkAnnotation, WidgetAnnotation, PopupAnnotation, etc.
                    AnnotationType type;
                    if (annot is HighlightAnnotation) type = AnnotationType.Highlight;
                    else if (annot is UnderlineAnnotation) type = AnnotationType.Underline;
                    else if (annot is StrikeOutAnnotation) type = AnnotationType.StrikeOut;
                    else if (annot is TextAnnotation) type = AnnotationType.Note;
                    else if (annot is FreeTextAnnotation) type = AnnotationType.FreeText;
                    else if (annot is SquareAnnotation) type = AnnotationType.Rectangle;
                    else if (annot is CircleAnnotation) type = AnnotationType.Ellipse;
                    else if (annot is InkAnnotation) type = AnnotationType.Ink;
                    else continue; // Skip links, widgets, forms, media, and other non-markup annotations

                    var rect = annot.Rect;
                    double normX = Math.Max(0, rect.LLX / pageWidth);
                    double normY = Math.Max(0, 1.0 - (rect.URY / pageHeight));
                    double normW = Math.Max(0.01, (rect.URX - rect.LLX) / pageWidth);
                    double normH = Math.Max(0.01, (rect.URY - rect.LLY) / pageHeight);

                    string colorHex = "#FF32CD32";
                    if (annot.Color != null && annot.Color != Aspose.Pdf.Color.Empty && annot.Color != Aspose.Pdf.Color.Transparent)
                    {
                        var rgb = annot.Color.ToRgb();
                        colorHex = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
                    }

                    var markup = annot as MarkupAnnotation;
                    double opacity = (markup != null && markup.Opacity > 0) ? markup.Opacity : (type == AnnotationType.Highlight ? 0.4 : 1.0);

                    list.Add(new AnnotationModel
                    {
                        PageNumber = p,
                        Type = type,
                        X = normX,
                        Y = normY,
                        Width = normW,
                        Height = normH,
                        ColorHex = colorHex,
                        Opacity = opacity,
                        Contents = annot.Contents ?? string.Empty,
                        Author = markup?.Title ?? "Author",
                        Title = markup?.Subject ?? annot.GetType().Name
                    });
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Saves the document with annotations applied, with strict prohibition against overwriting the original file.
    /// </summary>
    public async Task SaveAnnotatedDocumentAsync(
        string targetPath,
        AnnotationSaveMode mode,
        IEnumerable<AnnotationModel> annotations,
        string? originalPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target file path cannot be empty.", nameof(targetPath));

        // CRITICAL: Strict check never to save to original file
        if (!string.IsNullOrEmpty(originalPath))
        {
            string fullTarget = Path.GetFullPath(targetPath);
            string fullOriginal = Path.GetFullPath(originalPath);
            if (string.Equals(fullTarget, fullOriginal, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Saving directly over the original source file is prohibited. Please specify a new file name or directory.");
            }
        }

        await Task.Run(() =>
        {
            lock (_docLock)
            {
                if (_document == null)
                    throw new InvalidOperationException("No document is currently loaded.");

                if (mode == AnnotationSaveMode.ExportXfdf)
                {
                    ExportAnnotationsToXfdf(targetPath, annotations);
                    return;
                }

                // Create working clone stream to avoid mutating active in-memory document permanently
                using var memStream = new MemoryStream();
                _document.Save(memStream);
                memStream.Position = 0;

                using var saveDoc = new Document(memStream);

                // Apply annotations to target document
                foreach (var annot in annotations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (annot.PageNumber < 1 || annot.PageNumber > saveDoc.Pages.Count) continue;

                    var page = saveDoc.Pages[annot.PageNumber];
                    double pageWidth = page.Rect.Width;
                    double pageHeight = page.Rect.Height;

                    double llx = Math.Max(0, annot.X * pageWidth);
                    double lly = Math.Max(0, (1.0 - (annot.Y + annot.Height)) * pageHeight);
                    double urx = Math.Min(pageWidth, (annot.X + annot.Width) * pageWidth);
                    double ury = Math.Min(pageHeight, (1.0 - annot.Y) * pageHeight);

                    var rect = new Aspose.Pdf.Rectangle(llx, lly, urx, ury);
                    var asposeColor = AsposeColorFromHex(annot.ColorHex);

                    switch (annot.Type)
                    {
                        case AnnotationType.Highlight:
                            page.Annotations.Add(new HighlightAnnotation(page, rect)
                            {
                                Title = annot.Author,
                                Subject = annot.Title,
                                Contents = annot.Contents,
                                Color = asposeColor,
                                Opacity = annot.Opacity
                            });
                            break;

                        case AnnotationType.Underline:
                            page.Annotations.Add(new UnderlineAnnotation(page, rect)
                            {
                                Title = annot.Author,
                                Contents = annot.Contents,
                                Color = asposeColor
                            });
                            break;

                        case AnnotationType.StrikeOut:
                            page.Annotations.Add(new StrikeOutAnnotation(page, rect)
                            {
                                Title = annot.Author,
                                Contents = annot.Contents,
                                Color = asposeColor
                            });
                            break;

                        case AnnotationType.Note:
                            page.Annotations.Add(new TextAnnotation(page, rect)
                            {
                                Title = annot.Author,
                                Subject = annot.Title,
                                Contents = annot.Contents,
                                Color = asposeColor,
                                Open = false,
                                Icon = TextIcon.Comment
                            });
                            break;

                        case AnnotationType.FreeText:
                            page.Annotations.Add(new FreeTextAnnotation(page, rect, new DefaultAppearance("Arial", 12, System.Drawing.Color.Black))
                            {
                                Title = annot.Author,
                                Contents = annot.Contents,
                                Color = asposeColor
                            });
                            break;

                        case AnnotationType.Rectangle:
                            page.Annotations.Add(new SquareAnnotation(page, rect)
                            {
                                Title = annot.Author,
                                Contents = annot.Contents,
                                Color = asposeColor,
                                Opacity = annot.Opacity
                            });
                            break;

                        case AnnotationType.Ellipse:
                            page.Annotations.Add(new CircleAnnotation(page, rect)
                            {
                                Title = annot.Author,
                                Contents = annot.Contents,
                                Color = asposeColor,
                                Opacity = annot.Opacity
                            });
                            break;

                        case AnnotationType.Ink:
                            var inkList = new List<Aspose.Pdf.Point[]>();
                            if (annot.InkPoints != null && annot.InkPoints.Count > 1)
                            {
                                var pts = annot.InkPoints
                                    .Select(p => new Aspose.Pdf.Point(p.X * pageWidth, (1.0 - p.Y) * pageHeight))
                                    .ToArray();
                                inkList.Add(pts);
                            }
                            else
                            {
                                inkList.Add(new[] { new Aspose.Pdf.Point(llx, lly), new Aspose.Pdf.Point(urx, ury) });
                            }
                            var inkAnnot = new InkAnnotation(page, rect, (System.Collections.Generic.IList<Aspose.Pdf.Point[]>)inkList)
                            {
                                Title = annot.Author,
                                Contents = annot.Contents,
                                Color = asposeColor,
                                Opacity = annot.Opacity
                            };
                            page.Annotations.Add(inkAnnot);
                            break;
                    }
                }

                if (mode == AnnotationSaveMode.Flattened)
                {
                    saveDoc.Flatten();
                }

                string? outDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

                saveDoc.Save(targetPath);
            }
        }, ct);
    }

    /// <summary>
    /// Exports annotations to an XFDF XML comments file.
    /// </summary>
    public static void ExportAnnotationsToXfdf(string xfdfPath, IEnumerable<AnnotationModel> annotations)
    {
        string? outDir = Path.GetDirectoryName(xfdfPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        using var writer = new StreamWriter(xfdfPath, false, System.Text.Encoding.UTF8);
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\" xml:space=\"preserve\">");
        writer.WriteLine("  <annots>");

        foreach (var a in annotations)
        {
            string dateStr = a.CreationDate.ToString("yyyyMMddHHmmss");
            string annotTag = a.Type switch
            {
                AnnotationType.Highlight => "highlight",
                AnnotationType.Underline => "underline",
                AnnotationType.StrikeOut => "strikeout",
                AnnotationType.Note => "text",
                AnnotationType.FreeText => "freetext",
                AnnotationType.Rectangle => "square",
                AnnotationType.Ellipse => "circle",
                AnnotationType.Ink => "ink",
                _ => "highlight"
            };

            writer.WriteLine($"    <{annotTag} page=\"{a.PageNumber - 1}\" rect=\"{a.X:F4},{a.Y:F4},{a.X + a.Width:F4},{a.Y + a.Height:F4}\" color=\"{a.ColorHex}\" title=\"{System.Security.SecurityElement.Escape(a.Author)}\" date=\"D:{dateStr}\">");
            if (!string.IsNullOrEmpty(a.Contents))
            {
                writer.WriteLine($"      <contents>{System.Security.SecurityElement.Escape(a.Contents)}</contents>");
            }
            writer.WriteLine($"    </{annotTag}>");
        }

        writer.WriteLine("  </annots>");
        writer.WriteLine("</xfdf>");
    }

    #endregion

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
