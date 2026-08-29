using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pdfium.Native;
using PdfEngine.Rendering;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// High-performance UI-neutral page renderer using PDFium native BGRA bitmap buffer rasterization.
/// </summary>
public sealed class PdfiumRenderer : IPdfRenderer
{
    public ValueTask<RenderedPage> RenderPageAsync(
        IPdfDocument document,
        RenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(RenderInternal(pdfiumDoc, request, cancellationToken));
    }

    private static RenderedPage RenderInternal(
        PdfiumDocument document,
        RenderRequest request,
        CancellationToken cancellationToken)
    {
        lock (document.SyncLock)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(document.Handle, request.PageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid)
                throw new PdfCorruptDocumentException($"Failed to load page {request.PageNumber} for rendering.");

            float origW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
            float origH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);
            int nativeRotation = PdfiumNativeBridge.FPDFPage_GetRotation(pageHandle);

            int combinedRotation = ((int)request.Rotation / 90 + nativeRotation) % 4;

            int pixelW = request.TargetWidthPixels;
            int pixelH = request.TargetHeightPixels;

            if (pixelW <= 0 || pixelH <= 0)
            {
                double scale = request.Dpi / 72.0;
                if (combinedRotation == 1 || combinedRotation == 3)
                {
                    pixelW = (int)Math.Round(origH * scale);
                    pixelH = (int)Math.Round(origW * scale);
                }
                else
                {
                    pixelW = (int)Math.Round(origW * scale);
                    pixelH = (int)Math.Round(origH * scale);
                }
            }

            pixelW = Math.Max(1, pixelW);
            pixelH = Math.Max(1, pixelH);

            int stride = pixelW * 4;
            int bufferSize = stride * pixelH;

            var memoryOwner = new NativeMemoryOwner(bufferSize);
            IntPtr fpdfBitmap = IntPtr.Zero;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                fpdfBitmap = PdfiumNativeBridge.FPDFBitmap_CreateEx(
                    pixelW,
                    pixelH,
                    PdfiumNativeBridge.FPDFBitmap_BGRA,
                    memoryOwner.Pointer,
                    stride);

                if (fpdfBitmap == IntPtr.Zero)
                    throw new PdfException("Failed to allocate native FPDFBitmap.");

                // Clear background to white 0xFFFFFFFF
                PdfiumNativeBridge.FPDFBitmap_FillRect(fpdfBitmap, 0, 0, pixelW, pixelH, 0xFFFFFFFF);

                int renderFlags = PdfiumNativeBridge.FPDF_LCD_TEXT;
                if (request.RenderAnnotations) renderFlags |= PdfiumNativeBridge.FPDF_ANNOT;
                if (request.HighQuality) renderFlags |= PdfiumNativeBridge.FPDF_PRINTING;

                cancellationToken.ThrowIfCancellationRequested();

                PdfiumNativeBridge.FPDF_RenderPageBitmap(
                    fpdfBitmap,
                    pageHandle,
                    0,
                    0,
                    pixelW,
                    pixelH,
                    combinedRotation,
                    renderFlags);

                return new RenderedPage(
                    request.PageNumber,
                    pixelW,
                    pixelH,
                    stride,
                    request.Dpi,
                    request.Rotation,
                    memoryOwner);
            }
            catch
            {
                memoryOwner.Dispose();
                throw;
            }
            finally
            {
                if (fpdfBitmap != IntPtr.Zero)
                {
                    PdfiumNativeBridge.FPDFBitmap_Destroy(fpdfBitmap);
                }
            }
        }
    }
}
