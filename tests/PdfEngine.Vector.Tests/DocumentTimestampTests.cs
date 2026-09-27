using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
/// Document timestamps and PAdES-B-LTA: an RFC 3161 token over the whole document appended in an
/// invisible /DocTimeStamp field; validation that checks its message imprint over the byte range
/// and its authority's chain, treats it (and the DSS) as a permitted later change even under a
/// no-changes certification, and reports B-LTA only when the timestamp covers the validation
/// data the signature's LTV finding rests on; and renewal by timestamping again. The authority
/// and the CAs are in memory; nothing touches the network.
/// </summary>
public class DocumentTimestampTests : IDisposable
{
    private readonly TestPki _pki = new();
    private readonly X509Certificate2 _tsaCert;
    private readonly FakeTimestampAuthority _tsa;
    private readonly DateTimeOffset _vouched = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-3).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    public DocumentTimestampTests()
    {
        _tsaCert = _pki.Issue("Test Time Authority", eku: "1.3.6.1.5.5.7.3.8");
        _tsa = new FakeTimestampAuthority(_tsaCert, _vouched);
        _tsa.Chain.Add(TestPki.Public(_pki.Intermediate));
    }

    public void Dispose()
    {
        _tsaCert.Dispose();
        _pki.Dispose();
    }

    private X509Certificate2Collection Roots => new(TestPki.Public(_pki.Root));

    private Task<IReadOnlyList<PdfSignatureCheck>> Validate(byte[] pdf, string? password = null) =>
        PdfSignatureValidator.ValidateAsync(pdf, new PdfSignatureValidationOptions { Roots = Roots, Password = password });

    private async Task<byte[]> Sign(byte[] pdf, PdfSignatureRequest? request = null, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        var prepared = PdfSigner.Prepare(doc, pdf, request ?? new PdfSignatureRequest { SignerName = "Grace Hopper", Rect = new PdfRect(150, 20, 140, 40) });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), _pki.Signer));
    }

    [Fact]
    public async Task DocumentTimestamp_IsAnInvisibleDocTimeStampField_OverTheWholeDocument()
    {
        byte[] signed = await Sign(FormPdfFixture.Build());
        var result = await PdfDocumentTimestamp.AddAsync(signed, _tsa);

        Assert.Equal(1, _tsa.Calls);
        Assert.True(result.Bytes.AsSpan(0, signed.Length).SequenceEqual(signed), "the signed revision is untouched");
        Assert.Equal(_vouched, result.Time);
        Assert.Equal("Test Time Authority", result.Authority);
        Assert.Null(result.ValidationDataBefore);

        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var stamp = PdfSignatureValidator.SignedFields(doc).OrderBy(s => s.Range[2] + s.Range[3]).Last();
        Assert.Equal("DocTimeStamp", stamp.Sig.GetName("Type"));
        Assert.Equal("ETSI.RFC3161", stamp.Sig.GetName("SubFilter"));
        Assert.Equal("Adobe.PPKLite", stamp.Sig.GetName("Filter"));
        Assert.Equal(result.Bytes.Length, stamp.Range[2] + stamp.Range[3]);
        Assert.StartsWith("DocTimeStamp", stamp.Field.FullName);
        Assert.Equal(0, Assert.Single(stamp.Field.Widgets).Rect.Width);

        // Independently: the token's message imprint is the SHA-256 of the byte ranges.
        var range = stamp.Range;
        byte[] covered = result.Bytes.AsSpan(0, (int)range[1]).ToArray().Concat(result.Bytes.AsSpan((int)range[2], (int)range[3]).ToArray()).ToArray();
        byte[] token = PdfSignatureValidator.ContentsFromGap(result.Bytes, range[1], range[2], trim: true)!;
        Assert.True(Rfc3161TimestampToken.TryDecode(token, out var decoded, out _));
        Assert.Equal(SHA256.HashData(covered), decoded!.TokenInfo.GetMessageHash().ToArray());
        Assert.True(decoded.VerifySignatureForData(covered, out var signerCert));
        Assert.Equal(_tsaCert.Thumbprint, signerCert!.Thumbprint);
        // The request asked for a SHA-256 imprint with a nonce, and for the authority's certificate.
        Assert.True(Rfc3161TimestampRequest.TryDecode(_tsa.Requests[0], out var request, out _));
        Assert.Equal("2.16.840.1.101.3.4.2.1", request!.HashAlgorithmId.Value);
        Assert.NotNull(request.GetNonce());
        Assert.True(request.RequestSignerCertificate);
    }

    [Fact]
    public async Task Validation_ChecksTheTimestamp_AndTheSignatureIsTimestampedByIt()
    {
        byte[] stamped = (await PdfDocumentTimestamp.AddAsync(await Sign(FormPdfFixture.Build()), _tsa)).Bytes;
        var checks = await Validate(stamped);
        Assert.Equal(2, checks.Count);

        var signature = checks[0];
        Assert.False(signature.IsDocumentTimestamp);
        Assert.Equal(PdfSignatureVerdict.Valid, signature.Verdict);
        Assert.Equal(PdfLaterChanges.DocumentTimestamp, signature.LaterChanges);
        Assert.Contains("document timestamp was added", signature.Summary);
        Assert.Equal(PdfSignatureLevel.BaselineT, signature.Level); // a trusted time, but no validation data yet
        Assert.False(signature.IsArchiveTimestamped);

        var stamp = checks[1];
        Assert.True(stamp.IsDocumentTimestamp);
        Assert.Equal(PdfSignatureVerdict.Valid, stamp.Verdict);
        Assert.True(stamp.IntegrityValid);
        Assert.Equal(_vouched, stamp.TimestampTime);
        Assert.Equal("Test Time Authority", stamp.TimestampAuthority);
        Assert.Equal(PdfSignatureTrust.Trusted, stamp.Trust);
        Assert.Equal(_tsaCert.Thumbprint, stamp.Certificate!.Thumbprint);
        Assert.Null(stamp.Level);
    }

    [Fact]
    public async Task ValidationDataThenTimestamp_IsBaselineLta()
    {
        byte[] signed = await Sign(FormPdfFixture.Build());
        var source = new FakeRevocationSource(_pki);
        var result = await PdfDocumentTimestamp.AddAsync(signed, _tsa, new PdfDocumentTimestampOptions { ValidationData = source });
        Assert.True(result.IsValidationDataComplete,
            string.Join(" ", new[] { result.ValidationDataBefore, result.ValidationDataAfter }.SelectMany(r => r!.Signatures.SelectMany(s => s.Problems))));
        Assert.True(result.ValidationDataBefore!.Changed);
        Assert.True(result.ValidationDataAfter!.Changed, "the new timestamp's own validation data is added after it");

        var checks = await Validate(result.Bytes);
        var signature = checks[0];
        Assert.Equal(PdfSignatureVerdict.Valid, signature.Verdict);
        Assert.Equal(PdfLaterChanges.ValidationData | PdfLaterChanges.DocumentTimestamp, signature.LaterChanges);
        Assert.True(signature.IsLtvEnabled, signature.LtvDetail);
        Assert.Equal(PdfSignatureLevel.BaselineLTA, signature.Level);
        Assert.Equal(_vouched, signature.ArchiveTimestampTime);
        Assert.Equal(new DateTimeOffset(_tsaCert.NotAfter.ToUniversalTime(), TimeSpan.Zero), signature.ArchiveTimestampExpires);
        Assert.Contains("document timestamp", signature.LevelDetail);

        var stamp = checks[1];
        Assert.True(stamp.IsDocumentTimestamp);
        Assert.Equal(PdfSignatureVerdict.Valid, stamp.Verdict);
        Assert.Equal(PdfLaterChanges.ValidationData, stamp.LaterChanges);
        Assert.True(stamp.IsLtvEnabled, stamp.LtvDetail); // its own validation data came after it
        Assert.Contains(stamp.CertificateRevocations, r => r.Certificate.Thumbprint == _tsaCert.Thumbprint && r.Status == PdfRevocationStatus.Good);
    }

    [Fact]
    public async Task ValidationDataAddedAfterTheTimestamp_IsLtButNotLta()
    {
        byte[] stamped = (await PdfDocumentTimestamp.AddAsync(await Sign(FormPdfFixture.Build()), _tsa)).Bytes;
        byte[] withData = (await PdfLtv.AddValidationDataAsync(stamped, new FakeRevocationSource(_pki))).Bytes;
        var signature = (await Validate(withData))[0];
        Assert.True(signature.IsLtvEnabled, signature.LtvDetail);
        Assert.Equal(PdfSignatureLevel.BaselineLT, signature.Level); // the timestamp does not cover the data
        Assert.False(signature.IsArchiveTimestamped);
        Assert.Contains("no document timestamp covers", signature.LevelDetail);
    }

    [Fact]
    public async Task ATimestampFromAnUntrustedAuthority_ProtectsNothing()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Unknown Time Authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var unknown = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);

        byte[] withData = (await PdfLtv.AddValidationDataAsync(await Sign(FormPdfFixture.Build()), new FakeRevocationSource(_pki))).Bytes;
        byte[] stamped = (await PdfDocumentTimestamp.AddAsync(withData, new FakeTimestampAuthority(unknown, _vouched))).Bytes;
        var checks = await Validate(stamped);
        Assert.Equal(PdfSignatureVerdict.Valid, checks[1].Verdict); // intact...
        Assert.Equal(PdfSignatureTrust.Untrusted, checks[1].Trust);  // ...but from an authority nobody trusts
        Assert.True(checks[0].IsLtvEnabled);
        Assert.Equal(PdfSignatureLevel.BaselineB, checks[0].Level);
        Assert.False(checks[0].IsArchiveTimestamped);
    }

    [Fact]
    public async Task SignatureWithoutTimestamp_IsBaselineB_AndWithValidationDataStillNeedsATime()
    {
        byte[] signed = await Sign(FormPdfFixture.Build());
        Assert.Equal(PdfSignatureLevel.BaselineB, Assert.Single(await Validate(signed)).Level);
        byte[] withData = (await PdfLtv.AddValidationDataAsync(signed, new FakeRevocationSource(_pki))).Bytes;
        var check = Assert.Single(await Validate(withData));
        Assert.True(check.IsLtvEnabled);
        Assert.Equal(PdfSignatureLevel.BaselineB, check.Level); // no trusted time: B-LT needs B-T first
    }

    [Fact]
    public async Task Renewal_ASecondTimestamp_ProtectsTheFirstAndItsValidationData()
    {
        using var laterTsaCert = _pki.Issue("Second Time Authority", eku: "1.3.6.1.5.5.7.3.8");
        var laterTime = _vouched.AddMinutes(2);
        var laterTsa = new FakeTimestampAuthority(laterTsaCert, laterTime);
        var source = new FakeRevocationSource(_pki);
        var options = new PdfDocumentTimestampOptions { ValidationData = source };

        byte[] first = (await PdfDocumentTimestamp.AddAsync(await Sign(FormPdfFixture.Build()), _tsa, options)).Bytes;
        var renewed = await PdfDocumentTimestamp.AddAsync(first, laterTsa, options);
        Assert.True(renewed.IsValidationDataComplete);
        Assert.True(renewed.Bytes.AsSpan(0, first.Length).SequenceEqual(first));

        var checks = await Validate(renewed.Bytes);
        Assert.Equal(3, checks.Count);
        Assert.All(checks, c => Assert.Equal(PdfSignatureVerdict.Valid, c.Verdict));
        var signature = checks[0];
        Assert.Equal(PdfSignatureLevel.BaselineLTA, signature.Level);
        Assert.Equal(laterTime, signature.ArchiveTimestampTime); // the latest protection
        Assert.Equal(new DateTimeOffset(laterTsaCert.NotAfter.ToUniversalTime(), TimeSpan.Zero), signature.ArchiveTimestampExpires);

        var firstStamp = checks[1];
        Assert.True(firstStamp.IsDocumentTimestamp);
        Assert.Equal(_vouched, firstStamp.TimestampTime);
        Assert.True(firstStamp.IsArchiveTimestamped, "the first timestamp and its validation data are covered by the second");
        Assert.Equal(laterTime, firstStamp.ArchiveTimestampTime);

        var lastStamp = checks[2];
        Assert.Equal(laterTime, lastStamp.TimestampTime);
        Assert.True(lastStamp.IsLtvEnabled, lastStamp.LtvDetail);
        Assert.False(lastStamp.IsArchiveTimestamped);
    }

    [Fact]
    public async Task NoChangesCertification_PermitsValidationDataAndADocumentTimestamp()
    {
        byte[] certified = await Sign(FormPdfFixture.Build(),
            new PdfSignatureRequest { SignerName = "Grace Hopper", CertificationLevel = 1, Rect = new PdfRect(150, 20, 140, 40) });
        var result = await PdfDocumentTimestamp.AddAsync(certified, _tsa, new PdfDocumentTimestampOptions { ValidationData = new FakeRevocationSource(_pki) });
        var check = (await Validate(result.Bytes))[0];
        Assert.Equal(1, check.CertificationLevel);
        Assert.Equal(PdfSignatureVerdict.Valid, check.Verdict);
        Assert.Equal(PdfSignatureLevel.BaselineLTA, check.Level);

        // An approval signature, by contrast, is a change a no-changes certification forbids.
        using var other = _pki.Issue("Ada Lovelace");
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var prepared = PdfSigner.Prepare(doc, result.Bytes, new PdfSignatureRequest { SignerName = "Ada Lovelace" });
        byte[] countersigned = prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), other));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await Validate(countersigned))[0].Verdict);
    }

    [Fact]
    public async Task NoChangesCertification_RefusesATimestampFieldThatDrawsOnThePage()
    {
        // A "document timestamp" whose widget has a visible appearance puts new content on the
        // page; under a no-changes certification that must not pass as a timestamp.
        byte[] certified = await Sign(FormPdfFixture.Build(),
            new PdfSignatureRequest { SignerName = "Grace Hopper", CertificationLevel = 1, Rect = new PdfRect(150, 20, 140, 40) });
        byte[] genuine = (await PdfDocumentTimestamp.AddAsync(certified, _tsa)).Bytes;
        Assert.Equal(PdfSignatureVerdict.Valid, (await Validate(genuine))[0].Verdict);

        byte[] forged = await VisibleTimestampField(certified);
        var check = (await Validate(forged)).Single(c => !c.IsDocumentTimestamp);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.NotEqual(PdfLaterChanges.DocumentTimestamp, check.LaterChanges);
    }

    [Fact]
    public async Task ChangedBytes_UnderTheTimestamp_AreDetected()
    {
        byte[] stamped = (await PdfDocumentTimestamp.AddAsync(FormPdfFixture.Build(), _tsa)).Bytes;
        var stamp = Assert.Single(await Validate(stamped));
        Assert.Equal(PdfSignatureVerdict.Valid, stamp.Verdict);

        int at = Encoding.Latin1.GetString(stamped).IndexOf("0.9 g", StringComparison.Ordinal);
        Assert.True(at > 0);
        stamped[at + 2] = (byte)'1';
        var tampered = Assert.Single(await Validate(stamped));
        Assert.Equal(PdfSignatureVerdict.Invalid, tampered.Verdict);
        Assert.Contains("does not match", tampered.Summary);
    }

    [Fact]
    public async Task UnsignedDocument_CanBeTimestamped_WithTheAuthoritysValidationData()
    {
        var source = new FakeRevocationSource(_pki);
        var result = await PdfDocumentTimestamp.AddAsync(FormPdfFixture.Build(), _tsa, new PdfDocumentTimestampOptions { ValidationData = source });
        Assert.Null(result.ValidationDataBefore); // nothing to add validation data for before
        Assert.True(result.ValidationDataAfter!.IsComplete, string.Join(" ", result.ValidationDataAfter.Signatures.SelectMany(s => s.Problems)));
        var stamp = Assert.Single(await Validate(result.Bytes));
        Assert.True(stamp.IsDocumentTimestamp);
        Assert.Equal(PdfSignatureVerdict.Valid, stamp.Verdict);
        Assert.True(stamp.IsLtvEnabled, stamp.LtvDetail);
    }

    [Fact]
    public async Task AnAnswerForAnotherDocument_OrARefusal_IsNotWritten()
    {
        byte[] signed = await Sign(FormPdfFixture.Build());
        _tsa.WrongImprint = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfDocumentTimestamp.AddAsync(signed, _tsa));
        _tsa.WrongImprint = false;
        _tsa.Reject = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfDocumentTimestamp.AddAsync(signed, _tsa));
    }

    [Fact]
    public async Task ALargeToken_GetsMoreRoom()
    {
        _tsa.Padding = 3000;
        var result = await PdfDocumentTimestamp.AddAsync(await Sign(FormPdfFixture.Build()), _tsa, new PdfDocumentTimestampOptions { ContentsSize = 1024 });
        Assert.Equal(2, _tsa.Calls); // asked again with room for it
        Assert.Equal(PdfSignatureVerdict.Valid, (await Validate(result.Bytes))[1].Verdict);
    }

    [Theory]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    public async Task EncryptedDocument_IsTimestamped(TestEncryptionKind scheme)
    {
        byte[] original = FormPdfFixture.Build(new PdfTestEncryption(scheme, "user", "owner"));
        byte[] signed = await Sign(original, password: "user");
        var result = await PdfDocumentTimestamp.AddAsync(signed, _tsa, new PdfDocumentTimestampOptions { Password = "user", ValidationData = new FakeRevocationSource(_pki) });
        var checks = await Validate(result.Bytes, password: "user");
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(PdfSignatureVerdict.Valid, c.Verdict));
        Assert.Equal(PdfSignatureLevel.BaselineLTA, checks[0].Level);
    }

    [Fact]
    public async Task DelegateClients_AndInterfaceClients_AreInterchangeable()
    {
        var client = PdfTimestampClients.FromDelegate(_tsa.AsDelegate());
        var result = await PdfDocumentTimestamp.AddAsync(FormPdfFixture.Build(), client);
        Assert.Equal(1, _tsa.Calls);
        Assert.Equal(_vouched, result.Time);
    }

    /// <summary>An update adding a DocTimeStamp-valued field whose widget draws text on page 1.</summary>
    private static async Task<byte[]> VisibleTimestampField(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var r = doc.Resolver;
        var root = (PdfDictionary)r.Resolve(doc.XrefTable.Trailer!["Root"])!;
        var acroRef = (PdfIndirectRef)root["AcroForm"]!;
        var acro = (PdfDictionary)r.Resolve(acroRef)!;
        var sigField = PdfAcroForm.Read(doc)!.Fields.Single(f => f.Kind == PdfFormFieldKind.Signature);
        var widget = (PdfDictionary)r.Resolve(sigField.Widgets[0].ObjectNumber)!;
        var pageRef = (PdfIndirectRef)widget["P"]!;
        var page = (PdfDictionary)r.Resolve(pageRef)!;

        int n = PdfIncrementalWriter.NextObjectNumber(doc);
        int sig = n, field = n + 1, ap = n + 2;
        var objects = new Dictionary<int, PdfObject>
        {
            [sig] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("DocTimeStamp"), ["Filter"] = new PdfName("Adobe.PPKLite"), ["SubFilter"] = new PdfName("ETSI.RFC3161"),
                ["ByteRange"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) }),
                ["Contents"] = new PdfString(new byte[16], IsHex: true),
            }),
            [ap] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"),
                ["BBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(200), new PdfInteger(40) }),
            }, Encoding.Latin1.GetBytes("0 0 1 rg 0 0 200 40 re f")),
            [field] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Widget"), ["FT"] = new PdfName("Sig"),
                ["T"] = PdfObjectWriter.TextString("DocTimeStamp1"), ["V"] = new PdfIndirectRef(sig), ["P"] = pageRef, ["F"] = new PdfInteger(4),
                ["Rect"] = new PdfArray(new PdfObject[] { new PdfInteger(20), new PdfInteger(20), new PdfInteger(220), new PdfInteger(60) }),
                ["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(ap) }),
            }),
        };
        var fields = ((PdfArray)r.Resolve(acro["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToList();
        objects[acroRef.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(acro.Entries) { ["Fields"] = new PdfArray(fields) });
        var annots = ((PdfArray)r.Resolve(page["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToList();
        objects[pageRef.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Entries) { ["Annots"] = new PdfArray(annots) });
        return PdfIncrementalWriter.Append(pdf, doc, objects);
    }
}
