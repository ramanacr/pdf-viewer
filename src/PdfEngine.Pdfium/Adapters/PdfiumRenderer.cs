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
    /// <summary>
    /// Upper bound on a single page raster (bytes). Guards against int overflow in the
    /// stride/buffer math and against a caller-supplied DPI producing an unallocatable size.
    /// </summary>
    private const long MaxBufferBytes = 2_000_000_000L;

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

            // FPDF_GetPageWidthF/HeightF already return ROTATION-ADJUSTED dimensions, and
            // FPDF_RenderPageBitmap's rotate argument is applied ON TOP OF the page's own
            // /Rotate. Adding the page's native rotation here therefore applied it twice,
            // rendering any /Rotate 90 page sideways into a transposed bitmap.
            float displayW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
            float displayH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

            int userRotation = ((int)request.Rotation / 90) % 4;
            if (userRotation < 0) userRotation += 4;

            int pixelW = request.TargetWidthPixels;
            int pixelH = request.TargetHeightPixels;

            if (pixelW <= 0 || pixelH <= 0)
            {
                double scale = request.Dpi / 72.0;
                if (userRotation == 1 || userRotation == 3)
                {
                    pixelW = (int)Math.Round(displayH * scale);
                    pixelH = (int)Math.Round(displayW * scale);
                }
                else
                {
                    pixelW = (int)Math.Round(displayW * scale);
                    pixelH = (int)Math.Round(displayH * scale);
                }
            }

            pixelW = Math.Max(1, pixelW);
            pixelH = Math.Max(1, pixelH);

            // Compute in long: a large page at a high export DPI (e.g. an E-size drawing at
            // 600 DPI) overflows int, which either throws far from the cause or - worse -
            // wraps to a small positive size and hands PDFium a buffer far smaller than the
            // bitmap dimensions it will write.
            long strideL = (long)pixelW * 4;
            long bufferSizeL = strideL * pixelH;
            if (bufferSizeL > MaxBufferBytes)
            {
                throw new PdfException(
                    $"Requested render of {pixelW}x{pixelH} needs {bufferSizeL:N0} bytes, exceeding the " +
                    $"{MaxBufferBytes:N0} byte limit. Reduce the DPI or target pixel size.");
            }

            int stride = (int)strideL;
            int bufferSize = (int)bufferSizeL;

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
                // Form fields are widget annotations, so FPDF_ANNOT draws their appearance
                // streams. RenderForms was previously accepted and then silently ignored;
                // honouring it here means a caller asking only for forms still gets them.
                // Limitation: fields with no appearance stream need a full form-fill
                // environment (FPDFDOC_InitFormFillEnvironment + FPDF_FFLDraw), which this
                // renderer does not create.
                if (request.RenderAnnotations || request.RenderForms) renderFlags |= PdfiumNativeBridge.FPDF_ANNOT;
                if (request.HighQuality) renderFlags |= PdfiumNativeBridge.FPDF_PRINTING;

                cancellationToken.ThrowIfCancellationRequested();

                PdfiumNativeBridge.FPDF_RenderPageBitmap(
                    fpdfBitmap,
                    pageHandle,
                    0,
                    0,
                    pixelW,
                    pixelH,
                    userRotation,
                    renderFlags);

                // Report the DPI the raster was ACTUALLY produced at. When the caller drives
                // size via TargetWidth/HeightPixels, request.Dpi describes nothing, and a
                // consumer using it for BitmapSource.Create renders at the wrong physical size.
                double effectiveDpi = request.Dpi;
                double sourcePoints = (userRotation == 1 || userRotation == 3) ? displayH : displayW;
                if (sourcePoints > 0)
                {
                    effectiveDpi = pixelW / sourcePoints * 72.0;
                }

                return new RenderedPage(
                    request.PageNumber,
                    pixelW,
                    pixelH,
                    stride,
                    effectiveDpi,
                    request.Rotation,
                    memoryOwner);
            }
            catch
            {
                // Destroy the FPDF bitmap BEFORE releasing the buffer it was created over,
                // so PDFium never holds a bitmap pointing at freed memory.
                if (fpdfBitmap != IntPtr.Zero)
                {
                    PdfiumNativeBridge.FPDFBitmap_Destroy(fpdfBitmap);
                    fpdfBitmap = IntPtr.Zero;
                }
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
