using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Tiling patterns (ISO 32000-2 8.7.3): the cell is recorded once in pattern space and Direct2D
/// repeats it with a wrapping bitmap brush at device resolution, against the PDFium oracle.
/// </summary>
public class TilingPatternTests
{
    private readonly ITestOutputHelper _output;
    public TilingPatternTests(ITestOutputHelper output) => _output = output;

    private async Task AssertMatches(string name, byte[] pdf, double maxMean = 3.0, double maxBad = 0.03)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason + " " + t.Description))}");
        Assert.Contains(list.Commands, c => c is DrawTilingPattern);
        using var renderer = new Direct2DVectorRenderer();
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        // Pixel-aligned resolutions: at fractional cell sizes PDFium snaps each cell to whole
        // pixels and drifts, while this backend keeps the exact period (see ExactPeriod test).
        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi }, null, null, false, CancellationToken.None);
            using var v = result.Page;
            using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
            Assert.Empty(result.Fallbacks);
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"{name} @{dpi}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= maxMean, $"{name} @{dpi}: mean {mean:F2} > {maxMean}");
            Assert.True(bad <= maxBad, $"{name} @{dpi}: {bad:P2} pixels off");
        }
    }

    private static byte[] Page(string patternDict, string cell, string content, string extraResources = "")
    {
        var b = new VectorPdfBuilder();
        int pat = b.AddStream("/Type /Pattern /PatternType 1 " + patternDict, cell);
        b.AddPage(content, $"<< /Pattern << /P1 {pat} 0 R >> {extraResources} >>");
        return b.Build();
    }

    [Fact]
    public Task ColouredCheckerboard_MatchesPdfium() => AssertMatches("checker", Page(
        "/PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >>",
        "1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f",
        "/Pattern cs /P1 scn 10 10 180 180 re f"));

    [Fact]
    public Task UncolouredPattern_UsesTheScnColour() => AssertMatches("uncoloured", Page(
        "/PaintType 2 /TilingType 1 /BBox [0 0 12 12] /XStep 12 /YStep 12 /Resources << >>",
        "0 0 6 6 re f 3 w 6 6 m 12 12 l S",
        "/CS1 cs 0.1 0.6 0.3 /P1 scn 0 0 200 200 re f",
        "/ColorSpace << /CS1 [/Pattern /DeviceRGB] >>"));

    [Fact]
    public Task PatternMatrix_RotatesAndScalesTheTiling() => AssertMatches("matrix", Page(
        "/PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Matrix [1.5 0.866 -0.866 1.5 30 10] /Resources << >>",
        "0.9 0.5 0.1 rg 0 0 5 10 re f",
        "/Pattern cs /P1 scn 100 100 m 190 100 190 190 100 190 c 10 190 10 100 100 100 c f"));

    [Fact]
    public Task OverlappingCells_ContributeToNeighbours() => AssertMatches("overlap", Page(
        "/PaintType 1 /TilingType 1 /BBox [-10 -10 30 30] /XStep 25 /YStep 25 /Resources << >>",
        "0 0 0.8 RG 2 w 10 10 12 0 360 re S 0.8 0 0 rg -5 -5 10 10 re f",
        "/Pattern cs /P1 scn 0 0 200 200 re f"));

    [Fact]
    public Task PatternWithAlpha_MatchesPdfium() => AssertMatches("alpha", Page(
        "/PaintType 1 /TilingType 1 /BBox [0 0 16 16] /XStep 16 /YStep 16 /Resources << >>",
        "0 0 0 rg 4 4 8 8 re f",
        "1 0 0 rg 0 0 200 200 re f /A gs /Pattern cs /P1 scn 20 20 160 160 re f",
        "/ExtGState << /A << /ca 0.5 >> >>"));

    [Fact]
    public async Task FractionalCellSize_KeepsTheExactPeriod()
    {
        // 20 pt cells at 150 dpi are 41.67 px: after 8 periods a snapped cell would be ~3 px off.
        var pdf = Page("/PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >>",
            "1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f",
            "/Pattern cs /P1 scn 0 0 200 200 re f");
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        using var page = (await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 150 }, null, null, false, CancellationToken.None)).Page;
        double s = 150 / 72.0;
        (byte B, byte R) At(double xPt, double yPt)
        {
            int x = (int)(xPt * s), y = (int)((200 - yPt) * s);
            var px = page.Pixels.Span;
            int o = y * page.Stride + x * 4;
            return (px[o], px[o + 2]);
        }
        // Red squares occupy [20k, 20k+10]², blue [20k+10, 20k+20]²; probe 1 pt inside each edge.
        foreach (int k in new[] { 0, 4, 8 })
        {
            Assert.True(At(20 * k + 1, 20 * k + 1).R > 200, $"red corner of cell {k}");
            Assert.True(At(20 * k + 9, 20 * k + 9).R > 200, $"red far corner of cell {k}");
            Assert.True(At(20 * k + 11, 20 * k + 11).B > 200, $"blue corner of cell {k}");
            Assert.True(At(20 * k + 19, 20 * k + 19).B > 200, $"blue far corner of cell {k}");
        }
    }

    [Fact]
    public Task TilingPatternStroke_FillsTheStrokeOutline() => AssertMatches("stroke", Page(
        "/PaintType 1 /TilingType 1 /BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >>",
        "1 0 0 rg 0 0 4 4 re f 0 0 1 rg 4 4 4 4 re f",
        "/Pattern CS /P1 SCN 16 w 1 J 30 30 m 170 170 l 30 170 m 170 30 l S"));

    [Fact]
    public async Task ShadingPatternStroke_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int fn = b.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        int sh = b.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function {fn} 0 R /Extend [true true] >>");
        int pat = b.Add($"<< /Type /Pattern /PatternType 2 /Shading {sh} 0 R >>");
        b.AddPage("/Pattern CS /P1 SCN 12 w 1 j 20 20 m 100 180 l 180 20 l h S", $"<< /Pattern << /P1 {pat} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        Assert.Contains(list.Commands, c => c is PushStrokeClip);
        using var renderer = new Direct2DVectorRenderer();
        using var v = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 });
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(b.Build());
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"shading stroke: mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 3.0 && bad <= 0.02, $"mean {mean:F2} bad {bad:P2}");
    }

    [Fact]
    public async Task WpfBackend_ClassifiesTilingAsFallback()
    {
        var pdf = Page("/PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >>",
            "1 0 0 rg 0 0 10 10 re f", "/Pattern cs /P1 scn 10 10 50 50 re f");
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var wpf = new WindowsVectorRenderer();
        var token = Assert.Single(await wpf.AnalyzeAsync(list));
        Assert.Equal(PdfFallbackReason.Pattern, token.Reason);
    }

    [Fact]
    public async Task SelfReferencingPattern_FallsBackInsteadOfRecursing()
    {
        var b = new VectorPdfBuilder();
        // Object 3 is the pattern (catalog 1, pages 2); its cell paints with itself.
        int pat = b.AddStream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /Pattern << /P1 3 0 R >> >>",
            "/Pattern cs /P1 scn 0 0 10 10 re f");
        Assert.Equal(3, pat);
        b.AddPage("/Pattern cs /P1 scn 0 0 100 100 re f", $"<< /Pattern << /P1 {pat} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.Contains(list.FallbackTokens, t => t.Reason == PdfFallbackReason.Pattern);
    }
}
