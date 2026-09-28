using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Small geometries (a map's thousands of symbols) are drawn directly, not through realizations:
/// the first tile at a new zoom no longer builds thousands of meshes it draws once, and the pixels
/// are the same as when they are realized.
/// </summary>
public class SmallGeometryDrawingTests
{
    private static byte[] Symbols()
    {
        var content = new StringBuilder("0.2 0.4 0.8 rg 0 0 0 RG 0.5 w\n");
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 60; x++)
            {
                double cx = 10 + x * 6.5, cy = 10 + y * 6.5;
                // A small circle (four curves), filled and stroked.
                content.Append($"{cx + 2} {cy} m {cx + 2} {cy + 1.1} {cx + 1.1} {cy + 2} {cx} {cy + 2} c {cx - 1.1} {cy + 2} {cx - 2} {cy + 1.1} {cx - 2} {cy} c ");
                content.Append($"{cx - 2} {cy - 1.1} {cx - 1.1} {cy - 2} {cx} {cy - 2} c {cx + 1.1} {cy - 2} {cx + 2} {cy - 1.1} {cx + 2} {cy} c B\n");
            }
        var b = new VectorPdfBuilder();
        b.AddPage(content.ToString(), "<< >>", mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    [Fact]
    public async Task SmallGeometries_AreDrawnDirectly_WithTheSamePixels()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Symbols());
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = 600 };
        var region = new PixelRegion(1024, 1024, 512, 512);

        using var direct = new Direct2DVectorRenderer();
        using var a = (await direct.RenderAsync(list, request, null, region, invertColors: false, CancellationToken.None)).Page;
        Assert.Equal(0, direct.RealizationCounts.Built);

        using var realized = new Direct2DVectorRenderer { DrawSmallGeometriesDirectly = false };
        using var b = (await realized.RenderAsync(list, request, null, region, invertColors: false, CancellationToken.None)).Page;
        Assert.True(realized.RealizationCounts.Built > 100);

        var (mean, bad) = DifferentialRenderingTests.Compare(a, b);
        Assert.True(mean < 1.0 && bad < 0.005, $"direct vs realized: mean {mean:F3}, {bad:P2} of pixels");
    }
}
