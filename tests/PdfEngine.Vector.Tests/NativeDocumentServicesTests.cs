using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pages;
using PdfEngine.Pdfium.Native;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Oracle;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// M9 native document services against PDFium: what the vector engine reads (properties, page
/// geometry, outline, links, attachments, annotations) is what PDFium reads from the same file,
/// and what it writes (annotations, page edits, assembled documents, full rewrites) opens in
/// PDFium with the same content and renders the same.
/// </summary>
public class NativeDocumentServicesTests
{
    // ------------------------------------------------------------------ fixtures

    /// <summary>A PDF from object bodies numbered from 1 (object 1 must be the catalog).</summary>
    internal static byte[] Raw(params string[] objects) => RawWithTrailer(string.Empty, objects);

    internal static byte[] RawWithTrailer(string trailerExtra, params string[] objects)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.6\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    internal static string Stream(string content) => $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    /// <summary>Three pages in a two-level tree (the second level carries /Rotate 90 and a media box), each with a distinct drawing.</summary>
    internal static byte[] ThreePages(string catalogExtra = "", string page1Extra = "", string page3Extra = "", params string[] more)
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",                              // 1
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 3 /MediaBox [0 0 300 400] >>",          // 2
            $"<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Resources << >> {page1Extra} >>",   // 3
            "<< /Type /Pages /Parent 2 0 R /Kids [5 0 R 9 0 R] /Count 2 /Rotate 90 /MediaBox [0 0 500 250] >>", // 4
            "<< /Type /Page /Parent 4 0 R /Contents 7 0 R /Resources << >> /CropBox [10 10 400 240] >>",         // 5
            Stream("1 0 0 rg 20 20 100 50 re f"),                                                // 6
            Stream("0 0 1 rg 50 50 200 100 re f"),                                               // 7
            Stream("0 1 0 rg 10 300 80 80 re f 0 g 150 20 20 200 re f"),                         // 8
            $"<< /Type /Page /Parent 4 0 R /Contents 8 0 R /Resources << >> /Rotate 0 /MediaBox [0 0 250 500] {page3Extra} >>", // 9
        };
        objects.AddRange(more);
        return Raw(objects.ToArray());
    }

    private static async Task<PdfVectorDocument> Open(byte[] bytes, string? password = null) =>
        await PdfVectorDocument.OpenAsync(bytes, password: password);

    // ------------------------------------------------------------------ properties and geometry

    [Fact]
    public async Task Properties_MatchPdfium()
    {
        // PDFDocEncoding bullet and en dash, a UTF-16 title, dates.
        string title = "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("Čapek – R.U.R.")) + ">";
        var bytes = RawWithTrailer("/Info 4 0 R ",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Title {title} /Author (A\\200B\\205C) /Subject (s) /Keywords (k1, k2) /Creator (c) /Producer (p) /CreationDate (D:20210304093015+01'00') /ModDate (D:2022) >>");
        using var doc = await Open(bytes);
        using var oracle = new PdfiumOracle(bytes);
        var p = PdfDocumentInfo.ReadProperties(doc);
        Assert.Equal(oracle.MetaText("Title"), p.Title);
        Assert.Equal(oracle.MetaText("Author"), p.Author);
        Assert.Equal("A•B–C", p.Author);
        foreach (var (tag, value) in new[] { ("Subject", p.Subject), ("Keywords", p.Keywords), ("Creator", p.Creator), ("Producer", p.Producer), ("CreationDate", p.CreationDateText), ("ModDate", p.ModDateText) })
            Assert.Equal(oracle.MetaText(tag), value);
        Assert.Equal(new DateTime(2021, 3, 4, 9, 30, 15), p.CreationDate);
        Assert.Equal(new DateTime(2022, 1, 1), p.ModDate);
        Assert.Equal("1.6", p.HeaderVersion);
        Assert.Equal(16, oracle.FileVersion);
        Assert.Equal(-1, p.SecurityRevision);
        Assert.Equal(oracle.SecurityRevision, p.SecurityRevision);
    }

    [Theory]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    public async Task Security_MatchesPdfium(TestEncryptionKind kind)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 g 10 10 50 50 re f");
        var enc = new PdfTestEncryption(kind, "user", "owner", permissions: unchecked((int)0xFFFFF0C4));
        var bytes = b.Build(enc);
        foreach (var password in new[] { "user", "owner" })
        {
            using var doc = await Open(bytes, password);
            using var oracle = new PdfiumOracle(bytes, password);
            var p = PdfDocumentInfo.ReadProperties(doc);
            Assert.Equal(oracle.SecurityRevision, p.SecurityRevision);
            Assert.Equal(oracle.Permissions, p.Permissions);
        }
    }

    [Fact]
    public async Task PageGeometry_MatchesPdfium_WithInheritance()
    {
        var bytes = ThreePages();
        using var doc = await Open(bytes);
        using var oracle = new PdfiumOracle(bytes);
        Assert.Equal(oracle.PageCount, doc.PageCount);
        for (int i = 0; i < doc.PageCount; i++)
        {
            var g = PdfDocumentInfo.ReadPageGeometry(doc, i + 1);
            var (w, h) = oracle.PageSize(i);
            Assert.Equal(w, g.DisplaySize.Width, 2);
            Assert.Equal(h, g.DisplaySize.Height, 2);
            Assert.Equal(oracle.Rotation(i), g.Rotation);
            var box = oracle.BoundingBox(i);
            Assert.Equal(box.L, g.CropBox.Left, 2);
            Assert.Equal(box.B, g.CropBox.Top, 2);
            Assert.Equal(box.R, g.CropBox.Right, 2);
            Assert.Equal(box.T, g.CropBox.Bottom, 2);
        }
    }

    // ------------------------------------------------------------------ outline and links

    /// <summary>
    /// Explicit, named (name tree with kids, catalog /Dests by name), go-to and URI items, a
    /// page given by number, and an item whose /Next loops back to an earlier one.
    /// </summary>
    internal static byte[] Navigable()
    {
        string catalog = "/Outlines 10 0 R /Names << /Dests 20 0 R >> /Dests 23 0 R /URI << /Base (https://example.org/) >>";
        string page1 = "/Annots [30 0 R 31 0 R 32 0 R 33 0 R]";
        return ThreePages(catalog, page1, "",
            "<< /Type /Outlines /First 11 0 R /Last 14 0 R /Count 4 >>",                                     // 10
            "<< /Title (Explicit) /Parent 10 0 R /Next 12 0 R /Dest [9 0 R /XYZ 10 20 0] >>",                 // 11
            "<< /Title (Named tree) /Parent 10 0 R /Prev 11 0 R /Next 13 0 R /Dest (chap2) /First 15 0 R /Last 15 0 R /Count 1 >>", // 12
            "<< /Title (Go-to action) /Parent 10 0 R /Prev 12 0 R /Next 14 0 R /A << /S /GoTo /D /old >> >>", // 13
            "<< /Title (Web\\tsite) /Parent 10 0 R /Prev 13 0 R /Next 11 0 R /A << /S /URI /URI (https://example.com/a) >> >>", // 14
            "<< /Title (By number) /Parent 12 0 R /Dest [2 /Fit] >>",                                        // 15
            Stream(""), Stream(""), Stream(""), Stream(""),                                                   // 16-19 (unused)
            "<< /Kids [21 0 R 22 0 R] >>",                                                                   // 20
            "<< /Limits [(a) (chap1)] /Names [(a) [3 0 R /Fit] (chap1) << /D [3 0 R /FitH 300] >>] >>",       // 21
            "<< /Limits [(chap2) (chap3)] /Names [(chap2) [5 0 R /FitR 0 0 10 10] (chap3) [9 0 R /Fit]] >>",  // 22
            "<< /old [5 0 R /Fit] >>",                                                                       // 23
            Stream(""), Stream(""), Stream(""), Stream(""), Stream(""), Stream(""),                           // 24-29
            "<< /Type /Annot /Subtype /Link /Rect [10 10 60 30] /Dest (chap3) >>",                             // 30
            "<< /Type /Annot /Subtype /Link /Rect [70 10 120 30] /A << /S /URI /URI (relative/path) >> >>",    // 31
            "<< /Type /Annot /Subtype /Link /Rect [130 40 180 10] /A << /S /GoTo /D [5 0 R /Fit] >> >>",       // 32
            "<< /Type /Annot /Subtype /Link /Rect [10 50 60 70] /A << /S /GoToR /F (other.pdf) /D [0 /Fit] >> >>"); // 33
    }

    private static List<(int Depth, string Title, int Page)> Flatten(IReadOnlyList<PdfOutlineItem> items, int depth = 0) =>
        items.SelectMany(i => new[] { (depth, i.Title, i.PageIndex) }.Concat(Flatten(i.Children, depth + 1))).ToList();

    [Fact]
    public async Task Outline_MatchesPdfium()
    {
        var bytes = Navigable();
        using var doc = await Open(bytes);
        using var oracle = new PdfiumOracle(bytes);
        var ours = Flatten(PdfDocumentInfo.ReadOutline(doc));
        var theirs = oracle.Outline().Select(b => (b.Depth, b.Title, b.PageIndex)).ToList();
        Assert.Equal(theirs, ours);
        Assert.Contains((0, "Web site", -1), ours); // the tab is shown as a space, as PDFium shows it
        Assert.Equal(new[] { 2, 1, 2, 1, -1 }, ours.Select(o => o.Page));

        var bookmarks = await doc.GetBookmarksAsync();
        Assert.Equal(3, bookmarks[0].TargetPageNumber);
        Assert.Equal(10, bookmarks[0].TargetX);
        Assert.Equal(2, bookmarks[1].TargetPageNumber);
        Assert.Equal(3, bookmarks[1].Children[0].TargetPageNumber);
    }

    [Fact]
    public async Task Links_MatchPdfium()
    {
        var bytes = Navigable();
        using var doc = await Open(bytes);
        using var oracle = new PdfiumOracle(bytes);
        var ours = PdfDocumentInfo.ReadLinks(doc, 1);
        var theirs = oracle.Links(0);
        Assert.Equal(theirs.Count, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(theirs[i].L, ours[i].Rect.Left, 2);
            Assert.Equal(theirs[i].B, ours[i].Rect.Top, 2);
            Assert.Equal(theirs[i].R, ours[i].Rect.Right, 2);
            Assert.Equal(theirs[i].T, ours[i].Rect.Bottom, 2);
            int ourType = ours[i].Action == null ? -1 : ours[i].Action!.Kind switch
            {
                PdfActionKind.GoTo => 1, PdfActionKind.GoToR => 2, PdfActionKind.Uri => 3, PdfActionKind.Launch => 4, _ => 0,
            };
            Assert.Equal(theirs[i].ActionType, ourType);
            if (ourType != 2) Assert.Equal(theirs[i].PageIndex, ours[i].TargetPageIndex);
            Assert.Equal(theirs[i].Uri, ours[i].Action?.Uri);
        }
        Assert.Equal("https://example.org/relative/path", ours[1].Action!.Uri);
        Assert.Equal("other.pdf", ours[3].Action!.FilePath);
    }

    // ------------------------------------------------------------------ attachments

    private static byte[] WithAttachments()
    {
        byte[] payload = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        string flate = Convert.ToHexString(Redaction.ContentRedactor.Deflate(payload));
        return ThreePages("/Names << /EmbeddedFiles 10 0 R >>", "/Annots [14 0 R]", "",
            "<< /Kids [11 0 R] >>",                                                                       // 10
            "<< /Limits [(a.txt) (b.bin)] /Names [(a.txt) 12 0 R (b.bin) << /Type /Filespec /F (b.bin) /UF <FEFF00620069006E00E9002E00620069006E> /EF << /F 13 0 R >> >>] >>", // 11
            "<< /Type /Filespec /F (a.txt) /EF << /F 15 0 R >> /Desc (first) >>",                         // 12
            $"<< /Type /EmbeddedFile /Filter [/ASCIIHexDecode /FlateDecode] /Length {flate.Length + 1} >>\nstream\n{flate}>\nendstream", // 13
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS << /Type /Filespec /F (note.txt) /EF << /F 16 0 R >> >> >>", // 14
            Stream("hello"),                                                                              // 15
            Stream("annotated file"));                                                                    // 16
    }

    [Fact]
    public async Task Attachments_MatchPdfium_AndExtractByteForByte()
    {
        var bytes = WithAttachments();
        using var doc = await Open(bytes);
        using var oracle = new PdfiumOracle(bytes);
        var ours = PdfAttachments.List(doc);
        var theirs = oracle.Attachments();
        Assert.Equal(theirs.Count, ours.Count(a => a.Source == PdfAttachmentSource.EmbeddedFiles));
        for (int i = 0; i < theirs.Count; i++)
        {
            Assert.Equal(theirs[i].Name, ours[i].Name);
            Assert.Equal(theirs[i].Size, ours[i].Size);
            Assert.Equal(oracle.AttachmentBytes(i), PdfAttachments.Extract(doc, i));
        }
        Assert.Equal("biné.bin", ours[1].Name);
        Assert.Equal(3000, ours[1].Size);
        var annotated = Assert.Single(ours, a => a.Source == PdfAttachmentSource.Annotation);
        Assert.Equal("note.txt", annotated.Name);
        Assert.Equal("annotated file", Encoding.ASCII.GetString(PdfAttachments.Extract(doc, annotated.Index)));
    }

    [Fact]
    public async Task Attachments_AddAndDelete_AreSeenByPdfium()
    {
        var bytes = WithAttachments();
        using var doc = await Open(bytes);
        byte[] data = Encoding.UTF8.GetBytes("added payload é");
        var added = PdfAttachments.Add(doc, bytes, "a.txt", data, "dup name");
        Assert.True(added.AsSpan(0, bytes.Length).SequenceEqual(bytes)); // an incremental update
        using (var oracle = new PdfiumOracle(added))
        {
            var list = oracle.Attachments();
            Assert.Equal(3, list.Count);
            int at = list.FindIndex(a => a.Size == data.Length);
            Assert.Equal(data, oracle.AttachmentBytes(at));
        }
        using var after = await Open(added);
        Assert.Equal(new[] { "a.txt", "a.txt", "biné.bin" }, PdfAttachments.List(after, includeAnnotations: false).Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));

        var deleted = PdfAttachments.Delete(after, added, 0);
        using (var oracle = new PdfiumOracle(deleted)) Assert.Equal(2, oracle.Attachments().Count);
        using var afterDelete = await Open(deleted);
        int annotIndex = PdfAttachments.List(afterDelete).Single(a => a.Source == PdfAttachmentSource.Annotation).Index;
        var noAnnot = PdfAttachments.Delete(afterDelete, deleted, annotIndex);
        using var last = await Open(noAnnot);
        Assert.Empty(PdfAnnotationReader.Read(last, 1));
    }

    // ------------------------------------------------------------------ annotations

    private static PdfAnnotation Markup(string subtype, double x, double y, double w, double h, string contents) => new()
    {
        PageNumber = 1,
        Subtype = subtype,
        Rect = new PdfRect(x, y, w, h),
        Color = new[] { 1.0, 0.8, 0 },
        Opacity = 0.5,
        Contents = contents,
        Author = "Tester",
        CreationDate = "D:20240102030405+00'00'",
        BorderWidth = 2,
    };

    private static List<PdfAnnotation> Samples()
    {
        var highlight = Markup("Highlight", 20, 100, 120, 30, "hl");
        highlight.QuadPoints.Add(new double[] { 20, 130, 140, 130, 20, 115, 140, 115 });
        highlight.QuadPoints.Add(new double[] { 20, 115, 100, 115, 20, 100, 100, 100 });
        var ink = Markup("Ink", 30, 30, 100, 60, "ink");
        ink.InkList.Add(new[] { new PdfPoint(30, 30), new PdfPoint(80, 90), new PdfPoint(130, 40) });
        ink.InkList.Add(new[] { new PdfPoint(40, 80), new PdfPoint(120, 80) });
        var line = Markup("Line", 10, 150, 150, 40, "line");
        line.Line = new double[] { 15, 155, 155, 185 };
        line.LineEndings = new[] { "OpenArrow", "Circle" };
        var polygon = Markup("Polygon", 150, 20, 80, 80, "poly");
        polygon.Vertices.AddRange(new[] { new PdfPoint(160, 30), new PdfPoint(220, 40), new PdfPoint(190, 90) });
        var note = Markup("Text", 250, 300, 20, 20, "note with ünïcode");
        note.Icon = "Comment";
        note.PopupRect = new PdfRect(250, 200, 120, 80);
        var free = Markup("FreeText", 20, 300, 160, 40, "Free text");
        free.Color = null;
        free.DefaultAppearance = "/Helv 11 Tf 0 0 0 rg 1 0 0 RG";
        return new() { highlight, ink, line, polygon, note, free, Markup("Square", 200, 150, 60, 40, "sq"), Markup("Circle", 200, 220, 60, 40, "ci") };
    }

    [Fact]
    public async Task Annotations_WrittenNatively_AreReadIdenticallyByPdfium()
    {
        var bytes = ThreePages();
        using var doc = await Open(bytes);
        var samples = Samples();
        var written = PdfAnnotationEditor.Apply(doc, bytes, e => { foreach (var a in samples) e.Add(a); });
        Assert.True(written.AsSpan(0, bytes.Length).SequenceEqual(bytes));

        using var oracle = new PdfiumOracle(written);
        var theirs = oracle.Annots(0).Where(a => a.Subtype != "Popup").ToList();
        using var after = await Open(written);
        var ours = PdfAnnotationReader.Read(after, 1).Where(a => a.Subtype != "Popup").ToList();
        Assert.Equal(samples.Count, ours.Count);
        Assert.Equal(theirs.Count, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(theirs[i].Subtype, ours[i].Subtype);
            Assert.Equal(theirs[i].L, ours[i].Rect.Left, 2);
            Assert.Equal(theirs[i].B, ours[i].Rect.Top, 2);
            Assert.Equal(theirs[i].R, ours[i].Rect.Right, 2);
            Assert.Equal(theirs[i].T, ours[i].Rect.Bottom, 2);
            Assert.Equal(theirs[i].Contents, ours[i].Contents);
            Assert.Equal(theirs[i].Author, ours[i].Author);
            Assert.Equal(theirs[i].Quads, ours[i].QuadPoints.Count);
            Assert.Equal(theirs[i].InkPaths, ours[i].InkList.Count);
            Assert.True(ours[i].HasAppearance, $"{ours[i].Subtype} has no appearance");
            Assert.Equal(samples[i].Contents, ours[i].Contents);
        }
        Assert.Contains(oracle.Annots(0), a => a.Subtype == "Popup");
        Assert.NotEqual(0, ours.Single(a => a.Subtype == "Text").PopupObjectNumber);

        // Every appearance draws something inside its rectangle (PDFium renders with FPDF_ANNOT).
        var plain = oracle.Render(0);
        using var plainOracle = new PdfiumOracle(bytes);
        var before = plainOracle.Render(0);
        foreach (var a in ours)
            Assert.True(RegionDifference(before, plain, a.Rect, oracle.PageSize(0).Height) > 1, $"{a.Subtype} draws nothing");
    }

    /// <summary>Mean difference inside a page-space rectangle (page 1 of the fixture is unrotated, origin at 0,0).</summary>
    private static double RegionDifference((int Width, int Height, byte[] Bgra) a, (int Width, int Height, byte[] Bgra) b, PdfRect rect, double pageHeight)
    {
        int x0 = Math.Max(0, (int)rect.Left), x1 = Math.Min(a.Width, (int)Math.Ceiling(rect.Right));
        int y0 = Math.Max(0, (int)(pageHeight - rect.Bottom)), y1 = Math.Min(a.Height, (int)Math.Ceiling(pageHeight - rect.Top));
        long total = 0, n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * a.Width + x) * 4;
                total += Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
                n += 3;
            }
        return n == 0 ? 0 : (double)total / n;
    }

    [Fact]
    public async Task Annotations_CreatedByPdfium_AreReadIdentically()
    {
        var source = ThreePages();
        byte[] pdfiumWritten;
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            IntPtr buf = Marshal.AllocHGlobal(source.Length);
            Marshal.Copy(source, 0, buf, source.Length);
            using (var docHandle = PdfiumNativeBridge.FPDF_LoadMemDocument(buf, source.Length, null))
            {
                using var page = PdfiumNativeBridge.FPDF_LoadPage(docHandle, 0);
                foreach (var (subtype, rect) in new[] { (PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT, (10f, 100f, 90f, 120f)), (PdfiumNativeBridge.FPDF_ANNOT_INK, (20f, 20f, 120f, 80f)), (PdfiumNativeBridge.FPDF_ANNOT_SQUARE, (150f, 150f, 250f, 250f)), (PdfiumNativeBridge.FPDF_ANNOT_TEXT, (200f, 300f, 220f, 320f)) })
                {
                    using var annot = PdfiumNativeBridge.FPDFPage_CreateAnnot(page, subtype);
                    var r = new FS_RECTF { left = rect.Item1, bottom = rect.Item2, right = rect.Item3, top = rect.Item4 };
                    PdfiumNativeBridge.FPDFAnnot_SetRect(annot, ref r);
                    PdfiumNativeBridge.FPDFAnnot_SetColor(annot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_Color, 255, 0, 0, 128);
                    PdfiumNativeBridge.FPDFAnnot_SetStringValue(annot, "Contents", PdfiumNativeBridge.StringToUtf16NullTerminated("pdfium " + subtype + " ü"));
                    PdfiumNativeBridge.FPDFAnnot_SetStringValue(annot, "T", PdfiumNativeBridge.StringToUtf16NullTerminated("Oracle"));
                    if (subtype == PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT)
                    {
                        var q = new FS_QUADPOINTSF { x1 = 10, y1 = 120, x2 = 90, y2 = 120, x3 = 10, y3 = 100, x4 = 90, y4 = 100 };
                        PdfiumNativeBridge.FPDFAnnot_AppendAttachmentPoints(annot, ref q);
                    }
                    if (subtype == PdfiumNativeBridge.FPDF_ANNOT_INK)
                    {
                        PdfiumNativeBridge.FPDFAnnot_AddInkStroke(annot, new[] { new FS_POINTF { x = 20, y = 20 }, new FS_POINTF { x = 120, y = 80 } }, 2);
                        PdfiumNativeBridge.FPDFAnnot_SetBorder(annot, 0, 0, 3);
                    }
                }
                PdfiumNativeBridge.FPDFPage_GenerateContent(page);
                using var ms = new MemoryStream();
                var writer = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (_, data, size) =>
                    {
                        var chunk = new byte[size];
                        Marshal.Copy(data, chunk, 0, (int)size);
                        ms.Write(chunk);
                        return 1;
                    },
                };
                Assert.NotEqual(0, PdfiumNativeBridge.FPDF_SaveAsCopy(docHandle, ref writer, PdfiumNativeBridge.FPDF_NO_INCREMENTAL));
                GC.KeepAlive(writer);
                pdfiumWritten = ms.ToArray();
            }
            Marshal.FreeHGlobal(buf);
        }

        using var doc = await Open(pdfiumWritten);
        using var oracle = new PdfiumOracle(pdfiumWritten);
        var ours = PdfAnnotationReader.Read(doc, 1);
        var theirs = oracle.Annots(0);
        Assert.Equal(4, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(theirs[i].Subtype, ours[i].Subtype);
            Assert.Equal(theirs[i].L, ours[i].Rect.Left, 2);
            Assert.Equal(theirs[i].T, ours[i].Rect.Bottom, 2);
            Assert.Equal(theirs[i].Contents, ours[i].Contents);
            Assert.Equal(theirs[i].Author, ours[i].Author);
            Assert.Equal(theirs[i].Quads, ours[i].QuadPoints.Count);
            Assert.Equal(theirs[i].InkPaths, ours[i].InkList.Count);
            Assert.Equal(new[] { 1.0, 0, 0 }, ours[i].Color);
            Assert.Equal(128 / 255.0, ours[i].Opacity, 2);
        }
        Assert.Equal(3, ours[1].BorderWidth);
    }

    [Fact]
    public async Task Annotations_ModifyAndDelete_TakeThePopupAndRepliesWith()
    {
        var bytes = ThreePages();
        using var doc = await Open(bytes);
        var first = PdfAnnotationEditor.Apply(doc, bytes, e => { foreach (var a in Samples()) e.Add(a); });
        using var withNotes = await Open(first);
        var notes = PdfAnnotationReader.Read(withNotes, 1);
        var note = notes.Single(a => a.Subtype == "Text");
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(250, 280, 20, 20), Contents = "reply", InReplyTo = note.ObjectNumber, ReplyType = "R" };
        var second = PdfAnnotationEditor.Apply(withNotes, first, e => e.Add(reply));

        using var withReply = await Open(second);
        var all = PdfAnnotationReader.Read(withReply, 1);
        var square = all.Single(a => a.Subtype == "Square");
        square.Contents = "changed";
        square.Color = new[] { 0.0, 0, 1 };
        var third = PdfAnnotationEditor.Apply(withReply, second, e =>
        {
            e.Modify(square);
            e.Delete(1, all.Single(a => a.Subtype == "Text" && a.InReplyTo == 0).Index);
        });

        using var oracle = new PdfiumOracle(third);
        var theirs = oracle.Annots(0);
        Assert.DoesNotContain(theirs, a => a.Subtype is "Text" or "Popup");
        Assert.Equal("changed", theirs.Single(a => a.Subtype == "Square").Contents);
        using var final = await Open(third);
        var modified = PdfAnnotationReader.Read(final, 1).Single(a => a.Subtype == "Square");
        Assert.Equal(new[] { 0.0, 0, 1 }, modified.Color);
        Assert.True(modified.HasAppearance);
        Assert.Equal(Samples().Count - 1, theirs.Count);
    }

    [Theory]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    [InlineData(TestEncryptionKind.Aes128_R4)]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    public async Task Annotations_OnEncryptedDocuments_AreEncryptedAndReadable(TestEncryptionKind kind)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 g 10 10 50 50 re f", mediaBox: "[0 0 300 400]");
        var bytes = b.Build(new PdfTestEncryption(kind, "user", "owner"));
        using var doc = await Open(bytes, "user");
        var written = PdfAnnotationEditor.Apply(doc, bytes, e => e.Add(Markup("Square", 20, 20, 100, 80, "secret contents ü")));
        Assert.DoesNotContain("secret contents", Encoding.Latin1.GetString(written)); // strings are encrypted
        using var oracle = new PdfiumOracle(written, "user");
        Assert.Equal("secret contents ü", oracle.Annots(0).Single().Contents);
        using var reopened = await Open(written, "user");
        Assert.Equal("secret contents ü", PdfAnnotationReader.Read(reopened, 1).Single().Contents);
    }

    [Fact]
    public async Task Annotations_OnARepairedDocument_FallBackToAFullRewrite()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 g 10 10 50 50 re f");
        var bytes = b.Build(corruptStartXref: true);
        using var doc = await Open(bytes);
        Assert.True(doc.WasRepaired);
        var written = PdfAnnotationEditor.Apply(doc, bytes, e => e.Add(Markup("Circle", 20, 20, 100, 80, "c")));
        using var oracle = new PdfiumOracle(written);
        Assert.Equal("c", oracle.Annots(0).Single().Contents);
        using var reopened = await Open(written);
        Assert.False(reopened.WasRepaired);
    }

    [Fact]
    public async Task Flatten_DrawsAppearancesIntoTheContent_AndKeepsLinks()
    {
        var bytes = Navigable();
        using var doc = await Open(bytes);
        var annotated = PdfAnnotationEditor.Apply(doc, bytes, e => { foreach (var a in Samples()) e.Add(a); });
        using var withAnnots = await Open(annotated);
        var flat = PdfAnnotationEditor.Apply(withAnnots, annotated, e => Assert.Equal(Samples().Count, e.FlattenPage(1)));

        using var before = new PdfiumOracle(annotated);
        using var after = new PdfiumOracle(flat);
        Assert.All(after.Annots(0), a => Assert.Equal("Link", a.Subtype));
        Assert.Equal(4, after.Links(0).Count);
        double diff = PdfiumOracle.MeanDifference(before.Render(0, 144), after.Render(0, 144));
        Assert.True(diff < 0.5, $"Flattened page differs from the annotated one by {diff:F2}/255");
    }

    // ------------------------------------------------------------------ page organisation in place

    [Fact]
    public async Task PageTree_RotateDeleteInsert_InPlace_MatchPdfium()
    {
        var bytes = ThreePages();
        await using var native = await PdfNativeDocument.OpenAsync(bytes);
        var organizer = new PdfNativePageOrganizerService();
        await organizer.RotatePageAsync(native, 1, PageRotation.Rotate270);
        await organizer.DeletePageAsync(native, 2);                 // from the nested node
        await organizer.InsertBlankPageAsync(native, 1, 100, 150);   // becomes page 2
        await organizer.InsertBlankPageAsync(native, 99, 120, 80);   // appended
        Assert.True(native.Bytes.AsSpan(0, bytes.Length).SequenceEqual(bytes));

        using var oracle = new PdfiumOracle(native.Bytes);
        Assert.Equal(4, oracle.PageCount);
        Assert.Equal(4, native.PageCount);
        Assert.Equal(270, oracle.Rotation(0));
        Assert.Equal((100.0, 150.0), oracle.PageSize(1));
        Assert.Equal((250.0, 500.0), oracle.PageSize(2)); // the old page 3
        Assert.Equal((120.0, 80.0), oracle.PageSize(3));
        for (int i = 0; i < 4; i++)
        {
            var info = await native.GetPageInfoAsync(i + 1);
            Assert.Equal(oracle.PageSize(i), (info.WidthPoints, info.HeightPoints));
            Assert.Equal(oracle.Rotation(i), info.RotationDegrees);
        }
        using var source = new PdfiumOracle(bytes);
        Assert.Equal(0, PdfiumOracle.MeanDifference(source.Render(2), oracle.Render(2)), 3);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var single = await PdfNativeDocument.OpenAsync(Raw("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>"));
            await organizer.DeletePageAsync(single, 1);
        });
    }

    // ------------------------------------------------------------------ assembling documents

    [Fact]
    public async Task Arrange_ReordersRotatesAndRepeats_AndRendersAsTheSource()
    {
        var bytes = Navigable();
        using var doc = await Open(bytes);
        var output = PdfPageAssembler.Build(new[]
        {
            new PdfAssemblyPage(doc, 3), new PdfAssemblyPage(doc, 1, 90), new PdfAssemblyPage(doc, 3), new PdfAssemblyPage(doc, 2, 180),
        });
        using var source = new PdfiumOracle(bytes);
        using var oracle = new PdfiumOracle(output);
        Assert.Equal(4, oracle.PageCount);
        Assert.Equal(source.Rotation(2), oracle.Rotation(0));
        Assert.Equal((source.Rotation(0) + 90) % 360, oracle.Rotation(1));
        Assert.Equal((source.Rotation(1) + 180) % 360, oracle.Rotation(3));
        Assert.Equal(source.PageSize(2), oracle.PageSize(0));
        Assert.Equal(source.PageSize(2), oracle.PageSize(2));
        Assert.Equal(0, PdfiumOracle.MeanDifference(source.Render(2), oracle.Render(0)), 3);
        Assert.Equal(0, PdfiumOracle.MeanDifference(source.Render(2), oracle.Render(2)), 3);

        // The vector engine opens it with the same pages.
        using var reopened = await Open(output);
        Assert.Equal(4, reopened.PageCount);
        for (int i = 0; i < 4; i++)
        {
            var g = PdfDocumentInfo.ReadPageGeometry(reopened, i + 1);
            Assert.Equal(oracle.PageSize(i).Width, g.DisplaySize.Width, 2);
            Assert.Equal(oracle.Rotation(i), g.Rotation);
        }

        // Links on page 1 (now page 2): to page 3 (taken, first copy is page 1) and URI kept; to page 2 kept (page 4); remote kept.
        var links = oracle.Links(1);
        Assert.Equal(4, links.Count);
        Assert.Equal(0, links[0].PageIndex);
        Assert.Equal(3, links[2].PageIndex);
        // The outline keeps the items whose pages are taken; named destinations became explicit.
        Assert.Equal(new[] { (0, "Explicit", 0), (0, "Named tree", 3), (1, "By number", 0), (0, "Go-to action", 3), (0, "Web site", -1) },
            oracle.Outline().Select(b => (b.Depth, b.Title, b.PageIndex)).ToArray());
        Assert.DoesNotContain("/Dests", Encoding.Latin1.GetString(output));
    }

    [Fact]
    public async Task Extract_DropsLinksAndOutlineItemsLeadingElsewhere()
    {
        var bytes = Navigable();
        using var doc = await Open(bytes);
        var output = PdfPageAssembler.Extract(doc, new[] { 1 });
        using var oracle = new PdfiumOracle(output);
        Assert.Equal(1, oracle.PageCount);
        var links = oracle.Links(0);
        Assert.Equal(new[] { 3, 2 }, links.Select(l => l.ActionType)); // the URI and the remote go-to; links to pages 2 and 3 are gone
        Assert.Equal(new[] { "Web site" }, oracle.Outline().Select(b => b.Title));
    }

    [Fact]
    public async Task Merge_DeduplicatesSharedResources_AndOutlinesEachSource()
    {
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
        string image = "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>\nstream\n\u0000ÿÿ\u0000\nendstream";
        byte[] Make(string text) => Raw(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im 6 0 R >> >> >>",
            Stream($"BT /F1 12 Tf 20 100 Td ({text}) Tj ET q 50 0 0 50 100 20 cm /Im Do Q"),
            font, image);
        var a = Make("First");
        var b = Make("Second");
        using var da = await Open(a);
        using var db = await Open(b);
        var merged = PdfPageAssembler.Merge(new[] { da, db, da }, new PdfAssemblyOptions { SourceTitles = new[] { "a", "b" } });
        string text = Encoding.Latin1.GetString(merged);
        Assert.Equal(1, CountOccurrences(text, "/BaseFont /Helvetica"));
        Assert.Equal(1, CountOccurrences(text, "/Subtype /Image"));
        using var oracle = new PdfiumOracle(merged);
        Assert.Equal(3, oracle.PageCount); // the first source's page is taken twice
        using var oa = new PdfiumOracle(a);
        using var ob = new PdfiumOracle(b);
        Assert.Equal(0, PdfiumOracle.MeanDifference(oa.Render(0), oracle.Render(0)), 3);
        Assert.Equal(0, PdfiumOracle.MeanDifference(ob.Render(0), oracle.Render(1)), 3);
        Assert.Equal(0, PdfiumOracle.MeanDifference(oa.Render(0), oracle.Render(2)), 3);
        Assert.Equal(new[] { (0, "a", 0), (0, "b", 1) }, oracle.Outline().Select(x => (x.Depth, x.Title, x.PageIndex)).ToArray());
    }

    private static int CountOccurrences(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public async Task Extract_KeepsOnlyTheFieldsWhoseWidgetsAreTaken()
    {
        var bytes = ThreePages("/AcroForm << /Fields [10 0 R 13 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 14 0 R >> >> >>",
            "/Annots [11 0 R]", "/Annots [12 0 R]",
            "<< /FT /Tx /T (name) /Kids [11 0 R 12 0 R] /V (x) >>",                                                       // 10
            "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [10 10 100 30] /P 3 0 R >>",                          // 11
            "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [10 10 100 30] /P 9 0 R >>",                          // 12
            "<< /FT /Btn /T (other) /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",                                // 13 (on no page)
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");                                                     // 14
        using var doc = await Open(bytes);
        var output = PdfPageAssembler.Extract(doc, new[] { 1, 2 });
        using var reopened = await Open(output);
        var form = reopened.Resolver.Resolve(reopened.Catalog["AcroForm"]) as Objects.PdfDictionary;
        Assert.NotNull(form);
        var fields = (Objects.PdfArray)reopened.Resolver.Resolve(form!["Fields"])!;
        var field = Assert.Single(fields.Items.Select(f => reopened.Resolver.Resolve(f) as Objects.PdfDictionary));
        Assert.Equal("name", field!.GetString("T"));
        Assert.Single((Objects.PdfArray)reopened.Resolver.Resolve(field["Kids"])!);
        Assert.Equal("Widget", Assert.Single(PdfAnnotationReader.Read(reopened, 1)).Subtype);

        var none = PdfPageAssembler.Extract(doc, new[] { 2 });
        using var noForm = await Open(none);
        Assert.Null(noForm.Catalog["AcroForm"]);
    }

    // ------------------------------------------------------------------ save as copy

    [Theory]
    [InlineData(TestEncryptionKind.Rc4_40_R2)]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    public async Task FullRewrite_KeepsEncryption_AndDropsUnreachableObjects(TestEncryptionKind kind)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 rg 10 10 80 80 re f");
        b.Add("<< /Unreferenced (garbage) >>");
        var bytes = b.Build(new PdfTestEncryption(kind, "user", "owner"));
        using var doc = await Open(bytes, "user");
        var rewritten = PdfFullWriter.Rewrite(doc);
        using var oracle = new PdfiumOracle(rewritten, "user");
        using var source = new PdfiumOracle(bytes, "user");
        Assert.Equal(source.SecurityRevision, oracle.SecurityRevision);
        Assert.Equal(0, PdfiumOracle.MeanDifference(source.Render(0), oracle.Render(0)), 3);
        Assert.Throws<InvalidOperationException>(() => new PdfiumOracle(rewritten, "wrong"));
        using var reopened = await Open(rewritten, "user");
        Assert.Equal(1, reopened.PageCount);
        Assert.DoesNotContain(reopened.XrefTable.Entries.Keys, n => reopened.Resolver.Resolve(n) is Objects.PdfDictionary d && d.ContainsKey("Unreferenced"));
    }

    [Fact]
    public async Task SaveService_IncrementalAndFull_BothOpenInPdfium()
    {
        var bytes = ThreePages();
        string dir = Path.Combine(Path.GetTempPath(), "m9save_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var native = await PdfNativeDocument.OpenAsync(bytes, Path.Combine(dir, "source.pdf"));
            await new PdfNativePageOrganizerService().RotatePageAsync(native, 2, PageRotation.Rotate180);
            var save = new PdfNativeSaveService();
            string inc = Path.Combine(dir, "inc.pdf"), full = Path.Combine(dir, "full.pdf");
            await save.SaveAsync(native, inc, new PdfEngine.Save.SaveOptions { Mode = PdfEngine.Save.SaveMode.Incremental });
            await save.SaveAsync(native, full, new PdfEngine.Save.SaveOptions { Mode = PdfEngine.Save.SaveMode.FullRewrite });
            foreach (var path in new[] { inc, full })
            {
                using var oracle = new PdfiumOracle(await File.ReadAllBytesAsync(path));
                Assert.Equal(3, oracle.PageCount);
                Assert.Equal(180, oracle.Rotation(1));
            }
            Assert.True(new FileInfo(full).Length < new FileInfo(inc).Length);
            await Assert.ThrowsAsync<PdfEngine.Exceptions.PdfSaveException>(async () => await save.SaveAsync(native, Path.Combine(dir, "source.pdf")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InsertPages_FromAnotherDocument_AsFpdfImportPages()
    {
        var target = ThreePages();
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 100 100 re f", mediaBox: "[0 0 150 150]");
        var donor = b.Build();
        await using var native = await PdfNativeDocument.OpenAsync(target);
        await using var other = await PdfNativeDocument.OpenAsync(donor);
        await new PdfNativePageOrganizerService().InsertPagesAsync(native, 1, other, new[] { 1, 1 });
        using var oracle = new PdfiumOracle(native.Bytes);
        Assert.Equal(5, oracle.PageCount);
        Assert.Equal((150.0, 150.0), oracle.PageSize(1));
        Assert.Equal((150.0, 150.0), oracle.PageSize(2));
        using var donorOracle = new PdfiumOracle(donor);
        Assert.Equal(0, PdfiumOracle.MeanDifference(donorOracle.Render(0), oracle.Render(2)), 3);
        Assert.Equal(5, native.PageCount);
    }
}
