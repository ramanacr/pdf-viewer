using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Export to Excel and PowerPoint: the packages are valid Office Open XML (they open with
/// System.IO.Packaging, every part parses, every relationship has its target), tables become
/// cells whose numbers, currency, percentages and dates are values with number formats, and
/// pages become slides of their size with text boxes, pictures, shapes and tables where the page has them.
/// </summary>
public class OfficeExportTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    private const string HelvBold = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>";

    // ------------------------------------------------------------------ fixtures

    private static string Text(double x, double y, string text, string font = "F1", double size = 10) =>
        FormattableString.Invariant($"BT /{font} {size} Tf {x} {y} Td ({text}) Tj ET\n");

    /// <summary>
    /// A ruled table: a title row across all four columns, a shaded header row in bold, and rows
    /// of an item, an amount in dollars (one negative in parentheses), a share in percent and a date.
    /// </summary>
    private static byte[] RuledTablePdf()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv), f2 = b.Add(HelvBold);
        double[] xs = { 72, 200, 320, 420, 540 }, ys = { 700, 680, 660, 640, 620, 600, 580 };
        var c = new StringBuilder();
        c.Append("0.85 g 72 660 468 20 re f 0 g\n");
        c.Append("0.5 w 0 G\n");
        foreach (double y in ys) c.Append(FormattableString.Invariant($"72 {y} m 540 {y} l S\n"));
        foreach (double x in xs) c.Append(FormattableString.Invariant($"{x} {(x is 72 or 540 ? 700 : 680)} m {x} 580 l S\n"));
        c.Append(Text(250, 686, "Quarterly results", "F2"));
        string[] head = { "Item", "Amount", "Share", "Date" };
        string[,] rows =
        {
            { "Widgets", "$1,234.50", "12.5%", "2024-03-15" },
            { "Gadgets", "($2,000.00)", "7%", "03/15/2024" },
            { "Gizmos", "-15", "0.25%", "04/01/2024" },
            { "Total", "1,234", "100%", "January 5, 2024" },
        };
        for (int k = 0; k < 4; k++) c.Append(Text(xs[k] + 4, 666, head[k], "F2"));
        for (int r = 0; r < 4; r++)
            for (int k = 0; k < 4; k++)
                c.Append(Text(xs[k] + 4, ys[r + 2] - 14, rows[r, k]));
        b.AddPage(c.ToString(), $"<< /Font << /F1 {f1} 0 R /F2 {f2} 0 R >> >>", "[0 0 612 792]");
        return b.Build();
    }

    /// <summary>Columns of figures with no lines: a region, a count and a share, row under row.</summary>
    private static byte[] BorderlessTablePdf()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv);
        var c = new StringBuilder(Text(72, 740, "Visitors by region in the last year were as follows."));
        string[,] rows = { { "Region", "Visitors", "Share" }, { "North", "1,200", "40%" }, { "South", "900", "30%" }, { "East", "600", "20%" }, { "West", "300", "10%" } };
        for (int r = 0; r < 5; r++)
        {
            double y = 700 - r * 16;
            c.Append(Text(72, y, rows[r, 0])).Append(Text(260, y, rows[r, 1])).Append(Text(400, y, rows[r, 2]));
        }
        b.AddPage(c.ToString(), $"<< /Font << /F1 {f1} 0 R >> >>", "[0 0 612 792]");
        return b.Build();
    }

    /// <summary>A landscape page laid out like a slide: a bold title, two lines of text, a picture, a filled rectangle, a line and text up its edge.</summary>
    private static byte[] SlidePdf()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv), f2 = b.Add(HelvBold);
        var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
        int img = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels);
        var c = new StringBuilder();
        c.Append(Text(72, 520, "Quarterly Review", "F2", 28));
        c.Append("BT /F1 14 Tf 18 TL 72 470 Td (Sales rose in every region this quarter) Tj T* (and costs held steady against the plan.) Tj ET\n");
        c.Append("q 160 0 0 120 560 300 cm /Im1 Do Q\n");
        c.Append("0.1 0.4 0.8 rg 72 200 300 60 re f\n");
        c.Append("1 0 0 RG 2 w 72 150 m 700 150 l S\n");
        c.Append("BT /F1 12 Tf 0 1 -1 0 40 200 Tm (Confidential draft) Tj ET\n");
        b.AddPage(c.ToString(), $"<< /Font << /F1 {f1} 0 R /F2 {f2} 0 R >> /XObject << /Im1 {img} 0 R >> >>", "[0 0 792 612]");
        return b.Build();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(byte[] Bytes, XlsxExportResult Result)> Xlsx(byte[] pdf, XlsxExportOptions? options = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var output = new MemoryStream();
        var result = await XlsxExporter.ExportAsync(doc, output, options);
        return (output.ToArray(), result);
    }

    private static async Task<(byte[] Bytes, PptxExportResult Result)> Pptx(byte[] pdf, PptxExportOptions? options = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var output = new MemoryStream();
        var result = await PptxExporter.ExportAsync(doc, output, options);
        return (output.ToArray(), result);
    }

    /// <summary>
    /// Opens the package with System.IO.Packaging, parses every XML part, and checks that every
    /// relationship inside it points at a part the package has; returns the XML parts by name.
    /// </summary>
    private static Dictionary<string, XDocument> Parts(byte[] bytes)
    {
        var parts = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        using var package = Package.Open(new MemoryStream(bytes), FileMode.Open, FileAccess.Read);
        foreach (var rel in package.GetRelationships())
            Assert.True(package.PartExists(PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), rel.TargetUri)), rel.TargetUri.ToString());
        foreach (var part in package.GetParts())
        {
            if (part.ContentType == "application/vnd.openxmlformats-package.relationships+xml") continue;
            foreach (var rel in part.GetRelationships().Where(r => r.TargetMode == TargetMode.Internal))
                Assert.True(package.PartExists(PackUriHelper.ResolvePartUri(part.Uri, rel.TargetUri)), $"{part.Uri} -> {rel.TargetUri}");
            if (!part.ContentType.Contains("xml", StringComparison.Ordinal)) continue;
            using var s = part.GetStream();
            parts[part.Uri.OriginalString] = XDocument.Load(s);
        }
        return parts;
    }

    private static string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? string.Empty;

    /// <summary>A worksheet's cells by reference, with their value as shown in the XML (shared strings resolved).</summary>
    private sealed record Cell(string Value, bool IsString, int Style);

    private static Dictionary<string, Cell> Cells(Dictionary<string, XDocument> parts, string sheet)
    {
        var strings = parts["/xl/sharedStrings.xml"].Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
        return parts[sheet].Descendants(S + "c").Where(c => c.Element(S + "v") != null).ToDictionary(c => Attr(c, "r"), c =>
        {
            bool str = Attr(c, "t") == "s";
            string v = c.Element(S + "v")!.Value;
            return new Cell(str ? strings[int.Parse(v, CultureInfo.InvariantCulture)] : v, str, int.TryParse(Attr(c, "s"), out int s) ? s : 0);
        });
    }

    private static string FormatOf(Dictionary<string, XDocument> parts, int style)
    {
        var styles = parts["/xl/styles.xml"].Root!;
        string id = Attr(styles.Element(S + "cellXfs")!.Elements(S + "xf").ElementAt(style), "numFmtId");
        return styles.Element(S + "numFmts")?.Elements(S + "numFmt").FirstOrDefault(f => Attr(f, "numFmtId") == id) is { } f ? Attr(f, "formatCode") : id;
    }

    private static double Serial(int y, int m, int d) => (new DateTime(y, m, d) - new DateTime(1899, 12, 30)).TotalDays;

    // ------------------------------------------------------------------ Excel

    [Fact]
    public async Task Xlsx_RuledTable_BecomesCellsWithValuesAndFormats()
    {
        var (bytes, result) = await Xlsx(RuledTablePdf(), new XlsxExportOptions { IncludeText = false });
        var parts = Parts(bytes);
        var workbook = parts["/xl/workbook.xml"];
        var sheet = Assert.Single(workbook.Descendants(S + "sheet"));
        Assert.Equal("Page 1 Table 1", Attr(sheet, "name"));
        Assert.Equal(1, result.Tables);
        var cells = Cells(parts, "/xl/worksheets/sheet1.xml");

        Assert.Equal("Quarterly results", cells["A1"].Value);
        Assert.Contains(parts["/xl/worksheets/sheet1.xml"].Descendants(S + "mergeCell"), m => Attr(m, "ref") == "A1:D1");
        Assert.Equal("Item", cells["A2"].Value);
        Assert.True(cells["A2"].IsString);

        // $1,234.50: a number shown in dollars with two decimals.
        Assert.False(cells["B3"].IsString);
        Assert.Equal(1234.5, double.Parse(cells["B3"].Value, CultureInfo.InvariantCulture));
        Assert.Equal("\"$\"#,##0.00", FormatOf(parts, cells["B3"].Style));
        // ($2,000.00): negative, shown in parentheses.
        Assert.Equal(-2000, double.Parse(cells["B4"].Value, CultureInfo.InvariantCulture));
        Assert.Contains("\\(", FormatOf(parts, cells["B4"].Style));
        Assert.Equal(-15, double.Parse(cells["B5"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(1234, double.Parse(cells["B6"].Value, CultureInfo.InvariantCulture));
        Assert.Equal("#,##0", FormatOf(parts, cells["B6"].Style));
        // Percentages are fractions shown as percent.
        Assert.Equal(0.125, double.Parse(cells["C3"].Value, CultureInfo.InvariantCulture));
        Assert.Equal("0.0%", FormatOf(parts, cells["C3"].Style));
        Assert.Equal(0.0025, double.Parse(cells["C5"].Value, CultureInfo.InvariantCulture), 10);
        Assert.Equal(1, double.Parse(cells["C6"].Value, CultureInfo.InvariantCulture));
        // Dates: ISO, month first (15 cannot be a month, so 04/01 in the same column is April 1), and written out.
        Assert.Equal(Serial(2024, 3, 15), double.Parse(cells["D3"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(Serial(2024, 3, 15), double.Parse(cells["D4"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(Serial(2024, 4, 1), double.Parse(cells["D5"].Value, CultureInfo.InvariantCulture));
        Assert.Equal("mm/dd/yyyy", FormatOf(parts, cells["D4"].Style));
        Assert.Equal(Serial(2024, 1, 5), double.Parse(cells["D6"].Value, CultureInfo.InvariantCulture));
        Assert.Equal("mmmm d, yyyy", FormatOf(parts, cells["D6"].Style));
        Assert.Equal(8, result.Numbers);
        Assert.Equal(4, result.Dates);
        Assert.Equal(1, result.MergedCells);

        // The header row: bold, shaded; every cell ruled; the PDF's widths and heights.
        var styles = parts["/xl/styles.xml"].Root!;
        var xf = styles.Element(S + "cellXfs")!.Elements(S + "xf").ElementAt(cells["A2"].Style);
        var font = styles.Element(S + "fonts")!.Elements(S + "font").ElementAt(int.Parse(Attr(xf, "fontId"), CultureInfo.InvariantCulture));
        Assert.NotNull(font.Element(S + "b"));
        Assert.Equal("10", Attr(font.Element(S + "sz")!, "val"));
        var fill = styles.Element(S + "fills")!.Elements(S + "fill").ElementAt(int.Parse(Attr(xf, "fillId"), CultureInfo.InvariantCulture));
        Assert.Equal("FFD9D9D9", Attr(fill.Descendants(S + "fgColor").Single(), "rgb"));
        var border = styles.Element(S + "borders")!.Elements(S + "border").ElementAt(int.Parse(Attr(xf, "borderId"), CultureInfo.InvariantCulture));
        Assert.Equal("thin", Attr(border.Element(S + "top")!, "style"));
        Assert.Equal("thin", Attr(border.Element(S + "left")!, "style"));
        var cols = parts["/xl/worksheets/sheet1.xml"].Descendants(S + "col").ToList();
        Assert.Equal(4, cols.Count);
        // 128 points is 170.7 pixels, 24.4 widths of a 7-pixel digit.
        Assert.InRange(double.Parse(Attr(cols[0], "width"), CultureInfo.InvariantCulture), 24, 25);
        var row = parts["/xl/worksheets/sheet1.xml"].Descendants(S + "row").First();
        Assert.Equal("1", Attr(row, "customHeight"));
        Assert.InRange(double.Parse(Attr(row, "ht"), CultureInfo.InvariantCulture), 19, 21);
    }

    [Fact]
    public async Task Xlsx_BorderlessAlignedTable_IsFound()
    {
        var (bytes, result) = await Xlsx(BorderlessTablePdf());
        var parts = Parts(bytes);
        Assert.Equal("Page 1 Table 1", Attr(parts["/xl/workbook.xml"].Descendants(S + "sheet").Single(), "name"));
        Assert.Equal(1, result.Tables);
        var cells = Cells(parts, "/xl/worksheets/sheet1.xml");
        // The sentence above the table is a row above it, then a blank row.
        Assert.StartsWith("Visitors by region", cells["A1"].Value, StringComparison.Ordinal);
        Assert.Equal(1, result.TextRows);
        Assert.Equal("Region", cells["A3"].Value);
        Assert.Equal(1200, double.Parse(cells["B4"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(0.4, double.Parse(cells["C4"].Value, CultureInfo.InvariantCulture));
        Assert.Equal("West", cells["A7"].Value);
        Assert.Equal(0.1, double.Parse(cells["C7"].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Xlsx_Layouts_NameTheirSheets()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv);
        b.AddPage(Text(72, 700, "A page of prose only."), $"<< /Font << /F1 {f1} 0 R >> >>", "[0 0 612 792]");
        // Two pages: a table without lines, then a page of text alone.
        var pdf = TwoPages();
        var (perPage, _) = await Xlsx(pdf, new XlsxExportOptions { Layout = XlsxSheetLayout.SheetPerPage });
        Assert.Equal(new[] { "Page 1", "Page 2" }, Parts(perPage)["/xl/workbook.xml"].Descendants(S + "sheet").Select(s => Attr(s, "name")));
        var (single, singleResult) = await Xlsx(pdf, new XlsxExportOptions { Layout = XlsxSheetLayout.SingleSheet, IncludeText = false });
        Assert.Equal("Tables", Attr(Parts(single)["/xl/workbook.xml"].Descendants(S + "sheet").Single(), "name"));
        Assert.Equal(0, singleResult.TextRows);
        var (perTable, _) = await Xlsx(pdf);
        Assert.Equal(new[] { "Page 1 Table 1", "Page 2" }, Parts(perTable)["/xl/workbook.xml"].Descendants(S + "sheet").Select(s => Attr(s, "name")));
        // Without text and without tables, the workbook still has the one sheet Excel needs.
        var (empty, emptyResult) = await Xlsx(b.Build(), new XlsxExportOptions { IncludeText = false });
        Assert.Single(Parts(empty)["/xl/workbook.xml"].Descendants(S + "sheet"));
        Assert.Equal(1, emptyResult.Sheets);
    }

    private static byte[] TwoPages()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv);
        var c = new StringBuilder();
        string[,] rows = { { "Region", "Visitors", "Share" }, { "North", "1,200", "40%" }, { "South", "900", "30%" }, { "East", "600", "20%" }, { "West", "300", "10%" } };
        for (int r = 0; r < 5; r++)
        {
            double y = 700 - r * 16;
            c.Append(Text(72, y, rows[r, 0])).Append(Text(260, y, rows[r, 1])).Append(Text(400, y, rows[r, 2]));
        }
        string res = $"<< /Font << /F1 {f1} 0 R >> >>";
        b.AddPage(c.ToString(), res, "[0 0 612 792]");
        b.AddPage(Text(72, 700, "A page of prose with no table on it."), res, "[0 0 612 792]");
        return b.Build();
    }

    [Theory]
    [InlineData("1,234,567.89", 1234567.89, "#,##0.00")]
    [InlineData("-42", -42, "")]
    [InlineData("3.5", 3.5, "")]
    [InlineData("2.50", 2.5, "0.00")]
    [InlineData("(15.25)", -15.25, "0.00;\\(0.00\\)")]
    [InlineData("− 7", -7, "")]
    [InlineData("€1.234,56", 1234.56, "\"€\"#,##0.00")]
    [InlineData("12 %", 0.12, "0%")]
    [InlineData("£300", 300, "\"£\"0")]
    public void CellValues_RecognizeNumbers(string text, double value, string format)
    {
        var v = CellValues.Parse(text);
        Assert.Equal(CellKind.Number, v.Kind);
        Assert.Equal(value, v.Number, 9);
        Assert.Equal(format, v.Format);
    }

    [Theory]
    [InlineData("02134")]       // a ZIP code
    [InlineData("1234567890123456")]
    [InlineData("555-1234")]
    [InlineData("(12")]
    [InlineData("12/03/2024")]  // the day could be either part
    [InlineData("2024-02-30")]
    [InlineData("Section 4")]
    public void CellValues_LeaveCodesAndDoubtfulDatesAsText(string text) =>
        Assert.Equal(CellKind.Text, CellValues.Parse(text).Kind);

    [Fact]
    public void CellValues_DatesFollowTheirColumn()
    {
        Assert.Equal(DateOrder.DayFirst, CellValues.Evidence("25/12/2024"));
        Assert.Equal(DateOrder.MonthFirst, CellValues.Evidence("12/25/2024"));
        Assert.Equal(DateOrder.Unknown, CellValues.Evidence("12/03/2024"));
        var day = CellValues.Parse("12/03/2024", DateOrder.DayFirst);
        Assert.Equal(CellKind.Date, day.Kind);
        Assert.Equal(Serial(2024, 3, 12), day.Number);
        Assert.Equal("dd/mm/yyyy", day.Format);
        var named = CellValues.Parse("5-Mar-2024");
        Assert.Equal(Serial(2024, 3, 5), named.Number);
        Assert.Equal("d\\-mmm\\-yyyy", named.Format);
    }

    [Fact]
    public void Xlsx_CellOfTwoParagraphs_KeepsItsLineBreak_AndHasNoEmptyRuns()
    {
        // Two paragraphs in one cell, in two looks, with a control character XML cannot hold on its own in a run.
        static DocParagraph Para(double top, params DocRun[] runs) => new()
        {
            Runs = runs.ToList(), Box = new Box(10, top, 90, top + 10), Lines = { new DocLine { Box = new Box(10, top, 90, top + 10), Baseline = top + 8, Size = 10 } },
        };
        var cell = new DocCell
        {
            Box = new Box(5, 5, 100, 40),
            Paragraphs =
            {
                Para(8, new DocRun { Text = "Bold", Bold = true, Size = 10 }, new DocRun { Text = "\u001F", Size = 10 }, new DocRun { Text = " plain", Size = 10 }),
                Para(24, new DocRun { Text = "Second", Size = 10 }),
            },
        };
        var table = new DocTable { Cols = new double[] { 5, 100 }, Rows = new double[] { 5, 40 }, Box = new Box(5, 5, 100, 40), Cells = { cell } };
        var output = new MemoryStream();
        using (var opc = new OpcPackage(output))
        {
            var writer = new XlsxWriter(opc);
            writer.AddSheet("Sheet", new DocItem[] { table });
            writer.Finish(null);
        }
        var parts = Parts(output.ToArray());
        var si = Assert.Single(parts["/xl/sharedStrings.xml"].Root!.Elements(S + "si"));
        Assert.Equal("Bold plain\nSecond", string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        Assert.DoesNotContain(si.Elements(S + "r"), r => r.Element(S + "t")!.Value.Length == 0);
        // The cell wraps, so its two lines show.
        var c = parts["/xl/worksheets/sheet1.xml"].Descendants(S + "c").Single();
        var xf = parts["/xl/styles.xml"].Root!.Element(S + "cellXfs")!.Elements(S + "xf").ElementAt(int.Parse(Attr(c, "s"), CultureInfo.InvariantCulture));
        Assert.Equal("1", Attr(xf.Element(S + "alignment")!, "wrapText"));
    }

    [Fact]
    public async Task Xlsx_ExportToFile_LeavesNothingWhenCancelled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "XlsxCancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var doc = await PdfVectorDocument.OpenAsync(RuledTablePdf());
            string path = Path.Combine(dir, "out.xlsx");
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XlsxExporter.ExportAsync(doc, path, null, null, cts.Token));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ PowerPoint

    [Fact]
    public async Task Pptx_Package_IsValidAndSlidesArePageSized()
    {
        var (bytes, result) = await Pptx(SlidePdf());
        var parts = Parts(bytes);
        foreach (string part in new[] { "/ppt/presentation.xml", "/ppt/slideMasters/slideMaster1.xml", "/ppt/slideLayouts/slideLayout1.xml", "/ppt/theme/theme1.xml",
                     "/ppt/presProps.xml", "/ppt/viewProps.xml", "/ppt/tableStyles.xml", "/ppt/slides/slide1.xml", "/docProps/core.xml" })
            Assert.True(parts.ContainsKey(part), part);
        var size = parts["/ppt/presentation.xml"].Descendants(P + "sldSz").Single();
        Assert.Equal((792 * 12700).ToString(CultureInfo.InvariantCulture), Attr(size, "cx"));
        Assert.Equal((612 * 12700).ToString(CultureInfo.InvariantCulture), Attr(size, "cy"));
        Assert.Single(parts["/ppt/presentation.xml"].Descendants(P + "sldId"));
        Assert.Equal(1, result.Slides);
        Assert.Equal(792, result.SlideWidth, 3);

        using var package = Package.Open(new MemoryStream(bytes), FileMode.Open, FileAccess.Read);
        var slidePart = package.GetPart(new Uri("/ppt/slides/slide1.xml", UriKind.Relative));
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.slide+xml", slidePart.ContentType);
        var slide = parts["/ppt/slides/slide1.xml"];
        // Every shape has its own id on the slide.
        var ids = slide.Descendants(P + "cNvPr").Select(e => Attr(e, "id")).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // The title: a text box where it is, in bold 28 point.
        static string BoxText(XElement sp) => string.Concat(sp.Descendants(A + "t").Select(t => t.Value));
        var boxes = slide.Descendants(P + "sp").Where(sp => Attr(sp.Descendants(P + "cNvSpPr").Single(), "txBox") == "1").ToList();
        var title = Assert.Single(boxes, b => BoxText(b) == "Quarterly Review");
        var titleRun = title.Descendants(A + "r").First().Element(A + "rPr")!;
        Assert.Equal("2800", Attr(titleRun, "sz"));
        Assert.Equal("1", Attr(titleRun, "b"));
        Assert.Equal("Arial", Attr(titleRun.Element(A + "latin")!, "typeface"));
        var off = title.Descendants(A + "off").First();
        Assert.InRange(long.Parse(Attr(off, "x"), CultureInfo.InvariantCulture) / 12700.0, 71, 73);
        // Its baseline is 92 points down the page: the box starts a font's ascent above it.
        Assert.InRange(long.Parse(Attr(off, "y"), CultureInfo.InvariantCulture) / 12700.0, 92 - 28, 92 - 20);

        // Two lines of one paragraph: one box, the page's line break kept, the page's line spacing.
        var body = Assert.Single(boxes, b => BoxText(b).StartsWith("Sales rose", StringComparison.Ordinal));
        var para = Assert.Single(body.Descendants(A + "p"));
        Assert.Single(para.Elements(A + "br"));
        Assert.Equal("1800", Attr(para.Element(A + "pPr")!.Descendants(A + "spcPts").Single(), "val"));
        Assert.Equal("Sales rose in every region this quarterand costs held steady against the plan.", BoxText(body));

        // Text up the page's edge: a box turned 270 degrees.
        var turned = Assert.Single(boxes, b => BoxText(b) == "Confidential draft");
        Assert.Equal((270 * 60000).ToString(CultureInfo.InvariantCulture), Attr(turned.Descendants(A + "xfrm").Single(), "rot"));

        // The picture, embedded as a png the slide refers to.
        var pic = Assert.Single(slide.Descendants(P + "pic"));
        string embed = pic.Descendants(A + "blip").Single().Attribute(R + "embed")!.Value;
        var media = package.GetPart(PackUriHelper.ResolvePartUri(slidePart.Uri, slidePart.GetRelationship(embed).TargetUri));
        Assert.Equal("image/png", media.ContentType);
        var picOff = pic.Descendants(A + "off").Single();
        Assert.Equal((560 * 12700).ToString(CultureInfo.InvariantCulture), Attr(picOff, "x"));
        Assert.Equal(((612 - 420) * 12700).ToString(CultureInfo.InvariantCulture), Attr(picOff, "y"));
        Assert.Equal((160 * 12700).ToString(CultureInfo.InvariantCulture), Attr(pic.Descendants(A + "ext").Single(), "cx"));

        // The rectangle and the line, as shapes with their colours.
        var rect = Assert.Single(slide.Descendants(P + "sp"), sp => sp.Descendants(A + "prstGeom").Any(g => Attr(g, "prst") == "rect") && Attr(sp.Descendants(P + "cNvSpPr").Single(), "txBox") != "1");
        Assert.Equal("1A66CC", Attr(rect.Element(P + "spPr")!.Element(A + "solidFill")!.Element(A + "srgbClr")!, "val"));
        Assert.Equal((300 * 12700).ToString(CultureInfo.InvariantCulture), Attr(rect.Descendants(A + "ext").Single(), "cx"));
        var line = Assert.Single(slide.Descendants(P + "cxnSp"));
        Assert.Equal("FF0000", Attr(line.Descendants(A + "ln").Single().Descendants(A + "srgbClr").Single(), "val"));
        Assert.Equal("0", Attr(line.Descendants(A + "ext").Single(), "cy"));

        Assert.Equal(3, result.TextBoxes);
        Assert.Equal(1, result.Pictures);
        Assert.Equal(2, result.Shapes);
        // Shapes are behind the text: the text boxes come last in the slide's drawing order.
        var order = slide.Descendants(P + "spTree").Single().Elements().ToList();
        int lastDrawing = order.FindLastIndex(e => e.Name == P + "pic" || e.Name == P + "cxnSp" || ReferenceEquals(e, rect));
        int firstText = order.FindIndex(e => boxes.Contains(e));
        Assert.True(lastDrawing < firstText);
    }

    [Fact]
    public async Task Pptx_RuledTable_BecomesATableWithMergedCells()
    {
        var (bytes, result) = await Pptx(RuledTablePdf());
        var slide = Parts(bytes)["/ppt/slides/slide1.xml"];
        Assert.Equal(1, result.Tables);
        var tbl = Assert.Single(slide.Descendants(A + "tbl"));
        Assert.Equal(4, tbl.Element(A + "tblGrid")!.Elements(A + "gridCol").Count());
        var rows = tbl.Elements(A + "tr").ToList();
        Assert.Equal(6, rows.Count);
        // Every row has a cell for every column; the title's spans four, the rest are covered by it.
        Assert.All(rows, r => Assert.Equal(4, r.Elements(A + "tc").Count()));
        var first = rows[0].Elements(A + "tc").ToList();
        Assert.Equal("4", Attr(first[0], "gridSpan"));
        Assert.Equal("Quarterly results", string.Concat(first[0].Descendants(A + "t").Select(t => t.Value)));
        Assert.All(first.Skip(1), c => Assert.Equal("1", Attr(c, "hMerge")));
        var amount = rows[3].Elements(A + "tc").ElementAt(1);
        Assert.Equal("($2,000.00)", string.Concat(amount.Descendants(A + "t").Select(t => t.Value)));
        // Ruled and shaded as on the page.
        var header = rows[1].Elements(A + "tc").First().Element(A + "tcPr")!;
        Assert.Equal("D9D9D9", Attr(header.Element(A + "solidFill")!.Element(A + "srgbClr")!, "val"));
        Assert.NotNull(header.Element(A + "lnT")!.Element(A + "solidFill"));
        // The frame is where the table is: 72 points in, 92 down.
        var frameOff = slide.Descendants(P + "graphicFrame").Single().Element(P + "xfrm")!.Element(A + "off")!;
        Assert.Equal((72 * 12700).ToString(CultureInfo.InvariantCulture), Attr(frameOff, "x"));
        Assert.Equal((92 * 12700).ToString(CultureInfo.InvariantCulture), Attr(frameOff, "y"));
        // The table's lines are the table's: not shapes as well.
        Assert.Empty(slide.Descendants(P + "cxnSp"));
    }

    [Fact]
    public async Task Pptx_PageAsBackground_PutsEditableTextOverAPictureOfThePage()
    {
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer(forceSoftware: true);
        var (bytes, result) = await Pptx(SlidePdf(), new PptxExportOptions { Renderer = renderer, PageAsBackground = true });
        var parts = Parts(bytes);
        var slide = parts["/ppt/slides/slide1.xml"];
        var blip = slide.Descendants(P + "bg").Single().Descendants(A + "blip").Single();
        using var package = Package.Open(new MemoryStream(bytes), FileMode.Open, FileAccess.Read);
        var slidePart = package.GetPart(new Uri("/ppt/slides/slide1.xml", UriKind.Relative));
        var media = package.GetPart(PackUriHelper.ResolvePartUri(slidePart.Uri, slidePart.GetRelationship(blip.Attribute(R + "embed")!.Value).TargetUri));
        Assert.Equal("image/png", media.ContentType);
        // The picture, the rectangle and the line are in the background; the text is over it.
        Assert.Empty(slide.Descendants(P + "pic"));
        Assert.Empty(slide.Descendants(P + "cxnSp"));
        Assert.Equal(3, result.TextBoxes);
        Assert.Equal(1, result.Pictures);
        Assert.Contains(slide.Descendants(A + "t"), t => t.Value == "Quarterly Review");
    }

    [Fact]
    public async Task Pptx_PagesOfAnotherSize_AreFittedToTheSlide()
    {
        var b = new VectorPdfBuilder();
        int f1 = b.Add(Helv);
        string res = $"<< /Font << /F1 {f1} 0 R >> >>";
        b.AddPage(Text(72, 700, "A letter page."), res, "[0 0 612 792]");
        b.AddPage(Text(72, 1500, "A page twice as large."), res, "[0 0 1224 1584]");
        // A page turned by /Rotate is shown landscape.
        b.AddPage(Text(72, 700, "A turned page."), res, "[0 0 612 792]", "/Rotate 90");
        var (bytes, result) = await Pptx(b.Build());
        var parts = Parts(bytes);
        Assert.Equal(3, result.Slides);
        Assert.Equal(3, parts["/ppt/presentation.xml"].Descendants(P + "sldId").Count());
        // The large page is half size on the slide: its 10 point text is 5 point.
        var run = parts["/ppt/slides/slide2.xml"].Descendants(A + "rPr").First();
        Assert.Equal("500", Attr(run, "sz"));
        Assert.Equal((36 * 12700).ToString(CultureInfo.InvariantCulture), Attr(parts["/ppt/slides/slide2.xml"].Descendants(A + "off").Skip(1).First(), "x"));
    }
}
