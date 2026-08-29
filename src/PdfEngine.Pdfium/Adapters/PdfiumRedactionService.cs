using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, List<RedactionArea>> _pendingRedactions = new();

    public ValueTask<IReadOnlyList<RedactionArea>> GetPendingRedactionsAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        if (_pendingRedactions.TryGetValue(document.FilePath, out var list))
        {
            return ValueTask.FromResult<IReadOnlyList<RedactionArea>>(list.ToList());
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

        var list = _pendingRedactions.GetOrAdd(document.FilePath, _ => new List<RedactionArea>());
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

            using var editDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, null);
            if (editDoc == null || editDoc.IsInvalid)
            {
                Marshal.FreeHGlobal(unmanagedBuf);
                throw new PdfSaveException("Failed to open working copy for redaction.", targetPath);
            }

            try
            {
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
                    // Permanently flatten so underlying vectors and text are baked
                    PdfiumNativeBridge.FPDFPage_Flatten(pageHandle, PdfiumNativeBridge.FLAT_NORMALDISPLAY);
                }

                // Save out to file
                using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        byte[] buffer = new byte[size];
                        Marshal.Copy(pData, buffer, 0, (int)size);
                        outStream.Write(buffer, 0, (int)size);
                        return 1;
                    }
                };

                int saveResult = PdfiumNativeBridge.FPDF_SaveAsCopy(editDoc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL | PdfiumNativeBridge.FPDF_REMOVE_SECURITY);
                if (saveResult == 0)
                {
                    throw new PdfSaveException("Native FPDF_SaveAsCopy failed to save redacted document.", targetPath);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedBuf);
                _pendingRedactions.TryRemove(pdfiumDoc.FilePath, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
