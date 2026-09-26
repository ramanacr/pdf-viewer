using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Vector;
using PdfEngine.Vector.Security;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Secured documents in the viewer: permissions (ISO 32000-2 Table 22) are enforced for readers
/// without the owner password, and certificate-encrypted files — which PDFium cannot open — open
/// through the vector core's public-key handler on an in-memory decrypted copy that is never
/// written over the original.
/// </summary>
public class SecuredDocumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SecuredDocumentTests_" + Guid.NewGuid().ToString("N"));

    public SecuredDocumentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PdfPublicKeySecurityHandler.CertificateSource = PdfPublicKeySecurityHandler.StoreCertificates;
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Print (3), copy (5) and annotate (6) cleared; modify (4), fill forms (9), accessibility (10), assemble (11) set.
    private const int Restricted = unchecked((int)0xFFFFF0C8 & ~(1 << 2) & ~(1 << 4) & ~(1 << 5) | (1 << 8) | (1 << 9) | (1 << 10));

    private string Write(string name, PdfTestEncryption? encryption)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 24 Tf 20 100 Td (Secured text) Tj ET 0 0 1 rg 20 20 60 40 re f", $"<< /Font << /F1 {font} 0 R >> >>");
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build(encryption));
        return path;
    }

    [Fact]
    public void Permissions_FollowTable22_AndRevision2Rules()
    {
        var r3 = PdfDocumentPermissions.FromFlags(Restricted, 3, unrestricted: false);
        Assert.False(r3.CanPrint);
        Assert.False(r3.CanCopy);
        Assert.False(r3.CanAnnotate);
        Assert.True(r3.CanFillForms);               // bit 9 alone allows form filling (R ≥ 3)
        Assert.True(r3.CanExtractForAccessibility); // bit 10
        Assert.True(r3.CanAssemble);
        Assert.Contains("printing", r3.RestrictionSummary);

        var r2 = PdfDocumentPermissions.FromFlags(Restricted, 2, unrestricted: false);
        Assert.False(r2.CanFillForms);              // R2: form filling follows bit 6
        Assert.False(r2.CanExtractForAccessibility); // R2: follows bit 5
        Assert.True(PdfDocumentPermissions.FromFlags(0, 3, unrestricted: true).Unrestricted);
    }

    [Theory]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    public async Task UserPassword_Restricts_OwnerPassword_Unlocks(TestEncryptionKind scheme)
    {
        var enc = new PdfTestEncryption(scheme, "user", "owner", permissions: Restricted);
        string path = Write($"restricted-{scheme}.pdf", enc);

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path, "user");
        var p = service.Permissions;
        Assert.True(p.IsEncrypted);
        Assert.False(p.Unrestricted);
        Assert.False(p.CanPrint);
        Assert.False(p.CanCopy);

        using var owner = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await owner.OpenDocumentAsync(path, "owner");
        Assert.True(owner.Permissions.Unrestricted);
    }

    [Fact]
    public async Task ViewModel_RefusesRestrictedActions_AndExplains()
    {
        string path = Write("vm-restricted.pdf", new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner", permissions: Restricted));
        var vm = new MainViewModel();
        var alerts = new List<string>();
        vm.ShowMessageBoxAction = (message, _, _, _) => alerts.Add(message);
        bool printDialogShown = false;
        vm.ShowPrintDialogFunc = (_, _) => printDialogShown = true;

        await vm.LoadDocumentAsync(path);
        Assert.True(vm.IsSecured);
        Assert.Contains("printing", vm.SecuritySummary);

        vm.Print();
        Assert.False(printDialogShown);
        vm.CopySelectedText();
        vm.ToggleAnnotationTool("Highlight");
        Assert.Equal(3, alerts.Count);
        Assert.All(alerts, a => Assert.Contains("security settings do not allow", a));
        Assert.Null(vm.ActiveAnnotationTool);
    }

    [Fact]
    public async Task ViewModel_OwnerPassword_AllowsEverything()
    {
        string path = Write("vm-owner.pdf", new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner", permissions: Restricted));
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        bool printDialogShown = false;
        vm.ShowPrintDialogFunc = (_, _) => printDialogShown = true;
        await vm.LoadDocumentAsync(path, "owner");
        Assert.False(vm.IsSecured);
        vm.Print();
        Assert.True(printDialogShown);
    }

    [Fact]
    public async Task CertificateEncryptedDocument_OpensWithTheUsersCertificate()
    {
        var cert = PdfTestEncryption.CreateCertificate("Reader");
        PdfPublicKeySecurityHandler.CertificateSource = () => new[] { cert };
        string path = Write("pubsec.pdf", new PdfTestEncryption(TestEncryptionKind.PubSecS5Aes256, new[] { cert }, Restricted));
        byte[] original = File.ReadAllBytes(path);

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        var meta = await service.OpenDocumentAsync(path);
        Assert.Equal(1, meta.PageCount);
        Assert.True(service.IsDecryptedCopy);
        Assert.False(service.Permissions.CanPrint); // the recipient's permissions from the envelope
        Assert.NotNull(await service.RenderPageAsync(1, 72));
        Assert.Equal(PdfEngineMode.Vector, service.GetPageEngineReport(1)!.Engine);
        // PDFium works on the decrypted copy: text extraction and search are available.
        Assert.NotEmpty(await service.SearchTextAsync("Secured"));
        Assert.Equal(original, File.ReadAllBytes(path)); // the original is untouched
    }

    [Fact]
    public async Task CertificateEncryptedDocument_WithoutAMatchingCertificate_ExplainsWhy()
    {
        var recipient = PdfTestEncryption.CreateCertificate("Recipient");
        var stranger = PdfTestEncryption.CreateCertificate("Stranger");
        PdfPublicKeySecurityHandler.CertificateSource = () => new[] { stranger };
        string path = Write("pubsec-other.pdf", new PdfTestEncryption(TestEncryptionKind.PubSecS5Aes128, new[] { recipient }, -1));

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenDocumentAsync(path));
        Assert.Contains("certificates", ex.Message);
    }

    [Fact]
    public async Task ViewModel_NeverSavesADecryptedCopyOverTheOriginal()
    {
        var cert = PdfTestEncryption.CreateCertificate("Editor");
        PdfPublicKeySecurityHandler.CertificateSource = () => new[] { cert };
        string path = Write("pubsec-save.pdf", new PdfTestEncryption(TestEncryptionKind.PubSecS5Aes256, new[] { cert }, -1));
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path);
        Assert.True(vm.IsDocumentLoaded);
        vm.AddAnnotation(new PdfViewer.Models.AnnotationModel { PageNumber = 1, Type = PdfViewer.Models.AnnotationType.Highlight, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.05 });
        Assert.False(vm.CanSave());
    }
}
