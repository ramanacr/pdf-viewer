using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Optimize;
using PdfEngine.Vector.Security;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Kind = PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Reduce File Size on encrypted documents: the copy is smaller, encrypted with the same file key
/// and encryption dictionary (revision, cipher, permissions, metadata flag), opens with the same
/// open and permissions passwords, and renders as the original in our engine and in PDFium. A
/// document whose permissions forbid changes needs its permissions password.
/// </summary>
public class OptimizerEncryptionTests
{
    private const string User = "user", Owner = "owner";

    /// <summary>Printing and changing allowed; copying, commenting, forms and assembly not.</summary>
    private static readonly int ChangesAllowed = unchecked((int)0xFFFFF0CC);

    public static TheoryData<Kind> Schemes => new() { Kind.Rc4_40_R2, Kind.Rc4_128_R3, Kind.Rc4_128_R4, Kind.Aes128_R4, Kind.Aes256_R5, Kind.Aes256_R6 };

    /// <summary>Text, an indexed colour space (a string that changes rendering), an image, Info strings, and waste to remove.</summary>
    private static byte[] Document(PdfTestEncryption encryption, bool withMetadata = false)
    {
        var b = new VectorPdfBuilder();
        const string helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>";
        int font1 = b.Add(helv), font2 = b.Add(helv); // the same font twice
        int cs = b.Add("[/Indexed /DeviceRGB 2 <FF0000 00A040 2040FF>]");
        var pixels = Enumerable.Range(0, 16 * 16 * 3).Select(i => (byte)(i * 7)).ToArray();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels, flate: true);
        string program = new string('x', 6000);
        int extra1 = b.AddStream("", program), extra2 = b.AddStream("", program); // uncompressed and duplicated
        b.Add("<< /Unused (" + new string('u', 4000) + ") >>");                // nothing refers to it
        int info = b.Add("<< /Title (Confidential \\(draft\\)) /Author <4A616E65> >>");
        if (withMetadata)
        {
            int meta = b.AddStream("/Type /Metadata /Subtype /XML", "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'/><?xpacket end='w'?>");
            b.WithCatalog($"/Metadata {meta} 0 R");
        }
        b.AddPage("/CS1 cs 0 sc 10 10 60 60 re f 1 sc 70 10 60 60 re f 2 sc 130 10 60 60 re f " +
                  "BT /F1 18 Tf 10 150 Td (Secret 123) Tj ET BT /F2 12 Tf 10 180 Td (Second font) Tj ET q 80 0 0 60 110 90 cm /Im1 Do Q",
            $"<< /Font << /F1 {font1} 0 R /F2 {font2} 0 R >> /ColorSpace << /CS1 {cs} 0 R >> /XObject << /Im1 {image} 0 R >> /Extra [{extra1} 0 R {extra2} 0 R] >>");
        b.WithTrailer($"/Info {info} 0 R");
        return b.Build(encryption);
    }

    private static async Task<PdfOptimizeResult> Optimize(byte[] pdf, string? openWith, string? ownerPassword = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: openWith);
        return PdfOptimizer.Optimize(doc, pdf, new PdfOptimizeOptions { OwnerPassword = ownerPassword });
    }

    private static async Task<RenderedPage> RenderOurs(byte[] pdf, string? password)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, string.Join(", ", list.FallbackTokens.Select(t => t.Reason)));
        using var renderer = new Direct2DVectorRenderer();
        return await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
    }

    private static async Task<(RenderedPage Page, string Text)> RenderPdfium(byte[] pdf, string? password)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, password);
        var page = await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        return (page, await engine.TextService.ExtractPageTextAsync(doc, 1));
    }

    private static async Task AssertRendersAsOriginal(string name, byte[] original, byte[] copy, string password)
    {
        using (var before = await RenderOurs(original, password))
        using (var after = await RenderOurs(copy, password))
        {
            var (mean, _) = DifferentialRenderingTests.Compare(before, after);
            Assert.True(mean < 0.01, $"{name}: our rendering of the copy differs ({mean:F3})");
        }
        var (pdfiumBefore, textBefore) = await RenderPdfium(original, password);
        var (pdfiumAfter, textAfter) = await RenderPdfium(copy, password);
        using (pdfiumBefore)
        using (pdfiumAfter)
        {
            var (mean, _) = DifferentialRenderingTests.Compare(pdfiumBefore, pdfiumAfter);
            Assert.True(mean < 0.01, $"{name}: PDFium renders the copy differently ({mean:F3})");
        }
        Assert.Contains("Secret 123", textAfter);
        Assert.Equal(textBefore, textAfter);
    }

    private static PdfDictionary EncryptDictionary(PdfVectorDocument doc) =>
        (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Encrypt"])!;

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task TheCopy_IsSmaller_AndEncryptedTheSameWay(Kind scheme)
    {
        byte[] pdf = Document(new PdfTestEncryption(scheme, User, Owner, permissions: ChangesAllowed));
        var result = await Optimize(pdf, User); // the open password is enough: the permissions allow changes
        Assert.True(result.Encrypted);
        Assert.True(result.Bytes.Length < pdf.Length * 0.7, $"{scheme}: {pdf.Length} -> {result.Bytes.Length}");
        Assert.True(result.DuplicatesMerged >= 2, $"merged {result.DuplicatesMerged}");
        string raw = Encoding.Latin1.GetString(result.Bytes);
        Assert.DoesNotContain("Confidential", raw);
        Assert.DoesNotContain("Secret 123", raw);

        using var before = await PdfVectorDocument.OpenAsync(pdf, password: User);
        using (var user = await PdfVectorDocument.OpenAsync(result.Bytes, password: User))
        {
            var was = before.Security!;
            var security = Assert.IsType<PdfStandardSecurityHandler>(user.Security);
            Assert.False(security.IsOwner);
            Assert.Equal(was.Revision, security.Revision);
            Assert.Equal(was.Algorithm, security.Algorithm);
            Assert.Equal(ChangesAllowed, security.Permissions);
            if (security.Revision == 6) Assert.True(security.PermsMatch());
            foreach (string key in new[] { "O", "U", "OE", "UE", "Perms", "P", "R", "V" })
                Assert.Equal(EncryptDictionary(before)[key]?.ToString(), EncryptDictionary(user)[key]?.ToString());
            Assert.Equal("Confidential (draft)", user.Metadata.Title);
            Assert.Equal("Jane", user.Metadata.Author);
        }
        using (var owner = await PdfVectorDocument.OpenAsync(result.Bytes, password: Owner))
            Assert.True(owner.Security!.IsOwner);
        var refused = await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => (await PdfVectorDocument.OpenAsync(result.Bytes, password: "wrong")).Dispose());
        Assert.True(refused.PasswordRequired);

        await AssertRendersAsOriginal(scheme.ToString(), pdf, result.Bytes, User);
        await AssertRendersAsOriginal(scheme.ToString() + " (owner)", pdf, result.Bytes, Owner);
    }

    [Theory]
    [InlineData(Kind.Aes256_R6)]
    [InlineData(Kind.Aes128_R4)]
    [InlineData(Kind.Rc4_128_R3)]
    public async Task ADocumentThatForbidsChanges_NeedsItsPermissionsPassword(Kind scheme)
    {
        var encryption = new PdfTestEncryption(scheme, User, Owner); // print only: changing is not allowed
        byte[] pdf = Document(encryption);

        var missing = await Assert.ThrowsAsync<PdfEncryptedDocumentException>(() => Optimize(pdf, User));
        Assert.True(missing.PasswordRequired);
        Assert.Contains("permissions password", missing.Message);
        var wrong = await Assert.ThrowsAsync<PdfEncryptedDocumentException>(() => Optimize(pdf, User, ownerPassword: "not it"));
        Assert.Equal("The permissions password is incorrect.", wrong.Message);
        foreach (var message in new[] { missing.Message, wrong.Message })
        {
            Assert.DoesNotContain(User, message);  // diagnostics never carry passwords
            Assert.DoesNotContain("not it", message);
        }

        // With the permissions password, given now or used to open: the restrictions stay as they were.
        foreach (var result in new[] { await Optimize(pdf, User, ownerPassword: Owner), await Optimize(pdf, Owner) })
        {
            Assert.True(result.Bytes.Length < pdf.Length);
            using var user = await PdfVectorDocument.OpenAsync(result.Bytes, password: User);
            Assert.Equal(encryption.P, user.Security!.Permissions);
            Assert.False(user.Security.IsOwner);
            await AssertRendersAsOriginal(scheme.ToString(), pdf, result.Bytes, User);
        }
    }

    [Theory]
    [InlineData(Kind.Aes256_R6)]
    [InlineData(Kind.Aes128_R4)]
    public async Task UnencryptedMetadata_StaysReadable(Kind scheme)
    {
        byte[] pdf = Document(new PdfTestEncryption(scheme, User, Owner, encryptMetadata: false, permissions: ChangesAllowed), withMetadata: true);
        var result = await Optimize(pdf, User);
        Assert.Contains("W5M0MpCehiHzreSzNTczkc9d", Encoding.Latin1.GetString(result.Bytes));
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes, password: Owner);
        Assert.False(doc.Security!.IsMetadataEncrypted);
        Assert.True(doc.Security.IsOwner);
    }

    [Fact]
    public async Task WithoutAnOpenPassword_TheCopyStillOpensWithoutOne()
    {
        byte[] pdf = Document(new PdfTestEncryption(Kind.Aes256_R6, string.Empty, Owner, permissions: ChangesAllowed));
        var result = await Optimize(pdf, null);
        using (var doc = await PdfVectorDocument.OpenAsync(result.Bytes))
        {
            Assert.True(doc.IsEncrypted);
            Assert.Equal(ChangesAllowed, doc.Security!.Permissions);
        }
        using (var owner = await PdfVectorDocument.OpenAsync(result.Bytes, password: Owner))
            Assert.True(owner.Security!.IsOwner);
        var (page, text) = await RenderPdfium(result.Bytes, null);
        page.Dispose();
        Assert.Contains("Secret 123", text);
    }

    [Theory]
    [InlineData(Kind.PubSecS5Aes256)]
    [InlineData(Kind.PubSecS4Rc4)]
    public async Task ACertificateEncryptedDocument_KeepsItsRecipients(Kind scheme)
    {
        using var alice = PdfTestEncryption.CreateCertificate("Alice");
        using var bob = PdfTestEncryption.CreateCertificate("Bob");
        byte[] allowed = Document(new PdfTestEncryption(scheme, new[] { alice, bob }, ChangesAllowed));
        using (var doc = await PdfVectorDocument.OpenAsync(allowed, certificates: new[] { alice }))
        {
            var result = PdfOptimizer.Optimize(doc, allowed);
            Assert.True(result.Encrypted);
            Assert.True(result.Bytes.Length < allowed.Length);
            using var copy = await PdfVectorDocument.OpenAsync(result.Bytes, certificates: new[] { bob }); // the other recipient too
            Assert.IsType<PdfPublicKeySecurityHandler>(copy.Security);
            Assert.Equal(ChangesAllowed, copy.Security!.Permissions);
            Assert.Equal("Confidential (draft)", copy.Metadata.Title);
        }

        byte[] forbidden = Document(new PdfTestEncryption(scheme, new[] { alice }, unchecked((int)0xFFFFF0C4)));
        using var restricted = await PdfVectorDocument.OpenAsync(forbidden, certificates: new[] { alice });
        Assert.Throws<PdfEncryptedDocumentException>(() => PdfOptimizer.Optimize(restricted, forbidden));
    }

    [Fact]
    public async Task ACopyWithoutObjectStreams_IsEncryptedToo()
    {
        byte[] pdf = Document(new PdfTestEncryption(Kind.Aes128_R4, User, Owner, permissions: ChangesAllowed));
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: User);
        var result = PdfOptimizer.Optimize(doc, pdf, new PdfOptimizeOptions { CompressObjects = false });
        string raw = Encoding.Latin1.GetString(result.Bytes);
        Assert.DoesNotContain("/ObjStm", raw);
        Assert.Contains("/Encrypt", raw);
        Assert.DoesNotContain("Confidential", raw);
        await AssertRendersAsOriginal("classic table", pdf, result.Bytes, User);
    }
}
