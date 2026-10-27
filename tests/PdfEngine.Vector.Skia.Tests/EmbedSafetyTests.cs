using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The limits and rules that keep the web viewer's edits and saves safe: a save never posts
/// bytes without the latest edit, one save runs at a time, the annotation point budget, the
/// streaming XFDF reader's limits, choice and length checks of form values, and Acrobat's rules
/// for signed and certified documents.
/// </summary>
public class EmbedSafetyTests
{
    private static byte[] OnePage()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static ViewerAnnotation Note(string id) =>
        new() { Id = id, Page = 1, Kind = ViewerAnnotationKind.Note, Box = new ViewerBox(10, 10, 30, 30), Contents = "n" };

    private static ViewerAnnotation Ink(string id, int points)
    {
        var strokes = new List<IReadOnlyList<double>>();
        for (int left = points; left > 0; left -= ViewerAnnotationStore.MaxInkPointsPerStroke)
        {
            int n = Math.Min(left, ViewerAnnotationStore.MaxInkPointsPerStroke);
            var stroke = new double[2 * n];
            for (int i = 0; i < n; i++) (stroke[2 * i], stroke[2 * i + 1]) = (100 + i % 100, 100 + i % 50);
            strokes.Add(stroke);
        }
        return new ViewerAnnotation { Id = id, Page = 1, Kind = ViewerAnnotationKind.Ink, Box = new ViewerBox(90, 90, 210, 160), InkList = strokes };
    }

    // ------------------------------------------------------------------ saving

    [Fact]
    public void AFailedWrite_KeepsTheDocumentDirty_AndASaveRefusesToPost()
    {
        var s = new ViewerEditState();
        int first = s.Edit();
        s.Applied(first);
        Assert.True(s.TryBeginSave());
        int saveId = s.Post();
        Assert.True(saveId > 0);
        Assert.True(s.Answer(saveId, saved: true));
        Assert.False(s.IsDirty);

        // The next edit cannot be written (a forced failure): it is not marked applied.
        int second = s.Edit();
        s.Failed(second);
        Assert.True(s.IsDirty);
        Assert.False(s.IsCurrent);
        Assert.False(s.NeedsApply); // not retried in a loop
        Assert.Equal(first, s.AppliedVersion);

        // A save then posts nothing: no id, the save ends, the document is still dirty.
        Assert.True(s.TryBeginSave());
        Assert.Equal(0, s.Post());
        Assert.False(s.Saving);
        Assert.True(s.IsDirty);

        // A later edit is tried again; once written, it can be saved.
        int third = s.Edit();
        Assert.True(s.NeedsApply);
        s.Applied(third);
        Assert.Equal(0, s.FailedVersion);
        Assert.True(s.TryBeginSave());
        Assert.True(s.Post() > 0);
    }

    [Fact]
    public void OneSaveAtATime_AndTheSavedVersionIsTheOnePosted()
    {
        var s = new ViewerEditState();
        s.Applied(s.Edit());
        Assert.True(s.TryBeginSave());
        // A blur, an autosave and Ctrl+S during the save start nothing.
        Assert.False(s.TryBeginSave());
        int saveId = s.Post();
        int posted = s.PostedVersion;
        // An edit while the host page saves: the answer saves the posted version, not this one.
        s.Applied(s.Edit());
        Assert.False(s.TryBeginSave());
        Assert.False(s.Answer(saveId + 1, saved: true)); // an answer to another save is ignored
        Assert.True(s.Saving);
        Assert.True(s.Answer(saveId, saved: true));
        Assert.Equal(posted, s.SavedVersion);
        Assert.True(s.IsDirty);
        Assert.False(s.Saving);

        // A host page that says it did not save leaves the document dirty.
        Assert.True(s.TryBeginSave());
        int next = s.Post();
        Assert.True(s.Answer(next, saved: false));
        Assert.True(s.IsDirty);
    }

    [Fact]
    public async Task AnEditThatCannotBeWritten_LeavesTheBytesAsTheyWere()
    {
        // The page is a direct object in /Kids: an incremental update cannot change it (a real, non-validation failure).
        const string body = "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>] /Count 1 >>\nendobj\n";
        int second = body.IndexOf("2 0 obj", StringComparison.Ordinal);
        string pdf = body + $"xref\n0 3\n0000000000 65535 f \n{9:D10} 00000 n \n{second:D10} 00000 n \ntrailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{body.Length}\n%%EOF\n";
        await using var doc = await ViewerDocument.OpenAsync(Encoding.Latin1.GetBytes(pdf));
        byte[] before = doc.Bytes;
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add([Note("n")]);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => doc.ApplyEditsAsync(store.FromFile, store.Current, []));
        Assert.IsNotType<PdfFieldValidationException>(ex);
        Assert.Same(before, doc.Bytes);
        Assert.False(doc.IsEdited);
    }

    // ------------------------------------------------------------------ annotation limits

    [Fact]
    public async Task ThePointBudget_IsDocumentWide_InAddAndUpdate()
    {
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        // 20 ink annotations of 50,000 points: the budget, exactly.
        for (int i = 0; i < 20; i++) store.Add([Ink("i" + i, ViewerAnnotationStore.MaxInkPoints)]);
        Assert.Equal(ViewerAnnotationStore.MaxPoints, ViewerAnnotationStore.Points(store.Current));
        var add = Assert.Throws<ViewerAnnotationException>(() => store.Add([Ink("over", 1)]));
        Assert.Contains("1000000", add.Message);
        // Notes have no points: they can still be added.
        store.Add([Note("n")]);
        // An update that makes an annotation smaller frees points; one that makes it larger must fit.
        store.Update(Ink("i0", 10));
        store.Add([Ink("j", ViewerAnnotationStore.MaxInkPoints - 10)]);
        Assert.Throws<ViewerAnnotationException>(() => store.Update(Ink("i0", 11)));
        store.Update(Ink("i0", 5));
        Assert.Equal(ViewerAnnotationStore.MaxPoints - 5, ViewerAnnotationStore.Points(store.Current));
    }

    [Fact]
    public void ThePointBudget_IsCheckedPerCall_AsInvalidArgument()
    {
        // 251 highlights of 1000 quadrilaterals are 1,004,000 points: one call over the budget is refused while it is read.
        var quads = string.Join(",", Enumerable.Repeat("1,1,1,1,1,1,1,1", ViewerAnnotationStore.MaxQuads));
        string one = $"{{\"type\":\"Annotation\",\"target\":{{\"selector\":{{\"node\":{{\"index\":0}},\"subtype\":\"highlight\",\"quadPoints\":[{quads}]}}}}}}";
        using var over = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat(one, 251)) + "]");
        var ex = Assert.Throws<EmbedProtocolException>(() => EmbedAnnotationJson.ParseList(over.RootElement, 9, "", default));
        Assert.Equal(EmbedErrors.InvalidArgument, ex.Code);
        Assert.Equal(9, ex.RequestId);
        using var under = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat(one, 250)) + "]");
        Assert.Equal(250, EmbedAnnotationJson.ParseList(under.RootElement, 9, "", default).Count);
    }

    [Fact]
    public void TheStoreAndTheXfdfReader_ShareTheirLimits()
    {
        Assert.Equal(PdfAnnotationLimits.MaxContentsLength, ViewerAnnotationStore.MaxContentsLength);
        Assert.Equal(PdfAnnotationLimits.MaxTextLength, ViewerAnnotationStore.MaxAuthorLength);
        Assert.Equal(PdfAnnotationLimits.MaxQuads, ViewerAnnotationStore.MaxQuads);
        Assert.Equal(PdfAnnotationLimits.MaxInkPointsPerStroke, PdfAnnotationXfdf.MaxInkPoints);
        Assert.Equal(PdfAnnotationLimits.MaxInkStrokes, PdfAnnotationXfdf.MaxInkStrokes);
    }

    // ------------------------------------------------------------------ XFDF

    private const string Xfdf = "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>";
    private const string End = "</annots></xfdf>";

    [Fact]
    public async Task Xfdf_LongFieldsAreCut_AndTooManyQuadsAreSkipped_SoTheImportIsNotRefused()
    {
        string longText = new('a', 20000);
        string quads = string.Join(",", Enumerable.Repeat("72,720,200,720,72,700,200,700", PdfAnnotationLimits.MaxQuads + 1));
        var import = PdfAnnotationXfdf.ImportWithReport(Xfdf +
            $"<text page=\"0\" rect=\"10,10,30,30\" title=\"{longText}\" subject=\"{longText}\" name=\"{longText}\"><contents>{longText}</contents></text>" +
            $"<highlight page=\"0\" rect=\"72,700,200,720\" coords=\"{quads}\"/>" +
            "<square page=\"0\" rect=\"10,20,30,40\"/>" + End);
        Assert.Equal(1, import.Skipped);
        Assert.Equal(2, import.Annotations.Count);
        var note = import.Annotations[0];
        Assert.Equal(PdfAnnotationLimits.MaxTextLength, note.Author!.Length);
        Assert.Equal(PdfAnnotationLimits.MaxTextLength, note.Subject!.Length);
        Assert.Equal(PdfAnnotationLimits.MaxContentsLength, note.Contents!.Length);

        // What the reader gave, the store takes.
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        var list = import.Annotations.Select(p => ViewerAnnotationPdf.FromPdf(p, "")!).Select(a => a with { Id = "" }).ToList();
        Assert.Equal(2, store.Add(list).Count);
    }

    [Fact]
    public void Xfdf_HasAnElementLimit_AndAnAttributeLimit()
    {
        string many = string.Concat(Enumerable.Repeat("<square page=\"0\" rect=\"1,1,2,2\"><x/><x/><x/><x/><x/><x/><x/><x/><x/><x/></square>", 9500));
        var tooMany = Assert.Throws<FormatException>(() => PdfAnnotationXfdf.Import(Xfdf + many + End));
        Assert.Contains("elements", tooMany.Message);
        string attributes = string.Concat(Enumerable.Range(0, PdfAnnotationXfdf.MaxAttributes + 1).Select(i => $" a{i}=\"1\""));
        var wide = Assert.Throws<FormatException>(() => PdfAnnotationXfdf.Import(Xfdf + $"<square page=\"0\" rect=\"1,1,2,2\"{attributes}/>" + End));
        Assert.Contains("attributes", wide.Message);
    }

    [Fact]
    public void Xfdf_SkipsUnknownContent_AndStillRefusesDtds()
    {
        // Unknown elements anywhere (with deep content) are skipped; the annotations around them are read.
        string deep = string.Concat(Enumerable.Repeat("<y>", 500)) + string.Concat(Enumerable.Repeat("</y>", 500));
        var list = PdfAnnotationXfdf.Import("<?xml version=\"1.0\"?><!-- c --><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><f href=\"a.pdf\"/><fields><field name=\"x\"><value>1</value></field></fields><annots>" +
            $"<caret page=\"0\" rect=\"0,0,10,10\">{deep}</caret>" +
            $"<square page=\"0\" rect=\"10,20,30,40\"><unknown>{deep}</unknown><contents>Box<b>bold</b> end</contents><popup rect=\"1,2,3,4\" open=\"yes\"/></square>" +
            "<ink page=\"0\" rect=\"0,0,50,50\"><inklist><gesture>1,1;2,2;3,3</gesture><other/><gesture>4,4</gesture></inklist></ink>" + End);
        Assert.Equal(2, list.Count);
        Assert.Equal("Box end", list[0].Contents);
        Assert.True(list[0].PopupOpen);
        Assert.Equal([3, 1], list[1].InkList.Select(s => s.Count));
        Assert.Throws<FormatException>(() => PdfAnnotationXfdf.Import("<?xml version=\"1.0\"?><!DOCTYPE xfdf SYSTEM \"http://example.com/x.dtd\"><xfdf/>"));
        Assert.Throws<FormatException>(() => PdfAnnotationXfdf.Import("<xfdf><annots><square page=\"0\" rect=\"1,1,2,2\"></annots></xfdf>"));
    }

    [Theory]
    [InlineData("2 1", new[] { 2.0, 1.0 })]
    [InlineData("-1 2", new[] { 3.0 })]
    [InlineData("0 0", new[] { 3.0 })]
    [InlineData("", new[] { 3.0 })]
    [InlineData("1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1", new[] { 1.0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData("1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1", new[] { 3.0 })]
    public void Xfdf_DashPatterns_AreChecked(string dashes, double[] expected)
    {
        var a = Assert.Single(PdfAnnotationXfdf.Import(Xfdf + $"<square page=\"0\" rect=\"10,20,30,40\" style=\"dash\" dashes=\"{dashes}\"/>" + End));
        Assert.Equal(expected, a.BorderDash);
    }

    // ------------------------------------------------------------------ form values

    private static async Task<byte[]> Fill(byte[] pdf, params (string Name, IReadOnlyList<string> Values)[] values)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfNativeFormService.Fill(doc, pdf, values, validate: true).Bytes;
    }

    [Fact]
    public async Task Choices_MustBeOptions_UnlessTheComboIsEditable()
    {
        byte[] pdf = FormPdfFixture.Build();
        var rome = await Fill(pdf, ("city", ["Rome"]));
        using (var doc = await PdfVectorDocument.OpenAsync(rome)) Assert.Equal("ROM", PdfAcroForm.Read(doc)!["city"]!.Value);
        await Fill(pdf, ("city", ["PAR"]));
        await Fill(pdf, ("city", [""]));
        var city = await Assert.ThrowsAsync<ArgumentException>(() => Fill(pdf, ("city", ["Lisbon"])));
        Assert.Contains("not an option", city.Message);
        await Fill(pdf, ("fruit", ["Banana", "Cherry"]));
        await Assert.ThrowsAsync<ArgumentException>(() => Fill(pdf, ("fruit", ["Banana", "Kiwi"])));

        // An editable combo box takes any text.
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int page = b.AddPage("", $"<< /Font << /Helv {helv} 0 R >> >>", mediaBox: "[0 0 300 300]", extra: "/Annots [6 0 R]");
        int combo = b.Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff {(1 << 17) | (1 << 18)} /T (town) /Opt [(Oslo) (Bergen)] /Rect [20 20 200 40] /P {page} 0 R /DA (/Helv 11 Tf 0 g) >>");
        Assert.Equal(6, combo);
        b.WithCatalog($"/AcroForm << /Fields [{combo} 0 R] /DA (/Helv 0 Tf 0 g) >>");
        var typed = await Fill(b.Build(), ("town", ["Tromsø"]));
        using (var doc = await PdfVectorDocument.OpenAsync(typed)) Assert.Equal("Tromsø", PdfAcroForm.Read(doc)!["town"]!.Value);
    }

    [Fact]
    public async Task Values_KeepTheLengthLimits_AndTheFieldsMaxLen()
    {
        byte[] pdf = FormPdfFixture.Build();
        await Fill(pdf, ("zip", ["12345"]));
        var zip = await Assert.ThrowsAsync<ArgumentException>(() => Fill(pdf, ("zip", ["123456"])));
        Assert.Contains("at most 5 characters", zip.Message);
        await Fill(pdf, ("notes", [new string('x', PdfNativeFormService.MaxValueLength)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Fill(pdf, ("notes", [new string('x', PdfNativeFormService.MaxValueLength + 1)])));
        var many = Enumerable.Repeat(("notes", (IReadOnlyList<string>)["x"]), PdfNativeFormService.MaxChanges + 1).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => Fill(pdf, many));
    }

    // ------------------------------------------------------------------ signed and certified files

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Author", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task<byte[]> Signed(int? certificationLevel)
    {
        byte[] pdf = FormPdfFixture.Build();
        using var cert = Certificate();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Author", CertificationLevel = certificationLevel });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
    }

    private static async Task<JsonElement> Metadata(ViewerDocument doc)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) EmbedProtocol.Metadata(await doc.GetMetadataAsync(), "a.pdf", null)(w);
        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix) => bytes.Length > prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    [Fact]
    public async Task UnsignedFiles_SaySo_AndAreNotLimited()
    {
        await using var doc = await ViewerDocument.OpenAsync(FormPdfFixture.Build());
        Assert.False(doc.IsSigned);
        Assert.True(doc.CanAnnotate && doc.CanFillForms);
        Assert.Null(doc.EditRestriction);
        Assert.Null(doc.EditNotice);
        var m = await Metadata(doc);
        Assert.False(m.GetProperty("isSigned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("certificationLevel").ValueKind);
    }

    [Fact]
    public async Task CertifiedLevel1_AllowsNoChanges()
    {
        byte[] signed = await Signed(1);
        await using var doc = await ViewerDocument.OpenAsync(signed);
        Assert.True(doc.IsSigned);
        Assert.Equal(1, doc.CertificationLevel);
        Assert.False(doc.CanAnnotate);
        Assert.False(doc.CanFillForms);
        Assert.Contains("does not allow changes", doc.EditRestriction);
        Assert.Null(doc.EditNotice);
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add([Note("n")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.ApplyEditsAsync([], store.Current, []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.ApplyEditsAsync([], [], [("person.name", ["x"])]));
        Assert.Same(signed, doc.Bytes);
        var m = await Metadata(doc);
        Assert.True(m.GetProperty("isSigned").GetBoolean());
        Assert.Equal(1, m.GetProperty("certificationLevel").GetInt32());
    }

    [Fact]
    public async Task CertifiedLevel2_AllowsFormFillingOnly_AsANewRevision()
    {
        byte[] signed = await Signed(2);
        await using var doc = await ViewerDocument.OpenAsync(signed);
        Assert.Equal(2, doc.CertificationLevel);
        Assert.False(doc.CanAnnotate);
        Assert.True(doc.CanFillForms);
        Assert.Contains("form filling only", doc.EditRestriction);
        Assert.NotNull(doc.EditNotice);
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add([Note("n")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.ApplyEditsAsync([], store.Current, []));
        await doc.ApplyEditsAsync([], [], [("person.name", ["Grace"])]);
        Assert.True(StartsWith(doc.Bytes, signed));
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(doc.Bytes));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
    }

    [Fact]
    public async Task CertifiedLevel3_AllowsFormsAndAnnotations_AsANewRevision()
    {
        byte[] signed = await Signed(3);
        await using var doc = await ViewerDocument.OpenAsync(signed);
        Assert.Equal(3, doc.CertificationLevel);
        Assert.True(doc.CanAnnotate && doc.CanFillForms);
        Assert.Null(doc.EditRestriction);
        Assert.Contains("new revision", doc.EditNotice);
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add([Note("n")]);
        await doc.ApplyEditsAsync([], store.Current, [("person.name", ["Grace"])]);
        Assert.True(StartsWith(doc.Bytes, signed));
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(doc.Bytes));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
    }

    [Fact]
    public async Task ASignedFile_IsSavedIncrementally_WithANotice()
    {
        byte[] signed = await Signed(null);
        await using var doc = await ViewerDocument.OpenAsync(signed);
        Assert.True(doc.IsSigned);
        Assert.Null(doc.CertificationLevel);
        Assert.True(doc.CanAnnotate && doc.CanFillForms);
        Assert.Contains("modified after signing", doc.EditNotice);
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add([Note("n")]);
        await doc.ApplyEditsAsync([], store.Current, [("person.name", ["Grace"])]);
        // The original bytes are a prefix: the signed revision is untouched.
        Assert.True(StartsWith(doc.Bytes, signed));
        Assert.True((await Metadata(doc)).GetProperty("isSigned").GetBoolean());
    }

    [Fact]
    public async Task ARepairedSignedFile_RefusesEdits_InsteadOfBreakingItsSignatures()
    {
        byte[] signed = await Signed(null);
        // A broken last startxref: the file is repaired on opening, so a save would rewrite it whole.
        string text = Encoding.Latin1.GetString(signed);
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref\n".Length;
        int end = text.IndexOf('\n', at);
        byte[] damaged = Encoding.Latin1.GetBytes(text[..at] + "999999999" + text[end..]);
        await using var doc = await ViewerDocument.OpenAsync(damaged);
        Assert.True(doc.WasRepaired);
        Assert.True(doc.IsSigned);
        Assert.False(doc.CanAnnotate);
        Assert.False(doc.CanFillForms);
        Assert.Contains("break its signatures", doc.EditRestriction);
        Assert.Null(doc.EditNotice);
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add([Note("n")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.ApplyEditsAsync([], store.Current, []));
        Assert.Same(damaged, doc.Bytes);
    }
}
