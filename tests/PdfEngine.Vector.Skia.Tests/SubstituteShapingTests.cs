using System.Numerics;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using PdfEngine.Vector.Fonts;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Text in a font that is not embedded, drawn with a substitute: Arabic words take their joining
/// forms and ligatures, marks sit on their letters, and Indic vowel signs, conjuncts and reph are
/// shaped with the substitute's GSUB and GPOS. Installed Windows fonts stand in for the web demo's
/// Noto faces (Segoe UI and Arial for Arabic, Nirmala UI for Devanagari), as in <see cref="NotoFontTests"/>.
/// Arabic glyphs are checked against the presentation forms the font maps, which give each joining
/// form by a code point of its own. With NOTO_FONTS_DIR set, the real Noto faces are checked too.
/// </summary>
public class SubstituteShapingTests
{
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
    private static byte[] Page(string baseFont, TrueTypeFontFile metrics, params string[] lines) =>
        PageWithContent(baseFont, metrics, string.Concat(lines.Select((l, i) => $"BT /F0 24 Tf 20 {360 - 40 * i} Td <{Hex(l)}> Tj ET\n")), lines);

    private static string Hex(string text) => string.Concat(text.EnumerateRunes().Select(r => r.Value.ToString("X4")));

    private static byte[] PageWithContent(string baseFont, TrueTypeFontFile metrics, string content, IEnumerable<string> texts)
    {
        var b = new VectorPdfBuilder();
        var cps = texts.SelectMany(l => l.EnumerateRunes().Select(r => r.Value)).Distinct().Order().ToArray();
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        cmap.Append($"{cps.Length} beginbfchar\n");
        foreach (int cp in cps) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream("", cmap.ToString());
        string widths = string.Join(" ", cps.Select(cp => $"{cp} [{Width(metrics, cp)}]"));
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage(content, $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static int Width(TrueTypeFontFile metrics, int cp) => (int)Math.Round(metrics.Advance(metrics.GlyphFor(cp)));

    /// <summary>The glyphs each line's run is drawn with, left to right.</summary>
    private static async Task<List<List<(ushort Gid, Vector2 Offset, float Scale)>>> Resolve(byte[] pdf, SkiaFontSource? fonts = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var res = new SkiaResources(0, fonts ?? new SkiaFontSource());
        using var replayer = new SkiaReplayer(res, list, null, CancellationToken.None);
        var lines = new List<List<(ushort, Vector2, float)>>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var glyphs = replayer.ResolvedGlyphs(cmd.Run) ?? throw new InvalidOperationException("The run was not drawn.");
            lines.Add(glyphs.OrderBy(g => g.Offset.X).ToList());
        }
        return lines;
    }

    /// <summary>The glyph indices each line is drawn with, left to right.</summary>
    private static async Task<int[][]> Ids(byte[] pdf, SkiaFontSource? fonts = null) =>
        (await Resolve(pdf, fonts)).Select(l => l.Select(g => (int)g.Gid).ToArray()).ToArray();

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

    [Theory]
    [InlineData("कि", "कि")]                                 // ki, in reading order
    [InlineData("िक", "कि")]                                 // ki, as seen
    [InlineData("िक्ष", "क्षि")]         // kshi: after the whole conjunct
    [InlineData("िक़", "क़ि")]                     // ka with nukta
    [InlineData("ना िक", "ना कि")]       // a word after a space
    [InlineData("आि", "आि")]                                 // after a vowel letter, before no consonant: left alone
    [InlineData("িক", "কি")]                                 // Bengali ki
    public void PreBaseVowelSeenFirst_IsPutAfterItsConsonantCluster(string text, string expected) =>
        Assert.Equal(expected, SkiaReplayer.PreBaseVowelsInReadingOrder(text));

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

    // ------------------------------------------------------------------ faces by character (the web demo)

    /// <summary>
    /// A font source like the web demo's: Liberation for the standard fonts, and a face named for
    /// each Arabic or Devanagari character (the Noto faces there; here Arial and Nirmala UI, or
    /// the real files from the Noto folder).
    /// </summary>
    private sealed class CharacterFaces : SkiaFontSource
    {
        private readonly SystemFontFace _arabic, _devanagari;

        public CharacterFaces(string? noto = null)
            : base(SystemFontCatalog.ForDirectory(Path.Combine(RepositoryRoot(), "src", "PdfViewer.Web", "wwwroot", "fonts")))
        {
            _arabic = new SystemFontFace(noto != null ? Path.Combine(noto, "NotoSansArabic-Regular.ttf") : Path.Combine(WindowsFonts, "arial.ttf"), 0, "Arabic", "Regular", "Arabic", 400, false);
            _devanagari = new SystemFontFace(noto != null ? Path.Combine(noto, "NotoSansDevanagari-Regular.ttf") : Path.Combine(WindowsFonts, "Nirmala.ttc"), 0, "Devanagari", "Regular", "Devanagari", 400, false);
        }

        public string ArabicPath => _arabic.Path;
        public string DevanagariPath => _devanagari.Path;

        // Liberation Sans for Arial (the Helvetica substitute), as the web demo bundles it; nothing from the system.
        public override SKTypeface? MatchFamily(string family, bool bold, bool italic) =>
            family == "Arial" ? SKTypeface.FromFile(Path.Combine(RepositoryRoot(), "src", "PdfViewer.Web", "wwwroot", "fonts", "LiberationSans-Regular.ttf")) : null;
        public override bool HasCharacterFaces => true;

        public override SystemFontFace? FindCharacterFace(int codePoint, SkiaTextHint hint) => codePoint switch
        {
            >= 0x0600 and <= 0x06FF => _arabic,
            >= 0x0900 and <= 0x097F => _devanagari,
            _ => null,
        };
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("PdfViewer.slnx not found above the test output.");
    }

    [Fact]
    public async Task FacesByCharacter_ShapeEachWordWithItsFace()
    {
        var fonts = new CharacterFaces();
        string text = "Ab " + Seen("سلام") + " कि";
        var ids = (await Ids(PageWithContent("Helvetica", SegoeUi.Value, $"BT /F0 24 Tf 20 360 Td <{Hex(text)}> Tj ET\n", new[] { text }), fonts))[0];
        // A and b from Liberation Sans, then meem, lam-alef and seen, then the i sign before ka.
        Assert.Equal(7, ids.Length);
        Assert.Equal(Forms(Arial.Value, 0xFEE1, 0xFEFC, 0xFEB3), ids[2..5]);
        Assert.Equal(new[] { Nirmala.Value.GlyphFor(0x093F), Nirmala.Value.GlyphFor(0x0915) }, ids[5..7]);
    }

    /// <summary>Runs with the real Noto files (NOTO_FONTS_DIR; see <see cref="NotoFontsFactAttribute"/>).</summary>
    [NotoFontsFact]
    public async Task RealNotoFaces_JoinArabicAndReorderDevanagari()
    {
        var fonts = new CharacterFaces(NotoFontsFactAttribute.Directory);
        var arabic = Font(fonts.ArabicPath);
        var devanagari = Font(fonts.DevanagariPath);
        string[] lines = { Seen("بيت"), Seen("سلام"), "कि", "क्ष", "र्क" };
        var resolved = await Resolve(PageWithContent("Helvetica", SegoeUi.Value,
            string.Concat(lines.Select((l, i) => $"BT /F0 24 Tf 20 {360 - 40 * i} Td <{Hex(l)}> Tj ET\n")), lines), fonts);
        var ids = resolved.Select(l => l.Select(g => (int)g.Gid).ToArray()).ToArray();
        // The dots sit on their letters: none is left of the word's first letter or right of its last.
        var letters = resolved[0].Where(g => arabic.Advance(g.Gid) > 0).ToList();
        float start = letters.Min(g => g.Offset.X), end = letters.Max(g => g.Offset.X + (float)(arabic.Advance(g.Gid) / 1000 * 24 * g.Scale));
        Assert.All(resolved[0].Where(g => arabic.Advance(g.Gid) == 0), g => Assert.InRange(g.Offset.X, start - 1, end));
        // Joined: no letter keeps the glyph its character maps to (the isolated form). Noto Sans
        // Arabic draws the dots as glyphs of their own, so a word has more glyphs than letters.
        Assert.True(ids[0].Length >= 3);
        foreach (char c in "بيت") Assert.DoesNotContain(arabic.GlyphFor(c), ids[0]);
        Assert.DoesNotContain(arabic.GlyphFor('ل'), ids[1]);
        Assert.DoesNotContain(arabic.GlyphFor('ا'), ids[1]);
        Assert.Equal(devanagari.GlyphFor(0x0915), ids[2][^1]); // the i sign before ka
        Assert.Single(ids[3]);
        Assert.DoesNotContain(devanagari.GlyphFor(0x0930), ids[4]);
    }
}
