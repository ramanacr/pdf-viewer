using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// The WPF backend (used when no Direct3D device exists) draws a substitute's run that no single
/// installed face has as the Direct2D backend draws it (<see cref="Direct2DCharacterFallbackTests"/>):
/// the characters the substitute lacks come from the installed face for their script, each face's
/// words shaped with its own tables, by the same engine code (<see cref="CharacterComposition"/>).
/// </summary>
public class WpfCharacterFallbackTests
{
    private readonly ITestOutputHelper _output;
    public WpfCharacterFallbackTests(ITestOutputHelper output) => _output = output;

    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static TrueTypeFontFile Font(string file) =>
        TrueTypeFontFile.TryLoad(File.ReadAllBytes(Path.Combine(WindowsFonts, file))) ?? throw new InvalidOperationException(file);

    private static readonly Lazy<TrueTypeFontFile> Arial = new(() => Font("arial.ttf"));
    private static readonly Lazy<TrueTypeFontFile> Nirmala = new(() => Font("Nirmala.ttc"));

    private static byte[] Page(string baseFont, string ordering, TrueTypeFontFile[] metrics, params string[] lines) =>
        Direct2DCharacterFallbackTests.Page(baseFont, ordering, metrics, lines);

    /// <summary>
    /// A page as <see cref="Direct2DCharacterFallbackTests.Page"/> builds it (a composite font that is
    /// not embedded, CID = code point), with the characters <paramref name="cps"/> and its own content.
    /// </summary>
    private static byte[] PageWithContent(string baseFont, string ordering, TrueTypeFontFile[] metrics, int[] cps, string content, string mediaBox)
    {
        var b = new VectorPdfBuilder();
        cps = cps.Distinct().Order().ToArray();
        var cmap = new System.Text.StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        cmap.Append($"{cps.Length} beginbfchar\n");
        foreach (int cp in cps) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream("", cmap.ToString());
        string widths = string.Join(" ", cps.Select(cp => $"{cp} [{Width(metrics, cp)}]"));
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage(content, $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: mediaBox);
        return b.Build();
    }

    private static int Width(TrueTypeFontFile[] metrics, int cp)
    {
        foreach (var m in metrics)
            if (m.GlyphFor(cp) != 0) return (int)Math.Round(m.Advance(m.GlyphFor(cp)));
        return 1000; // a CJK character's full width
    }

    /// <summary>Right-to-left text as a producer lays it out: the characters reversed, left to right.</summary>
    private static string Seen(string reading) => new(reading.Reverse().ToArray());

    private static string Family(GlyphTypeface face) =>
        face.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name) ? name : face.FamilyNames.Values.First();

    private static WpfDisplayListCompiler Compiler(WpfFontCache fonts) => new(fonts, new WpfImageCache(1 << 20));

    /// <summary>Each line's run as the WPF backend resolves it: its faces' families and its glyphs, part after part.</summary>
    private static async Task<List<(IReadOnlyList<string> Families, int[] Glyphs)>> Resolve(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var fonts = new WpfFontCache();
        var compiler = Compiler(fonts);
        var lines = new List<(IReadOnlyList<string>, int[])>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var parts = compiler.ResolveText(cmd.Run, out string? problem) ?? throw new InvalidOperationException("The run was not drawn: " + problem);
            lines.Add((parts.Select(p => Family(p.Face)).ToList(), parts.SelectMany(p => p.Indices.Select(i => (int)i)).ToArray()));
        }
        return lines;
    }

    [Fact]
    public async Task HebrewAndDevanagari_UnderArial_TakeDevanagariFromNirmalaUi_Shaped()
    {
        // Before, the WPF backend classified the run (Arial lacks the Devanagari) and drew none of it.
        var n = Nirmala.Value;
        int ka = n.GlyphFor(0x0915), iSign = n.GlyphFor(0x093F), virama = n.GlyphFor(0x094D), ssa = n.GlyphFor(0x0937);
        var pdf = Page("Arial", "Identity", new[] { Arial.Value, n }, Seen("שלום עולם") + " कि क्ष");
        var (families, glyphs) = (await Resolve(pdf))[0];
        _output.WriteLine($"{string.Join(", ", families)}: {string.Join(" ", glyphs)}");
        Assert.Equal(new[] { "Arial", "Nirmala UI" }, families);
        var a = Arial.Value;
        Assert.Equal(Seen("שלום עולם").Where(c => c != ' ').Select(c => a.GlyphFor(c)), glyphs.Take(8));
        var devanagari = glyphs.Skip(8).ToArray();
        Assert.Equal(3, devanagari.Length);
        Assert.Equal(new[] { iSign, ka }, devanagari[..2]);
        Assert.DoesNotContain(devanagari[2], new[] { ka, virama, ssa });
    }

    [Theory]
    [InlineData("Japan1", "Yu Gothic")]
    [InlineData("GB1", "Microsoft YaHei")]
    [InlineData("Korea1", "Malgun Gothic")]
    public async Task Han_ComesFromTheFaceOfTheCharacterCollection(string ordering, string expected)
    {
        var pdf = Page("Ryumin", ordering, new[] { Nirmala.Value }, "कि क्ष 漢");
        var (families, glyphs) = (await Resolve(pdf))[0];
        _output.WriteLine($"{ordering}: {string.Join(", ", families)}: {string.Join(" ", glyphs)}");
        Assert.Equal(new[] { "Nirmala UI", expected }, families);
        Assert.Equal(4, glyphs.Length); // i sign, ka, ksha, and the Han glyph
        Assert.NotEqual(0, glyphs[3]);
    }

    [Fact]
    public async Task RunOneFaceHas_StaysOneFace()
    {
        // Latin by name: Arial alone, as before. Devanagari alone: Nirmala UI alone.
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /H 20 Tf 10 100 Td (office AV) Tj ET", $"<< /Font << /H {helv} 0 R >> >>", mediaBox: "[0 0 200 200]");
        var latin = (await Resolve(b.Build()))[0];
        Assert.Equal(new[] { "Arial" }, latin.Families);
        Assert.Equal("officeAV".Select(c => Arial.Value.GlyphFor(c)), latin.Glyphs);
        Assert.Equal(new[] { "Nirmala UI" }, (await Resolve(Page("Arial", "Identity", new[] { Nirmala.Value }, "कि क्ष")))[0].Families);
    }

    [Fact]
    public void CharacterFace_WithoutAScriptRule_ComesFromTheBroadFaces()
    {
        using var fonts = new WpfFontCache();
        // Ethiopic and Cherokee: no script rule names a face; a broad face has them (Ebrima, Gadugi).
        foreach (int cp in new[] { 0x1200, 0x13A0 })
        {
            var face = fonts.GetCharacterFace(cp, "sc", bold: false, italic: false);
            Assert.NotNull(face);
            Assert.True(face!.CharacterToGlyphMap.ContainsKey(cp));
            _output.WriteLine($"U+{cp:X4}: {Family(face)}");
        }
        // A script rule's face: Devanagari in Nirmala UI, the same face object every time.
        var nirmala = fonts.GetCharacterFace(0x0915, "sc", false, false);
        Assert.Same(nirmala, fonts.GetCharacterFace(0x0915, "sc", false, false));
        Assert.Same(nirmala, fonts.GetFamily("Nirmala UI", false, false));
        // Han by the text's CJK language.
        Assert.Equal("Yu Gothic", Family(fonts.GetCharacterFace(0x6F22, "jp", false, false)!));
        Assert.Equal("Malgun Gothic", Family(fonts.GetCharacterFace(0x6F22, "kr", false, false)!));
        // A private-use code no face has.
        Assert.Null(fonts.GetCharacterFace(0x10FFFD, "sc", false, false));
    }

    [Fact]
    public async Task ArabicWord_ThePrimaryHasInPart_IsDrawnWholeFromOneFace_Shaped()
    {
        // As in the Direct2D test: Arial "lacks" the word's last glyph (as seen, left to right), and
        // Segoe UI, the face for Arabic, has them all.
        string reading = "سمير";
        using var doc = await PdfVectorDocument.OpenAsync(Page("Arial", "Identity", new[] { Arial.Value }, Seen(reading)));
        var run = (await doc.GetPageDisplayListAsync(1)).Commands.OfType<DrawGlyphRun>().Single().Run;
        Assert.Equal(4, run.Glyphs.Count);
        using var fonts = new WpfFontCache();
        var compiler = Compiler(fonts);
        var arial = fonts.GetFamily("Arial", false, false)!;
        var segoe = fonts.GetCharacterFace(0x0633, "sc", false, false)!;
        Assert.Same(fonts.GetFamily("Segoe UI", false, false), segoe);
        var segoeFile = fonts.GetShapingFont(segoe)!;
        bool Has(GlyphTypeface face, int k) =>
            !(ReferenceEquals(face, arial) && k == 3) && face.CharacterToGlyphMap.ContainsKey(char.ConvertToUtf32(run.Glyphs[k].Unicode!, 0));
        GlyphTypeface?[] Split() => new GlyphTypeface?[] { arial, arial, arial, segoe };

        // Cut at the face change: Segoe UI draws the one letter Arial lacks, alone and so isolated.
        var split = compiler.ComposeParts(run, Split(), arial, false, 1, 1, shapeWords: true)!;
        Assert.Equal(2, split.Count);
        Assert.Equal(new[] { (ushort)segoeFile.GlyphFor(run.Glyphs[3].Unicode![0]) }, split[1].Indices);

        // Kept whole: the word goes to Segoe UI and is shaped there, in its joined forms.
        var owners = Split();
        CharacterComposition.KeepWordsWhole(run, owners, arial, Has, k => fonts.GetCharacterFace(char.ConvertToUtf32(run.Glyphs[k].Unicode!, 0), "sc", false, false));
        Assert.All(owners, o => Assert.Same(segoe, o));
        var part = Assert.Single(compiler.ComposeParts(run, owners, arial, false, 1, 1, shapeWords: true)!);
        Assert.Same(segoe, part.Face);
        var word = Assert.Single(SubstituteShaper.Words(run)!);
        var shaped = SubstituteShaper.ShapeWord(segoeFile, run, word, 1)!.Select(g => g.Gid).ToArray();
        Assert.Equal(shaped, part.Indices);
        var isolated = reading.Select(c => (ushort)segoeFile.GlyphFor(c)).ToArray();
        Assert.Equal(4, part.Indices.Count);
        Assert.DoesNotContain(part.Indices, isolated.Contains);
    }

    /// <summary>
    /// A run of Hebrew in Arial, Devanagari from Nirmala UI and Han from Yu Gothic: the WPF render is
    /// close to the Direct2D one, while a WPF render without the composed parts is far from it.
    /// </summary>
    [Fact]
    public async Task MixedRun_DrawsLikeDirect2D()
    {
        var metrics = new[] { Arial.Value, Nirmala.Value };
        string[] lines = { Seen("שלום") + " कि", "क्ष 漢字" };
        byte[] pdf = LargePage(lines);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = 96 };
        using var d2d = new Direct2DVectorRenderer();
        using var wpf = new WindowsVectorRenderer();
        using var a = await d2d.RenderDisplayListAsync(list, request);
        using var w = await wpf.RenderDisplayListAsync(list, request);
        var (mean, bad) = DifferentialRenderingTests.Compare(w, a);

        // Only the Hebrew drawn: still more than before, when the WPF backend classified each run
        // (its substitute lacks a glyph) and drew none of it.
        using var hebrewDoc = await PdfVectorDocument.OpenAsync(LargePage(new[] { Seen("שלום"), "" }));
        using var h = await wpf.RenderDisplayListAsync(await hebrewDoc.GetPageDisplayListAsync(1), request);
        var (meanBefore, badBefore) = DifferentialRenderingTests.Compare(h, a);
        _output.WriteLine($"wpf vs d2d mixed: mean={mean:F2} bad={bad:P2}; hebrew only: mean={meanBefore:F2} bad={badBefore:P2}");
        // Measured: mean 0.73 and 0.28 % of pixels off by more than 64 (each line alone: 0.32 and 0.39,
        // the two rasterizers' edges); Hebrew only: mean 8.2, 3.5 %.
        Assert.True(meanBefore > 3, $"the parts add little ink: {meanBefore:F2}");
        Assert.True(mean <= 0.9, $"mean {mean:F2}");
        Assert.True(bad <= 0.004, $"{bad:P2} off");

        // The same faces and glyphs, part after part, as the Direct2D backend draws.
        using var res = new D2DResources(0);
        using var replayer = new Replayer(res, list, null, analyzeOnly: true, System.Threading.CancellationToken.None);
        using var fonts = new WpfFontCache();
        var compiler = Compiler(fonts);
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var parts = compiler.ResolveText(cmd.Run, out _)!;
            Assert.Equal(replayer.ResolvedFamilies(cmd)!, parts.Select(p => Family(p.Face)));
            Assert.Equal(replayer.ResolvedGlyphs(cmd)!.Select(g => g.Gid), parts.SelectMany(p => p.Indices));
            Assert.Equal(2, parts.Count);
        }

        byte[] LargePage(string[] text)
        {
            var cps = lines.SelectMany(l => l.EnumerateRunes().Select(r => r.Value)).ToArray();
            string content = string.Concat(text.Select((l, i) => $"BT /F0 72 Tf 20 {280 - 120 * i} Td <{string.Concat(l.EnumerateRunes().Select(r => r.Value.ToString("X4")))}> Tj ET\n"));
            return PageWithContent("Arial", "Japan1", metrics, cps, content, "[0 0 400 400]");
        }
    }

    [Fact]
    public async Task MixedRun_KeepsTheCharacters_FallbackChangesDrawingOnly()
    {
        var pdf = Page("Arial", "Identity", new[] { Arial.Value, Nirmala.Value }, Seen("שלום עולם") + " कि क्ष");
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        using (var wpf = new WindowsVectorRenderer())
        using (await wpf.RenderDisplayListAsync(await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 72 })) { }
        var page = PdfTextExtractor.LoadPage(doc, 1);
        Assert.Contains("क्ष", page.Text);
        Assert.Single(PdfTextFind.FindAll(page, "क्ष", matchCase: true, wholeWord: false, consecutive: false));
    }
}
