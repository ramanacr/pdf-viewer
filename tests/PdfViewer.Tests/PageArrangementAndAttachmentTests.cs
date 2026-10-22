using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Exceptions;
using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using PdfEngine.Vector.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers Organize Pages and Embedded Files. Both write or read real files, so the assertions
/// are made against what ends up on disk rather than against the call returning without
/// throwing.
/// </summary>
public class PageArrangementAndAttachmentTests : IDisposable
{
    private readonly string _testDir;

    public PageArrangementAndAttachmentTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfArrangeTests_" + Guid.NewGuid().ToString("N"));
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

    private string Path_(string name) => Path.Combine(_testDir, name);

    /// <summary>The embedded-files service of the engine the test runs on.</summary>
    private static IPdfAttachmentService Attachments(string engineName) => engineName switch
    {
#if PDFIUM_RENDERER
        TestEngines.Pdfium => new PdfEngine.Pdfium.Adapters.PdfiumAttachmentService(),
#endif
        _ => new PdfNativeAttachmentService(),
    };

    /// <summary>
    /// Reorder, drop and rotate in one pass, then prove it by reading the output back: page
    /// text identifies which source page landed where.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestArrangePagesReordersDropsAndRotatesInOnePass(string engineName)
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("arrange.pdf"), 4, "Tok");
        string target = Path_("arranged.pdf");

        using IPdfEngine engine = TestEngines.Create(engineName);

        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            // Keep 3, 1 and 4 in that order; drop 2. Turn the first one on its side.
            await engine.PageOrganizer.ArrangePagesAsync(doc, new[]
            {
                new PageArrangementEntry(3, PageRotation.Rotate90),
                new PageArrangementEntry(1),
                new PageArrangementEntry(4, PageRotation.Rotate180)
            }, target);
        }

        await using var arranged = await engine.OpenDocumentAsync(target);
        Assert.Equal(3, arranged.PageCount);

        // The builder writes the page number into each page's text, so this proves the
        // ordering rather than just the count.
        Assert.Contains("3", await engine.TextService.ExtractPageTextAsync(arranged, 1));
        Assert.Contains("1", await engine.TextService.ExtractPageTextAsync(arranged, 2));
        Assert.Contains("4", await engine.TextService.ExtractPageTextAsync(arranged, 3));

        var first = await arranged.GetPageInfoAsync(1);
        var second = await arranged.GetPageInfoAsync(2);
        var third = await arranged.GetPageInfoAsync(3);

        Assert.Equal(90, first.RotationDegrees);
        Assert.Equal(0, second.RotationDegrees);
        Assert.Equal(180, third.RotationDegrees);

        // A rotated page reports swapped dimensions - the rotation is real, not cosmetic.
        Assert.Equal(second.HeightPoints, first.WidthPoints);
        Assert.Equal(second.WidthPoints, first.HeightPoints);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestArrangePagesCanDuplicateAPage(string engineName)
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("dup.pdf"), 2, "Tok");
        string target = Path_("duplicated.pdf");

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            await engine.PageOrganizer.ArrangePagesAsync(doc, new[]
            {
                new PageArrangementEntry(1),
                new PageArrangementEntry(1),
                new PageArrangementEntry(2)
            }, target);
        }

        await using var arranged = await engine.OpenDocumentAsync(target);
        Assert.Equal(3, arranged.PageCount);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestArrangePagesLeavesTheOriginalUntouched(string engineName)
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("untouched.pdf"), 3, "Tok");
        byte[] before = File.ReadAllBytes(source);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            await engine.PageOrganizer.ArrangePagesAsync(
                doc, new[] { new PageArrangementEntry(2) }, Path_("one_page.pdf"));
        }

        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestArrangePagesRejectsBadInput(string engineName)
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("reject.pdf"), 2, "Tok");

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(source);

        // An empty arrangement would produce a file no reader can open.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await engine.PageOrganizer.ArrangePagesAsync(
                doc, Array.Empty<PageArrangementEntry>(), Path_("empty.pdf")));

        // A page that does not exist must be caught before anything is written.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await engine.PageOrganizer.ArrangePagesAsync(
                doc, new[] { new PageArrangementEntry(99) }, Path_("oob.pdf")));

        // Writing over the source would destroy the thing being read.
        await Assert.ThrowsAsync<PdfException>(async () =>
            await engine.PageOrganizer.ArrangePagesAsync(
                doc, new[] { new PageArrangementEntry(1) }, source));

        Assert.False(File.Exists(Path_("empty.pdf")));
        Assert.False(File.Exists(Path_("oob.pdf")));
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestAttachmentsAreListedAndExtractedByteForByte(string engineName)
    {
        const string payload = "the exact bytes that were embedded";
        string source = TestPdfBuilder.CreateAttachmentPdf(Path_("carrier.pdf"), payload);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(source);

        var service = Attachments(engineName);
        var files = await service.GetAttachmentsAsync(doc);

        var file = Assert.Single(files);
        Assert.Equal("payload.txt", file.Name);
        Assert.False(file.HasExecutableExtension);
        Assert.True(file.SizeBytes > 0);

        string extracted = Path_("extracted.txt");
        long written = await service.ExtractAttachmentAsync(doc, file.Index, extracted);

        Assert.True(File.Exists(extracted));
        Assert.Equal(written, new FileInfo(extracted).Length);
        Assert.Contains(payload, File.ReadAllText(extracted));
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestAttachmentExtractionRejectsAnIndexThatDoesNotExist(string engineName)
    {
        string source = TestPdfBuilder.CreateAttachmentPdf(Path_("carrier2.pdf"));

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(source);

        var service = Attachments(engineName);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.ExtractAttachmentAsync(doc, 7, Path_("nope.bin")));

        Assert.False(File.Exists(Path_("nope.bin")));
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestOrdinaryDocumentReportsNoAttachments(string engineName)
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("plain.pdf"), 1, "Tok");

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(source);

        Assert.Empty(await Attachments(engineName).GetAttachmentsAsync(doc));
    }

    /// <summary>
    /// The executable warning drives whether the user is asked before extracting, so the
    /// name-matching has to survive the tricks used to make a payload look harmless.
    /// </summary>
    [Theory]
    [InlineData("report.pdf", false)]
    [InlineData("notes.txt", false)]
    [InlineData("photo.jpeg", false)]
    [InlineData("invoice.exe", true)]
    [InlineData("invoice.pdf.exe", true)]
    [InlineData("setup.MSI", true)]
    [InlineData("script.ps1", true)]
    [InlineData("macro.docm", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("payload.exe . ", true)]
    [InlineData("", false)]
    public void TestExecutableAttachmentNamesAreRecognised(string name, bool expected)
    {
        Assert.Equal(expected, PdfNativeAttachmentService.IsExecutableName(name));
#if PDFIUM_RENDERER
        Assert.Equal(expected, PdfEngine.Pdfium.Adapters.PdfiumAttachmentService.IsExecutableName(name));
#endif
    }

    /// <summary>
    /// The suggested save name comes from the document, so it is attacker-controlled. It must
    /// never be able to steer the save dialog out of the folder the user picked.
    /// </summary>
    [Theory]
    [InlineData("payload.txt", "payload.txt")]
    [InlineData(@"..\..\Startup\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData("has:invalid|chars?.txt", "has_invalid_chars_.txt")]
    [InlineData("...", "attachment")]
    [InlineData("   ", "attachment")]
    public void TestSuggestedAttachmentNamesCannotEscapeTheChosenFolder(string given, string expected)
    {
        string suggestion = PdfViewer.ViewModels.MainViewModel.SanitizeSuggestedFileName(given);

        Assert.Equal(expected, suggestion);
        Assert.DoesNotContain('/', suggestion);
        Assert.DoesNotContain('\\', suggestion);
        Assert.Equal(suggestion, Path.GetFileName(suggestion));
    }
}
