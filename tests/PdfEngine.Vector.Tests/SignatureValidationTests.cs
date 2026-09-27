using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Signature validation in the core: each signature judged on its own revision (integrity), on
/// what came after it (filled fields and further signatures are permitted; page content is not;
/// a certification level narrows what is permitted), on the signer's chain at the signing time,
/// and on its timestamp.
/// </summary>
public class SignatureValidationTests
{
    private static X509Certificate2 Certificate(string name, string? eku = null, X509Certificate2? issuer = null, bool ca = false)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
            : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        if (eku != null)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, true));
        var from = DateTimeOffset.UtcNow.AddDays(-2);
        // Within the issuer's validity: a certificate cannot outlive the one that issued it.
        var to = issuer != null ? new DateTimeOffset(issuer.NotAfter.ToUniversalTime()).AddDays(-1) : DateTimeOffset.UtcNow.AddYears(1);
        X509Certificate2 created;
        if (issuer == null) created = req.CreateSelfSigned(from, to);
        else
        {
            byte[] serial = RandomNumberGenerator.GetBytes(8);
            serial[0] = (byte)((serial[0] & 0x7F) | 0x01); // positive, minimally encoded
            using var issued = req.Create(issuer, from, to, serial);
            created = issued.CopyWithPrivateKey(key);
        }
        using (created)
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task<byte[]> Sign(byte[] pdf, X509Certificate2 cert, PdfSignatureRequest request, PdfTimestampClient? tsa = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, request);
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert, tsa));
    }

    private static async Task<byte[]> Fill(byte[] pdf, string field, string text)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfFormFiller.Apply(doc, pdf, new[] { new PdfFieldChange(field, Text: text) });
    }

    /// <summary>An incremental update that replaces page 1's content stream.</summary>
    private static async Task<byte[]> ChangeContent(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        int contents = ((PdfIndirectRef)doc.PageTree.Pages[0].Dictionary["Contents"]!).ObjectNumber;
        return PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [contents] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET")),
        });
    }

    /// <summary>An incremental update that adds a square annotation to page 1.</summary>
    private static async Task<byte[]> Annotate(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        var page = doc.PageTree.Pages[0];
        int pageNumber = PageObject(doc);
        var annots = (doc.Resolver.Resolve(page.Dictionary["Annots"]) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
        annots.Add(new PdfIndirectRef(next));
        return PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [next] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Square"),
                ["Rect"] = new PdfArray(new PdfObject[] { new PdfInteger(200), new PdfInteger(300), new PdfInteger(260), new PdfInteger(340) }),
            }),
            [pageNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Annots"] = new PdfArray(annots) }),
        });
    }

    private static int PageObject(PdfVectorDocument doc)
    {
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        var pages = (PdfDictionary)doc.Resolver.Resolve(root["Pages"])!;
        return ((PdfIndirectRef)((PdfArray)pages["Kids"]!)[0]).ObjectNumber;
    }

    private static readonly PdfRect Box = new(150, 20, 140, 40);

    [Fact]
    public async Task SignedDocument_IsValid_AndDescribed()
    {
        using var cert = Certificate("Ada Lovelace");
        var time = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest
        {
            SignerName = "Ada Lovelace", Reason = "Approved", Location = "London", Rect = Box, SigningTime = time,
        });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(signed));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.True(check.IntegrityValid);
        Assert.True(check.CoversWholeFile);
        Assert.Equal("Ada Lovelace", check.SignerName);
        Assert.Equal("Approved", check.Reason);
        Assert.Equal("London", check.Location);
        Assert.Equal(time, check.ClaimedTime);
        Assert.Equal(1, check.PageNumber);
        Assert.Equal(2, check.Revision);
        Assert.Equal(PdfSignatureTrust.Untrusted, check.Trust); // self-signed, unknown here
        Assert.Contains("identity is unknown", check.TrustDetail);
    }

    [Fact]
    public async Task TrustedRoot_MakesTheSignerTrusted()
    {
        using var root = Certificate("Test Root CA", ca: true);
        using var signer = Certificate("Grace Hopper", issuer: root);
        byte[] signed = await Sign(FormPdfFixture.Build(), signer, new PdfSignatureRequest { SignerName = "Grace Hopper" });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(signed, roots: new X509Certificate2Collection(root)));
        Assert.Equal(PdfSignatureTrust.Trusted, check.Trust);
        Assert.Equal(2, check.Chain.Count);
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
    }

    [Fact]
    public async Task FillingInAfterSigning_IsAPermittedChange()
    {
        using var cert = Certificate("Signer");
        byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "Signer", Rect = Box });
        byte[] filled = await Fill(signed, "person.name", "Filled later");
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(filled));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.False(check.CoversWholeFile);
        Assert.Equal(PdfLaterChanges.FormFields, check.LaterChanges);
        Assert.Contains("filled in", check.Summary);
    }

    [Fact]
    public async Task SecondSignature_IsPermitted_AndItsOwnSignatureValid()
    {
        using var alice = Certificate("Alice");
        using var bob = Certificate("Bob");
        byte[] once = await Sign(FormPdfFixture.Build(), alice, new PdfSignatureRequest { SignerName = "Alice", Rect = Box });
        byte[] twice = await Sign(once, bob, new PdfSignatureRequest { SignerName = "Bob", Rect = new PdfRect(10, 20, 130, 40) });
        var checks = await PdfSignatureValidator.ValidateAsync(twice);
        Assert.Equal(2, checks.Count);
        Assert.Equal("Alice", checks[0].SignerName); // oldest first
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, checks[0].Verdict);
        Assert.True(checks[0].LaterChanges.HasFlag(PdfLaterChanges.Signatures));
        Assert.False(checks[0].LaterChanges.HasFlag(PdfLaterChanges.Other));
        Assert.Equal("Bob", checks[1].SignerName);
        Assert.Equal(PdfSignatureVerdict.Valid, checks[1].Verdict);
    }

    [Fact]
    public async Task ChangingPageContentAfterSigning_IsDetected()
    {
        using var cert = Certificate("Signer");
        byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "Signer" });
        byte[] forged = await ChangeContent(signed);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.True(check.IntegrityValid, "the signed revision itself is intact");
        Assert.True(check.LaterChanges.HasFlag(PdfLaterChanges.Other));
    }

    [Fact]
    public async Task ChangingASignedByte_IsInvalid()
    {
        using var cert = Certificate("Signer");
        byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "Signer" });
        int at = Encoding.Latin1.GetString(signed).IndexOf("(Paris)", StringComparison.Ordinal);
        signed[at + 1] = (byte)'B';
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(signed));
        Assert.Equal(PdfSignatureVerdict.Invalid, check.Verdict);
        Assert.Contains("modified since it was signed", check.Summary);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, true)]
    public async Task CertificationLevel_DecidesWhatMayChange(int level, bool fillingAllowed, bool commentingAllowed)
    {
        using var cert = Certificate("Author");
        byte[] certified = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "Author", CertificationLevel = level, Rect = Box });
        var own = Assert.Single(await PdfSignatureValidator.ValidateAsync(certified));
        Assert.Equal(level, own.CertificationLevel);
        Assert.Equal(PdfSignatureVerdict.Valid, own.Verdict);

        var filled = Assert.Single(await PdfSignatureValidator.ValidateAsync(await Fill(certified, "person.name", "x")));
        Assert.Equal(fillingAllowed ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, filled.Verdict);

        var commented = Assert.Single(await PdfSignatureValidator.ValidateAsync(await Annotate(certified)));
        Assert.True(commented.LaterChanges.HasFlag(PdfLaterChanges.Annotations));
        Assert.Equal(commentingAllowed ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, commented.Verdict);
    }

    [Fact]
    public async Task OnlyTheFirstSignatureCanCertify()
    {
        using var cert = Certificate("Signer");
        byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "Signer" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sign(signed, cert, new PdfSignatureRequest { SignerName = "Signer", CertificationLevel = 2 }));
    }

    [Fact]
    public async Task Timestamp_GivesTheVouchedTime()
    {
        using var cert = Certificate("Signer");
        using var tsaCert = Certificate("Test TSA", eku: "1.3.6.1.5.5.7.3.8");
        var vouched = new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
        Task<byte[]> Tsa(byte[] request, CancellationToken ct) => Task.FromResult(TimestampResponse(request, tsaCert, vouched));
        byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "Signer", ContentsSize = 32768 }, Tsa);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(signed));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.Equal(vouched, check.TimestampTime);
        Assert.Equal("Test TSA", check.TimestampAuthority);
    }

    [Fact]
    public async Task UnsignedDocument_HasNoSignatures()
    {
        Assert.Empty(await PdfSignatureValidator.ValidateAsync(FormPdfFixture.Build()));
    }

    [Fact]
    public void RevisionEnds_FindsEachUpdate()
    {
        byte[] file = Encoding.ASCII.GetBytes("%PDF-1.7\n...%%EOF\n...%%EOF\r\n");
        Assert.Equal(new long[] { 18, 28 }, PdfSignatureValidator.RevisionEnds(file));
    }

    private static byte[] TimestampResponse(byte[] requestBytes, X509Certificate2 tsa, DateTimeOffset at)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(requestBytes, out var request, out _));
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5"), request!.HashAlgorithmId, request.GetMessageHash(),
            serialNumber: new byte[] { 0x07 }, timestamp: at, nonce: request.GetNonce());
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(tsa) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly };
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", PdfCmsSigner.SigningCertificateV2(tsa)));
        cms.ComputeSignature(signer, silent: true);
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence()) w.WriteInteger(0);
            w.WriteEncodedValue(cms.Encode());
        }
        return w.Encode();
    }
}
