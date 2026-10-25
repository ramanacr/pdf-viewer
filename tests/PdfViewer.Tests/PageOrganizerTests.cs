using System.IO;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Documents;
using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfViewer.Core.Commands;
using PdfViewer.Core.Session;
using Xunit;

namespace PdfViewer.Tests;

public class PageOrganizerTests
{
    private static string CreateSamplePdf(string name, int pages = 4)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
        TestPdfBuilder.CreateSimplePdf(path, pages, "OrganizerTestToken");
        return path;
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestRotatePage(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Rotate_Test_{engineName}.pdf", 3);
        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var infoBefore = await doc.GetPageInfoAsync(1);
        Assert.Equal(0, infoBefore.RotationDegrees);

        await engine.PageOrganizer.RotatePageAsync(doc, 1, PageRotation.Rotate90);

        var infoAfter = await doc.GetPageInfoAsync(1);
        Assert.Equal(90, infoAfter.RotationDegrees);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestDeletePage(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Delete_Test_{engineName}.pdf", 4);
        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        Assert.Equal(4, doc.PageCount);
        await engine.PageOrganizer.DeletePageAsync(doc, 2);

        // Verify page can still be fetched safely
        var info = await doc.GetPageInfoAsync(1);
        Assert.NotNull(info);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestInsertBlankPage(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Insert_Test_{engineName}.pdf", 2);
        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        await engine.PageOrganizer.InsertBlankPageAsync(doc, 1, 612, 792);

        var page = await doc.GetPageAsync(2);
        Assert.NotNull(page);
        Assert.Equal(2, page.PageNumber);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestExtractPages(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Extract_Source_{engineName}.pdf", 4);
        string extractedPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Extracted_Out_{engineName}.pdf");
        if (File.Exists(extractedPdf)) File.Delete(extractedPdf);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        await engine.PageOrganizer.ExtractPagesAsync(doc, new[] { 1, 3 }, extractedPdf);

        Assert.True(File.Exists(extractedPdf));
        await using var extractedDoc = await engine.OpenDocumentAsync(extractedPdf);
        Assert.Equal(2, extractedDoc.PageCount);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestMergeDocuments(string engineName)
    {
        string pdf1 = CreateSamplePdf($"Merge_Doc1_{engineName}.pdf", 2);
        string pdf2 = CreateSamplePdf($"Merge_Doc2_{engineName}.pdf", 3);
        string mergedOut = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Merged_Result_{engineName}.pdf");
        if (File.Exists(mergedOut)) File.Delete(mergedOut);

        using IPdfEngine engine = TestEngines.Create(engineName);
        double progressVal = 0;
        // Progress<T> reports on the thread pool, so its last report could land after the asserts;
        // this one records each value at once.
        var progress = new SyncProgress(p => progressVal = p);

        await engine.PageOrganizer.MergeDocumentsAsync(new[] { pdf1, pdf2 }, mergedOut, progress);

        Assert.True(File.Exists(mergedOut));
        Assert.True(progressVal >= 1.0);

        await using var mergedDoc = await engine.OpenDocumentAsync(mergedOut);
        Assert.Equal(5, mergedDoc.PageCount);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestSplitDocument(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Split_Source_{engineName}.pdf", 5);
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Split_Out_{engineName}");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        // Split into [2 pages, 2 pages, 1 page]
        await engine.PageOrganizer.SplitDocumentAsync(doc, new[] { 2, 2, 1 }, outDir, "split");

        Assert.True(Directory.Exists(outDir));
        var files = Directory.GetFiles(outDir, "*.pdf");
        Assert.Equal(3, files.Length);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestUndoRedoRotateCommand(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Undo_Rotate_Test_{engineName}.pdf", 2);
        using IPdfEngine engine = TestEngines.Create(engineName);
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);

        // Page operations are a Pro-tier feature and CommandHistory enforces the licence
        // gate, so this exercise needs a gate that actually licenses them.
        var history = new CommandHistory(
            featureGate: new PdfViewer.Core.Licensing.DefaultFeatureGate(PdfViewer.Core.Licensing.LicenseTier.Pro));
        var cmd = new RotatePageCommand(engine.PageOrganizer, 1, PageRotation.Rotate180, PageRotation.Rotate0);

        await history.ExecuteCommandAsync(cmd, session);
        var infoAfterExec = await doc.GetPageInfoAsync(1);
        Assert.Equal(180, infoAfterExec.RotationDegrees);
        Assert.Equal(2, session.Revision);

        await history.UndoAsync(session);
        var infoAfterUndo = await doc.GetPageInfoAsync(1);
        Assert.Equal(0, infoAfterUndo.RotationDegrees);
        Assert.Equal(3, session.Revision);

        await history.RedoAsync(session);
        var infoAfterRedo = await doc.GetPageInfoAsync(1);
        Assert.Equal(180, infoAfterRedo.RotationDegrees);
        Assert.Equal(4, session.Revision);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
