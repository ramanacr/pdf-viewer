using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Long-term validation in the viewer: the signing option (off by default) that adds the data
/// right after signing, the panel's LTV line, and the panel command that asks before contacting
/// anyone. The revocation source is a fake built on an in-memory PKI; the HTTP source is tested
/// against a message handler that answers from memory. Nothing here touches the network.
/// </summary>
public class LongTermValidationViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LtvViewer_" + Guid.NewGuid().ToString("N"));
    private readonly TestPki _pki = new();

    public LongTermValidationViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        _pki.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, byte[] pdf)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    /// <summary>A view model whose revocation source is the fake, counting how often it was asked for.</summary>
    private async Task<(MainViewModel Vm, Counter Created)> Open(string path, FakeRevocationSource source)
    {
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        var created = new Counter();
        vm.RevocationSourceFactory = () => { created.Count++; return source; };
        await vm.LoadDocumentAsync(path);
        return (vm, created);
    }

    private sealed class Counter { public int Count; }

    [Fact]
    public async Task SigningWithoutTheOption_ContactsNobody()
    {
        string path = Write("plain.pdf", FormPdfFixture.Build());
        var source = new FakeRevocationSource(_pki);
        var (vm, created) = await Open(path, source);

        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(_pki.Signer, null, null, null, null, path)), vm.StatusText);
        await vm.ValidateSignaturesAsync();

        Assert.Equal(0, created.Count);
        Assert.Equal(0, source.Calls);
        var item = Assert.Single(vm.Signatures);
        Assert.Equal("Not LTV enabled", item.Ltv);
        Assert.Equal(PdfRevocationStatus.NotChecked, item.Check.Revocation);
    }

    [Fact]
    public async Task SigningWithTheOption_AddsTheData_AndThePanelSaysLtvEnabled()
    {
        string path = Write("contract.pdf", FormPdfFixture.Build());
        var source = new FakeRevocationSource(_pki);
        var (vm, created) = await Open(path, source);

        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(_pki.Signer, "Approved", null, null, null, path, AddLongTermValidation: true)), vm.StatusText);
        Assert.Equal(1, created.Count);
        Assert.Contains("Long-term validation data was added", vm.StatusText);

        byte[] saved = File.ReadAllBytes(path);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(saved));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.Equal(PdfLaterChanges.ValidationData, check.LaterChanges);
        Assert.True(check.IsLtvEnabled, check.LtvDetail);

        await vm.ValidateSignaturesAsync();
        var item = Assert.Single(vm.Signatures);
        Assert.Equal("LTV enabled", item.Ltv);
        Assert.Contains("Revocation:", item.Details);
    }

    [Fact]
    public async Task SigningWithTheOption_WhenTheAuthorityIsSilent_StillSaves_AndWarns()
    {
        string path = Write("silent.pdf", FormPdfFixture.Build());
        var source = new FakeRevocationSource(_pki) { Ocsp = false, Crls = false };
        var (vm, _) = await Open(path, source);
        string? warning = null;
        vm.ShowMessageBoxAction = (message, _, _, image) => { if (image == MessageBoxImage.Warning) warning = message; };

        Assert.True(await vm.SignAsync(new SignaturePlacement(1, new Rect(0.1, 0.8, 0.5, 0.12)),
            new SignatureOptions(_pki.Signer, null, null, null, null, path, AddLongTermValidation: true)), vm.StatusText);
        Assert.NotNull(warning);
        Assert.Contains("No revocation information could be obtained", warning);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(File.ReadAllBytes(path)));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.False(check.IsLtvEnabled);
    }

    [Fact]
    public async Task PanelCommand_AsksFirst_AndDoesNothingWhenDeclined()
    {
        string path = Write("signed.pdf", await SignedPdf());
        byte[] before = File.ReadAllBytes(path);
        var source = new FakeRevocationSource(_pki);
        var (vm, created) = await Open(path, source);
        await vm.ValidateSignaturesAsync();
        Assert.Equal("Not LTV enabled", Assert.Single(vm.Signatures).Ltv);

        string? asked = null;
        vm.ConfirmFunc = (message, _) => { asked = message; return false; };
        await vm.AddLongTermValidationCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Contains("certificate authorities", asked);
        Assert.Contains("never the document", asked);
        Assert.Equal(0, created.Count);
        Assert.Equal(0, source.Calls);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PanelCommand_WhenConfirmed_AddsTheDataInPlace()
    {
        string path = Write("signed.pdf", await SignedPdf());
        byte[] before = File.ReadAllBytes(path);
        var source = new FakeRevocationSource(_pki);
        var (vm, created) = await Open(path, source);
        await vm.ValidateSignaturesAsync();
        vm.ConfirmFunc = (_, _) => true;

        await vm.AddLongTermValidationCommand.ExecuteAsync(null);

        Assert.Equal(1, created.Count);
        byte[] after = File.ReadAllBytes(path);
        Assert.True(after.Length > before.Length);
        Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before), "the update is appended; the signed bytes are untouched");
        Assert.Contains("Long-term validation data was added", vm.StatusText);
        await vm.ValidateSignaturesAsync();
        var item = Assert.Single(vm.Signatures);
        Assert.Equal("LTV enabled", item.Ltv);
        Assert.Equal(PdfSignatureVerdict.Valid, item.Check.Verdict);
    }

    [Fact]
    public async Task PanelCommand_WithUnsavedChanges_AsksToSaveFirst()
    {
        string path = Write("signed.pdf", await SignedPdf());
        var source = new FakeRevocationSource(_pki);
        var (vm, created) = await Open(path, source);
        await vm.ValidateSignaturesAsync();
        vm.HasUnsavedChanges = true;
        bool asked = false;
        vm.ConfirmFunc = (_, _) => asked = true;
        await vm.AddLongTermValidationCommand.ExecuteAsync(null);
        Assert.False(asked);
        Assert.Equal(0, created.Count);
        Assert.NotNull(vm.WhyCannotAddValidationData());
    }

    [Fact]
    public async Task HttpSource_AsksTheCertificatesOwnAddresses_OnceEach()
    {
        var handler = new MemoryHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url == TestPki.Base + "ocsp/intermediate")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("application/ocsp-request", request.Content!.Headers.ContentType!.MediaType);
                var body = TestPki.Ocsp(_pki.Signer, _pki.Intermediate, _pki.Intermediate, PdfRevocationStatus.Good,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
                return Respond(body, "application/ocsp-response");
            }
            if (url == TestPki.Base + "intermediate.crl")
                return Respond(TestPki.Crl(_pki.Intermediate, Array.Empty<(X509Certificate2, DateTimeOffset, X509RevocationReason?)>(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), "application/pkix-crl");
            if (url == TestPki.Base + "intermediate.cer")
                return Respond(_pki.Intermediate.RawData, "application/pkix-cert");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var source = new HttpRevocationSource(handler);

        byte[]? ocsp = await source.GetOcspResponseAsync(_pki.Signer, _pki.Intermediate, CancellationToken.None);
        Assert.NotNull(ocsp);
        Assert.NotNull(PdfOcspResponse.TryParse(ocsp!)?.Find(_pki.Signer, _pki.Intermediate));
        Assert.Single(await source.GetCrlsAsync(_pki.Signer, _pki.Intermediate, CancellationToken.None));
        var issuer = Assert.Single(await source.GetIssuersAsync(_pki.Signer, CancellationToken.None));
        Assert.Equal(_pki.Intermediate.Thumbprint, issuer.Thumbprint);
        Assert.Equal(3, handler.Requests.Count);

        // The root names no addresses: nothing is asked.
        Assert.Null(await source.GetOcspResponseAsync(_pki.Root, _pki.Root, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task HttpSource_ReportsAFailure_WithoutRetrying()
    {
        var handler = new MemoryHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var source = new HttpRevocationSource(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetOcspResponseAsync(_pki.Signer, _pki.Intermediate, CancellationToken.None));
        Assert.Contains("pki.test", ex.Message);
        Assert.Single(handler.Requests); // one address, one attempt

        // Through the engine, a failing authority becomes a problem in the report, not an exception.
        var result = await PdfLtv.AddValidationDataAsync(await SignedPdf(), source);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Signatures[0].Problems, p => p.Contains("could not be fetched"));
    }

    private async Task<byte[]> SignedPdf()
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Grace Hopper", Rect = new PdfEngine.Geometry.PdfRect(150, 20, 140, 40) });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), _pki.Signer));
    }

    private static HttpResponseMessage Respond(byte[] body, string type)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class MemoryHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public MemoryHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_answer(request));
        }
    }
}
