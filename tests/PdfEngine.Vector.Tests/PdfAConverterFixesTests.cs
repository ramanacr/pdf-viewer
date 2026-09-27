using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// What the PDF/A converter fixes beyond embedding and metadata: text codes whose glyph the font
/// program lacks are taken out with every other glyph kept in place; codes that share a glyph but
/// not its width get a copy of the glyph; and PDF/A-1b, for documents without transparency.
/// </summary>
public class PdfAConverterFixesTests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static TrueTypeFontFile Arial()
    {
        var catalog = SystemFontCatalog.Installed;
        var face = catalog.FindFamily("Arial", false, false) ?? throw new InvalidOperationException("Arial is not installed.");
        return catalog.Load(face)!;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>An embedded TrueType font (a subset of Arial with <paramref name="glyphs"/>) with the given widths from <paramref name="first"/>.</summary>
    private static int SimpleFont(VectorPdfBuilder b, int[] glyphs, int first, double[] widths, string encoding)
    {
        byte[] program = Arial().Subset(glyphs);
        int file = b.AddStream($"/Length1 {program.Length}", program, flate: true);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 32 /FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {file} 0 R >>");
        return b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Arial /FirstChar {first} /LastChar {first + widths.Length - 1} " +
                     $"/Widths [{string.Join(" ", widths.Select(Num))}] /Encoding {encoding} /FontDescriptor {descriptor} 0 R >>");
    }

    private static async Task<PdfAConversionResult> Convert(byte[] pdf, PdfAFlavour flavour)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavour = flavour });
    }

    private static async Task<PdfAReport> Validate(byte[] pdf, PdfAFlavour flavour)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfAValidator.Validate(doc, pdf, flavour);
    }

    /// <summary>Every glyph the vector engine draws on page 1: text and baseline origin.</summary>
    private static async Task<List<(string Ch, double X, double Y)>> Glyphs(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var result = new List<(string, double, double)>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
            foreach (var g in cmd.Run.Glyphs)
            {
                var p = cmd.Run.TextToPage.Transform(g.OffsetX, g.OffsetY);
                result.Add((g.Unicode ?? "?", p.X, p.Y));
            }
        return result;
    }

    private static void SameGlyphs(List<(string Ch, double X, double Y)> expected, List<(string Ch, double X, double Y)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var pool = actual.ToList();
        foreach (var g in expected)
        {
            int i = pool.FindIndex(a => a.Ch == g.Ch && Math.Abs(a.X - g.X) < 0.01 && Math.Abs(a.Y - g.Y) < 0.01);
            Assert.True(i >= 0, $"The glyph {g.Ch} at ({g.X:0.##}, {g.Y:0.##}) moved or is gone.");
            pool.RemoveAt(i);
        }
    }

    // ------------------------------------------------------------------ glyphs the program lacks

    [Fact]
    public async Task CodesWhoseGlyphIsMissing_AreTakenOut_AndEveryOtherGlyphKeepsItsPlace()
    {
        var file = Arial();
        int a = file.GlyphFor('A'), bGlyph = file.GlyphFor('B');
        byte[] program = file.Subset(new[] { a, bGlyph });
        var b = new VectorPdfBuilder();
        int fontFile = b.AddStream($"/Length1 {program.Length}", program, flate: true);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 32 /FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {fontFile} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                        $"/FontDescriptor {descriptor} 0 R /DW 750 /W [{a} [{Num(file.Advance(a))} {Num(file.Advance(bGlyph))}]] /CIDToGIDMap /Identity >>");
        // CID F000 is past the program's last glyph: it draws .notdef. The text is labelled so the test can tell the glyphs apart.
        string A = a.ToString("X4"), B = bGlyph.ToString("X4");
        int toUnicode = b.AddStream("", "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T def 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
                                        $"3 beginbfchar <{A}> <0041> <{B}> <0042> <F000> <E123> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end");
        int font = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Arial /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 400 400] /Resources << /Font << /F1 {font} 0 R >> >>",
            $"BT /F1 16 Tf 2 Tc 40 40 Td <{A}F000{B}> Tj ET");
        int appearance = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 180 40] /Resources << /Font << /F1 {font} 0 R >> >>",
            $"BT /F1 12 Tf 5 10 Td [<{A}> -120 <F000F000> 40 <{B}>] TJ ET");
        int annot = b.Add($"<< /Type /Annot /Subtype /Square /Rect [200 300 380 340] /F 4 /AP << /N {appearance} 0 R >> >>");
        b.AddPage(
            $"BT /F1 20 Tf 40 340 Td <{A}F000{B}> Tj ET\n" +
            $"BT /F1 20 Tf 3 Tc 40 300 Td [<{A}> -250 <F000F000{B}>] TJ ET\n" +
            $"BT /F1 20 Tf 150 Tz 12 TL 40 260 Td <{A}F000{B}> Tj <F000{A}> ' ET\n" +
            "q 1 0 0 1 0 100 cm /Fm1 Do Q\n" +
            $"BT /F1 20 Tf 3 Tr 40 120 Td <{A}F000{B}> Tj ET\n",
            $"<< /Font << /F1 {font} 0 R >> /XObject << /Fm1 {form} 0 R >> >>", mediaBox: "[0 0 400 400]", extra: $"/Annots [{annot} 0 R]");
        byte[] pdf = b.Build();
        Assert.Contains((await Validate(pdf, PdfAFlavour.A2b)).Issues, i => i.RuleId == "6.2.11.8-1");
        Assert.Contains((await Validate(pdf, PdfAFlavour.A1b)).Issues, i => i.RuleId == "6.3.5-1");

        var result = await Convert(pdf, PdfAFlavour.A2b);
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        // The page shows 1 + 2 + 2 + 1 (the form) missing glyphs and the appearance 2; the invisible line keeps its text.
        Assert.Contains(result.Changes, c => c.StartsWith("Took out 8 text glyph(s)", StringComparison.Ordinal));

        var before = await Glyphs(pdf);
        Assert.Contains(before, g => g.Ch == "");
        var after = await Glyphs(result.Bytes);
        SameGlyphs(before.Where(g => g.Ch != "" || Math.Abs(g.Y - 120) < 0.5).ToList(), after);
        Assert.Contains(after, g => g.Ch == "B" && Math.Abs(g.Y - 300) < 0.5);
    }
    // ------------------------------------------------------------------ one glyph, two widths

    [Fact]
    public async Task CodesSharingAGlyphWithDifferentWidths_GetACopyOfTheGlyph()
    {
        var file = Arial();
        int hyphen = file.GlyphFor('-');
        // WinAnsi codes 45 and 173 are both /hyphen; the document gives the second no width.
        var widths = new double[173 - 45 + 1];
        widths[0] = file.Advance(hyphen);
        var b = new VectorPdfBuilder();
        int font = SimpleFont(b, new[] { hyphen }, 45, widths, "/WinAnsiEncoding");
        b.AddPage("BT /F1 20 Tf 40 100 Td (-\\255-\\255-) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 200]");
        byte[] pdf = b.Build();
        Assert.Contains((await Validate(pdf, PdfAFlavour.A2b)).Issues, i => i.RuleId == "6.2.11.5-1");

        var result = await Convert(pdf, PdfAFlavour.A2b);
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        Assert.Contains(result.Changes, c => c.StartsWith("Gave codes that share a glyph", StringComparison.Ordinal));
        SameGlyphs(await Glyphs(pdf), await Glyphs(result.Bytes));

        // Code 173 now shows its own copy of the hyphen, with no advance; its text is still a hyphen.
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var fontDict = (PdfDictionary)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(doc.PageTree.Pages[0].Resources["Font"])!)["F1"])!;
        var pdfFont = new PdfFontResolver(doc.Resolver, PdfSecurityLimits.Default).ResolveFont(fontDict, "F1");
        var prepared = FontProgramPreparer.Prepare(pdfFont)!;
        var program = TrueTypeFontFile.TryLoad(prepared.Sfnt)!;
        int plain = prepared.GetGlyphId(45, 45), soft = prepared.GetGlyphId(173, 173);
        Assert.NotEqual(plain, soft);
        Assert.Equal(file.Advance(hyphen), program.Advance(plain), 0);
        Assert.Equal(0, program.Advance(soft), 0);
        Assert.Equal("-", pdfFont.MapToUnicode(173));
        Assert.Equal("-", pdfFont.MapToUnicode(45));
    }

    [Fact]
    public async Task CidsSharingAGlyphWithDifferentWidths_GetACopyThroughTheCidToGidMap()
    {
        var file = Arial();
        int a = file.GlyphFor('A');
        byte[] program = file.Subset(new[] { a });
        var b = new VectorPdfBuilder();
        int fontFile = b.AddStream($"/Length1 {program.Length}", program, flate: true);
        // CIDs 1 and 2 both show the A, the first at its width, the second narrower.
        byte[] map = { 0, 0, (byte)(a >> 8), (byte)a, (byte)(a >> 8), (byte)a };
        int cidMap = b.AddStream("", map);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 4 /FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {fontFile} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                        $"/FontDescriptor {descriptor} 0 R /W [1 [{Num(file.Advance(a))} 400]] /CIDToGIDMap {cidMap} 0 R >>");
        int font = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Arial /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>");
        b.AddPage("BT /F1 20 Tf 40 100 Td <000100020001> Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 200]");
        byte[] pdf = b.Build();
        Assert.Contains((await Validate(pdf, PdfAFlavour.A2b)).Issues, i => i.RuleId == "6.2.11.5-1");

        var result = await Convert(pdf, PdfAFlavour.A2b);
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        SameGlyphs(await Glyphs(pdf), await Glyphs(result.Bytes));
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var fontDict = (PdfDictionary)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(doc.PageTree.Pages[0].Resources["Font"])!)["F1"])!;
        var pdfFont = new PdfFontResolver(doc.Resolver, PdfSecurityLimits.Default).ResolveFont(fontDict, "F1");
        var prepared = FontProgramPreparer.Prepare(pdfFont)!;
        var copy = TrueTypeFontFile.TryLoad(prepared.Sfnt)!;
        Assert.Equal(a, prepared.GetGlyphId(1, 1));
        Assert.NotEqual(a, prepared.GetGlyphId(2, 2));
        Assert.Equal(400, copy.Advance(prepared.GetGlyphId(2, 2)), 0);
    }

    // ------------------------------------------------------------------ PDF/A-1b

    /// <summary>An opaque document with what PDF/A-1 does not have: layers (all shown), an attachment, a script, a later PDF version.</summary>
    private static byte[] Opaque(string extraPage = "", Func<VectorPdfBuilder, string>? extraResources = null)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int js = b.Add("<< /S /JavaScript /JS (app.alert\\(1\\)) >>");
        int layer = b.Add("<< /Type /OCG /Name (Notes) >>");
        int attachment = b.AddStream("/Type /EmbeddedFile", "attached");
        int spec = b.Add($"<< /Type /Filespec /F (notes.txt) /UF (notes.txt) /EF << /F {attachment} 0 R >> >>");
        int info = b.Add("<< /Title (Minutes) /Author (The clerk) /Producer (A word processor) /CreationDate (D:20240102030405+01'00') >>");
        string resources = extraResources?.Invoke(b) ?? string.Empty;
        b.AddPage("0.2 0.4 0.8 rg 40 500 200 100 re f /OC /L1 BDC BT /F1 18 Tf 0 0 0 rg 40 700 Td (Archive me) Tj ET EMC " + extraPage,
            $"<< /Font << /F1 {font} 0 R >> /Properties << /L1 {layer} 0 R >> {resources} >>", mediaBox: "[0 0 612 792]");
        b.WithCatalog($"/Version /1.7 /OpenAction {js} 0 R /OCProperties << /OCGs [{layer} 0 R] /D << /Order [{layer} 0 R] >> >> " +
                      $"/Names << /EmbeddedFiles << /Names [(notes.txt) {spec} 0 R] >> >>");
        b.WithTrailer($"/Info {info} 0 R");
        return b.Build();
    }
    [Fact]
    public async Task AnOpaqueDocument_BecomesValidPdfA1b()
    {
        byte[] pdf = Opaque();
        var result = await Convert(pdf, PdfAFlavour.A1b);
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        Assert.StartsWith("%PDF-1.4\n", Encoding.Latin1.GetString(result.Bytes, 0, 9));
        Assert.Contains(result.Changes, c => c.StartsWith("Removed optional content", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.StartsWith("Removed embedded files", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("PDF/A-1B identification", StringComparison.Ordinal));

        var again = await Validate(result.Bytes, PdfAFlavour.A1b);
        Assert.Equal(PdfAFlavour.A1b, again.Claimed);
        Assert.True(again.IsCompliant, string.Join("\n", again.Issues));
        Assert.DoesNotContain("/JavaScript", Encoding.Latin1.GetString(result.Bytes));
        // The text that was in the (shown) layer is still there.
        Assert.Contains(await Glyphs(result.Bytes), g => g.Ch == "A" && Math.Abs(g.X - 40) < 0.01 && Math.Abs(g.Y - 700) < 0.01);
    }

    [Theory]
    [InlineData("alpha", "constant alpha below 1")]
    [InlineData("blend", "blend modes other than Normal")]
    [InlineData("mask", "soft masks")]
    [InlineData("group", "transparency groups")]
    public async Task ADocumentWithTransparency_IsRefusedForPdfA1b_WithTheReason(string what, string kind)
    {
        byte[] pdf = Opaque(what == "group" ? "/Fm1 Do" : "/GS1 gs 1 0 0 rg 300 300 100 100 re f", b =>
        {
            int group = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency >>", "0 0 1 rg 0 0 50 50 re f");
            return what switch
            {
                "alpha" => "/ExtGState << /GS1 << /Type /ExtGState /ca 0.5 >> >>",
                "blend" => "/ExtGState << /GS1 << /Type /ExtGState /BM /Multiply >> >>",
                "mask" => $"/ExtGState << /GS1 << /Type /ExtGState /SMask << /S /Luminosity /G {group} 0 R >> >> >>",
                _ => $"/XObject << /Fm1 {group} 0 R >>",
            };
        });
        var refused = await Assert.ThrowsAsync<PdfAConversionRefusedException>(() => Convert(pdf, PdfAFlavour.A1b));
        Assert.Contains("does not allow transparency", refused.Message);
        Assert.Contains(kind, refused.Message);
    }
    [Fact]
    public async Task ADocumentWithHiddenLayers_IsRefusedForPdfA1b()
    {
        var b = new VectorPdfBuilder();
        int layer = b.Add("<< /Type /OCG /Name (Draft) >>");
        b.AddPage("/OC /L1 BDC 1 0 0 rg 40 40 100 100 re f EMC", $"<< /Properties << /L1 {layer} 0 R >> >>", mediaBox: "[0 0 200 200]");
        b.WithCatalog($"/OCProperties << /OCGs [{layer} 0 R] /D << /OFF [{layer} 0 R] /Order [{layer} 0 R] >> >>");
        byte[] pdf = b.Build();
        var refused = await Assert.ThrowsAsync<PdfAConversionRefusedException>(() => Convert(pdf, PdfAFlavour.A1b));
        Assert.Contains("hidden", refused.Message);
    }
}
