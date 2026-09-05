using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfViewer.Core.Security;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// Native high-performance PDF service backed by Google PDFium.
/// </summary>
public class PdfiumDocumentService : IPdfDocumentService
{
    private SafeDocumentHandle? _document;
    // The process-wide PDFium lock, not a private one. PDFium's font/codec/render-device
    // state is global, so serializing only this service still raced the PdfEngine.Pdfium
    // adapters running against the same native library.
    private readonly object _docLock = PdfiumNativeBridge.PdfiumLock;
    private string _currentFilePath = string.Empty;
    private byte[]? _fileBytes;
    private IntPtr _nativeBuffer = IntPtr.Zero;
    private bool _disposed;

    public bool IsDocumentLoaded => _document != null && !_document.IsInvalid;
    public string CurrentFilePath => _currentFilePath;
    /// <summary>
    /// Page count. Takes the document lock like every other member: this was the one public
    /// member that did not, so a UI-thread read could run FPDF_GetPageCount concurrently
    /// with a background FPDF_RenderPageBitmap, and could still be inside the native call
    /// when CloseDocument freed the document's backing buffer.
    /// </summary>
    public int PageCount
    {
        get
        {
            lock (_docLock)
            {
                return (_document != null && !_document.IsInvalid)
                    ? PdfiumNativeBridge.FPDF_GetPageCount(_document)
                    : 0;
            }
        }
    }

    private readonly PdfSecurityPolicy _securityPolicy;

    /// <summary>
    /// The security policy enforced by this service at the document-open and render boundaries.
    /// </summary>
    public PdfSecurityPolicy SecurityPolicy => _securityPolicy;

    public PdfiumDocumentService(PdfSecurityPolicy? securityPolicy = null)
    {
        _securityPolicy = securityPolicy ?? PdfSecurityPolicy.DefaultStrict;
        PdfiumNativeBridge.EnsureInitialized();
    }

    /// <summary>
    /// Opens a PDF document from file path, optionally with password.
    /// </summary>
    public async Task<DocumentMetadata> OpenDocumentAsync(string filePath, string? password = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_docLock)
            {
                CloseDocument();

                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"File not found: {filePath}", filePath);
                }

                // Enforce the size ceiling BEFORE reading the file into memory, so an
                // oversized document is refused rather than allocated twice (managed array
                // plus unmanaged copy) on the way to failing.
                _securityPolicy.EnsureDocumentSizeAllowed(new FileInfo(filePath).Length, filePath);

                // Read file bytes into pinned unmanaged native buffer
                byte[] bytes = File.ReadAllBytes(filePath);
                IntPtr nativeBuf = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, nativeBuf, bytes.Length);

                var doc = PdfiumNativeBridge.FPDF_LoadMemDocument(nativeBuf, bytes.Length, password);

                if (doc == null || doc.IsInvalid)
                {
                    Marshal.FreeHGlobal(nativeBuf);
                    uint error = PdfiumNativeBridge.FPDF_GetLastError();
                    if (error == PdfiumNativeBridge.FPDF_ERR_PASSWORD)
                    {
                        throw new UnauthorizedAccessException("Password required or incorrect password for PDF document.");
                    }
                    if (error == PdfiumNativeBridge.FPDF_ERR_FORMAT)
                    {
                        throw new InvalidDataException("The file format is invalid or corrupted.");
                    }
                    throw new InvalidOperationException($"Failed to load PDF document (PDFium error code {error}).");
                }

                _document = doc;
                _nativeBuffer = nativeBuf;
                _fileBytes = bytes;
                _currentFilePath = filePath;

                return ExtractMetadata(filePath, doc);
            }
        }, ct);
    }

    /// <summary>
    /// Closes the currently active document and frees all engine resources.
    /// </summary>
    public void CloseDocument()
    {
        lock (_docLock)
        {
            if (_document != null)
            {
                if (!_document.IsClosed && !_document.IsInvalid)
                {
                    _document.Dispose();
                }
                _document = null;
            }
            if (_nativeBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_nativeBuffer);
                _nativeBuffer = IntPtr.Zero;
            }
            _fileBytes = null;
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
            if (_document == null || _document.IsInvalid) return null;
            return ExtractMetadata(_currentFilePath, _document);
        }
    }

    private static DocumentMetadata ExtractMetadata(string filePath, SafeDocumentHandle doc)
    {
        var fileInfo = new FileInfo(filePath);
        int pageCount = PdfiumNativeBridge.FPDF_GetPageCount(doc);
        double width = 612, height = 792;

        if (pageCount > 0)
        {
            if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(doc, 0, out var size) != 0)
            {
                width = size.width;
                height = size.height;
            }
        }

        string title = ReadMetaText(doc, "Title");
        string author = ReadMetaText(doc, "Author");
        string subject = ReadMetaText(doc, "Subject");
        string keywords = ReadMetaText(doc, "Keywords");
        string creator = ReadMetaText(doc, "Creator");
        string producer = ReadMetaText(doc, "Producer");
        string creationDateStr = ReadMetaText(doc, "CreationDate");
        string modDateStr = ReadMetaText(doc, "ModDate");

        string pdfVersion = "1.7";
        if (PdfiumNativeBridge.FPDF_GetFileVersion(doc, out int ver) != 0 && ver > 0)
        {
            pdfVersion = $"{(ver / 10)}.{(ver % 10)}";
        }

        int securityRevision = PdfiumNativeBridge.FPDF_GetSecurityHandlerRevision(doc);
        bool isEncrypted = securityRevision >= 0;

        return new DocumentMetadata
        {
            FileName = fileInfo.Name,
            FilePath = fileInfo.FullName,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            PageCount = pageCount,
            Title = title,
            Author = author,
            Subject = subject,
            Keywords = keywords,
            Creator = creator,
            Producer = producer,
            CreationDate = ParsePdfDate(creationDateStr),
            ModDate = ParsePdfDate(modDateStr),
            PdfFormatVersion = pdfVersion,
            IsEncrypted = isEncrypted,
            IsLinearized = false,
            DefaultPageWidthPt = width,
            DefaultPageHeightPt = height,
            LicenseStatus = "Google PDFium Engine (Native x64)"
        };
    }

    private static string ReadMetaText(SafeDocumentHandle doc, string tag)
    {
        uint len = PdfiumNativeBridge.FPDF_GetMetaText(doc, tag, null, 0);
        if (len <= 2) return string.Empty;

        byte[] buf = new byte[len];
        PdfiumNativeBridge.FPDF_GetMetaText(doc, tag, buf, len);
        return PdfiumNativeBridge.Utf16BytesToString(buf, (int)len);
    }

    private static DateTime? ParsePdfDate(string? pdfDate)
    {
        if (string.IsNullOrWhiteSpace(pdfDate)) return null;

        try
        {
            // PDF date format: D:YYYYMMDDHHmmSSOHH'mm'
            string clean = pdfDate.Trim();
            if (clean.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(2);
            }

            clean = clean.Replace("'", "").Replace("Z", "");
            if (clean.Length >= 8)
            {
                if (DateTime.TryParseExact(clean.Substring(0, Math.Min(clean.Length, 14)),
                    new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    return dt;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Gets original dimensions (points) for a given page.
    /// </summary>
    public (double Width, double Height) GetPageDimensions(int pageNumber)
    {
        lock (_docLock)
        {
            if (_document == null || _document.IsInvalid || pageNumber < 1 || pageNumber > PageCount)
                return (612, 792);

            if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_document, pageNumber - 1, out var size) != 0)
            {
                return (size.width, size.height);
            }

            return (612, 792);
        }
    }

    /// <summary>
    /// Synchronously renders a single PDF page into a frozen WPF BitmapSource directly via BGRA memory.
    /// </summary>
    public BitmapSource? RenderPage(int pageNumber, int dpi = 150, int rotationAngle = 0)
    {
        lock (_docLock)
        {
            if (_document == null || _document.IsInvalid || pageNumber < 1 || pageNumber > PageCount)
                return null;

            if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_document, pageNumber - 1, out var size) == 0)
                return null;

            double widthPt = size.width > 0 ? size.width : 612;
            double heightPt = size.height > 0 ? size.height : 792;

            int pdfRotation = ((rotationAngle % 360) + 360) % 360 / 90;
            bool isRotated90 = (pdfRotation == 1 || pdfRotation == 3);

            int widthPx = Math.Max(1, (int)Math.Round((isRotated90 ? heightPt : widthPt) * dpi / 72.0));
            int heightPx = Math.Max(1, (int)Math.Round((isRotated90 ? widthPt : heightPt) * dpi / 72.0));

            // Bound the raster before allocating: a hostile MediaBox combined with a high
            // zoom DPI can otherwise demand an arbitrarily large native bitmap.
            //
            // This REFUSES rather than silently degrading. A render that would breach the
            // policy is reported to the user (see PageViewModel.RenderErrorMessage and the
            // print preview error banner) instead of quietly returning a lower-resolution
            // image that looks like the real page.
            _securityPolicy.EnsureRenderDimensionsAllowed(widthPx, heightPx);

            using var page = PdfiumNativeBridge.FPDF_LoadPage(_document, pageNumber - 1);
            if (page == null || page.IsInvalid) return null;

            IntPtr bitmap = PdfiumNativeBridge.FPDFBitmap_CreateEx(widthPx, heightPx, PdfiumNativeBridge.FPDFBitmap_BGRA, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) return null;

            try
            {
                // Clear white background
                PdfiumNativeBridge.FPDFBitmap_FillRect(bitmap, 0, 0, widthPx, heightPx, 0xFFFFFFFF);

                // Render page content & annotations
                int renderFlags = PdfiumNativeBridge.FPDF_ANNOT | PdfiumNativeBridge.FPDF_LCD_TEXT;
                PdfiumNativeBridge.FPDF_RenderPageBitmap(bitmap, page, 0, 0, widthPx, heightPx, pdfRotation, renderFlags);

                IntPtr buffer = PdfiumNativeBridge.FPDFBitmap_GetBuffer(bitmap);
                int stride = PdfiumNativeBridge.FPDFBitmap_GetStride(bitmap);

                var wpfBitmap = BitmapSource.Create(
                    widthPx,
                    heightPx,
                    dpi,
                    dpi,
                    PixelFormats.Bgra32,
                    null,
                    buffer,
                    stride * heightPx,
                    stride);

                wpfBitmap.Freeze();
                return wpfBitmap;
            }
            finally
            {
                PdfiumNativeBridge.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    /// <summary>
    /// Asynchronously renders a single PDF page into a frozen WPF BitmapSource with cancellation support.
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
            if (_document == null || _document.IsInvalid) return roots;

            IntPtr firstChild = PdfiumNativeBridge.FPDFBookmark_GetFirstChild(_document, IntPtr.Zero);
            if (firstChild == IntPtr.Zero) return roots;

            TraverseBookmarks(_document, firstChild, roots);
        }
        return roots;
    }

    private static void TraverseBookmarks(SafeDocumentHandle doc, IntPtr currentBookmark, ObservableCollection<BookmarkItem> collection)
    {
        while (currentBookmark != IntPtr.Zero)
        {
            uint titleLen = PdfiumNativeBridge.FPDFBookmark_GetTitle(currentBookmark, null, 0);
            string title = "Untitled";
            if (titleLen > 2)
            {
                byte[] buf = new byte[titleLen];
                PdfiumNativeBridge.FPDFBookmark_GetTitle(currentBookmark, buf, titleLen);
                title = PdfiumNativeBridge.Utf16BytesToString(buf, (int)titleLen);
            }

            int targetPage = 1;
            IntPtr dest = PdfiumNativeBridge.FPDFBookmark_GetDest(doc, currentBookmark);
            if (dest == IntPtr.Zero)
            {
                IntPtr action = PdfiumNativeBridge.FPDFBookmark_GetAction(currentBookmark);
                if (action != IntPtr.Zero)
                {
                    dest = PdfiumNativeBridge.FPDFAction_GetDest(doc, action);
                }
            }

            if (dest != IntPtr.Zero)
            {
                int pageIdx = PdfiumNativeBridge.FPDFDest_GetDestPageIndex(doc, dest);
                if (pageIdx >= 0)
                {
                    targetPage = pageIdx + 1;
                }
            }

            var item = new BookmarkItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                TargetPageNumber = targetPage,
                IsBold = false,
                IsItalic = false
            };

            // Check for children
            IntPtr child = PdfiumNativeBridge.FPDFBookmark_GetFirstChild(doc, currentBookmark);
            if (child != IntPtr.Zero)
            {
                TraverseBookmarks(doc, child, item.Children);
            }

            collection.Add(item);
            currentBookmark = PdfiumNativeBridge.FPDFBookmark_GetNextSibling(doc, currentBookmark);
        }
    }

    /// <summary>
    /// Searches the document for text query.
    /// </summary>
    public async Task<List<SearchMatch>> SearchTextAsync(string query, bool matchCase = false, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<SearchMatch>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            lock (_docLock)
            {
                if (_document == null || _document.IsInvalid) return results;

                int pageCount = PageCount;
                int matchIndex = 1;
                byte[] findBytes = PdfiumNativeBridge.StringToUtf16NullTerminated(query);
                uint flags = matchCase ? PdfiumNativeBridge.FPDF_MATCHCASE : 0;

                for (int p = 1; p <= pageCount; p++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var page = PdfiumNativeBridge.FPDF_LoadPage(_document, p - 1);
                    if (page == null || page.IsInvalid) continue;

                    using var textPage = PdfiumNativeBridge.FPDFText_LoadPage(page);
                    if (textPage == null || textPage.IsInvalid) continue;

                    if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_document, p - 1, out var size) == 0) continue;
                    GetUnrotatedPageSize(page, size, out double pageWidth, out double pageHeight);

                    using var searchHandle = PdfiumNativeBridge.FPDFText_FindStart(textPage, findBytes, flags, 0);
                    if (searchHandle == null || searchHandle.IsInvalid) continue;

                    while (PdfiumNativeBridge.FPDFText_FindNext(searchHandle) != 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        int startIndex = PdfiumNativeBridge.FPDFText_GetSchResultIndex(searchHandle);
                        int count = PdfiumNativeBridge.FPDFText_GetSchCount(searchHandle);
                        if (count <= 0) continue;

                        double minLeft = double.MaxValue;
                        double maxTop = double.MinValue;
                        double maxRight = double.MinValue;
                        double minBottom = double.MaxValue;

                        // Use character boxes for precise sub-pixel glyph coverage
                        for (int i = startIndex; i < startIndex + count; i++)
                        {
                            if (PdfiumNativeBridge.FPDFText_GetCharBox(textPage, i, out double cl, out double cr, out double cb, out double ctBox) != 0)
                            {
                                double cLeft = Math.Min(cl, cr);
                                double cRight = Math.Max(cl, cr);
                                double cTop = Math.Max(ctBox, cb);
                                double cBottom = Math.Min(ctBox, cb);

                                if (cRight > cLeft && cTop > cBottom)
                                {
                                    minLeft = Math.Min(minLeft, cLeft);
                                    maxTop = Math.Max(maxTop, cTop);
                                    maxRight = Math.Max(maxRight, cRight);
                                    minBottom = Math.Min(minBottom, cBottom);
                                }
                            }
                        }

                        // Fallback to CountRects if GetCharBox returned no printable characters
                        if (minLeft == double.MaxValue || maxTop == double.MinValue)
                        {
                            int rectCount = PdfiumNativeBridge.FPDFText_CountRects(textPage, startIndex, count);
                            for (int r = 0; r < rectCount; r++)
                            {
                                if (PdfiumNativeBridge.FPDFText_GetRect(textPage, r, out double l, out double t, out double rt, out double b) != 0)
                                {
                                    minLeft = Math.Min(minLeft, Math.Min(l, rt));
                                    maxTop = Math.Max(maxTop, Math.Max(t, b));
                                    maxRight = Math.Max(maxRight, Math.Max(l, rt));
                                    minBottom = Math.Min(minBottom, Math.Min(t, b));
                                }
                            }
                        }

                        if (minLeft == double.MaxValue || maxTop == double.MinValue) continue;

                        // Optical padding: shift top upwards by 1.5 pt and expand height by 2.5 pt so the highlight box
                        // cleanly envelops font ascenders and descenders without dipping downwards into whitespace.
                        double padY = 1.5;
                        double padH = 2.5;
                        double padX = 1.0;

                        double normX = Math.Max(0, (minLeft - padX) / pageWidth);
                        double normY = Math.Max(0, 1.0 - ((maxTop + padY) / pageHeight));
                        double normW = Math.Max(0.005, ((maxRight - minLeft) + (padX * 2)) / pageWidth);
                        double normH = Math.Max(0.005, ((maxTop - minBottom) + padH) / pageHeight);

                        byte[] textBuffer = new byte[(count + 1) * 2];
                        int written = PdfiumNativeBridge.FPDFText_GetText(textPage, startIndex, count, textBuffer);
                        string matchText = written > 0 ? PdfiumNativeBridge.Utf16BytesToString(textBuffer, written * 2) : query;

                        results.Add(new SearchMatch
                        {
                            MatchIndex = matchIndex++,
                            PageNumber = p,
                            Text = matchText,
                            ContextSnippet = matchText,
                            X = normX,
                            Y = normY,
                            Width = normW,
                            Height = normH
                        });
                    }
                }
            }

            return results;
        }, ct);
    }

    /// <summary>
    /// Returns the page dimensions in UNROTATED page space.
    /// FPDF_GetPageSizeByIndexF reports the DISPLAY size - PDFium swaps width and height for
    /// pages with /Rotate 90 or 270 - but FPDFText_GetCharBox and FPDFAnnot_GetRect report
    /// boxes in unrotated page space. Normalizing those boxes by the swapped dimensions put
    /// every search highlight and selection rectangle in the wrong place on rotated pages
    /// (commonly clamped into the top-left corner), even though the rendered bitmap itself
    /// was correct.
    /// </summary>
    private static void GetUnrotatedPageSize(SafePageHandle page, FS_SIZEF displaySize, out double width, out double height)
    {
        double w = displaySize.width > 0 ? displaySize.width : 612;
        double h = displaySize.height > 0 ? displaySize.height : 792;

        int rotation = PdfiumNativeBridge.FPDFPage_GetRotation(page);
        if (rotation == 1 || rotation == 3)
        {
            (w, h) = (h, w);
        }

        width = w;
        height = h;
    }

    /// <summary>
    /// Extracts all selectable text segments and their normalized bounding boxes from a given page.
    /// </summary>
    public List<PageTextSegment> ExtractPageTextSegments(int pageNumber)
    {
        var list = new List<PageTextSegment>();
        lock (_docLock)
        {
            if (_document == null || _document.IsInvalid || pageNumber < 1 || pageNumber > PageCount)
                return list;

            using var page = PdfiumNativeBridge.FPDF_LoadPage(_document, pageNumber - 1);
            if (page == null || page.IsInvalid) return list;

            using var textPage = PdfiumNativeBridge.FPDFText_LoadPage(page);
            if (textPage == null || textPage.IsInvalid) return list;

            if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_document, pageNumber - 1, out var size) == 0) return list;
            GetUnrotatedPageSize(page, size, out double pageWidth, out double pageHeight);

            int totalChars = PdfiumNativeBridge.FPDFText_CountChars(textPage);
            if (totalChars <= 0) return list;

            // Group adjacent characters into word segments
            var currentWord = new StringBuilder();
            double wordLeft = double.MaxValue;
            double wordTop = double.MinValue;
            double wordRight = double.MinValue;
            double wordBottom = double.MaxValue;
            bool inWord = false;

            for (int i = 0; i < totalChars; i++)
            {
                uint unicode = PdfiumNativeBridge.FPDFText_GetUnicode(textPage, i);
                char ch = (char)unicode;

                if (char.IsWhiteSpace(ch) || unicode == 0 || ch == '\r' || ch == '\n')
                {
                    if (inWord && currentWord.Length > 0 && wordLeft != double.MaxValue)
                    {
                        AddSegment(list, pageNumber, currentWord.ToString(), wordLeft, wordTop, wordRight, wordBottom, pageWidth, pageHeight);
                        currentWord.Clear();
                        wordLeft = double.MaxValue;
                        wordTop = double.MinValue;
                        wordRight = double.MinValue;
                        wordBottom = double.MaxValue;
                        inWord = false;
                    }
                    continue;
                }

                if (PdfiumNativeBridge.FPDFText_GetCharBox(textPage, i, out double l, out double r, out double b, out double t) != 0)
                {
                    currentWord.Append(ch);
                    wordLeft = Math.Min(wordLeft, l);
                    wordTop = Math.Max(wordTop, t);
                    wordRight = Math.Max(wordRight, r);
                    wordBottom = Math.Min(wordBottom, b);
                    inWord = true;
                }
            }

            if (inWord && currentWord.Length > 0 && wordLeft != double.MaxValue)
            {
                AddSegment(list, pageNumber, currentWord.ToString(), wordLeft, wordTop, wordRight, wordBottom, pageWidth, pageHeight);
            }

            // Sort in standard reading order (top-to-bottom, left-to-right)
            list.Sort((a, b) =>
            {
                double yDiff = a.Y - b.Y;
                double threshold = Math.Min(a.Height, b.Height) * 0.5;
                if (threshold <= 0) threshold = 0.008;

                if (Math.Abs(yDiff) > threshold)
                {
                    return a.Y.CompareTo(b.Y);
                }
                return a.X.CompareTo(b.X);
            });

            for (int i = 0; i < list.Count; i++)
            {
                list[i].SegmentIndex = i;
            }

            return list;
        }
    }

    private static void AddSegment(List<PageTextSegment> list, int pageNum, string text, double left, double top, double right, double bottom, double pageWidth, double pageHeight)
    {
        double padY = 1.5;
        double padH = 2.5;
        double padX = 0.5;

        double normX = Math.Max(0, (left - padX) / pageWidth);
        double normY = Math.Max(0, 1.0 - ((top + padY) / pageHeight));
        double normW = Math.Max(0.001, ((right - left) + (padX * 2)) / pageWidth);
        double normH = Math.Max(0.001, ((top - bottom) + padH) / pageHeight);

        list.Add(new PageTextSegment
        {
            PageNumber = pageNum,
            Text = text,
            X = normX,
            Y = normY,
            Width = normW,
            Height = normH
        });
    }

    /// <summary>
    /// Asynchronously extracts all text segments from a given page.
    /// </summary>
    public async Task<List<PageTextSegment>> ExtractPageTextSegmentsAsync(int pageNumber, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ExtractPageTextSegments(pageNumber);
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
                if (_document == null || _document.IsInvalid)
                    throw new InvalidOperationException("No document is open.");

                Directory.CreateDirectory(outputDirectory);
                startPage = Math.Max(1, startPage);
                endPage = Math.Min(PageCount, endPage);
                int totalToExport = endPage - startPage + 1;

                string ext = format.Equals("JPEG", StringComparison.OrdinalIgnoreCase) || format.Equals("JPG", StringComparison.OrdinalIgnoreCase)
                    ? "jpg"
                    : "png";

                for (int p = startPage; p <= endPage; p++)
                {
                    ct.ThrowIfCancellationRequested();

                    var bitmap = RenderPage(p, dpi, 0);
                    if (bitmap != null)
                    {
                        string outPath = Path.Combine(outputDirectory, $"{fileNamePrefix}_page_{p:D3}.{ext}");
                        using var fileStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);

                        BitmapEncoder encoder = ext == "jpg"
                            ? new JpegBitmapEncoder { QualityLevel = 95 }
                            : new PngBitmapEncoder();

                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(fileStream);
                    }

                    double pct = (double)(p - startPage + 1) / totalToExport * 100.0;
                    progress?.Report(pct);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Prints the document pages using standard WPF PrintDialog.
    /// </summary>
    public void PrintDocument(PrintDialog printDialog, int fromPage = 1, int toPage = -1, int rotationAngle = 0)
    {
        lock (_docLock)
        {
            if (_document == null || _document.IsInvalid) return;

            int totalPages = PageCount;
            int start = Math.Max(1, fromPage);
            int end = toPage < 1 ? totalPages : Math.Min(totalPages, toPage);

            var docPaginator = new PdfiumPdfPaginator(this, start, end, printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight, rotationAngle);
            printDialog.PrintDocument(docPaginator, $"Printing {Path.GetFileName(_currentFilePath)}");
        }
    }

    #region Annotations & Multi-Mode Saving

    /// <summary>
    /// Loads existing annotations from the current document.
    /// </summary>
    public List<AnnotationModel> LoadExistingAnnotations()
    {
        var list = new List<AnnotationModel>();
        lock (_docLock)
        {
            if (_document == null || _document.IsInvalid) return list;

            int pageCount = PageCount;
            for (int p = 1; p <= pageCount; p++)
            {
                using var page = PdfiumNativeBridge.FPDF_LoadPage(_document, p - 1);
                if (page == null || page.IsInvalid) continue;

                if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_document, p - 1, out var size) == 0) continue;
                // FPDFAnnot_GetRect is in unrotated page space, same as char boxes.
                GetUnrotatedPageSize(page, size, out double pageWidth, out double pageHeight);

                int annotCount = PdfiumNativeBridge.FPDFPage_GetAnnotCount(page);
                for (int i = 0; i < annotCount; i++)
                {
                    using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(page, i);
                    if (annot == null || annot.IsInvalid) continue;

                    int subtype = PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot);
                    AnnotationType type;

                    switch (subtype)
                    {
                        case PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT:
                            type = AnnotationType.Highlight;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_UNDERLINE:
                            type = AnnotationType.Underline;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_STRIKEOUT:
                            type = AnnotationType.StrikeOut;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_TEXT:
                            type = AnnotationType.Note;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_FREETEXT:
                            type = AnnotationType.FreeText;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_SQUARE:
                            type = AnnotationType.Rectangle;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_CIRCLE:
                            type = AnnotationType.Ellipse;
                            break;
                        case PdfiumNativeBridge.FPDF_ANNOT_INK:
                            type = AnnotationType.Ink;
                            break;
                        default:
                            continue; // Skip link, popup, widget and non-markup annotations
                    }

                    if (PdfiumNativeBridge.FPDFAnnot_GetRect(annot, out var rect) == 0) continue;

                    double normX = Math.Max(0, rect.left / pageWidth);
                    double normY = Math.Max(0, 1.0 - (rect.top / pageHeight));
                    double normW = Math.Max(0.01, (rect.right - rect.left) / pageWidth);
                    double normH = Math.Max(0.01, (rect.top - rect.bottom) / pageHeight);

                    string colorHex = "#FF32CD32";
                    double opacity = (type == AnnotationType.Highlight ? 0.4 : 1.0);

                    if (PdfiumNativeBridge.FPDFAnnot_GetColor(annot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_Color, out uint r, out uint g, out uint b, out uint a) != 0)
                    {
                        colorHex = $"#{r:X2}{g:X2}{b:X2}";
                        if (a > 0 && a <= 255)
                        {
                            opacity = a / 255.0;
                        }
                    }

                    string contents = ReadAnnotString(annot, "Contents");
                    string author = ReadAnnotString(annot, "T");
                    if (string.IsNullOrEmpty(author)) author = "Author";
                    string title = ReadAnnotString(annot, "Subj");
                    if (string.IsNullOrEmpty(title)) title = type.ToString();

                    var model = new AnnotationModel
                    {
                        PageNumber = p,
                        Type = type,
                        X = normX,
                        Y = normY,
                        Width = normW,
                        Height = normH,
                        ColorHex = colorHex,
                        Opacity = opacity,
                        Contents = contents,
                        Author = author,
                        Title = title
                    };

                    // Load ink points if Ink annotation
                    if (type == AnnotationType.Ink)
                    {
                        int pathCount = PdfiumNativeBridge.FPDFAnnot_GetInkListCount(annot);
                        if (pathCount > 0)
                        {
                            int ptCount = PdfiumNativeBridge.FPDFAnnot_GetInkListPath(annot, 0, null, 0);
                            if (ptCount > 0)
                            {
                                var ptBuf = new FS_POINTF[ptCount];
                                if (PdfiumNativeBridge.FPDFAnnot_GetInkListPath(annot, 0, ptBuf, ptCount) > 0)
                                {
                                    model.InkPoints = ptBuf
                                        .Select(pt => new Point(Math.Max(0, pt.x / pageWidth), Math.Max(0, 1.0 - (pt.y / pageHeight))))
                                        .ToList();
                                }
                            }
                        }
                    }

                    list.Add(model);
                }
            }
        }
        return list;
    }

    private static string ReadAnnotString(SafeAnnotHandle annot, string key)
    {
        uint len = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (len <= 2) return string.Empty;

        byte[] buf = new byte[len];
        PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, key, buf, len);
        return PdfiumNativeBridge.Utf16BytesToString(buf, (int)len);
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
            ct.ThrowIfCancellationRequested();

            if (mode == AnnotationSaveMode.ExportXfdf)
            {
                // Gather page dimensions so the export can emit real PDF points.
                var pageSizes = new Dictionary<int, (double Width, double Height)>();
                lock (_docLock)
                {
                    if (_document != null && !_document.IsInvalid)
                    {
                        foreach (int pageNumber in annotations.Select(a => a.PageNumber).Distinct())
                        {
                            using var pg = PdfiumNativeBridge.FPDF_LoadPage(_document, pageNumber - 1);
                            if (pg == null || pg.IsInvalid) continue;
                            if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_document, pageNumber - 1, out var sz) == 0) continue;

                            GetUnrotatedPageSize(pg, sz, out double w, out double h);
                            pageSizes[pageNumber] = (w, h);
                        }
                    }
                }

                ExportAnnotationsToXfdf(targetPath, annotations, pageSizes);
                return;
            }

            lock (_docLock)
            {
                if (_document == null || _document.IsInvalid || _fileBytes == null)
                    throw new InvalidOperationException("No document is currently loaded.");

                // Load a fresh document clone from the in-memory bytes
                IntPtr saveBuffer = Marshal.AllocHGlobal(_fileBytes.Length);
                try
                {
                    Marshal.Copy(_fileBytes, 0, saveBuffer, _fileBytes.Length);
                    using var saveDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(saveBuffer, _fileBytes.Length, null);
                    if (saveDoc == null || saveDoc.IsInvalid)
                        throw new InvalidOperationException("Failed to instantiate document copy for saving.");

                // Apply annotations
                foreach (var annot in annotations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (annot.PageNumber < 1 || annot.PageNumber > PdfiumNativeBridge.FPDF_GetPageCount(saveDoc)) continue;

                    using var page = PdfiumNativeBridge.FPDF_LoadPage(saveDoc, annot.PageNumber - 1);
                    if (page == null || page.IsInvalid) continue;

                    if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(saveDoc, annot.PageNumber - 1, out var size) == 0) continue;
                    double pageWidth = size.width > 0 ? size.width : 612;
                    double pageHeight = size.height > 0 ? size.height : 792;

                    int subtype = annot.Type switch
                    {
                        AnnotationType.Highlight => PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT,
                        AnnotationType.Underline => PdfiumNativeBridge.FPDF_ANNOT_UNDERLINE,
                        AnnotationType.StrikeOut => PdfiumNativeBridge.FPDF_ANNOT_STRIKEOUT,
                        AnnotationType.Note => PdfiumNativeBridge.FPDF_ANNOT_TEXT,
                        AnnotationType.FreeText => PdfiumNativeBridge.FPDF_ANNOT_FREETEXT,
                        AnnotationType.Rectangle => PdfiumNativeBridge.FPDF_ANNOT_SQUARE,
                        AnnotationType.Ellipse => PdfiumNativeBridge.FPDF_ANNOT_CIRCLE,
                        AnnotationType.Ink => PdfiumNativeBridge.FPDF_ANNOT_INK,
                        _ => PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT
                    };

                    using var nativeAnnot = PdfiumNativeBridge.FPDFPage_CreateAnnot(page, subtype);
                    if (nativeAnnot == null || nativeAnnot.IsInvalid) continue;

                    float left = (float)(annot.X * pageWidth);
                    float bottom = (float)((1.0 - (annot.Y + annot.Height)) * pageHeight);
                    float right = (float)((annot.X + annot.Width) * pageWidth);
                    float top = (float)((1.0 - annot.Y) * pageHeight);

                    var rect = new FS_RECTF { left = left, bottom = bottom, right = right, top = top };
                    PdfiumNativeBridge.FPDFAnnot_SetRect(nativeAnnot, ref rect);

                    // Parse color
                    ParseHexColor(annot.ColorHex, out uint r, out uint g, out uint b);
                    uint alpha = (uint)Math.Clamp((int)(annot.Opacity * 255), 0, 255);
                    PdfiumNativeBridge.FPDFAnnot_SetColor(nativeAnnot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_Color, r, g, b, alpha);

                    if (!string.IsNullOrEmpty(annot.Contents))
                    {
                        PdfiumNativeBridge.FPDFAnnot_SetStringValue(nativeAnnot, "Contents", PdfiumNativeBridge.StringToUtf16NullTerminated(annot.Contents));
                    }
                    if (!string.IsNullOrEmpty(annot.Author))
                    {
                        PdfiumNativeBridge.FPDFAnnot_SetStringValue(nativeAnnot, "T", PdfiumNativeBridge.StringToUtf16NullTerminated(annot.Author));
                    }
                    if (!string.IsNullOrEmpty(annot.Title))
                    {
                        PdfiumNativeBridge.FPDFAnnot_SetStringValue(nativeAnnot, "Subj", PdfiumNativeBridge.StringToUtf16NullTerminated(annot.Title));
                    }

                    // Add ink stroke points if Ink
                    if (annot.Type == AnnotationType.Ink && annot.InkPoints != null && annot.InkPoints.Count > 1)
                    {
                        var pts = annot.InkPoints
                            .Select(p => new FS_POINTF { x = (float)(p.X * pageWidth), y = (float)((1.0 - p.Y) * pageHeight) })
                            .ToArray();
                        PdfiumNativeBridge.FPDFAnnot_AddInkStroke(nativeAnnot, pts, pts.Length);
                    }

                    PdfiumNativeBridge.FPDFPage_GenerateContent(page);
                }

                if (mode == AnnotationSaveMode.Flattened)
                {
                    int total = PdfiumNativeBridge.FPDF_GetPageCount(saveDoc);
                    for (int p = 0; p < total; p++)
                    {
                        using var page = PdfiumNativeBridge.FPDF_LoadPage(saveDoc, p);
                        if (page != null && !page.IsInvalid)
                        {
                            PdfiumNativeBridge.FPDFPage_Flatten(page, PdfiumNativeBridge.FLAT_NORMALDISPLAY);
                        }
                    }
                }

                string? outDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

                using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Exception? writeFailure = null;
                var writer = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        // Never let a managed exception (disk full, share dropped, AV lock)
                        // unwind through PDFium's C++ frames: it is built without exception
                        // support, so destructors are skipped and native state is corrupted.
                        try
                        {
                            byte[] buffer = new byte[size];
                            Marshal.Copy(pData, buffer, 0, (int)size);
                            fileStream.Write(buffer, 0, (int)size);
                            return 1;
                        }
                        catch (Exception ex)
                        {
                            writeFailure ??= ex;
                            return 0;
                        }
                    }
                };

                int success = PdfiumNativeBridge.FPDF_SaveAsCopy(saveDoc, ref writer, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
                GC.KeepAlive(writer);

                if (writeFailure != null)
                {
                    throw new IOException("Failed writing the saved PDF document to disk.", writeFailure);
                }

                if (success == 0)
                {
                    throw new InvalidOperationException("PDFium failed to write the saved PDF document.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(saveBuffer);
            }
        }
    }, ct);
    }

    private static void ParseHexColor(string? hex, out uint r, out uint g, out uint b)
    {
        r = 50; g = 205; b = 50; // default lime green
        if (string.IsNullOrWhiteSpace(hex)) return;

        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
            else if (hex.Length == 8)
            {
                r = Convert.ToByte(hex.Substring(2, 2), 16);
                g = Convert.ToByte(hex.Substring(4, 2), 16);
                b = Convert.ToByte(hex.Substring(6, 2), 16);
            }
        }
        catch { }
    }

    /// <summary>
    /// Exports annotations to an XFDF XML comments file.
    /// </summary>
    /// <param name="pageSizes">
    /// Page number -> (width, height) in PDF points. Required to emit spec-conformant
    /// coordinates; when a page is missing, US Letter is assumed.
    /// </param>
    public static void ExportAnnotationsToXfdf(
        string xfdfPath,
        IEnumerable<AnnotationModel> annotations,
        IReadOnlyDictionary<int, (double Width, double Height)>? pageSizes = null)
    {
        string? outDir = Path.GetDirectoryName(xfdfPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        using var writer = new StreamWriter(xfdfPath, false, Encoding.UTF8);
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

            // XFDF rect is PDF user-space points with a BOTTOM-LEFT origin
            // (left,bottom,right,top). The annotation model stores normalized 0..1 top-down
            // values; writing those raw collapsed every annotation into a sub-point speck at
            // the page origin in Acrobat. The color attribute must also be #RRGGBB, whereas
            // ColorHex is #AARRGGBB.
            double pw = 612, ph = 792;
            if (pageSizes != null && pageSizes.TryGetValue(a.PageNumber, out var ps))
            {
                pw = ps.Width;
                ph = ps.Height;
            }

            double left = a.X * pw;
            double right = (a.X + a.Width) * pw;
            double bottom = (1.0 - a.Y - a.Height) * ph;
            double top = (1.0 - a.Y) * ph;

            string rect = string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4},{3:F4}", left, bottom, right, top);
            string colorAttr = ToXfdfRgbHex(a.ColorHex);

            writer.Write($"    <{annotTag} page=\"{a.PageNumber - 1}\" rect=\"{rect}\" color=\"{colorAttr}\" title=\"{System.Security.SecurityElement.Escape(a.Author ?? string.Empty)}\" date=\"D:{dateStr}\"");

            // Markup annotations are drawn from quadpoints; without coords most viewers
            // render nothing for highlight/underline/strikeout.
            if (a.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut)
            {
                string coords = string.Format(CultureInfo.InvariantCulture,
                    "{0:F4},{1:F4},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4}",
                    left, top, right, top, left, bottom, right, bottom);
                writer.Write($" coords=\"{coords}\"");
            }

            writer.WriteLine(">");
            if (!string.IsNullOrEmpty(a.Contents))
            {
                writer.WriteLine($"      <contents>{System.Security.SecurityElement.Escape(a.Contents)}</contents>");
            }
            writer.WriteLine($"    </{annotTag}>");
        }

        writer.WriteLine("  </annots>");
        writer.WriteLine("</xfdf>");
    }

    /// <summary>
    /// Converts the app's #AARRGGBB (or #RRGGBB) color to the #RRGGBB form XFDF requires.
    /// </summary>
    private static string ToXfdfRgbHex(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex)) return "#000000";
        string hex = colorHex.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex.Substring(2);
        if (hex.Length != 6) return "#000000";
        return "#" + hex.ToUpperInvariant();
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
