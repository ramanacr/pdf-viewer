using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Vortice.DirectWrite;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// A substitute's run with characters that no single installed face has: the Direct2D backend draws
/// the characters the substitute lacks from the installed face for their script (Nirmala UI for
/// Devanagari, the CJK face of the font's character collection for Han), each face's words shaped
/// with its own tables, as the Skia backend's per-character faces draw them in the web demo.
/// </summary>
public class Direct2DCharacterFallbackTests
{
    private readonly ITestOutputHelper _output;
    public Direct2DCharacterFallbackTests(ITestOutputHelper output) => _output = output;

    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static TrueTypeFontFile Font(string file) =>
        TrueTypeFontFile.TryLoad(File.ReadAllBytes(Path.Combine(WindowsFonts, file))) ?? throw new InvalidOperationException(file);

    private static readonly Lazy<TrueTypeFontFile> Arial = new(() => Font("arial.ttf"));
    private static readonly Lazy<TrueTypeFontFile> Nirmala = new(() => Font("Nirmala.ttc"));

    /// <summary>
    /// A page whose font /F0 is composite (Identity-H, CID = code point, a /ToUnicode CMap), not
    /// embedded, named <paramref name="baseFont"/> and of the character collection
    /// <paramref name="ordering"/>; each character's /W width is its advance in the first of
    /// <paramref name="metrics"/> that has it. One line per entry, the characters in the order seen.
    /// </summary>
    internal static byte[] Page(string baseFont, string ordering, TrueTypeFontFile[] metrics, params string[] lines)
    {
        var b = new VectorPdfBuilder();
        var cps = lines.SelectMany(l => l.EnumerateRunes().Select(r => r.Value)).Distinct().Order().ToArray();
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        cmap.Append($"{cps.Length} beginbfchar\n");
        foreach (int cp in cps) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream("", cmap.ToString());
        string widths = string.Join(" ", cps.Select(cp => $"{cp} [{Width(metrics, cp)}]"));
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        string content = string.Concat(lines.Select((l, i) => $"BT /F0 28 Tf 20 {150 - 45 * i} Td <{Hex(l)}> Tj ET\n"));
        b.AddPage(content, $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 260 200]");
        return b.Build();
    }

    private static string Hex(string text) => string.Concat(text.EnumerateRunes().Select(r => r.Value.ToString("X4")));

    private static int Width(TrueTypeFontFile[] metrics, int cp)
    {
        foreach (var m in metrics)
            if (m.GlyphFor(cp) != 0) return (int)Math.Round(m.Advance(m.GlyphFor(cp)));
        return 1000; // a CJK character's full width
    }

    /// <summary>Right-to-left text as a producer lays it out: the characters reversed, left to right.</summary>
    private static string Seen(string reading) => new(reading.Reverse().ToArray());

    /// <summary>Each line's run as the Direct2D backend resolves it: its faces' families and its glyphs, part after part.</summary>
    private static async Task<List<(IReadOnlyList<string> Families, int[] Glyphs)>> Resolve(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var res = new D2DResources(0);
        using var replayer = new Replayer(res, list, null, analyzeOnly: true, CancellationToken.None);
        var lines = new List<(IReadOnlyList<string>, int[])>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var families = replayer.ResolvedFamilies(cmd) ?? throw new InvalidOperationException("The run was not drawn.");
            var glyphs = replayer.ResolvedGlyphs(cmd)!.Select(g => (int)g.Gid).ToArray();
            lines.Add((families, glyphs));
        }
        return lines;
    }

    [Fact]
    public async Task HebrewAndDevanagari_UnderArial_TakeDevanagariFromNirmalaUi_Shaped()
    {
        // Arial has the Hebrew but no Devanagari, Nirmala UI the Devanagari but no Hebrew: no face of
        // the chain has the whole run. Before, Arial drew the Hebrew and the Devanagari was left out.
        var n = Nirmala.Value;
        int ka = n.GlyphFor(0x0915), iSign = n.GlyphFor(0x093F), virama = n.GlyphFor(0x094D), ssa = n.GlyphFor(0x0937);
        var pdf = Page("Arial", "Identity", new[] { Arial.Value, n }, Seen("שלום עולם") + " कि क्ष");
        var (families, glyphs) = (await Resolve(pdf))[0];
        _output.WriteLine($"{string.Join(", ", families)}: {string.Join(" ", glyphs)}");
        Assert.Equal(new[] { "Arial", "Nirmala UI" }, families);
        var a = Arial.Value;
        Assert.Equal(Seen("שלום עולם").Where(c => c != ' ').Select(c => a.GlyphFor(c)), glyphs.Take(8));
        // The Nirmala UI part, shaped: the i sign before its consonant, then the ksha conjunct as one glyph.
        var devanagari = glyphs.Skip(8).ToArray();
        Assert.Equal(3, devanagari.Length);
        Assert.Equal(new[] { iSign, ka }, devanagari[..2]);
        Assert.DoesNotContain(devanagari[2], new[] { ka, virama, ssa });
    }

    [Fact]
    public async Task MixedRun_DrawsTheCharactersTheSubstituteLacks()
    {
        // Ink: the Devanagari adds what the Hebrew alone draws (before, the two renders were the same).
        var metrics = new[] { Arial.Value, Nirmala.Value };
        long hebrew = await Ink(Page("Arial", "Identity", metrics, Seen("שלום עולם")));
        long mixed = await Ink(Page("Arial", "Identity", metrics, Seen("שלום עולם") + " कि क्ष"));
        _output.WriteLine($"ink: hebrew={hebrew} mixed={mixed}");
        Assert.True(hebrew > 50, "no Hebrew ink");
        Assert.True(mixed > hebrew * 3 / 2, $"the Devanagari drew little or nothing: {mixed} vs {hebrew}");
    }

    [Theory]
    [InlineData("Japan1", "Yu Gothic")]
    [InlineData("GB1", "Microsoft YaHei")]
    [InlineData("Korea1", "Malgun Gothic")]
    public async Task Han_ComesFromTheFaceOfTheCharacterCollection(string ordering, string expected)
    {
        // Mostly Devanagari, so Nirmala UI draws the run and the Han character is the one it lacks.
        var pdf = Page("Ryumin", ordering, new[] { Nirmala.Value }, "कि क्ष 漢");
        var (families, glyphs) = (await Resolve(pdf))[0];
        _output.WriteLine($"{ordering}: {string.Join(", ", families)}: {string.Join(" ", glyphs)}");
        Assert.Equal(new[] { "Nirmala UI", expected }, families);
        Assert.Equal(4, glyphs.Length); // i sign, ka, ksha, and the Han glyph
        Assert.NotEqual(0, glyphs[3]);
    }

    [Fact]
    public async Task Han_UnderJapan1_DrawsInk()
    {
        var metrics = new[] { Nirmala.Value };
        long without = await Ink(Page("Ryumin", "Japan1", metrics, "कि क्ष"));
        long with = await Ink(Page("Ryumin", "Japan1", metrics, "कि क्ष 漢字"));
        _output.WriteLine($"ink: without={without} with={with}");
        Assert.True(with > without + 100, $"the Han drew little or nothing: {with} vs {without}");
    }

    [Fact]
    public async Task RunOneFaceHas_StaysOneFace()
    {
        // Latin by name: Arial alone, as before. Devanagari alone: Nirmala UI alone, as before.
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /H 20 Tf 10 100 Td (office AV) Tj ET", $"<< /Font << /H {helv} 0 R >> >>", mediaBox: "[0 0 200 200]");
        Assert.Equal(new[] { "Arial" }, (await Resolve(b.Build()))[0].Families);
        Assert.Equal(new[] { "Nirmala UI" }, (await Resolve(Page("Arial", "Identity", new[] { Nirmala.Value }, "कि क्ष")))[0].Families);
    }

    [Fact]
    public void CharacterFace_WithoutAScriptRule_ComesFromTheSystemFallback()
    {
        using var res = new D2DResources(0);
        // Ethiopic and Cherokee: no script rule names a face; DirectWrite's system fallback does.
        foreach (int cp in new[] { 0x1200, 0x13A0 })
        {
            var face = res.GetCharacterFace(cp, "sc", bold: false, italic: false);
            Assert.NotNull(face);
            Assert.NotEqual(0, face!.GetGlyphIndices(new[] { (uint)cp })[0]);
        }
        // A script rule's face: Devanagari in Nirmala UI, the same face object every time.
        var nirmala = res.GetCharacterFace(0x0915, "sc", false, false);
        Assert.Same(nirmala, res.GetCharacterFace(0x0915, "sc", false, false));
        Assert.Same(nirmala, res.GetSystemFace("Nirmala UI", false, false));
        // A private-use code no face has.
        Assert.Null(res.GetCharacterFace(0x10FFFD, "sc", false, false));
    }

    [Fact]
    public void ShapingFont_OfAFaceInMemory_IsReadThroughItsLoaderStream()
    {
        using var res = new D2DResources(0);
        var bytes = File.ReadAllBytes(Path.Combine(WindowsFonts, "segoeui.ttf"));
        var face = res.CreateInMemoryFace(bytes, FontFaceType.Truetype);
        Assert.NotNull(face);
        var shaping = res.GetShapingFont(face!);
        Assert.NotNull(shaping);
        Assert.Equal((int)face!.GlyphCount, shaping!.GlyphCount);
        // An installed face still reads its file from disk.
        var installed = res.GetSystemFace("Arial", false, false)!;
        Assert.Equal((int)installed.GlyphCount, res.GetShapingFont(installed)!.GlyphCount);
    }

    [Fact]
    public async Task MixedRun_KeepsTheCharacters_FallbackChangesDrawingOnly()
    {
        var pdf = Page("Arial", "Identity", new[] { Arial.Value, Nirmala.Value }, Seen("שלום עולם") + " कि क्ष");
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        using (var d2d = new Direct2DVectorRenderer())
        using (await d2d.RenderDisplayListAsync(await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 72 })) { }
        var page = PdfTextExtractor.LoadPage(doc, 1);
        Assert.Contains("क्ष", page.Text);
        Assert.Single(PdfTextFind.FindAll(page, "क्ष", matchCase: true, wholeWord: false, consecutive: false));
    }

    /// <summary>The dark pixels of the page's Direct2D render at 96 dpi.</summary>
    private static async Task<long> Ink(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var d2d = new Direct2DVectorRenderer();
        using var page = await d2d.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 96 });
        var px = page.Pixels.Span;
        long ink = 0;
        for (int y = 0; y < page.HeightPixels; y++)
            for (int x = 0; x < page.WidthPixels; x++)
            {
                int i = y * page.Stride + x * 4;
                if (Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) < 128) ink++;
            }
        return ink;
    }
}
