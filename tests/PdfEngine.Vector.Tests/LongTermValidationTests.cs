using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Long-term validation (PAdES-B-LT): a Document Security Store written as an incremental update
/// that leaves every signature valid, with a VRI entry per signature under the SHA-1 of its
/// /Contents; validation that judges the embedded OCSP responses and CRLs (authentic, about this
/// certificate, current at the signing time) and reports whether each signature is LTV enabled;
/// revocation found by either route; and nothing fetched unless the caller asks. The PKI is made
/// in memory and the revocation source answers from it, so no test touches the network.
/// </summary>
public class LongTermValidationTests : IDisposable
{
    private readonly TestPki _pki = new();

    public void Dispose() => _pki.Dispose();

    private X509Certificate2Collection Roots => new(TestPki.Public(_pki.Root));

    private static async Task<byte[]> Sign(byte[] pdf, X509Certificate2 cert, PdfSignatureRequest? request = null, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        var prepared = PdfSigner.Prepare(doc, pdf, request ?? new PdfSignatureRequest { SignerName = PdfX509.Name(cert), Rect = new PdfRect(150, 20, 140, 40) });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
    }

    private Task<IReadOnlyList<PdfSignatureCheck>> Validate(byte[] pdf, IPdfRevocationSource? online = null, string? password = null) =>
        PdfSignatureValidator.ValidateAsync(pdf, new PdfSignatureValidationOptions { Roots = Roots, OnlineRevocation = online, Password = password });

    private static PdfDictionary Dss(PdfVectorDocument doc)
    {
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        return Assert.IsType<PdfDictionary>(doc.Resolver.Resolve(root["DSS"]));
    }

    /// <summary>The uppercase hex SHA-1 of a signature's whole /Contents string, read straight from the file.</summary>
    private static async Task<string> VriKey(byte[] pdf, int index = 0)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var (_, _, range) = PdfSignatureValidator.SignedFields(doc).OrderBy(s => s.Range[2] + s.Range[3]).ElementAt(index);
        string hex = Encoding.ASCII.GetString(pdf, (int)range[1] + 1, (int)(range[2] - range[1] - 2));
        return Convert.ToHexString(SHA1.HashData(Convert.FromHexString(hex)));
    }

    [Fact]
    public async Task AddingValidationData_WritesTheDss_AndTheSignatureStaysValid()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var source = new FakeRevocationSource(_pki);
        var result = await PdfLtv.AddValidationDataAsync(signed, source);

        Assert.True(result.Changed);
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));
        Assert.True(result.Bytes.AsSpan(0, signed.Length).SequenceEqual(signed), "the signed revision is untouched");
        Assert.True(source.OcspCalls >= 2, "the signer and the intermediate were both asked about");

        using (var doc = await PdfVectorDocument.OpenAsync(result.Bytes))
        {
            var dss = Dss(doc);
            Assert.Equal("DSS", dss.GetName("Type"));
            var certs = Assert.IsType<PdfArray>(dss["Certs"]);
            Assert.Equal(3, certs.Count); // signer, intermediate (fetched from its AIA address), root
            Assert.All(certs, c => Assert.IsType<PdfStream>(doc.Resolver.Resolve(c)));
            Assert.Equal(2, Assert.IsType<PdfArray>(dss["OCSPs"]).Count);
            var vri = Assert.IsType<PdfDictionary>(dss["VRI"]);
            var entry = Assert.IsType<PdfDictionary>(vri[await VriKey(result.Bytes)]);
            Assert.Equal("VRI", entry.GetName("Type"));
            Assert.Equal(3, Assert.IsType<PdfArray>(entry["Cert"]).Count);
            Assert.Equal(2, Assert.IsType<PdfArray>(entry["OCSP"]).Count);
            Assert.NotNull(entry["TU"]);
            // What is stored is the DER itself: the first certificate stream decodes to a certificate.
            var first = (PdfStream)doc.Resolver.Resolve(certs[0])!;
            byte[] der = new PdfEngine.Vector.Streams.PdfStreamDecoder(null, doc.Resolver.Resolve).DecodeStream(first);
            Assert.NotNull(X509CertificateLoader.LoadCertificate(der));
        }

        var check = Assert.Single(await Validate(result.Bytes));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.Equal(PdfLaterChanges.ValidationData, check.LaterChanges);
        Assert.Contains("Long-term validation data was added", check.Summary);
        Assert.Equal(PdfSignatureTrust.Trusted, check.Trust);
        Assert.Equal(PdfRevocationStatus.Good, check.Revocation);
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
        Assert.Equal(2, check.CertificateRevocations.Count);
        Assert.All(check.CertificateRevocations, r => Assert.Equal(PdfRevocationSourceKind.EmbeddedOcsp, r.Source));
        Assert.Contains("not revoked", check.TrustDetail);
    }

    [Fact]
    public async Task WithoutValidationData_TheSignatureIsNotLtvEnabled()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var check = Assert.Single(await Validate(signed));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.False(check.IsLtvEnabled);
        Assert.NotEmpty(check.LtvDetail);
        Assert.Equal(PdfRevocationStatus.NotChecked, check.Revocation);
    }

    [Fact]
    public async Task SelfSignedSigner_IsNeverLtvEnabled()
    {
        using var self = SelfSigned("Solo Signer");
        byte[] signed = await Sign(FormPdfFixture.Build(), self);
        var result = await PdfLtv.AddValidationDataAsync(signed, new FakeRevocationSource(_pki));
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.False(check.IsLtvEnabled);
        Assert.Contains("self-signed", check.LtvDetail);
    }

    [Fact]
    public async Task Crls_AloneMakeTheSignatureLtvEnabled()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var source = new FakeRevocationSource(_pki) { Ocsp = false };
        var result = await PdfLtv.AddValidationDataAsync(signed, source);
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));

        using (var doc = await PdfVectorDocument.OpenAsync(result.Bytes))
        {
            var dss = Dss(doc);
            Assert.Null(dss["OCSPs"]);
            Assert.Equal(2, Assert.IsType<PdfArray>(dss["CRLs"]).Count); // the intermediate's and the root's
        }
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
        Assert.All(check.CertificateRevocations, r => Assert.Equal(PdfRevocationSourceKind.EmbeddedCrl, r.Source));
    }

    [Fact]
    public async Task RevokedSigner_IsDetected_ThroughTheCrl()
    {
        using var signer = _pki.Issue("Mallory");
        var source = new FakeRevocationSource(_pki) { Ocsp = false };
        source.Extra.Add(signer);
        source.Revoke(signer, DateTimeOffset.UtcNow.AddHours(-2), X509RevocationReason.KeyCompromise);
        byte[] signed = await Sign(FormPdfFixture.Build(), signer);

        var result = await PdfLtv.AddValidationDataAsync(signed, source);
        Assert.Contains("Mallory", Assert.Single(result.Signatures).Revoked);
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.Equal(PdfRevocationStatus.Revoked, check.Revocation);
        Assert.Equal(PdfSignatureTrust.Untrusted, check.Trust);
        Assert.Contains("revoked", check.TrustDetail);
        Assert.Contains("key compromise", check.RevocationDetail);
        Assert.False(check.IsLtvEnabled);
        var entry = check.CertificateRevocations[0];
        Assert.Equal(PdfRevocationSourceKind.EmbeddedCrl, entry.Source);
        Assert.Equal(1, entry.RevocationReason);
    }

    [Fact]
    public async Task RevokedSigner_IsDetected_ThroughOcsp()
    {
        using var signer = _pki.Issue("Mallory");
        var source = new FakeRevocationSource(_pki) { Crls = false };
        source.Revoke(signer, DateTimeOffset.UtcNow.AddHours(-2));
        byte[] signed = await Sign(FormPdfFixture.Build(), signer);

        var result = await PdfLtv.AddValidationDataAsync(signed, source);
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.Equal(PdfRevocationStatus.Revoked, check.Revocation);
        Assert.Equal(PdfSignatureTrust.Untrusted, check.Trust);
        Assert.Equal(PdfRevocationSourceKind.EmbeddedOcsp, check.CertificateRevocations[0].Source);
        Assert.Equal(PdfRevocationStatus.Revoked, check.CertificateRevocations[0].Status);
    }

    [Fact]
    public async Task RevokedAfterSigning_ForAHarmlessReason_DoesNotCount_ButKeyCompromiseDoes()
    {
        // Revoked an hour after signing. Without a timestamp the signing time is only the
        // signer's word, so a compromised key could have back-dated it; being superseded could not.
        using var superseded = _pki.Issue("Promoted Signer");
        using var compromised = _pki.Issue("Leaked Signer");
        var source = new FakeRevocationSource(_pki) { Ocsp = false };
        source.Extra.AddRange(new[] { superseded, compromised });
        var signingTime = DateTimeOffset.Now.AddHours(-3);
        source.Revoke(superseded, signingTime.AddHours(1), X509RevocationReason.Superseded);
        source.Revoke(compromised, signingTime.AddHours(1), X509RevocationReason.KeyCompromise);

        async Task<PdfSignatureCheck> SignedAt(X509Certificate2 cert)
        {
            byte[] signed = await Sign(FormPdfFixture.Build(), cert, new PdfSignatureRequest { SignerName = "S", SigningTime = signingTime });
            return Assert.Single(await Validate((await PdfLtv.AddValidationDataAsync(signed, source)).Bytes));
        }

        var fine = await SignedAt(superseded);
        Assert.Equal(PdfRevocationStatus.Good, fine.Revocation);
        Assert.True(fine.IsLtvEnabled, fine.LtvDetail);
        Assert.Contains("revoked later", fine.CertificateRevocations[0].Detail);

        var bad = await SignedAt(compromised);
        Assert.Equal(PdfRevocationStatus.Revoked, bad.Revocation);
    }

    [Fact]
    public async Task NothingIsFetched_UnlessOnlineChecksAreAskedFor()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var source = new FakeRevocationSource(_pki);

        // Validation by default: offline. The source exists but nobody gave it to the validator.
        var offline = Assert.Single(await PdfSignatureValidator.ValidateAsync(signed, roots: Roots));
        Assert.Equal(PdfRevocationStatus.NotChecked, offline.Revocation);
        Assert.Equal(0, source.Calls);

        // Asked for: the status is fetched and reported, but fetched data does not make the document LTV enabled.
        var online = Assert.Single(await Validate(signed, online: source));
        Assert.True(source.Calls > 0);
        Assert.Equal(PdfRevocationStatus.Good, online.Revocation);
        Assert.Equal(PdfRevocationSourceKind.OnlineOcsp, online.CertificateRevocations[0].Source);
        Assert.False(online.IsLtvEnabled);
        Assert.Contains("checked online", online.RevocationDetail);
    }

    [Fact]
    public async Task OnlineChecks_FindARevocationTheDocumentDoesNotMention()
    {
        using var signer = _pki.Issue("Mallory");
        byte[] signed = await Sign(FormPdfFixture.Build(), signer);
        var source = new FakeRevocationSource(_pki);
        source.Revoke(signer, DateTimeOffset.UtcNow.AddDays(-1), X509RevocationReason.KeyCompromise);
        var check = Assert.Single(await Validate(signed, online: source));
        Assert.Equal(PdfRevocationStatus.Revoked, check.Revocation);
        Assert.Equal(PdfSignatureTrust.Untrusted, check.Trust);
    }

    [Fact]
    public async Task ForgedOcspResponse_IsNotBelieved()
    {
        // An impostor with the CA's name but its own key signs a "good" answer.
        using var impostor = SelfSigned("Test Issuing CA, O=Test PKI");
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var source = new FakeRevocationSource(_pki) { Crls = false, Responder = impostor };
        var result = await PdfLtv.AddValidationDataAsync(signed, source);
        var report = Assert.Single(result.Signatures);
        Assert.False(report.IsComplete);
        Assert.Contains(report.Problems, p => p.Contains("could not be verified"));

        // Even planted in the DSS by hand, it is not taken as evidence.
        byte[] planted = await PlantOcsp(signed, TestPki.Ocsp(_pki.Signer, _pki.Intermediate, impostor, PdfRevocationStatus.Good,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7)));
        var check = Assert.Single(await Validate(planted));
        Assert.Equal(PdfRevocationStatus.NotChecked, check.CertificateRevocations[0].Status);
        Assert.False(check.IsLtvEnabled);
    }

    [Fact]
    public async Task StaleOcspResponse_DoesNotSpeakForTheSigningTime()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        // Produced ten days before signing and expired three days before it.
        byte[] stale = TestPki.Ocsp(_pki.Signer, _pki.Intermediate, _pki.Intermediate, PdfRevocationStatus.Good,
            DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-3));
        var check = Assert.Single(await Validate(await PlantOcsp(signed, stale)));
        Assert.Equal(PdfRevocationStatus.NotChecked, check.CertificateRevocations[0].Status);

        byte[] current = TestPki.Ocsp(_pki.Signer, _pki.Intermediate, _pki.Intermediate, PdfRevocationStatus.Good,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6));
        var ok = Assert.Single(await Validate(await PlantOcsp(signed, current)));
        Assert.Equal(PdfRevocationStatus.Good, ok.CertificateRevocations[0].Status);
    }

    [Fact]
    public async Task DelegatedResponder_IsAccepted_AndItsCertificateEmbedded()
    {
        using var responder = _pki.Issue("Test OCSP Responder", eku: "1.3.6.1.5.5.7.3.9", noCheck: true);
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var source = new FakeRevocationSource(_pki) { Responder = responder };
        var result = await PdfLtv.AddValidationDataAsync(signed, source);
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));
        using (var doc = await PdfVectorDocument.OpenAsync(result.Bytes))
            Assert.Equal(4, Assert.IsType<PdfArray>(Dss(doc)["Certs"]).Count); // the chain and the responder
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
    }

    [Fact]
    public async Task ResponderWithoutAnAuthorisation_IsRefused()
    {
        // Issued by the right CA, but not for OCSP signing.
        using var clerk = _pki.Issue("Some Clerk");
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        byte[] response = TestPki.Ocsp(_pki.Signer, _pki.Intermediate, clerk, PdfRevocationStatus.Good,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));
        var check = Assert.Single(await Validate(await PlantOcsp(signed, response)));
        Assert.Equal(PdfRevocationStatus.NotChecked, check.CertificateRevocations[0].Status);
    }

    [Fact]
    public async Task SecondSignature_AndSecondUpdate_MergeIntoOneDss()
    {
        using var second = _pki.Issue("Ada Lovelace");
        var source = new FakeRevocationSource(_pki);
        byte[] once = (await PdfLtv.AddValidationDataAsync(await Sign(FormPdfFixture.Build(), _pki.Signer), source)).Bytes;
        byte[] twice = await Sign(once, second, new PdfSignatureRequest { SignerName = "Ada Lovelace", Rect = new PdfRect(20, 20, 120, 40) });
        var result = await PdfLtv.AddValidationDataAsync(twice, source);
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));

        using (var doc = await PdfVectorDocument.OpenAsync(result.Bytes))
        {
            var dss = Dss(doc);
            Assert.Equal(4, Assert.IsType<PdfArray>(dss["Certs"]).Count); // two signers, one intermediate, one root: no duplicates
            var vri = Assert.IsType<PdfDictionary>(dss["VRI"]);
            Assert.Equal(2, vri.Count);
            Assert.NotNull(vri[await VriKey(result.Bytes, 0)]);
            Assert.NotNull(vri[await VriKey(result.Bytes, 1)]);
        }

        var checks = await Validate(result.Bytes);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(c.IsLtvEnabled, c.LtvDetail));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, checks[0].Verdict); // signed again after it
        Assert.Equal(PdfSignatureVerdict.Valid, checks[1].Verdict);
        Assert.Equal(PdfLaterChanges.ValidationData, checks[1].LaterChanges);

        // Nothing new to be had (the CA's servers are silent), but the document already answers for
        // every certificate: it comes back as it was, and complete.
        var again = await PdfLtv.AddValidationDataAsync(result.Bytes, new FakeRevocationSource(_pki) { Ocsp = false, Crls = false });
        Assert.True(again.IsComplete, string.Join(' ', again.Signatures.SelectMany(s => s.Problems)));
        Assert.False(again.Changed);
        Assert.Same(result.Bytes, again.Bytes);
    }

    [Fact]
    public async Task NoChangesCertification_PermitsValidationData()
    {
        byte[] certified = await Sign(FormPdfFixture.Build(), _pki.Signer,
            new PdfSignatureRequest { SignerName = "Grace Hopper", CertificationLevel = 1, Rect = new PdfRect(150, 20, 140, 40) });
        var result = await PdfLtv.AddValidationDataAsync(certified, new FakeRevocationSource(_pki));
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.Equal(1, check.CertificationLevel);
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
    }

    [Fact]
    public async Task ValidationData_ListingAnExistingObject_IsAChange()
    {
        // A DSS that lists the page's content stream (rewritten) must not pass that rewrite off as validation data.
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        var contents = (PdfIndirectRef)doc.PageTree.Pages[0].Dictionary["Contents"]!;
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        byte[] forged = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [contents.ObjectNumber] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET")),
            [next] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Type"] = new PdfName("DSS"), ["Certs"] = new PdfArray(new PdfObject[] { contents }) }),
            [((PdfIndirectRef)doc.XrefTable.Trailer!["Root"]!).ObjectNumber] =
                new PdfDictionary(new Dictionary<string, PdfObject>(root.Entries) { ["DSS"] = new PdfIndirectRef(next) }),
        });
        var check = Assert.Single(await Validate(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.True(check.LaterChanges.HasFlag(PdfLaterChanges.Other));
    }

    [Theory]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    public async Task EncryptedDocument_GetsEncryptedValidationData(TestEncryptionKind scheme)
    {
        byte[] original = FormPdfFixture.Build(new PdfTestEncryption(scheme, "user", "owner"));
        byte[] signed = await Sign(original, _pki.Signer, password: "user");
        var result = await PdfLtv.AddValidationDataAsync(signed, new FakeRevocationSource(_pki), password: "user");
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));
        // Reading decrypts every stream, so the validation data reads back as certificates and
        // responses only because it was written encrypted with the document's key.
        var check = Assert.Single(await Validate(result.Bytes, password: "user"));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
    }

    [Fact]
    public async Task SignatureTimestamp_ItsAuthoritysChainIsCoveredToo()
    {
        using var tsa = _pki.Issue("Test Time Authority", eku: "1.3.6.1.5.5.7.3.8");
        var vouched = DateTimeOffset.UtcNow.AddMinutes(-2);
        Task<byte[]> Tsa(byte[] request, System.Threading.CancellationToken ct) => Task.FromResult(TimestampResponse(request, tsa, vouched));
        byte[] pdf = FormPdfFixture.Build();
        byte[] signed;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Grace Hopper", ContentsSize = 32768 });
            signed = prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), _pki.Signer, Tsa));
        }

        var result = await PdfLtv.AddValidationDataAsync(signed, new FakeRevocationSource(_pki));
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.Equal(vouched.ToUnixTimeSeconds(), check.TimestampTime!.Value.ToUnixTimeSeconds());
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
        var authority = Assert.Single(check.CertificateRevocations, r => r.IsTimestampAuthority && r.Certificate.Thumbprint == tsa.Thumbprint);
        Assert.Equal(PdfRevocationStatus.Good, authority.Status);
    }

    [Fact]
    public async Task AdobeRevocationArchive_InTheSignature_CountsAsEmbedded()
    {
        // The older way: the CRLs and OCSP responses in a signed attribute of the signature itself.
        var now = DateTimeOffset.UtcNow;
        byte[] signerOcsp = TestPki.Ocsp(_pki.Signer, _pki.Intermediate, _pki.Intermediate, PdfRevocationStatus.Good, now.AddMinutes(-5), now.AddDays(7));
        byte[] intermediateCrl = TestPki.Crl(_pki.Root, Array.Empty<(X509Certificate2, DateTimeOffset, X509RevocationReason?)>(), now.AddMinutes(-5), now.AddDays(7));
        var archive = new AsnWriter(AsnEncodingRules.DER);
        using (archive.PushSequence())
        {
            using (archive.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (archive.PushSequence())
                archive.WriteEncodedValue(intermediateCrl);
            using (archive.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
            using (archive.PushSequence())
                archive.WriteEncodedValue(signerOcsp);
        }

        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Grace Hopper" });
        var cms = new System.Security.Cryptography.Pkcs.SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(prepared.SignedBytes()), detached: true);
        var signer = new System.Security.Cryptography.Pkcs.CmsSigner(_pki.Signer)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.Certificates.Add(TestPki.Public(_pki.Intermediate));
        signer.Certificates.Add(TestPki.Public(_pki.Root));
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113583.1.1.8", archive.Encode()));
        cms.ComputeSignature(signer, silent: true);
        byte[] signed = prepared.Complete(cms.Encode());

        var check = Assert.Single(await Validate(signed));
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.True(check.IsLtvEnabled, check.LtvDetail);
        Assert.Equal(PdfRevocationSourceKind.EmbeddedOcsp, check.CertificateRevocations[0].Source);
        Assert.Equal(PdfRevocationSourceKind.EmbeddedCrl, check.CertificateRevocations[1].Source);
    }

    [Fact]
    public async Task DelegatedResponder_WithoutNoCheck_HasItsOwnStatusEmbedded()
    {
        using var responder = _pki.Issue("Checked OCSP Responder", eku: "1.3.6.1.5.5.7.3.9");
        byte[] signed = await Sign(FormPdfFixture.Build(), _pki.Signer);
        var source = new FakeRevocationSource(_pki) { Responder = responder };
        source.Extra.Add(responder);
        var result = await PdfLtv.AddValidationDataAsync(signed, source);
        Assert.True(result.IsComplete, string.Join(" ", result.Signatures.SelectMany(s => s.Problems)));
        using (var doc = await PdfVectorDocument.OpenAsync(result.Bytes))
            Assert.NotNull(Dss(doc)["CRLs"]); // the responder's status, from its issuer's list
        var check = Assert.Single(await Validate(result.Bytes));
        Assert.True(check.IsLtvEnabled, check.LtvDetail);

        // Without the responder's own status, the document is not self-sufficient.
        var withoutCrl = new FakeRevocationSource(_pki) { Responder = responder, Crls = false };
        var partial = await PdfLtv.AddValidationDataAsync(signed, withoutCrl);
        Assert.False(partial.IsComplete);
        Assert.False(Assert.Single(await Validate(partial.Bytes)).IsLtvEnabled);
    }

    private static byte[] TimestampResponse(byte[] requestBytes, X509Certificate2 tsa, DateTimeOffset at)
    {
        Assert.True(System.Security.Cryptography.Pkcs.Rfc3161TimestampRequest.TryDecode(requestBytes, out var request, out _));
        var info = new System.Security.Cryptography.Pkcs.Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5"), request!.HashAlgorithmId, request.GetMessageHash(),
            serialNumber: new byte[] { 0x07 }, timestamp: at, nonce: request.GetNonce());
        var cms = new System.Security.Cryptography.Pkcs.SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new System.Security.Cryptography.Pkcs.CmsSigner(tsa) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly };
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

    [Fact]
    public void CertificateAddresses_AndTheOcspRequest_AreRead()
    {
        Assert.Equal(new Uri(TestPki.Base + "ocsp/intermediate"), Assert.Single(PdfCertificateUrls.Ocsp(_pki.Signer)));
        Assert.Equal(new Uri(TestPki.Base + "intermediate.cer"), Assert.Single(PdfCertificateUrls.CaIssuers(_pki.Signer)));
        Assert.Equal(new Uri(TestPki.Base + "intermediate.crl"), Assert.Single(PdfCertificateUrls.CrlDistributionPoints(_pki.Signer)));
        Assert.Empty(PdfCertificateUrls.Ocsp(_pki.Root));

        // OCSPRequest { TBSRequest { requestList { Request { CertID } } } }, the CertID naming the signer under its issuer.
        byte[] request = PdfOcspRequest.Create(_pki.Signer, _pki.Intermediate);
        var certId = new AsnReader(request, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence().ReadSequence().ReadSequence();
        Assert.Equal("1.3.14.3.2.26", certId.ReadSequence().ReadObjectIdentifier());
        Assert.Equal(SHA1.HashData(_pki.Intermediate.SubjectName.RawData), certId.ReadOctetString());
        Assert.Equal(SHA1.HashData(_pki.Intermediate.PublicKey.EncodedKeyValue.RawData), certId.ReadOctetString());
        Assert.Equal(PdfX509.Serial(_pki.Signer), certId.ReadInteger());
    }

    [Fact]
    public void Crl_Parsing_ReadsTheRevokedSerials_AndChecksTheSignature()
    {
        var when = DateTimeOffset.UtcNow.AddDays(-1);
        byte[] der = TestPki.Crl(_pki.Intermediate, new[] { (_pki.Signer, when, (X509RevocationReason?)X509RevocationReason.CessationOfOperation) },
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        var crl = PdfCrl.TryParse(der)!;
        Assert.NotNull(crl);
        Assert.True(crl.IsSignedBy(_pki.Intermediate));
        Assert.False(crl.IsSignedBy(_pki.Root));
        Assert.True(crl.Covers(_pki.Signer));
        Assert.False(crl.Covers(_pki.Intermediate)); // issued by the root, not by this CRL's issuer
        var entry = crl.Revoked[PdfX509.Serial(_pki.Signer)];
        Assert.Equal(5, entry.Reason);
        Assert.Equal(when.ToUnixTimeSeconds(), entry.When.ToUnixTimeSeconds());
    }

    /// <summary>An incremental update adding a DSS holding just this OCSP response and the intermediate and root certificates.</summary>
    private async Task<byte[]> PlantOcsp(byte[] pdf, byte[] response)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        int n = PdfIncrementalWriter.NextObjectNumber(doc);
        var objects = new Dictionary<int, PdfObject>
        {
            [n] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), response),
            [n + 1] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), _pki.Intermediate.RawData),
            [n + 2] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), _pki.Root.RawData),
            [n + 3] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("DSS"),
                ["OCSPs"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(n) }),
                ["Certs"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(n + 1), new PdfIndirectRef(n + 2) }),
            }),
            [((PdfIndirectRef)doc.XrefTable.Trailer!["Root"]!).ObjectNumber] =
                new PdfDictionary(new Dictionary<string, PdfObject>(root.Entries) { ["DSS"] = new PdfIndirectRef(n + 3) }),
        };
        return PdfIncrementalWriter.Append(pdf, doc, objects);
    }

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
