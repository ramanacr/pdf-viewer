using System.IO;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Comparison;
using PdfEngine.Pdfium;
using PdfViewer.Core.Comparison;
using PdfViewer.Core.Ocr;
using Xunit;

namespace PdfViewer.Tests;

public class ComparisonAndOcrTests
{
    private static string CreateSamplePdf(string name, string token = "Sample")
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
        TestPdfBuilder.CreateSimplePdf(path, 2, token);
        return path;
    }

    [Fact]
    public async Task TestDocumentComparisonIdentical()
    {
        string pdf1 = CreateSamplePdf("Compare_Doc1.pdf", "IdenticalToken");
        string pdf2 = CreateSamplePdf("Compare_Doc2.pdf", "IdenticalToken");

        using IPdfEngine engine = new PdfiumEngine();
        await using var docA = await engine.OpenDocumentAsync(pdf1);
        await using var docB = await engine.OpenDocumentAsync(pdf2);

        var comparisonService = new PdfComparisonService(engine.Renderer, engine.TextService);
        var result = await comparisonService.CompareDocumentsAsync(docA, docB);

        Assert.Equal(2, result.PageCountA);
        Assert.Equal(2, result.PageCountB);
        Assert.True(result.VisualSimilarityScore >= 0.99);
        Assert.Empty(result.TextDifferences);
    }

    [Fact]
    public async Task TestDocumentComparisonModified()
    {
        string pdf1 = CreateSamplePdf("Diff_DocA.pdf", "VersionAlpha");
        string pdf2 = CreateSamplePdf("Diff_DocB.pdf", "VersionBeta");

        using IPdfEngine engine = new PdfiumEngine();
        await using var docA = await engine.OpenDocumentAsync(pdf1);
        await using var docB = await engine.OpenDocumentAsync(pdf2);

        var comparisonService = new PdfComparisonService(engine.Renderer, engine.TextService);
        var result = await comparisonService.CompareDocumentsAsync(docA, docB);

        Assert.NotEmpty(result.TextDifferences);
        Assert.Contains(result.TextDifferences, d => d.Type == DiffType.Modified);
    }

    [Fact]
    public async Task TestVisualDiffPageGeneration()
    {
        string pdf1 = CreateSamplePdf("Diff_Visual_A.pdf", "Alpha");
        string pdf2 = CreateSamplePdf("Diff_Visual_B.pdf", "Beta");

        using IPdfEngine engine = new PdfiumEngine();
        await using var docA = await engine.OpenDocumentAsync(pdf1);
        await using var docB = await engine.OpenDocumentAsync(pdf2);

        var comparisonService = new PdfComparisonService(engine.Renderer, engine.TextService);
        using var diffPage = await comparisonService.GenerateVisualDiffPageAsync(docA, docB, 1, 96.0);

        Assert.NotNull(diffPage);
        Assert.True(diffPage.WidthPixels > 0);
        Assert.True(diffPage.HeightPixels > 0);
        Assert.False(diffPage.Pixels.IsEmpty);
    }

    [Fact]
    public async Task TestOcrWordExtraction()
    {
        string samplePdf = CreateSamplePdf("Ocr_Test_Doc.pdf", "OcrRecognizedToken");

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var ocrEngine = new DefaultOcrEngine(engine.TextService);
        var pageResult = await ocrEngine.RecognizePageAsync(doc, 1, "en");

        Assert.NotNull(pageResult);
        Assert.Equal(1, pageResult.PageNumber);
        Assert.True(pageResult.Confidence > 0.9);
        Assert.NotEmpty(pageResult.Words);
        Assert.Contains(pageResult.Words, w => w.Text.Contains("OcrRecognizedToken") || w.Text.Contains("Page"));
    }
}
