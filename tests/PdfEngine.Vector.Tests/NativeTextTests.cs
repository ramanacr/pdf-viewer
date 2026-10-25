using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// M9 native text: the vector engine's text page (PdfTextExtractor) against PDFium's (the
/// oracle) on synthetic pages that exercise each rule: generated spaces and line breaks,
/// ligatures, /ActualText, rotated pages and crop box offsets, vertical writing, Type 3 fonts,
/// invisible text, form XObjects, line-end hyphens, overprinted bold, ExtGState fonts, /ToUnicode
/// sections PDFium rejects, and search with match case and whole word.
/// </summary>
public class NativeTextTests
{
    private const string Helvetica = "/F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private static string ArialPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");

    /// <summary>A page whose /F1 is the system Arial embedded as a simple TrueType font (built at test time, never stored).</summary>
    private static byte[] ArialPage(string content, string extraPage = "", string mediaBox = "[0 0 300 300]", Action<VectorPdfBuilder>? before = null, string extraResources = "")
    {
        var b = new VectorPdfBuilder();
        before?.Invoke(b);
        byte[] arial = File.ReadAllBytes(ArialPath);
        int stream = b.AddStream($"/Length1 {arial.Length}", arial, flate: true);
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /ArialMT /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {stream} 0 R >>");
        var widths = string.Join(" ", Enumerable.Range(32, 95).Select(c => (int)Math.Round(ArialWidth((char)c))));
        int font = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT /FirstChar 32 /LastChar 126 /Widths [{widths}] /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
        b.AddPage(content, $"<< /Font << /F1 {font} 0 R >> {extraResources} >>", mediaBox: mediaBox, extra: extraPage);
        return b.Build();
    }

    private static readonly Lazy<System.Windows.Media.GlyphTypeface> Arial = new(() => new System.Windows.Media.GlyphTypeface(new Uri(ArialPath)));

    private static double ArialWidth(char c)
    {
        var face = Arial.Value;
        return face.CharacterToGlyphMap.TryGetValue(c, out ushort g) ? face.AdvanceWidths[g] * 1000 : 0;
    }

    private static byte[] Page(string content, string fonts = Helvetica, string extraPage = "", string mediaBox = "[0 0 300 300]", Action<VectorPdfBuilder>? before = null)
    {
        var b = new VectorPdfBuilder();
        before?.Invoke(b);
        b.AddPage(content, $"<< /Font << {fonts} >> >>", mediaBox: mediaBox, extra: extraPage);
        return b.Build();
    }

    private static PdfTextPage Native(byte[] pdf, int page = 1)
    {
        using var doc = PdfVectorDocument.OpenAsync(pdf).AsTask().GetAwaiter().GetResult();
        return PdfTextExtractor.LoadPage(doc, page);
    }

    private static double? IoU(TextRect a, TextRect b)
    {
        a.Normalize(); b.Normalize();
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0) return null;
        double iw = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), ih = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        double inter = iw > 0 && ih > 0 ? iw * ih : 0;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }

    /// <summary>The native text page equals PDFium's: text, characters, types, boxes (when <paramref name="minIoU"/> &gt; 0) and search hits.</summary>
    private static PdfTextPage AssertSameAsPdfium(byte[] pdf, double minIoU = 0.9, double minLooseIoU = 0.9, params string[] queries)
    {
        var oracle = PdfiumTextOracle.Read(pdf, 1, queries);
        var native = Native(pdf);
        Assert.Equal(oracle.Text, native.GetAllText());
        Assert.Equal(oracle.Count, native.CharCount);
        for (int i = 0; i < oracle.Count; i++)
        {
            Assert.True(oracle.Unicode[i] == native.GetUnicode(i), $"char {i}: U+{(int)oracle.Unicode[i]:X4} vs U+{(int)native.GetUnicode(i):X4}");
            Assert.True(oracle.Generated[i] == native.IsGenerated(i), $"char {i} generated");
            Assert.True(oracle.Hyphen[i] == native.IsHyphen(i), $"char {i} hyphen");
            if (minIoU > 0 && IoU(oracle.Boxes[i], native.GetCharBox(i)) is double iou)
                Assert.True(iou >= minIoU, $"char {i} '{oracle.Unicode[i]}' box {oracle.Boxes[i]} vs {native.GetCharBox(i)} (IoU {iou:0.00})");
            if (minLooseIoU > 0 && IoU(oracle.Loose[i], native.GetLooseCharBox(i)) is double liou)
                Assert.True(liou >= minLooseIoU, $"char {i} '{oracle.Unicode[i]}' loose box {oracle.Loose[i]} vs {native.GetLooseCharBox(i)} (IoU {liou:0.00})");
        }
        foreach (string q in queries)
        {
            Assert.Equal(oracle.Hits(q), native.Find(q));
            Assert.Equal(oracle.Hits(q, matchCase: true), native.Find(q, matchCase: true));
            Assert.Equal(oracle.Hits(q, wholeWord: true), native.Find(q, matchWholeWord: true));
        }
        return native;
    }

    [Fact]
    public void Lines_Words_AndKerning_MatchPdfium()
    {
        var page = AssertSameAsPdfium(ArialPage(
            "BT /F1 12 Tf 20 250 Td (Hello world, this is) Tj ( a test) Tj 0 -16 Td [(Kern) -250 (ed) -900 (gap)] TJ " +
            "0 -16 Td (Second) Tj 60 0 Td (column) Tj 0 -16 Td 3 Tc (Spaced) Tj 0 Tc 0 -16 Td 4 Tw (word spacing here) Tj ET"),
            queries: new[] { "world", "Kerned", "gap", "column", "word spacing" });
        Assert.Contains("\r\n", page.GetAllText());
    }

    [Fact]
    public void LigatureFromToUnicode_SplitsIntoPieces_AsPdfium()
    {
        var page = AssertSameAsPdfium(Page("BT /F1 14 Tf 20 200 Td (\\001nd the \\002ow) Tj ET",
            fonts: "/F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 3 0 R >>",
            before: b => b.AddStream(string.Empty, "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                "2 beginbfchar <01> <FB01> <02> <FB02> endbfchar 1 beginbfrange <20> <7E> <0020> endbfrange endcmap end end")),
            minIoU: 0, minLooseIoU: 0, queries: new[] { "find", "flow" });
        Assert.StartsWith("find", page.GetAllText());
        Assert.Equal(PdfTextCharType.Piece, page.GetCharType(0));
    }

    [Fact]
    public void ActualText_ReplacesTheGlyphs_AsPdfium()
    {
        AssertSameAsPdfium(ArialPage(
            "BT /F1 12 Tf 20 250 Td (See ) Tj /Span << /ActualText (\\376\\377\\000f\\000i) >> BDC (X) Tj EMC (gure) Tj " +
            "0 -20 Td /Span << /ActualText (Replaced) >> BDC (ab) Tj (cd) Tj EMC ( after) Tj ET"),
            minIoU: 0.9, minLooseIoU: 0, queries: new[] { "figure", "Replaced" });
    }

    [Fact]
    public void RotatedPage_WithCropBoxOffset_MatchesPdfium()
    {
        AssertSameAsPdfium(ArialPage("BT /F1 12 Tf 80 200 Td (Rotated page text) Tj 0 -20 Td (second line) Tj ET",
            extraPage: "/Rotate 90 /CropBox [50 40 280 290]"), queries: new[] { "page", "second" });
        AssertSameAsPdfium(ArialPage("BT /F1 12 Tf 0 1 -1 0 150 60 Tm (Text running up) Tj ET"), queries: new[] { "running" });
    }

    [Fact]
    public async Task CropBoxOffset_WordSegmentsMatchThePdfiumService()
    {
        byte[] pdf = ArialPage("BT /F1 12 Tf 120 160 Td (Offset crop box words) Tj ET", extraPage: "/CropBox [100 100 300 300]");
        using var engine = new PdfiumEngine();
        using var pdoc = await engine.OpenDocumentAsync(pdf);
        var expected = await engine.TextService.ExtractTextSegmentsAsync(pdoc, 1);
        using var vdoc = await PdfVectorDocument.OpenAsync(pdf);
        var service = new PdfVectorTextService();
        var actual = await service.ExtractTextSegmentsAsync(vdoc, 1);
        Assert.Equal(expected.Select(s => s.Text), actual.Select(s => s.Text));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 3);
            Assert.Equal(expected[i].Y, actual[i].Y, 3);
            Assert.Equal(expected[i].Width, actual[i].Width, 3);
            Assert.Equal(expected[i].Height, actual[i].Height, 3);
        }
        // The first word starts near the crop box's left edge, not at x = 120 / 200 of the media box.
        Assert.InRange(actual[0].X, 0.05, 0.15);
        Assert.Equal(await engine.TextService.ExtractPageTextAsync(pdoc, 1), await service.ExtractPageTextAsync(vdoc, 1));
    }

    [Fact]
    public void VerticalWriting_IdentityV_MatchesPdfium()
    {
        var map = new System.Windows.Media.GlyphTypeface(new Uri(ArialPath)).CharacterToGlyphMap;
        string Hex(string s) => "<" + string.Concat(s.Select(c => map[c].ToString("X4"))) + ">";
        var b = new VectorPdfBuilder();
        byte[] arial = File.ReadAllBytes(ArialPath);
        int stream = b.AddStream($"/Length1 {arial.Length}", arial, flate: true);
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /ArialMT /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {stream} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ArialMT /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {desc} 0 R /DW 1000 /DW2 [880 -1000] /CIDToGIDMap /Identity >>");
        var bfchar = string.Join(" ", "VERTICALAB".Distinct().Select(c => $"<{map[c]:X4}> <{(int)c:X4}>"));
        int toUnicode = b.AddStream(string.Empty, $"/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange {"VERTICALAB".Distinct().Count()} beginbfchar {bfchar} endbfchar endcmap end end");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /ArialMT /Encoding /Identity-V /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage($"BT /F1 24 Tf 60 180 Td {Hex("VERTICAL")} Tj ET BT /F1 18 Tf 1 0 0 1 140 190 Tm [{Hex("AB")} -500 {Hex("CA")}] TJ ET",
            $"<< /Font << /F1 {type0} 0 R >> >>", mediaBox: "[0 0 200 200]");
        AssertSameAsPdfium(b.Build(), minIoU: 0.9, minLooseIoU: 0, queries: new[] { "VERTICAL", "AB" });
    }

    [Fact]
    public void Type3Font_MatchesPdfium()
    {
        AssertSameAsPdfium(Page("BT /T3 12 Tf 20 200 Td (ABAB) Tj 0 -20 Td (BA) Tj ET",
            fonts: "/T3 << /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /FirstChar 65 /LastChar 66 /Widths [800 800] " +
                   "/Encoding << /Type /Encoding /Differences [65 /square /tri] >> /CharProcs << /square 3 0 R /tri 4 0 R >> /ToUnicode 5 0 R >>",
            before: b =>
            {
                b.AddStream(string.Empty, "800 0 0 0 750 750 d1 0 0 750 750 re f");
                b.AddStream(string.Empty, "800 0 0 0 750 750 d1 0 0 m 750 0 l 375 750 l f");
                b.AddStream(string.Empty, "begincmap 1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> <25A0> <42> <25B2> endbfchar endcmap");
            }), queries: new[] { "■▲" });
        // Type 3 glyph names are not text to PDFium: without /ToUnicode the codes are the text.
        AssertSameAsPdfium(Page("BT /T3 12 Tf 20 200 Td (AB) Tj ET",
            fonts: "/T3 << /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /FirstChar 65 /LastChar 66 /Widths [0 0] " +
                   "/Encoding << /Type /Encoding /Differences [65 /bullet /a1] >> /CharProcs << /bullet 3 0 R /a1 3 0 R >> >>",
            before: b => b.AddStream(string.Empty, "600 0 0 0 500 700 d1 0 0 500 700 re f")));
    }

    [Fact]
    public void InvisibleText_IsText_AsInPdfium()
    {
        var page = AssertSameAsPdfium(ArialPage("BT 3 Tr /F1 12 Tf 20 200 Td (Hidden OCR layer) Tj 7 Tr 0 -20 Td (clip mode too) Tj ET"),
            queries: new[] { "OCR", "clip" });
        Assert.Contains("Hidden OCR layer", page.GetAllText());
    }

    [Fact]
    public void FormXObjects_AreReadInPlace_AsPdfium()
    {
        AssertSameAsPdfium(ArialPage("BT /F1 12 Tf 20 250 Td (Before the form) Tj ET q 1 0 0 1 10 -40 cm /Fm1 Do Q BT /F1 12 Tf 20 150 Td (After) Tj ET",
            before: b => b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Matrix [1 0 0 1 5 0] /Resources << /Font << /F1 6 0 R >> >>",
                "BT /F1 10 Tf 20 220 Td (Inside the form) Tj ET"),
            extraResources: "/XObject << /Fm1 3 0 R >>"), queries: new[] { "form", "Inside" });
    }

    [Fact]
    public void LineEndHyphen_IsMarked_AsPdfium()
    {
        var page = AssertSameAsPdfium(ArialPage("BT /F1 12 Tf 20 250 Td (An exam-) Tj 0 -14 Td (ple of a hy-) Tj 0 -14 Td (phenated word) Tj ET"),
            queries: new[] { "example", "hyphenated", "exam" });
        Assert.Contains(Enumerable.Range(0, page.CharCount), i => page.IsHyphen(i));
    }

    [Fact]
    public void OverprintedBold_IsReadOnce_AsPdfium()
    {
        var page = AssertSameAsPdfium(ArialPage("BT /F1 12 Tf 20 250 Td (Bold) Tj ET BT /F1 12 Tf 20.3 250 Td (Bold) Tj ET BT /F1 12 Tf 60 250 Td (text) Tj ET"),
            queries: new[] { "Bold" });
        Assert.Single(page.Find("Bold"));
    }

    [Fact]
    public void ExtGStateFont_SetsTheFont_AsPdfium()
    {
        AssertSameAsPdfium(ArialPage("BT /GS1 gs 20 250 Td (Font from the graphics state) Tj ET",
            extraResources: "/ExtGState << /GS1 << /Type /ExtGState /Font [5 0 R 14] >> >>"), queries: new[] { "graphics" });
    }

    [Fact]
    public void ToUnicode_SectionsPdfiumRejects_AreRejected()
    {
        // The range runs past its 256-code block and the second section's count is wrong: PDFium
        // ignores both, so the codes read through the encoding.
        AssertSameAsPdfium(Page("BT /F1 14 Tf 20 200 Td (ABC) Tj ET",
            fonts: "/F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 3 0 R >>",
            before: b => b.AddStream(string.Empty, "begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0041> <0142> [<0058> <0059>] endbfrange " +
                "3 beginbfchar <43> <005A> endbfchar endcmap")), minIoU: 0, minLooseIoU: 0);
    }

    [Fact]
    public void Search_MatchCase_WholeWord_AndCjk_MatchPdfium()
    {
        AssertSameAsPdfium(ArialPage("BT /F1 12 Tf 20 250 Td (Apple apple APPLE pineapple apples) Tj 0 -16 Td (an apple, an Apple-tree) Tj ET"),
            queries: new[] { "apple", "Apple", "APPLE", "an apple", "apple tree", " apple", "le a" });
    }

    [Fact]
    public void NoFont_ShowsNoText_AsPdfium()
    {
        AssertSameAsPdfium(Page("BT 20 200 Td (Nothing) Tj ET /F1 12 Tf BT /F1 12 Tf 20 180 Td (Something) Tj ET"), minIoU: 0, minLooseIoU: 0);
    }

    [Fact]
    public async Task Service_SearchMatchesPdfiumService()
    {
        byte[] pdf = ArialPage("BT /F1 12 Tf 20 250 Td (Searching for needles in a haystack of needles) Tj ET");
        using var engine = new PdfiumEngine();
        using var pdoc = await engine.OpenDocumentAsync(pdf);
        var expected = await engine.TextService.SearchTextAsync(pdoc, "needles");
        using var vdoc = await PdfVectorDocument.OpenAsync(pdf);
        var actual = await new PdfVectorTextService().SearchTextAsync(vdoc, "needles");
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].CharacterIndex, actual[i].CharacterIndex);
            Assert.Equal(expected[i].MatchLength, actual[i].MatchLength);
            Assert.Equal(expected[i].ContextSnippet, actual[i].ContextSnippet);
            Assert.Equal(expected[i].X, actual[i].X, 3);
            Assert.Equal(expected[i].Y, actual[i].Y, 3);
        }
    }
    [Fact]
    public void LongPage_ThousandsOfCharacters_MatchPdfium()
    {
        // Past the 512-character chunks the text page keeps its characters in.
        var content = new StringBuilder("BT /F1 6 Tf 10 290 Td 7 TL ");
        for (int line = 0; line < 40; line++)
            content.Append($"(Line {line:00} of a long page with many characters, words and spaces on it) ' ");
        content.Append("ET");
        var page = AssertSameAsPdfium(ArialPage(content.ToString()), queries: new[] { "Line 39", "spaces on it", "many" });
        Assert.True(page.CharCount > 2048, $"{page.CharCount} characters");
    }

    private static byte[] Pages(int count, Func<int, string> content)
    {
        var b = new VectorPdfBuilder();
        for (int p = 1; p <= count; p++) b.AddPage(content(p), $"<< /Font << {Helvetica} >> >>", mediaBox: "[0 0 300 300]");
        return b.Build();
    }

    [Fact]
    public async Task Cache_KeepsPagesWithinItsCharacterBudget()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Pages(40, p => $"BT /F1 12 Tf 20 250 Td (Page {p}) Tj ET"));
        var first = PdfTextExtractor.LoadPage(doc, 1);
        for (int p = 2; p <= 40; p++) PdfTextExtractor.LoadPage(doc, p);
        // Forty small pages are well inside the budget: the first is still kept, not rebuilt.
        Assert.True(PdfTextExtractor.IsLoaded(doc, 1));
        Assert.Same(first, PdfTextExtractor.LoadPage(doc, 1));
        PdfTextExtractor.Invalidate(doc);
        Assert.False(PdfTextExtractor.IsLoaded(doc, 1));
        Assert.NotSame(first, PdfTextExtractor.LoadPage(doc, 1));
    }

    [Fact]
    public async Task Prefetch_BuildsThePageInTheBackground()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Pages(3, p => $"BT /F1 12 Tf 20 250 Td (Prefetched page {p}) Tj ET"));
        PdfTextExtractor.Prefetch(doc, 2);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!PdfTextExtractor.IsLoaded(doc, 2) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert.True(PdfTextExtractor.IsLoaded(doc, 2));
        Assert.False(PdfTextExtractor.IsLoaded(doc, 3));
        Assert.Equal("Prefetched page 2", PdfTextExtractor.LoadPage(doc, 2).GetAllText());
        // Asking again for a page that is kept queues nothing; a closed document is not read.
        PdfTextExtractor.Prefetch(doc, 2);
        doc.Dispose();
        PdfTextExtractor.Prefetch(doc, 3);
        Assert.False(PdfTextExtractor.IsLoaded(doc, 3));
    }
}
