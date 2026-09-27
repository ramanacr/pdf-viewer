using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// A password-protected form in the viewer: filled (each commit an encrypted revision both
/// engines reload with the password), saved, signed and re-opened.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class EncryptedFormViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EncryptedFormViewer_" + Guid.NewGuid().ToString("N"));

    public EncryptedFormViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<MainViewModel> Open(string path, string password)
    {
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path, password);
        return vm;
    }

    private const int AllowEverything = unchecked((int)0xFFFFFFFC);

    [Fact]
    public async Task FormFillingForbiddenByPermissions_IsRefused()
    {
        // The usual writer's /P: printing and copying allowed, forms and changes not.
        string path = Path.Combine(_dir, "locked-form.pdf");
        File.WriteAllBytes(path, FormPdfFixture.Build(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "user", "owner")));
        byte[] before = File.ReadAllBytes(path);
        var vm = await Open(path, "user");
        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.False(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "x")));
        Assert.Contains("do not allow filling in forms", vm.StatusText);
        Assert.NotNull(vm.WhyCannotSign());
        Assert.Equal(before, File.ReadAllBytes(path));

        // The owner password lifts the restriction.
        var owner = await Open(path, "owner");
        Assert.True(await owner.CommitFormFieldAsync(owner.AllFormFields.Single(f => f.FullName == "person.name"),
            new PdfFieldChange("person.name", Text: "Owner")), owner.StatusText);
    }

    [Theory]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    public async Task EncryptedForm_IsFilledSavedAndSigned(TestEncryptionKind scheme)
    {
        string path = Path.Combine(_dir, "secret-form.pdf");
        File.WriteAllBytes(path, FormPdfFixture.Build(new PdfTestEncryption(scheme, "user", "owner", permissions: AllowEverything)));
        var vm = await Open(path, "user");
        Assert.True(vm.HasForm);
        Assert.Null(vm.FormReadOnlyReason);

        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.True(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "Grace Hopper")), vm.StatusText);
        // A second commit reloads the first encrypted revision: the password still opens it.
        var city = vm.AllFormFields.Single(f => f.FullName == "city");
        Assert.True(await vm.CommitFormFieldAsync(city, new PdfFieldChange("city", Choice: "ROM")), vm.StatusText);
        await vm.SaveAsync();
        Assert.False(vm.HasUnsavedChanges);
        Assert.DoesNotContain("Grace Hopper", Encoding.Latin1.GetString(File.ReadAllBytes(path)));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Ada Lovelace", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.5, 0.85, 0.4, 0.1)),
            new SignatureOptions(cert, "Approved", null, null, null, path)), vm.StatusText);

        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(File.ReadAllBytes(path), password: "user"));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);

        var reopened = await Open(path, "user");
        Assert.Equal("Grace Hopper", reopened.AllFormFields.Single(f => f.FullName == "person.name").Value);
        Assert.Equal("ROM", reopened.AllFormFields.Single(f => f.FullName == "city").Value);
        await reopened.ValidateSignaturesAsync();
        Assert.Single(reopened.Signatures);
    }
}
