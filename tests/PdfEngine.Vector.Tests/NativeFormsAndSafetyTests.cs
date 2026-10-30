using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Forms;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Safety;
using PdfEngine.Signatures;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Oracle;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// M10: forms, document safety, Save Clean Copy, signatures and redaction on the engine's native
/// services. PDFium's adapters (PdfiumFormService, PdfiumSafetyInspector, PdfiumSanitizer,
/// PdfiumSignatureService) are the oracle: each native result is checked against what PDFium
/// reports for the same file, and PDFium reads back what the native services wrote.
/// </summary>
public class NativeFormsAndSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NativeForms_" + Guid.NewGuid().ToString("N"));

    public NativeFormsAndSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Save(byte[] bytes, string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<List<FormFieldModel>> PdfiumFields(string path)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(path);
        var all = new List<FormFieldModel>();
        for (int p = 1; p <= doc.PageCount; p++) all.AddRange(await engine.FormService.GetFormFieldsAsync(doc, p));
        return all;
    }

    // ------------------------------------------------------------------ forms

    [Fact]
    public async Task Fields_AgreeWithPdfiumFormService()
    {
        string path = Save(FormPdfFixture.Build(), "form.pdf");
        var pdfium = await PdfiumFields(path);
        await using var doc = await PdfEditableDocument.OpenAsync(path);
        var native = PdfFormService.ReadAllFields(doc.Vector);

        // One entry per widget, in /Annots order.
        Assert.Equal(pdfium.Count, native.Count);
        for (int i = 0; i < native.Count; i++)
        {
            var p = pdfium[i];
            var n = native[i];
            Assert.Equal(p.PageNumber, n.PageNumber);
            Assert.Equal(p.Bounds.X, n.Bounds.X, 3);
            Assert.Equal(p.Bounds.Y, n.Bounds.Y, 3);
            Assert.Equal(p.Bounds.Width, n.Bounds.Width, 3);
            Assert.Equal(p.Bounds.Height, n.Bounds.Height, 3);
            // PDFium reads the widget's own /T and /V; the engine gives full names and inherited values.
            if (p.Name.Length > 0) Assert.True(n.Name == p.Name || n.Name.EndsWith("." + p.Name, StringComparison.Ordinal), $"{n.Name} / {p.Name}");
            if (p.Value.Length > 0) Assert.Equal(p.Value, n.Value);
            if (p.Type is FormFieldType.TextField or FormFieldType.Signature) Assert.Equal(p.Type, n.Type);
            if (p.Type == FormFieldType.ComboBox) Assert.True(n.Type is FormFieldType.ComboBox or FormFieldType.ListBox);
            if (p.Type == FormFieldType.CheckBox)
            {
                Assert.True(n.Type is FormFieldType.CheckBox or FormFieldType.RadioButton or FormFieldType.PushButton);
                Assert.Equal(p.IsChecked, n.IsChecked);
            }
        }
        // What PDFium's dictionary-only reading cannot give.
        Assert.Contains(native, f => f.Name == "person.name");
        Assert.Equal(new[] { "Apple", "Banana", "Cherry" }, native.Single(f => f.Name == "fruit").Options);
        Assert.Equal(FormFieldType.RadioButton, native.First(f => f.Name == "colour").Type);
    }

    [Fact]
    public async Task SetValues_RegenerateAppearances_AsAnIncrementalUpdate_PdfiumReadsThem()
    {
        byte[] original = FormPdfFixture.Build();
        await using var doc = await PdfEditableDocument.OpenAsync(original);
        var service = new PdfFormService();
        await service.SetFieldValueAsync(doc, "person.name", "Ada Lovelace");
        await service.SetFieldValueAsync(doc, "zip", "75001");
        await service.SetFieldValueAsync(doc, "agree", "Yes");
        await service.SetFieldValueAsync(doc, "colour", "Blue");
        await service.SetFieldValueAsync(doc, "city", "Rome"); // display text maps to the export value
        PdfFormService.SetValues(doc, new[] { ("fruit", (IReadOnlyList<string>)new[] { "Banana" }) }, validate: true);

        Assert.True(doc.Bytes.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes stay as they were");
        var form = PdfAcroForm.Read(doc.Vector)!;
        Assert.Equal("Ada Lovelace", form["person.name"]!.Value);
        Assert.True(form["agree"]!.IsChecked);
        Assert.Equal("Blue", form["colour"]!.Value);
        Assert.Equal("ROM", form["city"]!.Value);
        Assert.Equal(new[] { "Banana" }, form["fruit"]!.Values);
        foreach (var name in new[] { "person.name", "zip", "city" })
        {
            var w = form[name]!.Widgets[0];
            var ap = doc.Vector.Resolver.Resolve((doc.Vector.Resolver.Resolve(doc.Vector.Resolver.Resolve(w.ObjectNumber) is Objects.PdfDictionary d ? d["AP"] : null) as Objects.PdfDictionary)?["N"]);
            Assert.IsType<Objects.PdfStream>(ap);
        }

        var pdfium = await PdfiumFields(Save(doc.Bytes, "filled.pdf"));
        Assert.Equal("75001", pdfium.Single(f => f.Name == "zip").Value);
        Assert.Equal("Ada Lovelace", pdfium.Single(f => f.Name == "name").Value);
        Assert.True(pdfium.Single(f => f.Name == "agree").IsChecked);
        Assert.Equal("ROM", pdfium.Single(f => f.Name == "city").Value);
    }

    [Fact]
    public async Task SetValue_RefusesReadOnlyAndUnknownFields_AndValidates()
    {
        await using var doc = await PdfEditableDocument.OpenAsync(FormPdfFixture.Build());
        var service = new PdfFormService();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.SetFieldValueAsync(doc, "nope", "x"));
        Assert.Throws<ArgumentException>(() => PdfFormService.SetValues(doc, new[] { ("colour", (IReadOnlyList<string>)new[] { "Green" }) }, validate: true));

        await using var scripted = await PdfEditableDocument.OpenAsync(ScriptedFormFixture.Build());
        Assert.Throws<PdfFieldValidationException>(() => PdfFormService.SetValues(scripted, new[] { ("price", (IReadOnlyList<string>)new[] { "5000" }) }, validate: true));
        Assert.False(scripted.IsModified);
    }

    [Fact]
    public async Task CalculatedFields_AreRecalculated_InCalculationOrder_WithoutRunningScript()
    {
        await using var doc = await PdfEditableDocument.OpenAsync(ScriptedFormFixture.Build());
        var changed = PdfFormService.SetValues(doc, new[]
        {
            ("qty", (IReadOnlyList<string>)new[] { "3" }),
            ("price", (IReadOnlyList<string>)new[] { "2.5" }),
        }, validate: true);
        var form = PdfAcroForm.Read(doc.Vector)!;
        Assert.Contains("total", changed);
        Assert.Equal("7.5", form["total"]!.Value);
    }

    [Fact]
    public async Task Xfdf_And_Fdf_RoundTrip_FieldValues()
    {
        byte[] original = FormPdfFixture.Build();
        await using var filled = await PdfEditableDocument.OpenAsync(original);
        PdfFormService.SetValues(filled, new[]
        {
            ("person.name", (IReadOnlyList<string>)new[] { "Grace Hopper ü" }),
            ("notes", (IReadOnlyList<string>)new[] { "line one\nline two" }),
            ("agree", (IReadOnlyList<string>)new[] { "Yes" }),
            ("colour", (IReadOnlyList<string>)new[] { "Red" }),
            ("city", (IReadOnlyList<string>)new[] { "ROM" }),
            ("fruit", (IReadOnlyList<string>)new[] { "Cherry" }),
        }, validate: false);
        var expected = PdfAcroForm.Read(filled.Vector)!.Fields.ToDictionary(f => f.FullName, f => (f.Value, string.Join("|", f.Values)));

        string xfdf = PdfFormService.ExportXfdf(filled.Vector, "form.pdf");
        Assert.Contains("<field name=\"person\">", xfdf);
        byte[] fdf = PdfFormService.ExportFdf(filled.Vector, "form.pdf");
        Assert.StartsWith("%FDF-1.2", Encoding.ASCII.GetString(fdf));

        foreach (var data in new[] { Encoding.UTF8.GetBytes(xfdf), fdf })
        {
            await using var fresh = await PdfEditableDocument.OpenAsync(original);
            var result = PdfFormService.Import(fresh, data);
            Assert.Empty(result.UnknownFields);
            var actual = PdfAcroForm.Read(fresh.Vector)!.Fields.ToDictionary(f => f.FullName, f => (f.Value, string.Join("|", f.Values)));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Fdf_WrittenByAnotherProducer_IsRead()
    {
        string fdf = "%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [ << /T (person) /Kids [ << /T (name) /V (Nested) >> ] >> << /T (agree) /V /Yes >> << /T (fruit) /V [(Apple) (Cherry)] >> ] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n";
        var values = PdfFormService.ReadFdf(Encoding.ASCII.GetBytes(fdf));
        Assert.Equal(("person.name", "Nested"), (values[0].Name, values[0].Values[0]));
        Assert.Equal("Yes", values.Single(v => v.Name == "agree").Values[0]);
        Assert.Equal(new[] { "Apple", "Cherry" }, values.Single(v => v.Name == "fruit").Values);
        Assert.Throws<InvalidDataException>(() => PdfFormService.ReadXfdf(Encoding.ASCII.GetBytes("<not xml")));
    }

    [Fact]
    public async Task Reset_ReturnsFieldsToTheirDefaults()
    {
        await using var doc = await PdfEditableDocument.OpenAsync(FormPdfFixture.Build());
        PdfFormService.SetValues(doc, new[] { ("zip", (IReadOnlyList<string>)new[] { "12345" }), ("agree", (IReadOnlyList<string>)new[] { "Yes" }) }, validate: false);
        await new PdfFormService().ResetFormAsync(doc);
        var form = PdfAcroForm.Read(doc.Vector)!;
        Assert.Equal(string.Empty, form["zip"]!.Value);
        Assert.False(form["agree"]!.IsChecked);
        Assert.Equal("Off", form["colour"]!.Value);
    }

    [Fact]
    public async Task Flatten_DrawsTheFieldsIntoThePage_AsPdfiumShowsTheFilledForm()
    {
        await using var doc = await PdfEditableDocument.OpenAsync(FormPdfFixture.Build());
        PdfFormService.SetValues(doc, new[]
        {
            ("person.name", (IReadOnlyList<string>)new[] { "Flattened Name" }),
            ("notes", (IReadOnlyList<string>)new[] { "Notes" }),
            ("zip", (IReadOnlyList<string>)new[] { "75001" }),
            ("agree", (IReadOnlyList<string>)new[] { "Yes" }),
            ("colour", (IReadOnlyList<string>)new[] { "Red" }),
            ("city", (IReadOnlyList<string>)new[] { "PAR" }),
            ("fruit", (IReadOnlyList<string>)new[] { "Apple" }),
        }, validate: false);
        string flatPath = Path.Combine(_dir, "flat.pdf");
        await new PdfFormService().FlattenFormFieldsAsync(doc, flatPath);
        byte[] flat = File.ReadAllBytes(flatPath);

        using (var flattened = await PdfVectorDocument.OpenAsync(flat))
        {
            Assert.Null(PdfAcroForm.Read(flattened));
            Assert.Empty(PdfFormService.ReadAllFields(flattened));
        }
        // PDFium's page rendering never draws widgets (only FPDF_FFLDraw does), so the filled form
        // is drawn by the engine; the flattened page, with nothing left to fill, by both.
        var filledPage = await NativeRender(doc.Bytes);
        var flatPage = await NativeRender(flat);
        Assert.True(PdfiumOracle.MeanDifference(filledPage, flatPage) < 0.5, "flattening changes nothing on the page");
        using var after = new PdfiumOracle(flat);
        Assert.DoesNotContain(after.Annots(0), a => a.Subtype == "Widget");
        double diff = PdfiumOracle.MeanDifference(flatPage, after.Render(0, 72));
        Assert.True(diff < 4.0, $"PDFium draws the flattened fields: mean difference {diff:F2}/255");
    }

    private static async Task<(int Width, int Height, byte[] Bgra)> NativeRender(byte[] pdf)
    {
        using var vdoc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await vdoc.GetPageDisplayListAsync(1);
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new PdfEngine.Rendering.RenderRequest { PageNumber = 1, Dpi = 72 }, null, System.Threading.CancellationToken.None);
        using var page = result.Page;
        var tight = new byte[page.WidthPixels * page.HeightPixels * 4];
        var span = page.Pixels.Span;
        for (int y = 0; y < page.HeightPixels; y++)
            span.Slice(y * page.Stride, page.WidthPixels * 4).CopyTo(tight.AsSpan(y * page.WidthPixels * 4));
        return (page.WidthPixels, page.HeightPixels, tight);
    }

    // ------------------------------------------------------------------ safety and clean copy

    /// <summary>Document script (name tree and open action), a launch link, a URI link, a page action, a field script and an embedded file.</summary>
    private static byte[] ActiveContent()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int js = b.Add("<< /Type /Action /S /JavaScript /JS (app.alert\\('hello'\\);) >>");
        int payload = b.AddStream("/Type /EmbeddedFile", "MALICIOUS_PAYLOAD_MARKER");
        int spec = b.Add($"<< /Type /Filespec /F (payload.exe) /UF (payload.exe) /EF << /F {payload} 0 R >> >>");
        int uri = b.Add("<< /Type /Annot /Subtype /Link /Rect [50 650 300 670] /A << /S /URI /URI (https://example.com/tracker) >> >>");
        int launch = b.Add("<< /Type /Annot /Subtype /Link /Rect [50 600 300 620] /A << /S /Launch /F (calc.exe) >> >>");
        int widget = b.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /Rect [50 500 300 520] /AA << /K << /S /JavaScript /JS (fieldKey;) >> >> >>");
        int attach = b.Add($"<< /Type /Annot /Subtype /FileAttachment /Rect [50 450 70 470] /FS {spec} 0 R >>");
        b.AddPage("BT /F1 12 Tf 50 700 Td (Active content) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>",
            extra: $"/Annots [{uri} 0 R {launch} 0 R {widget} 0 R {attach} 0 R] /AA << /O << /S /JavaScript /JS (pageOpen;) >> >>");
        b.WithCatalog($"/OpenAction {js} 0 R /Names << /JavaScript << /Names [(EmbeddedScript) {js} 0 R] >> /EmbeddedFiles << /Names [(payload.exe) {spec} 0 R] >> >> /AcroForm << /Fields [{widget} 0 R] >>");
        return b.Build();
    }

    [Fact]
    public async Task SafetyReport_AgreesWithPdfium_AndFindsWhatItDoesNot()
    {
        string path = Save(ActiveContent(), "active.pdf");
        DocumentSafetyReport pdfium;
        using (var engine = new PdfiumEngine())
        await using (var doc = await engine.OpenDocumentAsync(path))
            pdfium = await new PdfiumSafetyInspector().InspectAsync(doc);
        await using var nativeDoc = await PdfEditableDocument.OpenAsync(path);
        var native = await new PdfSafetyInspector().InspectAsync(nativeDoc);

        Assert.Equal(pdfium.IsClean, native.IsClean);
        foreach (var f in pdfium.Findings)
        {
            var n = Assert.Single(native.Findings, x => x.Kind == f.Kind);
            Assert.Equal(f.Severity, n.Severity);
            if (f.Kind == DocumentRiskKind.JavaScript) Assert.True(n.Count >= f.Count);
            else Assert.Equal(f.Count, n.Count);
            Assert.Equal(f.Description.Length > 0, n.Description.Length > 0);
        }
        Assert.Equal(1, pdfium.Findings.Single(f => f.Kind == DocumentRiskKind.JavaScript).Count);
        // The name-tree script, the page action and the field keystroke script (the open action is the name-tree script).
        Assert.Equal(3, native.Findings.Single(f => f.Kind == DocumentRiskKind.JavaScript).Count);
        Assert.Contains("payload.exe", native.Findings.Single(f => f.Kind == DocumentRiskKind.EmbeddedFile).Details);
    }

    [Fact]
    public async Task CleanCopy_IsInert_ForBothInspectors_AndKeepsPagesAndLinks()
    {
        string path = Save(ActiveContent(), "active2.pdf");
        string cleanPath = Path.Combine(_dir, "active2.clean.pdf");
        SanitizationResult result;
        await using (var doc = await PdfEditableDocument.OpenAsync(path))
            result = await new PdfSanitizer().SanitizeAsync(doc, cleanPath);

        Assert.Equal(1, result.PageCount);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.DocumentScript);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.EmbeddedFile);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.LaunchAction);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.EmbeddedMedia);
        Assert.Contains(result.Changes, c => c.Kind == SanitizedContentKind.TriggeredAction);

        // PDFium's inspector (the oracle) finds nothing either.
        using (var engine = new PdfiumEngine())
        await using (var written = await engine.OpenDocumentAsync(cleanPath))
            Assert.True((await new PdfiumSafetyInspector().InspectAsync(written)).IsClean);
        string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(cleanPath));
        foreach (var gone in new[] { "/JavaScript", "/Launch", "calc.exe", "MALICIOUS_PAYLOAD_MARKER", "/OpenAction", "app.alert", "pageOpen", "fieldKey", "/EmbeddedFile" })
            Assert.DoesNotContain(gone, bytes);
        using var oracle = new PdfiumOracle(File.ReadAllBytes(cleanPath));
        Assert.Equal(1, oracle.PageCount);
        // The web link stays: this reader never follows a link on its own.
        Assert.Contains("example.com", Assert.Single(oracle.Links(0)).Uri);
        // The field stays visible, drawn into the page (it is no longer fillable).
        Assert.Contains(result.SideEffects, e => e.Contains("no longer fillable"));
    }

    [Fact]
    public async Task CleanCopy_ReportsAtLeastWhatPdfiumRemoves()
    {
        string path = Save(ActiveContent(), "active3.pdf");
        SanitizationResult pdfium;
        using (var engine = new PdfiumEngine())
        await using (var doc = await engine.OpenDocumentAsync(path))
            pdfium = await new PdfiumSanitizer().SanitizeAsync(doc, Path.Combine(_dir, "p.clean.pdf"));
        await using var nativeDoc = await PdfEditableDocument.OpenAsync(path);
        var native = await new PdfSanitizer().SanitizeAsync(nativeDoc, Path.Combine(_dir, "n.clean.pdf"));
        foreach (var c in pdfium.Changes)
            Assert.True(native.Changes.Single(n => n.Kind == c.Kind).Count >= c.Count, c.Kind.ToString());
        Assert.Equal(pdfium.SideEffects.Count(s => s.Contains("fillable")), native.SideEffects.Count(s => s.Contains("fillable")));
    }

    [Fact]
    public async Task CleanCopy_RefusesTheOriginal_AndDiscardsAnUnverifiedCopy()
    {
        string path = Save(ActiveContent(), "active4.pdf");
        await using var doc = await PdfEditableDocument.OpenAsync(path);
        var ex = await Assert.ThrowsAsync<PdfSanitizationException>(async () => await new PdfSanitizer().SanitizeAsync(doc, path));
        Assert.Contains("cannot overwrite the original", ex.Message);

        string target = Path.Combine(_dir, "dirty.pdf");
        var dirty = new PdfSanitizer(new AlwaysDirty());
        var fail = await Assert.ThrowsAsync<PdfSanitizationException>(async () => await dirty.SanitizeAsync(doc, target));
        Assert.Contains("still contained active content", fail.Message);
        Assert.False(File.Exists(target));
    }

    private sealed class AlwaysDirty : IPdfSafetyInspector
    {
        public ValueTask<DocumentSafetyReport> InspectAsync(PdfEngine.Documents.IPdfDocument document, System.Threading.CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DocumentSafetyReport
            {
                Findings = new[] { new DocumentRiskFinding { Kind = DocumentRiskKind.JavaScript, Severity = RiskSeverity.Elevated, Count = 1, Description = "Simulated." } },
            });
    }

    // ------------------------------------------------------------------ signatures and redaction

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    [Fact]
    public async Task SignatureService_AgreesWithPdfiumVerifier_IntactAndTampered()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 18 Tf 20 350 Td (Contract) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 300 400]");
        byte[] pdf = b.Build();
        using var cert = SelfSigned("Signer");
        byte[] signed;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Signer", Reason = "Approve" });
            signed = prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
        }
        byte[] tampered = (byte[])signed.Clone();
        int at = Encoding.ASCII.GetString(tampered).IndexOf("(Contract)", StringComparison.Ordinal);
        tampered[at + 1] = (byte)'K';

        foreach (var (bytes, expected) in new[] { (signed, SignatureStatus.Untrusted), (tampered, SignatureStatus.DocumentModified) })
        {
            string path = Save(bytes, Guid.NewGuid().ToString("N") + ".pdf");
            IReadOnlyList<SignatureInfo> pdfium;
            using (var engine = new PdfiumEngine())
            await using (var doc = await engine.OpenDocumentAsync(path))
                pdfium = await engine.SignatureService.GetSignaturesAsync(doc);
            await using var nativeDoc = await PdfEditableDocument.OpenAsync(path);
            var native = await new PdfSignatureService().GetSignaturesAsync(nativeDoc);
            Assert.Equal(expected, Assert.Single(pdfium).Status);
            var n = Assert.Single(native);
            Assert.Equal(expected, n.Status);
            Assert.Equal("Approve", n.Reason);
            Assert.Equal(expected, await new PdfSignatureService().VerifySignatureAsync(nativeDoc, n.FieldName));
        }
    }

    [Fact]
    public async Task RedactionService_RemovesTheTextUnderTheArea()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 18 Tf 20 350 Td (SECRETWORD) Tj ET BT /F1 18 Tf 20 100 Td (KEEPWORD) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 300 400]");
        await using var doc = await PdfEditableDocument.OpenAsync(b.Build());
        var service = new PdfRedactionService();
        // Top of the page, normalized from the top-left corner: covers the first line only.
        await service.AddPendingRedactionAsync(doc, new PdfEngine.Redaction.RedactionArea { PageNumber = 1, Bounds = new PdfEngine.Geometry.PdfRect(0, 0, 1, 0.25) });
        var pending = await service.GetPendingRedactionsAsync(doc);
        string target = Path.Combine(_dir, "redacted.pdf");
        await service.ApplyRedactionsAsync(doc, target, pending);
        using var oracle = new PdfiumOracle(File.ReadAllBytes(target));
        using var engine = new PdfiumEngine();
        await using var written = await engine.OpenDocumentAsync(target);
        string text = await engine.TextService.ExtractPageTextAsync(written, 1);
        Assert.DoesNotContain("SECRETWORD", text);
        Assert.Contains("KEEPWORD", text);
        Assert.Empty(await service.GetPendingRedactionsAsync(doc));
    }
}
