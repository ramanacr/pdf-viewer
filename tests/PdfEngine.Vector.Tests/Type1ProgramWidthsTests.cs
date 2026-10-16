using System;
using System.Collections.Generic;
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

namespace PdfEngine.Vector.Tests;

/// <summary>
/// A simple font with an embedded Type 1 (FontFile) program and no /Widths advances by the
/// program's own widths: hsbw or sbw in each charstring (a seac composite keeps its own), mapped
/// through the FontMatrix and chosen through the font's encoding, as PDFium places them. A
/// /Widths array that is too short gives the codes past its end /MissingWidth, else 0.
/// </summary>
public class Type1ProgramWidthsTests
{
    private readonly ITestOutputHelper _output;
    public Type1ProgramWidthsTests(ITestOutputHelper output) => _output = output;

    // "A" is 600 wide (hsbw), "B" 800 (sbw), "Aacute" 700: a seac of A and acute, whose own widths differ.
    private static readonly (string, object[])[] Glyphs =
    {
        (".notdef", new object[] { 0, 500, "hsbw", "endchar" }),
        ("A", new object[] { 0, 600, "hsbw", 50, 0, "rmoveto", 500, 0, "rlineto", 0, 700, "rlineto", -500, 0, "rlineto", "closepath", "endchar" }),
        ("B", new object[] { 0, 0, 800, 0, "sbw", 100, 0, "rmoveto", 600, 0, "rlineto", 0, 300, "rlineto", -600, 0, "rlineto", "closepath", "endchar" }),
        ("acute", new object[] { 0, 400, "hsbw", 100, 800, "rmoveto", 200, 0, "rlineto", 0, 100, "rlineto", -200, 0, "rlineto", "closepath", "endchar" }),
        ("Aacute", new object[] { 0, 700, "hsbw", 0, 150, 0, 65, 194, "seac" }),
    };

    [Theory]
    [InlineData("identity", "0.001 0 0 0.001 0 0", 1.0)]
    [InlineData("narrow", "0.0005 0 0 0.001 0 0", 0.5)]
    [InlineData("skew", "0.001 0 0.0003 0.001 0 0", 1.0)]
    [InlineData("units-2048", "0.00048828125 0 0 0.00048828125 0 0", 0.48828125)]
    public async Task NoWidths_AdvancesByTheProgramThroughItsMatrix(string label, string fontMatrix, double scale)
    {
        var pdf = Type1Pdf(SyntheticType1.Build(fontMatrix, Glyphs), "(ABCAB)", widths: null, encoding: "<< /Type /Encoding /Differences [65 /A /B /Aacute] >>");
        await AssertAdvances(pdf, new[] { 600, 800, 700, 600, 800 }.Select(w => w * scale).ToArray());
        await AssertRendersLikePdfium(pdf, label);
    }

    [Fact]
    public async Task NoWidths_SymbolicFont_UsesTheBuiltInEncoding()
    {
        // No /Encoding and a symbolic font: codes go through the program's own encoding, which
        // swaps A and B here, and so do their widths.
        var program = SyntheticType1.Build("0.001 0 0 0.001 0 0", Glyphs, encoding: new Dictionary<int, string> { [65] = "B", [66] = "A" });
        var pdf = Type1Pdf(program, "(ABA)", widths: null, encoding: null, symbolic: true);
        await AssertAdvances(pdf, new double[] { 800, 600, 800 });
        await AssertRendersLikePdfium(pdf, "symbolic");
    }

    [Theory]
    [InlineData("no-missing-width", null, 0.0)]
    [InlineData("missing-width", 250, 250.0)]
    public async Task ShortWidths_CodesPastTheEnd_UseMissingWidthElseZero(string label, int? missingWidth, double expected)
    {
        // /Widths covers 65 only although LastChar is 67: B and Aacute advance by /MissingWidth
        // (0 when absent), not by the program, as in PDFium.
        var pdf = Type1Pdf(SyntheticType1.Build("0.001 0 0 0.001 0 0", Glyphs), "(ABCA)", widths: "/FirstChar 65 /LastChar 67 /Widths [600]",
            encoding: "<< /Type /Encoding /Differences [65 /A /B /Aacute] >>", missingWidth: missingWidth);
        await AssertAdvances(pdf, new[] { 600, expected, expected, 600 });
        await AssertRendersLikePdfium(pdf, label);
    }

    [Fact]
    public async Task StandardFontName_KeepsStandardMetrics()
    {
        // A standard 14 name keeps its metrics: Helvetica's A is 667 wide, not the program's 600.
        var pdf = Type1Pdf(SyntheticType1.Build("0.001 0 0 0.001 0 0", Glyphs), "(A)", widths: null, encoding: "/WinAnsiEncoding", baseFont: "Helvetica");
        await AssertAdvances(pdf, new double[] { 667 });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task AssertAdvances(byte[] pdf, double[] expected)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run;
        Assert.Equal(expected.Length, run.Glyphs.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], run.Glyphs[i].Width * 1000, 3);
            if (i > 0)
                Assert.Equal(expected[i - 1] / 1000 * FontSize, run.Glyphs[i].OffsetX - run.Glyphs[i - 1].OffsetX, 3);
        }
    }

    private async Task AssertRendersLikePdfium(byte[] pdf, string label)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run;
        Assert.Equal(PdfFontProgramFormat.OpenTypeCff, run.Face!.Format);

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });
        int refDark = CountDark(reference.Pixels.Span);
        Assert.True(refDark > 200, $"{label}: PDFium drew no glyph pixels");

        using var d2d = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        var result = await d2d.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 }, null, CancellationToken.None);
        Assert.Empty(result.Fallbacks);
        using (var vector = result.Page)
            Check(vector, "direct2d");

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
        for (int p = 0; p < span.Length; p += 4) if (Math.Min(span[p], Math.Min(span[p + 1], span[p + 2])) < 128) dark++;
        return dark;
    }

    private const double FontSize = 40;

    private static byte[] Type1Pdf(SyntheticType1.Program program, string text, string? widths, string? encoding,
        bool symbolic = false, int? missingWidth = null, string baseFont = "TestType1")
    {
        var b = new VectorPdfBuilder();
        int stream = b.AddStream($"/Length1 {program.ClearLength} /Length2 {program.Data.Length - program.ClearLength} /Length3 0", program.Data);
        string mw = missingWidth is int m ? $"/MissingWidth {m} " : "";
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags {(symbolic ? 4 : 32)} /FontBBox [0 -200 1000 1000] /ItalicAngle 0 " +
                         $"/Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 {mw}/FontFile {stream} 0 R >>");
        string enc = encoding != null ? $"/Encoding {encoding} " : "";
        int font = b.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} {widths} {enc}/FontDescriptor {desc} 0 R >>");
        b.AddPage($"BT /F1 {FontSize} Tf 20 60 Td {text} Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 220 140]");
        return b.Build();
    }
}
