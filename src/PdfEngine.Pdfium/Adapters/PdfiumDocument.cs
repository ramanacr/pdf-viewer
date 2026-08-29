using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pdfium.Native;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Thread-safe PDFium document wrapper implementing IPdfDocument.
/// </summary>
public sealed class PdfiumDocument : IPdfDocument
{
    private readonly SafeDocumentHandle _handle;
    private readonly IntPtr _unmanagedBuffer;
    private readonly object _syncLock = new();
    private bool _isDisposed;

    public string FilePath { get; }
    public DocumentMetadata Metadata { get; }
    public int PageCount { get; }
    public bool IsOpen => !_isDisposed && !_handle.IsInvalid && !_handle.IsClosed;
    public SafeDocumentHandle Handle => _handle;
    public object SyncLock => _syncLock;

    public PdfiumDocument(string filePath, SafeDocumentHandle handle, IntPtr unmanagedBuffer, DocumentMetadata metadata, int pageCount)
    {
        FilePath = filePath;
        _handle = handle;
        _unmanagedBuffer = unmanagedBuffer;
        Metadata = metadata;
        PageCount = pageCount;
    }

    public ValueTask<PageInfo> GetPageInfoAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(PdfiumDocument));
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page number {pageNumber} must be between 1 and {PageCount}.");

        lock (_syncLock)
        {
            int pageIndex = pageNumber - 1;
            if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(_handle, pageIndex, out var size) != 0)
            {
                using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(_handle, pageIndex);
                int rotation = 0;
                if (pageHandle != null && !pageHandle.IsInvalid)
                {
                    rotation = PdfiumNativeBridge.FPDFPage_GetRotation(pageHandle);
                }

                return ValueTask.FromResult(new PageInfo
                {
                    PageNumber = pageNumber,
                    WidthPoints = size.width,
                    HeightPoints = size.height,
                    RotationDegrees = rotation * 90
                });
            }

            return ValueTask.FromResult(new PageInfo
            {
                PageNumber = pageNumber,
                WidthPoints = 612,
                HeightPoints = 792,
                RotationDegrees = 0
            });
        }
    }

    public ValueTask<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(PdfiumDocument));

        lock (_syncLock)
        {
            var bookmarks = new List<BookmarkItem>();
            IntPtr first = PdfiumNativeBridge.FPDFBookmark_GetFirstChild(_handle, IntPtr.Zero);
            if (first != IntPtr.Zero)
            {
                ExtractBookmarkLevel(_handle, first, bookmarks);
            }
            return ValueTask.FromResult<IReadOnlyList<BookmarkItem>>(bookmarks);
        }
    }

    private static void ExtractBookmarkLevel(SafeDocumentHandle doc, IntPtr current, List<BookmarkItem> targetList)
    {
        while (current != IntPtr.Zero)
        {
            var item = new BookmarkItem();
            uint titleLen = PdfiumNativeBridge.FPDFBookmark_GetTitle(current, null, 0);
            if (titleLen > 0)
            {
                byte[] buf = new byte[titleLen];
                PdfiumNativeBridge.FPDFBookmark_GetTitle(current, buf, titleLen);
                item.Title = PdfiumNativeBridge.Utf16BytesToString(buf, (int)titleLen);
            }

            IntPtr dest = PdfiumNativeBridge.FPDFBookmark_GetDest(doc, current);
            if (dest == IntPtr.Zero)
            {
                IntPtr action = PdfiumNativeBridge.FPDFBookmark_GetAction(current);
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
                    item.TargetPageNumber = pageIdx + 1;
                }
            }

            targetList.Add(item);

            IntPtr child = PdfiumNativeBridge.FPDFBookmark_GetFirstChild(doc, current);
            if (child != IntPtr.Zero)
            {
                ExtractBookmarkLevel(doc, child, item.Children);
            }

            current = PdfiumNativeBridge.FPDFBookmark_GetNextSibling(doc, current);
        }
    }

    public ValueTask<IPdfPage> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(PdfiumDocument));
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        lock (_syncLock)
        {
            var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(_handle, pageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid)
                throw new PdfCorruptDocumentException($"Failed to load page {pageNumber}.");

            float w = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
            float h = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);
            int rot = PdfiumNativeBridge.FPDFPage_GetRotation(pageHandle);

            var info = new PageInfo
            {
                PageNumber = pageNumber,
                WidthPoints = w,
                HeightPoints = h,
                RotationDegrees = rot * 90
            };

            return ValueTask.FromResult<IPdfPage>(new PdfiumPage(this, pageHandle, info));
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _handle?.Dispose();
            if (_unmanagedBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_unmanagedBuffer);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class PdfiumPage : IPdfPage
{
    private readonly PdfiumDocument _document;
    private readonly SafePageHandle _handle;
    private bool _isDisposed;

    public int PageNumber => Info.PageNumber;
    public PageInfo Info { get; }
    public SafePageHandle Handle => _handle;

    public PdfiumPage(PdfiumDocument document, SafePageHandle handle, PageInfo info)
    {
        _document = document;
        _handle = handle;
        Info = info;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _handle?.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
