using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using Xunit;

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

    /// <summary>A text field whose appearance shows "FILLED" in large black type, and a checked box.</summary>
    private string FormPdf()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int ap = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 180 40] /Resources << /Font << /Helv {helv} 0 R >> >>",
            "/Tx BMC q BT /Helv 30 Tf 0 g 4 8 Td (FILLED) Tj ET Q EMC");
        int check = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "q 0 g 0 0 20 20 re f Q");
        int off = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "");
        int page = b.AddPage("", "<< >>", mediaBox: "[0 0 200 200]", extra: "/Annots [PAGE_ANNOTS]");
        int text = page + 1, box = page + 2, acro = page + 3;
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (FILLED) /Rect [10 140 190 180] /P {page} 0 R /DA (/Helv 30 Tf 0 g) /AP << /N {ap} 0 R >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T (ok) /V /Yes /AS /Yes /Rect [10 100 30 120] /P {page} 0 R /AP << /N << /Yes {check} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /Fields [{text} 0 R {box} 0 R] /DR << /Font << /Helv {helv} 0 R >> >> /DA (/Helv 0 Tf 0 g) >>");
        b.WithCatalog($"/AcroForm {acro} 0 R");
        string pdf = System.Text.Encoding.Latin1.GetString(b.Build()).Replace("/Annots [PAGE_ANNOTS]", $"/Annots [{text} 0 R {box} 0 R]");
        // Offsets moved: let the loader rebuild the table (PDFium and this viewer both repair).
        string path = Path.Combine(_dir, "form.pdf");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(pdf));
        return path;
    }

    private static int DarkPixels(BitmapSource bmp, int x0, int y0, int x1, int y1)
    {
        int stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);
        int dark = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (px[y * stride + x * 4 + 1] < 100) dark++;
        return dark;
    }

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
