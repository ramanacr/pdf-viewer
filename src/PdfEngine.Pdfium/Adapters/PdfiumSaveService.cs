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

            uint flags = options.Mode switch
            {
                SaveMode.Incremental => PdfiumNativeBridge.FPDF_INCREMENTAL,
                _ => PdfiumNativeBridge.FPDF_NO_INCREMENTAL
            };

            if (options.RemoveUnusedObjects) flags |= PdfiumNativeBridge.FPDF_REMOVE_SECURITY;

            int result = PdfiumNativeBridge.FPDF_SaveAsCopy(pdfiumDoc.Handle, ref fileWrite, flags);
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
