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
    public async Task PdfiumMode_StaysOnTheBitmapPath()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "bitmap.pdf"), 1);
        using var pdfium = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Pdfium);
        await pdfium.OpenDocumentAsync(path);
        var (raster, r2) = PageFor(pdfium);
        await raster.LoadImageAsync(r2, 72, 0);
        Assert.Null(raster.VectorSurface);
        Assert.NotNull(raster.RenderedImage);
    }

    [Fact]
    public async Task NightMode_IsALiveSurfaceEqualToThePixelInvertedPage()
    {
        // Colour, alpha, gradient and stroke content: inversion must commute with compositing.
        string path = Path.Combine(_dir, "night.pdf");
        WriteSinglePagePdf(path,
            "1 0 0 rg 10 10 120 120 re f /G1 gs 0 0 1 rg 60 60 120 120 re f " +
            "q 0 0 200 40 re W n /Sh1 sh Q 0.2 g 2 w 20 190 m 180 150 l S",
            "<< /ExtGState << /G1 << /ca 0.5 >> >> /Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 0.5 1] /N 1 >> /Extend [true true] >> >> >>");

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        var (page, renderer) = PageFor(service, w: 200, h: 200);

        await page.LoadImageAsync(renderer, 150, 0, nightMode: true);
        var night = Assert.IsType<DrawingImage>(page.PageSurface);
        Assert.Null(page.RenderedImage);

        var day = await service.GetVectorPageSurfaceAsync(1);
        Assert.NotNull(day);
        Assert.NotSame(day, night);

        var dayPixels = ReadAll(Rasterize(day!, 400, 400));
        var nightPixels = ReadAll(Rasterize(night, 400, 400));
        long total = 0;
        for (int i = 0; i < dayPixels.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
                total += Math.Abs(255 - dayPixels[i + c] - nightPixels[i + c]);
        }
        double mean = total / (dayPixels.Length / 4.0 * 3);
        Assert.True(mean < 1.0, $"night surface differs from the inverted day surface by {mean:F2}/255");

        // Toggling back returns the day surface.
        await page.LoadImageAsync(renderer, 150, 0, nightMode: false);
        Assert.Same(day, page.PageSurface);
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

    [Fact]
    public void SurfaceCache_OnlyForDrawingsThatFitATexture()
    {
        var drawing = new DrawingImage(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        drawing.Freeze();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);

        Assert.IsType<BitmapCache>(PdfViewer.Views.PageSurfaceCache.Choose(drawing, 612, 792, 1.0));
        Assert.IsType<BitmapCache>(PdfViewer.Views.PageSurfaceCache.Choose(drawing, 2048, 2650, 1.5)); // 3975 px
        Assert.Null(PdfViewer.Views.PageSurfaceCache.Choose(drawing, 612 * 8, 792 * 8, 1.0));        // 1600 % zoom: live
        Assert.Null(PdfViewer.Views.PageSurfaceCache.Choose(bitmap, 612, 792, 1.0));                // bitmaps are textures already
        Assert.Null(PdfViewer.Views.PageSurfaceCache.Choose(null, 612, 792, 1.0));
    }

    private static void WriteSinglePagePdf(string path, string content, string resources = "<< >>")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources {resources} /Contents 4 0 R >>",
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

    private static byte[] ReadAll(BitmapSource bitmap)
    {
        var all = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(all, bitmap.PixelWidth * 4, 0);
        return all;
    }

    private static byte[] ReadRow(BitmapSource bitmap, int y)
    {
        var row = new byte[bitmap.PixelWidth * 4];
        bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, 1), row, row.Length, 0);
        return row;
    }
}
