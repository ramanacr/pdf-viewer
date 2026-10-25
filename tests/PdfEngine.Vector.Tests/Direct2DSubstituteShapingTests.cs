using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// Text in a font that is not embedded, drawn by the Direct2D backend with a substitute: Arabic
/// words take their joining forms and ligatures, marks sit on their letters, and Indic vowel signs,
/// conjuncts and reph are shaped with the substitute's GSUB and GPOS, by the engine code the Skia
/// backend uses (<see cref="SubstituteShaper"/>). The same cases as the Skia backend's
/// SubstituteShapingTests, with installed Windows fonts (Segoe UI, Arial, Nirmala UI).
/// </summary>
public class Direct2DSubstituteShapingTests
{
    private readonly ITestOutputHelper _output;
    public Direct2DSubstituteShapingTests(ITestOutputHelper output) => _output = output;

    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static TrueTypeFontFile Font(string path) =>
        TrueTypeFontFile.TryLoad(File.ReadAllBytes(path)) ?? throw new InvalidOperationException(path);

    private static readonly Lazy<TrueTypeFontFile> SegoeUi = new(() => Font(Path.Combine(WindowsFonts, "segoeui.ttf")));
    private static readonly Lazy<TrueTypeFontFile> Arial = new(() => Font(Path.Combine(WindowsFonts, "arial.ttf")));
    private static readonly Lazy<TrueTypeFontFile> Nirmala = new(() => Font(Path.Combine(WindowsFonts, "Nirmala.ttc")));

    /// <summary>
    /// A page whose font /F0 is composite (Identity-H, CID = code point, a /ToUnicode CMap) and not
    /// embedded, with /W widths from <paramref name="metrics"/>; one line per entry, the characters
    /// in the order given, which is the order they are seen, left to right.
    /// </summary>
    internal static byte[] Page(string baseFont, TrueTypeFontFile metrics, params string[] lines) =>
        PageWithContent(baseFont, metrics, string.Concat(lines.Select((l, i) => $"BT /F0 24 Tf 20 {360 - 40 * i} Td <{Hex(l)}> Tj ET\n")), lines);

    private static string Hex(string text) => string.Concat(text.EnumerateRunes().Select(r => r.Value.ToString("X4")));

    private static byte[] PageWithContent(string baseFont, TrueTypeFontFile metrics, string content, IEnumerable<string> texts, double widthScale = 1)
    {
        var b = new VectorPdfBuilder();
        var cps = texts.SelectMany(l => l.EnumerateRunes().Select(r => r.Value)).Distinct().Order().ToArray();
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        cmap.Append($"{cps.Length} beginbfchar\n");
        foreach (int cp in cps) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream("", cmap.ToString());
        string widths = string.Join(" ", cps.Select(cp => $"{cp} [{Math.Round(Width(metrics, cp) * widthScale)}]"));
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage(content, $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static int Width(TrueTypeFontFile metrics, int cp) => (int)Math.Round(metrics.Advance(metrics.GlyphFor(cp)));

    /// <summary>The glyphs each line's run is drawn with by the Direct2D backend, left to right.</summary>
    private static async Task<List<List<(ushort Gid, Vector2 Offset, float Scale)>>> Resolve(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var res = new D2DResources(0);
        using var replayer = new Replayer(res, list, null, analyzeOnly: true, CancellationToken.None);
        var lines = new List<List<(ushort, Vector2, float)>>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var glyphs = replayer.ResolvedGlyphs(cmd) ?? throw new InvalidOperationException("The run was not drawn.");
            lines.Add(glyphs.OrderBy(g => g.Offset.X).ToList());
        }
        return lines;
    }

    private static async Task<int[][]> Ids(byte[] pdf) =>
        (await Resolve(pdf)).Select(l => l.Select(g => (int)g.Gid).ToArray()).ToArray();

    /// <summary>Right-to-left text as a producer lays it out: the characters reversed, left to right.</summary>
    private static string Seen(string reading) => new(reading.Reverse().ToArray());

    private static int[] Forms(TrueTypeFontFile font, params int[] cps) => cps.Select(font.GlyphFor).ToArray();

    // ------------------------------------------------------------------ Arabic

    [Fact]
    public async Task Arabic_TakesInitialMedialFinalAndIsolatedForms()
    {
        var f = SegoeUi.Value;
        var ids = await Ids(Page("SegoeUI", f, Seen("بيت"), Seen("ببب"), Seen("دار")));
        // Left to right: teh final, yeh medial, beh initial.
        Assert.Equal(Forms(f, 0xFE96, 0xFEF4, 0xFE91), ids[0]);
        Assert.Equal(Forms(f, 0xFE90, 0xFE92, 0xFE91), ids[1]);
        // Dal, alef and reh never join the letter after them: each stays isolated.
        Assert.Equal(3, ids[2].Length);
        foreach (var (gid, (letter, isolated)) in ids[2].Zip(new[] { (0x0631, 0xFEAD), (0x0627, 0xFE8D), (0x062F, 0xFEA9) }))
            Assert.Contains(gid, new[] { f.GlyphFor(letter), f.GlyphFor(isolated) });
    }

    [Fact]
    public async Task Arabic_LamAlef_IsOneLigature()
    {
        // Arial: Segoe UI joins lam and alef in two glyphs of their own instead.
        var f = Arial.Value;
        var ids = await Ids(Page("Arial", f, Seen("سلام"), Seen("لا")));
        // Meem isolated, lam-alef final, seen initial.
        Assert.Equal(Forms(f, 0xFEE1, 0xFEFC, 0xFEB3), ids[0]);
        Assert.Equal(Forms(f, 0xFEFB), ids[1]);
    }

    [Fact]
    public async Task Arabic_Mark_IsPlacedOnItsLetterByGpos()
    {
        var f = SegoeUi.Value;
        int fatha = f.GlyphFor(0x064E), behInitial = f.GlyphFor(0xFE91);
        // The producer reversed the characters (the mark before its letter), or kept each mark after its letter.
        var lines = await Resolve(Page("SegoeUI", f, Seen("بَب"), "ب" + "بَ"));
        foreach (var line in lines)
        {
            Assert.Equal(3, line.Count);
            Assert.Equal(f.GlyphFor(0xFE90), line[0].Gid);
            var mark = line.Single(g => g.Gid == fatha);
            var letter = line.Single(g => g.Gid == behInitial);
            // Moved off the baseline by the font's mark anchors, and over the initial beh.
            Assert.NotEqual(0f, mark.Offset.Y);
            Assert.InRange(mark.Offset.X, letter.Offset.X - 12, letter.Offset.X + 12);
        }
        Assert.Equal(lines[0], lines[1]);
    }

    [Fact]
    public async Task Arabic_InReadingOrderRightToLeft_IsNotReversedAgain()
    {
        var f = SegoeUi.Value;
        // The same word with its glyphs in reading order, each placed left of the one before (TJ moves).
        string word = "بيت";
        var tj = new StringBuilder("[");
        for (int i = 0; i < word.Length; i++)
        {
            tj.Append($"<{(int)word[i]:X4}>");
            if (i + 1 < word.Length) tj.Append($" {Width(f, word[i]) + Width(f, word[i + 1])} ");
        }
        tj.Append("] TJ");
        var content = $"BT /F0 24 Tf 200 360 Td {tj} ET\nBT /F0 24 Tf 20 320 Td <{Hex(Seen(word))}> Tj ET\n";
        var ids = await Ids(PageWithContent("SegoeUI", f, content, new[] { word }));
        Assert.Equal(Forms(f, 0xFE96, 0xFEF4, 0xFE91), ids[0]);
        Assert.Equal(ids[0], ids[1]);
    }

    // ------------------------------------------------------------------ Devanagari

    [Fact]
    public async Task Devanagari_PreBaseVowel_ConjunctAndReph()
    {
        var f = Nirmala.Value;
        int ka = f.GlyphFor(0x0915), iSign = f.GlyphFor(0x093F), virama = f.GlyphFor(0x094D), ra = f.GlyphFor(0x0930), ssa = f.GlyphFor(0x0937);
        var ids = await Ids(Page("NirmalaUI", f, "कि", "क्ष", "र्क", "िक", "कर्म"));
        // The i sign is typed after its consonant and drawn before it.
        Assert.Equal(new[] { iSign, ka }, ids[0]);
        // Ka, virama, ssa: one conjunct glyph.
        var conjunct = Assert.Single(ids[1]);
        Assert.DoesNotContain(conjunct, new[] { ka, virama, ssa });
        // Ra and virama before a consonant: the reph, drawn after the consonant it sits on.
        Assert.Equal(2, ids[2].Length);
        Assert.Equal(ka, ids[2][0]);
        Assert.DoesNotContain(ra, ids[2]);
        Assert.DoesNotContain(virama, ids[2]);
        // An i sign written where it is seen is not moved a second time.
        Assert.Equal(ids[0], ids[3]);
        // Karma: ka, then the reph over ma (no ra or virama left).
        Assert.Equal(ka, ids[4][0]);
        Assert.DoesNotContain(ra, ids[4]);
        Assert.DoesNotContain(virama, ids[4]);
    }

    [Fact]
    public async Task ShapedWord_FillsTheSpaceItsWidthsGive()
    {
        // Each word is fitted as a whole to the span its /W widths give, as the Skia backend fits it.
        var f = Nirmala.Value;
        var line = (await Resolve(Page("NirmalaUI", f, "क्ष")))[0];
        var g = Assert.Single(line);
        double span = (Width(f, 0x0915) + Width(f, 0x094D) + Width(f, 0x0937)) / 1000.0 * 24;
        double own = f.Advance(g.Gid) / 1000.0 * 24;
        float expected = (float)Math.Min(span / own, 1.10);
        Assert.Equal(expected, g.Scale, 3);
    }

    // ------------------------------------------------------------------ shared code

    [Fact]
    public async Task ShapeWord_FontWithoutTheGlyphs_FallsBackToGlyphByGlyph()
    {
        // Arial has no Devanagari: the word is not shaped (null), so the backend maps it glyph by glyph.
        using var doc = await PdfVectorDocument.OpenAsync(Page("NirmalaUI", Nirmala.Value, "क्ष"));
        var list = await doc.GetPageDisplayListAsync(1);
        var run = list.Commands.OfType<DrawGlyphRun>().Single().Run;
        var word = Assert.Single(SubstituteShaper.Words(run)!);
        Assert.Equal(SubstituteShaper.Script.Indic, word.Script);
        Assert.Null(SubstituteShaper.ShapeWord(Arial.Value, run, word, 1.0));
        Assert.NotNull(SubstituteShaper.ShapeWord(Nirmala.Value, run, word, 1.0));
    }

    [Fact]
    public async Task TextPage_KeepsTheCharacters_ShapingChangesDrawingOnly()
    {
        // Extraction, selection and search read the content stream's characters, not the shaped glyphs.
        var pdf = Page("NirmalaUI", Nirmala.Value, "क्ष", "कि");
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        using (var d2d = new Direct2DVectorRenderer())
        using (await d2d.RenderDisplayListAsync(await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 72 })) { }
        var page = PdfTextExtractor.LoadPage(doc, 1);
        Assert.Contains("क्ष", page.Text);
        Assert.Contains("कि", page.Text);
        Assert.Single(PdfTextFind.FindAll(page, "क्ष", matchCase: true, wholeWord: false, consecutive: false));
    }

    // ------------------------------------------------------------------ unchanged

    [Fact]
    public async Task EmbeddedFont_IsDrawnByItsOwnGlyphs_Unshaped()
    {
        var f = SegoeUi.Value;
        // Segoe UI embedded, with the base letters' glyphs as the producer chose them (isolated forms).
        var b = new VectorPdfBuilder();
        byte[] program = File.ReadAllBytes(Path.Combine(WindowsFonts, "segoeui.ttf"));
        int stream = b.AddStream($"/Length1 {program.Length}", program, flate: true);
        string seen = Seen("بيت");
        int[] gids = seen.Select(c => f.GlyphFor(c)).ToArray();
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /SegoeUI /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 /FontFile2 {stream} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SegoeUI /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 600 /CIDToGIDMap /Identity >>");
        var cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
                   $"3 beginbfchar\n{string.Concat(seen.Select((c, i) => $"<{gids[i]:X4}> <{(int)c:X4}>\n"))}endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end";
        int toUnicode = b.AddStream("", cmap);
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /SegoeUI /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage($"BT /F0 24 Tf 20 360 Td <{string.Concat(gids.Select(g => g.ToString("X4")))}> Tj ET", $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 400 400]");
        var line = (await Resolve(b.Build()))[0];
        Assert.Equal(gids, line.Select(g => (int)g.Gid));
        Assert.Equal(new[] { 0f, 14.4f, 28.8f }, line.Select(g => MathF.Round(g.Offset.X, 2)));
    }

    [Fact]
    public async Task LatinText_IsUnchanged()
    {
        // Helvetica by name, drawn with Arial: one glyph per character, no ligatures.
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /H 20 Tf 10 300 Td (office AV) Tj ET", $"<< /Font << /H {helv} 0 R >> >>", mediaBox: "[0 0 400 400]");
        var line = (await Resolve(b.Build()))[0];
        Assert.Equal("officeAV".Select(c => Arial.Value.GlyphFor(c)), line.Select(g => (int)g.Gid));
        Assert.All(line, g => Assert.Equal(0f, g.Offset.Y));
    }

    // ------------------------------------------------------------------ the WPF backend

    /// <summary>
    /// The WPF backend (used when no Direct3D device exists) shapes with the same code: its render of
    /// shaped words is close to the Direct2D one (the unshaped glyphs are off on many more pixels).
    /// </summary>
    [Theory]
    [InlineData("arabic")]
    [InlineData("devanagari")]
    public async Task Wpf_ShapesSubstituteTextLikeDirect2D(string script)
    {
        // Large text, so the words cover much of the page.
        static string Lines(params string[] lines) => string.Concat(lines.Select((l, i) => $"BT /F0 72 Tf 20 {300 - 110 * i} Td <{Hex(l)}> Tj ET\n"));
        string[] words = script == "arabic" ? new[] { Seen("بيت"), Seen("سلام") } : new[] { "कि", "क्ष", "कर्म" };
        byte[] shaped = script == "arabic"
            ? PageWithContent("SegoeUI", SegoeUi.Value, Lines(words), words)
            : PageWithContent("NirmalaUI", Nirmala.Value, Lines(words), words);
        using var d2d = new Direct2DVectorRenderer();
        using var wpf = new WindowsVectorRenderer();
        using var doc = await PdfVectorDocument.OpenAsync(shaped);
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = 96 };
        using var a = await d2d.RenderDisplayListAsync(list, request);
        using var b = await wpf.RenderDisplayListAsync(list, request);
        var (mean, bad) = DifferentialRenderingTests.Compare(b, a);
        _output.WriteLine($"wpf vs d2d {script}: mean={mean:F2} bad={bad:P2}");
        // Shaped and fitted: mean 0.10 (Arabic) and 0.27 (Devanagari), 0.00 % and 0.08 % of pixels off by
        // more than 64. Shaped with each glyph at its own width: mean 2.4 and 3.2, 1.1 % and 1.5 %.
        // Unshaped: mean 8.2 and 12.0, 3.5 % and 5.1 %.
        Assert.True(mean <= 0.6, $"{script}: mean {mean:F2}");
        Assert.True(bad <= 0.003, $"{script}: {bad:P2} off");
    }

    /// <summary>
    /// A word whose /W widths give it much less space than its shaped glyphs take: both backends
    /// narrow it to that space, so its ink is as wide in the WPF render as in the Direct2D one.
    /// </summary>
    [Theory]
    [InlineData("arabic")]
    [InlineData("devanagari")]
    public async Task Wpf_FitsANarrowShapedWordLikeDirect2D(string script)
    {
        string word = script == "arabic" ? Seen("سلام") : "कर्म";
        var font = script == "arabic" ? SegoeUi.Value : Nirmala.Value;
        byte[] pdf = PageWithContent(script == "arabic" ? "SegoeUI" : "NirmalaUI", font,
            $"BT /F0 72 Tf 20 200 Td <{Hex(word)}> Tj ET\n", new[] { word }, widthScale: 0.6);
        var scale = (await Resolve(pdf))[0].Select(g => g.Scale).Distinct().Single();
        Assert.InRange(scale, 0.3f, 0.9f);

        using var d2d = new Direct2DVectorRenderer();
        using var wpf = new WindowsVectorRenderer();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = 72 };
        using var a = await d2d.RenderDisplayListAsync(list, request);
        using var b = await wpf.RenderDisplayListAsync(list, request);
        int wa = InkWidth(a), wb = InkWidth(b);
        _output.WriteLine($"{script}: scale={scale:F3} ink width d2d={wa} wpf={wb}");
        Assert.True(wa > 20, $"{script}: no ink");
        Assert.InRange(wb, wa - 1, wa + 1);
    }

    /// <summary>
    /// Helvetica, not embedded (drawn with Arial), with /Widths that are Arial's advances times
    /// <paramref name="factor"/> for each character code.
    /// </summary>
    private static byte[] HelveticaPage(string content, Func<int, double> factor)
    {
        var arial = Arial.Value;
        string widths = string.Join(" ", Enumerable.Range(32, 95).Select(c => Math.Round(Width(arial, c) * factor(c)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var b = new VectorPdfBuilder();
        int helv = b.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 126 /Widths [{widths}] /Encoding /WinAnsiEncoding >>");
        b.AddPage(content, $"<< /Font << /H {helv} 0 R >> >>", mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static async Task<(RenderedPage D2D, RenderedPage Wpf)> RenderBoth(byte[] pdf, double dpi)
    {
        using var d2d = new Direct2DVectorRenderer();
        using var wpf = new WindowsVectorRenderer();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = dpi };
        return (await d2d.RenderDisplayListAsync(list, request), await wpf.RenderDisplayListAsync(list, request));
    }

    /// <summary>
    /// A single glyph whose /Widths width is much less than the substitute's own advance: both
    /// backends narrow it to that width (<see cref="SubstituteShaper.FitToWidth"/>), so its ink is as
    /// wide in the WPF render as in the Direct2D one.
    /// </summary>
    [Fact]
    public async Task Wpf_FitsANarrowSingleGlyphLikeDirect2D()
    {
        // "W" at 40 % of Arial's advance.
        byte[] pdf = HelveticaPage("BT /H 144 Tf 20 150 Td (W) Tj ET", c => c == 'W' ? 0.4 : 1);
        var scale = Assert.Single((await Resolve(pdf))[0]).Scale;
        Assert.InRange(scale, 0.39f, 0.41f);
        var (a, b) = await RenderBoth(pdf, 72);
        using (a)
        using (b)
        {
            int wa = InkWidth(a), wb = InkWidth(b);
            _output.WriteLine($"scale={scale:F3} ink width d2d={wa} wpf={wb}");
            Assert.True(wa > 20, "no ink");
            Assert.InRange(wb, wa - 1, wa + 1);
        }
    }

    /// <summary>
    /// A Latin line whose /Widths are narrower and wider than Arial's advances: the WPF backend fits
    /// and centres each glyph as the Direct2D backend does, so the two renders nearly agree.
    /// </summary>
    [Fact]
    public async Task Wpf_FitsLatinSubstituteGlyphsLikeDirect2D()
    {
        static string Lines(params string[] lines) => string.Concat(lines.Select((l, i) => $"BT /H 40 Tf 10 {340 - 60 * i} Td ({l}) Tj ET\n"));
        // Every other code 25 % narrower, the rest 20 % wider (widened by a tenth and centred).
        byte[] pdf = HelveticaPage(Lines("Waving flags", "over MOUNTAINS", "quick brown fox"), c => c % 2 == 0 ? 0.75 : 1.2);
        var (a, b) = await RenderBoth(pdf, 96);
        using (a)
        using (b)
        {
            var (mean, bad) = DifferentialRenderingTests.Compare(b, a);
            _output.WriteLine($"wpf vs d2d latin: mean={mean:F2} bad={bad:P2}");
            // Fitted: mean 1.08, 0.48 % of pixels off by more than 64, as close as the same line at
            // Arial's own widths (mean 1.03, 0.46 %: the two rasterizers differ). Each glyph at its own
            // width: mean 11.5, 5.3 %.
            Assert.True(mean <= 2, $"mean {mean:F2}");
            Assert.True(bad <= 0.01, $"{bad:P2} off");
        }
    }

    /// <summary>The width in pixels of the dark pixels' bounding box.</summary>
    private static int InkWidth(RenderedPage page)
    {
        var px = page.Pixels.Span;
        int left = int.MaxValue, right = -1;
        for (int y = 0; y < page.HeightPixels; y++)
            for (int x = 0; x < page.WidthPixels; x++)
            {
                int i = y * page.Stride + x * 4;
                if (Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) < 128)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        return right < 0 ? 0 : right - left + 1;
    }
}
