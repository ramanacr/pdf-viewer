using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers "Save Clean Copy". The value of this feature is entirely in whether the output is
/// actually inert, so these tests prove it by re-inspecting what was written rather than by
/// trusting the return value.
/// </summary>
public class SanitizationTests : IDisposable
{
    private readonly string _testDir;

    public SanitizationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfSanitizeTests_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task TestSanitizeRemovesScriptAndLaunchActionsAndProvesTheOutputIsClean()
    {
        string source = TestPdfBuilder.CreateActiveContentPdf(Path_("active.pdf"));
        string cleaned = Path_("active.clean.pdf");

        using IPdfEngine engine = new PdfiumEngine();
        var inspector = new PdfiumSafetyInspector();
        var sanitizer = new PdfiumSanitizer();

        SanitizationResult result;
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            var before = await inspector.InspectAsync(doc);
            Assert.False(before.IsClean);
            Assert.True(before.HasKind(DocumentRiskKind.JavaScript));
            Assert.True(before.HasKind(DocumentRiskKind.LaunchAction));

            result = await sanitizer.SanitizeAsync(doc, cleaned);
        }

        Assert.True(File.Exists(cleaned));
        Assert.True(result.RemovedAnything);
        Assert.Equal(1, result.PageCount);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.DocumentScript);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.LaunchAction);

        // The claim that matters: open what was written and inspect it independently.
        await using var written = await engine.OpenDocumentAsync(cleaned);
        var after = await inspector.InspectAsync(written);

        Assert.True(after.IsClean, "The sanitized copy still reports elevated risk.");
        Assert.False(after.HasKind(DocumentRiskKind.JavaScript));
        Assert.False(after.HasKind(DocumentRiskKind.LaunchAction));

        // Unlinking an annotation from /Annots is not the same as removing it: PDFium's save
        // serializes orphaned objects too, so an earlier version of this left the launch
        // action sitting in the file, unreferenced but fully readable. Inspection cannot
        // catch that - it walks references - so the bytes are checked directly.
        string bytes = File.ReadAllText(cleaned);
        Assert.DoesNotContain("/Launch", bytes);
        Assert.DoesNotContain("calc.exe", bytes);
        Assert.DoesNotContain("/JavaScript", bytes);
        Assert.DoesNotContain("app.alert", bytes);
        Assert.DoesNotContain("/OpenAction", bytes);

        // The ordinary web link survives: this reader never follows a link on its own, so a
        // URI is information, not a capability, and stripping it would damage the document.
        Assert.Contains("example.com", bytes);
    }

    [Fact]
    public async Task TestSanitizeRemovesEmbeddedFilesAndTheirPageAnnotation()
    {
        string source = TestPdfBuilder.CreateAttachmentPdf(Path_("attach.pdf"), "MALICIOUS_PAYLOAD_MARKER");
        string cleaned = Path_("attach.clean.pdf");

        using IPdfEngine engine = new PdfiumEngine();
        var inspector = new PdfiumSafetyInspector();
        var sanitizer = new PdfiumSanitizer();

        SanitizationResult result;
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            var before = await inspector.InspectAsync(doc);
            Assert.True(before.HasKind(DocumentRiskKind.EmbeddedFile));

            result = await sanitizer.SanitizeAsync(doc, cleaned);
        }

        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.EmbeddedFile);

        await using (var written = await engine.OpenDocumentAsync(cleaned))
        {
            var after = await inspector.InspectAsync(written);
            Assert.True(after.IsClean);
            Assert.False(after.HasKind(DocumentRiskKind.EmbeddedFile));
        }

        // Removing the reference is not enough - the payload bytes must not survive into
        // the output, or the attachment is still there for anything that scans the file.
        string writtenBytes = File.ReadAllText(cleaned);
        Assert.DoesNotContain("MALICIOUS_PAYLOAD_MARKER", writtenBytes);
    }

    [Fact]
    public async Task TestSanitizeKeepsThePagesAndTheirText()
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("ordinary.pdf"), 3, "KeepThisToken");
        string cleaned = Path_("ordinary.clean.pdf");

        using IPdfEngine engine = new PdfiumEngine();
        var sanitizer = new PdfiumSanitizer();

        string originalText;
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            originalText = await engine.TextService.ExtractPageTextAsync(doc, 1);
            var result = await sanitizer.SanitizeAsync(doc, cleaned);

            // An ordinary document has nothing to strip; the feature must say so rather
            // than manufacture a change to look useful.
            Assert.False(result.RemovedAnything);
            Assert.Equal(3, result.PageCount);
        }

        await using var written = await engine.OpenDocumentAsync(cleaned);
        Assert.Equal(3, written.PageCount);
        Assert.Contains("KeepThisToken", await engine.TextService.ExtractPageTextAsync(written, 1));
        Assert.Equal(originalText, await engine.TextService.ExtractPageTextAsync(written, 1));

        // And it still renders, which is the other half of "the document still works".
        using var rendered = await engine.Renderer.RenderPageAsync(written, new RenderRequest
        {
            PageNumber = 1,
            Dpi = 96.0,
            Rotation = PageRotation.Rotate0
        });
        Assert.True(rendered.WidthPixels > 0);
        Assert.True(rendered.HeightPixels > 0);
    }

    [Fact]
    public async Task TestSanitizeLeavesTheOriginalUntouched()
    {
        string source = TestPdfBuilder.CreateActiveContentPdf(Path_("original.pdf"));
        byte[] before = File.ReadAllBytes(source);

        using IPdfEngine engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            await new PdfiumSanitizer().SanitizeAsync(doc, Path_("original.clean.pdf"));
        }

        Assert.Equal(before, File.ReadAllBytes(source));

        // The original still reports its active content: sanitizing produced a copy, it did
        // not quietly disarm the file the user opened.
        await using var reopened = await engine.OpenDocumentAsync(source);
        Assert.False((await new PdfiumSafetyInspector().InspectAsync(reopened)).IsClean);
    }

    [Fact]
    public async Task TestSanitizeRefusesToOverwriteTheOriginal()
    {
        string source = TestPdfBuilder.CreateActiveContentPdf(Path_("selfwrite.pdf"));

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(source);

        var ex = await Assert.ThrowsAsync<PdfSanitizationException>(
            async () => await new PdfiumSanitizer().SanitizeAsync(doc, source));

        Assert.Contains("cannot overwrite the original", ex.Message);
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// The output is verified before it is returned. If verification says the copy is still
    /// dirty, the file must be deleted rather than handed to a user who would trust it.
    /// </summary>
    [Fact]
    public async Task TestSanitizeDiscardsTheOutputWhenVerificationFails()
    {
        string source = TestPdfBuilder.CreateActiveContentPdf(Path_("verify.pdf"));
        string cleaned = Path_("verify.clean.pdf");

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(source);

        var sanitizer = new PdfiumSanitizer(new AlwaysDirtyInspector());

        var ex = await Assert.ThrowsAsync<PdfSanitizationException>(
            async () => await sanitizer.SanitizeAsync(doc, cleaned));

        Assert.Contains("still contained active content", ex.Message);
        Assert.False(File.Exists(cleaned), "A copy that failed verification must not be left on disk.");
    }

    [Fact]
    public async Task TestSanitizeReportsWhatTheCopyLoses()
    {
        string source = TestPdfBuilder.CreateSimplePdf(Path_("withbookmarks.pdf"), 4, "Token");

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(source);

        var result = await new PdfiumSanitizer().SanitizeAsync(doc, Path_("withbookmarks.clean.pdf"));

        // The sample builder writes an outline, so the user has to be told it will not
        // survive the rebuild.
        Assert.Contains(result.SideEffects, s => s.Contains("Bookmarks", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Stands in for an inspector that finds the copy still dirty.</summary>
    private sealed class AlwaysDirtyInspector : IPdfSafetyInspector
    {
        public ValueTask<DocumentSafetyReport> InspectAsync(
            PdfEngine.Documents.IPdfDocument document,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new DocumentSafetyReport
            {
                Findings = new[]
                {
                    new DocumentRiskFinding
                    {
                        Kind = DocumentRiskKind.JavaScript,
                        Severity = RiskSeverity.Elevated,
                        Count = 1,
                        Description = "Simulated surviving script."
                    }
                }
            });
        }
    }
}
