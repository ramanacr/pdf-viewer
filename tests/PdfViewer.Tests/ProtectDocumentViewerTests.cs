using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfEngine.Vector;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Security;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Core.Security;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Protect with Password and Remove Security in the viewer: the protected copy goes through Save
/// As (the original only with confirmation), the passwords are dropped after the write, signed
/// documents need confirmation, Remove Security needs the owner password, and Document Properties
/// shows the method and permissions.
/// </summary>
public class ProtectDocumentViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ProtectViewer_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _alerts = new();
    private readonly List<string> _confirmations = new();

    public ProtectDocumentViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly PdfPermissionSet NoPrintNoCopy = PdfPermissionSet.All with { Printing = PdfPrintPermission.None, CopyContent = false };

    private string Write(string name, PdfTestEncryption? encryption = null, bool signed = false)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        if (signed) b.Add("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <3082> >>");
        b.AddPage("BT /F1 24 Tf 20 100 Td (Quarterly figures) Tj ET 0 0 1 rg 20 20 60 40 re f", $"<< /Font << /F1 {font} 0 R >> >>");
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build(encryption));
        return path;
    }

    private MainViewModel NewViewModel(bool confirm = true) => new()
    {
        ShowMessageBoxAction = (message, _, _, _) => _alerts.Add(message),
        ConfirmFunc = (message, _) => { _confirmations.Add(message); return confirm; },
    };

    private static ProtectDocumentOptions Options(string? open = "open", string? owner = "owner", PdfPermissionSet? permissions = null) => new()
    {
        OpenPassword = open,
        PermissionsPassword = owner,
        Permissions = permissions ?? NoPrintNoCopy,
    };

    [Fact]
    public async Task Protect_SavesAnEncryptedCopy_AndDropsThePasswords()
    {
        string path = Write("report.pdf");
        byte[] original = File.ReadAllBytes(path);
        string output = Path.Combine(_dir, "report_protected.pdf");
        var options = Options();
        var vm = NewViewModel();
        vm.ShowProtectDialogFunc = name => { Assert.Equal("report.pdf", name); return options; };
        string? suggested = null;
        vm.PickSavePathFunc = (s, _) => { suggested = s; return output; };
        await vm.LoadDocumentAsync(path);
        Assert.Equal("No security", vm.SecurityMethod);
        Assert.False(vm.CanRemoveSecurity);

        await vm.ProtectWithPasswordAsync();

        Assert.Equal(Path.Combine(_dir, "report_protected.pdf"), suggested);
        Assert.Null(options.OpenPassword);        // cleared as soon as the copy is written
        Assert.Null(options.PermissionsPassword);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(path, vm.Metadata!.FilePath); // the original stays open
        Assert.Contains(_alerts, a => a.Contains("protected copy is saved"));

        // The copy opens in the viewer's own service with the open password, restricted.
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await Assert.ThrowsAnyAsync<Exception>(() => service.OpenDocumentAsync(output));
        await service.OpenDocumentAsync(output, "open");
        Assert.False(service.Permissions.CanPrint);
        Assert.False(service.Permissions.CanCopy);
        Assert.True(service.Permissions.CanAnnotate);
        using var owner = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await owner.OpenDocumentAsync(output, "owner");
        Assert.True(owner.Permissions.Unrestricted);
    }

    [Fact]
    public async Task Protect_OverTheOriginal_OnlyWhenConfirmed()
    {
        string path = Write("inplace.pdf");
        byte[] original = File.ReadAllBytes(path);

        var declined = NewViewModel(confirm: false);
        declined.ShowProtectDialogFunc = _ => Options();
        declined.PickSavePathFunc = (_, _) => path;
        await declined.LoadDocumentAsync(path);
        await declined.ProtectWithPasswordAsync();
        Assert.Contains(_confirmations, c => c.Contains("Replace the original"));
        Assert.Equal(original, File.ReadAllBytes(path));

        var accepted = NewViewModel();
        accepted.ShowProtectDialogFunc = _ => Options();
        accepted.PickSavePathFunc = (_, _) => path;
        string? asked = null;
        accepted.RequestPasswordFunc = name => { asked = name; return Task.FromResult<string?>("open"); };
        await accepted.LoadDocumentAsync(path);
        await accepted.ProtectWithPasswordAsync();
        Assert.NotEqual(original, File.ReadAllBytes(path));
        // Reopened from disk, which now asks for the new open password.
        Assert.Equal("inplace.pdf", asked);
        Assert.True(accepted.IsDocumentLoaded);
        Assert.True(accepted.IsSecured);
        Assert.Equal("Password security, AES-256", accepted.SecurityMethod);
        Assert.False(accepted.CanRemoveSecurity); // opened with the open password only
    }

    [Fact]
    public async Task Protect_SignedDocument_WarnsAndNeedsConfirmation()
    {
        string path = Write("signed.pdf", signed: true);
        string output = Path.Combine(_dir, "signed_protected.pdf");

        var declined = NewViewModel(confirm: false);
        await declined.LoadDocumentAsync(path);
        Assert.False(await declined.ProtectToAsync(Options(), output));
        Assert.Contains(_confirmations, c => c.Contains("1 digital signature(s)") && c.Contains("no longer be valid"));
        Assert.False(File.Exists(output));

        var accepted = NewViewModel();
        await accepted.LoadDocumentAsync(path);
        Assert.True(await accepted.ProtectToAsync(Options(), output));
        Assert.Contains(_alerts, a => a.Contains("1 signature(s) in the copy are no longer valid"));
        using var doc = await PdfVectorDocument.OpenAsync(output, password: "open");
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public async Task Protect_AnEncryptedDocument_NeedsItsPermissionsPassword()
    {
        string path = Write("locked.pdf", new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "user", "owner", permissions: NoPrintNoCopy.ToFlags()));
        string output = Path.Combine(_dir, "locked_protected.pdf");
        var vm = NewViewModel();
        vm.RequestPasswordFunc = _ => Task.FromResult<string?>("user");
        vm.ShowProtectDialogFunc = _ => Options("new", "newowner", PdfPermissionSet.All);
        vm.PickSavePathFunc = (_, _) => output;
        await vm.LoadDocumentAsync(path);
        Assert.False(vm.IsOwnerPasswordKnown);

        vm.RequestOwnerPasswordFunc = _ => "user"; // the open password is not the permissions password
        await vm.ProtectWithPasswordAsync();
        Assert.False(File.Exists(output));
        Assert.Contains(_alerts, a => a.Contains("permissions password is incorrect"));

        vm.RequestOwnerPasswordFunc = _ => "owner";
        await vm.ProtectWithPasswordAsync();
        using var doc = await PdfVectorDocument.OpenAsync(output, password: "new");
        Assert.Equal("AES-256", doc.Security!.Algorithm);
    }

    [Fact]
    public async Task RemoveSecurity_IsOnlyOfferedWithTheOwnerPassword()
    {
        string path = Write("secret.pdf", new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner", permissions: NoPrintNoCopy.ToFlags()));
        string output = Path.Combine(_dir, "secret_unsecured.pdf");

        var reader = NewViewModel();
        reader.RequestPasswordFunc = _ => Task.FromResult<string?>("user");
        await reader.LoadDocumentAsync(path);
        Assert.True(reader.DocumentPermissions.IsEncrypted);
        Assert.False(reader.CanRemoveSecurity);

        var author = NewViewModel();
        author.RequestPasswordFunc = _ => Task.FromResult<string?>("owner");
        author.PickSavePathFunc = (_, _) => output;
        await author.LoadDocumentAsync(path);
        Assert.True(author.IsOwnerPasswordKnown);
        Assert.True(author.CanRemoveSecurity);
        await author.RemoveSecurityAsync();
        Assert.Contains(_confirmations, c => c.Contains("Remove the password"));
        using var doc = await PdfVectorDocument.OpenAsync(output);
        Assert.False(doc.IsEncrypted);
        using var engine = new PdfEngine.Pdfium.PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(output);
        Assert.Contains("Quarterly figures", await engine.TextService.ExtractPageTextAsync(pdoc, 1));
    }

    [Fact]
    public async Task RemoveSecurity_WithoutAnOpenPassword_AsksForThePermissionsPassword()
    {
        string path = Write("open.pdf", new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner", permissions: NoPrintNoCopy.ToFlags()));
        string output = Path.Combine(_dir, "open_unsecured.pdf");
        var vm = NewViewModel();
        vm.PickSavePathFunc = (_, _) => output;
        await vm.LoadDocumentAsync(path);
        Assert.False(vm.IsOwnerPasswordKnown);
        Assert.True(vm.CanRemoveSecurity);

        vm.RequestOwnerPasswordFunc = _ => null;
        await vm.RemoveSecurityAsync();
        vm.RequestOwnerPasswordFunc = _ => "wrong";
        await vm.RemoveSecurityAsync();
        Assert.False(File.Exists(output));
        Assert.Contains(_alerts, a => a.Contains("needs its permissions password"));
        Assert.Contains(_alerts, a => a.Contains("incorrect"));

        vm.RequestOwnerPasswordFunc = _ => "owner";
        await vm.RemoveSecurityAsync();
        using var doc = await PdfVectorDocument.OpenAsync(output);
        Assert.False(doc.IsEncrypted);
    }

    [Fact]
    public async Task DocumentProperties_ShowTheSecurityMethodAndPermissions()
    {
        string path = Write("props.pdf", new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "", "owner", permissions: NoPrintNoCopy.ToFlags()));
        var vm = NewViewModel();
        DocumentMetadata? shown = null;
        vm.ShowPropertiesAction = m => shown = m;
        await vm.LoadDocumentAsync(path);
        vm.ShowProperties();
        Assert.NotNull(shown);
        Assert.Equal("Password security, AES-256", shown!.SecurityMethod);
        Assert.Contains("Printing: Not allowed", shown.SecurityPermissions);
        Assert.Contains("Content copying: Not allowed", shown.SecurityPermissions);
        Assert.Contains("Commenting: Allowed", shown.SecurityPermissions);
        Assert.Contains("Page assembly: Allowed", shown.SecurityPermissions);

        var plain = NewViewModel();
        plain.ShowPropertiesAction = m => shown = m;
        await plain.LoadDocumentAsync(Write("plain.pdf"));
        plain.ShowProperties();
        Assert.Equal("No security", shown!.SecurityMethod);
    }

    [Theory]
    [InlineData(false, "", "", false, "", "", "Require an open password")]
    [InlineData(true, "", "", false, "", "", "Type the open password")]
    [InlineData(true, "a", "b", false, "", "", "do not match")]
    [InlineData(false, "", "", true, "", "", "permissions password")]
    [InlineData(false, "", "", true, "o", "x", "do not match")]
    [InlineData(true, "same", "same", true, "same", "same", "must differ")]
    [InlineData(true, "bad\u0007", "bad\u0007", false, "", "", "not allowed")]
    [InlineData(true, "fine", "fine", true, "owner", "owner", null)]
    [InlineData(true, "Пароль", "Пароль", false, "", "", null)]
    public void ProtectDialogRules(bool requireOpen, string open, string openConfirm, bool restrict, string owner, string ownerConfirm, string? expected)
    {
        string? error = ProtectPasswordRules.Validate(requireOpen, open, openConfirm, restrict, owner, ownerConfirm, PdfEncryptionStrength.Aes256);
        if (expected == null) Assert.Null(error);
        else Assert.Contains(expected, error);
        Assert.Contains("AES-128", ProtectPasswordRules.Validate(true, "Пароль", "Пароль", false, "", "", PdfEncryptionStrength.Aes128));
    }
}
