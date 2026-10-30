using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine;
using PdfEngine.Pages;
#if PDFIUM_RENDERER
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
#endif
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Services;
using PdfViewer.Core.Security;
using PdfViewer.Models;
using PdfViewer.Services;
using Xunit;
using PdfEngine.Vector.Annotations;

namespace PdfViewer.Tests;

/// <summary>
/// M9: in Auto and Vector modes the viewer's document information, bookmarks and annotations
/// come from the vector engine's native services, not PDFium. PDFium (PdfiumDocumentService,
/// the PDFium-mode service) is the oracle: each reads what the other wrote, the same way. Where
/// PDFium reads a file back, the test runs once per reading engine (PDFium in the developer
/// build only); where PDFium's own results are the expectation, every build compares with them
/// as committed goldens (<see cref="PdfiumGoldens"/>), which the developer build checks against PDFium.
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
    public async Task AnnotationsSavedNatively_ReadBackTheSame(int rotation)
    {
        string source = TestPdfBuilder.CreateSimplePdf(P($"src{rotation}.pdf"), 2, "Tok", rotation);
        string saved = P($"saved{rotation}.pdf");
        using (var service = Native())
        {
            await service.OpenDocumentAsync(source);
            Assert.True(service.IsVectorDocumentOpen);
            await service.SaveAnnotatedDocumentAsync(saved, AnnotationSaveMode.Embedded, Samples(2), source);
        }
        using var native = Native();
        await native.OpenDocumentAsync(saved);
        SameAnnotations(Samples(2), native.LoadExistingAnnotations());
    }

#if PDFIUM_RENDERER
    // PDFium reading the file the vector engine wrote: an interoperability check only PDFium can make.
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
#endif

    /// <summary>An annotation as a reader loaded it, for the goldens.</summary>
    public sealed record LoadedAnnotation(AnnotationType Type, int PageNumber, double X, double Y, double Width, double Height,
        string ColorHex, double Opacity, string? Contents, string? Author, string? Title, DateTime CreationDate,
        double StrokeThickness, List<int> InkStrokes, int Quads)
    {
        public static LoadedAnnotation Of(AnnotationModel a) => new(a.Type, a.PageNumber, a.X, a.Y, a.Width, a.Height, a.ColorHex, a.Opacity,
            a.Contents, a.Author, a.Title, a.CreationDate, a.StrokeThickness, a.InkStrokes.Select(s => s.Count).ToList(), a.Quads.Count);
    }

    private static void SameAsLoaded(IReadOnlyList<LoadedAnnotation> expected, IReadOnlyList<AnnotationModel> actual)
    {
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
            Assert.Equal(expected[i].InkStrokes, actual[i].InkStrokes.Select(s => s.Count));
            Assert.Equal(expected[i].Quads, actual[i].Quads.Count);
        }
    }

    /// <summary>
    /// The samples with a fixed author and date, for the file PDFium writes for the goldens (the
    /// defaults are the user's name and the time, which would end up in a committed file).
    /// </summary>
    private static List<AnnotationModel> FixedSamples(int pages)
    {
        var samples = Samples(pages);
        foreach (var a in samples)
        {
            if (a.Author == Environment.UserName) a.Author = "Reviewer";
            a.CreationDate = new DateTime(2026, 3, 4, 5, 6, 7);
        }
        return samples;
    }

    private const string PdfiumAnnotated = "NativeServices_AnnotatedByPdfium.pdf";

    /// <summary>
    /// A file PDFium annotated (committed, so the PDFium-free build has one): the native service
    /// loads what PDFium loads from it, as the developer build checks live below.
    /// </summary>
    [Fact]
    public async Task AnnotationsSavedByPdfium_AreLoadedAsPdfiumLoadsThem()
    {
        string saved = P("pdfium_saved.pdf");
        await File.WriteAllBytesAsync(saved, PdfiumGoldens.LoadFile(PdfiumAnnotated));
        using var native = Native();
        await native.OpenDocumentAsync(saved);
        SameAsLoaded(PdfiumGoldens.Load<List<LoadedAnnotation>>("NativeServices_AnnotatedByPdfium"), native.LoadExistingAnnotations());
    }

#if PDFIUM_RENDERER
    [Fact]
    [Trait("Category", PdfiumGoldens.Trait)]
    public async Task AnnotationsSavedByPdfium_TheCommittedCopyReadsAsPdfiumReadsIt()
    {
        if (PdfiumGoldens.Updating)
        {
            string source = TestPdfBuilder.CreateSimplePdf(P("fixed.pdf"), 2, "Tok");
            using var writer = new PdfiumDocumentService();
            await writer.OpenDocumentAsync(source);
            await writer.SaveAnnotatedDocumentAsync(P("fixed_saved.pdf"), AnnotationSaveMode.Embedded, FixedSamples(2), source);
            PdfiumGoldens.SaveFile(PdfiumAnnotated, await File.ReadAllBytesAsync(P("fixed_saved.pdf")));
        }
        string saved = P("pdfium_saved.pdf");
        await File.WriteAllBytesAsync(saved, PdfiumGoldens.LoadFile(PdfiumAnnotated));
        using var oracle = new PdfiumDocumentService();
        await oracle.OpenDocumentAsync(saved);
        var loaded = oracle.LoadExistingAnnotations();
        Assert.Equal(Samples(2).Count, loaded.Count);
        PdfiumGoldens.Verify("NativeServices_AnnotatedByPdfium", loaded.Select(LoadedAnnotation.Of).ToList());
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
#endif

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

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task SignedDocument_GetsAnIncrementalUpdate(string reader)
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
        using var check = TestEngines.CreateService(reader);
        await check.OpenDocumentAsync(saved);
        Assert.Equal(Samples(1).Count, check.LoadExistingAnnotations().Count);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task PasswordProtectedDocument_SavesAnnotationsNatively(string reader)
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

        using var check = TestEngines.CreateService(reader);
        await check.OpenDocumentAsync(P("locked_saved.pdf"), "user");
        Assert.True(check.GetMetadata()!.IsEncrypted);
        Assert.Equal(Samples(1).Select(a => a.Contents).OrderBy(c => c), check.LoadExistingAnnotations().Select(a => a.Contents).OrderBy(c => c));
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task FlattenedSave_DrawsTheAnnotationsIntoThePages(string reader)
    {
        string source = TestPdfBuilder.CreateSimplePdf(P("flat.pdf"), 1, "Tok");
        using var service = Native();
        await service.OpenDocumentAsync(source);
        await service.SaveAnnotatedDocumentAsync(P("embedded.pdf"), AnnotationSaveMode.Embedded, Samples(1), source);
        await service.SaveAnnotatedDocumentAsync(P("flattened.pdf"), AnnotationSaveMode.Flattened, Samples(1), source);

        using var flat = TestEngines.CreateService(reader);
        await flat.OpenDocumentAsync(P("flattened.pdf"));
        Assert.Empty(flat.LoadExistingAnnotations());
        using var embedded = TestEngines.CreateService(reader);
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


    // ------------------------------------------------------------------ document information

    /// <summary>The document information the viewer shows, for the goldens.</summary>
    public sealed record Information(string Title, string Author, string Subject, string Keywords, string Creator, string Producer,
        DateTime? CreationDate, DateTime? ModDate, string PdfFormatVersion, int PageCount, bool IsEncrypted,
        double DefaultPageWidthPt, double DefaultPageHeightPt, long FileSizeBytes,
        List<Mark> Bookmarks, PdfEngine.Documents.PdfDocumentPermissions Permissions);

    public sealed record Mark(int Depth, string Title, int Page);

    private static Information Read(DocumentMetadata m, IPdfDocumentService service)
    {
        static IEnumerable<Mark> Flat(IEnumerable<BookmarkItem> items, int d = 0) =>
            items.SelectMany(b => new[] { new Mark(d, b.Title, b.TargetPageNumber) }.Concat(Flat(b.Children, d + 1)));
        return new Information(m.Title, m.Author, m.Subject, m.Keywords, m.Creator, m.Producer, m.CreationDate, m.ModDate,
            m.PdfFormatVersion, m.PageCount, m.IsEncrypted, m.DefaultPageWidthPt, m.DefaultPageHeightPt, m.FileSizeBytes,
            Flat(service.ExtractBookmarks()).ToList(), service.Permissions);
    }

    private static void SameInformation(Information expected, Information actual)
    {
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Author, actual.Author);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.Keywords, actual.Keywords);
        Assert.Equal(expected.Creator, actual.Creator);
        Assert.Equal(expected.Producer, actual.Producer);
        Assert.Equal(expected.CreationDate, actual.CreationDate);
        Assert.Equal(expected.ModDate, actual.ModDate);
        Assert.Equal(expected.PdfFormatVersion, actual.PdfFormatVersion);
        Assert.Equal(expected.PageCount, actual.PageCount);
        Assert.Equal(expected.IsEncrypted, actual.IsEncrypted);
        Assert.Equal(expected.DefaultPageWidthPt, actual.DefaultPageWidthPt, 2);
        Assert.Equal(expected.DefaultPageHeightPt, actual.DefaultPageHeightPt, 2);
        Assert.Equal(expected.FileSizeBytes, actual.FileSizeBytes);
        Assert.Equal(expected.Bookmarks, actual.Bookmarks);
        Assert.Equal(expected.Permissions, actual.Permissions);
    }

    /// <summary>The native service's information, from opening the document and from asking for it afterwards.</summary>
    private async Task<Information[]> NativeInformation(string path)
    {
        using var native = Native();
        var opened = await native.OpenDocumentAsync(path);
        Assert.True(native.IsVectorDocumentOpen);
        return new[] { Read(opened, native), Read(native.GetMetadata()!, native) };
    }

    [Fact]
    public async Task MetadataAndBookmarks_MatchWhatPdfiumGave()
    {
        var expected = PdfiumGoldens.Load<Information>("NativeServices_Showcase");
        foreach (var actual in await NativeInformation(TestPdfBuilder.CreateEngineShowcasePdf(P("showcase.pdf"))))
            SameInformation(expected, actual);
    }

#if PDFIUM_RENDERER
    [Fact]
    [Trait("Category", PdfiumGoldens.Trait)]
    public async Task MetadataAndBookmarks_MatchThePdfiumService()
    {
        string path = TestPdfBuilder.CreateEngineShowcasePdf(P("showcase.pdf"));
        Information expected;
        using (var pdfium = new PdfiumDocumentService())
            expected = Read(await pdfium.OpenDocumentAsync(path), pdfium);
        PdfiumGoldens.Verify("NativeServices_Showcase", expected);
        foreach (var actual in await NativeInformation(path))
            SameInformation(expected, actual);
    }
#endif

    // ------------------------------------------------------------------ page organisation and attachments

    private static readonly PageArrangementEntry[] Arrangement =
        { new(3, PageRotation.Rotate90), new(1), new(4, PageRotation.Rotate180), new(1) };

    public sealed record ArrangedPage(PdfEngine.Documents.PageInfo Info, string Text);

    private static async Task<List<ArrangedPage>> Pages(IPdfEngine reader, string path)
    {
        await using var doc = await reader.OpenDocumentAsync(path);
        var pages = new List<ArrangedPage>();
        for (int i = 1; i <= doc.PageCount; i++)
            pages.Add(new ArrangedPage(await doc.GetPageInfoAsync(i), await reader.TextService.ExtractPageTextAsync(doc, i)));
        return pages;
    }

    /// <summary>Arranges the pages with the native organizer: each page as PDFium arranged them (read natively), and the outline.</summary>
    [Fact]
    public async Task ArrangePages_Natively_MatchesWhatPdfiumWrote()
    {
        string source = TestPdfBuilder.CreateSimplePdf(P("arrange.pdf"), 4, "Tok");
        await using (var doc = await PdfEditableDocument.OpenAsync(source))
            await new PdfPageOrganizerService().ArrangePagesAsync(doc, Arrangement, P("by_native.pdf"));
        using var native = TestEngines.Create(TestEngines.Native);
        Assert.Equal(PdfiumGoldens.Load<List<ArrangedPage>>("NativeServices_ArrangedByPdfium"), await Pages(native, P("by_native.pdf")));
        await using var b = await native.OpenDocumentAsync(P("by_native.pdf"));
        Assert.Equal(new[] { 3, 2, 1 }, (await b.GetBookmarksAsync()).Select(x => x.TargetPageNumber).OrderByDescending(n => n));
    }

#if PDFIUM_RENDERER
    [Fact]
    [Trait("Category", PdfiumGoldens.Trait)]
    public async Task ArrangePages_Natively_MatchesWhatPdfiumWrites()
    {
        string source = TestPdfBuilder.CreateSimplePdf(P("arrange.pdf"), 4, "Tok");
        var arrangement = Arrangement;
        using IPdfEngine pdfium = new PdfiumEngine();
        await using (var doc = await pdfium.OpenDocumentAsync(source))
            await pdfium.PageOrganizer.ArrangePagesAsync(doc, arrangement, P("by_pdfium.pdf"));
        await using (var doc = await PdfEditableDocument.OpenAsync(source))
            await new PdfPageOrganizerService().ArrangePagesAsync(doc, arrangement, P("by_native.pdf"));
        PdfiumGoldens.Verify("NativeServices_ArrangedByPdfium", await Pages(pdfium, P("by_pdfium.pdf")));

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
#endif

    /// <summary>Merges, splits and extracts natively, and opens the results with each reading engine.</summary>
    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task MergeSplitAndExtract_Natively_OpenInEachEngine(string reader)
    {
        string a = TestPdfBuilder.CreateSimplePdf(P("a.pdf"), 2, "Alpha");
        string b = TestPdfBuilder.CreateSimplePdf(P("b.pdf"), 3, "Beta");
        var organizer = new PdfPageOrganizerService();
        await organizer.MergeDocumentsAsync(new[] { a, b }, P("merged.pdf"));
        using IPdfEngine check = TestEngines.Create(reader);
        await using (var merged = await check.OpenDocumentAsync(P("merged.pdf")))
        {
            Assert.Equal(5, merged.PageCount);
            Assert.Contains("Beta", await check.TextService.ExtractPageTextAsync(merged, 3));
            var outline = await merged.GetBookmarksAsync();
            Assert.Equal(new[] { "a", "b" }, outline.Select(o => o.Title));
            Assert.Equal(new[] { 1, 3 }, outline.Select(o => o.TargetPageNumber));
        }

        await using var doc = await PdfEditableDocument.OpenAsync(P("merged.pdf"));
        await organizer.SplitDocumentAsync(doc, new[] { 2, 3 }, P("split"), "part");
        await organizer.ExtractPagesAsync(doc, new[] { 5, 1 }, P("extract.pdf"));
        await using (var first = await check.OpenDocumentAsync(Path.Combine(P("split"), "part_part001.pdf")))
            Assert.Equal(2, first.PageCount);
        await using (var second = await check.OpenDocumentAsync(Path.Combine(P("split"), "part_part002.pdf")))
            Assert.Equal(3, second.PageCount);
        await using var extract = await check.OpenDocumentAsync(P("extract.pdf"));
        Assert.Equal(2, extract.PageCount);
        Assert.Contains("Beta", await check.TextService.ExtractPageTextAsync(extract, 1));
        Assert.Contains("Alpha", await check.TextService.ExtractPageTextAsync(extract, 2));
    }

    private static readonly string[] ExecutableNameSamples = { "invoice.pdf.exe", "payload.exe . ", "notes.txt", "script.PS1" };

    /// <summary>What PDFium's attachment service reports and extracts, for the goldens.</summary>
    public sealed record Attachments(List<PdfEngine.Safety.EmbeddedFileInfo> Files, byte[] FirstFile, List<bool> ExecutableNames);

    [Fact]
    public async Task Attachments_Natively_ListAndExtractAsPdfiumDid()
    {
        var expected = PdfiumGoldens.Load<Attachments>("NativeServices_Attachments");
        string path = TestPdfBuilder.CreateAttachmentPdf(P("att.pdf"), "the payload");
        await using var ndoc = await PdfEditableDocument.OpenAsync(path);
        var service = new PdfAttachmentService();
        Assert.Equal(expected.Files, await service.GetAttachmentsAsync(ndoc));
        long written = await service.ExtractAttachmentAsync(ndoc, 0, P("out.bin"));
        Assert.Equal(expected.FirstFile, await File.ReadAllBytesAsync(P("out.bin")));
        Assert.Equal(written, new FileInfo(P("out.bin")).Length);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await service.ExtractAttachmentAsync(ndoc, 5, P("none.bin")));
        Assert.Equal(expected.ExecutableNames, ExecutableNameSamples.Select(PdfAttachmentService.IsExecutableName));
    }

#if PDFIUM_RENDERER
    [Fact]
    [Trait("Category", PdfiumGoldens.Trait)]
    public async Task Attachments_Natively_ListAndExtractAsPdfiumDoes()
    {
        string path = TestPdfBuilder.CreateAttachmentPdf(P("att.pdf"), "the payload");
        using IPdfEngine pdfium = new PdfiumEngine();
        await using var pdoc = await pdfium.OpenDocumentAsync(path);
        var expected = await new PdfiumAttachmentService().GetAttachmentsAsync(pdoc);
        await using var ndoc = await PdfEditableDocument.OpenAsync(path);
        var service = new PdfAttachmentService();
        var actual = await service.GetAttachmentsAsync(ndoc);
        Assert.Equal(expected, actual);
        long written = await service.ExtractAttachmentAsync(ndoc, 0, P("out.bin"));
        await new PdfiumAttachmentService().ExtractAttachmentAsync(pdoc, 0, P("out_pdfium.bin"));
        Assert.Equal(await File.ReadAllBytesAsync(P("out_pdfium.bin")), await File.ReadAllBytesAsync(P("out.bin")));
        Assert.Equal(written, new FileInfo(P("out.bin")).Length);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await service.ExtractAttachmentAsync(ndoc, 5, P("none.bin")));

        foreach (var name in ExecutableNameSamples)
            Assert.Equal(PdfiumAttachmentService.IsExecutableName(name), PdfAttachmentService.IsExecutableName(name));

        PdfiumGoldens.Verify("NativeServices_Attachments", new Attachments(expected.ToList(), await File.ReadAllBytesAsync(P("out_pdfium.bin")),
            ExecutableNameSamples.Select(PdfiumAttachmentService.IsExecutableName).ToList()));
    }
#endif
}
