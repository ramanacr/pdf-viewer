using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine;
using PdfEngine.Pages;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Services;
using PdfViewer.Core.Security;
using PdfViewer.Models;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// M9: in Auto and Vector modes the viewer's document information, bookmarks and annotations
/// come from the vector engine's native services, not PDFium. PDFium (PdfiumDocumentService,
/// the PDFium-mode service) is the oracle: each reads what the other wrote, the same way.
/// </summary>
public class NativeDocumentServicesViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PdfNativeServices_" + Guid.NewGuid().ToString("N"));

    public NativeDocumentServicesViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static HybridVectorDocumentService Native(PdfEngineMode mode = PdfEngineMode.Auto) => new(PdfSecurityPolicy.DefaultStrict, mode);

    private static List<AnnotationModel> Samples(int pages) => new()
    {
        new() { PageNumber = 1, Type = AnnotationType.Highlight, X = 0.1, Y = 0.1, Width = 0.3, Height = 0.05, ColorHex = "#FFFF00", Opacity = 0.4, Contents = "hl", Author = "A", Title = "Sub" },
        new() { PageNumber = 1, Type = AnnotationType.Ink, X = 0.1, Y = 0.3, Width = 0.3, Height = 0.2, ColorHex = "#FF0000", Opacity = 1, StrokeThickness = 4,
            InkStrokes = new() { new() { new Point(0.1, 0.3), new Point(0.2, 0.4) }, new() { new Point(0.3, 0.3), new Point(0.35, 0.45), new Point(0.4, 0.5) } } },
        new() { PageNumber = pages, Type = AnnotationType.Rectangle, X = 0.5, Y = 0.5, Width = 0.2, Height = 0.1, ColorHex = "#2979FF", Opacity = 0.7, StrokeThickness = 3, Contents = "box" },
        new() { PageNumber = pages, Type = AnnotationType.Note, X = 0.8, Y = 0.1, Width = 0.03, Height = 0.03, ColorHex = "#FFA500", Opacity = 1, Contents = "note ü" },
        new() { PageNumber = 1, Type = AnnotationType.FreeText, X = 0.1, Y = 0.7, Width = 0.4, Height = 0.06, ColorHex = "#008000", Opacity = 1, Contents = "typed" },
        new() { PageNumber = 1, Type = AnnotationType.Ellipse, X = 0.6, Y = 0.7, Width = 0.2, Height = 0.1, ColorHex = "#800080", Opacity = 1 },
    };

    private static void SameAnnotations(IReadOnlyList<AnnotationModel> expected, IReadOnlyList<AnnotationModel> actual, bool colours = true)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var e in expected)
        {
            var a = actual.Single(x => x.PageNumber == e.PageNumber && x.Type == e.Type && Math.Abs(x.X - e.X) < 1e-3 && Math.Abs(x.Y - e.Y) < 1e-3);
            Assert.Equal(e.Width, a.Width, 3);
            Assert.Equal(e.Height, a.Height, 3);
            Assert.Equal(e.Contents, a.Contents);
            Assert.Equal(e.Author, a.Author);
            Assert.Equal(e.InkStrokes.Count, a.InkStrokes.Count);
            Assert.Equal(e.InkStrokes.Select(s => s.Count), a.InkStrokes.Select(s => s.Count));
            Assert.Equal(e.Opacity, a.Opacity, 2);
            if (colours) Assert.Equal(e.ColorHex, a.ColorHex);
            Assert.Equal(e.StrokeThickness, a.StrokeThickness, 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task AnnotationsSavedNatively_ReadTheSameInPdfium_AndBack(int rotation)
    {
        string source = TestPdfBuilder.CreateSimplePdf(P($"src{rotation}.pdf"), 2, "Tok", rotation);
        string saved = P($"saved{rotation}.pdf");
        using (var service = Native())
        {
            await service.OpenDocumentAsync(source);
            Assert.True(service.IsVectorDocumentOpen);
            await service.SaveAnnotatedDocumentAsync(saved, AnnotationSaveMode.Embedded, Samples(2), source);
        }

        using var pdfium = new PdfiumDocumentService();
        await pdfium.OpenDocumentAsync(saved);
        var byPdfium = pdfium.LoadExistingAnnotations();
        using var native = Native();
        await native.OpenDocumentAsync(saved);
        var byNative = native.LoadExistingAnnotations();

        // PDFium does not report colours of annotations that have appearance streams; everything else agrees.
        SameAnnotations(Samples(2), byPdfium.Select(a => { a.Author = a.Author == "Author" ? Environment.UserName : a.Author; return a; }).ToList(), colours: false);
        SameAnnotations(Samples(2), byNative);
        Assert.Equal(byPdfium.Select(a => (a.Type, a.Contents)).OrderBy(x => x.ToString()), byNative.Select(a => (a.Type, a.Contents)).OrderBy(x => x.ToString()));
    }

    [Fact]
    public async Task AnnotationsSavedByPdfium_AreLoadedTheSameNatively()
    {
        string source = TestPdfBuilder.CreateSimplePdf(P("p.pdf"), 2, "Tok");
        string saved = P("p_saved.pdf");
        using (var pdfium = new PdfiumDocumentService())
        {
            await pdfium.OpenDocumentAsync(source);
            await pdfium.SaveAnnotatedDocumentAsync(saved, AnnotationSaveMode.Embedded, Samples(2), source);
        }
        using var oracle = new PdfiumDocumentService();
        await oracle.OpenDocumentAsync(saved);
        var expected = oracle.LoadExistingAnnotations();
        using var native = Native();
        await native.OpenDocumentAsync(saved);
        var actual = native.LoadExistingAnnotations();
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Type, actual[i].Type);
            Assert.Equal(expected[i].PageNumber, actual[i].PageNumber);
            Assert.Equal(expected[i].X, actual[i].X, 4);
            Assert.Equal(expected[i].Y, actual[i].Y, 4);
            Assert.Equal(expected[i].Width, actual[i].Width, 4);
            Assert.Equal(expected[i].Height, actual[i].Height, 4);
            Assert.Equal(expected[i].ColorHex, actual[i].ColorHex);
            Assert.Equal(expected[i].Opacity, actual[i].Opacity, 2);
            Assert.Equal(expected[i].Contents, actual[i].Contents);
            Assert.Equal(expected[i].Author, actual[i].Author);
            Assert.Equal(expected[i].Title, actual[i].Title);
            Assert.Equal(expected[i].CreationDate, actual[i].CreationDate);
            Assert.Equal(expected[i].StrokeThickness, actual[i].StrokeThickness, 2);
            Assert.Equal(expected[i].InkStrokes.Select(s => s.Count), actual[i].InkStrokes.Select(s => s.Count));
            Assert.Equal(expected[i].Quads.Count, actual[i].Quads.Count);
        }
    }

    [Fact]
    public async Task UnchangedAnnotations_KeepWhatTheViewerDoesNotModel()
    {
        // A note with a pop-up, a unique name and rich text, as another application wrote it.
        string path = P("foreign.pdf");
        TestPdfBuilder.CreateSimplePdf(path, 1, "Tok");
        byte[] original = await File.ReadAllBytesAsync(path);
        byte[] foreign;
        using (var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(original))
        {
            foreign = PdfAnnotationEditor.Apply(doc, original, e => e.Add(new PdfAnnotation
            {
                PageNumber = 1, Subtype = "Text", Rect = new PdfEngine.Geometry.PdfRect(100, 600, 20, 20), Contents = "foreign", Name = "unique-name-1",
                RichContents = "<body><p>foreign</p></body>", PopupRect = new PdfEngine.Geometry.PdfRect(120, 500, 150, 100), Author = "Someone",
            }));
        }
        await File.WriteAllBytesAsync(path, foreign);

        var vm = new PdfViewer.ViewModels.MainViewModel();
        await vm.LoadDocumentAsync(path);
        Assert.Single(vm.AllAnnotations);
        vm.AddAnnotation(Samples(1)[0]);
        await vm.SaveAsync();

        using var reopened = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(await File.ReadAllBytesAsync(path));
        var annots = PdfAnnotationReader.Read(reopened, 1);
        var note = Assert.Single(annots, a => a.Subtype == "Text");
        Assert.Equal("unique-name-1", note.Name);
        Assert.Equal("<body><p>foreign</p></body>", note.RichContents);
        Assert.NotEqual(0, note.PopupObjectNumber);
        Assert.Single(annots, a => a.Subtype == "Popup");
        Assert.Single(annots, a => a.Subtype == "Highlight");

        // Editing it keeps the pop-up and name, and drops rich text that no longer matches.
        var vm2 = new PdfViewer.ViewModels.MainViewModel();
        await vm2.LoadDocumentAsync(path);
        var loaded = vm2.AllAnnotations.Single(a => a.Type == AnnotationType.Note);
        loaded.Contents = "edited";
        vm2.NoteAnnotationEdited(loaded);
        await vm2.SaveAsync();
        using var edited = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(await File.ReadAllBytesAsync(path));
        var editedNote = PdfAnnotationReader.Read(edited, 1).Single(a => a.Subtype == "Text");
        Assert.Equal("edited", editedNote.Contents);
        Assert.Equal("unique-name-1", editedNote.Name);
        Assert.Null(editedNote.RichContents);
        Assert.NotEqual(0, editedNote.PopupObjectNumber);
    }

    [Fact]
    public async Task UnsignedSave_IsWrittenWhole_SoDeletedCommentsDoNotLinger()
    {
        string path = TestPdfBuilder.CreateSimplePdf(P("whole.pdf"), 1, "Tok");
        var vm = new PdfViewer.ViewModels.MainViewModel();
        await vm.LoadDocumentAsync(path);
        var secret = Samples(1)[0];
        secret.Contents = "SECRETCOMMENT";
        vm.AddAnnotation(secret);
        await vm.SaveAsync();
        string secretHex = Convert.ToHexString(Encoding.ASCII.GetBytes("SECRETCOMMENT")); // strings are written in hex
        Assert.Contains(secretHex, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path)));

        var again = new PdfViewer.ViewModels.MainViewModel();
        await again.LoadDocumentAsync(path);
        again.DeleteAnnotation(again.AllAnnotations.Single());
        await again.SaveAsync();
        string bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(secretHex, bytes);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(bytes, "%%EOF"));
    }

    [Fact]
    public async Task SignedDocument_GetsAnIncrementalUpdate()
    {
        string path = P("signed.pdf");
        TestPdfBuilder.CreateSimplePdf(path, 1, "Tok");
        byte[] original = await File.ReadAllBytesAsync(path);
        byte[] signedBytes;
        using (var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(original))
        {
            // A signature field with a value: enough for the save to treat the file as signed.
            var update = new PdfUpdate(doc, original);
            int sig = update.Add(new PdfEngine.Vector.Objects.PdfDictionary(new Dictionary<string, PdfEngine.Vector.Objects.PdfObject>
            {
                ["Type"] = new PdfEngine.Vector.Objects.PdfName("Sig"),
                ["Filter"] = new PdfEngine.Vector.Objects.PdfName("Adobe.PPKLite"),
            }));
            int field = update.Add(new PdfEngine.Vector.Objects.PdfDictionary(new Dictionary<string, PdfEngine.Vector.Objects.PdfObject>
            {
                ["FT"] = new PdfEngine.Vector.Objects.PdfName("Sig"),
                ["T"] = PdfEngine.Vector.Objects.PdfObjectWriter.TextString("Signature1"),
                ["V"] = new PdfEngine.Vector.Objects.PdfIndirectRef(sig),
            }));
            update.Set(update.RootNumber, PdfUpdate.With(update.Catalog, ("AcroForm", new PdfEngine.Vector.Objects.PdfDictionary(new Dictionary<string, PdfEngine.Vector.Objects.PdfObject>
            {
                ["Fields"] = new PdfEngine.Vector.Objects.PdfArray(new PdfEngine.Vector.Objects.PdfObject[] { new PdfEngine.Vector.Objects.PdfIndirectRef(field) }),
            }))));
            signedBytes = update.Commit();
        }
        await File.WriteAllBytesAsync(path, signedBytes);

        using var service = Native();
        await service.OpenDocumentAsync(path);
        string saved = P("signed_saved.pdf");
        await service.SaveAnnotatedDocumentAsync(saved, AnnotationSaveMode.Embedded, Samples(1), path);
        byte[] output = await File.ReadAllBytesAsync(saved);
        Assert.True(output.AsSpan(0, signedBytes.Length).SequenceEqual(signedBytes), "the signed revision must be kept byte for byte");
        using var pdfium = new PdfiumDocumentService();
        await pdfium.OpenDocumentAsync(saved);
        Assert.Equal(Samples(1).Count, pdfium.LoadExistingAnnotations().Count);
    }

    [Fact]
    public async Task PasswordProtectedDocument_SavesAnnotationsNatively()
    {
        string plain = TestPdfBuilder.CreateSimplePdf(P("plain.pdf"), 1, "Tok");
        using (var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(await File.ReadAllBytesAsync(plain)))
        {
            var result = PdfEngine.Vector.Security.PdfEncryptor.Encrypt(doc, new PdfEngine.Vector.Security.PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" });
            await File.WriteAllBytesAsync(P("locked.pdf"), result.Bytes);
        }
        using var service = Native();
        await service.OpenDocumentAsync(P("locked.pdf"), "user");
        await service.SaveAnnotatedDocumentAsync(P("locked_saved.pdf"), AnnotationSaveMode.Embedded, Samples(1), P("locked.pdf"));

        using var pdfium = new PdfiumDocumentService();
        await pdfium.OpenDocumentAsync(P("locked_saved.pdf"), "user");
        Assert.True(pdfium.GetMetadata()!.IsEncrypted);
        Assert.Equal(Samples(1).Select(a => a.Contents).OrderBy(c => c), pdfium.LoadExistingAnnotations().Select(a => a.Contents).OrderBy(c => c));
    }

    [Fact]
    public async Task FlattenedSave_DrawsTheAnnotationsIntoThePages()
    {
        string source = TestPdfBuilder.CreateSimplePdf(P("flat.pdf"), 1, "Tok");
        using var service = Native();
        await service.OpenDocumentAsync(source);
        await service.SaveAnnotatedDocumentAsync(P("embedded.pdf"), AnnotationSaveMode.Embedded, Samples(1), source);
        await service.SaveAnnotatedDocumentAsync(P("flattened.pdf"), AnnotationSaveMode.Flattened, Samples(1), source);

        using var flat = new PdfiumDocumentService();
        await flat.OpenDocumentAsync(P("flattened.pdf"));
        Assert.Empty(flat.LoadExistingAnnotations());
        using var embedded = new PdfiumDocumentService();
        await embedded.OpenDocumentAsync(P("embedded.pdf"));
        var a = embedded.RenderPage(1, 72)!;
        var b = flat.RenderPage(1, 72)!;
        Assert.True(MeanDifference(a, b) < 0.5, "a flattened page looks like the annotated one");
    }

    private static double MeanDifference(System.Windows.Media.Imaging.BitmapSource a, System.Windows.Media.Imaging.BitmapSource b)
    {
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        int stride = a.PixelWidth * 4;
        var pa = new byte[stride * a.PixelHeight];
        var pb = new byte[stride * b.PixelHeight];
        new System.Windows.Media.Imaging.FormatConvertedBitmap(a, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pa, stride, 0);
        new System.Windows.Media.Imaging.FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pb, stride, 0);
        long total = 0;
        for (int i = 0; i < pa.Length; i++) total += Math.Abs(pa[i] - pb[i]);
        return (double)total / pa.Length;
    }

    [Fact]
    public async Task MetadataAndBookmarks_MatchThePdfiumService()
    {
        string path = TestPdfBuilder.CreateEngineShowcasePdf(P("showcase.pdf"));
        using var pdfium = new PdfiumDocumentService();
        var expected = await pdfium.OpenDocumentAsync(path);
        using var native = Native();
        var actual = await native.OpenDocumentAsync(path);
        Assert.True(native.IsVectorDocumentOpen);
        foreach (var m in new[] { actual, native.GetMetadata()! })
        {
            Assert.Equal(expected.Title, m.Title);
            Assert.Equal(expected.Author, m.Author);
            Assert.Equal(expected.Subject, m.Subject);
            Assert.Equal(expected.Keywords, m.Keywords);
            Assert.Equal(expected.Creator, m.Creator);
            Assert.Equal(expected.Producer, m.Producer);
            Assert.Equal(expected.CreationDate, m.CreationDate);
            Assert.Equal(expected.ModDate, m.ModDate);
            Assert.Equal(expected.PdfFormatVersion, m.PdfFormatVersion);
            Assert.Equal(expected.PageCount, m.PageCount);
            Assert.Equal(expected.IsEncrypted, m.IsEncrypted);
            Assert.Equal(expected.DefaultPageWidthPt, m.DefaultPageWidthPt, 2);
            Assert.Equal(expected.DefaultPageHeightPt, m.DefaultPageHeightPt, 2);
            Assert.Equal(expected.FileSizeBytes, m.FileSizeBytes);
        }
        static IEnumerable<(int, string, int)> Flat(IEnumerable<BookmarkItem> items, int d = 0) =>
            items.SelectMany(b => new[] { (d, b.Title, b.TargetPageNumber) }.Concat(Flat(b.Children, d + 1)));
        Assert.Equal(Flat(pdfium.ExtractBookmarks()), Flat(native.ExtractBookmarks()));
        Assert.Equal(pdfium.Permissions, native.Permissions);
    }

    // ------------------------------------------------------------------ page organisation and attachments

    [Fact]
    public async Task ArrangePages_Natively_MatchesWhatPdfiumWrites()
    {
        string source = TestPdfBuilder.CreateSimplePdf(P("arrange.pdf"), 4, "Tok");
        var arrangement = new[] { new PageArrangementEntry(3, PageRotation.Rotate90), new PageArrangementEntry(1), new PageArrangementEntry(4, PageRotation.Rotate180), new PageArrangementEntry(1) };
        using IPdfEngine pdfium = new PdfiumEngine();
        await using (var doc = await pdfium.OpenDocumentAsync(source))
            await pdfium.PageOrganizer.ArrangePagesAsync(doc, arrangement, P("by_pdfium.pdf"));
        await using (var doc = await PdfNativeDocument.OpenAsync(source))
            await new PdfNativePageOrganizerService().ArrangePagesAsync(doc, arrangement, P("by_native.pdf"));

        await using var a = await pdfium.OpenDocumentAsync(P("by_pdfium.pdf"));
        await using var b = await pdfium.OpenDocumentAsync(P("by_native.pdf"));
        Assert.Equal(a.PageCount, b.PageCount);
        for (int i = 1; i <= a.PageCount; i++)
        {
            Assert.Equal(await a.GetPageInfoAsync(i), await b.GetPageInfoAsync(i));
            Assert.Equal(await pdfium.TextService.ExtractPageTextAsync(a, i), await pdfium.TextService.ExtractPageTextAsync(b, i));
        }
        // Unlike FPDF_ImportPages, the outline items whose pages were taken come along.
        Assert.Equal(new[] { 3, 2, 1 }, (await b.GetBookmarksAsync()).Select(x => x.TargetPageNumber).OrderByDescending(n => n));
    }

    [Fact]
    public async Task MergeSplitAndExtract_Natively_OpenInPdfium()
    {
        string a = TestPdfBuilder.CreateSimplePdf(P("a.pdf"), 2, "Alpha");
        string b = TestPdfBuilder.CreateSimplePdf(P("b.pdf"), 3, "Beta");
        var organizer = new PdfNativePageOrganizerService();
        await organizer.MergeDocumentsAsync(new[] { a, b }, P("merged.pdf"));
        using IPdfEngine pdfium = new PdfiumEngine();
        await using (var merged = await pdfium.OpenDocumentAsync(P("merged.pdf")))
        {
            Assert.Equal(5, merged.PageCount);
            Assert.Contains("Beta", await pdfium.TextService.ExtractPageTextAsync(merged, 3));
            var outline = await merged.GetBookmarksAsync();
            Assert.Equal(new[] { "a", "b" }, outline.Select(o => o.Title));
            Assert.Equal(new[] { 1, 3 }, outline.Select(o => o.TargetPageNumber));
        }

        await using var doc = await PdfNativeDocument.OpenAsync(P("merged.pdf"));
        await organizer.SplitDocumentAsync(doc, new[] { 2, 3 }, P("split"), "part");
        await organizer.ExtractPagesAsync(doc, new[] { 5, 1 }, P("extract.pdf"));
        await using (var first = await pdfium.OpenDocumentAsync(Path.Combine(P("split"), "part_part001.pdf")))
            Assert.Equal(2, first.PageCount);
        await using (var second = await pdfium.OpenDocumentAsync(Path.Combine(P("split"), "part_part002.pdf")))
            Assert.Equal(3, second.PageCount);
        await using var extract = await pdfium.OpenDocumentAsync(P("extract.pdf"));
        Assert.Equal(2, extract.PageCount);
        Assert.Contains("Beta", await pdfium.TextService.ExtractPageTextAsync(extract, 1));
        Assert.Contains("Alpha", await pdfium.TextService.ExtractPageTextAsync(extract, 2));
    }

    [Fact]
    public async Task Attachments_Natively_ListAndExtractAsPdfiumDoes()
    {
        string path = TestPdfBuilder.CreateAttachmentPdf(P("att.pdf"), "the payload");
        using IPdfEngine pdfium = new PdfiumEngine();
        await using var pdoc = await pdfium.OpenDocumentAsync(path);
        var expected = await new PdfiumAttachmentService().GetAttachmentsAsync(pdoc);
        await using var ndoc = await PdfNativeDocument.OpenAsync(path);
        var service = new PdfNativeAttachmentService();
        var actual = await service.GetAttachmentsAsync(ndoc);
        Assert.Equal(expected, actual);
        long written = await service.ExtractAttachmentAsync(ndoc, 0, P("out.bin"));
        await new PdfiumAttachmentService().ExtractAttachmentAsync(pdoc, 0, P("out_pdfium.bin"));
        Assert.Equal(await File.ReadAllBytesAsync(P("out_pdfium.bin")), await File.ReadAllBytesAsync(P("out.bin")));
        Assert.Equal(written, new FileInfo(P("out.bin")).Length);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await service.ExtractAttachmentAsync(ndoc, 5, P("none.bin")));

        foreach (var (name, executable) in new[] { ("invoice.pdf.exe", true), ("payload.exe . ", true), ("notes.txt", false), ("script.PS1", true) })
            Assert.Equal(PdfiumAttachmentService.IsExecutableName(name), PdfNativeAttachmentService.IsExecutableName(name));
    }
}
