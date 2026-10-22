using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfEngine.Vector;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Form fields show on the pages the viewer draws with the vector engine (Auto mode), whole pages
/// and zoomed regions alike: the counterpart of <c>PdfiumFormRenderingTests</c>, on the same document.
/// </summary>
public class NativeFormRenderingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NativeFormRendering_" + Guid.NewGuid().ToString("N"));

    public NativeFormRenderingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A text field whose appearance shows "FILLED" in large black type, and a checked box.</summary>
    internal static string FormPdf(string dir)
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
        string path = Path.Combine(dir, "form.pdf");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(pdf));
        return path;
    }

    internal static int DarkPixels(BitmapSource bmp, int x0, int y0, int x1, int y1)
    {
        if (bmp.Format != System.Windows.Media.PixelFormats.Bgra32 && bmp.Format != System.Windows.Media.PixelFormats.Pbgra32)
            bmp = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
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
    public async Task VectorRenders_ShowFormFields()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(FormPdf(_dir));
        Assert.True(service.IsVectorDocumentOpen);
        var bmp = service.RenderPage(1, 72)!;
        // Page 200 pt at 72 dpi: the text field spans rows 20..60, the box rows 80..100.
        Assert.True(DarkPixels(bmp, 12, 22, 188, 58) > 150, "the text field's value is drawn");
        Assert.True(DarkPixels(bmp, 11, 81, 29, 99) > 200, "the checked box is drawn");
        var region = (await service.RenderPageRegionAsync(1, 0, 2.0, 20, 40, 300, 80, nightMode: false))!;
        Assert.True(DarkPixels(region, 0, 0, region.PixelWidth, region.PixelHeight) > 300, "region renders draw fields too");
    }
}
