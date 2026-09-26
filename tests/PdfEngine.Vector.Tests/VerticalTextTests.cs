using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
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
/// Vertical writing (ISO 32000-2 9.7.4.3): Identity-V and /WMode 1 CMaps place each glyph's
/// position vector (/W2, /DW2) on the current point and advance downwards, instead of falling back.
/// </summary>
public class VerticalTextTests
{
    private readonly ITestOutputHelper _output;
    public VerticalTextTests(ITestOutputHelper output) => _output = output;

    private static byte[] VerticalPdf(string encodingRef, Func<VectorPdfBuilder, string>? extraEncoding = null, string w2 = "")
    {
        string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        var map = new GlyphTypeface(new Uri(fontPath)).CharacterToGlyphMap;
        string Hex(string s) => "<" + string.Concat(s.Select(c => map[c].ToString("X4"))) + ">";

        var b = new VectorPdfBuilder();
        byte[] arial = File.ReadAllBytes(fontPath);
        int stream = b.AddStream($"/Length1 {arial.Length}", arial, flate: true);
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /ArialMT /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {stream} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ArialMT /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {desc} 0 R /DW 1000 /DW2 [880 -1000] {w2} /CIDToGIDMap /Identity >>");
        string enc = extraEncoding != null ? extraEncoding(b) : encodingRef;
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /ArialMT /Encoding {enc} /DescendantFonts [{cid} 0 R] >>");
        b.AddPage($"BT /F1 24 Tf 60 180 Td {Hex("VERTICAL")} Tj ET BT /F1 18 Tf 1 0 0 1 140 190 Tm [{Hex("AB")} -500 {Hex("CD")}] TJ ET",
            $"<< /Font << /F1 {type0} 0 R >> >>", mediaBox: "[0 0 200 200]");
        return b.Build();
    }

    private async Task AssertMatchesPdfium(string name, byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
        var run = list.Commands.OfType<DrawGlyphRun>().First().Run;
        Assert.All(run.Glyphs, g => Assert.Equal(0, g.AdvanceX));
        Assert.True(run.Glyphs[1].OffsetY < run.Glyphs[0].OffsetY, "glyphs advance downwards");

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });
        using var d2d = new Direct2DVectorRenderer();
        using var wpf = new WindowsVectorRenderer();
        foreach (var (label, renderer) in new (string, IPdfVectorRenderer)[] { ("d2d", d2d), ("wpf", wpf) })
        {
            using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 });
            var (mean, bad) = DifferentialRenderingTests.Compare(page, reference);
            _output.WriteLine($"{name} {label}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 3.0, $"{name} {label}: mean {mean:F2}");
            Assert.True(bad <= 0.02, $"{name} {label}: {bad:P2} off");
        }
    }

    [Fact]
    public Task IdentityV_MatchesPdfium() => AssertMatchesPdfium("Identity-V", VerticalPdf("/Identity-V"));

    [Fact]
    public Task W2Metrics_MatchPdfium()
    {
        // Custom vertical metrics for every glyph id in [0, 3000): VX 300, VY 900, W1y -1200.
        return AssertMatchesPdfium("W2", VerticalPdf("/Identity-V", w2: "/W2 [0 2999 -1200 300 900]"));
    }

    [Fact]
    public Task EmbeddedWMode1CMap_MatchesPdfium() => AssertMatchesPdfium("WMode1", VerticalPdf("", b =>
    {
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def " +
            "/CMapName /Test-V def /CMapType 1 def /WMode 1 def " +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "1 begincidrange <0000> <FFFF> 0 endcidrange endcmap CMapName currentdict /CMap defineresource pop end end";
        int s = b.AddStream("/Type /CMap /CMapName /Test-V /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /WMode 1", Encoding.ASCII.GetBytes(cmap));
        return $"{s} 0 R";
    }));
}
