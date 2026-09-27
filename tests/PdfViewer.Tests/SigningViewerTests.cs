using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PdfEngine.Pdfium;
using PdfEngine.Signatures;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Signing in the viewer: a box placed on a page or an empty signature field, the current
/// revision (filled fields included) signed and saved, the signed file re-opened, and the
/// preconditions (unsaved annotations) and certificate rules that keep a signature honest.
/// </summary>
public class SigningViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SigningViewer_" + Guid.NewGuid().ToString("N"));

    public SigningViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static X509Certificate2 Certificate(string name, Action<CertificateRequest>? extend = null,
        DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, bool keyUsage = true)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (keyUsage)
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        extend?.Invoke(req);
        using var created = req.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    private string Write(string name, byte[] pdf)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    private static byte[] SignatureFieldPdf()
    {
        var b = new VectorPdfBuilder();
        int field = b.Add("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approver) /TU (Approver signature) /Rect [40 40 260 100] /F 4 >>");
        b.AddPage("", mediaBox: "[0 0 300 400]", extra: $"/Annots [{field} 0 R]");
        int acro = b.Add($"<< /Fields [{field} 0 R] >>");
        b.WithCatalog($"/AcroForm {acro} 0 R");
        return b.Build();
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    private static async Task<System.Collections.Generic.IReadOnlyList<SignatureInfo>> Verify(string path)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(path);
        return await engine.SignatureService.GetSignaturesAsync(doc);
    }

    [Fact]
    public async Task SigningABox_SavesTheSignedFile_AndReopensIt()
    {
        string path = Write("contract.pdf", FormPdfFixture.Build());
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        using var cert = Certificate("Ada Lovelace");

        bool ok = await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(cert, "I approve this document", "London", null, null, path));

        Assert.True(ok, vm.StatusText);
        byte[] saved = File.ReadAllBytes(path);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the signature is appended, the original untouched");
        var info = Assert.Single(await Verify(path));
        Assert.True(info.Status == SignatureStatus.Untrusted, info.StatusMessage); // intact; self-signed
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.IsDocumentLoaded);
        Assert.Contains("Signed by Ada Lovelace", vm.StatusText);

        // The box landed where it was placed: x 0.1..0.6 of 300 pt, top 0.8 of 400 pt (so y 32..80).
        using var doc = await PdfVectorDocument.OpenAsync(saved);
        var sig = PdfAcroForm.Read(doc)!.Fields.Single(f => f.Kind == PdfFormFieldKind.Signature);
        Assert.True(sig.IsSigned);
        var r = Assert.Single(sig.Widgets).Rect;
        Assert.Equal(30, r.X, 3);
        Assert.Equal(150, r.Width, 3);
        Assert.Equal(32, r.Y, 3);
        Assert.Equal(48, r.Height, 3);
    }

    [Fact]
    public async Task FilledFields_AreSignedWithTheDocument()
    {
        string path = Write("form.pdf", FormPdfFixture.Build());
        var vm = await Open(path);
        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.True(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "Grace Hopper")));
        Assert.True(vm.HasUnsavedChanges);
        Assert.Null(vm.WhyCannotSign()); // only form changes: the revision in memory is what gets signed

        using var cert = Certificate("Grace Hopper");
        string output = Path.Combine(_dir, "form-signed.pdf");
        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.5, 0.85, 0.4, 0.1)),
            new SignatureOptions(cert, null, null, null, null, output)), vm.StatusText);

        var info = Assert.Single(await Verify(output));
        Assert.True(info.Status == SignatureStatus.Untrusted, info.StatusMessage);
        var reopened = await Open(output);
        Assert.Equal("Grace Hopper", reopened.AllFormFields.Single(f => f.FullName == "person.name").Value);
        Assert.Equal(output, reopened.Metadata!.FilePath);
        Assert.True(File.Exists(path) && File.ReadAllBytes(path).Length < File.ReadAllBytes(output).Length, "the original file is left as it was");
    }

    [Fact]
    public async Task CommentingOnASignedDocument_KeepsItsSignatureValid()
    {
        string path = Write("signed-then-commented.pdf", FormPdfFixture.Build());
        var vm = await Open(path);
        using var cert = Certificate("Ada Lovelace");
        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(cert, null, null, null, null, path)), vm.StatusText);
        byte[] signedBytes = File.ReadAllBytes(path);

        vm.AddAnnotation(new AnnotationModel { PageNumber = 1, Type = AnnotationType.Rectangle, X = 0.6, Y = 0.1, Width = 0.2, Height = 0.1, ColorHex = "#FF0000", StrokeThickness = 2 });
        await vm.SaveAsync();
        byte[] saved = File.ReadAllBytes(path);

        Assert.True(saved.Length > signedBytes.Length);
        Assert.True(saved.AsSpan(0, signedBytes.Length).SequenceEqual(signedBytes), "the signed revision is kept byte for byte");
        var check = Assert.Single(await PdfEngine.Vector.Signatures.PdfSignatureValidator.ValidateAsync(saved));
        Assert.True(check.Verdict == PdfEngine.Vector.Signatures.PdfSignatureVerdict.ValidWithPermittedChanges, check.Summary);
        Assert.True(check.LaterChanges.HasFlag(PdfEngine.Vector.Signatures.PdfLaterChanges.Annotations), check.LaterChanges.ToString());
        Assert.Single((await Open(path)).AllAnnotations);
    }

    [Fact]
    public async Task SignaturePanel_ShowsEachSignaturesVerdict()
    {
        string path = Write("panel.pdf", FormPdfFixture.Build());
        var vm = await Open(path);
        Assert.False(vm.HasSignatures);
        using var cert = Certificate("Ada Lovelace");
        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(cert, "Approved", null, null, null, path)), vm.StatusText);
        await vm.ValidateSignaturesAsync();

        var item = Assert.Single(vm.Signatures);
        Assert.Equal("Signed by Ada Lovelace", item.Title);
        Assert.Equal("warn", item.Level); // intact, but a self-signed identity is unknown
        Assert.Contains("identity is unknown", item.Status);
        Assert.Contains("Reason: Approved", item.Details);
        Assert.True(item.HasPage);
        Assert.Equal("warn", vm.SignatureBannerLevel);
        Assert.StartsWith("Signed by Ada Lovelace. The document is intact", vm.SignatureBanner);

        // Filling a field (not yet saved) shows up as a permitted change to the signature.
        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.True(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "Later")));
        await vm.ValidateSignaturesAsync();
        var after = Assert.Single(vm.Signatures);
        Assert.Equal(PdfEngine.Vector.Signatures.PdfSignatureVerdict.ValidWithPermittedChanges, after.Check.Verdict);
        Assert.Contains("filled in", after.Summary);

        // Tools > Verify opens the panel.
        vm.IsSidebarOpen = false;
        await vm.VerifySignaturesCommand.ExecuteAsync(null);
        Assert.True(vm.IsSidebarOpen);
        Assert.Equal(MainViewModel.SignaturesTabIndex, vm.SelectedSidebarTab);
    }

    [Fact]
    public async Task CertifiedWithNoChangesAllowed_FlagsALaterFill()
    {
        string path = Write("certified.pdf", FormPdfFixture.Build());
        var vm = await Open(path);
        Assert.True(vm.CanCertify);
        using var cert = Certificate("Author");
        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(cert, null, null, null, null, path, CertificationLevel: 1)), vm.StatusText);
        await vm.ValidateSignaturesAsync();
        Assert.False(vm.CanCertify);
        var item = Assert.Single(vm.Signatures);
        Assert.Equal("Certified by Author", item.Title);
        Assert.Contains("no changes are allowed", item.Details);

        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.True(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "Not allowed")));
        await vm.ValidateSignaturesAsync();
        var after = Assert.Single(vm.Signatures);
        Assert.Equal("bad", after.Level);
        Assert.Equal(PdfEngine.Vector.Signatures.PdfSignatureVerdict.ModifiedAfterSigning, after.Check.Verdict);
        Assert.Equal("bad", vm.SignatureBannerLevel);
    }

    [Fact]
    public async Task UnsavedAnnotations_MustBeSavedFirst()
    {
        string path = Write("notes.pdf", FormPdfFixture.Build());
        var vm = await Open(path);
        string? alert = null;
        vm.ShowMessageBoxAction = (m, _, _, _) => alert = m;
        vm.AddAnnotation(new AnnotationModel { PageNumber = 1, Type = AnnotationType.Rectangle, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 });
        Assert.NotNull(vm.WhyCannotSign());

        using var cert = Certificate("Signer");
        Assert.False(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.1, 0.3, 0.1)),
            new SignatureOptions(cert, null, null, null, null, path)));
        Assert.Contains("Save your changes", alert);
        Assert.Empty(await Verify(path));
    }

    [Fact]
    public async Task EmptySignatureField_IsOfferedAndSigned()
    {
        string path = Write("approval.pdf", SignatureFieldPdf());
        var vm = await Open(path);
        Assert.True(vm.HasForm);
        var field = Assert.Single(vm.EmptySignatureFields);
        Assert.Equal("Approver", field.FullName);
        Assert.Equal("Approver signature", field.AccessibleName);

        using var cert = Certificate("Grace Hopper");
        SignaturePlacement? asked = null;
        vm.ShowSignDialogFunc = (p, _) => { asked = p; return new SignatureOptions(cert, "Approved", null, null, null, path); };
        Assert.True(await vm.PlaceSignatureAsync(new SignaturePlacement(field.PageNumber, null, field.FullName)), vm.StatusText);
        Assert.Equal("Approver", asked!.FieldName);

        Assert.Single(await Verify(path));
        Assert.Empty(vm.EmptySignatureFields); // signed: no longer offered
    }

    [Fact]
    public async Task CancellingTheDialog_ChangesNothing()
    {
        string path = Write("keep.pdf", FormPdfFixture.Build());
        byte[] before = File.ReadAllBytes(path);
        var vm = await Open(path);
        vm.ShowSignDialogFunc = (_, _) => null;
        vm.BeginSignatureCommand.Execute(null);
        Assert.True(vm.IsPlacingSignature);
        Assert.False(await vm.PlaceSignatureAsync(new SignaturePlacement(1, new Rect(0.1, 0.1, 0.3, 0.1))));
        Assert.False(vm.IsPlacingSignature);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PlacingASignature_AndDrawingAnnotations_AreExclusive()
    {
        var vm = await Open(Write("modes.pdf", FormPdfFixture.Build()));
        vm.BeginSignatureCommand.Execute(null);
        Assert.True(vm.IsPlacingSignature);
        vm.ActiveAnnotationTool = AnnotationType.Rectangle;
        Assert.False(vm.IsPlacingSignature);
        vm.BeginSignatureCommand.Execute(null);
        Assert.Null(vm.ActiveAnnotationTool);
        vm.BeginSignatureCommand.Execute(null); // again: cancels
        Assert.False(vm.IsPlacingSignature);
    }

    [Fact]
    public async Task Placement_FollowsTheCropBox()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 600 800]", extra: "/CropBox [100 200 400 600]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var r = MainViewModel.ToUserSpace(doc, 1, new Rect(0.25, 0.5, 0.5, 0.25));
        Assert.Equal(100 + 0.25 * 300, r.X, 6);
        Assert.Equal(150, r.Width, 6);
        Assert.Equal(200 + 0.25 * 400, r.Y, 6); // top 0.5 down, 0.25 tall: bottom at 0.75 from the top
        Assert.Equal(100, r.Height, 6);
    }

    [Fact]
    public void CertificateRules_KeepSignaturesHonest()
    {
        var now = DateTimeOffset.Now;
        using var good = Certificate("Good", req => req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.4") }, false)));
        Assert.Null(SigningCertificates.WhyNotUsable(good, now));

        using var expired = Certificate("Old", notBefore: now.AddYears(-3), notAfter: now.AddYears(-1));
        Assert.Contains("expired", SigningCertificates.WhyNotUsable(expired, now));

        using var future = Certificate("Future", notBefore: now.AddDays(10), notAfter: now.AddYears(1));
        Assert.Contains("not valid until", SigningCertificates.WhyNotUsable(future, now));

        using var encryptOnly = Certificate("Encrypt", req => req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true)), keyUsage: false);
        Assert.Contains("not issued for digital signatures", SigningCertificates.WhyNotUsable(encryptOnly, now));

        using var serverOnly = Certificate("Server", req => req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)));
        Assert.Contains("not issued for signing documents", SigningCertificates.WhyNotUsable(serverOnly, now));

        using var publicOnly = X509CertificateLoader.LoadCertificate(good.RawData);
        Assert.Contains("private key", SigningCertificates.WhyNotUsable(publicOnly, now));
    }

    [Fact]
    public void CertificateFile_LoadsTheSigningIdentity()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=File Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        string pfx = Path.Combine(_dir, "id.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "secret"));
        using var loaded = SigningCertificates.FromFile(pfx, "secret");
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal("File Signer", SigningCertificates.SignerName(loaded));
        Assert.ThrowsAny<CryptographicException>(() => SigningCertificates.FromFile(pfx, "wrong"));
    }

    [Fact]
    public async Task FormLayer_OffersSignHere_ForEmptySignatureFields()
    {
        var vm = await Open(Write("layer.pdf", SignatureFieldPdf()));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var layer = new FormFieldsLayer { Page = vm.Pages[0], Width = 600, Height = 800 };
                var editor = Assert.Single(layer.Children.OfType<Border>()).Child;
                var button = Assert.IsType<Button>(editor);
                Assert.Equal("Sign here", ((TextBlock)button.Content).Text);
                Assert.Equal("Approver signature", System.Windows.Automation.AutomationProperties.GetName(button));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null) throw error;
    }
}
