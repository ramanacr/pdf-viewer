using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The vector page host (backlog E8): pages the vector engine can show are displayed as a live,
/// resolution-independent drawing, so zooming never re-renders or stretches a page bitmap.
/// </summary>
public class VectorPageHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VectorPageHostTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static (PageViewModel Page, AsyncPageRenderer Renderer) PageFor(IPdfDocumentService service, int page = 1, double w = 612, double h = 792)
        => (new PageViewModel(page, w, h), new AsyncPageRenderer(service, new LruPageCache(8)));

    [Fact]
    public async Task VectorPage_IsShownAsLiveSurface_AndZoomDoesNotRebuildIt()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "live.pdf"), 1);
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        var (page, renderer) = PageFor(service);

        await page.LoadImageAsync(renderer, dpi: 150, rotation: 0);
        var surface = Assert.IsType<DrawingImage>(page.PageSurface);
        Assert.Null(page.RenderedImage);
        Assert.Equal(612, surface.Width, 1);
        Assert.Equal(792, surface.Height, 1);

        // Zooming in changes the requested DPI; the same surface must be kept.
        await page.LoadImageAsync(renderer, dpi: 300, rotation: 0);
        Assert.Same(surface, page.PageSurface);

        var report = service.GetPageEngineReport(1)!;
        Assert.Equal(PdfEngineMode.Vector, report.Engine);
        Assert.Contains("live vector surface", report.Tooltip);
    }

    [Fact]
    public async Task Rotation_ProducesARotatedSurface()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "rot.pdf"), 1);
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        var (page, renderer) = PageFor(service);

        await page.LoadImageAsync(renderer, 150, rotation: 90);
        var surface = Assert.IsAssignableFrom<ImageSource>(page.PageSurface);
        Assert.Equal(792, surface.Width, 1);
        Assert.Equal(612, surface.Height, 1);
    }

    [Fact]
    public async Task NightModeAndPdfiumMode_StayOnTheBitmapPath()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "bitmap.pdf"), 1);

        using var hybrid = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await hybrid.OpenDocumentAsync(path);
        var (night, r1) = PageFor(hybrid);
        await night.LoadImageAsync(r1, 72, 0, nightMode: true);
        Assert.Null(night.VectorSurface);
        Assert.NotNull(night.RenderedImage);

        using var pdfium = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Pdfium);
        await pdfium.OpenDocumentAsync(path);
        var (raster, r2) = PageFor(pdfium);
        await raster.LoadImageAsync(r2, 72, 0);
        Assert.Null(raster.VectorSurface);
        Assert.NotNull(raster.RenderedImage);
    }

    [Fact]
    public async Task RegionFallbackPage_IsStillALiveSurface()
    {
        string path = Path.Combine(_dir, "showcase.pdf");
        TestPdfBuilder.CreateEngineShowcasePdf(path);
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        var (page, renderer) = PageFor(service, page: 2);

        await page.LoadImageAsync(renderer, 150, 0);
        Assert.IsType<DrawingImage>(page.PageSurface);
        var report = service.GetPageEngineReport(2)!;
        Assert.Equal(PdfEngineMode.Hybrid, report.Engine);
        Assert.Contains(report.FallbackReasons, r => r.Contains("BlendMode"));
    }

    [Fact]
    public async Task VeryDensePage_FallsBackToABitmap()
    {
        string path = Path.Combine(_dir, "dense.pdf");
        var content = new StringBuilder();
        for (int i = 0; i < HybridVectorDocumentService.MaxLiveSurfaceCommands + 100; i++)
            content.Append("0 0 1 1 re f\n");
        WriteSinglePagePdf(path, content.ToString());

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        var (page, renderer) = PageFor(service, w: 200, h: 200);
        await page.LoadImageAsync(renderer, 72, 0);

        Assert.Null(page.VectorSurface);
        Assert.NotNull(page.RenderedImage);
    }

    [Fact]
    public async Task SurfaceStaysSharpAtHighZoom()
    {
        // A black square on white: drawn at 16× (≈1600 % of 72 dpi), a live surface keeps a hard
        // edge; a stretched 300 dpi bitmap would smear it over ~4 device pixels.
        string path = Path.Combine(_dir, "edge.pdf");
        WriteSinglePagePdf(path, "0 g 50 50 100 100 re f");
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        var surface = await service.GetVectorPageSurfaceAsync(1);
        Assert.NotNull(surface);

        const int scale = 16;
        var bitmap = Rasterize(surface!, 200 * scale, 200 * scale);
        int y = 100 * scale;               // middle of the square vertically
        int edgeX = 50 * scale;            // left edge of the square
        var row = ReadRow(bitmap, y);
        int soft = 0;
        for (int x = edgeX - 8; x <= edgeX + 8; x++)
        {
            byte v = row[x * 4];
            if (v > 20 && v < 235) soft++;
        }
        Assert.True(soft <= 2, $"edge spans {soft} intermediate pixels at {scale}x");
    }

    private static void WriteSinglePagePdf(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        };
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = ms.Position;
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static BitmapSource Rasterize(ImageSource surface, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(surface, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static byte[] ReadRow(BitmapSource bitmap, int y)
    {
        var row = new byte[bitmap.PixelWidth * 4];
        bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, 1), row, row.Length, 0);
        return row;
    }
}
