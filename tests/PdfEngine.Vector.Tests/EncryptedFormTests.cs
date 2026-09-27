using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Signatures;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Filling and signing password-protected documents: each incremental update is encrypted with
/// the document's key (RC4 or AES, per object), so the filled values are never written in the
/// clear, and both engines read them back; a signature's /Contents is left unencrypted, as the
/// standard requires, so it verifies.
/// </summary>
public class EncryptedFormTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EncryptedForms_" + Guid.NewGuid().ToString("N"));
    public EncryptedFormTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<TestEncryptionKind> Schemes => new()
    {
        TestEncryptionKind.Rc4_128_R3,
        TestEncryptionKind.Aes128_R4,
        TestEncryptionKind.Aes256_R6,
    };

    private static byte[] EncryptedForm(TestEncryptionKind scheme) =>
        FormPdfFixture.Build(new PdfTestEncryption(scheme, "user", "owner"));

    private string Save(byte[] pdf)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task FillingAnEncryptedForm_EncryptsTheUpdate(TestEncryptionKind scheme)
    {
        byte[] original = EncryptedForm(scheme);
        byte[] filled;
        using (var doc = await PdfVectorDocument.OpenAsync(original, password: "user"))
        {
            Assert.True(doc.IsEncrypted);
            filled = PdfFormFiller.Apply(doc, original, new[]
            {
                new PdfFieldChange("person.name", Text: "Grace Hopper"),
                new PdfFieldChange("agree", Checked: true),
            });
        }
        Assert.True(filled.AsSpan(0, original.Length).SequenceEqual(original));
        string appended = Encoding.Latin1.GetString(filled, original.Length, filled.Length - original.Length);
        Assert.DoesNotContain("Grace Hopper", appended);
        Assert.Contains("/Encrypt", appended); // the update's trailer keeps the encryption

        using (var reopened = await PdfVectorDocument.OpenAsync(filled, password: "user"))
        {
            var form = PdfAcroForm.Read(reopened)!;
            Assert.Equal("Grace Hopper", form["person.name"]!.Value);
            Assert.True(form["agree"]!.IsChecked);
        }

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(filled, "user");
        var fields = await engine.FormService.GetFormFieldsAsync(pdoc, 1);
        Assert.Contains(fields, f => f.Value == "Grace Hopper");
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public async Task SigningAnEncryptedDocument_Verifies(TestEncryptionKind scheme)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Ada Lovelace", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);

        byte[] original = EncryptedForm(scheme);
        byte[] signed;
        using (var doc = await PdfVectorDocument.OpenAsync(original, password: "user"))
        {
            var prepared = PdfSigner.Prepare(doc, original, new PdfSignatureRequest
            {
                SignerName = "Ada Lovelace", Reason = "Approved", Rect = new PdfRect(150, 20, 140, 40),
            });
            signed = prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
        }

        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(signed, password: "user"));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.Equal("Approved", check.Reason); // an encrypted string, read back
        Assert.DoesNotContain("Approved", Encoding.Latin1.GetString(signed, original.Length, signed.Length - original.Length));

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(Save(signed), "user");
        var info = Assert.Single(await engine.SignatureService.GetSignaturesAsync(pdoc));
        Assert.True(info.Status == SignatureStatus.Untrusted, info.StatusMessage);
        Assert.Equal("Approved", info.Reason);
    }

    [Fact]
    public async Task EncryptObject_RoundTripsThroughDecryptObject()
    {
        byte[] pdf = EncryptedForm(TestEncryptionKind.Aes256_R6);
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: "user");
        var security = doc.Security!;
        var original = new Objects.PdfDictionary(new System.Collections.Generic.Dictionary<string, Objects.PdfObject>
        {
            ["T"] = Objects.PdfObjectWriter.TextString("secret"),
            ["Kids"] = new Objects.PdfArray(new Objects.PdfObject[] { Objects.PdfObjectWriter.TextString("nested") }),
        });
        var cipher = (Objects.PdfDictionary)security.EncryptObject(original, 42, 0);
        Assert.NotEqual("secret", ((Objects.PdfString)cipher["T"]!).AsDecodedString());
        var plain = (Objects.PdfDictionary)security.DecryptObject(cipher, 42, 0);
        Assert.Equal("secret", ((Objects.PdfString)plain["T"]!).AsDecodedString());
        Assert.Equal("nested", ((Objects.PdfString)((Objects.PdfArray)plain["Kids"]!)[0]).AsDecodedString());

        // A signature's /Contents is never encrypted.
        var sig = new Objects.PdfDictionary(new System.Collections.Generic.Dictionary<string, Objects.PdfObject>
        {
            ["Type"] = new Objects.PdfName("Sig"),
            ["Contents"] = new Objects.PdfString(new byte[] { 1, 2, 3 }, IsHex: true),
            ["Reason"] = Objects.PdfObjectWriter.TextString("why"),
        });
        var sigCipher = (Objects.PdfDictionary)security.EncryptObject(sig, 43, 0);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((Objects.PdfString)sigCipher["Contents"]!).RawBytes.ToArray());
        Assert.NotEqual("why", ((Objects.PdfString)sigCipher["Reason"]!).AsDecodedString());
        var sigPlain = (Objects.PdfDictionary)security.DecryptObject(sigCipher, 43, 0);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((Objects.PdfString)sigPlain["Contents"]!).RawBytes.ToArray());
        Assert.Equal("why", ((Objects.PdfString)sigPlain["Reason"]!).AsDecodedString());
    }
}
