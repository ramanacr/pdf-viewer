using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Make Accessible (<see cref="PdfAutoTagger"/>): generated untagged documents with headings,
/// lists, two columns, tables, an image, a link, repeated headers and footers and a turned page are
/// tagged; the structure reads as a person would read the page, every page reads and draws exactly
/// as before, and the PDF/UA check leaves only what needs a person (or is not about tags).
/// </summary>
public class PdfAutoTaggerTests
{
    private const string Fonts = "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> " +
                                 "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >> >>";

    private static string T(string font, double size, double x, double y, string text) =>
        FormattableString.Invariant($"BT /{font} {size} Tf {x} {y} Td ({text}) Tj ET\n");

    private static byte[] Page(string content, string extraResources = "", string pageExtra = "", string catalog = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, $"<< {Fonts} {extraResources} >>", "[0 0 612 792]", pageExtra);
        if (catalog.Length > 0) b.WithCatalog(catalog);
        return b.Build();
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Elem(string Type, List<Elem> Kids, List<int> Mcids, PdfDictionary Dict, int Page, bool HasObjr);

    private static async Task<(PdfAutoTagResult Result, PdfVectorDocument Tagged)> Tag(byte[] pdf, PdfAutoTagOptions? options = null, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        var result = PdfAutoTagger.Tag(doc, options);
        Assert.True(result.Succeeded, result.Message);
        var tagged = await PdfVectorDocument.OpenAsync(result.Bytes!, password: password);
        await AssertSameTextAndPixels(pdf, result.Bytes!, password);
        // Only checks that need a person (or that tags cannot fix) remain.
        Assert.True(result.OnlyHumanChecksRemain, string.Join("; ", result.Remaining.Where(r => r.Kind == PdfAutoTagIssueKind.NotFixed).Select(r => $"{r.Checkpoint}: {r.Message}")));
        return (result, tagged);
    }

    /// <summary>Every page extracts the same text and renders the same pixels, with both renderers.</summary>
    internal static async Task AssertSameTextAndPixels(byte[] before, byte[] after, string? password = null)
    {
        using var a = await PdfVectorDocument.OpenAsync(before, password: password);
        using var b = await PdfVectorDocument.OpenAsync(after, password: password);
        Assert.Equal(a.PageCount, b.PageCount);
        using var d2d = new Direct2DVectorRenderer();
        for (int p = 1; p <= a.PageCount; p++)
        {
            Assert.Equal(PdfTextExtractor.LoadPage(a, p).GetAllText(), PdfTextExtractor.LoadPage(b, p).GetAllText());
            using (var pa = await InterpreterCoverageTests.RenderAsync(await a.GetPageDisplayListAsync(p)))
            using (var pb = await InterpreterCoverageTests.RenderAsync(await b.GetPageDisplayListAsync(p)))
                Assert.True(pa.Pixels.Span.SequenceEqual(pb.Pixels.Span), $"page {p} draws differently (WPF renderer)");
            var ra = await d2d.RenderAsync(await a.GetPageDisplayListAsync(p), new RenderRequest { PageNumber = p, Dpi = 72 }, null, null, false, CancellationToken.None);
            var rb = await d2d.RenderAsync(await b.GetPageDisplayListAsync(p), new RenderRequest { PageNumber = p, Dpi = 72 }, null, null, false, CancellationToken.None);
            using (var pa = ra.Page)
            using (var pb = rb.Page)
                Assert.True(pa.Pixels.Span.SequenceEqual(pb.Pixels.Span), $"page {p} draws differently (Direct2D renderer)");
        }
    }

    private static Elem Tree(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        var root = Assert.IsType<PdfDictionary>(r.Resolve(doc.Catalog["StructTreeRoot"]));
        var pages = new Dictionary<int, int>();
        for (int i = 0; i < doc.PageCount; i++)
            foreach (var (n, entry) in doc.XrefTable.Entries)
                if (entry.IsInUse && ReferenceEquals(r.Resolve(n), doc.PageTree.Pages[i].Dictionary)) { pages[n] = i; break; }
        Elem Read(PdfObject? o, int page)
        {
            var d = Assert.IsType<PdfDictionary>(r.Resolve(o));
            if (d["Pg"] is PdfIndirectRef pg && pages.TryGetValue(pg.ObjectNumber, out int pi)) page = pi;
            var kids = new List<Elem>();
            var mcids = new List<int>();
            bool objr = false;
            var k = r.Resolve(d["K"]);
            foreach (var item in k is PdfArray a ? a.Items : k == null ? Array.Empty<PdfObject>() : new[] { k })
            {
                var v = r.Resolve(item);
                if (v is PdfInteger mcid) mcids.Add((int)mcid.Value);
                else if (v is PdfDictionary kd && kd.GetName("Type") == "OBJR") objr = true;
                else kids.Add(Read(item, page));
            }
            return new Elem(d.GetName("S") ?? "", kids, mcids, d, page, objr);
        }
        return Read(root["K"], -1);
    }

    /// <summary>The text each MCID's sequence shows on a page (the fixtures show literal strings).</summary>
    private static Dictionary<int, string> McidText(PdfVectorDocument doc, int page)
    {
        var decoder = new PdfStreamDecoder(null, doc.Resolver.Resolve);
        string content = string.Concat(doc.PageTree.Pages[page].Contents.Select(s => Encoding.Latin1.GetString(decoder.DecodeStream(s)) + "\n"));
        var result = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(content, @"<</MCID (\d+)>>BDC(.*?)\nEMC", RegexOptions.Singleline))
        {
            var parts = Regex.Matches(m.Groups[2].Value, @"\(((?:\\.|[^\\)])*)\)").Select(s => Unescape(s.Groups[1].Value));
            result[int.Parse(m.Groups[1].Value)] = string.Join("", parts);
        }
        return result;
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\([0-7]{1,3}|.)", m =>
        m.Groups[1].Value.All(char.IsDigit) ? (Convert.ToInt32(m.Groups[1].Value, 8) == 0x95 ? "•" : ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()) : m.Groups[1].Value);

    /// <summary>The elements as "Type: text" in reading order (containers by their type and kids).</summary>
    private static List<string> Outline(PdfVectorDocument doc, Elem e)
    {
        var texts = new Dictionary<int, Dictionary<int, string>>();
        string TextOf(Elem x)
        {
            if (!texts.TryGetValue(x.Page, out var t)) texts[x.Page] = t = McidText(doc, x.Page);
            return string.Join("", x.Mcids.Select(m => t.GetValueOrDefault(m, "?")));
        }
        var lines = new List<string>();
        void Walk(Elem x, int depth)
        {
            string text = x.Mcids.Count > 0 ? ": " + TextOf(x).Trim() : string.Empty;
            lines.Add(new string(' ', depth * 2) + x.Type + text);
            foreach (var k in x.Kids) Walk(k, depth + 1);
        }
        foreach (var k in e.Kids) Walk(k, 0);
        return lines;
    }

    // ------------------------------------------------------------------ headings and paragraphs

    [Fact]
    public async Task Headings_FromSizeAndWeight_WithParagraphs()
    {
        string content = T("F2", 24, 72, 720, "Annual report")
                         + T("F1", 10, 72, 690, "This report describes the year in brief and")
                         + T("F1", 10, 72, 678, "covers the main results of every team.")
                         + T("F2", 16, 72, 640, "Introduction")
                         + T("F1", 10, 72, 615, "The introduction explains why the report was")
                         + T("F1", 10, 72, 603, "written and who it is for.")
                         + T("F2", 12, 72, 570, "Scope")
                         + T("F1", 10, 72, 545, "Only the main office is covered here.")
                         + T("F2", 16, 72, 510, "Results")
                         + T("F1", 10, 72, 485, "Results improved in every quarter.");
        var (result, tagged) = await Tag(Page(content));
        using var _ = tagged;
        var outline = Outline(tagged, Tree(tagged));
        Assert.Equal(new[]
        {
            "H1: Annual report", "P: This report describes the year in brief andcovers the main results of every team.",
            "H2: Introduction", "P: The introduction explains why the report waswritten and who it is for.",
            "H3: Scope", "P: Only the main office is covered here.",
            "H2: Results", "P: Results improved in every quarter.",
        }, outline);
        Assert.Equal(4, result.Summary.Headings);
        Assert.Equal(4, result.Summary.Paragraphs);

        // The catalog: tagged, a language, the title shown, a title from the first heading.
        var r = tagged.Resolver;
        Assert.Equal(PdfBoolean.True, (r.Resolve(tagged.Catalog["MarkInfo"]) as PdfDictionary)?["Marked"]);
        Assert.Equal("en", (r.Resolve(tagged.Catalog["Lang"]) as PdfString)?.AsDecodedString());
        Assert.Equal(PdfBoolean.True, (r.Resolve(tagged.Catalog["ViewerPreferences"]) as PdfDictionary)?["DisplayDocTitle"]);
        var report = result.Report!;
        Assert.DoesNotContain(report.Errors, i => i.Checkpoint is "06-001" or "06-003" or "07-001" or "11-001" or "01-005");
        // Helvetica is not embedded: a font matter, not a tagging one.
        Assert.Contains(result.Remaining, i => i.Kind == PdfAutoTagIssueKind.OutsideTagging && i.Checkpoint.StartsWith("31-", StringComparison.Ordinal));
        Assert.Contains(result.Remaining, i => i.Checkpoint == "06-002" && i.Kind == PdfAutoTagIssueKind.HumanReview);
    }

    [Fact]
    public async Task EmbeddedFonts_LeaveOnlyTheConformanceClaim_AndWithItTheCheckPasses()
    {
        using var ms = new MemoryStream();
        PdfTextDocument.Write("Plain text set in an embedded font.\nA second line of text.\fPage two has more text.", ms, info: new PdfCreateInfo { Title = "Notes" });
        byte[] pdf = ms.ToArray();
        var (result, tagged) = await Tag(pdf);
        tagged.Dispose();
        Assert.Equal(new[] { "06-002" }, result.Remaining.Select(r => r.Checkpoint));

        var (claimed, again) = await Tag(pdf, new PdfAutoTagOptions { ClaimPdfUa = true, Language = "en-GB" });
        using var _ = again;
        Assert.True(claimed.Report!.IsCompliant, string.Join("; ", claimed.Report.Errors));
        Assert.Empty(claimed.Remaining);
        Assert.Equal("en-GB", (again.Resolver.Resolve(again.Catalog["Lang"]) as PdfString)?.AsDecodedString());
    }

    // ------------------------------------------------------------------ lists

    [Fact]
    public async Task Lists_BulletsAndNumbers_InlineSeparateAndSetApart()
    {
        string content = T("F1", 10, 72, 720, "Shopping list:")
                         + T("F1", 10, 72, 700, @"\225 Apples") + T("F1", 10, 72, 686, @"\225 Pears") + T("F1", 10, 72, 672, @"\225 Plums")
                         + T("F1", 10, 72, 640, "After the list.")
                         // Numbers drawn by their own operators, just before the text.
                         + T("F1", 10, 72, 600, "1.") + T("F1", 10, 84, 600, "First step")
                         + T("F1", 10, 72, 586, "2.") + T("F1", 10, 84, 586, "Second step")
                         + T("F1", 10, 72, 572, "3.") + T("F1", 10, 84, 572, "Third step")
                         + T("F1", 10, 72, 540, "Colours:")
                         // Bullets set well apart from their text: lines of their own.
                         + T("F1", 10, 72, 520, @"\225") + T("F1", 10, 100, 520, "Red")
                         + T("F1", 10, 72, 506, @"\225") + T("F1", 10, 100, 506, "Green")
                         + T("F1", 10, 72, 492, @"\225") + T("F1", 10, 100, 492, "Blue");
        var (result, tagged) = await Tag(Page(content));
        using var _ = tagged;
        var outline = Outline(tagged, Tree(tagged));
        Assert.Equal(new[]
        {
            "P: Shopping list:",
            "L", "  LI", "    LBody: • Apples", "  LI", "    LBody: • Pears", "  LI", "    LBody: • Plums",
            "P: After the list.",
            "L", "  LI", "    Lbl: 1.", "    LBody: First step", "  LI", "    Lbl: 2.", "    LBody: Second step", "  LI", "    Lbl: 3.", "    LBody: Third step",
            "P: Colours:",
            "L", "  LI", "    Lbl: •", "    LBody: Red", "  LI", "    Lbl: •", "    LBody: Green", "  LI", "    Lbl: •", "    LBody: Blue",
        }, outline);
        Assert.Equal(3, result.Summary.Lists);
        Assert.Equal(9, result.Summary.ListItems);
        Assert.DoesNotContain(result.Report!.Errors, i => i.Checkpoint == "09-005");
    }

    [Fact]
    public async Task Lists_OneLabelledParagraph_OrABrokenSequence_StayParagraphs()
    {
        string content = T("F1", 10, 72, 700, "1. Only one numbered paragraph here.")
                         + T("F1", 10, 72, 660, "1. First") + T("F1", 10, 72, 640, "3. Not the second");
        var (result, tagged) = await Tag(Page(content));
        using var _ = tagged;
        Assert.Equal(0, result.Summary.Lists);
        Assert.All(Tree(tagged).Kids, k => Assert.Equal("P", k.Type));
    }

    // ------------------------------------------------------------------ reading order

    [Fact]
    public async Task TwoColumns_ReadColumnByColumn_ThoughDrawnRowByRow()
    {
        string content = T("F1", 10, 72, 700, "Column one starts here and") + T("F1", 10, 330, 700, "Column two starts here and")
                         + T("F1", 10, 72, 688, "continues on this line.") + T("F1", 10, 330, 688, "continues over here.")
                         + T("F1", 10, 72, 660, "Second left paragraph.") + T("F1", 10, 330, 660, "Second right paragraph.");
        var (_, tagged) = await Tag(Page(content));
        using var __ = tagged;
        Assert.Equal(new[]
        {
            "P: Column one starts here andcontinues on this line.", "P: Second left paragraph.",
            "P: Column two starts here andcontinues over here.", "P: Second right paragraph.",
        }, Outline(tagged, Tree(tagged)));
    }

    [Fact]
    public async Task TurnedPage_IsReadInItsDisplayedOrientation()
    {
        // /Rotate 90 shows the page turned clockwise; text drawn up the page reads left to right.
        string Up(string font, double size, double x, double y, string text) =>
            FormattableString.Invariant($"BT /{font} {size} Tf 0 1 -1 0 {x} {y} Tm ({text}) Tj ET\n");
        // Up the page is to the right as displayed, and further right on the page is further down.
        string content = Up("F1", 10, 140, 72, "Body text below the heading.") + Up("F2", 20, 100, 72, "Turned heading");
        var (result, tagged) = await Tag(Page(content, pageExtra: "/Rotate 90"));
        using var _ = tagged;
        Assert.Equal(new[] { "H1: Turned heading", "P: Body text below the heading." }, Outline(tagged, Tree(tagged)));
        Assert.Equal(1, result.Summary.RotatedPages);
    }

    // ------------------------------------------------------------------ tables

    [Fact]
    public async Task RuledTable_BecomesTableWithHeaderCells()
    {
        var sb = new StringBuilder("0.5 w 0 0 0 RG\n");
        foreach (int y in new[] { 700, 680, 660, 640 }) sb.Append($"72 {y} m 372 {y} l S\n");
        foreach (int x in new[] { 72, 172, 272, 372 }) sb.Append($"{x} 640 m {x} 700 l S\n");
        string[][] cells = { new[] { "Name", "Qty", "Price" }, new[] { "Apple", "3", "1.20" }, new[] { "Pear", "5", "0.80" } };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) sb.Append(T(r == 0 ? "F2" : "F1", 10, 77 + 100 * c, 686 - 20 * r, cells[r][c]));
        sb.Append(T("F1", 10, 72, 600, "Prices are per kilogram."));
        var (result, tagged) = await Tag(Page(sb.ToString()));
        using var _ = tagged;
        Assert.Equal(new[]
        {
            "Table",
            "  TR", "    TH: Name", "    TH: Qty", "    TH: Price",
            "  TR", "    TD: Apple", "    TD: 3", "    TD: 1.20",
            "  TR", "    TD: Pear", "    TD: 5", "    TD: 0.80",
            "P: Prices are per kilogram.",
        }, Outline(tagged, Tree(tagged)));
        var th = Tree(tagged).Kids[0].Kids[0].Kids[0];
        var a = Assert.IsType<PdfDictionary>(tagged.Resolver.Resolve(th.Dict["A"]));
        Assert.Equal("Column", a.GetName("Scope"));
        Assert.Equal(1, result.Summary.Tables);
        Assert.Equal(1, result.Summary.TablesWithHeaders);
        Assert.DoesNotContain(result.Report!.Errors, i => i.Category == PdfUaCategory.Tables);
    }

    [Fact]
    public async Task AlignedColumns_WithoutRules_BecomeATable()
    {
        var sb = new StringBuilder();
        string[][] rows = { new[] { "City", "Population", "Area" }, new[] { "Oslo", "709000", "454" }, new[] { "Bergen", "286000", "465" }, new[] { "Tromso", "77000", "2521" } };
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < 3; c++) sb.Append(T(r == 0 ? "F2" : "F1", 10, 72 + 140 * c, 700 - 16 * r, rows[r][c]));
        var (result, tagged) = await Tag(Page(sb.ToString()));
        using var _ = tagged;
        var outline = Outline(tagged, Tree(tagged));
        Assert.Equal("Table", outline[0]);
        Assert.Equal("    TH: City", outline[2]);
        Assert.Equal("    TD: 2521", outline[^1]);
        Assert.Equal(1, result.Summary.Tables);
    }

    [Fact]
    public async Task TwoColumnProse_IsNotATable()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 6; i++)
        {
            sb.Append(T("F1", 10, 72, 700 - 12 * i, "Prose in the left column runs on and on here"));
            sb.Append(T("F1", 10, 330, 700 - 12 * i, "Prose in the right column also runs on and on"));
        }
        var (result, tagged) = await Tag(Page(sb.ToString()));
        using var _ = tagged;
        Assert.Equal(0, result.Summary.Tables);
        Assert.Equal(new[] { "P", "P" }, Tree(tagged).Kids.Select(k => k.Type));
    }

    // ------------------------------------------------------------------ figures, links, furniture

    [Fact]
    public async Task Image_IsAFigureWithPlaceholderAltText_AndRulesAreArtifacts()
    {
        string image = "/XObject << /Im1 5 0 R >>";
        var b = new VectorPdfBuilder();
        b.AddPage("q 200 0 0 150 100 400 cm /Im1 Do Q\n0 0 1 RG 72 380 m 540 380 l S\n" + T("F1", 10, 100, 360, "Figure 1: a picture."),
            $"<< {Fonts} {image} >>", "[0 0 612 792]");
        int im = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 });
        Assert.Equal(5, im);
        var (result, tagged) = await Tag(b.Build());
        using var _ = tagged;
        var tree = Tree(tagged);
        Assert.Equal(new[] { "Figure", "P" }, tree.Kids.Select(k => k.Type));
        var figure = tree.Kids[0];
        Assert.Equal(PdfAutoTagOptions.DefaultAltPlaceholder, (tagged.Resolver.Resolve(figure.Dict["Alt"]) as PdfString)?.AsDecodedString());
        var bbox = Assert.IsType<PdfArray>((tagged.Resolver.Resolve(figure.Dict["A"]) as PdfDictionary)?["BBox"]);
        Assert.Equal(4, bbox.Count);
        var needing = Assert.Single(result.FiguresNeedingAlt);
        Assert.Equal(1, needing.Page);
        Assert.Contains(result.Review, r => r.Contains("placeholder alternate text", StringComparison.Ordinal));
        Assert.True(result.Summary.ArtifactSequences >= 1); // the rule

        // Without a placeholder the checker reports the figure, for a person to describe.
        var (bare, again) = await Tag(b.Build(), new PdfAutoTagOptions { FigureAltPlaceholder = null });
        using var __ = again;
        Assert.Contains(bare.Remaining, i => i.Checkpoint == "13-004" && i.Kind == PdfAutoTagIssueKind.HumanReview);
    }

    [Fact]
    public async Task FullPageImage_IsABackgroundArtifact()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("q 612 0 0 792 0 0 cm /Im1 Do Q\n" + T("F1", 10, 72, 700, "Text over a scanned background."), $"<< {Fonts} /XObject << /Im1 5 0 R >> >>", "[0 0 612 792]");
        b.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 200 });
        var (result, tagged) = await Tag(b.Build());
        using var _ = tagged;
        Assert.Equal(0, result.Summary.Figures);
        Assert.Equal(new[] { "P" }, Tree(tagged).Kids.Select(k => k.Type));
        Assert.Contains("/Artifact <</Type /Background >>BDC", McidContent(tagged, 0), StringComparison.Ordinal);
    }

    private static string McidContent(PdfVectorDocument doc, int page)
    {
        var decoder = new PdfStreamDecoder(null, doc.Resolver.Resolve);
        return string.Concat(doc.PageTree.Pages[page].Contents.Select(s => Encoding.Latin1.GetString(decoder.DecodeStream(s))));
    }

    [Fact]
    public async Task Link_IsTaggedWithItsText_ObjectReference_AndDescription()
    {
        var b = new VectorPdfBuilder();
        string content = "BT /F1 10 Tf 72 700 Td (Visit our ) Tj ET BT /F1 10 Tf 120 700 Td (website) Tj ET BT /F1 10 Tf 160 700 Td ( for more.) Tj ET";
        int page = b.AddPage(content, $"<< {Fonts} >>", "[0 0 612 792]", "/Annots [5 0 R]");
        b.Add($"<< /Type /Annot /Subtype /Link /Rect [118 696 158 710] /Border [0 0 0] /A << /S /URI /URI (https://example.org/) >> >>");
        var (result, tagged) = await Tag(b.Build());
        using var _ = tagged;
        var outline = Outline(tagged, Tree(tagged));
        Assert.Equal(new[] { "P: Visit our  for more.", "  Link: website" }, outline);
        var link = Tree(tagged).Kids[0].Kids[0];
        Assert.True(link.HasObjr);
        var r = tagged.Resolver;
        var annot = Assert.IsType<PdfDictionary>(r.Resolve((r.Resolve(tagged.PageTree.Pages[0].Dictionary["Annots"]) as PdfArray)![0]));
        Assert.Equal("website", (r.Resolve(annot["Contents"]) as PdfString)?.AsDecodedString());
        Assert.NotNull(annot["StructParent"]);
        Assert.Equal("S", tagged.PageTree.Pages[0].Dictionary.GetName("Tabs"));
        Assert.Equal(1, result.Summary.Links);
        Assert.DoesNotContain(result.Report!.Errors, i => i.Category == PdfUaCategory.Annotations);
        // The link's text keeps its place in the paragraph: before it, the link, after it.
        var p = Tree(tagged).Kids[0];
        var k = Assert.IsType<PdfArray>(r.Resolve(p.Dict["K"]));
        Assert.Equal(3, k.Count);
        Assert.IsType<PdfIndirectRef>(k[1]);
    }

    [Fact]
    public async Task RepeatedHeaderAndFooter_AndPageNumbers_AreArtifacts()
    {
        var b = new VectorPdfBuilder();
        for (int p = 1; p <= 3; p++)
            b.AddPage(T("F1", 9, 72, 760, "ACME Quarterly Report") + T("F1", 10, 72, 600, $"Body text of page {p} goes here.") + T("F1", 9, 290, 30, $"Page {p}"),
                $"<< {Fonts} >>", "[0 0 612 792]");
        var (result, tagged) = await Tag(b.Build());
        using var _ = tagged;
        var tree = Tree(tagged);
        Assert.Equal(new[] { "P", "P", "P" }, tree.Kids.Select(k => k.Type));
        Assert.Equal(6, result.Summary.HeadersAndFooters);
        string page2 = McidContent(tagged, 1);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Header >>BDC", page2, StringComparison.Ordinal);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer >>BDC", page2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Title_ComesFromTheDocumentInformation_WhenItHasOne()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(T("F2", 20, 72, 700, "Big heading") + T("F1", 10, 72, 670, "Some text."), $"<< {Fonts} >>", "[0 0 612 792]");
        int info = b.Add("<< /Title (Given title) >>");
        b.WithTrailer($"/Info {info} 0 R");
        var (_, tagged) = await Tag(b.Build());
        using var _ = tagged;
        var xmp = PdfEngine.Vector.PdfA.PdfAValidator.ReadMetadata(tagged)!;
        Assert.Contains("Given title", xmp.Get(PdfEngine.Vector.PdfA.Xmp.XmpSchemas.Dc, "title")!.Value.Items[0].Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ refusals and re-tagging

    private static byte[] AlreadyTagged()
    {
        var b = new VectorPdfBuilder();
        int page = b.AddPage("/P <</MCID 0>> BDC " + T("F1", 10, 72, 700, "Tagged text.") + "EMC", $"<< {Fonts} >>", "[0 0 612 792]", "/StructParents 0");
        int p = b.Add($"<< /Type /StructElem /S /P /Pg {page} 0 R /K 0 >>");
        int doc = b.Add($"<< /Type /StructElem /S /Document /K [{p} 0 R] >>");
        int tree = b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R /ParentTree << /Nums [0 [{p} 0 R]] >> >>");
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {tree} 0 R");
        return b.Build();
    }

    [Fact]
    public async Task AlreadyTagged_IsRefused_UnlessRetagging()
    {
        byte[] pdf = AlreadyTagged();
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var refused = PdfAutoTagger.Tag(doc);
            Assert.Equal(PdfAutoTagStatus.AlreadyTagged, refused.Status);
            Assert.Null(refused.Bytes);
            Assert.True(PdfAutoTagger.IsTagged(doc));
        }
        var (result, tagged) = await Tag(pdf, new PdfAutoTagOptions { Retag = true });
        using var _ = tagged;
        Assert.Equal(new[] { "P: Tagged text." }, Outline(tagged, Tree(tagged)));
        // The old MCID is gone: one sequence with an MCID, owned by the new tree.
        Assert.Single(Regex.Matches(McidContent(tagged, 0), "/MCID"));
        Assert.DoesNotContain(result.Report!.Errors, i => i.Checkpoint == "01-005");
    }

    [Fact]
    public async Task EncryptedWithoutPermissionToChange_IsRefused_WithPermission_StaysEncrypted()
    {
        byte[] Build(int permissions)
        {
            var b = new VectorPdfBuilder();
            b.AddPage(T("F1", 10, 72, 700, "Secret text."), $"<< {Fonts} >>", "[0 0 612 792]");
            return b.Build(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner", permissions: permissions));
        }
        using (var doc = await PdfVectorDocument.OpenAsync(Build(unchecked((int)0xFFFFF0C4))))
            Assert.Equal(PdfAutoTagStatus.NotPermitted, PdfAutoTagger.Tag(doc).Status);
        var (result, tagged) = await Tag(Build(-4));
        using var _ = tagged;
        Assert.True(tagged.IsEncrypted);
        Assert.Equal(new[] { "P: Secret text." }, Outline(tagged, Tree(tagged)));
    }

    [Fact]
    public async Task UnreadableContent_IsRefusedWithThePage()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(T("F1", 10, 72, 700, "Fine."), $"<< {Fonts} >>", "[0 0 612 792]");
        b.AddPage(T("F1", 10, 72, 700, "Broken.") + "BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001", $"<< {Fonts} >>", "[0 0 612 792]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var result = PdfAutoTagger.Tag(doc);
        Assert.Equal(PdfAutoTagStatus.ContentUnreadable, result.Status);
        Assert.Equal(2, result.Page);
        Assert.DoesNotContain("Broken", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidLanguage_IsRejected()
    {
        using var doc = PdfVectorDocument.Open(new PdfEngine.Vector.Parsing.MemoryByteSource(Page(T("F1", 10, 72, 700, "x"))), "");
        Assert.Throws<ArgumentException>(() => PdfAutoTagger.Tag(doc, new PdfAutoTagOptions { Language = "en_GB" }));
    }

    [Fact]
    public async Task ExistingArtifactsAndActualText_AreKept()
    {
        // A header the producer already marked, and a ligature with /ActualText across two operators.
        string content = "/Artifact BMC " + T("F1", 9, 72, 760, "Producer header") + "EMC\n"
                         + "BT /F1 10 Tf 72 700 Td /Span <</ActualText (ffi)>> BDC (f) Tj (f) Tj (i) Tj EMC ( rest of the line.) Tj ET";
        var (_, tagged) = await Tag(Page(content));
        using var _ = tagged;
        string drawn = McidContent(tagged, 0);
        Assert.Contains("/Artifact BMC", drawn, StringComparison.Ordinal);
        Assert.Contains("/Span <</ActualText (ffi)>> BDC", drawn, StringComparison.Ordinal);
        Assert.Equal(new[] { "P" }, Tree(tagged).Kids.Select(k => k.Type));
    }
}
