using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;
using Kind = PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Standard security handler (ISO 32000-2 7.6.4) against PDFium: RC4-40 (R2), RC4-128 (R3, R4 crypt
/// filter), AES-128 (R4) and AES-256 (R6); user and owner passwords; strings that change rendering
/// (an Indexed lookup table), document info and unencrypted metadata.
/// </summary>
public class EncryptionTests
{
    private readonly ITestOutputHelper _output;
    public EncryptionTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<Kind> Schemes => new() { Kind.Rc4_40_R2, Kind.Rc4_128_R3, Kind.Rc4_128_R4, Kind.Aes128_R4, Kind.Aes256_R5, Kind.Aes256_R6 };

    private static byte[] Document(PdfTestEncryption? encryption, bool withMetadata = false)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int cs = b.Add("[/Indexed /DeviceRGB 2 <FF0000 00A040 2040FF>]");           // the lookup is a string: must be decrypted
        var pixels = Enumerable.Range(0, 16 * 16 * 3).Select(i => (byte)(i * 7)).ToArray();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels, flate: true);
        int info = b.Add("<< /Title (Confidential \\(draft\\)) /Author <4A616E65> >>");
        string catalogExtra = "";
        if (withMetadata)
        {
            int meta = b.AddStream("/Type /Metadata /Subtype /XML", "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'/><?xpacket end='w'?>");
            catalogExtra = $"/Metadata {meta} 0 R";
        }
        b.AddPage("/CS1 cs 0 sc 10 10 60 60 re f 1 sc 70 10 60 60 re f 2 sc 130 10 60 60 re f " +
                  "BT /F1 18 Tf 10 150 Td (Secret 123) Tj ET q 80 0 0 60 110 90 cm /Im1 Do Q",
            $"<< /Font << /F1 {font} 0 R >> /ColorSpace << /CS1 {cs} 0 R >> /XObject << /Im1 {image} 0 R >> >>");
        b.WithTrailer($"/Info {info} 0 R");
        if (catalogExtra.Length > 0) b.WithCatalog(catalogExtra);
        return b.Build(encryption);
    }

    private async Task AssertRendersLikePdfium(string name, byte[] pdf, string? password)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        Assert.True(doc.IsEncrypted);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
        Assert.Equal("Confidential (draft)", doc.Metadata.Title);

        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var v = result.Page;
        Assert.Empty(result.Fallbacks);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf, password);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"{name}: mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 2.0 && bad <= 0.02, $"{name}: mean {mean:F2} bad {bad:P2}");

        // The same document unencrypted renders identically (decryption is exact).
        using var plainDoc = await PdfVectorDocument.OpenAsync(Document(null));
        using var plain = await renderer.RenderDisplayListAsync(await plainDoc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 72 });
        var (pm, _) = DifferentialRenderingTests.Compare(v, plain);
        Assert.True(pm < 0.01, $"{name}: differs from the unencrypted document ({pm:F3})");
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task UserPassword_Decrypts_AndMatchesPdfium(Kind scheme)
    {
        var pdf = Document(new PdfTestEncryption(scheme, "user", "owner"));
        await AssertRendersLikePdfium($"{scheme} user", pdf, "user");
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task OwnerPassword_Decrypts(Kind scheme)
    {
        var pdf = Document(new PdfTestEncryption(scheme, "user", "owner"));
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: "owner");
        Assert.True(doc.Security!.IsOwner);
        await AssertRendersLikePdfium($"{scheme} owner", pdf, "owner");
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task MissingOrWrongPassword_IsRefused(Kind scheme)
    {
        var pdf = Document(new PdfTestEncryption(scheme, "user", "owner"));
        var missing = await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => await PdfVectorDocument.OpenAsync(pdf));
        Assert.True(missing.PasswordRequired);
        var wrong = await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => await PdfVectorDocument.OpenAsync(pdf, password: "guess"));
        Assert.True(wrong.PasswordRequired);
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task EmptyUserPassword_OpensWithoutPrompt(Kind scheme)
    {
        var pdf = Document(new PdfTestEncryption(scheme, "", "owner"));
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.False(doc.Security!.IsOwner);
        await AssertRendersLikePdfium($"{scheme} empty user", pdf, null);
    }

    [Theory]
    [InlineData(Kind.Aes128_R4)]
    [InlineData(Kind.Aes256_R6)]
    public async Task UnencryptedMetadata_IsReadAsIs(Kind scheme)
    {
        var pdf = Document(new PdfTestEncryption(scheme, "", "owner", encryptMetadata: false), withMetadata: true);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var catalogMeta = doc.Resolver.Resolve(doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"]) is Objects.PdfDictionary cat ? cat["Metadata"] : null)
            as Objects.PdfStream;
        Assert.NotNull(catalogMeta);
        string xmp = System.Text.Encoding.ASCII.GetString(catalogMeta!.GetRawBytes().Span);
        Assert.StartsWith("<?xpacket", xmp);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task MutatedEncryptedFiles_FailOnlyWithTypedErrors(Kind scheme)
    {
        var valid = Document(new PdfTestEncryption(scheme, "user", "owner"));
        var rng = new Random((int)scheme + 1);
        for (int i = 0; i < 60; i++)
        {
            var bytes = (byte[])valid.Clone();
            for (int f = 0; f < 1 + rng.Next(8); f++) bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
            try
            {
                using var doc = await PdfVectorDocument.OpenAsync(bytes, password: "user");
                for (int p = 1; p <= doc.PageCount; p++) await doc.GetPageDisplayListAsync(p);
            }
            catch (PdfVectorException) { }
        }
    }

    [Fact]
    public void Rc4_MatchesTheKnownVector()
    {
        // RFC 6229 / classic test vector: key "Key", plaintext "Plaintext".
        var c = PdfEngine.Vector.Security.PdfStandardSecurityHandler.Rc4("Key"u8.ToArray(), "Plaintext"u8.ToArray());
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(c));
    }
}
