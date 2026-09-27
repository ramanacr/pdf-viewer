using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfEngine.Vector.Signatures;

/// <summary>An AlgorithmIdentifier: the OID and its parameters as encoded.</summary>
internal readonly record struct PdfAlgorithmId(string Oid, ReadOnlyMemory<byte>? Parameters);

/// <summary>
/// The X.509 plumbing that revocation checking needs and the BCL does not expose: verifying
/// the signature on a certificate, CRL or OCSP response with the issuer's public key, matching
/// names and serial numbers, and reading ASN.1 times.
/// </summary>
internal static class PdfX509
{
    internal const string OcspSigningEku = "1.3.6.1.5.5.7.3.9";
    internal const string OcspNoCheckOid = "1.3.6.1.5.5.7.48.1.5";

    /// <summary>Splits a signed structure, SEQUENCE { tbs, AlgorithmIdentifier, BIT STRING }.</summary>
    internal static bool TrySplitSigned(ReadOnlyMemory<byte> der, out ReadOnlyMemory<byte> tbs, out PdfAlgorithmId algorithm, out byte[] signature)
    {
        tbs = default;
        algorithm = default;
        signature = Array.Empty<byte>();
        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            tbs = outer.ReadEncodedValue();
            algorithm = ReadAlgorithm(outer);
            signature = outer.ReadBitString(out int unused);
            return unused == 0;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    internal static PdfAlgorithmId ReadAlgorithm(AsnReader reader)
    {
        var seq = reader.ReadSequence();
        string oid = seq.ReadObjectIdentifier();
        ReadOnlyMemory<byte>? parameters = seq.HasData ? seq.ReadEncodedValue() : null;
        return new PdfAlgorithmId(oid, parameters);
    }

    /// <summary>
    /// Whether <paramref name="signature"/> over <paramref name="data"/> was made with the key of
    /// <paramref name="signer"/>: RSA (PKCS #1 v1.5 or PSS) and ECDSA with the SHA-1 and SHA-2
    /// digests. Anything else is not verified, so it does not count as verified.
    /// </summary>
    internal static bool VerifySignature(X509Certificate2 signer, ReadOnlySpan<byte> data, PdfAlgorithmId algorithm, byte[] signature)
    {
        HashAlgorithmName hash;
        RSASignaturePadding? padding = null;
        bool ec = false;
        switch (algorithm.Oid)
        {
            case "1.2.840.113549.1.1.5": hash = HashAlgorithmName.SHA1; padding = RSASignaturePadding.Pkcs1; break;
            case "1.2.840.113549.1.1.11": hash = HashAlgorithmName.SHA256; padding = RSASignaturePadding.Pkcs1; break;
            case "1.2.840.113549.1.1.12": hash = HashAlgorithmName.SHA384; padding = RSASignaturePadding.Pkcs1; break;
            case "1.2.840.113549.1.1.13": hash = HashAlgorithmName.SHA512; padding = RSASignaturePadding.Pkcs1; break;
            case "1.2.840.113549.1.1.10":
                if (PssHash(algorithm.Parameters) is not { } pss) return false;
                hash = pss;
                padding = RSASignaturePadding.Pss;
                break;
            case "1.2.840.10045.4.1": hash = HashAlgorithmName.SHA1; ec = true; break;
            case "1.2.840.10045.4.3.2": hash = HashAlgorithmName.SHA256; ec = true; break;
            case "1.2.840.10045.4.3.3": hash = HashAlgorithmName.SHA384; ec = true; break;
            case "1.2.840.10045.4.3.4": hash = HashAlgorithmName.SHA512; ec = true; break;
            default: return false;
        }
        try
        {
            if (ec)
            {
                using var key = signer.GetECDsaPublicKey();
                return key != null && key.VerifyData(data, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
            }
            using var rsa = signer.GetRSAPublicKey();
            return rsa != null && rsa.VerifyData(data, signature, hash, padding!);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// The digest of RSASSA-PSS parameters (RFC 4055), when they are ones the BCL verifies: MGF1
    /// with the same digest and a salt as long as the digest.
    /// </summary>
    private static HashAlgorithmName? PssHash(ReadOnlyMemory<byte>? parameters)
    {
        string hashOid = "1.3.14.3.2.26", mgfHashOid = hashOid;
        int salt = 20;
        try
        {
            if (parameters is { } p)
            {
                var seq = new AsnReader(p, AsnEncodingRules.BER).ReadSequence();
                if (seq.HasData && seq.PeekTag().HasSameClassAndValue(Ctx(0)))
                    hashOid = ReadAlgorithm(seq.ReadSequence(Ctx(0))).Oid;
                if (seq.HasData && seq.PeekTag().HasSameClassAndValue(Ctx(1)))
                {
                    var mgf = ReadAlgorithm(seq.ReadSequence(Ctx(1)));
                    if (mgf.Oid != "1.2.840.113549.1.1.8" || mgf.Parameters is not { } mp) return null;
                    mgfHashOid = ReadAlgorithm(new AsnReader(mp, AsnEncodingRules.BER)).Oid;
                }
                else mgfHashOid = "1.3.14.3.2.26";
                if (seq.HasData && seq.PeekTag().HasSameClassAndValue(Ctx(2)))
                    salt = (int)seq.ReadSequence(Ctx(2)).ReadInteger();
            }
        }
        catch (AsnContentException)
        {
            return null;
        }
        if (mgfHashOid != hashOid) return null;
        return hashOid switch
        {
            "1.3.14.3.2.26" when salt == 20 => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" when salt == 32 => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" when salt == 48 => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" when salt == 64 => HashAlgorithmName.SHA512,
            _ => null,
        };
    }

    /// <summary>The digest a CertID (or similar) names, or null for one this does not know.</summary>
    internal static byte[]? Hash(string oid, ReadOnlySpan<byte> data) => oid switch
    {
        "1.3.14.3.2.26" => SHA1.HashData(data),
        "2.16.840.1.101.3.4.2.1" => SHA256.HashData(data),
        "2.16.840.1.101.3.4.2.2" => SHA384.HashData(data),
        "2.16.840.1.101.3.4.2.3" => SHA512.HashData(data),
        _ => null,
    };

    /// <summary>The SHA-1 of a certificate's subjectPublicKey bits: the OCSP key hash (RFC 6960 4.1.1).</summary>
    internal static byte[] KeyHash(X509Certificate2 certificate) => SHA1.HashData(certificate.PublicKey.EncodedKeyValue.RawData);

    /// <summary>Whether <paramref name="certificate"/> names <paramref name="issuer"/> as its issuer and carries its signature.</summary>
    internal static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 issuer) =>
        SameName(certificate.IssuerName, issuer.SubjectName)
        && TrySplitSigned(certificate.RawDataMemory, out var tbs, out var algorithm, out var signature)
        && VerifySignature(issuer, tbs.Span, algorithm, signature);

    internal static bool IsSelfSigned(X509Certificate2 certificate) =>
        SameName(certificate.SubjectName, certificate.IssuerName) && IsIssuedBy(certificate, certificate);

    /// <summary>
    /// Names compared as encoded, then as their string form (RFC 5280 7.1 allows re-encoding, and
    /// some CAs write the same name with different string types).
    /// </summary>
    internal static bool SameName(X500DistinguishedName a, X500DistinguishedName b) =>
        a.RawData.AsSpan().SequenceEqual(b.RawData) || string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    internal static bool SameCertificate(X509Certificate2 a, X509Certificate2 b) => a.RawDataMemory.Span.SequenceEqual(b.RawDataMemory.Span);

    internal static BigInteger Serial(X509Certificate2 certificate) => new(certificate.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true);

    internal static bool IsCa(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);

    internal static bool HasEku(X509Certificate2 certificate, string oid) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == oid));

    /// <summary>A key usage extension, when present, permits signing CRLs.</summary>
    internal static bool MaySignCrls(X509Certificate2 certificate)
    {
        var ku = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return ku == null || ku.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign);
    }

    internal static bool ValidAt(X509Certificate2 certificate, DateTimeOffset at) =>
        at.UtcDateTime >= certificate.NotBefore.ToUniversalTime() && at.UtcDateTime <= certificate.NotAfter.ToUniversalTime();

    internal static string Name(X509Certificate2 certificate)
    {
        string name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
    }

    /// <summary>A Time (UTCTime or GeneralizedTime).</summary>
    internal static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    internal static bool IsTime(Asn1Tag tag) => tag.HasSameClassAndValue(Asn1Tag.UtcTime) || tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

    /// <summary>A small ENUMERATED (a CRL reason, a response status).</summary>
    internal static int ReadSmallEnumerated(AsnReader reader, Asn1Tag? tag = null)
    {
        var bytes = reader.ReadEnumeratedBytes(tag);
        return bytes.Length is > 0 and <= 4 ? (int)new BigInteger(bytes.Span, isUnsigned: false, isBigEndian: true) : -1;
    }

    internal static Asn1Tag Ctx(int n, bool constructed = true) => new(TagClass.ContextSpecific, n, constructed);

    /// <summary>Whether a revocation reason means the key itself may be in someone else's hands (or the reason is not given).</summary>
    internal static bool MeansCompromise(int? reason) => reason is null or 0 or 1 or 2 or 10;

    internal static string ReasonText(int? reason) => reason switch
    {
        1 => "key compromise",
        2 => "CA compromise",
        3 => "affiliation changed",
        4 => "superseded",
        5 => "cessation of operation",
        6 => "certificate hold",
        9 => "privilege withdrawn",
        10 => "attribute authority compromise",
        _ => "no reason given",
    };
}

/// <summary>One certificate's entry in an OCSP response.</summary>
internal sealed record PdfOcspSingleResponse(
    string HashOid, byte[] IssuerNameHash, byte[] IssuerKeyHash, BigInteger Serial,
    PdfRevocationStatus Status, DateTimeOffset? RevocationTime, int? RevocationReason,
    DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate);

/// <summary>
/// A parsed OCSP response (RFC 6960 4.2): the basic response's signed data, who signed it,
/// the per-certificate answers, and the certificates it carries to help verify it.
/// </summary>
internal sealed class PdfOcspResponse
{
    private const string BasicResponseOid = "1.3.6.1.5.5.7.48.1.1";

    /// <summary>The full OCSPResponse, as it is embedded in a DSS.</summary>
    public required byte[] Encoded { get; init; }
    public required ReadOnlyMemory<byte> Tbs { get; init; }
    public required PdfAlgorithmId Algorithm { get; init; }
    public required byte[] Signature { get; init; }
    public byte[]? ResponderName { get; init; }
    public byte[]? ResponderKeyHash { get; init; }
    public DateTimeOffset ProducedAt { get; init; }
    public required IReadOnlyList<PdfOcspSingleResponse> Responses { get; init; }
    public required IReadOnlyList<X509Certificate2> Certificates { get; init; }

    /// <summary>An OCSPResponse, or a bare BasicOCSPResponse (some writers embed those); null if it is neither or not successful.</summary>
    public static PdfOcspResponse? TryParse(ReadOnlyMemory<byte> der)
    {
        try
        {
            var top = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            ReadOnlyMemory<byte> basic;
            byte[] encoded;
            if (top.PeekTag().HasSameClassAndValue(Asn1Tag.Enumerated))
            {
                if (PdfX509.ReadSmallEnumerated(top) != 0) return null; // not "successful": no answer inside
                var bytes = top.ReadSequence(PdfX509.Ctx(0)).ReadSequence();
                if (bytes.ReadObjectIdentifier() != BasicResponseOid) return null;
                basic = bytes.ReadOctetString();
                encoded = der.ToArray();
            }
            else
            {
                basic = der;
                encoded = Wrap(der.Span);
            }
            return ParseBasic(basic, encoded);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static PdfOcspResponse ParseBasic(ReadOnlyMemory<byte> basic, byte[] encoded)
    {
        var b = new AsnReader(basic, AsnEncodingRules.BER).ReadSequence();
        var tbs = b.ReadEncodedValue();
        var algorithm = PdfX509.ReadAlgorithm(b);
        byte[] signature = b.ReadBitString(out _);
        var certificates = new List<X509Certificate2>();
        if (b.HasData && b.PeekTag().HasSameClassAndValue(PdfX509.Ctx(0)))
        {
            var certs = b.ReadSequence(PdfX509.Ctx(0)).ReadSequence();
            while (certs.HasData) certificates.Add(X509CertificateLoader.LoadCertificate(certs.ReadEncodedValue().Span));
        }

        var t = new AsnReader(tbs, AsnEncodingRules.BER).ReadSequence();
        if (t.PeekTag().HasSameClassAndValue(PdfX509.Ctx(0))) t.ReadSequence(PdfX509.Ctx(0)); // version
        byte[]? byName = null, byKey = null;
        var responder = t.PeekTag();
        if (responder.HasSameClassAndValue(PdfX509.Ctx(1))) byName = t.ReadSequence(PdfX509.Ctx(1)).ReadEncodedValue().ToArray();
        else if (responder.HasSameClassAndValue(PdfX509.Ctx(2))) byKey = t.ReadSequence(PdfX509.Ctx(2)).ReadOctetString();
        else throw new AsnContentException("The OCSP response has no responder ID.");
        var producedAt = t.ReadGeneralizedTime();

        var responses = new List<PdfOcspSingleResponse>();
        var list = t.ReadSequence();
        while (list.HasData)
        {
            var single = list.ReadSequence();
            var certId = single.ReadSequence();
            string hashOid = PdfX509.ReadAlgorithm(certId).Oid;
            byte[] nameHash = certId.ReadOctetString();
            byte[] keyHash = certId.ReadOctetString();
            BigInteger serial = certId.ReadInteger();

            var statusTag = single.PeekTag();
            PdfRevocationStatus status;
            DateTimeOffset? revokedAt = null;
            int? reason = null;
            if (statusTag.HasSameClassAndValue(PdfX509.Ctx(0, false)))
            {
                single.ReadNull(PdfX509.Ctx(0, false));
                status = PdfRevocationStatus.Good;
            }
            else if (statusTag.HasSameClassAndValue(PdfX509.Ctx(1)))
            {
                var info = single.ReadSequence(PdfX509.Ctx(1));
                revokedAt = info.ReadGeneralizedTime();
                if (info.HasData && info.PeekTag().HasSameClassAndValue(PdfX509.Ctx(0)))
                    reason = PdfX509.ReadSmallEnumerated(info.ReadSequence(PdfX509.Ctx(0)));
                status = PdfRevocationStatus.Revoked;
            }
            else
            {
                single.ReadEncodedValue();
                status = PdfRevocationStatus.Unknown;
            }
            var thisUpdate = single.ReadGeneralizedTime();
            DateTimeOffset? nextUpdate = null;
            if (single.HasData && single.PeekTag().HasSameClassAndValue(PdfX509.Ctx(0)))
                nextUpdate = single.ReadSequence(PdfX509.Ctx(0)).ReadGeneralizedTime();
            responses.Add(new PdfOcspSingleResponse(hashOid, nameHash, keyHash, serial, status, revokedAt, reason, thisUpdate, nextUpdate));
        }

        return new PdfOcspResponse
        {
            Encoded = encoded, Tbs = tbs, Algorithm = algorithm, Signature = signature,
            ResponderName = byName, ResponderKeyHash = byKey, ProducedAt = producedAt,
            Responses = responses, Certificates = certificates,
        };
    }

    /// <summary>A BasicOCSPResponse wrapped as a successful OCSPResponse (the form ISO 32000-2 12.8.4.3 embeds).</summary>
    internal static byte[] Wrap(ReadOnlySpan<byte> basic)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteEncodedValue(new byte[] { 0x0A, 0x01, 0x00 }); // responseStatus successful (0)
            using (w.PushSequence(PdfX509.Ctx(0)))
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(BasicResponseOid);
                w.WriteOctetString(basic);
            }
        }
        return w.Encode();
    }

    /// <summary>The answer about <paramref name="certificate"/> from <paramref name="issuer"/>, if the response has one.</summary>
    public PdfOcspSingleResponse? Find(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var serial = PdfX509.Serial(certificate);
        foreach (var single in Responses)
        {
            if (single.Serial != serial) continue;
            byte[]? nameHash = PdfX509.Hash(single.HashOid, issuer.SubjectName.RawData);
            byte[]? keyHash = PdfX509.Hash(single.HashOid, issuer.PublicKey.EncodedKeyValue.RawData);
            if (nameHash != null && keyHash != null && nameHash.AsSpan().SequenceEqual(single.IssuerNameHash)
                && keyHash.AsSpan().SequenceEqual(single.IssuerKeyHash))
                return single;
        }
        return null;
    }

    /// <summary>
    /// The certificate that signed this response, when that is someone entitled to answer for
    /// <paramref name="issuer"/>'s certificates (RFC 6960 4.2.2.2): the issuer itself, or a
    /// responder the issuer certified directly for OCSP signing, valid when the response was
    /// produced. Null when no such certificate's signature verifies.
    /// </summary>
    public X509Certificate2? VerifiedResponder(X509Certificate2 issuer, IEnumerable<X509Certificate2> pool)
    {
        foreach (var candidate in new[] { issuer }.Concat(Certificates).Concat(pool))
        {
            if (!IsNamed(candidate)) continue;
            bool isIssuer = PdfX509.SameCertificate(candidate, issuer);
            if (!isIssuer)
            {
                if (!PdfX509.HasEku(candidate, PdfX509.OcspSigningEku) || !PdfX509.ValidAt(candidate, ProducedAt)
                    || !PdfX509.IsIssuedBy(candidate, issuer))
                    continue;
            }
            if (PdfX509.VerifySignature(candidate, Tbs.Span, Algorithm, Signature)) return candidate;
        }
        return null;
    }

    private bool IsNamed(X509Certificate2 candidate)
    {
        if (ResponderName != null)
            return PdfX509.SameName(candidate.SubjectName, new X500DistinguishedName(ResponderName));
        return ResponderKeyHash != null && PdfX509.KeyHash(candidate).AsSpan().SequenceEqual(ResponderKeyHash);
    }
}

/// <summary>
/// A parsed certificate revocation list (RFC 5280 5): who issued it, when, which serial numbers
/// it revokes, and the scope it claims. Delta, indirect and partitioned-by-reason lists are
/// recognised and not used, because treating them as complete would report revoked
/// certificates as good.
/// </summary>
internal sealed class PdfCrl
{
    public required byte[] Encoded { get; init; }
    public required ReadOnlyMemory<byte> Tbs { get; init; }
    public required PdfAlgorithmId Algorithm { get; init; }
    public required byte[] Signature { get; init; }
    public required X500DistinguishedName Issuer { get; init; }
    public DateTimeOffset ThisUpdate { get; init; }
    public DateTimeOffset? NextUpdate { get; init; }
    public required IReadOnlyDictionary<BigInteger, (DateTimeOffset When, int? Reason)> Revoked { get; init; }
    /// <summary>Delta, indirect, reason-partitioned, attribute-only, or with a critical extension this does not understand.</summary>
    public bool IsUnusable { get; init; }
    public bool OnlyUserCertificates { get; init; }
    public bool OnlyCaCertificates { get; init; }
    public IReadOnlyList<Uri> DistributionPoints { get; init; } = Array.Empty<Uri>();

    public static PdfCrl? TryParse(ReadOnlyMemory<byte> der)
    {
        try
        {
            if (!PdfX509.TrySplitSigned(der, out var tbs, out var algorithm, out var signature)) return null;
            var t = new AsnReader(tbs, AsnEncodingRules.BER).ReadSequence();
            if (t.PeekTag().HasSameClassAndValue(Asn1Tag.Integer)) t.ReadInteger(); // version
            PdfX509.ReadAlgorithm(t);
            var issuer = new X500DistinguishedName(t.ReadEncodedValue().Span);
            var thisUpdate = PdfX509.ReadTime(t);
            DateTimeOffset? nextUpdate = t.HasData && PdfX509.IsTime(t.PeekTag()) ? PdfX509.ReadTime(t) : null;
            bool unusable = false, onlyUser = false, onlyCa = false;
            var points = new List<Uri>();

            var revoked = new Dictionary<BigInteger, (DateTimeOffset, int?)>();
            if (t.HasData && t.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var entries = t.ReadSequence();
                while (entries.HasData)
                {
                    var entry = entries.ReadSequence();
                    BigInteger serial = entry.ReadInteger();
                    var when = PdfX509.ReadTime(entry);
                    int? reason = null;
                    if (entry.HasData)
                    {
                        var exts = entry.ReadSequence();
                        while (exts.HasData)
                        {
                            var (oid, critical, value) = ReadExtension(exts);
                            if (oid == "2.5.29.21") reason = PdfX509.ReadSmallEnumerated(new AsnReader(value, AsnEncodingRules.BER));
                            else if (oid == "2.5.29.29") unusable = true; // certificateIssuer: an indirect CRL
                            else if (critical && oid != "2.5.29.24") unusable = true;
                        }
                    }
                    if (reason == 8) continue; // removeFromCRL: no longer revoked
                    revoked[serial] = (when, reason);
                }
            }
            if (t.HasData && t.PeekTag().HasSameClassAndValue(PdfX509.Ctx(0)))
            {
                var exts = t.ReadSequence(PdfX509.Ctx(0)).ReadSequence();
                while (exts.HasData)
                {
                    var (oid, critical, value) = ReadExtension(exts);
                    switch (oid)
                    {
                        case "2.5.29.20": case "2.5.29.35": case "2.5.29.46": case "1.3.6.1.5.5.7.1.1":
                            break; // CRL number, authority key identifier, freshest CRL, AIA
                        case "2.5.29.27":
                            unusable = true; // a delta CRL lists only what changed
                            break;
                        case "2.5.29.28":
                            var idp = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
                            if (idp.HasData && idp.PeekTag().HasSameClassAndValue(PdfX509.Ctx(0)))
                                points.AddRange(PdfCertificateUrls.DistributionPointUris(idp.ReadSequence(PdfX509.Ctx(0))));
                            while (idp.HasData)
                            {
                                var tag = idp.PeekTag();
                                if (tag.TagClass != TagClass.ContextSpecific) { idp.ReadEncodedValue(); continue; }
                                if (tag.TagValue == 3) { idp.ReadEncodedValue(); unusable = true; continue; } // onlySomeReasons
                                bool flag = idp.ReadBoolean(PdfX509.Ctx(tag.TagValue, false));
                                if (!flag) continue;
                                if (tag.TagValue == 1) onlyUser = true;
                                else if (tag.TagValue == 2) onlyCa = true;
                                else unusable = true; // indirectCRL [4], onlyContainsAttributeCerts [5]
                            }
                            break;
                        default:
                            if (critical) unusable = true;
                            break;
                    }
                }
            }
            return new PdfCrl
            {
                Encoded = der.ToArray(), Tbs = tbs, Algorithm = algorithm, Signature = signature, Issuer = issuer,
                ThisUpdate = thisUpdate, NextUpdate = nextUpdate, Revoked = revoked, IsUnusable = unusable,
                OnlyUserCertificates = onlyUser, OnlyCaCertificates = onlyCa, DistributionPoints = points,
            };
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static (string Oid, bool Critical, ReadOnlyMemory<byte> Value) ReadExtension(AsnReader list)
    {
        var ext = list.ReadSequence();
        string oid = ext.ReadObjectIdentifier();
        bool critical = ext.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && ext.ReadBoolean();
        return (oid, critical, ext.ReadOctetString());
    }

    /// <summary>Whether this list speaks for <paramref name="certificate"/>: same issuer, complete, and its scope includes it.</summary>
    public bool Covers(X509Certificate2 certificate)
    {
        if (IsUnusable || !PdfX509.SameName(Issuer, certificate.IssuerName)) return false;
        bool ca = PdfX509.IsCa(certificate);
        if (OnlyUserCertificates && ca || OnlyCaCertificates && !ca) return false;
        if (DistributionPoints.Count == 0) return true;
        // A partitioned list covers only the certificates that point at it.
        var theirs = PdfCertificateUrls.CrlDistributionPoints(certificate);
        return theirs.Any(u => DistributionPoints.Any(p => string.Equals(p.AbsoluteUri, u.AbsoluteUri, StringComparison.OrdinalIgnoreCase)));
    }

    public bool IsSignedBy(X509Certificate2 issuer) =>
        PdfX509.SameName(Issuer, issuer.SubjectName) && PdfX509.MaySignCrls(issuer)
        && PdfX509.VerifySignature(issuer, Tbs.Span, Algorithm, Signature);
}

/// <summary>What one piece of revocation information says about one certificate.</summary>
internal sealed record PdfRevocationFinding(
    PdfRevocationStatus Status, PdfRevocationSourceKind Kind, DateTimeOffset? RevokedAt, int? Reason,
    DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate, X509Certificate2? Responder, byte[] Evidence);

/// <summary>
/// Judges OCSP responses and CRLs about a certificate at a point in time: whether each is
/// authentic (signed by the issuer, or by a responder it delegated to), about this certificate,
/// and current for that time; then which of several pieces of evidence decides.
/// </summary>
internal static class PdfRevocationChecker
{
    /// <summary>A response without nextUpdate issued before the time in question is accepted for this long after it.</summary>
    internal static readonly TimeSpan OpenEndedValidity = TimeSpan.FromDays(1);

    public static PdfRevocationFinding? FromOcsp(PdfOcspResponse response, X509Certificate2 certificate, X509Certificate2 issuer,
        IEnumerable<X509Certificate2> pool, DateTimeOffset at, bool online)
    {
        var single = response.Find(certificate, issuer);
        if (single == null) return null;
        var responder = response.VerifiedResponder(issuer, pool);
        if (responder == null) return null;
        var kind = online ? PdfRevocationSourceKind.OnlineOcsp : PdfRevocationSourceKind.EmbeddedOcsp;
        return single.Status switch
        {
            // A signed statement of revocation proves it whenever it was issued.
            PdfRevocationStatus.Revoked => new(PdfRevocationStatus.Revoked, kind, single.RevocationTime, single.RevocationReason,
                single.ThisUpdate, single.NextUpdate, responder, response.Encoded),
            PdfRevocationStatus.Good when Current(single.ThisUpdate, single.NextUpdate, at) => new(PdfRevocationStatus.Good, kind, null, null,
                single.ThisUpdate, single.NextUpdate, responder, response.Encoded),
            PdfRevocationStatus.Unknown => new(PdfRevocationStatus.Unknown, kind, null, null, single.ThisUpdate, single.NextUpdate, responder, response.Encoded),
            _ => null, // good, but stale for that time
        };
    }

    public static PdfRevocationFinding? FromCrl(PdfCrl crl, X509Certificate2 certificate, X509Certificate2 issuer, DateTimeOffset at, bool online)
    {
        if (!crl.Covers(certificate) || !crl.IsSignedBy(issuer)) return null;
        var kind = online ? PdfRevocationSourceKind.OnlineCrl : PdfRevocationSourceKind.EmbeddedCrl;
        if (crl.Revoked.TryGetValue(PdfX509.Serial(certificate), out var entry))
            return new(PdfRevocationStatus.Revoked, kind, entry.When, entry.Reason, crl.ThisUpdate, crl.NextUpdate, null, crl.Encoded);
        return Current(crl.ThisUpdate, crl.NextUpdate, at)
            ? new(PdfRevocationStatus.Good, kind, null, null, crl.ThisUpdate, crl.NextUpdate, null, crl.Encoded)
            : null;
    }

    /// <summary>
    /// Whether information produced at <paramref name="thisUpdate"/> speaks for <paramref name="at"/>:
    /// produced after it (revocation is permanent, so "not revoked later" means "not revoked
    /// then"), or current then (before its nextUpdate, or within a day for open-ended answers).
    /// </summary>
    public static bool Current(DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate, DateTimeOffset at)
    {
        if (thisUpdate >= at) return true;
        return nextUpdate is { } next ? at <= next : at - thisUpdate <= OpenEndedValidity;
    }

    /// <summary>
    /// Whether a revocation counts against a signature made at <paramref name="at"/>: revoked by
    /// then, or revoked later for a reason that means the key may have been misused, when the
    /// time itself cannot be trusted (the signer's own clock, or a TSA vouching for itself).
    /// </summary>
    public static bool CountsAgainst(PdfRevocationFinding finding, DateTimeOffset at, bool trustedTime) =>
        finding.Status == PdfRevocationStatus.Revoked
        && (finding.RevokedAt is not { } when || when <= at || !trustedTime && PdfX509.MeansCompromise(finding.Reason));

    /// <summary>The deciding evidence: a revocation that counts, then a good answer, then a revocation that does not count, then unknown.</summary>
    public static PdfRevocationFinding? Best(IEnumerable<PdfRevocationFinding?> findings, DateTimeOffset at, bool trustedTime)
    {
        PdfRevocationFinding? good = null, laterRevoked = null, unknown = null;
        foreach (var f in findings)
        {
            if (f == null) continue;
            if (f.Status == PdfRevocationStatus.Revoked)
            {
                if (CountsAgainst(f, at, trustedTime)) return f;
                laterRevoked ??= f;
            }
            else if (f.Status == PdfRevocationStatus.Good)
            {
                if (good == null || f.ThisUpdate > good.ThisUpdate) good = f;
            }
            else unknown ??= f;
        }
        return laterRevoked ?? good ?? unknown;
    }
}
