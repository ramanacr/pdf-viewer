using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Glyph outlines as a clip (text rendering modes 4–7) and pattern-filled text: Direct2D clips to
/// the DirectWrite outlines instead of classifying the text box as fallback.
/// </summary>
public class TextClipTests
{
    private readonly ITestOutputHelper _output;
    public TextClipTests(ITestOutputHelper output) => _output = output;

    private async Task AssertMatches(string name, byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
        Assert.Contains(list.Commands, c => c is PushTextClip);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 }, null, null, false, CancellationToken.None);
        using var v = result.Page;
        Assert.Empty(result.Fallbacks);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"{name}: mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 3.0 && bad <= 0.02, $"{name}: mean {mean:F2} bad {bad:P2}");
    }

    private static byte[] Page(string content, string extraResources = "")
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        b.AddPage(content, $"<< /Font << /F1 {helv} 0 R >> {extraResources} >>");
        return b.Build();
    }

    [Fact]
    public Task Mode7Clip_ThenFill_MatchesPdfium() => AssertMatches("mode7", Page(
        "q BT /F1 60 Tf 7 Tr 10 120 Td (CLIP) Tj 0 -80 Td (ME) Tj ET 1 0 0 rg 0 0 100 200 re f 0 0 1 rg 100 0 100 200 re f Q 0 g 0 0 10 10 re f"));

    [Fact]
    public Task Mode4FillAndClip_MatchesPdfium() => AssertMatches("mode4", Page(
        "q BT /F1 70 Tf 4 Tr 0 0.6 0 rg 10 70 Td (Hi!) Tj ET 0 0 1 RG 3 w 0 0 m 200 200 l S Q"));

    [Fact]
    public async Task PatternFilledText_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int fn = b.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        int sh = b.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function {fn} 0 R /Extend [true true] >>");
        int pat = b.Add($"<< /Type /Pattern /PatternType 2 /Shading {sh} 0 R >>");
        b.AddPage("BT /Pattern cs /P1 scn /F1 64 Tf 10 80 Td (Grad) Tj ET", $"<< /Font << /F1 {helv} 0 R >> /Pattern << /P1 {pat} 0 R >> >>");
        await AssertMatches("pattern-text", b.Build());
    }

    private static (VectorPdfBuilder B, int Type3, int Helv) Type3Fonts()
    {
        var b = new VectorPdfBuilder();
        int bar = b.AddStream(string.Empty, "1000 0 0 0 900 900 d1 0 0 900 300 re f 0 500 900 300 re f");
        int ring = b.AddStream(string.Empty, "1000 0 0 0 900 900 d1 450 0 m 900 450 l 450 900 l 0 450 l h 450 250 m 250 450 l 450 650 l 650 450 l h f*");
        int t3 = b.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                       $"/CharProcs << /bar {bar} 0 R /ring {ring} 0 R >> /Encoding << /Type /Encoding /Differences [65 /bar /ring] >> " +
                       "/FirstChar 65 /LastChar 66 /Widths [1000 1000] >>");
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        return (b, t3, helv);
    }

    [Fact]
    public async Task Type3GlyphsInMode7_AddNothingToTheClip()
    {
        var (b, t3, _) = Type3Fonts();
        b.AddPage("q BT 7 Tr /T3 40 Tf 10 120 Td (ABABA) Tj ET 1 0 0 rg 0 0 100 200 re f 0 0 1 rg 100 0 100 200 re f Q 0 g 0 0 5 5 re f",
            $"<< /Font << /T3 {t3} 0 R >> >>");
        await AssertMatchesWithoutClip("type3 mode7", b.Build());
    }

    [Fact]
    public async Task Type3AndNormalGlyphs_Mode4_OnlyTheNormalGlyphsClip()
    {
        var (b, t3, helv) = Type3Fonts();
        b.AddPage("q BT 4 Tr 0 0.5 0 rg /T3 36 Tf 10 130 Td (BAB) Tj /F1 44 Tf 0 -90 Td (Clip) Tj ET 0 0 1 RG 4 w 0 0 m 200 200 l S Q",
            $"<< /Font << /T3 {t3} 0 R /F1 {helv} 0 R >> >>");
        await AssertMatchesWithoutClip("type3+helvetica mode4", b.Build(), expectTextClip: true);
    }

    /// <summary>Type 3 glyphs contribute nothing to a text clip (ISO 32000-2 9.3.6); PDFium agrees.</summary>
    private async Task AssertMatchesWithoutClip(string name, byte[] pdf, bool expectTextClip = false)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
        Assert.Equal(expectTextClip, list.Commands.Any(c => c is PushTextClip));
        using var renderer = new Direct2DVectorRenderer();
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi }, null, null, false, CancellationToken.None);
            using var v = result.Page;
            Assert.Empty(result.Fallbacks);
            using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"{name} @{dpi}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 3.0 && bad <= 0.02, $"{name} @{dpi}: mean {mean:F2} bad {bad:P2}");
        }
    }
}
