using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Export to Word: the package is valid Office Open XML, the text comes over in reading order
/// with its fonts and styles, and columns, tables, lists, images, links, headings, headers and
/// footers become their Word counterparts.
/// </summary>
public class DocxExportTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    private const string HelvBold = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>";
    private const string TimesItalic = "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >>";
    private const string Letter = "[0 0 612 792]";

    // ------------------------------------------------------------------ fixtures

    private static (VectorPdfBuilder B, string Res) Fonts()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv), f2 = b.Add(HelvBold), f3 = b.Add(TimesItalic);
        // An embedded font's subset name: the family is what comes before the style.
        int f4 = b.Add("<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Calibri-Bold /FirstChar 32 /LastChar 126 /Widths [" +
                       string.Join(' ', Enumerable.Repeat("500", 95)) + "] /Encoding /WinAnsiEncoding >>");
        return (b, $"/Font << /F1 {f1} 0 R /F2 {f2} 0 R /F3 {f3} 0 R /F4 {f4} 0 R >>");
    }

    private static byte[] OnePage(string content, string extraResources = "", string pageExtra = "")
    {
        var (b, res) = Fonts();
        b.AddPage(content, $"<< {res} {extraResources} >>", Letter, pageExtra);
        return b.Build();
    }

    private static string Lines(double x, double y, double size, double leading, IEnumerable<string> lines, string font = "F1") =>
        $"BT /{font} {size} Tf {leading} TL {x} {y} Td " + string.Join(" T* ", lines.Select(l => $"({l}) Tj")) + " ET\n";

    private static readonly string[] Prose =
    {
        "The committee met on Tuesday to review", "the budget for the coming year and agreed", "to fund the new library building, which",
        "will open to the public next spring after", "two years of planning and construction.",
    };

    // ------------------------------------------------------------------ helpers

    private static async Task<(byte[] Docx, DocxExportResult Result)> Export(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var output = new MemoryStream();
        var progress = new List<int>();
        var result = await DocxExporter.ExportAsync(doc, output, new Progress<int>(progress.Add));
        return (output.ToArray(), result);
    }

    /// <summary>Opens the package with System.IO.Packaging and returns every XML part by name.</summary>
    private static Dictionary<string, XDocument> Parts(byte[] docx)
    {
        var parts = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        using var package = Package.Open(new MemoryStream(docx), FileMode.Open, FileAccess.Read);
        foreach (var part in package.GetParts())
        {
            if (!part.ContentType.Contains("xml", StringComparison.Ordinal)) continue;
            using var s = part.GetStream();
            parts[part.Uri.OriginalString] = XDocument.Load(s);
        }
        return parts;
    }

    private static XDocument Document(byte[] docx) => Parts(docx)["/word/document.xml"];

    private static string ParagraphText(XElement p) =>
        string.Concat(p.Descendants().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : string.Empty));

    private static List<string> BodyParagraphs(XDocument doc) =>
        doc.Root!.Element(W + "body")!.Descendants(W + "p").Where(p => p.Ancestors(W + "tbl").Count() == 0).Select(ParagraphText).ToList();

    private static List<string> Words(string s) => Regex.Matches(s, @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();

    private static string? Val(XElement? e, string attr = "val") => e?.Attribute(W + attr)?.Value;

    /// <summary>The children of every pPr, rPr, sectPr and tcPr come in the order the schema gives (Word rejects others).</summary>
    private static void AssertSchemaOrder(XDocument doc)
    {
        var orders = new Dictionary<string, string[]>
        {
            ["pPr"] = new[] { "pStyle", "keepNext", "keepLines", "pageBreakBefore", "widowControl", "numPr", "shd", "tabs", "bidi", "spacing", "ind", "jc", "outlineLvl", "rPr", "sectPr" },
            ["rPr"] = new[] { "rStyle", "rFonts", "b", "bCs", "i", "iCs", "strike", "color", "w", "sz", "szCs", "u", "vertAlign", "rtl" },
            ["sectPr"] = new[] { "headerReference", "footerReference", "type", "pgSz", "pgMar", "pgNumType", "cols", "titlePg", "textDirection" },
            ["tcPr"] = new[] { "tcW", "gridSpan", "vMerge", "tcBorders", "shd", "noWrap", "vAlign" },
            ["tblPr"] = new[] { "tblW", "tblInd", "tblLayout", "tblCellMar", "tblLook" },
        };
        foreach (var (name, order) in orders)
            foreach (var e in doc.Descendants(W + name))
            {
                var seen = e.Elements().Select(c => Array.IndexOf(order, c.Name.LocalName)).ToList();
                Assert.DoesNotContain(-1, seen);
                Assert.True(seen.SequenceEqual(seen.OrderBy(i => i)), $"{name} children out of order: {string.Join(",", e.Elements().Select(c => c.Name.LocalName))}");
            }
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Package_IsValidOpenXml()
    {
        var pdf = OnePage(Lines(72, 700, 12, 14, Prose));
        var (docx, result) = await Export(pdf);
        using (var package = Package.Open(new MemoryStream(docx), FileMode.Open, FileAccess.Read))
        {
            var main = package.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument").Single();
            Assert.Equal("/word/document.xml", main.TargetUri.OriginalString.StartsWith('/') ? main.TargetUri.OriginalString : "/" + main.TargetUri.OriginalString);
            var document = package.GetPart(new Uri("/word/document.xml", UriKind.Relative));
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", document.ContentType);
            var rels = document.GetRelationships().ToList();
            foreach (string type in new[] { "styles", "settings", "fontTable" })
            {
                var rel = rels.Single(r => r.RelationshipType == "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type);
                Assert.True(package.PartExists(PackUriHelper.ResolvePartUri(document.Uri, rel.TargetUri)));
            }
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml",
                package.GetPart(new Uri("/word/styles.xml", UriKind.Relative)).ContentType);
            Assert.True(package.PartExists(new Uri("/docProps/core.xml", UriKind.Relative)));
        }
        var doc = Document(docx);
        Assert.Equal(W + "document", doc.Root!.Name);
        var body = doc.Root.Element(W + "body")!;
        Assert.Equal(W + "sectPr", body.Elements().Last().Name);
        Assert.All(body.Elements().SkipLast(1), e => Assert.Contains(e.Name, new[] { W + "p", W + "tbl" }));
        var pgSz = body.Elements().Last().Element(W + "pgSz")!;
        Assert.Equal("12240", Val(pgSz, "w"));
        Assert.Equal("15840", Val(pgSz, "h"));
        AssertSchemaOrder(doc);
        Assert.Equal(1, result.Pages);
        Assert.True(result.Paragraphs >= 1);
    }

    [Fact]
    public async Task Text_ComesOverInReadingOrder()
    {
        var b = new VectorPdfBuilder();
        int f = b.Add(Helv);
        var pages = new[]
        {
            Lines(72, 720, 18, 20, new[] { "Quarterly report" }) + Lines(72, 680, 11, 13, Prose),
            // Drawn bottom first: reading order is top to bottom anyway.
            Lines(72, 400, 11, 13, new[] { "The second paragraph comes last." }) + Lines(72, 700, 11, 13, new[] { "The first paragraph comes first." }),
        };
        foreach (var c in pages) b.AddPage(c, $"<< /Font << /F1 {f} 0 R >> >>", Letter);
        var pdf = b.Build();
        var (docx, _) = await Export(pdf);
        var docWords = Words(string.Join("\n", BodyParagraphs(Document(docx))));

        using var engine = new PdfiumEngine();
        await using var pd = await engine.OpenDocumentAsync(pdf, null);
        var expected = new List<string>();
        for (int page = 1; page <= pd.PageCount; page++)
        {
            var pageWords = Words(await engine.TextService.ExtractPageTextAsync(pd, page));
            if (page == 2) pageWords = Words("The first paragraph comes first. The second paragraph comes last.");
            expected.AddRange(pageWords);
        }
        // Every word, in order.
        int at = 0;
        foreach (var w in expected)
        {
            int found = docWords.IndexOf(w, at);
            Assert.True(found >= 0, $"word {expected.IndexOf(w)} missing or out of order");
            at = found + 1;
        }
        Assert.Equal(expected.Count, docWords.Count);
    }

    [Fact]
    public async Task Fonts_SizesAndStyles_AreCarried()
    {
        var pdf = OnePage(
            "BT /F2 16 Tf 72 700 Td (Bold heading here) Tj ET\n" +
            "BT /F1 11 Tf 72 670 Td (Plain words and ) Tj /F3 11 Tf (italic words) Tj /F1 11 Tf ( again.) Tj ET\n" +
            "BT /F4 12 Tf 72 640 Td (Subset font line) Tj ET\n" +
            "BT 1 0 0 rg /F1 10 Tf 72 610 Td (Red text) Tj ET\n");
        var (docx, _) = await Export(pdf);
        var doc = Document(docx);
        AssertSchemaOrder(doc);
        var runs = doc.Descendants(W + "r").Where(r => r.Element(W + "t") != null).ToList();
        XElement RunWith(string text) => runs.First(r => r.Element(W + "t")!.Value.Contains(text, StringComparison.Ordinal));
        string? Font(XElement r) => r.Element(W + "rPr")!.Element(W + "rFonts")!.Attribute(W + "ascii")!.Value;
        string? Prop(XElement r, string name) => Val(r.Element(W + "rPr")!.Element(W + name));

        var bold = RunWith("Bold heading");
        Assert.Equal("Arial", Font(bold));
        Assert.Equal("1", Prop(bold, "b"));
        Assert.Equal("32", Prop(bold, "sz"));

        var italic = RunWith("italic words");
        Assert.Equal("Times New Roman", Font(italic));
        Assert.Equal("1", Prop(italic, "i"));
        Assert.Equal("0", Prop(italic, "b"));
        Assert.Equal("22", Prop(italic, "sz"));
        // One paragraph with three runs: plain, italic, plain.
        var mixed = italic.Parent!;
        Assert.Equal("Plain words and italic words again.", ParagraphText(mixed));
        Assert.Equal(3, mixed.Elements(W + "r").Count());

        var subset = RunWith("Subset font");
        Assert.Equal("Calibri", Font(subset));
        Assert.Equal("1", Prop(subset, "b"));
        Assert.Equal("24", Prop(subset, "sz"));
        Assert.Equal("FF0000", Prop(RunWith("Red text"), "color"));
    }

    [Fact]
    public async Task TwoColumnPage_BecomesSectionColumns()
    {
        var left = Enumerable.Range(1, 18).Select(i => $"Left column line {i} of the running text").ToList();
        var right = Enumerable.Range(1, 18).Select(i => $"Right column line {i} of the text too").ToList();
        var pdf = OnePage(Lines(72, 740, 18, 20, new[] { "A title that runs across the whole page width here" }, "F2")
                          + Lines(72, 700, 10, 12, left) + Lines(324, 700, 10, 12, right));
        var (docx, result) = await Export(pdf);
        var doc = Document(docx);
        AssertSchemaOrder(doc);
        var cols = doc.Descendants(W + "cols").Where(c => Val(c, "num") == "2").ToList();
        Assert.Single(cols);
        var widths = cols[0].Elements(W + "col").Select(c => int.Parse(Val(c, "w")!)).ToList();
        Assert.Equal(2, widths.Count);
        Assert.All(widths, w => Assert.InRange(w, 3000, 5400));
        // The title is in a single-column section before; the right column starts after a column break.
        var text = BodyParagraphs(doc);
        Assert.True(text.FindIndex(t => t.StartsWith("A title", StringComparison.Ordinal)) < text.FindIndex(t => t.StartsWith("Left column line 1 ", StringComparison.Ordinal)));
        Assert.True(text.FindLastIndex(t => t.StartsWith("Left column", StringComparison.Ordinal)) < text.FindIndex(t => t.StartsWith("Right column", StringComparison.Ordinal)));
        Assert.Contains(doc.Descendants(W + "br"), br => Val(br, "type") == "column");
        Assert.True(result.Sections >= 2);
        Assert.Equal("continuous", Val(doc.Root!.Element(W + "body")!.Elements().Last().Element(W + "type")));
    }

    [Fact]
    public async Task RuledTable_BecomesWordTable()
    {
        var grid = new StringBuilder("0.5 w 0 G\n");
        double[] xs = { 72, 200, 330, 460 }, ys = { 600, 580, 560, 540 };
        foreach (double y in ys) grid.Append($"72 {y} m 460 {y} l S\n");
        foreach (double x in xs) grid.Append($"{x} 600 m {x} 540 l S\n");
        string[,] cells = { { "Name", "Age", "City" }, { "Alice", "30", "Paris" }, { "Bob", "25", "Rome" } };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                grid.Append($"BT /F1 10 Tf {xs[c] + 4} {ys[r + 1] + 6} Td ({cells[r, c]}) Tj ET\n");
        var pdf = OnePage(Lines(72, 700, 11, 13, new[] { "The table below lists the members." }) + grid);
        var (docx, result) = await Export(pdf);
        var doc = Document(docx);
        AssertSchemaOrder(doc);
        var table = Assert.Single(doc.Descendants(W + "tbl"));
        var rows = table.Elements(W + "tr").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, table.Element(W + "tblGrid")!.Elements(W + "gridCol").Count());
        for (int r = 0; r < 3; r++)
        {
            var tcs = rows[r].Elements(W + "tc").ToList();
            Assert.Equal(3, tcs.Count);
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(cells[r, c], string.Concat(tcs[c].Elements(W + "p").Select(ParagraphText)));
                Assert.Equal("single", Val(tcs[c].Element(W + "tcPr")!.Element(W + "tcBorders")!.Element(W + "top")));
            }
        }
        Assert.Equal(1, result.Tables);
        // The cell text is not in the flow as well.
        Assert.DoesNotContain(BodyParagraphs(doc), t => t.Contains("Alice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergedCell_SpansColumns()
    {
        var grid = new StringBuilder("0.5 w\n");
        foreach (double y in new[] { 600, 580, 560 }) grid.Append($"72 {y} m 330 {y} l S\n");
        grid.Append("72 600 m 72 560 l S 330 600 m 330 560 l S 200 580 m 200 560 l S\n"); // the middle line stops at the first row
        grid.Append("BT /F1 10 Tf 150 586 Td (Heading across) Tj ET BT /F1 10 Tf 76 566 Td (Left) Tj ET BT /F1 10 Tf 204 566 Td (Right) Tj ET\n");
        var (docx, _) = await Export(OnePage(grid.ToString()));
        var table = Assert.Single(Document(docx).Descendants(W + "tbl"));
        var first = table.Elements(W + "tr").First().Elements(W + "tc").ToList();
        Assert.Single(first);
        Assert.Equal("2", Val(first[0].Element(W + "tcPr")!.Element(W + "gridSpan")));
        Assert.Equal(2, table.Elements(W + "tr").Last().Elements(W + "tc").Count());
    }

    [Fact]
    public async Task Lists_BecomeNumbering()
    {
        var pdf = OnePage(
            Lines(72, 700, 11, 14, new[] { "Shopping:" }) +
            Lines(72, 680, 11, 14, new[] { "\\225 Apples", "\\225 Pears", "\\225 Plums" }) +
            Lines(72, 620, 11, 14, new[] { "Steps to follow:" }) +
            Lines(72, 600, 11, 14, new[] { "1. Open the box", "2. Take out the parts", "3. Read the manual" }));
        var (docx, result) = await Export(pdf);
        var parts = Parts(docx);
        var doc = parts["/word/document.xml"];
        AssertSchemaOrder(doc);
        Assert.True(parts.ContainsKey("/word/numbering.xml"));
        var numbering = parts["/word/numbering.xml"];
        var items = doc.Descendants(W + "p").Where(p => p.Element(W + "pPr")?.Element(W + "numPr") != null).ToList();
        Assert.Equal(6, items.Count);
        Assert.Equal(new[] { "Apples", "Pears", "Plums", "Open the box", "Take out the parts", "Read the manual" }, items.Select(ParagraphText));
        var ids = items.Select(p => Val(p.Element(W + "pPr")!.Element(W + "numPr")!.Element(W + "numId"))).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        string Format(string numId)
        {
            var abs = Val(numbering.Root!.Elements(W + "num").Single(n => Val(n, "numId") == numId).Element(W + "abstractNumId"));
            var lvl = numbering.Root.Elements(W + "abstractNum").Single(a => Val(a, "abstractNumId") == abs).Elements(W + "lvl").First();
            return Val(lvl.Element(W + "numFmt"))! + " " + Val(lvl.Element(W + "lvlText"));
        }
        Assert.Equal("bullet •", Format(ids[0]!));
        Assert.Equal("decimal %1.", Format(ids[3]!));
        Assert.Equal(2, result.Lists);
        // Numbering comes before the abstract definitions' users, as the schema wants.
        Assert.True(numbering.Root!.Elements().TakeWhile(e => e.Name == W + "abstractNum").Count() == 2);
    }

    [Fact]
    public async Task Images_AreEmbedded()
    {
        var (b, res) = Fonts();
        var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
        int img = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels);
        b.AddPage(Lines(72, 700, 11, 13, new[] { "A picture follows." }) + "q 144 0 0 96 72 560 cm /Im1 Do Q\n" + Lines(72, 530, 11, 13, new[] { "And text after it." }),
            $"<< {res} /XObject << /Im1 {img} 0 R >> >>", Letter);
        var (docx, result) = await Export(b.Build());
        Assert.Equal(1, result.Images);
        using var package = Package.Open(new MemoryStream(docx), FileMode.Open, FileAccess.Read);
        var document = package.GetPart(new Uri("/word/document.xml", UriKind.Relative));
        var rel = document.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image").Single();
        var media = package.GetPart(PackUriHelper.ResolvePartUri(document.Uri, rel.TargetUri));
        Assert.Equal("image/png", media.ContentType);
        using (var s = media.GetStream())
        {
            var sig = new byte[8];
            s.ReadExactly(sig);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, sig);
        }
        XDocument doc;
        using (var s = document.GetStream()) doc = XDocument.Load(s);
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing", a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var inline = Assert.Single(doc.Descendants(wp + "inline"));
        Assert.Equal((144 * 12700).ToString(), inline.Element(wp + "extent")!.Attribute("cx")!.Value);
        Assert.Equal(rel.Id, doc.Descendants(a + "blip").Single().Attribute(R + "embed")!.Value);
        // In the flow, between the paragraphs it sits between.
        var body = doc.Root!.Element(W + "body")!.Elements(W + "p").ToList();
        int picture = body.FindIndex(p => p.Descendants(wp + "inline").Any());
        Assert.True(body.FindIndex(p => ParagraphText(p).StartsWith("A picture", StringComparison.Ordinal)) < picture);
        Assert.True(body.FindIndex(p => ParagraphText(p).StartsWith("And text", StringComparison.Ordinal)) > picture);
    }

    [Fact]
    public async Task HeadersAndFooters_AreDetected()
    {
        var b = new VectorPdfBuilder();
        int f = b.Add(Helv);
        for (int page = 1; page <= 3; page++)
            b.AddPage(Lines(72, 760, 9, 11, new[] { "Annual Report 2025" }) + Lines(72, 700, 11, 13, new[] { $"Body text of page {page} goes here." }) +
                      Lines(290, 30, 9, 11, new[] { $"Page {page} of 3" }), $"<< /Font << /F1 {f} 0 R >> >>", Letter);
        var (docx, result) = await Export(b.Build());
        Assert.True(result.HasHeaderOrFooter);
        var parts = Parts(docx);
        var doc = parts["/word/document.xml"];
        AssertSchemaOrder(doc);
        var header = parts.Where(p => p.Key.StartsWith("/word/header", StringComparison.Ordinal)).Select(p => p.Value).ToList();
        var footer = parts.Where(p => p.Key.StartsWith("/word/footer", StringComparison.Ordinal)).Select(p => p.Value).ToList();
        Assert.Contains(header, h => h.Descendants(W + "p").Any(p => ParagraphText(p) == "Annual Report 2025"));
        var footerPara = footer.SelectMany(x => x.Descendants(W + "p")).First(p => ParagraphText(p).StartsWith("Page", StringComparison.Ordinal));
        var fields = footerPara.Descendants(W + "fldSimple").Select(e => e.Attribute(W + "instr")!.Value.Trim()).ToList();
        Assert.Equal(new[] { "PAGE", "NUMPAGES" }, fields);
        // Taken off the pages: the body has the body text only, one page after another.
        var body = BodyParagraphs(doc).Where(t => t.Length > 0).ToList();
        Assert.Equal(new[] { "Body text of page 1 goes here.", "Body text of page 2 goes here.", "Body text of page 3 goes here." }, body);
        var sectPr = doc.Root!.Element(W + "body")!.Elements().Last();
        Assert.NotNull(sectPr.Elements(W + "headerReference").SingleOrDefault(h => Val(h, "type") == "default"));
        Assert.NotNull(sectPr.Elements(W + "footerReference").SingleOrDefault(h => Val(h, "type") == "default"));
        Assert.Equal(2, doc.Descendants(W + "pageBreakBefore").Count());
    }

    [Fact]
    public async Task Links_BecomeHyperlinks()
    {
        var (b, res) = Fonts();
        int annot = b.Add("<< /Type /Annot /Subtype /Link /Rect [70 395 200 412] /A << /S /URI /URI (https://example.com/docs) >> >>");
        b.AddPage("BT /F1 11 Tf 72 400 Td (Visit the site) Tj ( for more.) Tj ET\n", $"<< {res} >>", Letter, $"/Annots [{annot} 0 R]");
        var (docx, result) = await Export(b.Build());
        Assert.Equal(1, result.Hyperlinks);
        using var package = Package.Open(new MemoryStream(docx), FileMode.Open, FileAccess.Read);
        var document = package.GetPart(new Uri("/word/document.xml", UriKind.Relative));
        var rel = document.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink").Single();
        Assert.Equal(TargetMode.External, rel.TargetMode);
        Assert.Equal("https://example.com/docs", rel.TargetUri.OriginalString);
        XDocument doc;
        using (var s = document.GetStream()) doc = XDocument.Load(s);
        var link = Assert.Single(doc.Descendants(W + "hyperlink"));
        Assert.Equal(rel.Id, link.Attribute(R + "id")!.Value);
        Assert.StartsWith("Visit the site", ParagraphText(link));
    }

    [Fact]
    public async Task Headings_FromLargerBoldLines()
    {
        var pdf = OnePage(Lines(72, 720, 20, 24, new[] { "Introduction" }, "F2") + Lines(72, 690, 11, 13, Prose) +
                          Lines(72, 600, 14, 16, new[] { "Background" }, "F2") + Lines(72, 575, 11, 13, Prose));
        var (docx, result) = await Export(pdf);
        var doc = Document(docx);
        var styled = doc.Descendants(W + "p").Select(p => (Style: Val(p.Element(W + "pPr")?.Element(W + "pStyle")), Text: ParagraphText(p))).Where(x => x.Style != null).ToList();
        Assert.Contains(("Heading1", "Introduction"), styled);
        Assert.Contains(("Heading2", "Background"), styled);
        Assert.Equal(2, result.Headings);
        var styles = Parts(docx)["/word/styles.xml"];
        Assert.Contains(styles.Descendants(W + "style"), s => Val(s, "styleId") == "Heading1" && Val(s.Element(W + "name")) == "heading 1");
    }

    [Fact]
    public async Task TaggedDocument_UsesTheStructureTree()
    {
        var b = new VectorPdfBuilder();
        int f = b.Add(Helv);
        // Tagged as a heading although it looks like body text.
        string content = "/H1 << /MCID 0 >> BDC BT /F1 11 Tf 72 700 Td (Scope of the work) Tj ET EMC\n" +
                         "/P << /MCID 1 >> BDC " + Lines(72, 680, 11, 13, Prose) + "EMC\n";
        int page = b.AddPage(content, $"<< /Font << /F1 {f} 0 R >> >>", Letter, "/StructParents 0");
        int h1 = b.Add($"<< /Type /StructElem /S /Heading /Pg {page} 0 R /K 0 >>");
        int p = b.Add($"<< /Type /StructElem /S /P /Pg {page} 0 R /K 1 >>");
        int root = b.Add($"<< /Type /StructTreeRoot /RoleMap << /Heading /H1 >> /K [{h1} 0 R {p} 0 R] >>");
        b.WithCatalog($"/StructTreeRoot {root} 0 R /MarkInfo << /Marked true >>");
        var (docx, result) = await Export(b.Build());
        Assert.True(result.UsedTags);
        var doc = Document(docx);
        var heading = doc.Descendants(W + "p").Single(x => ParagraphText(x) == "Scope of the work");
        Assert.Equal("Heading1", Val(heading.Element(W + "pPr")!.Element(W + "pStyle")));
    }

    [Fact]
    public async Task RightToLeft_IsMarkedBidi()
    {
        var b = new VectorPdfBuilder();
        // A font whose codes map to Hebrew letters.
        int cmap = b.AddStream(string.Empty, "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T def 1 begincodespacerange <00> <FF> endcodespacerange " +
                                             "3 beginbfchar <41> <05E9> <42> <05DC> <43> <05D5> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end");
        int font = b.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode {cmap} 0 R >>");
        // Drawn left to right as the page shows it: the last letter first.
        b.AddPage("BT /F1 14 Tf 400 700 Td (CBA) Tj ET\n",$"<< /Font << /F1 {font} 0 R >> >>", Letter);
        var (docx, _) = await Export(b.Build());
        var doc = Document(docx);
        AssertSchemaOrder(doc);
        var para = doc.Descendants(W + "p").First(x => ParagraphText(x).Length > 0);
        Assert.NotNull(para.Element(W + "pPr")!.Element(W + "bidi"));
        Assert.Equal("שלו", ParagraphText(para));
        Assert.NotNull(para.Descendants(W + "rtl").FirstOrDefault());
    }

    [Fact]
    public async Task ExportToFile_LeavesNothingWhenCancelled()
    {
        var pdf = OnePage(Lines(72, 700, 12, 14, Prose));
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        string path = Path.Combine(Path.GetTempPath(), $"docx-export-{Guid.NewGuid():N}.docx");
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocxExporter.ExportAsync(doc, path, null, cts.Token));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
        var result = await DocxExporter.ExportAsync(doc, path);
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(1, result.Pages);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("ABCDEF+Calibri-Bold", "Calibri", true, false)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", "Times New Roman", true, true)]
    [InlineData("ArialMT", "Arial", false, false)]
    [InlineData("Helvetica-Oblique", "Arial", false, true)]
    [InlineData("Times-Roman", "Times New Roman", false, false)]
    [InlineData("CourierNewPSMT", "Courier New", false, false)]
    [InlineData("GillSansMT", "Gill Sans MT", false, false)]
    [InlineData("SegoeUI-Semibold", "Segoe UI", true, false)]
    [InlineData("MinionPro-Regular", "Minion Pro", false, false)]
    [InlineData("Arial,BoldItalic", "Arial", true, true)]
    public void FontNames_MapToWordFamilies(string postScript, string family, bool bold, bool italic)
    {
        var (f, b, i) = FontNames.Resolve(postScript, false, false);
        Assert.Equal(family, f);
        Assert.Equal(bold, b);
        Assert.Equal(italic, i);
    }
}
