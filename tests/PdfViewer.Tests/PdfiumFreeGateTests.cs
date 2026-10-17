using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
#if PDFIUM_RENDERER
using PdfEngine.Pdfium;
#endif
using PdfEngine.Vector;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Core.Security;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The PDFium-free release gate (M10): with PDFium disabled, the viewer opens, views, searches,
/// selects, annotates, fills forms, imports and exports form data, signs, validates, redacts,
/// makes a clean copy and saves in Auto mode. In a developer build the scope is PdfiumGuard's
/// (every PDFium initialisation, engine and document load passes it), so any PDFium attempt,
/// even one a caller catches, is recorded and fails the test. In the PDFium-free build
/// (PdfiumRenderer=false) there is no PDFium to call; the scope then fails the test if any of
/// PDFium was loaded into the process.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class PdfiumFreeGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PdfiumFree_" + Guid.NewGuid().ToString("N"));

    public PdfiumFreeGateTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel();
        Assert.Equal(PdfEngineMode.Auto, vm.CurrentEngineMode);
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path);
        Assert.True(vm.IsDocumentLoaded, vm.StatusText);
        return vm;
    }

#if PDFIUM_RENDERER
    [Fact]
    public void TheGuard_RefusesAndRecordsPdfium()
    {
        using var scope = PdfiumGuard.DisableInThisFlow();
        Assert.Throws<PdfiumDisabledException>(() => new PdfiumEngine());
        Assert.Throws<PdfiumDisabledException>(() => new PdfiumDocumentService());
        Assert.Equal(new[] { "PdfiumEngine", "PdfiumDocumentService" }, scope.Attempts);
    }
#endif

    [Fact]
    public async Task ViewingSearchingAnnotatingRedactingAndSaving_NeverReachPdfium()
    {
        string path = TestPdfBuilder.CreateSimplePdf(P("text.pdf"), 3, "GateToken");
        using var scope = NoPdfium.Scope();

        var vm = await Open(path);
        Assert.Equal(3, vm.PageCount);
        Assert.NotNull(vm.DocumentSafety);
        Assert.False(vm.DocumentSafety!.InspectionWasLimited, vm.DocumentSafety.LimitationReason);

        // Text: search, words and selection layout, read-mode content.
        vm.SearchQuery = "GateToken";
        await vm.ExecuteSearchAsync();
        Assert.NotEmpty(vm.SearchMatches);
        using (var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto))
        {
            await service.OpenDocumentAsync(path);
            Assert.NotNull(await service.RenderPageAsync(1, 72));
            Assert.NotEmpty(await service.SearchTextAsync("GateToken"));
            Assert.NotEmpty(await service.ExtractPageTextSegmentsAsync(1));
            Assert.NotEmpty(service.ExtractPageTextSegments(2));
            Assert.Contains("GateToken", string.Concat((await service.ExtractPageTextLayoutAsync(1)).Glyphs.Select(g => g.Text)));
            await service.ExtractAccessibleContentAsync(1);
            Assert.NotEmpty(service.ExtractBookmarks());
            Assert.NotNull(service.GetMetadata());
            Assert.False(service.IsPdfiumDocumentOpen);
        }

        // Annotate and save in place.
        vm.AddAnnotation(new AnnotationModel { PageNumber = 1, Type = AnnotationType.Highlight, X = 0.1, Y = 0.1, Width = 0.3, Height = 0.05, ColorHex = "#FFFF00", Opacity = 0.4, Contents = "gate" });
        await vm.SaveAsync();
        Assert.False(vm.HasUnsavedChanges, vm.StatusText);

        // A clean copy, and redaction of the search matches.
        var clean = await vm.SaveCleanCopyToAsync(P("text.clean.pdf"));
        Assert.Equal(3, clean.PageCount);
        vm.MarkSearchResultsForRedactionCommand.Execute(null);
        Assert.NotNull(await vm.ApplyRedactionsToAsync(new RedactionApplyOptions(P("text.redacted.pdf"), "Redacted", RemoveMetadata: false)));
        await vm.ValidateSignaturesAsync();

        Assert.Empty(scope.Attempts);
    }

    [Fact]
    public async Task FormFillingFormDataSigningAndSaving_NeverReachPdfium()
    {
        string path = P("form.pdf");
        File.WriteAllBytes(path, FormPdfFixture.Build());
        using var scope = NoPdfium.Scope();

        var vm = await Open(path);
        Assert.True(vm.HasForm);
        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.True(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "Ada Lovelace")));

        // Form data out and back in, reset, flatten (no script is run by any of them).
        Assert.True(await vm.ExportFormDataToAsync(P("form.xfdf")));
        Assert.True(await vm.ExportFormDataToAsync(P("form.fdf")));
        await vm.ResetFormAsync();
        Assert.Equal(string.Empty, vm.AllFormFields.Single(f => f.FullName == "person.name").Value);
        var imported = await vm.ImportFormDataFromAsync(P("form.fdf"));
        Assert.NotNull(imported);
        Assert.Equal("Ada Lovelace", vm.AllFormFields.Single(f => f.FullName == "person.name").Value);
        Assert.True(await vm.FlattenFormToAsync(P("form.flat.pdf")));
        await vm.SaveAsync();

        // Sign, then validate the signature.
        using var cert = Certificate("Gate Signer");
        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(cert, "Approve", "Here", null, null, path)), vm.StatusText);
        await vm.ValidateSignaturesAsync();
        Assert.True(vm.HasSignatures);

        Assert.Empty(scope.Attempts);
    }

    [Fact]
    public async Task OpeningEncryptedAndDamagedFiles_NeverReachesPdfium()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 20 20 160 160 re f");
        string encrypted = P("locked.pdf");
        File.WriteAllBytes(encrypted, b.Build(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "secret", "owner")));
        string damaged = P("damaged.pdf");
        byte[] good = new VectorPdfBuilder().AddPageAndBuild();
        File.WriteAllBytes(damaged, good.Take(good.Length - 40).ToArray()); // the cross-reference table cut off

        using var scope = NoPdfium.Scope();
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        var prompt = await Assert.ThrowsAsync<PdfEngine.Exceptions.PdfPasswordRequiredException>(() => service.OpenDocumentAsync(encrypted));
        Assert.Contains("password", prompt.Message, StringComparison.OrdinalIgnoreCase);
        await service.OpenDocumentAsync(encrypted, "secret");
        Assert.True(service.Permissions.IsEncrypted);
        Assert.NotNull(await service.RenderPageAsync(1, 72));

        await service.OpenDocumentAsync(damaged);
        Assert.Equal(1, service.PageCount);
        Assert.Empty(scope.Attempts);
    }
}

/// <summary>The gate's scope for the build under test (see <see cref="PdfiumFreeGateTests"/>).</summary>
internal static class NoPdfium
{
    public interface IScope : IDisposable
    {
        /// <summary>What reached PDFium while the scope was active: nothing, if the gate holds.</summary>
        IReadOnlyList<string> Attempts { get; }
    }

    public static IScope Scope() => new GateScope();

#if PDFIUM_RENDERER
    private sealed class GateScope : IScope
    {
        private readonly PdfiumGuard.Scope _guard = PdfiumGuard.DisableInThisFlow();
        public IReadOnlyList<string> Attempts => _guard.Attempts;
        public void Dispose() => _guard.Dispose();
    }
#else
    private sealed class GateScope : IScope
    {
        /// <summary>Any of PDFium in this process: its adapter assembly or its native library.</summary>
        public IReadOnlyList<string> Attempts
        {
            get
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetName().Name ?? string.Empty)
                    .Where(n => n.Contains("Pdfium", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                foreach (System.Diagnostics.ProcessModule module in self.Modules)
                    if (module.ModuleName.Contains("pdfium", StringComparison.OrdinalIgnoreCase)) loaded.Add(module.ModuleName);
                return loaded;
            }
        }

        public void Dispose() { }
    }
#endif
}

internal static class GateBuilderExtensions
{
    public static byte[] AddPageAndBuild(this VectorPdfBuilder b)
    {
        b.AddPage("0 0 1 rg 20 20 160 160 re f");
        return b.Build();
    }
}
