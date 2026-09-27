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
using System.Windows.Ink;
using System.Windows.Input;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// PAdES-B-LTA and signature pictures in the viewer: the signing options that add a document
/// timestamp (off unless asked for, and only to the authority the user named), the panel command
/// that asks before timestamping or renewing, the panel's level line, the HTTP client against an
/// in-memory handler, and the signature picture (from a file or drawn), remembered only when the
/// user opts in, in their profile and never in the document. Nothing touches the network.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class SigningLtaViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LtaViewer_" + Guid.NewGuid().ToString("N"));
    private readonly TestPki _pki = new();
    private readonly X509Certificate2 _tsaCert;
    private static readonly Uri Tsa = new("http://tsa.test/stamp");

    public SigningLtaViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
        _tsaCert = _pki.Issue("Test Time Authority", eku: "1.3.6.1.5.5.7.3.8");
    }

    public void Dispose()
    {
        _tsaCert.Dispose();
        _pki.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, byte[] pdf)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    private sealed class Counter { public int Count; public List<Uri> Servers { get; } = new(); }

    // The last message the view model showed, so a failure says why.
    private string? _lastAlert;

    /// <summary>A view model whose authorities are fakes, counting how often each was created.</summary>
    private async Task<(MainViewModel Vm, Counter Tsas, Counter Sources, FakeTimestampAuthority Tsa)> Open(string path)
    {
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (message, _, _, _) => _lastAlert = message;
        var tsa = new FakeTimestampAuthority(_tsaCert);
        tsa.Chain.Add(TestPki.Public(_pki.Intermediate));
        var tsas = new Counter();
        var sources = new Counter();
        vm.TimestampClientFactory = server => { tsas.Count++; tsas.Servers.Add(server); return tsa; };
        vm.RevocationSourceFactory = () => { sources.Count++; return new FakeRevocationSource(_pki); };
        await vm.LoadDocumentAsync(path);
        return (vm, tsas, sources, tsa);
    }

    private X509Certificate2Collection Roots => new(TestPki.Public(_pki.Root));

    private static readonly SignaturePlacement Box = new(1, new Rect(0.1, 0.8, 0.5, 0.12));

    [Fact]
    public async Task SigningWithADocumentTimestamp_AndValidationData_IsBaselineLta()
    {
        string path = Write("contract.pdf", FormPdfFixture.Build());
        var (vm, tsas, sources, tsa) = await Open(path);

        Assert.True(await vm.SignAsync(Box, new SignatureOptions(_pki.Signer, "Approved", null, null, Tsa, path,
            AddLongTermValidation: true, AddDocumentTimestamp: true)), $"{vm.StatusText} {_lastAlert}");
        Assert.Contains("A document timestamp was added", vm.StatusText);
        Assert.All(tsas.Servers, s => Assert.Equal(Tsa, s)); // only the authority the user named
        Assert.Equal(2, tsa.Calls);                             // the signature's timestamp, then the document's
        Assert.Equal(1, sources.Count);

        var checks = await PdfSignatureValidator.ValidateAsync(File.ReadAllBytes(path), roots: Roots);
        Assert.Equal(2, checks.Count);
        Assert.Equal(PdfSignatureVerdict.Valid, checks[0].Verdict);
        Assert.Equal(PdfSignatureLevel.BaselineLTA, checks[0].Level);
        Assert.True(checks[1].IsDocumentTimestamp);

        await vm.ValidateSignaturesAsync();
        Assert.Equal(2, vm.Signatures.Count);
        var stamp = vm.Signatures[1];
        Assert.Equal("Document timestamp", stamp.Title);
        Assert.StartsWith("Timestamped ", stamp.When);
        Assert.Contains("Test Time Authority", stamp.When);
        Assert.StartsWith("PAdES B-", vm.Signatures[0].PadesLevel);
    }

    [Fact]
    public async Task SigningWithoutTheOption_AsksNoAuthorityForADocumentTimestamp()
    {
        string path = Write("plain.pdf", FormPdfFixture.Build());
        var (vm, tsas, sources, _) = await Open(path);
        Assert.True(await vm.SignAsync(Box, new SignatureOptions(_pki.Signer, null, null, null, null, path)), vm.StatusText);
        Assert.Equal(0, tsas.Count);
        Assert.Equal(0, sources.Count);
        await vm.ValidateSignaturesAsync();
        var item = Assert.Single(vm.Signatures);
        Assert.Equal("PAdES B-B", item.PadesLevel);
        Assert.Contains("PAdES B-B:", item.Details);
    }

    [Fact]
    public async Task PanelCommand_AsksFirst_ThenTimestampsInPlace_AndRenews()
    {
        new SigningSettings { TimestampServer = Tsa.AbsoluteUri, UseTimestamp = true }.Save();
        string path = Write("signed.pdf", await SignedPdf());
        var (vm, tsas, sources, tsa) = await Open(path);
        await vm.ValidateSignaturesAsync();

        string? asked = null;
        vm.ConfirmFunc = (message, _) => { asked = message; return false; };
        byte[] before = File.ReadAllBytes(path);
        await vm.AddDocumentTimestampCommand.ExecuteAsync(null);
        Assert.NotNull(asked);
        Assert.Contains("tsa.test", asked);
        Assert.Contains("never the document", asked);
        Assert.Equal(0, tsas.Count + sources.Count);
        Assert.Equal(before, File.ReadAllBytes(path));

        vm.ConfirmFunc = (_, _) => true;
        await vm.AddDocumentTimestampCommand.ExecuteAsync(null);
        byte[] once = File.ReadAllBytes(path);
        Assert.True(once.AsSpan(0, before.Length).SequenceEqual(before), "the update is appended; the signed bytes are untouched");
        Assert.Contains("document timestamp", vm.StatusText);
        Assert.Equal(1, tsa.Calls);
        var checks = await PdfSignatureValidator.ValidateAsync(once, roots: Roots);
        Assert.Equal(PdfSignatureLevel.BaselineLTA, checks[0].Level);

        // Renewing: a second timestamp over the first and its validation data.
        await vm.AddDocumentTimestampCommand.ExecuteAsync(null);
        var renewed = await PdfSignatureValidator.ValidateAsync(File.ReadAllBytes(path), roots: Roots);
        Assert.Equal(3, renewed.Count);
        Assert.True(renewed[1].IsArchiveTimestamped);
    }

    [Fact]
    public async Task PanelCommand_WithoutATimestampServer_ExplainsAndContactsNobody()
    {
        string path = Write("signed.pdf", await SignedPdf());
        var (vm, tsas, sources, _) = await Open(path);
        await vm.ValidateSignaturesAsync();
        string? alert = null;
        vm.ShowMessageBoxAction = (message, _, _, _) => alert = message;
        bool asked = false;
        vm.ConfirmFunc = (_, _) => asked = true;
        await vm.AddDocumentTimestampCommand.ExecuteAsync(null);
        Assert.False(asked);
        Assert.Contains("No timestamp server", alert);
        Assert.Equal(0, tsas.Count + sources.Count);
    }

    [Fact]
    public async Task HttpClient_PostsTheQuery_ToTheChosenServer_AndChecksTheReply()
    {
        var tsa = new FakeTimestampAuthority(_tsaCert);
        var handler = new MemoryHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Tsa, request.RequestUri);
            Assert.Equal("application/timestamp-query", request.Content!.Headers.ContentType!.MediaType);
            byte[] body = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var content = new ByteArrayContent(tsa.Respond(body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-reply");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpTimestampClient(Tsa, handler);
        var result = await PdfDocumentTimestamp.AddAsync(FormPdfFixture.Build(), client);
        Assert.Single(handler.Requests);
        Assert.Equal("Test Time Authority", result.Authority);

        using var wrong = new HttpTimestampClient(Tsa, new MemoryHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", System.Text.Encoding.UTF8, "text/html") }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => wrong.RequestAsync(new byte[] { 0x30, 0x00 }, CancellationToken.None));
        Assert.Contains("did not return a timestamp", ex.Message);
        Assert.Throws<ArgumentException>(() => new HttpTimestampClient(new Uri("ftp://tsa.test/")));
    }

    [Fact]
    public async Task SigningWithAPicture_DrawsItInTheWidget_AndTheDocumentHoldsNoPathToIt()
    {
        string path = Write("picture.pdf", FormPdfFixture.Build());
        var (vm, _, _, _) = await Open(path);
        var appearance = new PdfSignatureAppearance { Layout = PdfSignatureLayout.ImageAndText, Image = Stroke() };
        Assert.True(await vm.SignAsync(Box, new SignatureOptions(_pki.Signer, "Approved", null, null, null, path, Appearance: appearance)), vm.StatusText);

        byte[] saved = File.ReadAllBytes(path);
        using var doc = await PdfVectorDocument.OpenAsync(saved);
        var (field, _, _) = PdfSignatureValidator.SignedFields(doc).Single();
        var widget = (PdfDictionary)doc.Resolver.Resolve(field.Widgets[0].ObjectNumber)!;
        var ap = (PdfStream)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(widget["AP"])!)["N"])!;
        var resources = (PdfDictionary)doc.Resolver.Resolve(ap.Dictionary["Resources"])!;
        var image = (PdfStream)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(resources["XObject"])!)["SigImg"])!;
        Assert.NotNull(image.Dictionary["SMask"]);
        // The viewer embeds the text font (it passes the installed fonts).
        var fonts = (PdfDictionary)doc.Resolver.Resolve(resources["Font"])!;
        Assert.All(fonts.Entries.Values, f => Assert.Equal("Type0", ((PdfDictionary)doc.Resolver.Resolve(f)!).GetName("Subtype")));
        Assert.DoesNotContain("signature-image", System.Text.Encoding.Latin1.GetString(saved));
        Assert.DoesNotContain(_dir.Replace('\\', '/'), System.Text.Encoding.Latin1.GetString(saved).Replace('\\', '/'));
        Assert.True(Assert.Single(await PdfSignatureValidator.ValidateAsync(saved)).IntegrityValid);
    }

    [Fact]
    public void SignatureImage_IsRememberedOnlyWhenAsked_InTheUsersProfile()
    {
        Assert.Null(SignatureImageStore.LoadRemembered());
        Assert.StartsWith(_dir, SignatureImageStore.Location);
        Assert.False(new SigningSettings().RememberSignatureImage); // off by default

        byte[] png = RunSta(() =>
        {
            var strokes = new StrokeCollection
            {
                new Stroke(new StylusPointCollection(new[] { new StylusPoint(0, 0), new StylusPoint(60, 20), new StylusPoint(120, 5) })),
            };
            return SignatureImageStore.PngFromStrokes(strokes)!;
        });
        var image = RunSta(() => SignatureImageStore.ToImage(png));
        Assert.True(image.Width > 300, "drawn at three times its size");
        Assert.NotNull(image.Alpha);                                      // the background is transparent...
        Assert.Contains(image.Alpha!, a => a == 0);
        Assert.Contains(image.Alpha!, a => a == 255);                     // ...and the ink opaque

        SignatureImageStore.Remember(png);
        Assert.Equal(png, SignatureImageStore.LoadRemembered());
        SignatureImageStore.Forget();
        Assert.Null(SignatureImageStore.LoadRemembered());
        Assert.False(File.Exists(SignatureImageStore.Location));

        Assert.Null(RunSta(() => SignatureImageStore.PngFromStrokes(new StrokeCollection())));
    }

    [Fact]
    public void SignatureImage_FromAFile_KeepsItsTransparency_AndIsScaledDown()
    {
        string file = Path.Combine(_dir, "scan.png");
        RunSta(() =>
        {
            var pixels = new byte[2400 * 600 * 4];
            for (int y = 0; y < 600; y++)
                for (int x = 0; x < 2400; x++)
                    if (Math.Abs(y - x / 4) < 30)
                    {
                        int o = (y * 2400 + x) * 4;
                        pixels[o] = 120; pixels[o + 3] = 255; // blue ink, else transparent
                    }
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2400, 600, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 2400 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var fs = File.Create(file);
            encoder.Save(fs);
            return 0;
        });
        var image = RunSta(() => SignatureImageStore.ToImage(SignatureImageStore.PngFromFile(file)));
        Assert.Equal(SignatureImageStore.MaxSide, image.Width);
        Assert.Equal(300, image.Height);
        Assert.NotNull(image.Alpha);
    }

    [Fact]
    public void Dialog_OffersTheAppearances_WithRememberingAndTheDocumentTimestampOffByDefault()
    {
        RunSta(() =>
        {
            var dialog = new PdfViewer.Views.Dialogs.SignDocumentDialog(Box, Path.Combine(_dir, "x.pdf"));
            try
            {
                Assert.Equal(4, dialog.LayoutCombo.Items.Count);
                Assert.Equal(Visibility.Collapsed, dialog.ImagePanel.Visibility);
                Assert.False(dialog.RememberImageCheck.IsChecked);
                Assert.False(dialog.DocTimestampCheck.IsEnabled); // needs a timestamp server first
                dialog.LayoutCombo.SelectedIndex = 2;             // picture and details
                Assert.Equal(Visibility.Visible, dialog.ImagePanel.Visibility);
                Assert.Null(dialog.ImagePreview.Source);
                dialog.TimestampCheck.IsChecked = true;
                Assert.True(dialog.DocTimestampCheck.IsEnabled);
                Assert.False(dialog.DocTimestampCheck.IsChecked);
            }
            finally { dialog.Close(); }

            // A remembered picture comes back, only when remembering was asked for.
            var strokes = new StrokeCollection { new Stroke(new StylusPointCollection(new[] { new StylusPoint(0, 0), new StylusPoint(50, 30) })) };
            SignatureImageStore.Remember(SignatureImageStore.PngFromStrokes(strokes)!);
            new SigningSettings { RememberSignatureImage = true, AppearanceLayout = nameof(PdfSignatureLayout.ImageOnly) }.Save();
            var again = new PdfViewer.Views.Dialogs.SignDocumentDialog(Box, Path.Combine(_dir, "x.pdf"));
            try
            {
                Assert.Equal(3, again.LayoutCombo.SelectedIndex);
                Assert.NotNull(again.ImagePreview.Source);
                Assert.True(again.RememberImageCheck.IsChecked);
            }
            finally { again.Close(); }
            return 0;
        });
    }

    private static PdfImageContent Stroke()
    {
        const int w = 90, h = 30;
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++) { rgb[i * 3 + 2] = 150; alpha[i] = Math.Abs(i / w - i % w / 3) < 4 ? (byte)255 : (byte)0; }
        return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, alpha);
    }

    private async Task<byte[]> SignedPdf()
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Grace Hopper", Rect = new PdfEngine.Geometry.PdfRect(150, 20, 140, 40) });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), _pki.Signer));
    }

    private static T RunSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null) throw error;
        return result;
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
