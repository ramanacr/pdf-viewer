using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pdfium.Native;
using PdfEngine.Rendering;
using PdfEngine.Save;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// PDF document saving, flattening, and high-DPI image export service.
/// </summary>
public sealed class PdfiumSaveService : IPdfSaveService
{
    private readonly IPdfRenderer _renderer;

    public PdfiumSaveService(IPdfRenderer renderer)
    {
        _renderer = renderer;
    }

    public ValueTask SaveAsync(
        IPdfDocument document,
        string targetPath,
        SaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(pdfiumDoc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException("Target path cannot overwrite currently open document directly.", targetPath);

        cancellationToken.ThrowIfCancellationRequested();
        options ??= new SaveOptions();

        lock (pdfiumDoc.SyncLock)
        {
            using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            Exception? writeFailure = null;
            var fileWrite = new FPDF_FILEWRITE
            {
                version = 1,
                WriteBlock = (pThis, pData, size) =>
                {
                    // Never unwind a managed exception through PDFium's C++ frames.
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

            // Always a full (non-incremental) write. FPDF_INCREMENTAL emits ONLY the
            // incremental update section, which is meaningless against the brand-new empty
            // file this method creates (FileMode.Create, and the target may not be the
            // source) - the result was a PDF with no base body that no reader could open.
            uint flags = PdfiumNativeBridge.FPDF_NO_INCREMENTAL;

            // NOTE: RemoveUnusedObjects deliberately does NOT map to FPDF_REMOVE_SECURITY.
            // That flag strips the document's encryption and permissions - nothing to do
            // with unused objects - so saving a password-protected PDF silently produced an
            // unencrypted copy. PDFium has no "remove unused objects" flag; a full rewrite
            // already drops unreferenced objects.

            int result = PdfiumNativeBridge.FPDF_SaveAsCopy(pdfiumDoc.Handle, ref fileWrite, flags);
            GC.KeepAlive(fileWrite);

            if (writeFailure != null)
                throw new PdfSaveException("Failed writing document to disk.", writeFailure, targetPath);

            if (result == 0)
            {
                throw new PdfSaveException("Native FPDF_SaveAsCopy failed to save document.", targetPath);
            }

            return ValueTask.CompletedTask;
        }
    }

    public async ValueTask ExportPagesToImagesAsync(
        IPdfDocument document,
        string outputDirectory,
        string filePrefix,
        int startPage,
        int endPage,
        string format = "png",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // PNG is the only encoder ImageEncoder implements. Previously any other value was
        // accepted and silently ignored, so a caller asking for "bmp" or "jpg" got a PNG
        // written under that assumption. Fail loudly instead of substituting.
        if (!string.IsNullOrWhiteSpace(format) &&
            !format.Trim().TrimStart('.').Equals("png", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Image export format '{format}' is not supported; only 'png' is available.");
        }

        startPage = Math.Clamp(startPage, 1, pdfiumDoc.PageCount);
        endPage = Math.Clamp(endPage, startPage, pdfiumDoc.PageCount);
        int totalToExport = endPage - startPage + 1;
        int completed = 0;

        for (int p = startPage; p <= endPage; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var req = new RenderRequest
            {
                PageNumber = p,
                Dpi = dpi,
                HighQuality = true
            };

            using var rendered = await _renderer.RenderPageAsync(pdfiumDoc, req, cancellationToken);
            string outPath = Path.Combine(outputDirectory, $"{filePrefix}_page_{p:D4}.png");

            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ImageEncoder.SaveAsPng(fs, rendered.WidthPixels, rendered.HeightPixels, rendered.Pixels.Span, rendered.Stride);
            }

            completed++;
            progress?.Report((double)completed / totalToExport);
        }
    }
}
