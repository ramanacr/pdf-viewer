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
}
