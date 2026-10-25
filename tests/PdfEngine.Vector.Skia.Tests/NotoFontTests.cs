using System.Text;
using PdfEngine.Vector.Skia;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The web demo's Noto fallback (fonts/noto, built in CI by eng/webdemo/noto_fonts.py and not
/// committed): the character index, the choice of face by script and CJK language, and fetching
/// on demand. No test reaches the network. A fake fetch serves the committed Liberation files and
/// an index written here, whose Noto file names are served by installed Windows fonts with the
/// same scripts (Arial for Arabic, Nirmala UI for Devanagari, Microsoft YaHei for Chinese). So the
/// whole chain runs as in the browser; only the outlines differ. With NOTO_FONTS_DIR set to a
/// folder that the script wrote, <see cref="RealNotoFonts_DrawArabicDevanagariAndChinese"/> draws
/// with the real files too.
/// </summary>
public class NotoFontTests
{
    private static readonly string LiberationDirectory = Path.Combine(RepositoryRoot(), "src", "PdfViewer.Web", "wwwroot", "fonts");
    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("PdfViewer.slnx not found above the test output.");
    }

    // ------------------------------------------------------------------ index

    /// <summary>Code point ranges in the index's delta form: pairs [gap after the previous run, extra code points].</summary>
    private static int[] Encode(params (int First, int Last)[] runs)
    {
        var list = new List<int>();
        int end = -1;
        foreach (var (first, last) in runs)
        {
            list.Add(first - end - 1);
            list.Add(last - first);
            end = last;
        }
        return list.ToArray();
    }

    private static byte[] IndexJson(params (string File, string Family, string Group, (int, int)[] Runs)[] faces)
    {
        var sb = new StringBuilder("{\"version\":1,\"faces\":[");
        for (int i = 0; i < faces.Length; i++)
        {
            var f = faces[i];
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"file\":\"{f.File}\",\"family\":\"{f.Family}\",\"group\":\"{f.Group}\",\"ranges\":[{string.Join(",", Encode(f.Runs))}]}}");
        }
        return Encoding.UTF8.GetBytes(sb.Append("]}").ToString());
    }

    private const string Arabic = "NotoSansArabic-Regular.ttf", Devanagari = "NotoSansDevanagari-Regular.ttf", Symbols = "NotoSansSymbols2-Regular.ttf";
    private const string ScCore = "NotoSansSC-Regular-00.otf", ScRest = "NotoSansSC-Regular-01.otf", TcCore = "NotoSansTC-Regular-00.otf", JpCore = "NotoSansJP-Regular-00.otf", KrCore = "NotoSansKR-Regular-00.otf";

    /// <summary>An index like the script's: script and symbol faces, then CJK slices by language.</summary>
    private static byte[] TestIndex() => IndexJson(
        (Arabic, "Noto Sans Arabic", "arabic", new[] { (0x0600, 0x06FF), (0xFB50, 0xFDFF), (0xFE70, 0xFEFF) }),
        (Devanagari, "Noto Sans Devanagari", "devanagari", new[] { (0x0900, 0x097F) }),
        (Symbols, "Noto Sans Symbols 2", "symbols2", new[] { (0x2190, 0x21FF), (0x2700, 0x27BF) }),
        // The core slice has the common characters; the rest slice the others (here: a block of rare ones).
        (ScCore, "Noto Sans SC", "sc", new[] { (0x3000, 0x303F), (0x4E00, 0x4E2D), (0x6587, 0x6587) }),
        (ScRest, "Noto Sans SC", "sc", new[] { (0x9F00, 0x9FA5) }),
        (TcCore, "Noto Sans TC", "tc", new[] { (0x3000, 0x303F), (0x4E00, 0x4E2D), (0x6587, 0x6587) }),
        (JpCore, "Noto Sans JP", "jp", new[] { (0x3000, 0x30FF), (0x4E00, 0x4E2D), (0x6587, 0x6587) }),
        (KrCore, "Noto Sans KR", "kr", new[] { (0x3000, 0x303F), (0x4E00, 0x4E2D), (0xAC00, 0xD7A3) }));

    private static BundledFontIndex Index => BundledFontIndex.Parse(TestIndex(), "noto");

    [Fact]
    public void Index_DecodesRunsAndPaths()
    {
        var index = Index;
        Assert.Equal(8, index.Entries.Count);
        var arabic = index.Entries[0];
        Assert.Equal("noto/" + Arabic, arabic.Face.Path);
        Assert.Equal("Noto Sans Arabic", arabic.Face.Family);
        Assert.Equal(0x100 + 0x2B0 + 0x90, arabic.CharacterCount);
        Assert.True(arabic.Covers(0x0600) && arabic.Covers(0x06FF) && arabic.Covers(0xFB50) && arabic.Covers(0xFEFF));
        Assert.False(arabic.Covers(0x05FF) || arabic.Covers(0x0700) || arabic.Covers(0xFE6F) || arabic.Covers(0xFF00));
        Assert.Equal(new[] { false, false, false, true, true, true, true, true }, index.Entries.Select(e => e.IsCjk));
    }

    [Fact]
    public void Index_LeavesOutUnsafeNames_AndRejectsOtherVersions()
    {
        var index = BundledFontIndex.Parse(IndexJson(("../evil.ttf", "X", "arabic", new[] { (0x600, 0x6FF) }),
            ("sub/dir.ttf", "X", "arabic", new[] { (0x600, 0x6FF) }), (Arabic, "Noto Sans Arabic", "arabic", new[] { (0x600, 0x6FF) })), "noto");
        Assert.Equal(new[] { "noto/" + Arabic }, index.Entries.Select(e => e.Face.Path));
        Assert.Throws<FormatException>(() => BundledFontIndex.Parse("{\"version\":2,\"faces\":[]}"u8, "noto"));
        Assert.Throws<FormatException>(() => BundledFontIndex.Parse("not json"u8, "noto"));
        Assert.Throws<FormatException>(() => BundledFontIndex.Parse("{\"version\":1,\"faces\":[{\"file\":\"a.ttf\",\"family\":\"A\",\"group\":\"x\",\"ranges\":[1]}]}"u8, "noto"));
    }

    [Theory]
    [InlineData(0x0628, "", null, Arabic)]                 // Arabic beh
    [InlineData(0xFEF7, "", null, Arabic)]                 // a presentation form
    [InlineData(0x0915, "", null, Devanagari)]             // ka
    [InlineData(0x2713, "", null, Symbols)]                // check mark
    [InlineData(0x4E2D, "", null, ScCore)]                 // a common Han character: Simplified Chinese when nothing tells
    [InlineData(0x9F99, "", null, ScRest)]                 // a rare one: the slice that has it
    [InlineData(0x4E2D, "", "Japan1", JpCore)]             // the font's character collection
    [InlineData(0x4E2D, "", "CNS1", TcCore)]
    [InlineData(0x4E2D, "", "Korea1", KrCore)]
    [InlineData(0x4E2D, "中の", null, JpCore)]     // kana in the text
    [InlineData(0x4E2D, "한中", null, KrCore)]     // hangul in the text
    [InlineData(0x3042, "", "GB1", JpCore)]                // kana: only the Japanese face has it
    [InlineData(0xAC00, "", null, KrCore)]                 // hangul: only the Korean face has it
    [InlineData(0x0041, "", null, null)]                   // Latin is Liberation's: no Noto face here
    public void Find_PicksTheFaceByScriptAndLanguage(int codePoint, string text, string? ordering, string? file)
    {
        var face = Index.Find(codePoint, new SkiaTextHint("SomeFont", ordering, text));
        Assert.Equal(file == null ? null : "noto/" + file, face?.Path);
    }

    [Theory]
    [InlineData("MS-Mincho", "jp")]
    [InlineData("MSPGothic", "jp")]
    [InlineData("Malgun Gothic", "kr")]
    [InlineData("Batang", "kr")]
    [InlineData("MingLiU", "tc")]
    [InlineData("PMingLiU-ExtB", "tc")]
    [InlineData("SimSun", "sc")]
    [InlineData("STSong-Light", "sc")]
    [InlineData("Microsoft YaHei", "sc")]
    [InlineData("Unknown", "sc")]
    public void CjkLanguage_FromTheFontName(string name, string language) =>
        Assert.Equal(language, BundledFontIndex.CjkLanguage(new SkiaTextHint(name, null, "中文")));

    // ------------------------------------------------------------------ fetching and drawing

    /// <summary>
    /// A fetch with no network: Liberation from the committed files, the index above, and each Noto
    /// file name served by an installed face of the same script. Records what was asked for.
    /// </summary>
    private sealed class FakeFetch
    {
        public List<string> Requests { get; } = new();
        public bool FailIndex { get; init; }
        public string? NotoDirectory { get; init; }

        public Task<byte[]?> FetchAsync(string file, CancellationToken cancellationToken)
        {
            Requests.Add(file);
            if (!file.StartsWith("noto/", StringComparison.Ordinal))
                return Task.FromResult<byte[]?>(File.ReadAllBytes(Path.Combine(LiberationDirectory, file)));
            string name = file["noto/".Length..];
            if (NotoDirectory != null)
                return Task.FromResult<byte[]?>(File.ReadAllBytes(Path.Combine(NotoDirectory, name)));
            if (name == "index.json")
                return Task.FromResult(FailIndex ? null : TestIndex());
            string? stand = name switch
            {
                Arabic => "arial.ttf",
                Devanagari => "Nirmala.ttc",
                Symbols => "seguisym.ttf",
                ScCore or ScRest => "msyh.ttc",
                TcCore => "msjh.ttc",
                JpCore => "msgothic.ttc",
                KrCore => "malgun.ttf",
                _ => null,
            };
            return Task.FromResult(stand == null ? null : File.ReadAllBytes(Path.Combine(WindowsFonts, stand)));
        }

        public IEnumerable<string> NotoRequests => Requests.Where(r => r.StartsWith("noto/", StringComparison.Ordinal));
    }

    /// <summary>
    /// One line per entry: a composite font (Identity-H, a /ToUnicode CMap, not embedded) so that the
    /// characters reach the renderer as Unicode, as a PDF made with a system CJK or Arabic font does.
    /// </summary>
    private static byte[] UnicodePage(params (string BaseFont, string? Ordering, string Text)[] lines)
    {
        var b = new VectorPdfBuilder();
        var fonts = new StringBuilder();
        var content = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var (baseFont, ordering, text) = lines[i];
            var cps = text.EnumerateRunes().Select(r => r.Value).ToArray();
            var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
            var distinct = cps.Distinct().ToArray();
            cmap.Append($"{distinct.Length} beginbfchar\n");
            foreach (int cp in distinct) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
            cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
            int toUnicode = b.AddStream("", cmap.ToString());
            int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >>");
            int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering ?? "Identity"}) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /CIDToGIDMap /Identity >>");
            int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
            fonts.Append($"/F{i} {type0} 0 R ");
            content.Append($"BT /F{i} 24 Tf 20 {360 - 40 * i} Td <{string.Concat(cps.Select(c => c.ToString("X4")))}> Tj ET\n");
        }
        b.AddPage(content.ToString(), $"<< /Font << {fonts} >> >>", mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static int Ink(ViewerBitmap bitmap, int top = 0, int bottom = int.MaxValue)
    {
        int ink = 0;
        var px = bitmap.Pixels.AsSpan();
        for (int y = Math.Max(0, top); y < Math.Min(bitmap.Height, bottom); y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                int i = (y * bitmap.Width + x) * 4;
                if (px[i] + px[i + 1] + px[i + 2] < 3 * 160) ink++;
            }
        return ink;
    }

    private const string ArabicText = "مرحبا";            // marhaba
    private const string HindiText = "नमस्ते";       // namaste
    private const string ChineseText = "中文";                              // zhongwen

    [Fact]
    public async Task WithoutNoto_ArabicHindiChinese_AreNotDrawn()
    {
        var fetch = new FakeFetch();
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("ArialUnicode", null, ArabicText), ("Mangal", null, HindiText), ("SimSun", null, ChineseText)),
            options: new ViewerOptions { Fonts = OnDemandFontSource.Liberation(fetch.FetchAsync) });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(3, bitmap.TextRegionsWithoutFont);
        Assert.Equal(0, Ink(bitmap));
    }

    [Fact]
    public async Task WithNoto_ArabicHindiChinese_DrawFromTheirFaces_FetchingOnlyThose()
    {
        var fetch = new FakeFetch();
        var fonts = OnDemandFontSource.Bundled(fetch.FetchAsync);
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("ArialUnicode", null, ArabicText), ("Mangal", null, HindiText), ("SimSun", null, ChineseText)),
            options: new ViewerOptions { Fonts = fonts });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(0, bitmap.TextRegionsWithoutFont);
        for (int line = 0; line < 3; line++)
        {
            int baseline = 400 - (360 - 40 * line);
            Assert.True(Ink(bitmap, baseline - 26, baseline + 10) > 100, $"line {line} drew no glyphs");
        }
        // The index once, then only the files with the page's characters: not the rare CJK slice,
        // nor the other languages' faces, nor the symbols.
        Assert.Equal(new[] { "noto/index.json", "noto/" + Arabic, "noto/" + Devanagari, "noto/" + ScCore }.Order(), fetch.NotoRequests.Order());
        Assert.False(fonts.HasPendingRequests);
    }

    [Fact]
    public async Task LatinOnlyPage_FetchesNoNotoFile()
    {
        var fetch = new FakeFetch();
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("Helvetica", null, "Latin only")),
            options: new ViewerOptions { Fonts = OnDemandFontSource.Bundled(fetch.FetchAsync) });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(0, bitmap.TextRegionsWithoutFont);
        Assert.Empty(fetch.NotoRequests);
        Assert.Equal(new[] { "LiberationSans-Regular.ttf" }, fetch.Requests);
    }

    [Fact]
    public async Task MixedRun_TakesEachCharacterFromItsFace()
    {
        var fetch = new FakeFetch();
        var fonts = OnDemandFontSource.Bundled(fetch.FetchAsync);
        // One run: Latin from Liberation Sans, Arabic, a check mark from the symbols face, Han from the Chinese face.
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("Helvetica", null, "Ab " + ArabicText + " ✓ " + ChineseText)),
            options: new ViewerOptions { Fonts = fonts });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(0, bitmap.TextRegionsWithoutFont);
        Assert.Equal(new[] { "noto/index.json", "noto/" + Arabic, "noto/" + Symbols, "noto/" + ScCore }.Order(), fetch.NotoRequests.Order());
        Assert.Contains("LiberationSans-Regular.ttf", fetch.Requests);

        // Every part is drawn: the line has more ink than the Latin letters alone.
        await using var latin = await ViewerDocument.OpenAsync(UnicodePage(("Helvetica", null, "Ab")), options: new ViewerOptions { Fonts = fonts });
        Assert.True(Ink(bitmap) > Ink(await latin.RenderPageAsync(1, 1.0)) * 3);
    }

    [Fact]
    public async Task JapaneseFont_TakesHanFromTheJapaneseFace()
    {
        var fetch = new FakeFetch();
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("MS-Mincho", "Japan1", ChineseText)),
            options: new ViewerOptions { Fonts = OnDemandFontSource.Bundled(fetch.FetchAsync) });
        Assert.Equal(0, (await doc.RenderPageAsync(1, 1.0)).TextRegionsWithoutFont);
        Assert.Equal(new[] { "noto/index.json", "noto/" + JpCore }.Order(), fetch.NotoRequests.Order());
    }

    [Theory]
    [InlineData("Ryumin", "Japan1", JpCore)]
    [InlineData("Ryumin", "GB1", ScCore)]
    [InlineData("Ryumin", "CNS1", TcCore)]
    [InlineData("Ryumin", "Korea1", KrCore)]
    [InlineData("Ryumin-Light", null, JpCore)] // no collection: the font's name tells
    public async Task AllHanRun_TakesTheSliceOfItsLanguage(string baseFont, string? ordering, string file)
    {
        // A run of Han characters only: no Liberation face has any of them, so the whole run comes
        // from the CJK face of the font's language, as on Windows (Yu Gothic for Japan1, and so on).
        var fetch = new FakeFetch();
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage((baseFont, ordering, "一中")),
            options: new ViewerOptions { Fonts = OnDemandFontSource.Bundled(fetch.FetchAsync) });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(0, bitmap.TextRegionsWithoutFont);
        Assert.True(Ink(bitmap) > 50, "the Han drew nothing");
        Assert.Equal(new[] { "noto/index.json", "noto/" + file }.Order(), fetch.NotoRequests.Order());
    }

    [Fact]
    public async Task NotoFiles_AreFetchedOncePerSession()
    {
        var fetch = new FakeFetch();
        var fonts = OnDemandFontSource.Bundled(fetch.FetchAsync);
        byte[] pdf = UnicodePage(("ArialUnicode", null, ArabicText));
        for (int i = 0; i < 2; i++)
        {
            await using var doc = await ViewerDocument.OpenAsync(pdf, options: new ViewerOptions { Fonts = fonts });
            Assert.Equal(0, (await doc.RenderPageAsync(1, 1.0 + i)).TextRegionsWithoutFont);
        }
        Assert.Equal(new[] { "noto/index.json", "noto/" + Arabic }, fetch.NotoRequests.Order());
        Assert.NotNull(fonts.CharacterIndex);
    }

    [Fact]
    public async Task FailedIndex_IsReportedAndNotRetried()
    {
        var fetch = new FakeFetch { FailIndex = true };
        var fonts = OnDemandFontSource.Bundled(fetch.FetchAsync);
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("ArialUnicode", null, ArabicText)), options: new ViewerOptions { Fonts = fonts });
        Assert.Equal(1, (await doc.RenderPageAsync(1, 1.0)).TextRegionsWithoutFont);
        Assert.Equal(1, (await doc.RenderPageAsync(1, 1.5)).TextRegionsWithoutFont);
        Assert.Equal(new[] { "noto/index.json" }, fetch.NotoRequests);
        Assert.False(fonts.HasPendingRequests);
        Assert.Null(fonts.CharacterIndex);
    }

    /// <summary>Runs only with NOTO_FONTS_DIR set to the output of eng/webdemo/noto_fonts.py.</summary>
    private sealed class NotoFontsFactAttribute : FactAttribute
    {
        public NotoFontsFactAttribute()
        {
            if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("NOTO_FONTS_DIR") ?? "", "index.json")))
                Skip = "Set NOTO_FONTS_DIR to the folder eng/webdemo/noto_fonts.py wrote (fonts/noto).";
        }
    }

    [NotoFontsFact]
    public async Task RealNotoFonts_DrawArabicDevanagariAndChinese()
    {
        var fetch = new FakeFetch { NotoDirectory = Environment.GetEnvironmentVariable("NOTO_FONTS_DIR") };
        var fonts = OnDemandFontSource.Bundled(fetch.FetchAsync);
        await using var doc = await ViewerDocument.OpenAsync(UnicodePage(("ArialUnicode", null, ArabicText), ("Mangal", null, HindiText),
            ("SimSun", null, ChineseText), ("MS-Mincho", "Japan1", "日本語です"), ("Batang", null, "한국어")), options: new ViewerOptions { Fonts = fonts });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(0, bitmap.TextRegionsWithoutFont);
        for (int line = 0; line < 5; line++)
        {
            int baseline = 400 - (360 - 40 * line);
            Assert.True(Ink(bitmap, baseline - 26, baseline + 10) > 100, $"line {line} drew no glyphs");
        }
        Assert.Equal(new[] { "noto/NotoSansArabic-Regular.ttf", "noto/NotoSansDevanagari-Regular.ttf", "noto/NotoSansJP-Regular-00.otf",
            "noto/NotoSansKR-Regular-00.otf", "noto/NotoSansSC-Regular-00.otf", "noto/index.json" }, fetch.NotoRequests.Order(StringComparer.Ordinal));
    }
}
