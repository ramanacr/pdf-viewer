using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Headers, footers, Bates numbers, watermarks and backgrounds: drawn where Acrobat draws them on
/// the page as it is seen (rotated and cropped pages too), found again by their pagination
/// artifacts, replaced and removed without touching the rest of the page.
/// </summary>
public class PageMarksTests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>Pages of 400 x 300 points, each saying "Body text N".</summary>
    private static byte[] Document(int pages = 1, string extra = "", string? content = null, string mediaBox = "[0 0 400 300]", Func<VectorPdfBuilder, string>? resources = null,
        Action<VectorPdfBuilder>? catalog = null)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        string more = resources?.Invoke(b) ?? string.Empty;
        for (int i = 1; i <= pages; i++)
            b.AddPage(content ?? $"BT /F1 12 Tf 60 150 Td (Body text {i}) Tj ET", $"<< /Font << /F1 {font} 0 R >> {more} >>", mediaBox: mediaBox, extra: extra);
        catalog?.Invoke(b);
        return b.Build();
    }

    private static async Task<PdfContentEditResult> Mark(byte[] pdf, Func<PdfVectorDocument, byte[], PdfContentEditResult> edit)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, "C:\\Reports\\report.pdf");
        return edit(doc, pdf);
    }

    private static async Task<PdfPageMarksInfo> ReadMarks(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfPageMarks.Read(doc);
    }

    private static async Task<string> Text(byte[] pdf, int page = 1)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, page);
    }

    /// <summary>The page as PDFium shows it at 72 dpi: BGRA rows, top first.</summary>
    private static async Task<(int W, int H, byte[] Px)> Render(byte[] pdf, int page = 1)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        using var r = await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = page, Dpi = 72 });
        var px = new byte[r.WidthPixels * r.HeightPixels * 4];
        for (int y = 0; y < r.HeightPixels; y++)
            r.Pixels.Span.Slice(y * r.Stride, r.WidthPixels * 4).CopyTo(px.AsSpan(y * r.WidthPixels * 4));
        return (r.WidthPixels, r.HeightPixels, px);
    }

    private static (byte R, byte G, byte B) Pixel((int W, int H, byte[] Px) img, int x, int y)
    {
        int i = (y * img.W + x) * 4;
        return (img.Px[i + 2], img.Px[i + 1], img.Px[i]);
    }

    private static (byte R, byte G, byte B) Rgb(byte r, byte g, byte b) => (r, g, b);

    /// <summary>The box (left, top, right, bottom; inclusive) of the pixels that match.</summary>
    private static (int L, int T, int R, int B)? Bounds((int W, int H, byte[] Px) img, Func<(byte R, byte G, byte B), bool> match)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, bottom = -1;
        for (int y = 0; y < img.H; y++)
            for (int x = 0; x < img.W; x++)
                if (match(Pixel(img, x, y)))
                {
                    l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); bottom = Math.Max(bottom, y);
                }
        return r < 0 ? null : (l, t, r, bottom);
    }

    private static PdfImageContent Solid(int size, byte r, byte g, byte b)
    {
        var data = new byte[size * size * 3];
        for (int i = 0; i < size * size; i++) { data[i * 3] = r; data[i * 3 + 1] = g; data[i * 3 + 2] = b; }
        return new PdfImageContent(size, size, data, PdfImageEncoding.Rgb);
    }

    private static int Count(string text, string word)
    {
        int n = 0;
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The page's decoded content, stream by stream, and the objects its /Contents names.</summary>
    private static async Task<(List<string> Streams, string Contents)> Content(byte[] pdf, int page = 1)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var node = doc.PageTree.Pages[page - 1];
        var decoder = new PdfStreamDecoder(null, doc.Resolver.Resolve);
        return (node.Contents.Select(s => Encoding.Latin1.GetString(decoder.DecodeStream(s))).ToList(), node.Dictionary["Contents"]!.ToString());
    }

    // ------------------------------------------------------------------ headers and footers

    [Fact]
    public async Task HeadersAndFooters_ShowPageNumbersDatesAndTheFileName()
    {
        byte[] pdf = Document(3);
        var hf = new PdfHeaderFooter
        {
            TopCenter = "Page <<1>> of <<n>>", BottomLeft = "<<File>>", BottomRight = "<<mm/dd/yyyy>>", TopRight = "<<1/n>>",
            Date = new DateTime(2026, 3, 4), FileName = "report.pdf",
        };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, hf));
        Assert.Contains("Arial", string.Join(",", result.EmbeddedFonts));
        for (int p = 1; p <= 3; p++)
        {
            string text = await Text(result.Bytes, p);
            Assert.Contains($"Page {p} of 3", text);
            Assert.Contains($"{p}/3", text);
            Assert.Contains("report.pdf", text);
            Assert.Contains("03/04/2026", text);
            Assert.Contains($"Body text {p}", text);
        }
        Assert.Equal(pdf, result.Bytes.AsSpan(0, pdf.Length).ToArray()); // an incremental update

        var marks = await ReadMarks(result.Bytes);
        Assert.Equal(new[] { 1, 2, 3 }, marks.PagesWith(PdfMarkKind.HeaderFooter));
        Assert.All(marks.Marks, m => Assert.True(m.Ours && !m.Behind));
        Assert.Equal("Page <<1>> of <<n>>", marks.HeaderFooter!.TopCenter); // the settings come back for Update
    }

    [Fact]
    public async Task PageNumbers_StartWhereAsked_AndOnlyTheRangeIsMarked()
    {
        byte[] pdf = Document(4);
        var hf = new PdfHeaderFooter { BottomCenter = "- <<1>> -", StartPageNumber = 10, Pages = new PdfPageRange(2, 4, PdfPageSubset.Even) };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, hf));
        Assert.DoesNotContain("- 10 -", await Text(result.Bytes, 1));
        Assert.Contains("- 11 -", await Text(result.Bytes, 2));
        Assert.DoesNotContain("- 12 -", await Text(result.Bytes, 3));
        Assert.Contains("- 13 -", await Text(result.Bytes, 4));
        Assert.Equal(new[] { 2, 4 }, (await ReadMarks(result.Bytes)).PagesWith(PdfMarkKind.HeaderFooter));
    }

    [Fact]
    public async Task BatesNumbers_RunOverTheRange_AndAreRemovedApartFromHeaders()
    {
        byte[] pdf = Document(4);
        var header = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, new PdfHeaderFooter { TopLeft = "Confidential" }));
        var bates = new PdfHeaderFooter { BottomRight = "<<Bates>>", Bates = new PdfBatesNumbering("ABC", "-X", 100, 6), Pages = new PdfPageRange(2) };
        var result = await Mark(header.Bytes, (d, b) => PdfPageMarks.Add(d, b, bates));
        Assert.DoesNotContain("ABC", await Text(result.Bytes, 1));
        Assert.Contains("ABC000100-X", await Text(result.Bytes, 2));
        Assert.Contains("ABC000102-X", await Text(result.Bytes, 4));
        var marks = await ReadMarks(result.Bytes);
        Assert.Equal(new[] { 2, 3, 4 }, marks.PagesWith(PdfMarkKind.Bates));
        Assert.Equal(new[] { 1, 2, 3, 4 }, marks.PagesWith(PdfMarkKind.HeaderFooter));
        Assert.Equal(100, marks.Bates!.Bates!.Start);

        var removed = await Mark(result.Bytes, (d, b) => PdfPageMarks.Remove(d, b, PdfMarkKind.Bates));
        Assert.DoesNotContain("ABC", await Text(removed.Bytes, 4));
        Assert.Contains("Confidential", await Text(removed.Bytes, 4));
        Assert.False((await ReadMarks(removed.Bytes)).Has(PdfMarkKind.Bates));
    }

    [Fact]
    public void Tokens_AreReplaced()
    {
        var hf = new PdfHeaderFooter { StartPageNumber = 1, DateFormat = "yyyy-MM-dd" };
        var date = new DateTime(2026, 9, 28);
        Assert.Equal("Page 2 of 7", PdfPageMarks.Expand("<<Page 1 of n>>", 2, 7, hf, null, date, "a.pdf"));
        Assert.Equal("2026-09-28 | 28/9/26 | a.pdf | B01 | <<unknown>>",
            PdfPageMarks.Expand("<<Date>> | <<d/m/yy>> | <<File>> | <<Bates>> | <<unknown>>", 1, 1, hf, "B01", date, "a.pdf"));
    }

    // ------------------------------------------------------------------ placement

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Marks_AreUpright_OnRotatedAndCroppedPages(int rotate)
    {
        // A crop box inset from the media box, and the page turned.
        byte[] pdf = Document(content: string.Empty, extra: $"/CropBox [50 20 350 280] /Rotate {rotate}");
        var watermark = new PdfWatermark
        {
            Source = PdfMarkSource.Image, Image = Solid(10, 255, 0, 0), Scale = 2,
            Horizontal = PdfMarkHorizontal.Left, Vertical = PdfMarkVertical.Top, OffsetX = 10, OffsetY = 15,
        };
        var marked = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, watermark));
        var header = new PdfHeaderFooter { TopCenter = "HHHH", Format = new PdfTextFormat("Arial", 20), TopMargin = 40 };
        var result = await Mark(marked.Bytes, (d, b) => PdfPageMarks.Add(d, b, header));

        var img = await Render(result.Bytes);
        bool sideways = rotate is 90 or 270;
        Assert.Equal(sideways ? 260 : 300, img.W);
        Assert.Equal(sideways ? 300 : 260, img.H);
        // The picture 20 points square, 10 from the left and 15 from the top as the page is seen.
        var red = Bounds(img, p => p.R > 200 && p.G < 60 && p.B < 60)!.Value;
        Assert.InRange(red.L, 9, 11);
        Assert.InRange(red.T, 14, 16);
        Assert.InRange(red.R, 28, 30);
        Assert.InRange(red.B, 33, 35);
        // The header centred across the top: its capitals' tops just below the margin's ascent line.
        var dark = Bounds(img, p => p.R < 100 && p.G < 100 && p.B < 100 && !(p.R > 200))!.Value;
        Assert.InRange(dark.T, 40, 45);
        Assert.InRange((dark.L + dark.R) / 2.0, img.W / 2.0 - 3, img.W / 2.0 + 3);
        Assert.True(dark.B < img.H / 2, "the header is at the top of the page as it is seen");
    }

    [Fact]
    public async Task Watermarks_ScaleRelativeToThePage_AndTurn()
    {
        byte[] pdf = Document(content: string.Empty);
        var watermark = new PdfWatermark { Source = PdfMarkSource.Image, Image = Solid(10, 0, 0, 255), RelativeScale = 0.5, RotationDegrees = 90 };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, watermark));
        var blue = Bounds(await Render(result.Bytes), p => p.B > 200 && p.R < 60)!.Value;
        // A square half the page's height, in the middle.
        Assert.InRange(blue.B - blue.T + 1, 148, 152);
        Assert.InRange(blue.R - blue.L + 1, 148, 152);
        Assert.InRange((blue.L + blue.R) / 2.0, 198, 202);
    }

    [Fact]
    public async Task TextWatermarks_ShowTheirText()
    {
        byte[] pdf = Document(content: string.Empty);
        var watermark = new PdfWatermark { Text = "DRAFT COPY", RotationDegrees = 45, Opacity = 0.4 };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, watermark));
        Assert.Contains("DRAFT COPY", await Text(result.Bytes));
        var grey = Bounds(await Render(result.Bytes), p => p.R < 235 && Math.Abs(p.R - p.B) < 8)!.Value;
        Assert.True(grey.R - grey.L > 150, "the turned text spans the middle of the page");
    }

    // ------------------------------------------------------------------ opacity and drawing behind

    private const string BlackSquare = "0 g 100 100 100 100 re f";

    [Fact]
    public async Task Opacity_BlendsWithWhatIsUnder()
    {
        byte[] pdf = Document(content: BlackSquare);
        var watermark = new PdfWatermark { Source = PdfMarkSource.Image, Image = Solid(1, 0, 0, 255), Scale = 400, Opacity = 0.5 };
        var img = await Render((await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, watermark))).Bytes);
        var white = Pixel(img, 20, 20);
        Assert.InRange(white.R, 120, 135);
        Assert.InRange(white.B, 250, 255);
        var black = Pixel(img, 150, 150); // (150, 150) seen from the top of a 300-high page is inside the square
        Assert.InRange(black.R, 0, 5);
        Assert.InRange(black.B, 120, 135);
    }

    [Fact]
    public async Task BehindTheContent_TheContentCoversTheMark()
    {
        byte[] pdf = Document(content: BlackSquare);
        var watermark = new PdfWatermark { Source = PdfMarkSource.Image, Image = Solid(1, 0, 0, 255), Scale = 400, Behind = true };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, watermark));
        var img = await Render(result.Bytes);
        Assert.Equal(Rgb(0, 0, 255), Pixel(img, 20, 20));
        Assert.Equal(Rgb(0, 0, 0), Pixel(img, 150, 150));
        Assert.True(Assert.Single((await ReadMarks(result.Bytes)).Marks).Behind);

        // Drawn first: the stream this editor put before the page's own.
        var (streams, _) = await Content(result.Bytes);
        Assert.StartsWith("/Artifact <</Subtype /Watermark /Type /Pagination >>BDC", streams[0]);
        Assert.Equal(BlackSquare, streams[1]);
    }

    [Fact]
    public async Task Backgrounds_FillThePageBehindTheContent()
    {
        byte[] pdf = Document(content: BlackSquare, extra: "/CropBox [10 10 390 290] /Rotate 90");
        var background = new PdfWatermark { IsBackground = true, Source = PdfMarkSource.Color, Color = new[] { 1.0, 0, 0 } };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, background));
        var img = await Render(result.Bytes);
        Assert.Equal(Rgb(255, 0, 0), Pixel(img, 1, 1));
        Assert.Equal(Rgb(255, 0, 0), Pixel(img, img.W - 2, img.H - 2));
        Assert.Equal(Rgb(0, 0, 0), Pixel(img, 140, 140)); // user space (150, 150), turned and cropped
        var (streams, _) = await Content(result.Bytes);
        Assert.StartsWith("/Artifact <</Subtype /Background /Type /Pagination >>BDC", streams[0]);
        Assert.Equal(PdfMarkKind.Background, Assert.Single((await ReadMarks(result.Bytes)).Marks).Kind);
    }

    [Fact]
    public async Task APageOfAnotherPdf_IsAWatermark_StoredOnceForEveryPage()
    {
        var src = new VectorPdfBuilder();
        src.AddPage("0 1 0 rg 0 0 100 50 re f", mediaBox: "[0 0 100 50]");
        byte[] other = src.Build();
        byte[] pdf = Document(3, content: string.Empty);
        var watermark = new PdfWatermark { Source = PdfMarkSource.Page, SourcePdf = other, RelativeScale = 0.5 };
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, watermark));
        var img = await Render(result.Bytes, 2);
        var green = Bounds(img, p => p.G > 200 && p.R < 60)!.Value;
        Assert.InRange(green.R - green.L + 1, 198, 202); // half the page's width (the page's shape limits it by width)
        Assert.InRange((green.T + green.B) / 2.0, 148, 152);

        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var forms = doc.PageTree.Pages.Select(p => ((PdfDictionary)doc.Resolver.Resolve(p.Dictionary["Resources"])!).Get<PdfDictionary>("XObject")!.Entries.Values.Single()).Distinct().ToList();
        Assert.Single(forms);
    }

    // ------------------------------------------------------------------ update and remove

    [Fact]
    public async Task Update_ReplacesAndRemove_Removes_LeavingTheRestByteForByte()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int s1 = b.AddStream(string.Empty, "q 1 0 0 1 5 5 cm");
        int s2 = b.AddStream(string.Empty, "BT /F1 12 Tf 60 150 Td (Body text) Tj ET", flate: true);
        b.AddPageObject($"<< /Type /Page /Parent {{0}} 0 R /MediaBox [0 0 400 300] /Resources << /Font << /F1 {font} 0 R >> >> /Contents [{s1} 0 R {s2} 0 R] >>");
        byte[] pdf = b.Build();
        var before = await Content(pdf);

        var added = await Mark(pdf, (d, x) => PdfPageMarks.Add(d, x, new PdfWatermark { Text = "DRAFT" }));
        Assert.Contains("DRAFT", await Text(added.Bytes));
        var withHeader = await Mark(added.Bytes, (d, x) => PdfPageMarks.Add(d, x, new PdfHeaderFooter { TopLeft = "Header line" }));

        var updated = await Mark(withHeader.Bytes, (d, x) => PdfPageMarks.Update(d, x, new PdfWatermark { Text = "FINAL" }));
        string text = await Text(updated.Bytes);
        Assert.DoesNotContain("DRAFT", text);
        Assert.Equal(1, Count(text, "FINAL"));
        Assert.Contains("Header line", text);
        Assert.Contains("Body text", text);
        // The page's own streams, unchanged between the marks.
        var middle = await Content(updated.Bytes);
        Assert.Equal(before.Streams, middle.Streams.Skip(1).Take(2));

        var removed = await Mark(updated.Bytes, (d, x) => PdfPageMarks.Remove(d, x, PdfMarkKind.Watermark));
        text = await Text(removed.Bytes);
        Assert.DoesNotContain("FINAL", text);
        Assert.Contains("Header line", text);

        var none = await Mark(removed.Bytes, (d, x) => PdfPageMarks.Remove(d, x, PdfMarkKind.HeaderFooter));
        var after = await Content(none.Bytes);
        Assert.Equal(before.Contents, after.Contents); // the same stream objects, in the same order
        Assert.Equal(before.Streams, after.Streams);
        Assert.Equal("Body text", (await Text(none.Bytes)).Trim());
        Assert.Empty((await ReadMarks(none.Bytes)).Marks);
        using var doc = await PdfVectorDocument.OpenAsync(none.Bytes);
        Assert.Null(doc.PageTree.Pages[0].Resources["XObject"]); // the marks' forms are no longer named
        Assert.Equal(pdf, none.Bytes.AsSpan(0, pdf.Length).ToArray());

        var nothing = await Mark(none.Bytes, (d, x) => PdfPageMarks.Remove(d, x, PdfMarkKind.Watermark));
        Assert.Same(none.Bytes, nothing.Bytes);
        Assert.NotEmpty(nothing.Warnings);
    }

    // ------------------------------------------------------------------ Acrobat's marks

    /// <summary>A page the way Acrobat marks one: its header and watermark as pagination artifacts, the watermark a form with PieceInfo.</summary>
    private static byte[] AcrobatMarked()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 40] /Resources << /Font << /F1 {font} 0 R >> >> " +
                               "/PieceInfo << /ADBE_CompoundType << /DocSettings << /Version 1 >> /LastModified (D:20240101000000Z) /Private /Watermark >> >>",
            "BT /F1 30 Tf 0 5 Td (SECRET) Tj ET");
        string content =
            "q BT /F1 12 Tf 60 150 Td (Body text) Tj ET Q\n" +
            "/Artifact <</Subtype /Header /Type /Pagination >>BDC q BT /F1 9 Tf 60 280 Td (Acrobat header) Tj ET Q EMC\n" +
            "/Artifact /MC0 BDC q BT /F1 9 Tf 60 10 Td (Acrobat footer) Tj ET Q EMC\n" +
            "/Artifact <</Subtype /Watermark /Type /Pagination >>BDC q 1 0 0 1 100 100 cm /Fm0 Do Q EMC\n" +
            "/Span <</Lang (en-GB)>> BDC BT /F1 12 Tf 60 120 Td (Tagged text) Tj ET EMC\n";
        b.AddPage(content, $"<< /Font << /F1 {font} 0 R >> /XObject << /Fm0 {form} 0 R >> /Properties << /MC0 << /Type /Pagination /Subtype /Footer >> >> >>",
            mediaBox: "[0 0 400 300]");
        return b.Build();
    }

    [Fact]
    public async Task AcrobatsMarks_AreRecognised()
    {
        var marks = await ReadMarks(AcrobatMarked());
        Assert.Equal(2, marks.Marks.Count(m => m.Kind == PdfMarkKind.HeaderFooter));
        Assert.Single(marks.Marks, m => m.Kind == PdfMarkKind.Watermark);
        Assert.All(marks.Marks, m => Assert.False(m.Ours));
        Assert.Null(marks.HeaderFooter);
    }

    [Fact]
    public async Task AcrobatsMarks_AreRemovedAndReplaced_AndNothingElse()
    {
        byte[] pdf = AcrobatMarked();
        var removed = await Mark(pdf, (d, b) => PdfPageMarks.Remove(d, b, PdfMarkKind.HeaderFooter));
        string text = await Text(removed.Bytes);
        Assert.DoesNotContain("Acrobat header", text);
        Assert.DoesNotContain("Acrobat footer", text);
        Assert.Contains("SECRET", text);
        Assert.Contains("Body text", text);
        Assert.Contains("Tagged text", text);
        // The stream is what it was without the two sequences.
        var (streams, _) = await Content(removed.Bytes);
        Assert.Equal("q BT /F1 12 Tf 60 150 Td (Body text) Tj ET Q\n\n\n" +
                     "/Artifact <</Subtype /Watermark /Type /Pagination >>BDC q 1 0 0 1 100 100 cm /Fm0 Do Q EMC\n" +
                     "/Span <</Lang (en-GB)>> BDC BT /F1 12 Tf 60 120 Td (Tagged text) Tj ET EMC\n", Assert.Single(streams));

        var noWatermark = await Mark(removed.Bytes, (d, b) => PdfPageMarks.Remove(d, b, PdfMarkKind.Watermark));
        Assert.DoesNotContain("SECRET", await Text(noWatermark.Bytes));
        using (var doc = await PdfVectorDocument.OpenAsync(noWatermark.Bytes))
            Assert.Null(doc.PageTree.Pages[0].Resources["XObject"]); // Acrobat's watermark form went with it

        var replaced = await Mark(pdf, (d, b) => PdfPageMarks.Update(d, b, new PdfHeaderFooter { BottomCenter = "Our footer" }));
        text = await Text(replaced.Bytes);
        Assert.Contains("Our footer", text);
        Assert.DoesNotContain("Acrobat footer", text);
        Assert.Contains("SECRET", text);
        var marks = await ReadMarks(replaced.Bytes);
        Assert.True(Assert.Single(marks.Marks, m => m.Kind == PdfMarkKind.HeaderFooter).Ours);
    }

    [Fact]
    public async Task Marks_AreArtifacts_SoATaggedDocumentsStructureIsUntouched()
    {
        byte[] pdf = Document(content: "/P <</MCID 0>> BDC BT /F1 12 Tf 60 150 Td (Tagged) Tj ET EMC", extra: "/StructParents 0",
            catalog: c => c.WithCatalog("/MarkInfo << /Marked true >> /StructTreeRoot << /Type /StructTreeRoot >>"));
        var result = await Mark(pdf, (d, b) => PdfPageMarks.Add(d, b, new PdfHeaderFooter { TopCenter = "<<1>>" }));
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        Assert.NotNull(root["StructTreeRoot"]);
        Assert.Equal(0L, doc.PageTree.Pages[0].Dictionary.GetInteger("StructParents"));
        var (streams, _) = await Content(result.Bytes);
        Assert.All(streams.Where(s => s.Contains("Do")), s => Assert.Contains("/Artifact <<", s));
        Assert.DoesNotContain(streams, s => s.Contains("MCID 1"));
    }
}
