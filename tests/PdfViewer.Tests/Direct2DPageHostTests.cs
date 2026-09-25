using System;
using System.IO;
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
/// The default page host: Direct2D renders the page bitmap, and when the page is zoomed past that
/// bitmap's resolution, a viewport detail tile at exact device resolution is laid over it.
/// </summary>
public class Direct2DPageHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Direct2DPageHostTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string SquarePdf()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "square.pdf");
        string content = "0 g 50 50 100 100 re f";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        using var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.ASCII.GetBytes(t));
        W("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++) { offsets[i] = ms.Position; W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static byte[] Pixels(BitmapSource b)
    {
        BitmapSource bgra = b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Pbgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var p = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(p, bgra.PixelWidth * 4, 0);
        return p;
    }

    [Fact]
    public async Task DefaultHost_IsDirect2DBitmaps()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        Assert.Equal(HybridVectorDocumentService.VectorHostMode.Direct2DTiles, service.HostMode);
        Assert.True(service.IsDirect2DActive);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 144, 0);
        Assert.Null(page.VectorSurface);
        var bmp = Assert.IsAssignableFrom<BitmapSource>(page.PageSurface);
        Assert.Equal(400, bmp.PixelWidth);
    }

    [Fact]
    public async Task ZoomedPage_GetsASharpDetailTileForTheVisibleArea()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 150, 0);
        page.UpdateScale(16); // 1600 %: the page is 3200 × 3200 DIPs, its bitmap only ~417 px

        // The viewport shows the square's left edge region.
        var visible = new Rect(700, 1400, 400, 300);
        await page.UpdateDetailAsync(service, visible, devicePixelsPerDip: 1.0, rotation: 0, nightMode: false);
        var tile = Assert.IsAssignableFrom<BitmapSource>(page.DetailImage);
        Assert.True(page.DetailRect.Contains(visible), "the tile covers the visible area");
        Assert.True(tile.PixelWidth <= PageViewModel.MaxDetailPixels && tile.PixelHeight <= PageViewModel.MaxDetailPixels);
        Assert.Equal(page.DetailRect.Width, tile.PixelWidth, 0);

        // The square's left edge is at x = 50 pt → 800 DIPs; it must be a hard edge in the tile.
        var px = Pixels(tile);
        int row = (int)(1550 - page.DetailRect.Y);
        int edge = (int)(800 - page.DetailRect.X);
        int soft = 0;
        for (int x = edge - 6; x <= edge + 6; x++)
        {
            byte v = px[(row * tile.PixelWidth + x) * 4];
            if (v > 20 && v < 235) soft++;
        }
        Assert.True(soft <= 2, $"edge spans {soft} intermediate pixels at 1600 %");

        // A small scroll inside the tile's margin reuses it.
        var same = page.DetailImage;
        await page.UpdateDetailAsync(service, new Rect(720, 1420, 400, 300), 1.0, 0, false);
        Assert.Same(same, page.DetailImage);

        // A zoom change drops the stale tile.
        page.UpdateScale(8);
        Assert.Null(page.DetailImage);
    }

    [Fact]
    public async Task UnzoomedPage_NeedsNoDetailTile()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 150, 0);
        page.UpdateScale(1.0);
        await page.UpdateDetailAsync(service, new Rect(0, 0, 200, 200), 1.0, 0, false);
        Assert.Null(page.DetailImage);
    }

    [Theory]
    [InlineData(PdfEngineMode.Auto)]
    [InlineData(PdfEngineMode.Pdfium)]
    public async Task NightModeDetailTile_IsInverted(PdfEngineMode mode)
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, mode);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        page.UpdateScale(8);
        await page.UpdateDetailAsync(service, new Rect(0, 0, 300, 300), 1.0, 0, nightMode: true);
        var tile = Assert.IsAssignableFrom<BitmapSource>(page.DetailImage);
        var px = Pixels(tile);
        Assert.True(px[0] < 30, "outside the square (white page) is dark in night mode");
        // (52.5 pt, 52.5 pt) = 420 DIPs from the top-left is inside the square (400..1200 DIPs at 800 %).
        int inside = ((int)(420 - page.DetailRect.Y) * tile.PixelWidth + (int)(420 - page.DetailRect.X)) * 4;
        Assert.True(px[inside] > 225, "the black square is light in night mode");
    }
}
