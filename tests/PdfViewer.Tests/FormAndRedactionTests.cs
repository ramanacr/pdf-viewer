using System.IO;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Forms;
using PdfEngine.Geometry;
using PdfEngine.Redaction;
using PdfEngine.Signatures;
using Xunit;

namespace PdfViewer.Tests;

public class FormAndRedactionTests
{
    private static string CreateSamplePdf(string name)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
        TestPdfBuilder.CreateSimplePdf(path, 3, "FormRedactToken");
        return path;
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestPendingRedactionRegistration(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Redact_Reg_Doc_{engineName}.pdf");
        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var area = new RedactionArea
        {
            PageNumber = 1,
            Bounds = new PdfRect(0.1, 0.1, 0.5, 0.2),
            OverlayText = "CONFIDENTIAL",
            FillColor = "#000000"
        };

        await engine.RedactionService.AddPendingRedactionAsync(doc, area);

        var pending = await engine.RedactionService.GetPendingRedactionsAsync(doc);
        Assert.Single(pending);
        Assert.Equal("CONFIDENTIAL", pending[0].OverlayText);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestApplyPermanentRedaction(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Redact_Apply_Doc_{engineName}.pdf");
        string redactedOut = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Redacted_Result_{engineName}.pdf");
        if (File.Exists(redactedOut)) File.Delete(redactedOut);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var redactions = new[]
        {
            new RedactionArea
            {
                PageNumber = 1,
                Bounds = new PdfRect(0.2, 0.2, 0.4, 0.1),
                OverlayText = "[REDACTED]"
            }
        };

        await engine.RedactionService.ApplyRedactionsAsync(doc, redactedOut, redactions);

        Assert.True(File.Exists(redactedOut));
        await using var redactedDoc = await engine.OpenDocumentAsync(redactedOut);
        Assert.Equal(doc.PageCount, redactedDoc.PageCount);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestFormFieldsDiscoveryAndExportXfdf(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Form_Test_Doc_{engineName}.pdf");
        string xfdfOut = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"FormData_Export_{engineName}.xfdf");
        if (File.Exists(xfdfOut)) File.Delete(xfdfOut);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var fields = await engine.FormService.GetFormFieldsAsync(doc, 1);
        Assert.NotNull(fields);

        await engine.FormService.ExportFormDataXfdfAsync(doc, xfdfOut);
        Assert.True(File.Exists(xfdfOut));

        string xfdfText = await File.ReadAllTextAsync(xfdfOut);
        Assert.Contains("<xfdf", xfdfText);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestFlattenFormFields(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Form_Flatten_Source_{engineName}.pdf");
        string flattenedOut = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Form_Flattened_{engineName}.pdf");
        if (File.Exists(flattenedOut)) File.Delete(flattenedOut);

        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        await engine.FormService.FlattenFormFieldsAsync(doc, flattenedOut);

        Assert.True(File.Exists(flattenedOut));
        await using var fltDoc = await engine.OpenDocumentAsync(flattenedOut);
        Assert.Equal(doc.PageCount, fltDoc.PageCount);
    }

    [Theory]
    [MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task TestSignaturesDiscovery(string engineName)
    {
        string samplePdf = CreateSamplePdf($"Sig_Discovery_Doc_{engineName}.pdf");
        using IPdfEngine engine = TestEngines.Create(engineName);
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var sigs = await engine.SignatureService.GetSignaturesAsync(doc);
        Assert.NotNull(sigs);
    }
}
