using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pdfium.Native;
using PdfEngine.Redaction;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Permanent, irreversible PDF content redaction service with vector flattening and blackout rendering.
/// </summary>
public sealed class PdfiumRedactionService : IPdfRedactionService
{
    // Keyed on document IDENTITY, not FilePath. FilePath is string.Empty for documents
    // opened from bytes, so every in-memory document shared one bucket and applying
    // redactions to one wiped another's. A ConditionalWeakTable also drops the entry
    // automatically when the document is collected, where the old dictionary leaked
    // every document's redactions for the process lifetime.
    private readonly ConditionalWeakTable<IPdfDocument, List<RedactionArea>> _pendingRedactions = new();

    public ValueTask<IReadOnlyList<RedactionArea>> GetPendingRedactionsAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        if (_pendingRedactions.TryGetValue(document, out var list))
        {
            // Copy under the same lock the writer uses; enumerating without it threw
            // InvalidOperationException if an add landed mid-copy.
            lock (list)
            {
                return ValueTask.FromResult<IReadOnlyList<RedactionArea>>(list.ToList());
            }
        }
        return ValueTask.FromResult<IReadOnlyList<RedactionArea>>(Array.Empty<RedactionArea>());
    }

    public ValueTask AddPendingRedactionAsync(
        IPdfDocument document,
        RedactionArea redaction,
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (redaction == null) throw new ArgumentNullException(nameof(redaction));

        var list = _pendingRedactions.GetValue(document, _ => new List<RedactionArea>());
        lock (list)
        {
            list.Add(redaction);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ApplyRedactionsAsync(
        IPdfDocument document,
        string targetPath,
        IReadOnlyList<RedactionArea> redactions,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(pdfiumDoc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException("Target path cannot overwrite currently open document directly.", targetPath);

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            byte[] fileBytes = File.ReadAllBytes(pdfiumDoc.FilePath);
            IntPtr unmanagedBuf = Marshal.AllocHGlobal(fileBytes.Length);
            Marshal.Copy(fileBytes, 0, unmanagedBuf, fileBytes.Length);

            // NOTE: editDoc is declared INSIDE the try so that FPDF_CloseDocument runs before
            // the finally frees unmanagedBuf. FPDF_LoadMemDocument does not copy the buffer -
            // PDFium parses lazily out of it for the document's whole lifetime - so freeing
            // first left FPDF_CloseDocument walking freed heap.
            SafeDocumentHandle? editDoc = null;
            try
            {
                editDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, null);
                if (editDoc == null || editDoc.IsInvalid)
                {
                    throw new PdfSaveException(
                        $"Failed to open working copy for redaction (PDFium error {PdfiumNativeBridge.FPDF_GetLastError()}).",
                        targetPath);
                }

                var grouped = redactions.GroupBy(r => r.PageNumber);
                foreach (var grp in grouped)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageIndex = grp.Key - 1;
                    if (pageIndex < 0 || pageIndex >= pdfiumDoc.PageCount) continue;

                    using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(editDoc, pageIndex);
                    if (pageHandle == null || pageHandle.IsInvalid) continue;

                    float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
                    float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

                    // Build the target rectangles in PDF user space (bottom-left origin).
                    var targetRects = new List<FS_RECTF>();
                    foreach (var redaction in grp)
                    {
                        targetRects.Add(new FS_RECTF
                        {
                            left = (float)(redaction.Bounds.X * pageW),
                            right = (float)((redaction.Bounds.X + redaction.Bounds.Width) * pageW),
                            bottom = (float)((1.0 - redaction.Bounds.Y - redaction.Bounds.Height) * pageH),
                            top = (float)((1.0 - redaction.Bounds.Y) * pageH)
                        });
                    }

                    // STEP 1 - actually remove the covered content. Drawing a black box and
                    // flattening only paints OVER the text: the original glyphs survive in the
                    // content stream and come straight back out of any text extraction or
                    // copy-paste. Real redaction has to delete the page objects.
                    RemoveObjectsIntersecting(pageHandle, targetRects);

                    // STEP 2 - draw the opaque blackout box over the now-empty region.
                    foreach (var redaction in grp)
                    {
                        using var annot = PdfiumNativeBridge.FPDFPage_CreateAnnot(pageHandle, PdfiumNativeBridge.FPDF_ANNOT_SQUARE);
                        if (annot == null || annot.IsInvalid) continue;

                        var rect = new FS_RECTF
                        {
                            left = (float)(redaction.Bounds.X * pageW),
                            right = (float)((redaction.Bounds.X + redaction.Bounds.Width) * pageW),
                            bottom = (float)((1.0 - redaction.Bounds.Y - redaction.Bounds.Height) * pageH),
                            top = (float)((1.0 - redaction.Bounds.Y) * pageH)
                        };
                        PdfiumNativeBridge.FPDFAnnot_SetRect(annot, ref rect);

                        // Set solid black fill and stroke
                        PdfiumNativeBridge.FPDFAnnot_SetColor(annot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_Color, 0, 0, 0, 255);
                        PdfiumNativeBridge.FPDFAnnot_SetColor(annot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_InteriorColor, 0, 0, 0, 255);

                        if (!string.IsNullOrEmpty(redaction.OverlayText))
                        {
                            byte[] overlayBytes = PdfiumNativeBridge.StringToUtf16NullTerminated(redaction.OverlayText);
                            PdfiumNativeBridge.FPDFAnnot_SetStringValue(annot, "Contents", overlayBytes);
                        }
                    }

                    PdfiumNativeBridge.FPDFPage_GenerateContent(pageHandle);
                    // Flatten the blackout annotation into the content stream so it cannot be
                    // deleted by a viewer. The content underneath is already gone (step 1).
                    PdfiumNativeBridge.FPDFPage_Flatten(pageHandle, PdfiumNativeBridge.FLAT_NORMALDISPLAY);
                }

                // Save out to file
                using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Exception? writeFailure = null;
                var fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        // Must never let a managed exception escape into PDFium's C++ frames:
                        // PDFium is built without exception support, so its stack is not
                        // unwound and destructors are skipped. Capture and signal failure
                        // by returning 0 instead, then rethrow once the native call returns.
                        try
                        {
                            byte[] buffer = new byte[size];
                            Marshal.Copy(pData, buffer, 0, (int)size);
                            outStream.Write(buffer, 0, (int)size);
                            return 1;
                        }
                        catch (Exception ex)
                        {
                            writeFailure ??= ex;
                            return 0;
                        }
                    }
                };

                // Only FPDF_NO_INCREMENTAL here. FPDF_REMOVE_SECURITY was previously passed
                // unconditionally, silently stripping the document's encryption and
                // permissions from every redacted output.
                int saveResult = PdfiumNativeBridge.FPDF_SaveAsCopy(editDoc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
                GC.KeepAlive(fileWrite);

                if (writeFailure != null)
                    throw new PdfSaveException("Failed writing redacted document to disk.", writeFailure, targetPath);

                if (saveResult == 0)
                {
                    throw new PdfSaveException("Native FPDF_SaveAsCopy failed to save redacted document.", targetPath);
                }

                // Only drop the queued redactions once the save actually succeeded, otherwise
                // a failed write both throws AND destroys the user's pending work.
                _pendingRedactions.Remove(pdfiumDoc);
            }
            finally
            {
                editDoc?.Dispose();
                Marshal.FreeHGlobal(unmanagedBuf);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Deletes every page object whose bounding box intersects any redaction rectangle.
    /// Iterates in reverse because removal reindexes the object list.
    /// </summary>
    private static void RemoveObjectsIntersecting(SafePageHandle pageHandle, List<FS_RECTF> targets)
    {
        if (targets.Count == 0) return;

        int objectCount = PdfiumNativeBridge.FPDFPage_CountObjects(pageHandle);
        for (int i = objectCount - 1; i >= 0; i--)
        {
            IntPtr obj = PdfiumNativeBridge.FPDFPage_GetObject(pageHandle, i);
            if (obj == IntPtr.Zero) continue;

            if (PdfiumNativeBridge.FPDFPageObj_GetBounds(obj, out float left, out float bottom, out float right, out float top) == 0)
                continue;

            bool intersects = false;
            foreach (var t in targets)
            {
                // Standard AABB overlap test in PDF user space.
                if (left < t.right && right > t.left && bottom < t.top && top > t.bottom)
                {
                    intersects = true;
                    break;
                }
            }

            if (!intersects) continue;

            if (PdfiumNativeBridge.FPDFPage_RemoveObject(pageHandle, obj) != 0)
            {
                // RemoveObject detaches but does not free; ownership transfers to the caller.
                PdfiumNativeBridge.FPDFPageObj_Destroy(obj);
            }
        }
    }
}
