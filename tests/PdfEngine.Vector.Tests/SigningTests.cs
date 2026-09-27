using System;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Signatures;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Digital signatures in the core: a PAdES signature appended as an incremental update, verified
/// independently (the CMS over the byte ranges, and PDFium's verifier reading the saved file);
/// tampering detected; a second signature leaving the first one's bytes intact; existing empty
/// signature fields filled; a visible appearance drawn; an RFC 3161 timestamp embedded.
/// </summary>
public class SigningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Signing_" + Guid.NewGuid().ToString("N"));

    public SigningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static X509Certificate2 Certificate(string name, bool ecdsa = false, string? eku = null)
    {
        X509Certificate2 created;
        if (ecdsa)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            AddExtensions(req, eku);
            created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }
        else
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            AddExtensions(req, eku);
            created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }
        using (created)
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static void AddExtensions(CertificateRequest req, string? eku)
    {
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        if (eku != null)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, critical: true));
    }

    private static byte[] PlainPdf()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 18 Tf 20 350 Td (Contract) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 300 400]");
        return b.Build();
    }

    private static async Task<byte[]> Sign(byte[] pdf, X509Certificate2 cert, PdfSignatureRequest request, PdfTimestampClient? tsa = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, request);
        byte[] cms = await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert, tsa);
        return prepared.Complete(cms);
    }

    /// <summary>Signature dictionaries of the file, newest last, with the signed bytes and the CMS.</summary>
    private static async Task<(PdfDictionary Sig, byte[] Signed, SignedCms Cms, long[] Range)[]> Signatures(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var form = PdfAcroForm.Read(doc)!;
        var list = new System.Collections.Generic.List<(PdfDictionary, byte[], SignedCms, long[])>();
        foreach (var field in form.Fields.Where(f => f.Kind == PdfFormFieldKind.Signature))
        {
            var fieldDict = (PdfDictionary)doc.Resolver.Resolve(field.ObjectNumber)!;
            if (doc.Resolver.Resolve(fieldDict["V"]) is not PdfDictionary sig) continue;
            var range = ((PdfArray)sig["ByteRange"]!).Select(o => ((PdfInteger)o).Value).ToArray();
            var signed = pdf.AsSpan((int)range[0], (int)range[1]).ToArray().Concat(pdf.AsSpan((int)range[2], (int)range[3]).ToArray()).ToArray();
            // The CMS is the hex string in the gap, read from the file itself (not the parsed object).
            string hex = Encoding.ASCII.GetString(pdf, (int)range[1] + 1, (int)(range[2] - range[1] - 2)).TrimEnd('0');
            if (hex.Length % 2 == 1) hex += "0";
            var cms = new SignedCms(new ContentInfo(signed), detached: true);
            cms.Decode(Convert.FromHexString(hex));
            list.Add((sig, signed, cms, range));
        }
        return list.OrderBy(s => s.Item4[3]).Reverse().ToArray();
    }

    private async Task<System.Collections.Generic.IReadOnlyList<SignatureInfo>> PdfiumVerify(byte[] pdf)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, pdf);
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(path);
        return await engine.SignatureService.GetSignaturesAsync(doc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signature_IsPAdES_CoversTheWholeFile_AndVerifies(bool ecdsa)
    {
        using var cert = Certificate("Ada Lovelace", ecdsa);
        byte[] original = PlainPdf();
        var time = new DateTimeOffset(2026, 9, 27, 10, 30, 0, TimeSpan.FromHours(5.5));
        byte[] signed = await Sign(original, cert, new PdfSignatureRequest
        {
            SignerName = "Ada Lovelace", Reason = "I approve this contract", Location = "London", SigningTime = time,
            Rect = new PdfRect(20, 20, 200, 60),
        });

        Assert.True(signed.AsSpan(0, original.Length).SequenceEqual(original), "the original revision is untouched");
        var (sig, signedBytes, cms, range) = Assert.Single(await Signatures(signed));
        Assert.Equal("ETSI.CAdES.detached", sig.GetName("SubFilter"));
        Assert.Equal("Adobe.PPKLite", sig.GetName("Filter"));
        Assert.Equal("D:20260927103000+05'30'", ((PdfString)sig["M"]!).AsDecodedString());
        Assert.Equal(0, range[0]);
        Assert.Equal(signed.Length, range[2] + range[3]);
        Assert.Equal((byte)'<', signed[range[1]]);
        Assert.Equal((byte)'>', signed[range[2] - 1]);

        cms.CheckSignature(verifySignatureOnly: true);
        var signer = cms.SignerInfos[0];
        Assert.Equal("2.16.840.1.101.3.4.2.1", signer.DigestAlgorithm.Value);
        Assert.Contains(signer.SignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47");
        Assert.DoesNotContain(signer.SignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.5"); // no signing-time (PAdES)
        Assert.Equal(cert.Thumbprint, signer.Certificate!.Thumbprint);

        // PDFium's verifier, from the saved file: intact; self-signed, so not trusted here.
        var info = Assert.Single(await PdfiumVerify(signed));
        Assert.True(info.Status == SignatureStatus.Untrusted, info.StatusMessage);
        Assert.Equal("I approve this contract", info.Reason);
    }

    [Fact]
    public async Task ChangingASignedByte_IsDetected()
    {
        using var cert = Certificate("Signer");
        byte[] signed = await Sign(PlainPdf(), cert, new PdfSignatureRequest { SignerName = "Signer" });
        int at = Encoding.ASCII.GetString(signed).IndexOf("(Contract)", StringComparison.Ordinal);
        signed[at + 1] = (byte)'K';

        var info = Assert.Single(await PdfiumVerify(signed));
        Assert.Equal(SignatureStatus.DocumentModified, info.Status);
        var (_, bytes, cms, _) = Assert.Single(await Signatures(signed));
        Assert.Throws<CryptographicException>(() => cms.CheckSignature(verifySignatureOnly: true));
        Assert.False(SHA256.HashData(bytes).SequenceEqual(MessageDigest(cms)), "the signed digest no longer matches the bytes");
    }

    private static byte[] MessageDigest(SignedCms cms) =>
        cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>()
            .Where(a => a.Oid.Value == "1.2.840.113549.1.9.4")
            .Select(a => AsnDecoder.ReadOctetString(a.Values[0].RawData, AsnEncodingRules.DER, out _)).Single();

    [Fact]
    public async Task SecondSignature_LeavesTheFirstRevisionIntact()
    {
        using var alice = Certificate("Alice");
        using var bob = Certificate("Bob", ecdsa: true);
        byte[] once = await Sign(PlainPdf(), alice, new PdfSignatureRequest { SignerName = "Alice", Rect = new PdfRect(20, 20, 120, 40) });
        byte[] twice = await Sign(once, bob, new PdfSignatureRequest { SignerName = "Bob", Rect = new PdfRect(160, 20, 120, 40) });

        Assert.True(twice.AsSpan(0, once.Length).SequenceEqual(once));
        var sigs = await Signatures(twice);
        Assert.Equal(2, sigs.Length);
        foreach (var s in sigs)
        {
            s.Cms.CheckSignature(verifySignatureOnly: true);
            Assert.True(SHA256.HashData(s.Signed).SequenceEqual(MessageDigest(s.Cms)), "each signature still matches the revision it signed");
        }
        using var doc = await PdfVectorDocument.OpenAsync(twice);
        var names = PdfAcroForm.Read(doc)!.Fields.Where(f => f.Kind == PdfFormFieldKind.Signature).Select(f => f.FullName).ToArray();
        Assert.Equal(new[] { "Signature1", "Signature2" }, names.OrderBy(n => n));

        var infos = await PdfiumVerify(twice);
        Assert.Equal(2, infos.Count);
        Assert.Contains(infos, i => i.Status == SignatureStatus.Untrusted);           // the newest covers the whole file
        Assert.Contains(infos, i => i.Status == SignatureStatus.DocumentModified);   // the first is followed by a later revision
    }

    [Fact]
    public async Task EmptySignatureField_IsFilled_WithItsWidgetAppearance()
    {
        var b = new VectorPdfBuilder();
        int field = b.Add("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approver) /Rect [40 40 260 100] /F 4 >>");
        b.AddPage("", mediaBox: "[0 0 300 400]", extra: $"/Annots [{field} 0 R]");
        int acroForm = b.Add($"<< /Fields [{field} 0 R] >>");
        b.WithCatalog($"/AcroForm {acroForm} 0 R");
        byte[] pdf = b.Build();

        using var cert = Certificate("Grace Hopper");
        byte[] signed = await Sign(pdf, cert, new PdfSignatureRequest { FieldName = "Approver", SignerName = "Grace Hopper", Reason = "Approved" });
        Assert.Single(await Signatures(signed));

        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var widgetDict = (PdfDictionary)doc.Resolver.Resolve(field)!;
        var ap = (PdfStream)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(widgetDict["AP"])!)["N"])!;
        string content = Encoding.Latin1.GetString(ap.GetRawBytes().Span);
        Assert.Contains("Digitally signed by Grace Hopper", content.Replace(") Tj\nT* (", " "));
        var acro = (PdfDictionary)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!)["AcroForm"])!;
        Assert.Equal(3, ((PdfInteger)acro["SigFlags"]!).Value);

        // Signing an already signed field is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sign(signed, cert, new PdfSignatureRequest { FieldName = "Approver", SignerName = "X" }));
    }

    [Fact]
    public async Task VisibleSignature_IsDrawnOnThePage()
    {
        using var cert = Certificate("Ada Lovelace");
        byte[] signed = await Sign(PlainPdf(), cert, new PdfSignatureRequest
        {
            SignerName = "Ada Lovelace", Reason = "Approved", Rect = new PdfRect(20, 20, 200, 60),
        });
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, CancellationToken.None);
        using var page = result.Page;
        int dark = 0, outside = 0;
        var span = page.Pixels.Span;
        for (int y = 0; y < 400; y++)
            for (int x = 0; x < 300; x++)
            {
                bool isDark = span[y * page.Stride + x * 4 + 1] < 128;
                bool inBox = x >= 20 && x < 220 && y >= 400 - 80 && y < 400 - 20;
                bool inTitle = y < 60;
                if (isDark && inBox) dark++;
                else if (isDark && !inTitle) outside++;
            }
        Assert.True(dark > 150, $"the signature text is drawn in its box ({dark} dark pixels)");
        Assert.True(outside < 5, $"nothing is drawn outside the box and the title ({outside})");
    }

    [Fact]
    public async Task InvisibleSignature_IsHiddenWithAZeroRect()
    {
        using var cert = Certificate("Signer");
        byte[] signed = await Sign(PlainPdf(), cert, new PdfSignatureRequest { SignerName = "Signer" });
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var field = PdfAcroForm.Read(doc)!.Fields.Single(f => f.Kind == PdfFormFieldKind.Signature);
        var w = Assert.Single(field.Widgets);
        Assert.Equal(0, w.Rect.Width);
        Assert.Equal(1, w.PageNumber);
    }

    [Fact]
    public async Task Timestamp_IsEmbeddedAsAnUnsignedAttribute()
    {
        using var cert = Certificate("Signer");
        using var tsaCert = Certificate("Test TSA", eku: "1.3.6.1.5.5.7.3.8");
        byte[]? seenRequest = null;
        Task<byte[]> Tsa(byte[] request, CancellationToken ct)
        {
            seenRequest = request;
            return Task.FromResult(TimestampResponse(request, tsaCert));
        }

        byte[] signed = await Sign(PlainPdf(), cert, new PdfSignatureRequest { SignerName = "Signer", ContentsSize = 32768 }, Tsa);
        Assert.NotNull(seenRequest);
        var (_, _, cms, _) = Assert.Single(await Signatures(signed));
        cms.CheckSignature(verifySignatureOnly: true);
        var attr = cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>().Single(a => a.Oid.Value == "1.2.840.113549.1.9.16.2.14");
        Assert.True(Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _));
        // The token is over this signature's value.
        Assert.True(token!.VerifySignatureForSignerInfo(cms.SignerInfos[0], out _));
    }

    [Fact]
    public async Task SignatureTooLargeForTheReservedSpace_IsRefused()
    {
        using var cert = Certificate("Signer");
        using var doc = await PdfVectorDocument.OpenAsync(PlainPdf());
        var prepared = PdfSigner.Prepare(doc, PlainPdf(), new PdfSignatureRequest { SignerName = "Signer", ContentsSize = 1024 });
        Assert.Throws<InvalidOperationException>(() => prepared.Complete(new byte[2048]));
    }

    /// <summary>A minimal RFC 3161 authority: TimeStampResp { granted, token over the request's hash }.</summary>
    private static byte[] TimestampResponse(byte[] requestBytes, X509Certificate2 tsa)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(requestBytes, out var request, out _));
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5"), request!.HashAlgorithmId, request.GetMessageHash(),
            serialNumber: new byte[] { 0x01, 0x02 }, timestamp: DateTimeOffset.UtcNow, nonce: request.GetNonce());
        var content = new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode());
        var cms = new SignedCms(content);
        var signer = new CmsSigner(tsa) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly };
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", PdfCmsSigner.SigningCertificateV2(tsa)));
        cms.ComputeSignature(signer, silent: true);

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence()) w.WriteInteger(0); // PKIStatus granted
            w.WriteEncodedValue(cms.Encode());
        }
        return w.Encode();
    }
}
