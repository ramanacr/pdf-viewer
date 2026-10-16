using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;
using Xunit.Abstractions;
using static PdfEngine.Vector.Tests.Fixtures.SyntheticCff;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Bare CFF programs whose FontMatrix is not the default [0.001 0 0 0.001 0 0]: a Top DICT
/// matrix that scales one axis, skews, rotates or uses 2048 units per em, and CID-keyed fonts
/// whose FDArray Font DICTs carry their own matrix, concatenated with the Top DICT's (FD × Top,
/// as FreeType and so PDFium apply it). The vector engine must draw them like PDFium, with no
/// fallback, and keep glyph positions on the PDF widths.
/// </summary>
public class CffFontMatrixTests
{
    private readonly ITestOutputHelper _output;
    public CffFontMatrixTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("narrow", 0.0005, 0, 0, 0.001, 1000)]
    [InlineData("skew", 0.001, 0, 0.0003, 0.001, 1000)]
    [InlineData("units-2048", 1 / 2048.0, 0, 0, 1 / 2048.0, 2048)]
    [InlineData("rotated", 0, 0.001, -0.001, 0, 1000)]
    [InlineData("mirrored", -0.001, 0, 0, 0.001, 1000)]
    public async Task NameKeyed_TopMatrix_RendersLikePdfium(string label, double a, double b, double c, double d, int u)
    {
        byte[] cff = NameKeyed(new[] { a, b, c, d, 0, 0 }, new[] { ".notdef", "A" }, new[] { new byte[] { 14 }, LShape(u, hinted: true) });
        var pdf = SimplePdf(cff);
        await AssertRendersLikePdfium(pdf, label);
    }

    public static TheoryData<string, double[]?, double[]?[], int[]> CidCases => new()
    {
        // Top DICT without a matrix: each FD's matrix applies alone.
        { "fd-only", null, new double[]?[] { new[] { 0.001, 0, 0, 0.001, 0, 0 } }, new[] { 1000, 1000 } },
        { "fd-narrow", null, new double[]?[] { new[] { 0.0005, 0, 0, 0.001, 0, 0 } }, new[] { 1000, 1000 } },
        // The common Adobe layout: an identity Top matrix with the scale in the FDs.
        { "top-identity", new double[] { 1, 0, 0, 1, 0, 0 }, new double[]?[] { new[] { 0.001, 0, 0, 0.001, 0, 0 } }, new[] { 1000, 1000 } },
        // Both carry part of the transform: FD scale, Top skew.
        { "fd-times-top", new double[] { 1, 0, 0.3, 1, 0, 0 }, new double[]?[] { new[] { 1 / 2048.0, 0, 0, 1 / 2048.0, 0, 0 } }, new[] { 2048, 2048 } },
        // FDs with different matrices: one glyph per FD.
        { "two-fds", null, new double[]?[] { new[] { 0.001, 0, 0, 0.001, 0, 0 }, new[] { 0.0005, 0, 0.0002, 0.0005, 0, 0 } }, new[] { 1000, 2000 } },
        { "two-fds-top", new double[] { 0.002, 0, 0, 0.002, 0, 0 }, new double[]?[] { new[] { 0.5, 0, 0, 0.5, 0, 0 }, null }, new[] { 1000, 1000 } },
        // The FD matrix alone may skew, rotate or use 2048 units per em.
        { "fd-skew", null, new double[]?[] { new[] { 0.001, 0, 0.0003, 0.001, 0, 0 } }, new[] { 1000, 1000 } },
        { "fd-rotated", null, new double[]?[] { new[] { 0, 0.001, -0.001, 0, 0, 0 } }, new[] { 1000, 1000 } },
        { "fd-2048", null, new double[]?[] { new[] { 1 / 2048.0, 0, 0, 1 / 2048.0, 0, 0 } }, new[] { 2048, 2048 } },
        { "top-rotated", new double[] { 0, 1, -1, 0, 0, 0 }, new double[]?[] { new[] { 0.001, 0, 0, 0.001, 0, 0 } }, new[] { 1000, 1000 } },
        // A non-default Top matrix and no FD matrices.
        { "top-only", new double[] { 0.0008, 0, 0.0002, 0.001, 0, 0 }, new double[]?[] { null }, new[] { 1000, 1000 } },
    };

    [Theory]
    [MemberData(nameof(CidCases))]
    public async Task CidKeyed_FontDictMatrices_RenderLikePdfium(string label, double[]? top, double[]?[] fdMatrices, int[] units)
    {
        // Glyph 1 (CID 1) uses FD 0; glyph 2 (CID 2) the last FD. The second calls a local subr.
        int last = fdMatrices.Length - 1;
        var subr = new CharString().N(0, units[1] / 5).Op(5).Op(11).ToArray(); // rlineto, return
        var fds = fdMatrices.Select((m, i) => new FontDict(m, i == last ? new[] { subr } : null)).ToArray();
        byte[] second = new CharString().N(units[1] / 10, 0).Op(21).N(units[1] / 2, 0).Op(5).N(-107).Op(10) // callsubr 0 (bias 107)
            .N(-units[1] / 2, 0).Op(5).Op(14).ToArray();
        byte[] cff = CidKeyed(top, fds, new[] { new CidGlyph(1, 0, LShape(units[0], hinted: true)), new CidGlyph(2, last, second) });
        await AssertRendersLikePdfium(CidPdf(cff), label);
    }

    [Fact]
    public void OutlineMatrix_IsFontDictTimesTop()
    {
        byte[] data = CidKeyed(new double[] { 1, 0, 0.25, 1, 0, 0 },
            new[] { new FontDict(new[] { 0.002, 0, 0, 0.001, 0, 0 }), new FontDict(null) },
            new[] { new CidGlyph(7, 0, LShape(1000)), new CidGlyph(9, 1, LShape(1000)) });
        var cff = PdfEngine.Vector.Fonts.Programs.CffFont.TryParse(data)!;
        Assert.True(cff.IsCidKeyed && cff.HasTopFontMatrix && cff.HasFontDictMatrices);
        Assert.Equal(new[] { 0.002, 0, 0.00025, 0.001, 0, 0 }, cff.OutlineMatrix(1).Select(v => Math.Round(v, 9)));
        Assert.Equal(new[] { 1, 0, 0.25, 1, 0, 0 }, cff.OutlineMatrix(2)); // no FD matrix: the Top DICT's

        // Without a Top DICT matrix the FD matrix applies alone, not on top of the default 1/1000.
        var fdOnly = PdfEngine.Vector.Fonts.Programs.CffFont.TryParse(CidKeyed(null, new[] { new FontDict(new[] { 0.0005, 0, 0, 0.0005, 0, 0 }) },
            new[] { new CidGlyph(1, 0, LShape(2000)) }))!;
        Assert.False(fdOnly.HasTopFontMatrix);
        Assert.Equal(new[] { 0.0005, 0, 0, 0.0005, 0, 0 }, fdOnly.OutlineMatrix(1));
    }

    [Fact]
    public async Task CidKeyed_GlyphPositions_FollowPdfWidths()
    {
        // The FD halves the glyph; /W still advances each glyph a full 1000 units.
        byte[] cff = CidKeyed(null, new[] { new FontDict(new[] { 0.0005, 0, 0, 0.0005, 0, 0 }) },
            new[] { new CidGlyph(1, 0, LShape(1000)), new CidGlyph(2, 0, LShape(1000)) });
        using var doc = await PdfVectorDocument.OpenAsync(CidPdf(cff));
        var list = await doc.GetPageDisplayListAsync(1);
        var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run;
        Assert.Equal(PdfFontProgramFormat.OpenTypeCff, run.Face!.Format);
        Assert.Equal(2, run.Glyphs.Count);
        Assert.Equal(1.0, run.Glyphs[0].Width, 3);
        Assert.Equal(60.0, run.Glyphs[1].OffsetX - run.Glyphs[0].OffsetX, 3);
    }

    [Fact]
    public async Task CidKeyed_TextClip_UsesTheFontDictMatrix()
    {
        // Render mode 7 adds the outlines to the clip; the filled square shows them. The clip
        // path is outlined separately from glyph drawing, so it must see the same FD x Top.
        byte[] cff = CidKeyed(new double[] { 1, 0, 0.3, 1, 0, 0 }, new[] { new FontDict(new[] { 0.0005, 0, 0, 0.001, 0, 0 }) },
            new[] { new CidGlyph(1, 0, LShape(1000)), new CidGlyph(2, 0, LShape(1000)) });
        var pdf = CidPdf(cff, "BT 7 Tr /F1 60 Tf 30 70 Td <00010002> Tj ET 0 0 1 rg 0 0 220 160 re f");
        await AssertRendersLikePdfium(pdf, "text-clip", glyphRun: false);
    }

    [Fact]
    public async Task SimpleFontWithoutWidths_AdvancesByTheProgramThroughItsMatrix()
    {
        // No /Widths: each glyph advances by its charstring width (1000 units) mapped through the
        // FontMatrix, half an em here, as PDFium places them.
        byte[] cff = NameKeyed(new[] { 0.0005, 0, 0, 0.001, 0, 0 }, new[] { ".notdef", "A" }, new[] { new byte[] { 14 }, LShape(1000, width: 1000) });
        var pdf = SimplePdf(cff, text: "(AAA)", widths: false);
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var list = await doc.GetPageDisplayListAsync(1);
            var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run;
            Assert.Equal(500.0, run.Glyphs[0].Width * 1000, 3);
        }
        await AssertRendersLikePdfium(pdf, "no-widths");
    }

    // ------------------------------------------------------------------ helpers

    private async Task AssertRendersLikePdfium(byte[] pdf, string label, bool glyphRun = true)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var run = glyphRun ? Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run : Assert.Single(list.Commands.OfType<PushTextClip>()).Runs[0];
        Assert.Equal(PdfFontProgramFormat.OpenTypeCff, run.Face!.Format);

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });
        int refDark = CountDark(reference.Pixels.Span);
        Assert.True(refDark > 200, $"{label}: PDFium drew no glyph pixels");

        // Both backends: Direct2D (the live renderer) and WPF (printing, export, fallbacks of the host).
        using var d2d = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        var result = await d2d.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 }, null, CancellationToken.None);
        Assert.Empty(result.Fallbacks);
        using (var vector = result.Page)
            Check(vector, "direct2d");

        if (!glyphRun)
            return; // the WPF backend classifies glyph-outline clips (any font)
        using var wpf = new WindowsVectorRenderer();
        var tokens = await wpf.AnalyzeAsync(list);
        Assert.True(tokens.Count == 0, $"{label}: {string.Join("; ", tokens.Select(t => t.Description))}");
        using (var vector = await wpf.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 }))
            Check(vector, "wpf");

        void Check(RenderedPage vector, string backend)
        {
            var (mean, bad) = DifferentialRenderingTests.Compare(vector, reference);
            int dark = CountDark(vector.Pixels.Span);
            _output.WriteLine($"{label} {backend}: mean={mean:F2} bad={bad:P2} dark={dark} pdfium dark={refDark}");
            Assert.True(dark > 200, $"{label} {backend}: no glyph pixels drawn");
            Assert.True(mean <= 1.0 && bad <= 0.005, $"{label} {backend}: mean {mean:F2} bad {bad:P2}");
        }
    }

    private static int CountDark(ReadOnlySpan<byte> span)
    {
        int dark = 0;
        for (int p = 0; p < span.Length; p += 4) if (Math.Min(span[p], Math.Min(span[p + 1], span[p + 2])) < 128) dark++; // any ink, whatever colour
        return dark;
    }

    private static byte[] SimplePdf(byte[] cff, string text = "(A)", bool widths = true)
    {
        var b = new VectorPdfBuilder();
        int stream = b.AddStream("/Subtype /Type1C", cff);
        int desc = b.Add("<< /Type /FontDescriptor /FontName /Synthetic /Flags 4 /FontBBox [-1000 -1000 1000 1000] /ItalicAngle 0 " +
                         $"/Ascent 900 /Descent -200 /CapHeight 800 /StemV 80 /FontFile3 {stream} 0 R >>");
        string w = widths ? "/FirstChar 65 /LastChar 65 /Widths [1000] " : "";
        int font = b.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Synthetic {w}" +
                         $"/Encoding << /Type /Encoding /Differences [65 /A] >> /FontDescriptor {desc} 0 R >>");
        b.AddPage($"BT /F1 60 Tf 100 70 Td {text} Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 220 160]");
        return b.Build();
    }

    private static byte[] CidPdf(byte[] cff, string content = "BT /F1 60 Tf 30 70 Td <00010002> Tj ET")
    {
        var b = new VectorPdfBuilder();
        int stream = b.AddStream("/Subtype /CIDFontType0C", cff);
        int desc = b.Add("<< /Type /FontDescriptor /FontName /Synthetic /Flags 4 /FontBBox [-1000 -1000 1000 1000] /ItalicAngle 0 " +
                         $"/Ascent 900 /Descent -200 /CapHeight 800 /StemV 80 /FontFile3 {stream} 0 R >>");
        int cid = b.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Synthetic /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                        $"/DW 1000 /W [1 [1000 1000]] /FontDescriptor {desc} 0 R >>");
        int font = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Synthetic /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>");
        b.AddPage(content, $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 220 160]");
        return b.Build();
    }
}
