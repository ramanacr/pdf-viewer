using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// What an annotation is worth is what survives being written to a file and read back - by
/// this viewer and by any other. These tests are about the round trip and about the bytes,
/// not about a call returning without throwing.
/// </summary>
public class AnnotationFidelityTests : IDisposable
{
    private readonly string _testDir;

    public AnnotationFidelityTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfAnnotFidelity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private string NewPdf(string name, int pages = 2) =>
        TestPdfBuilder.CreateSimplePdf(Path.Combine(_testDir, name), pages, "FidelityToken");

    private static AnnotationModel Highlight(int page = 1, string contents = "note") => new()
    {
        PageNumber = page,
        Type = AnnotationType.Highlight,
        X = 0.1,
        Y = 0.1,
        Width = 0.25,
        Height = 0.04,
        ColorHex = "#FFFF00",
        Opacity = 0.4,
        Contents = contents
    };

    private static async Task<List<AnnotationModel>> ReadBack(string path)
    {
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(path);
        return service.LoadExistingAnnotations();
    }

    // ---------------------------------------------------------------------------------
    // The set that goes in is the set that comes out.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Saving used to copy the original bytes and add the annotation set on top. Since that
    /// set had been read out of those same bytes, every save doubled the document's comments -
    /// three saves, eight copies of the same highlight.
    /// </summary>
    [Fact]
    public async Task TestSavingRepeatedlyDoesNotMultiplyTheAnnotations()
    {
        string path = NewPdf("multiply.pdf");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(Highlight());
        await vm.SaveAsync();

        Assert.Single(await ReadBack(path));

        // Reopen and save again without touching anything, three times over.
        for (int round = 0; round < 3; round++)
        {
            var reopened = new MainViewModel();
            await reopened.LoadDocumentAsync(path);

            // Exactly what the previous round left behind - no copies of it.
            Assert.Equal(round + 1, reopened.AllAnnotations.Count);

            reopened.AddAnnotation(Highlight(1, $"round {round}"));
            await reopened.SaveAsync();

            Assert.Equal(round + 2, (await ReadBack(path)).Count);
        }
    }

    /// <summary>
    /// Deleting is the half that was purely decorative: the annotation left the list, the
    /// title bar showed unsaved work, Save reported success - and the annotation was still
    /// in the file, because the file being copied was the one that still contained it.
    /// </summary>
    [Fact]
    public async Task TestDeletingAnAnnotationRemovesItFromTheFile()
    {
        string path = NewPdf("delete.pdf");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(Highlight(1, "keep me"));
        vm.AddAnnotation(Highlight(1, "delete me"));
        await vm.SaveAsync();

        Assert.Equal(2, (await ReadBack(path)).Count);

        var reopened = new MainViewModel();
        await reopened.LoadDocumentAsync(path);

        var doomed = reopened.AllAnnotations.Single(a => a.Contents == "delete me");
        reopened.DeleteAnnotation(doomed);
        await reopened.SaveAsync();

        var survivors = await ReadBack(path);
        Assert.Single(survivors);
        Assert.Equal("keep me", survivors[0].Contents);
    }

    [Fact]
    public async Task TestClearingAllAnnotationsEmptiesTheFile()
    {
        string path = NewPdf("clear.pdf");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(Highlight(1));
        vm.AddAnnotation(Highlight(2));
        await vm.SaveAsync();
        Assert.Equal(2, (await ReadBack(path)).Count);

        var reopened = new MainViewModel();
        await reopened.LoadDocumentAsync(path);
        reopened.ClearAllAnnotations();
        await reopened.SaveAsync();

        Assert.Empty(await ReadBack(path));
    }

    // ---------------------------------------------------------------------------------
    // What other viewers need in order to draw it at all.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A text markup annotation is painted from its /QuadPoints. Written with only a /Rect,
    /// it is a well-formed annotation that Acrobat, Foxit and Edge all draw as nothing - the
    /// highlight existed only inside this viewer. Asserted against the file's bytes.
    /// </summary>
    [Theory]
    [InlineData(AnnotationType.Highlight)]
    [InlineData(AnnotationType.Underline)]
    [InlineData(AnnotationType.StrikeOut)]
    public async Task TestTextMarkupIsWrittenWithQuadPoints(AnnotationType type)
    {
        string path = NewPdf($"quads_{type}.pdf", 1);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        var annot = Highlight();
        annot.Type = type;
        vm.AddAnnotation(annot);
        await vm.SaveAsync();

        string bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        Assert.Contains("/QuadPoints", bytes);
    }

    /// <summary>
    /// Every viewer lists a comment as who wrote it and when. Without these entries the date
    /// column was blank everywhere else, and reopening here stamped the annotation with the
    /// moment the file happened to be opened.
    /// </summary>
    [Fact]
    public async Task TestTheCreationDateIsWrittenAndComesBack()
    {
        string path = NewPdf("dates.pdf", 1);
        var when = new DateTime(2021, 3, 4, 9, 30, 15, DateTimeKind.Local);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        var annot = Highlight();
        annot.CreationDate = when;
        vm.AddAnnotation(annot);
        await vm.SaveAsync();

        string bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        Assert.Contains("/CreationDate", bytes);
        Assert.Contains("/M", bytes);

        var loaded = Assert.Single(await ReadBack(path));
        Assert.Equal(when, loaded.CreationDate);
    }

    [Fact]
    public void TestPdfDatesAreWrittenInTheFormTheSpecRequires()
    {
        string formatted = PdfiumDocumentService.FormatPdfDate(new DateTime(2026, 9, 6, 14, 30, 0));

        Assert.StartsWith("D:20260906143000", formatted);
        Assert.Matches(@"^D:\d{14}[+-]\d{2}'\d{2}'$", formatted);
    }

    // ---------------------------------------------------------------------------------
    // Freehand ink.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A PDF ink annotation holds a list of strokes. Reading only the first one and then
    /// saving threw away every stroke after it, silently, in a file the user had opened only
    /// to add a comment.
    /// </summary>
    [Fact]
    public async Task TestEveryInkStrokeSurvivesTheRoundTrip()
    {
        string path = NewPdf("ink.pdf", 1);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        vm.AddAnnotation(new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Ink,
            X = 0.1, Y = 0.1, Width = 0.4, Height = 0.3,
            ColorHex = "#FF0000",
            Opacity = 1.0,
            StrokeThickness = 5.0,
            InkStrokes = new List<List<Point>>
            {
                new() { new Point(0.10, 0.10), new Point(0.20, 0.20), new Point(0.30, 0.15) },
                new() { new Point(0.35, 0.30), new Point(0.45, 0.35) },
                new() { new Point(0.12, 0.38), new Point(0.22, 0.40), new Point(0.32, 0.36) }
            }
        });
        await vm.SaveAsync();

        var loaded = Assert.Single(await ReadBack(path));
        Assert.Equal(AnnotationType.Ink, loaded.Type);
        Assert.Equal(3, loaded.InkStrokes.Count);
        Assert.Equal(new[] { 3, 2, 3 }, loaded.InkStrokes.Select(s => s.Count));
    }

    /// <summary>
    /// The width a stroke was drawn at is part of the drawing. It was never written, so a
    /// heavy pen line came back as a hairline.
    /// </summary>
    [Fact]
    public async Task TestStrokeThicknessSurvivesTheRoundTrip()
    {
        string path = NewPdf("thickness.pdf", 1);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Rectangle,
            X = 0.2, Y = 0.2, Width = 0.3, Height = 0.2,
            ColorHex = "#2979FF",
            Opacity = 1.0,
            StrokeThickness = 6.0
        });
        await vm.SaveAsync();

        var loaded = Assert.Single(await ReadBack(path));
        Assert.Equal(6.0, loaded.StrokeThickness, 1);
    }

    // ---------------------------------------------------------------------------------
    // Editing an annotation is work too.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Rewriting a comment changed only the object in memory. No asterisk, Save greyed out,
    /// no question on the way out - the rewritten comment was dropped without a word.
    /// </summary>
    [Fact]
    public async Task TestEditingAnAnnotationCountsAsUnsavedWork()
    {
        string path = NewPdf("edit.pdf", 1);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(Highlight(1, "first draft"));
        await vm.SaveAsync();

        Assert.False(vm.HasUnsavedChanges);

        var existing = vm.AllAnnotations.Single();
        existing.Contents = "second draft";
        vm.NoteAnnotationEdited(existing);

        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CanSave());
        Assert.StartsWith("*", vm.WindowTitle);

        await vm.SaveAsync();

        var loaded = Assert.Single(await ReadBack(path));
        Assert.Equal("second draft", loaded.Contents);
    }

    // ---------------------------------------------------------------------------------
    // Annotations belonging to the document, not to this application.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Clearing the page before writing must take only what this viewer manages. A link is
    /// part of the document; losing every hyperlink because someone added a highlight would
    /// be a far worse bug than the one being fixed.
    /// </summary>
    [Fact]
    public async Task TestSavingLeavesTheDocumentsOwnLinkAnnotationsAlone()
    {
        string path = Path.Combine(_testDir, "withlink.pdf");
        TestPdfBuilder.CreatePdfWithLinkAnnotation(path);

        string before = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        Assert.Contains("/Link", before);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(Highlight());
        await vm.SaveAsync();

        string after = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        Assert.Contains("/Link", after);
        Assert.Contains("/Highlight", after);
    }

    // ---------------------------------------------------------------------------------
    // Rotated pages.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Annotation rectangles are in unrotated page space. Loading corrected for that and
    /// saving did not, so on a page carrying /Rotate 90 the two disagreed by the ratio of the
    /// page's sides - and because the error is a multiplication, it compounded: an annotation
    /// walked further down and to the right on every save until it left the page entirely.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TestAnnotationsStayPutOnARotatedPageAcrossRepeatedSaves(int rotation)
    {
        string path = Path.Combine(_testDir, $"rotate_{rotation}.pdf");
        TestPdfBuilder.CreateRotatedPdf(path, rotation);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.AddAnnotation(Highlight());
        await vm.SaveAsync();

        var first = Assert.Single(await ReadBack(path));

        // Three more open-and-save cycles. Any mismatch between the two coordinate
        // conversions shows up here as drift, not as a one-off rounding difference.
        for (int round = 0; round < 3; round++)
        {
            var reopened = new MainViewModel();
            await reopened.LoadDocumentAsync(path);

            var current = Assert.Single(reopened.AllAnnotations);
            current.Contents = $"round {round}";
            reopened.NoteAnnotationEdited(current);
            await reopened.SaveAsync();
        }

        var last = Assert.Single(await ReadBack(path));

        Assert.Equal(first.X, last.X, 3);
        Assert.Equal(first.Y, last.Y, 3);
        Assert.Equal(first.Width, last.Width, 3);
        Assert.Equal(first.Height, last.Height, 3);
    }

    /// <summary>
    /// A highlight made over a word has to stay over that word. Text positions and annotation
    /// rectangles are read and written through separate conversions, so this checks the two
    /// agree - on a rotated page as well as a plain one, where they most easily drift apart.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task TestAHighlightStaysOverTheWordItWasMadeFrom(int rotation)
    {
        string path = Path.Combine(_testDir, $"overtext_{rotation}.pdf");
        TestPdfBuilder.CreateRotatedPdf(path, rotation);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        var page = vm.Pages[0];
        await page.LoadTextSegmentsAsync(vm.DocumentService);
        Assert.NotEmpty(page.TextSegments);

        var word = page.TextSegments[0];
        vm.AddAnnotation(new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Highlight,
            X = word.X,
            Y = word.Y,
            Width = word.Width,
            Height = word.Height,
            ColorHex = "#FFFF00",
            Opacity = 0.4
        });
        await vm.SaveAsync();

        var reopened = new MainViewModel();
        await reopened.LoadDocumentAsync(path);

        var reloadedPage = reopened.Pages[0];
        await reloadedPage.LoadTextSegmentsAsync(reopened.DocumentService);

        var sameWord = reloadedPage.TextSegments[0];
        var annotation = Assert.Single(reopened.AllAnnotations);

        Assert.Equal(sameWord.X, annotation.X, 2);
        Assert.Equal(sameWord.Y, annotation.Y, 2);
        Assert.Equal(sameWord.Width, annotation.Width, 2);
        Assert.Equal(sameWord.Height, annotation.Height, 2);
    }

    // ---------------------------------------------------------------------------------
    // Opacity.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A fully transparent annotation used to load as a fully opaque one, because zero alpha
    /// was read as "no alpha given". Saving then wrote the opacity back as solid, so an
    /// invisible marker turned into a block covering the text underneath it.
    /// </summary>
    [Fact]
    public async Task TestATransparentAnnotationDoesNotComeBackOpaque()
    {
        string path = NewPdf("alpha.pdf", 1);

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        var annot = Highlight();
        annot.Opacity = 0.0;
        vm.AddAnnotation(annot);
        await vm.SaveAsync();

        var loaded = Assert.Single(await ReadBack(path));
        Assert.True(loaded.Opacity < 0.05, $"Expected a transparent annotation, got opacity {loaded.Opacity}.");
    }
}
