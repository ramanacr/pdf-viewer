using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using SkiaSharp;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// A whole CJK run in a font that is not embedded, in the Skia backend with the installed fonts (the
/// default font source, as on Windows and Android): the face of its language draws it, by the rule
/// the Direct2D and WPF backends use (Yu Gothic for a Japan1 font, Microsoft JhengHei for CNS1,
/// Malgun Gothic for Korea1, Microsoft YaHei for GB1). The web demo's Noto slices are chosen by the
/// same rule (<see cref="NotoFontTests"/>).
/// </summary>
public class CjkRunFaceTests
{
    // 直 and 骨 have different Japanese and Simplified Chinese forms; 漢字 is common Han.
    private const string Han = "直骨漢字";

    /// <summary>A page whose font /F0 is composite (Identity-H, CID = code point, a /ToUnicode CMap) and not embedded.</summary>
    private static byte[] Page(string baseFont, string ordering, string text)
    {
        var b = new VectorPdfBuilder();
        var cps = text.EnumerateRunes().Select(r => r.Value).ToArray();
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        var distinct = cps.Distinct().ToArray();
        cmap.Append($"{distinct.Length} beginbfchar\n");
        foreach (int cp in distinct) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream("", cmap.ToString());
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage($"BT /F0 28 Tf 20 100 Td <{string.Concat(cps.Select(c => c.ToString("X4")))}> Tj ET", $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 260 200]");
        return b.Build();
    }

    /// <summary>The faces' families and the glyphs of the page's one run, as the Skia backend resolves it with the installed fonts.</summary>
    private static async Task<(IReadOnlyList<string> Families, int[] Glyphs)> Resolve(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var res = new SkiaResources(0, new SkiaFontSource());
        using var replayer = new SkiaReplayer(res, list, null, CancellationToken.None);
        var run = list.Commands.OfType<DrawGlyphRun>().Single().Run;
        var families = replayer.ResolvedFamilies(run) ?? throw new InvalidOperationException("The run was not drawn.");
        return (families, replayer.ResolvedGlyphs(run)!.Select(g => (int)g.Gid).ToArray());
    }

    private static int[] Glyphs(string family, string text)
    {
        using var typeface = SKFontManager.Default.MatchFamily(family) ?? throw new InvalidOperationException(family);
        Assert.Equal(family, typeface.FamilyName);
        using var font = new SKFont(typeface);
        return text.Select(c => (int)font.GetGlyph(c)).ToArray();
    }

    [Theory]
    [InlineData("Japan1", "Yu Gothic")]
    [InlineData("GB1", "Microsoft YaHei")]
    [InlineData("CNS1", "Microsoft JhengHei")]
    [InlineData("Korea1", "Malgun Gothic")]
    public async Task AllHanRun_TakesTheFaceOfTheCharacterCollection(string ordering, string expected)
    {
        var (families, glyphs) = await Resolve(Page("Ryumin", ordering, Han));
        Assert.Equal(new[] { expected }, families);
        Assert.Equal(Glyphs(expected, Han), glyphs);
    }

    [Fact]
    public async Task JapaneseHan_TakesJapaneseGlyphs_NotChinese()
    {
        var (_, glyphs) = await Resolve(Page("Ryumin", "Japan1", "直骨"));
        Assert.Equal(Glyphs("Yu Gothic", "直骨"), glyphs);
        Assert.NotEqual(Glyphs("Microsoft YaHei", "直骨"), glyphs);
    }

    [Theory]
    [InlineData("SimSun", "Japan1", "SimSun")]
    [InlineData("MalgunGothic", "Japan1", "Malgun Gothic")]
    public async Task InstalledFontOfTheName_StillWins(string baseFont, string ordering, string expected) =>
        Assert.Equal(new[] { expected }, (await Resolve(Page(baseFont, ordering, Han))).Families);

    [Fact]
    public async Task LatinRun_InACjkFont_KeepsItsFace() =>
        Assert.Equal(new[] { "Arial" }, (await Resolve(Page("Ryumin", "Japan1", "Abc"))).Families);
}
