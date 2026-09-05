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
    private IntPtr _unmanagedBuffer;

    // Deliberately THE process-wide PDFium lock rather than a per-document lock.
    // PDFium's font cache, codec modules and render-device pool are global, so two
    // documents locking independently still corrupt each other. Using one lock also
    // removes any possibility of a lock-ordering deadlock between the document lock
    // and the native lock. Monitor is reentrant, so nesting is safe.
    private readonly object _syncLock = PdfiumNativeBridge.PdfiumLock;
    private readonly HashSet<PdfiumPage> _livePages = new();
    private readonly int _initialPageCount;
    private volatile bool _isDisposed;

    public string FilePath { get; }
    public DocumentMetadata Metadata { get; }

    /// <summary>
    /// Live page count. Queried from the native document on every access because
    /// page insert/delete mutates it; a value cached at open time goes stale and
    /// lets out-of-range indices slip past bounds checks.
    /// </summary>
    public int PageCount
    {
        get
        {
            if (_isDisposed) return _initialPageCount;
            lock (_syncLock)
            {
                if (_isDisposed || _handle.IsInvalid || _handle.IsClosed) return _initialPageCount;
                int live = PdfiumNativeBridge.FPDF_GetPageCount(_handle);
                return live >= 0 ? live : _initialPageCount;
            }
        }
    }

    public bool IsOpen => !_isDisposed && !_handle.IsInvalid && !_handle.IsClosed;
    public SafeDocumentHandle Handle => _handle;
    public object SyncLock => _syncLock;

    public PdfiumDocument(string filePath, SafeDocumentHandle handle, IntPtr unmanagedBuffer, DocumentMetadata metadata, int pageCount)
    {
        FilePath = filePath;
        _handle = handle;
        _unmanagedBuffer = unmanagedBuffer;
        Metadata = metadata;
        _initialPageCount = pageCount;
    }

    ~PdfiumDocument()
    {
        // Safety net: SafeDocumentHandle has its own finalizer, but the backing
        // buffer is plain unmanaged memory that would otherwise leak the entire
        // file for the life of the process if a caller forgets to dispose.
        FreeBuffer();
    }

    internal void RegisterPage(PdfiumPage page)
    {
        lock (_syncLock) { _livePages.Add(page); }
    }

    internal void UnregisterPage(PdfiumPage page)
    {
        lock (_syncLock) { _livePages.Remove(page); }
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
        // Taking the lock is what makes this safe against an in-flight render:
        // FPDF_CloseDocument must not run while another thread is inside a native
        // call on this document.
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // A page must be closed before the document that owns it, otherwise
            // FPDF_ClosePage runs against a freed CPDF_Document.
            foreach (var page in _livePages.ToArray())
            {
                page.CloseHandleOnly();
            }
            _livePages.Clear();

            _handle?.Dispose();
            FreeBuffer();
        }

        GC.SuppressFinalize(this);
    }

    private void FreeBuffer()
    {
        IntPtr buffer = Interlocked.Exchange(ref _unmanagedBuffer, IntPtr.Zero);
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
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
        _document.RegisterPage(this);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _handle?.Dispose();
        _document.UnregisterPage(this);
    }

    /// <summary>
    /// Closes the native page handle without touching the owning document's page
    /// registry. Called by PdfiumDocument.Dispose, which already holds the lock and
    /// is clearing the registry itself.
    /// </summary>
    internal void CloseHandleOnly()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _handle?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
