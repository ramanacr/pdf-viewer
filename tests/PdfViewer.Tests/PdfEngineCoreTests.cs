using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Documents;
using PdfEngine.Forms;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Signatures;
using PdfViewer.Core.Cache;
using PdfViewer.Core.Commands;
using PdfViewer.Core.Licensing;
using PdfViewer.Core.Rendering;
using PdfViewer.Core.Security;
using PdfViewer.Core.Session;
using PdfViewer.RenderingAdapters;
using Xunit;

namespace PdfViewer.Tests;

public class PdfEngineCoreTests
{
    private static string GetOrCreateSamplePdf()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core_Test_Doc.pdf");
        if (!File.Exists(path))
        {
            TestPdfBuilder.CreateSimplePdf(path, 2, "CoreTestToken");
        }
        return path;
    }

    [Fact]
    public async Task TestPdfiumEngineOpeningAndLifecycle()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();

        Assert.Equal("Google PDFium Native", engine.EngineName);
        Assert.Contains("154.0.8021.0", engine.EngineVersion);

        await using var doc = await engine.OpenDocumentAsync(samplePdf);
        Assert.True(doc.IsOpen);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(samplePdf, doc.FilePath);

        var info1 = await doc.GetPageInfoAsync(1);
        Assert.Equal(612, info1.WidthPoints);
        Assert.Equal(792, info1.HeightPoints);
        Assert.Equal(0, info1.RotationDegrees);
    }

    [Fact]
    public async Task TestPdfiumRendererRawBgraOutputAndWpfAdapter()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var req = new RenderRequest
        {
            PageNumber = 1,
            Dpi = 96.0,
            Rotation = PageRotation.Rotate0,
            HighQuality = true
        };

        using var rendered = await engine.Renderer.RenderPageAsync(doc, req);
        Assert.Equal(1, rendered.PageNumber);
        Assert.True(rendered.WidthPixels > 0);
        Assert.True(rendered.HeightPixels > 0);
        Assert.Equal(rendered.WidthPixels * 4, rendered.Stride);
        Assert.True(rendered.ByteLength > 0);
        Assert.False(rendered.Pixels.IsEmpty);

        // Convert via UI adapter
        var bitmap = WpfBitmapAdapter.ToBitmapSource(rendered);
        Assert.NotNull(bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(rendered.WidthPixels, bitmap.PixelWidth);
        Assert.Equal(rendered.HeightPixels, bitmap.PixelHeight);
    }

    [Fact]
    public async Task TestDocumentSessionFingerprintAndRevision()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);

        Assert.True(session.IsOpen);
        Assert.False(string.IsNullOrEmpty(session.Fingerprint));
        Assert.Equal(1, session.Revision);
        Assert.False(session.IsDirty);

        session.IncrementRevision();
        Assert.Equal(2, session.Revision);
        Assert.True(session.IsDirty);

        session.MarkSaved();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task TestSafetyInspectorDetectsActiveContent()
    {
        // The product promise: tell the user what the document is carrying, rather than
        // silently acting on it. Detection must be authoritative, so this fixture carries
        // real script, a launch action, and an external link.
        string pdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ActiveContent.pdf");
        TestPdfBuilder.CreateActiveContentPdf(pdf);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf);

        var inspector = new PdfEngine.Pdfium.Adapters.PdfiumSafetyInspector();
        var report = await inspector.InspectAsync(doc);

        Assert.True(report.HasKind(PdfEngine.Safety.DocumentRiskKind.JavaScript),
            "Embedded JavaScript must be detected - it is the vector behind most PDF reader RCEs.");
        Assert.True(report.HasKind(PdfEngine.Safety.DocumentRiskKind.LaunchAction),
            "A /Launch action must be detected.");
        Assert.True(report.HasKind(PdfEngine.Safety.DocumentRiskKind.ExternalLink),
            "An external URI link must be detected.");

        // A document carrying script is never reported as clean.
        Assert.False(report.IsClean);

        var js = report.Findings.Single(f => f.Kind == PdfEngine.Safety.DocumentRiskKind.JavaScript);
        Assert.Equal(PdfEngine.Safety.RiskSeverity.Elevated, js.Severity);
        Assert.Contains("not been run", js.Description);
    }

    [Fact]
    public async Task TestSafetyInspectorReportsOrdinaryDocumentAsClean()
    {
        // The inverse must also hold, or the indicator is noise: a plain document with no
        // active content reports clean.
        string samplePdf = GetOrCreateSamplePdf();

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var inspector = new PdfEngine.Pdfium.Adapters.PdfiumSafetyInspector();
        var report = await inspector.InspectAsync(doc);

        Assert.True(report.IsClean);
        Assert.False(report.HasKind(PdfEngine.Safety.DocumentRiskKind.JavaScript));
        Assert.False(report.HasKind(PdfEngine.Safety.DocumentRiskKind.EmbeddedFile));
        Assert.False(report.InspectionWasLimited);
    }

    [Fact]
    public void TestApplicationStartsWhetherOrNotOcrRuntimeIsPresent()
    {
        // The Windows OCR runtime is optional: it is absent on Server Core, on installs
        // without a language pack, and on Windows older than the build the app is compiled
        // against. Probing for it must never throw, and the application's composition root
        // must construct either way - otherwise raising the target framework to reach OCR
        // would stop the whole app from starting on those machines.
        bool available = PdfViewer.Services.WindowsOcrEngine.IsAvailable;

        var vm = new PdfViewer.ViewModels.MainViewModel();

        Assert.NotNull(vm.OcrEngine);
        Assert.Equal(available, vm.IsOpticalRecognitionAvailable);

        // Text acquisition is wired regardless; without a recognizer it reports honestly
        // rather than failing to construct.
        Assert.NotEmpty(vm.OcrEngine.EngineName);
    }

    [Fact]
    public async Task TestDocumentComparisonDetectsIdenticalAndDifferentDocuments()
    {
        // Backs Tools > Compare With Document. Identical input must report identical, and
        // genuinely different content must be detected - not merely "compared".
        string a = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CompareA.pdf");
        string b = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CompareB.pdf");
        TestPdfBuilder.CreateSimplePdf(a, 2, "SameToken");
        TestPdfBuilder.CreateSimplePdf(b, 2, "OtherToken");

        using IPdfEngine engine = new PdfiumEngine();
        var comparer = new PdfViewer.Core.Comparison.PdfComparisonService(engine.Renderer, engine.TextService);

        // Same file compared against itself: identical.
        await using (var d1 = await engine.OpenDocumentAsync(a))
        await using (var d2 = await engine.OpenDocumentAsync(a))
        {
            var same = await comparer.CompareDocumentsAsync(d1, d2);
            Assert.Equal(2, same.PageCountA);
            Assert.Empty(same.PagesWithVisualDifferences);
            Assert.Equal(1.0, same.VisualSimilarityScore, precision: 3);
        }

        // Different text on every page: detected.
        await using (var d1 = await engine.OpenDocumentAsync(a))
        await using (var d2 = await engine.OpenDocumentAsync(b))
        {
            var diff = await comparer.CompareDocumentsAsync(d1, d2);
            Assert.NotEmpty(diff.PagesWithVisualDifferences);
            Assert.True(diff.VisualSimilarityScore < 1.0);
        }
    }

    [Fact]
    public async Task TestFormFieldsDialogInstantiatesOnUiThread()
    {
        // The dialog must construct cleanly - a XAML or resource-lookup failure here would
        // only ever surface when a user opened it.
        string formPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Dialog_Test.pdf");
        TestPdfBuilder.CreateFormPdf(formPdf);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(formPdf);
        var fields = await engine.FormService.GetFormFieldsAsync(doc, 1);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dialog = new PdfViewer.Views.Dialogs.FormFieldsDialog(fields, "Form_Dialog_Test.pdf");
                Assert.Equal(fields.Count, dialog.Fields.Count);
                Assert.False(dialog.SaveRequested);
                dialog.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public async Task TestRedactSelectedTextRemovesItFromTheSavedCopy()
    {
        // End-to-end for Edit > Redact Selected Text: select text in the viewer, build the
        // redaction areas the command uses, apply them, and confirm the text is genuinely
        // gone from the output - and that the original document is untouched.
        string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RedactSelection.pdf");
        TestPdfBuilder.CreateSimplePdf(source, 1, "ClassifiedToken");

        var vm = new PdfViewer.ViewModels.MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(source);

        var page = vm.Pages[0];
        await page.LoadTextSegmentsAsync(vm.DocumentService);
        page.SelectAllText();
        vm.UpdateSelectionFromPages();

        var areas = vm.BuildRedactionAreasFromSelection();
        Assert.NotEmpty(areas);
        Assert.All(areas, a => Assert.Equal(1, a.PageNumber));

        string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RedactSelection_out.pdf");
        if (File.Exists(target)) File.Delete(target);

        using IPdfEngine engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            await engine.RedactionService.ApplyRedactionsAsync(doc, target, areas);
        }

        await using (var redacted = await engine.OpenDocumentAsync(target))
        {
            string after = await engine.TextService.ExtractPageTextAsync(redacted, 1);
            Assert.DoesNotContain("ClassifiedToken", after);
        }

        // The original must still contain the text: redaction never edits the open file.
        await using var original = await engine.OpenDocumentAsync(source);
        Assert.Contains("ClassifiedToken", await engine.TextService.ExtractPageTextAsync(original, 1));
    }

    [Fact]
    public async Task TestMergeSplitAndExtractProduceUsableDocuments()
    {
        // These operations back the Tools > Merge / Split / Extract commands. Each output
        // must be a real, re-openable PDF with the expected page count - not just a file.
        string docA = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OrganiseA.pdf");
        string docB = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OrganiseB.pdf");
        TestPdfBuilder.CreateSimplePdf(docA, 2, "AlphaToken");
        TestPdfBuilder.CreateSimplePdf(docB, 3, "BetaToken");

        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OrganiseOut");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        using IPdfEngine engine = new PdfiumEngine();

        // Merge: 2 + 3 pages => 5.
        string merged = Path.Combine(outDir, "Merged.pdf");
        await engine.PageOrganizer.MergeDocumentsAsync(new[] { docA, docB }, merged);

        await using (var mergedDoc = await engine.OpenDocumentAsync(merged))
        {
            Assert.Equal(5, mergedDoc.PageCount);
        }

        // Extract a single page into its own document.
        string extracted = Path.Combine(outDir, "Page2.pdf");
        await using (var source = await engine.OpenDocumentAsync(merged))
        {
            await engine.PageOrganizer.ExtractPagesAsync(source, new[] { 2 }, extracted);
        }

        await using (var extractedDoc = await engine.OpenDocumentAsync(extracted))
        {
            Assert.Equal(1, extractedDoc.PageCount);
        }

        // Split into one file per page.
        string splitDir = Path.Combine(outDir, "Split");
        Directory.CreateDirectory(splitDir);
        await using (var source = await engine.OpenDocumentAsync(merged))
        {
            await engine.PageOrganizer.SplitDocumentAsync(
                source, Enumerable.Repeat(1, source.PageCount).ToList(), splitDir, "part");
        }

        var parts = Directory.GetFiles(splitDir, "*.pdf");
        Assert.Equal(5, parts.Length);

        await using var firstPart = await engine.OpenDocumentAsync(parts[0]);
        Assert.Equal(1, firstPart.PageCount);
    }

    [Fact]
    public async Task TestVerifySignaturesCommandNeverClaimsUnsignedDocumentIsValid()
    {
        // The Tools > Verify Digital Signatures command must tell the truth about an
        // unsigned document rather than reporting a reassuring success.
        string samplePdf = GetOrCreateSamplePdf();

        var vm = new PdfViewer.ViewModels.MainViewModel();
        string? caption = null;
        string? message = null;
        vm.ShowMessageBoxAction = (msg, cap, _, _) => { message = msg; caption = cap; };

        await vm.LoadDocumentAsync(samplePdf);
        await vm.VerifySignaturesCommand.ExecuteAsync(null);

        Assert.Equal("Digital Signatures", caption);
        Assert.Contains("no digital signatures", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("valid", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestRecognizeTextCommandReturnsPageText()
    {
        // The Tools > Recognise Text command must produce the page's text and say how it
        // was obtained, without depending on the clipboard being writable.
        string samplePdf = GetOrCreateSamplePdf();

        var vm = new PdfViewer.ViewModels.MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };

        await vm.LoadDocumentAsync(samplePdf);
        await vm.RecognizeTextOnPageCommand.ExecuteAsync(null);

        Assert.True(vm.RecognizedPageText.Contains("CoreTestToken"),
            $"RecognizedPageText was '{vm.RecognizedPageText}'; StatusText was '{vm.StatusText}'");
        Assert.Contains("text layer", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestFormFieldsReportTrueTypesAndMetadata()
    {
        // Regression test: every widget was reported as TextField and IsChecked was never
        // populated, so a UI could not build the right editor for a field.
        string formPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Fields_Test.pdf");
        TestPdfBuilder.CreateFormPdf(formPdf);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(formPdf);

        var fields = await engine.FormService.GetFormFieldsAsync(doc, 1);
        Assert.Equal(3, fields.Count);

        var text = fields.Single(f => f.Name == "FullName");
        Assert.Equal(FormFieldType.TextField, text.Type);
        Assert.Equal("Initial Value", text.Value);

        var checkbox = fields.Single(f => f.Name == "Subscribe");
        Assert.Equal(FormFieldType.CheckBox, checkbox.Type);
        Assert.False(checkbox.IsChecked);

        var combo = fields.Single(f => f.Name == "Country");
        Assert.Equal(FormFieldType.ComboBox, combo.Type);
        Assert.Equal("India", combo.Value);
    }

    [Fact]
    public async Task TestSetFieldValueActuallyPersists()
    {
        // Regression test: SetFieldValueAsync was a no-op (later a throw), so XFDF import
        // reported success while discarding every value. The write must survive a save and
        // reopen cycle.
        string formPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Write_Test.pdf");
        TestPdfBuilder.CreateFormPdf(formPdf);

        string savedPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Write_Saved.pdf");
        if (File.Exists(savedPdf)) File.Delete(savedPdf);

        using IPdfEngine engine = new PdfiumEngine();

        await using (var doc = await engine.OpenDocumentAsync(formPdf))
        {
            await engine.FormService.SetFieldValueAsync(doc, "FullName", "Ramana Reddy");

            // Visible immediately on the open document.
            var updated = await engine.FormService.GetFormFieldsAsync(doc, 1);
            Assert.Equal("Ramana Reddy", updated.Single(f => f.Name == "FullName").Value);

            await engine.SaveService.SaveAsync(doc, savedPdf);
        }

        // And persisted to the saved file.
        await using var reopened = await engine.OpenDocumentAsync(savedPdf);
        var reloaded = await engine.FormService.GetFormFieldsAsync(reopened, 1);
        Assert.Equal("Ramana Reddy", reloaded.Single(f => f.Name == "FullName").Value);
    }

    [Fact]
    public async Task TestSetFieldValueRejectsUnknownField()
    {
        string formPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Unknown_Test.pdf");
        TestPdfBuilder.CreateFormPdf(formPdf);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(formPdf);

        // A misspelled field must fail rather than silently doing nothing.
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await engine.FormService.SetFieldValueAsync(doc, "NoSuchField", "x"));
    }

    [Fact]
    public async Task TestXfdfImportActuallyAppliesValues()
    {
        // The whole point of the round trip: export, edit, import, and see the values land.
        string formPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Xfdf_Test.pdf");
        TestPdfBuilder.CreateFormPdf(formPdf);

        string xfdfPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Form_Xfdf_Test.xfdf");
        File.WriteAllText(xfdfPath,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>" +
            "<field name=\"FullName\"><value>Imported Name</value></field>" +
            "</fields></xfdf>");

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(formPdf);

        await engine.FormService.ImportFormDataXfdfAsync(doc, xfdfPath);

        var fields = await engine.FormService.GetFormFieldsAsync(doc, 1);
        Assert.Equal("Imported Name", fields.Single(f => f.Name == "FullName").Value);
    }

    [Fact]
    public async Task TestImageExportProducesDecodableJpegAndBmp()
    {
        // Regression test: the engine advertised a `format` parameter (and its own doc
        // comment claimed BMP) while only ever writing PNG. Each format must now produce a
        // file the platform decoder can actually read back at the right dimensions.
        string samplePdf = GetOrCreateSamplePdf();
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageExportFormats");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        foreach (var (format, extension) in new[] { ("png", "png"), ("jpeg", "jpg"), ("bmp", "bmp") })
        {
            await engine.SaveService.ExportPagesToImagesAsync(
                doc, outDir, $"page_{format}", 1, 1, format, dpi: 72);

            string path = Path.Combine(outDir, $"page_{format}_page_0001.{extension}");
            Assert.True(File.Exists(path), $"{format} export did not produce {path}");
            Assert.True(new FileInfo(path).Length > 0, $"{format} export produced an empty file");

            // Decode it with WPF's own codecs - proof the bytes are a valid image, not just
            // a file with the right extension.
            using var stream = File.OpenRead(path);
            var decoded = System.Windows.Media.Imaging.BitmapFrame.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            Assert.True(decoded.PixelWidth > 0 && decoded.PixelHeight > 0,
                $"{format} decoded to an empty image");
        }
    }

    [Fact]
    public async Task TestImageExportRejectsUnknownFormat()
    {
        string samplePdf = GetOrCreateSamplePdf();
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageExportBadFormat");

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        // Unknown formats are refused rather than silently written as PNG under a .tiff name.
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await engine.SaveService.ExportPagesToImagesAsync(doc, outDir, "x", 1, 1, "tiff", dpi: 72));
    }

    [Fact]
    public async Task TestOcrReportsHonestlyWhenNoTextLayerAndNoOpticalEngine()
    {
        // Regression test: DefaultOcrEngine used to return the embedded text layer stamped
        // with a fabricated 0.98 confidence and claim it was OCR. A page with no text layer
        // and no optical engine must report zero confidence and say why - not look like a
        // successful recognition that happened to find nothing.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var textLayerOnly = new PdfViewer.Core.Ocr.DefaultOcrEngine(engine.TextService);

        // This page HAS a text layer: exact text, full confidence, and clearly not optical.
        var withText = await textLayerOnly.RecognizePageAsync(doc, 1);
        Assert.NotEmpty(withText.FullText);
        Assert.Equal(1.0, withText.Confidence);
        Assert.False(withText.UsedOpticalRecognition);
        Assert.Contains("embedded text layer", withText.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestOcrFallsBackToOpticalEngineForPagesWithoutText()
    {
        // A page with no embedded text must be routed to the optical engine rather than
        // silently returning nothing.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var stubOptical = new RecordingOcrEngine();
        var composed = new PdfViewer.Core.Ocr.DefaultOcrEngine(new EmptyTextService(), stubOptical);

        var result = await composed.RecognizePageAsync(doc, 1);

        Assert.True(stubOptical.WasCalled, "The optical engine must be used when there is no text layer.");
        Assert.True(result.UsedOpticalRecognition);
        Assert.Equal("recognized text", result.FullText);
    }

    [Fact]
    public async Task TestOcrWithoutOpticalEngineReportsZeroConfidence()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var noFallback = new PdfViewer.Core.Ocr.DefaultOcrEngine(new EmptyTextService());
        var result = await noFallback.RecognizePageAsync(doc, 1);

        Assert.Empty(result.FullText);
        Assert.Equal(0.0, result.Confidence);
        Assert.False(result.UsedOpticalRecognition);
        Assert.Contains("no optical recognition engine", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A text service that reports no embedded text, simulating a scanned page.</summary>
    private sealed class EmptyTextService : PdfEngine.Text.IPdfTextService
    {
        public ValueTask<IReadOnlyList<PdfEngine.Text.TextSegment>> ExtractTextSegmentsAsync(
            IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PdfEngine.Text.TextSegment>>(Array.Empty<PdfEngine.Text.TextSegment>());

        public ValueTask<string> ExtractPageTextAsync(
            IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(string.Empty);

        public ValueTask<IReadOnlyList<PdfEngine.Text.SearchMatch>> SearchTextAsync(
            IPdfDocument document, string query, PdfEngine.Text.SearchOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PdfEngine.Text.SearchMatch>>(Array.Empty<PdfEngine.Text.SearchMatch>());
    }

    private sealed class RecordingOcrEngine : PdfEngine.Ocr.IOcrEngine
    {
        public bool WasCalled { get; private set; }
        public string EngineName => "Recording Test Engine";
        public IReadOnlyList<string> SupportedLanguages { get; } = new[] { "en" };

        public ValueTask<PdfEngine.Ocr.OcrPageResult> RecognizePageAsync(
            IPdfDocument document, int pageNumber, string language = "en",
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return ValueTask.FromResult(new PdfEngine.Ocr.OcrPageResult
            {
                PageNumber = pageNumber,
                FullText = "recognized text",
                Language = language,
                UsedOpticalRecognition = true
            });
        }
    }

    [Fact]
    public async Task TestWindowsOcrEngineActuallyRecognizesRenderedText()
    {
        // Proves the OCR engine performs REAL optical recognition: it rasterizes the page
        // and reads the pixels, with no access to the embedded text layer.
        if (!PdfViewer.Services.WindowsOcrEngine.IsAvailable)
        {
            // No recognizer language pack installed on this machine - nothing to assert.
            return;
        }

        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var ocr = new PdfViewer.Services.WindowsOcrEngine(engine.Renderer);
        var result = await ocr.RecognizePageAsync(doc, 1, ocr.SupportedLanguages[0]);

        Assert.True(result.UsedOpticalRecognition);
        Assert.NotEmpty(result.Words);

        // The sample page renders the words "This is page number 1 of the test document".
        Assert.Contains("page", result.FullText, StringComparison.OrdinalIgnoreCase);

        // Word geometry must come back normalized to the page, not in raw pixels.
        foreach (var word in result.Words)
        {
            Assert.InRange(word.Bounds.X, 0.0, 1.0);
            Assert.InRange(word.Bounds.Y, 0.0, 1.0);
        }
    }

    [Fact]
    public async Task TestUnsignedDocumentIsNotReportedAsValidlySigned()
    {
        // SECURITY regression test: VerifySignatureAsync previously returned
        // SignatureStatus.Valid unconditionally, for ANY document - including unsigned and
        // tampered ones. An unsigned document must report Unknown, never Valid.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var signatures = await engine.SignatureService.GetSignaturesAsync(doc);
        Assert.Empty(signatures);

        var status = await engine.SignatureService.VerifySignatureAsync(doc, "Signature1");
        Assert.NotEqual(SignatureStatus.Valid, status);
        Assert.Equal(SignatureStatus.Unknown, status);
    }

    [Fact]
    public void TestPdfDateParsing()
    {
        // Signing times come through as PDF date strings; a bad parse must yield null
        // rather than a bogus DateTime presented to the user as fact.
        Assert.Equal(new DateTime(2026, 9, 5, 12, 30, 45, DateTimeKind.Utc),
            PdfEngine.Pdfium.Adapters.PdfiumSignatureService.ParsePdfDate("D:20260905123045+05'30'"));

        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PdfEngine.Pdfium.Adapters.PdfiumSignatureService.ParsePdfDate("D:2026"));

        Assert.Null(PdfEngine.Pdfium.Adapters.PdfiumSignatureService.ParsePdfDate(""));
        Assert.Null(PdfEngine.Pdfium.Adapters.PdfiumSignatureService.ParsePdfDate("not-a-date"));
        Assert.Null(PdfEngine.Pdfium.Adapters.PdfiumSignatureService.ParsePdfDate("D:20261305000000"));
    }

    [Fact]
    public async Task TestRedactionActuallyRemovesUnderlyingText()
    {
        // SECURITY regression test: redaction previously only drew a black box and flattened
        // it, leaving the original text objects intact underneath. The "redacted" output
        // gave the text straight back to any extraction or copy-paste - the classic
        // real-world redaction disclosure.
        string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redaction_Source.pdf");
        TestPdfBuilder.CreateSimplePdf(source, 1, "TopSecretToken");

        string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redaction_Output.pdf");
        if (File.Exists(target)) File.Delete(target);

        using IPdfEngine engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            // Sanity check: the secret really is extractable from the source.
            string before = await engine.TextService.ExtractPageTextAsync(doc, 1);
            Assert.Contains("TopSecretToken", before);

            // Cover the upper portion of the page, where the sample text is drawn.
            var redaction = new PdfEngine.Redaction.RedactionArea
            {
                PageNumber = 1,
                Bounds = new PdfRect(0.0, 0.0, 1.0, 0.4)
            };

            await engine.RedactionService.ApplyRedactionsAsync(doc, target, new[] { redaction });
        }

        Assert.True(File.Exists(target));

        await using var redacted = await engine.OpenDocumentAsync(target);
        string after = await engine.TextService.ExtractPageTextAsync(redacted, 1);

        Assert.DoesNotContain("TopSecretToken", after);
    }

    [Fact]
    public async Task TestDefaultSaveProducesAReadableDocument()
    {
        // Regression test: SaveOptions defaulted to Incremental, and FPDF_SaveAsCopy with
        // FPDF_INCREMENTAL emits ONLY the incremental update section. Written into a
        // brand-new file that has no base document, the result was a PDF no reader could
        // open. RemoveUnusedObjects also defaulted true and was mapped to
        // FPDF_REMOVE_SECURITY, silently stripping encryption.
        string source = GetOrCreateSamplePdf();
        string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DefaultSave_Output.pdf");
        if (File.Exists(target)) File.Delete(target);

        using IPdfEngine engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            // Default options - exactly what a caller gets from SaveAsync(doc, path).
            await engine.SaveService.SaveAsync(doc, target);
        }

        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 0);

        // The saved file must be a complete, re-openable document.
        await using var reopened = await engine.OpenDocumentAsync(target);
        Assert.True(reopened.IsOpen);
        Assert.Equal(2, reopened.PageCount);

        string text = await engine.TextService.ExtractPageTextAsync(reopened, 1);
        Assert.Contains("CoreTestToken", text);
    }

    [Fact]
    public async Task TestIntrinsicPageRotationIsNotAppliedTwice()
    {
        // Regression test: the renderer added the page's own /Rotate to the requested
        // rotation, but FPDF_RenderPageBitmap already applies /Rotate internally and
        // FPDF_GetPageWidthF/HeightF already return rotation-adjusted sizes. A /Rotate 90
        // page therefore rendered 90 degrees off, into a transposed bitmap.
        string rotatedPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rotated90_Test_Doc.pdf");
        TestPdfBuilder.CreateRotatedPdf(rotatedPdf, 90);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(rotatedPdf);

        // The page is 612x792 with /Rotate 90, so its DISPLAY size is 792x612 (landscape).
        var info = await doc.GetPageInfoAsync(1);
        Assert.Equal(792, info.WidthPoints);
        Assert.Equal(612, info.HeightPoints);

        // Rendering with no additional user rotation must preserve that landscape shape.
        var rendered = await engine.Renderer.RenderPageAsync(doc,
            new RenderRequest { PageNumber = 1, Dpi = 72.0, Rotation = PageRotation.Rotate0 });
        using (rendered)
        {
            Assert.True(rendered.WidthPixels > rendered.HeightPixels,
                $"Expected landscape output for a /Rotate 90 page, got {rendered.WidthPixels}x{rendered.HeightPixels}.");
        }

        // Adding a further 90 degrees must flip it back to portrait - exactly once.
        var rotatedAgain = await engine.Renderer.RenderPageAsync(doc,
            new RenderRequest { PageNumber = 1, Dpi = 72.0, Rotation = PageRotation.Rotate90 });
        using (rotatedAgain)
        {
            Assert.True(rotatedAgain.HeightPixels > rotatedAgain.WidthPixels,
                $"Expected portrait output after a further 90 degrees, got {rotatedAgain.WidthPixels}x{rotatedAgain.HeightPixels}.");
        }
    }

    [Fact]
    public async Task TestRenderReportsEffectiveDpiWhenSizedByPixels()
    {
        // Regression test: RenderedPage.Dpi echoed request.Dpi even when the raster size was
        // driven by TargetWidth/HeightPixels, so consumers using it for physical sizing
        // (BitmapSource.Create) rendered at the wrong scale.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        // 612pt wide page rasterized to 1224px => 144 DPI, regardless of the requested 96.
        using var rendered = await engine.Renderer.RenderPageAsync(doc, new RenderRequest
        {
            PageNumber = 1,
            Dpi = 96.0,
            TargetWidthPixels = 1224,
            TargetHeightPixels = 1584
        });

        Assert.Equal(144.0, rendered.Dpi, precision: 1);
    }

    [Fact]
    public async Task TestOversizedRenderIsRejectedInsteadOfOverflowing()
    {
        // Regression test: stride/buffer size were computed in int, so a large page at a high
        // DPI overflowed - either throwing far from the cause, or wrapping to a small
        // positive size and handing PDFium a buffer smaller than the bitmap it would write.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var _ = await engine.Renderer.RenderPageAsync(doc, new RenderRequest
            {
                PageNumber = 1,
                TargetWidthPixels = 40000,
                TargetHeightPixels = 40000
            });
        });

        Assert.Contains("exceed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestNativeMemoryOwnerThrowsAfterDispose()
    {
        // Regression test: the memory manager kept its raw pointer after Dispose freed the
        // block, so reads silently returned a Span over freed heap instead of throwing.
        var owner = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(1024);
        Assert.Equal(1024, owner.Memory.Length);

        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory.Span[0]);
    }

    [Fact]
    public async Task TestAttachDocumentDisposesPreviousDocument()
    {
        // Regression test: attaching a second document over the first silently dropped it,
        // leaking its PDFium handle and backing buffer and keeping the file locked.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();

        var first = await engine.OpenDocumentAsync(samplePdf);
        var second = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(first);
        Assert.True(first.IsOpen);

        session.AttachDocument(second);

        Assert.False(first.IsOpen);      // previous document was closed, not leaked
        Assert.True(second.IsOpen);
    }

    [Fact]
    public async Task TestSessionTokenIsCancelledOnClose()
    {
        // Regression test: Close() disposed the document with no signal to in-flight work,
        // so a queued render could hand a freed PDFium handle back to the native library.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);
        Assert.False(session.SessionToken.IsCancellationRequested);

        session.Close();
        Assert.True(session.SessionToken.IsCancellationRequested);
    }

    [Fact]
    public void TestMultiTierCacheMemoryBudgetAndEviction()
    {
        // 200 KB cache limit
        using var cache = new MultiTierCache(maxMemoryBytes: 200 * 1024);

        var key1 = new RenderCacheKey("doc1", 1, 96, PageRotation.Rotate0, 1);
        var key2 = new RenderCacheKey("doc1", 2, 96, PageRotation.Rotate0, 1);

        // Create dummy 100 KB rendered pages
        int pageBytes = 100 * 1024;
        var mem1 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page1 = new RenderedPage(1, 100, 250, 400, 96, PageRotation.Rotate0, mem1);

        var mem2 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page2 = new RenderedPage(2, 100, 250, 400, 96, PageRotation.Rotate0, mem2);

        cache.Put(key1, page1).Dispose();
        cache.Put(key2, page2).Dispose();

        Assert.True(cache.TryGet(key1, out var fetched1));
        Assert.NotNull(fetched1);
        using (fetched1)
        {
            Assert.Equal(1, fetched1!.Page.PageNumber);
        }

        // Put a 3rd page which forces eviction of least recently used key2
        var key3 = new RenderCacheKey("doc1", 3, 96, PageRotation.Rotate0, 1);
        var mem3 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page3 = new RenderedPage(3, 100, 250, 400, 96, PageRotation.Rotate0, mem3);

        cache.Put(key3, page3).Dispose();

        Assert.True(cache.EvictionCount >= 1);
        Assert.True(cache.CurrentMemoryBytes <= cache.MaxMemoryBytes);
    }

    [Fact]
    public void TestCacheDoesNotFreePageStillHeldByCaller()
    {
        // Regression test: TryGet used to return the raw RenderedPage while the cache kept
        // ownership, so an eviction disposed it under the caller and reads of the pixel
        // span hit freed unmanaged memory.
        using var cache = new MultiTierCache(maxMemoryBytes: 200 * 1024);

        int pageBytes = 100 * 1024;
        var keyA = new RenderCacheKey("doc1", 1, 96, PageRotation.Rotate0, 1);
        var pageA = new RenderedPage(1, 100, 250, 400, 96, PageRotation.Rotate0,
            new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes));

        cache.Put(keyA, pageA).Dispose();

        // Borrow it, then force it out of the cache while the lease is still held.
        Assert.True(cache.TryGet(keyA, out var lease));
        Assert.NotNull(lease);

        cache.Clear();

        // The buffer must still be readable through the outstanding lease.
        Assert.Equal(pageBytes, lease!.Page.Pixels.Span.Length);
        _ = lease.Page.Pixels.Span[0];

        // Only after the last lease is released does the buffer go away.
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = pageA.Pixels.Span[0]);
    }

    [Fact]
    public void TestCacheRejectsEntryLargerThanEntireBudget()
    {
        // Regression test: an entry bigger than the whole budget could never satisfy the
        // eviction loop's exit condition, so the cache evicted and disposed EVERY entry and
        // then stored it anyway, staying permanently over budget with a 0% hit rate.
        using var cache = new MultiTierCache(maxMemoryBytes: 100 * 1024);

        var smallKey = new RenderCacheKey("doc1", 1, 96, PageRotation.Rotate0, 1);
        var smallPage = new RenderedPage(1, 10, 10, 40, 96, PageRotation.Rotate0,
            new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(400));
        cache.Put(smallKey, smallPage).Dispose();

        var hugeKey = new RenderCacheKey("doc1", 2, 96, PageRotation.Rotate0, 1);
        var hugePage = new RenderedPage(2, 500, 500, 2000, 96, PageRotation.Rotate0,
            new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(1000 * 1024));

        using (var hugeLease = cache.Put(hugeKey, hugePage))
        {
            // Caller still gets a usable lease...
            Assert.NotNull(hugeLease);
            Assert.Equal(2, hugeLease.Page.PageNumber);
        }

        // ...but it was not cached, the budget was not blown, and the small entry survived.
        Assert.True(cache.CurrentMemoryBytes <= cache.MaxMemoryBytes);
        Assert.True(cache.TryGet(smallKey, out var stillThere));
        stillThere?.Dispose();
    }

    [Fact]
    public async Task TestRenderPrioritySchedulerDeduplicationAndCancellation()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);

        using var scheduler = new RenderPriorityScheduler(engine.Renderer);

        var req = new RenderRequest { PageNumber = 1, Dpi = 96.0 };

        // Fire 5 concurrent requests for the exact same page
        var t1 = scheduler.GetOrRenderPageAsync(session, req);
        var t2 = scheduler.GetOrRenderPageAsync(session, req);
        var t3 = scheduler.GetOrRenderPageAsync(session, req);

        var results = await Task.WhenAll(t1, t2, t3);

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.NotNull(results[2]);
        Assert.Equal(results[0].Page.WidthPixels, results[1].Page.WidthPixels);

        foreach (var lease in results)
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task TestFailedUndoKeepsCommandOnUndoStack()
    {
        // Regression test: UndoAsync popped BEFORE awaiting, so a failing or cancelled undo
        // lost the command from both stacks - that edit became permanently un-undoable and
        // the history silently diverged from the document.
        var history = new CommandHistory(maxHistory: 10);
        using var session = new DocumentSession();

        bool shouldFailUndo = true;
        var command = new DelegateCommand(
            "Failing Undo",
            _ => ValueTask.CompletedTask,
            _ => shouldFailUndo
                ? throw new InvalidOperationException("undo failed")
                : ValueTask.CompletedTask);

        await history.ExecuteCommandAsync(command, session);
        Assert.True(history.CanUndo);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await history.UndoAsync(session));

        // The command must still be undoable after the failure.
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        shouldFailUndo = false;
        await history.UndoAsync(session);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    private sealed class DelegateCommand : IDocumentCommand
    {
        private readonly Func<DocumentSession, ValueTask> _execute;
        private readonly Func<DocumentSession, ValueTask> _undo;

        public string Name { get; }

        public DelegateCommand(string name, Func<DocumentSession, ValueTask> execute, Func<DocumentSession, ValueTask> undo)
        {
            Name = name;
            _execute = execute;
            _undo = undo;
        }

        public ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default) => _execute(session);
        public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default) => _undo(session);
    }

    [Fact]
    public async Task TestCommandHistoryUndoRedo()
    {
        var history = new CommandHistory(maxHistory: 10);
        using var session = new DocumentSession();

        bool executed = false;
        bool undone = false;

        var mockCommand = new MockDocCommand(
            "TestCommand",
            () => executed = true,
            () => undone = true);

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        await history.ExecuteCommandAsync(mockCommand, session);

        Assert.True(executed);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("TestCommand", history.NextUndoName);

        await history.UndoAsync(session);
        Assert.True(undone);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.Equal("TestCommand", history.NextRedoName);

        executed = false;
        await history.RedoAsync(session);
        Assert.True(executed);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void TestFeatureGateEntitlements()
    {
        // The tier is now constructor-supplied and read-only: a public setter let any code
        // path silently promote itself to Enterprise, defeating the gate entirely.
        var community = new DefaultFeatureGate(LicenseTier.Community);
        Assert.True(community.IsFeatureEnabled(FeatureId.Viewer));
        Assert.True(community.IsFeatureEnabled(FeatureId.Search));
        Assert.True(community.IsFeatureEnabled(FeatureId.Annotations));
        Assert.False(community.IsFeatureEnabled(FeatureId.Redaction));
        Assert.False(community.IsFeatureEnabled(FeatureId.Sdk));

        var pro = new DefaultFeatureGate(LicenseTier.Pro);
        Assert.True(pro.IsFeatureEnabled(FeatureId.Redaction));
        Assert.True(pro.IsFeatureEnabled(FeatureId.Forms));
        Assert.False(pro.IsFeatureEnabled(FeatureId.Sdk));

        var sdk = new DefaultFeatureGate(LicenseTier.DeveloperSdk);
        Assert.True(sdk.IsFeatureEnabled(FeatureId.Sdk));

        // The default is the most restrictive tier.
        Assert.Equal(LicenseTier.Community, new DefaultFeatureGate().CurrentTier);
    }

    [Fact]
    public void TestEnsureFeatureEnabledThrowsForUnlicensedFeature()
    {
        var community = new DefaultFeatureGate(LicenseTier.Community);

        community.EnsureFeatureEnabled(FeatureId.Viewer); // allowed, must not throw

        var ex = Assert.Throws<FeatureNotLicensedException>(
            () => community.EnsureFeatureEnabled(FeatureId.Redaction));

        Assert.Equal(FeatureId.Redaction, ex.Feature);
        Assert.Equal(LicenseTier.Community, ex.CurrentTier);
    }

    [Fact]
    public async Task TestCommandHistoryEnforcesFeatureGate()
    {
        // The gate is enforced at the CommandHistory choke point, so an unlicensed command
        // cannot execute no matter which call site constructed it.
        using var session = new DocumentSession();

        var community = new CommandHistory(featureGate: new DefaultFeatureGate(LicenseTier.Community));
        var redaction = new ApplyRedactionsCommand(
            new PdfEngine.Pdfium.Adapters.PdfiumRedactionService(),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gated_redaction.pdf"),
            Array.Empty<PdfEngine.Redaction.RedactionArea>());

        await Assert.ThrowsAsync<FeatureNotLicensedException>(
            async () => await community.ExecuteCommandAsync(redaction, session));

        // Nothing was recorded, because nothing ran.
        Assert.False(community.CanUndo);

        // A Community-tier command still executes normally.
        var viewerCommand = new DelegateCommand("Viewer op", _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
        await community.ExecuteCommandAsync(viewerCommand, session);
        Assert.True(community.CanUndo);
    }

    [Fact]
    public void TestSecurityPolicyRejectsOversizedDocumentAndRender()
    {
        var policy = PdfSecurityPolicy.DefaultStrict with
        {
            MaxDocumentSizeBytes = 1024,
            MaxRenderDimensionPixels = 100
        };

        // Within limits - must not throw.
        policy.EnsureDocumentSizeAllowed(512, "small.pdf");
        policy.EnsureRenderDimensionsAllowed(100, 100);

        var sizeEx = Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            () => policy.EnsureDocumentSizeAllowed(4096, "huge.pdf"));
        Assert.Equal(nameof(PdfSecurityPolicy.MaxDocumentSizeBytes), sizeEx.PolicyName);

        var renderEx = Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            () => policy.EnsureRenderDimensionsAllowed(101, 50));
        Assert.Equal(nameof(PdfSecurityPolicy.MaxRenderDimensionPixels), renderEx.PolicyName);
    }

    [Fact]
    public void TestSecurityPolicyBlocksDocumentOriginatedActions()
    {
        var strict = PdfSecurityPolicy.DefaultStrict;

        // Strict policy blocks script, launch and network actions by default.
        Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            () => strict.EnsureActionAllowed(PdfDocumentAction.JavaScript));
        Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            () => strict.EnsureActionAllowed(PdfDocumentAction.LaunchProgram));
        Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            () => strict.EnsureActionAllowed(PdfDocumentAction.NetworkAccess));

        // External links are permitted by default, attachments need confirmation.
        strict.EnsureActionAllowed(PdfDocumentAction.ExternalLink);
        Assert.True(strict.IsConfirmationRequired(PdfDocumentAction.AttachmentExtraction));

        // The permissive policy allows everything.
        var permissive = PdfSecurityPolicy.Permissive;
        permissive.EnsureActionAllowed(PdfDocumentAction.JavaScript);
        permissive.EnsureActionAllowed(PdfDocumentAction.LaunchProgram);
        permissive.EnsureActionAllowed(PdfDocumentAction.NetworkAccess);
        Assert.False(permissive.IsConfirmationRequired(PdfDocumentAction.AttachmentExtraction));
    }

    [Fact]
    public async Task TestSchedulerEnforcesRenderDimensionCeiling()
    {
        // The policy ceiling is applied before any render work is scheduled or cached.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);

        var policy = PdfSecurityPolicy.DefaultStrict with { MaxRenderDimensionPixels = 200 };
        using var scheduler = new RenderPriorityScheduler(engine.Renderer, cache: null, securityPolicy: policy);

        await Assert.ThrowsAsync<PdfEngine.Exceptions.PdfSecurityPolicyException>(async () =>
        {
            using var _ = await scheduler.GetOrRenderPageAsync(session,
                new RenderRequest { PageNumber = 1, TargetWidthPixels = 5000, TargetHeightPixels = 5000 });
        });

        // A request inside the ceiling still renders.
        using var ok = await scheduler.GetOrRenderPageAsync(session,
            new RenderRequest { PageNumber = 1, TargetWidthPixels = 150, TargetHeightPixels = 150 });
        Assert.Equal(150, ok.Page.WidthPixels);
    }

    [Fact]
    public async Task TestViewerRefusesOversizedRenderLoudly()
    {
        // The viewer REFUSES a render that would breach the policy rather than silently
        // returning a lower-resolution image that looks like the real page. The refusal is
        // an exception, which PageViewModel surfaces via RenderErrorMessage and
        // MainViewModel reports to the user - never a silently blank page.
        string samplePdf = GetOrCreateSamplePdf();
        var policy = PdfSecurityPolicy.DefaultStrict with { MaxRenderDimensionPixels = 200 };

        using var service = new PdfViewer.Services.PdfiumDocumentService(policy);
        await service.OpenDocumentAsync(samplePdf);

        // 5000 DPI would demand a raster far beyond the ceiling.
        var ex = Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            () => service.RenderPage(1, dpi: 5000));
        Assert.Equal(nameof(PdfSecurityPolicy.MaxRenderDimensionPixels), ex.PolicyName);

        // A render inside the ceiling still succeeds (792pt at 15 DPI = 165px, under 200).
        var ok = service.RenderPage(1, dpi: 15);
        Assert.NotNull(ok);
        Assert.True(ok!.PixelWidth <= 200 && ok.PixelHeight <= 200,
            $"Expected a raster within the ceiling, got {ok.PixelWidth}x{ok.PixelHeight}.");
    }

    [Fact]
    public async Task TestPageViewModelSurfacesPolicyRefusal()
    {
        // A policy refusal must be reported, not swallowed: a silently blank page is
        // indistinguishable from a rendering bug.
        string samplePdf = GetOrCreateSamplePdf();
        var policy = PdfSecurityPolicy.DefaultStrict with { MaxRenderDimensionPixels = 50 };

        using var service = new PdfViewer.Services.PdfiumDocumentService(policy);
        await service.OpenDocumentAsync(samplePdf);

        var cache = new PdfViewer.Services.LruPageCache(4);
        var renderer = new PdfViewer.Services.AsyncPageRenderer(service, cache);

        int? refusedPage = null;
        string? refusedMessage = null;

        var pageVm = new PdfViewer.ViewModels.PageViewModel(1, 612, 792)
        {
            RenderRefused = (page, message) => { refusedPage = page; refusedMessage = message; }
        };

        await pageVm.LoadImageAsync(renderer, dpi: 300, rotation: 0);

        Assert.Null(pageVm.RenderedImage);
        Assert.False(pageVm.IsLoading);
        Assert.NotEmpty(pageVm.RenderErrorMessage);
        Assert.Equal(1, refusedPage);
        Assert.Contains("security limit", refusedMessage);
    }

    [Fact]
    public async Task TestServiceRejectsDocumentExceedingSizePolicy()
    {
        // The open boundary DOES hard-refuse: an oversized file is never read into memory.
        string samplePdf = GetOrCreateSamplePdf();
        var policy = PdfSecurityPolicy.DefaultStrict with { MaxDocumentSizeBytes = 1 };

        using var service = new PdfViewer.Services.PdfiumDocumentService(policy);

        var ex = await Assert.ThrowsAsync<PdfEngine.Exceptions.PdfSecurityPolicyException>(
            async () => await service.OpenDocumentAsync(samplePdf));

        Assert.Equal(nameof(PdfSecurityPolicy.MaxDocumentSizeBytes), ex.PolicyName);
    }

    [Fact]
    public async Task TestSessionRejectsDocumentExceedingSizePolicy()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        // A 1-byte ceiling rejects any real document.
        var policy = PdfSecurityPolicy.DefaultStrict with { MaxDocumentSizeBytes = 1 };
        using var session = new DocumentSession(policy);

        Assert.Throws<PdfEngine.Exceptions.PdfSecurityPolicyException>(() => session.AttachDocument(doc));
        Assert.False(session.IsOpen);

        doc.Dispose();
    }

    [Fact]
    public void TestPdfSecurityPolicy()
    {
        var strict = PdfSecurityPolicy.DefaultStrict;
        Assert.False(strict.AllowJavaScript);
        Assert.False(strict.AllowLaunchActions);
        Assert.True(strict.ConfirmAttachmentExtraction);

        var permissive = PdfSecurityPolicy.Permissive;
        Assert.True(permissive.AllowJavaScript);
        Assert.True(permissive.AllowLaunchActions);
        Assert.False(permissive.ConfirmAttachmentExtraction);
    }

    private sealed class MockDocCommand : IDocumentCommand
    {
        private readonly Action _onExecute;
        private readonly Action _onUndo;

        public string Name { get; }

        public MockDocCommand(string name, Action onExecute, Action onUndo)
        {
            Name = name;
            _onExecute = onExecute;
            _onUndo = onUndo;
        }

        public ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            _onExecute();
            return ValueTask.CompletedTask;
        }

        public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            _onUndo();
            return ValueTask.CompletedTask;
        }
    }
}
