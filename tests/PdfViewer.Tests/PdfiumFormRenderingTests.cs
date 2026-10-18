using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfViewer.Services;
using Xunit;

// PDFium-only (PdfiumOnlyTest): it tests the PDFium adapter's form-fill environment, which the
// PDFium-free build does not have. NativeFormRenderingTests checks the same fields on vector-drawn pages.

namespace PdfViewer.Tests;

/// <summary>
/// PDFium draws form fields only through FPDF_FFLDraw (FPDF_ANNOT leaves widgets out), which needs
/// the form-fill environment. With it, fields show on PDFium-rendered pages; and the environment's
/// lifetime is right (the struct in native memory for the handle's life), so opening, rendering
/// and closing many times is stable.
/// </summary>
public class PdfiumFormRenderingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PdfiumFormRendering_" + Guid.NewGuid().ToString("N"));

    public PdfiumFormRenderingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string FormPdf() => NativeFormRenderingTests.FormPdf(_dir);

    private static int DarkPixels(BitmapSource bmp, int x0, int y0, int x1, int y1) => NativeFormRenderingTests.DarkPixels(bmp, x0, y0, x1, y1);

    [Fact]
    public async Task PdfiumRenders_ShowFormFields()
    {
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(FormPdf());
        var bmp = service.RenderPage(1, 72)!;
        // Page 200 pt at 72 dpi: the text field spans rows 20..60, the box rows 80..100.
        Assert.True(DarkPixels(bmp, 12, 22, 188, 58) > 150, "the text field's value is drawn");
        Assert.True(DarkPixels(bmp, 11, 81, 29, 99) > 200, "the checked box is drawn");
        var region = service.RenderPageRegion(1, 144, 0, 20, 40, 300, 80)!;
        Assert.True(DarkPixels(region, 0, 0, region.PixelWidth, region.PixelHeight) > 300, "region renders draw fields too");
    }

    [Fact]
    public async Task OpeningRenderingAndClosing_ManyTimes_IsStable()
    {
        string path = FormPdf();
        for (int i = 0; i < 60; i++)
        {
            using var service = new PdfiumDocumentService();
            await service.OpenDocumentAsync(path);
            Assert.NotNull(service.RenderPage(1, 36));
            service.CloseDocument();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
