using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using PdfEngine.Vector.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// A whole CJK run in a font that is not embedded takes the installed face of its language, by the
/// rule the per-character faces use (<see cref="Fonts.CharacterFaces.CjkLanguage"/>): Yu Gothic for
/// a Japan1 font, Microsoft JhengHei for CNS1, Malgun Gothic for Korea1, Microsoft YaHei for GB1.
/// Before, the first CJK face of the generic chain (Microsoft YaHei) drew all-Han text of every
/// language, in Simplified Chinese glyph forms. Both Windows backends: Direct2D and WPF.
/// </summary>
public class CjkRunFaceTests
{
    private readonly ITestOutputHelper _output;
    public CjkRunFaceTests(ITestOutputHelper output) => _output = output;

    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // 直 and 骨 have different Japanese and Simplified Chinese forms; 漢字 is common Han.
    private const string Han = "直骨漢字";

    private static byte[] Page(string baseFont, string ordering, string text) =>
        Direct2DCharacterFallbackTests.Page(baseFont, ordering, Array.Empty<TrueTypeFontFile>(), text);

    /// <summary>The run of each line in the Direct2D backend: its faces' families and its glyphs.</summary>
    private static async Task<List<(IReadOnlyList<string> Families, int[] Glyphs)>> ResolveD2D(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var res = new D2DResources(0);
        using var replayer = new Replayer(res, list, null, analyzeOnly: true, CancellationToken.None);
        return list.Commands.OfType<DrawGlyphRun>()
            .Select(cmd => (replayer.ResolvedFamilies(cmd) ?? throw new InvalidOperationException("The run was not drawn."),
                replayer.ResolvedGlyphs(cmd)!.Select(g => (int)g.Gid).ToArray()))
            .ToList();
    }

    private static string Family(GlyphTypeface face) =>
        face.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name) ? name : face.FamilyNames.Values.First();

    /// <summary>The run of each line in the WPF backend: its faces' families and its glyphs.</summary>
    private static async Task<List<(IReadOnlyList<string> Families, int[] Glyphs)>> ResolveWpf(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var fonts = new WpfFontCache();
        var compiler = new WpfDisplayListCompiler(fonts, new WpfImageCache(1 << 20));
        var lines = new List<(IReadOnlyList<string>, int[])>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var parts = compiler.ResolveText(cmd.Run, out string? problem) ?? throw new InvalidOperationException("The run was not drawn: " + problem);
            lines.Add((parts.Select(p => Family(p.Face)).ToList(), parts.SelectMany(p => p.Indices.Select(i => (int)i)).ToArray()));
        }
        return lines;
    }

    private static int[] D2DGlyphs(string family, string text)
    {
        using var res = new D2DResources(0);
        var face = res.GetSystemFace(family, false, false) ?? throw new InvalidOperationException(family);
        return text.Select(c => (int)face.GetGlyphIndices(new[] { (uint)c })[0]).ToArray();
    }

    private static int[] WpfGlyphs(string family, string text)
    {
        using var fonts = new WpfFontCache();
        var face = fonts.GetFamily(family, false, false) ?? throw new InvalidOperationException(family);
        return text.Select(c => (int)face.CharacterToGlyphMap[c]).ToArray();
    }

    [Theory]
    [InlineData("Japan1", "Yu Gothic")]
    [InlineData("GB1", "Microsoft YaHei")]
    [InlineData("CNS1", "Microsoft JhengHei")]
    [InlineData("Korea1", "Malgun Gothic")]
    public async Task Direct2D_AllHanRun_TakesTheFaceOfTheCharacterCollection(string ordering, string expected)
    {
        var (families, glyphs) = (await ResolveD2D(Page("Ryumin", ordering, Han)))[0];
        _output.WriteLine($"{ordering}: {string.Join(", ", families)}: {string.Join(" ", glyphs)}");
        Assert.Equal(new[] { expected }, families);
        Assert.Equal(D2DGlyphs(expected, Han), glyphs);
    }

    [Theory]
    [InlineData("Japan1", "Yu Gothic")]
    [InlineData("GB1", "Microsoft YaHei")]
    [InlineData("CNS1", "Microsoft JhengHei")]
    [InlineData("Korea1", "Malgun Gothic")]
    public async Task Wpf_AllHanRun_TakesTheFaceOfTheCharacterCollection(string ordering, string expected)
    {
        var (families, glyphs) = (await ResolveWpf(Page("Ryumin", ordering, Han)))[0];
        _output.WriteLine($"{ordering}: {string.Join(", ", families)}: {string.Join(" ", glyphs)}");
        Assert.Equal(new[] { expected }, families);
        Assert.Equal(WpfGlyphs(expected, Han), glyphs);
    }

    [Fact]
    public async Task Direct2D_JapaneseHan_IsDrawnInJapaneseForms_NotChinese()
    {
        // 直 and 骨 in a Japan1 font: Yu Gothic's glyphs, not Microsoft YaHei's, and other pixels.
        var japanese = (await ResolveD2D(Page("Ryumin", "Japan1", "直骨")))[0].Glyphs;
        Assert.Equal(D2DGlyphs("Yu Gothic", "直骨"), japanese);
        Assert.NotEqual(D2DGlyphs("Microsoft YaHei", "直骨"), japanese);
        var jp = await Render(Page("Ryumin", "Japan1", "直骨"));
        var sc = await Render(Page("Ryumin", "GB1", "直骨"));
        int differ = 0;
        for (int i = 0; i < jp.Length; i += 4)
            if (Math.Abs(jp[i] - sc[i]) > 64) differ++;
        _output.WriteLine($"pixels that differ: {differ}");
        Assert.True(differ > 100, $"the Japan1 and GB1 renders are nearly the same: {differ}");
    }

    [Theory]
    [InlineData("Ryumin-Light", "Identity", "直骨", "Yu Gothic")]  // the font's name
    [InlineData("Foo", "Identity", "直の", "Yu Gothic")]           // kana in the text
    [InlineData("Foo", "Identity", "直한", "Malgun Gothic")]       // hangul in the text
    [InlineData("Big5Song", "Identity", "直骨", "Microsoft JhengHei")]
    [InlineData("Foo", "Identity", "直骨", "Microsoft YaHei")]      // nothing tells: Simplified Chinese
    public async Task AllCjkRun_WithoutACollection_TakesTheLanguageOfTheNameOrText(string baseFont, string ordering, string text, string expected)
    {
        var pdf = Page(baseFont, ordering, text);
        Assert.Equal(new[] { expected }, (await ResolveD2D(pdf))[0].Families);
        Assert.Equal(new[] { expected }, (await ResolveWpf(pdf))[0].Families);
    }

    [Theory]
    [InlineData("SimSun", "Japan1", "SimSun")]
    [InlineData("MSGothic", "GB1", "MS Gothic")]
    [InlineData("MalgunGothic", "Japan1", "Malgun Gothic")]
    public async Task InstalledFontOfTheName_StillWins(string baseFont, string ordering, string expected)
    {
        var pdf = Page(baseFont, ordering, Han);
        Assert.Equal(new[] { expected }, (await ResolveD2D(pdf))[0].Families);
        Assert.Equal(new[] { expected }, (await ResolveWpf(pdf))[0].Families);
    }

    [Fact]
    public async Task LatinRun_InACjkFont_KeepsItsFace()
    {
        var pdf = Page("Ryumin", "Japan1", "Abc");
        Assert.Equal(new[] { "Arial" }, (await ResolveD2D(pdf))[0].Families);
        Assert.Equal(new[] { "Arial" }, (await ResolveWpf(pdf))[0].Families);
    }

    [Fact]
    public async Task EmbeddedFont_IsDrawnByItsOwnGlyphs()
    {
        // Malgun Gothic embedded in a Japan1 font: its own program draws the run, glyph by index.
        var malgun = File.ReadAllBytes(Path.Combine(WindowsFonts, "malgun.ttf"));
        var metrics = TrueTypeFontFile.TryLoad(malgun)!;
        var gids = Han.Select(c => metrics.GlyphFor(c)).ToArray();
        Assert.All(gids, g => Assert.NotEqual(0, g));
        var b = new VectorPdfBuilder();
        int file = b.AddStream($"/Length1 {malgun.Length}", malgun, flate: true);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /MalgunGothic /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 /FontFile2 {file} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MalgunGothic /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /MalgunGothic /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>");
        b.AddPage($"BT /F0 28 Tf 20 100 Td <{string.Concat(gids.Select(g => g.ToString("X4")))}> Tj ET", $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 260 200]");
        var pdf = b.Build();
        var (families, glyphs) = (await ResolveD2D(pdf))[0];
        _output.WriteLine($"Direct2D: {string.Join(", ", families)}");
        Assert.DoesNotContain(Assert.Single(families), new[] { "Yu Gothic", "Microsoft YaHei" });
        Assert.Equal(gids, glyphs);
        var wpf = (await ResolveWpf(pdf))[0];
        _output.WriteLine($"WPF: {string.Join(", ", wpf.Families)}");
        Assert.DoesNotContain(Assert.Single(wpf.Families), new[] { "Yu Gothic", "Microsoft YaHei" });
        Assert.Equal(gids, wpf.Glyphs);
    }

    [Fact]
    public async Task TextExtraction_IsUnchanged()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Page("Ryumin", "Japan1", Han));
        using (var d2d = new Direct2DVectorRenderer())
        using (await d2d.RenderDisplayListAsync(await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 72 })) { }
        var page = PdfTextExtractor.LoadPage(doc, 1);
        Assert.Contains(Han, page.Text);
    }

    /// <summary>The page's Direct2D render at 96 dpi, as BGRA bytes.</summary>
    private static async Task<byte[]> Render(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        using var d2d = new Direct2DVectorRenderer();
        using var page = await d2d.RenderDisplayListAsync(await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 96 });
        return page.Pixels.ToArray();
    }
}
