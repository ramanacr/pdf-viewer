using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Signatures;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// A certificate authority made in memory: a root, an intermediate and signers under it, each
/// certificate naming (fictitious, never contacted) addresses for its issuer, OCSP responder
/// and CRL. It issues CRLs with the BCL's CertificateRevocationListBuilder and OCSP responses
/// written out in DER by hand (RFC 6960), so revocation can be tested without any network.
/// </summary>
internal sealed class TestPki : IDisposable
{
    public const string Base = "http://pki.test/";
    private readonly List<X509Certificate2> _issued = new();

    public X509Certificate2 Root { get; }
    public X509Certificate2 Intermediate { get; }
    public X509Certificate2 Signer { get; }

    public TestPki(string signerName = "Grace Hopper")
    {
        Root = Create("Test Root CA", null, ca: true);
        Intermediate = Create("Test Issuing CA", Root, ca: true);
        Signer = Issue(signerName);
    }

    /// <summary>A signing certificate under the intermediate.</summary>
    public X509Certificate2 Issue(string name, string? eku = null, bool noCheck = false, X509Certificate2? issuer = null) =>
        Create(name, issuer ?? Intermediate, ca: false, eku, noCheck);

    private X509Certificate2 Create(string name, X509Certificate2? issuer, bool ca, string? eku = null, bool noCheck = false)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}, O=Test PKI", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
            : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        if (eku != null)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, true));
        if (noCheck)
            req.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.48.1.5", new byte[] { 0x05, 0x00 }, false));
        var from = DateTimeOffset.UtcNow.AddDays(-30);
        DateTimeOffset to;
        X509Certificate2 created;
        if (issuer == null)
        {
            to = DateTimeOffset.UtcNow.AddYears(10);
            created = req.CreateSelfSigned(from, to);
        }
        else
        {
            string issuerSlug = issuer.GetNameInfo(X509NameType.SimpleName, false) == "Test Root CA" ? "root" : "intermediate";
            req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(
                new[] { Base + "ocsp/" + issuerSlug }, new[] { Base + issuerSlug + ".cer" }));
            req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { Base + issuerSlug + ".crl" }));
            to = new DateTimeOffset(issuer.NotAfter.ToUniversalTime()).AddDays(-1);
            byte[] serial = RandomNumberGenerator.GetBytes(8);
            serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
            using var issued = req.Create(issuer, from, to, serial);
            created = issued.CopyWithPrivateKey(key);
        }
        using (created)
        {
            var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            _issued.Add(cert);
            return cert;
        }
    }

    /// <summary>The certificate without its private key, as a server would hand it out.</summary>
    public static X509Certificate2 Public(X509Certificate2 cert) => X509CertificateLoader.LoadCertificate(cert.RawData);

    /// <summary>A CRL from <paramref name="issuer"/> listing <paramref name="revoked"/>.</summary>
    public static byte[] Crl(X509Certificate2 issuer, IEnumerable<(X509Certificate2 Certificate, DateTimeOffset When, X509RevocationReason? Reason)> revoked,
        DateTimeOffset thisUpdate, DateTimeOffset nextUpdate)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var (cert, when, reason) in revoked) builder.AddEntry(cert, when, reason);
        return builder.Build(issuer, BigInteger.One, nextUpdate, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, thisUpdate);
    }

    /// <summary>
    /// An OCSP response (RFC 6960) about <paramref name="cert"/>, signed by <paramref name="responder"/>
    /// (which must hold its private key), identified by name, and carrying the responder's certificate.
    /// </summary>
    public static byte[] Ocsp(X509Certificate2 cert, X509Certificate2 issuer, X509Certificate2 responder, PdfRevocationStatus status,
        DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate, DateTimeOffset? revokedAt = null, int? reason = null, bool byKey = false)
    {
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        using (tbs.PushSequence())
        {
            if (byKey)
                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    tbs.WriteOctetString(SHA1.HashData(responder.PublicKey.EncodedKeyValue.RawData));
            else
                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                    tbs.WriteEncodedValue(responder.SubjectName.RawData);
            tbs.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
            using (tbs.PushSequence())       // responses
            using (tbs.PushSequence())       //   SingleResponse
            {
                using (tbs.PushSequence())   //     CertID
                {
                    using (tbs.PushSequence()) { tbs.WriteObjectIdentifier("1.3.14.3.2.26"); tbs.WriteNull(); }
                    tbs.WriteOctetString(SHA1.HashData(issuer.SubjectName.RawData));
                    tbs.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
                    tbs.WriteInteger(cert.SerialNumberBytes.Span);
                }
                switch (status)
                {
                    case PdfRevocationStatus.Good:
                        tbs.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                        break;
                    case PdfRevocationStatus.Revoked:
                        using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                        {
                            tbs.WriteGeneralizedTime(revokedAt ?? DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                            if (reason is int r)
                                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                                    tbs.WriteEncodedValue(new byte[] { 0x0A, 0x01, (byte)r });
                        }
                        break;
                    default:
                        tbs.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 2));
                        break;
                }
                tbs.WriteGeneralizedTime(thisUpdate, omitFractionalSeconds: true);
                if (nextUpdate is { } next)
                    using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                        tbs.WriteGeneralizedTime(next, omitFractionalSeconds: true);
            }
        }
        byte[] tbsBytes = tbs.Encode();
        using var key = responder.GetRSAPrivateKey() ?? throw new InvalidOperationException("The responder needs its private key.");
        byte[] signature = key.SignData(tbsBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var basic = new AsnWriter(AsnEncodingRules.DER);
        using (basic.PushSequence())
        {
            basic.WriteEncodedValue(tbsBytes);
            using (basic.PushSequence()) { basic.WriteObjectIdentifier("1.2.840.113549.1.1.11"); basic.WriteNull(); }
            basic.WriteBitString(signature);
            using (basic.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (basic.PushSequence())
                basic.WriteEncodedValue(responder.RawData);
        }
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteEnumeratedValue(ResponseStatus.Successful);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                w.WriteOctetString(basic.Encode());
            }
        }
        return w.Encode();
    }

    private enum ResponseStatus { Successful = 0 }

    public void Dispose()
    {
        foreach (var c in _issued) c.Dispose();
    }
}

/// <summary>
/// A revocation source that answers from the test PKI in memory, as a CA's servers would: OCSP
/// responses and CRLs about the certificates it marks revoked, and issuer certificates. It counts
/// every request, so tests can show that nothing asks for revocation data unless told to.
/// </summary>
internal sealed class FakeRevocationSource : IPdfRevocationSource
{
    private readonly TestPki _pki;
    private readonly Dictionary<string, (DateTimeOffset When, X509RevocationReason? Reason)> _revoked = new();

    public FakeRevocationSource(TestPki pki) => _pki = pki;

    public int Calls => OcspCalls + CrlCalls + IssuerCalls;
    public int OcspCalls { get; private set; }
    public int CrlCalls { get; private set; }
    public int IssuerCalls { get; private set; }
    public bool Ocsp { get; set; } = true;
    public bool Crls { get; set; } = true;
    public bool Issuers { get; set; } = true;
    /// <summary>Signs OCSP responses with this certificate instead of the issuer (a delegated responder, or an impostor).</summary>
    public X509Certificate2? Responder { get; set; }
    public TimeSpan ThisUpdateOffset { get; set; } = TimeSpan.FromMinutes(-1);
    public TimeSpan? Validity { get; set; } = TimeSpan.FromDays(7);

    public void Revoke(X509Certificate2 cert, DateTimeOffset when, X509RevocationReason? reason = null) => _revoked[cert.SerialNumber] = (when, reason);

    public Task<byte[]?> GetOcspResponseAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken)
    {
        OcspCalls++;
        if (!Ocsp) return Task.FromResult<byte[]?>(null);
        var signerOf = Responder != null && Responder.Issuer == issuer.Subject ? Responder : Signing(issuer);
        var thisUpdate = DateTimeOffset.UtcNow + ThisUpdateOffset;
        DateTimeOffset? next = Validity is { } v ? thisUpdate + v : null;
        byte[] response = _revoked.TryGetValue(certificate.SerialNumber, out var r)
            ? TestPki.Ocsp(certificate, issuer, signerOf, PdfRevocationStatus.Revoked, thisUpdate, next, r.When, (int?)r.Reason)
            : TestPki.Ocsp(certificate, issuer, signerOf, PdfRevocationStatus.Good, thisUpdate, next);
        return Task.FromResult<byte[]?>(response);
    }

    public Task<IReadOnlyList<byte[]>> GetCrlsAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken)
    {
        CrlCalls++;
        if (!Crls) return Task.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());
        var ca = Signing(issuer);
        var listed = new[] { _pki.Signer, _pki.Intermediate }.Concat(Extra)
            .Where(c => c.Issuer == ca.Subject && _revoked.ContainsKey(c.SerialNumber))
            .Select(c => (c, _revoked[c.SerialNumber].When, _revoked[c.SerialNumber].Reason));
        var thisUpdate = DateTimeOffset.UtcNow + ThisUpdateOffset;
        return Task.FromResult<IReadOnlyList<byte[]>>(new[] { TestPki.Crl(ca, listed, thisUpdate, thisUpdate + (Validity ?? TimeSpan.FromDays(7))) });
    }

    public Task<IReadOnlyList<X509Certificate2>> GetIssuersAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        IssuerCalls++;
        if (!Issuers) return Task.FromResult<IReadOnlyList<X509Certificate2>>(Array.Empty<X509Certificate2>());
        var found = new[] { _pki.Root, _pki.Intermediate }.Where(c => c.Subject == certificate.Issuer).Select(TestPki.Public).ToList();
        return Task.FromResult<IReadOnlyList<X509Certificate2>>(found);
    }

    /// <summary>Further certificates (issued by the test PKI) the CRLs may list.</summary>
    public List<X509Certificate2> Extra { get; } = new();

    /// <summary>The CA's own certificate with its private key (the one passed in may be a public copy).</summary>
    private X509Certificate2 Signing(X509Certificate2 issuer) =>
        new[] { _pki.Root, _pki.Intermediate }.First(c => c.Thumbprint == issuer.Thumbprint);
}
