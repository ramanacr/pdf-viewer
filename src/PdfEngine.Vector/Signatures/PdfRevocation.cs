using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEngine.Vector.Signatures;

/// <summary>
/// Where revocation information comes from when it is not already in the document: a
/// certificate's OCSP responder, its CRL distribution points, and the issuer certificates its
/// authority information access names. The core never implements this with the network itself;
/// the caller supplies an implementation (the viewer's goes over HTTP) and only when the user
/// has asked for it. Tests supply one that answers from memory.
/// </summary>
public interface IPdfRevocationSource
{
    /// <summary>
    /// An OCSP response (RFC 6960, the DER OCSPResponse) about <paramref name="certificate"/>,
    /// or null when it names no responder or none answered. The caller verifies the response.
    /// </summary>
    Task<byte[]?> GetOcspResponseAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken);

    /// <summary>
    /// Certificate revocation lists (DER) that may cover <paramref name="certificate"/>, from its
    /// CRL distribution points; empty when there are none. The caller verifies each one.
    /// </summary>
    Task<IReadOnlyList<byte[]>> GetCrlsAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken);

    /// <summary>
    /// Certificates that may have issued <paramref name="certificate"/>, from its authority
    /// information access caIssuers entries; the caller checks which one actually signed it.
    /// </summary>
    Task<IReadOnlyList<X509Certificate2>> GetIssuersAsync(X509Certificate2 certificate, CancellationToken cancellationToken);
}

/// <summary>Whether a certificate was revoked at the time that matters (when it signed).</summary>
public enum PdfRevocationStatus
{
    /// <summary>No revocation information was available, and none was fetched.</summary>
    NotChecked,
    /// <summary>Verified revocation information shows the certificate was not revoked then.</summary>
    Good,
    /// <summary>Verified revocation information shows the certificate was revoked (or could have been misused) then.</summary>
    Revoked,
    /// <summary>The responder did not know the certificate, or the information could not be verified.</summary>
    Unknown,
}

/// <summary>Where the revocation information that decided a certificate's status came from.</summary>
public enum PdfRevocationSourceKind
{
    None,
    /// <summary>An OCSP response embedded in the document (its DSS, or the signature's revocation archive).</summary>
    EmbeddedOcsp,
    /// <summary>A CRL embedded in the document.</summary>
    EmbeddedCrl,
    /// <summary>An OCSP response fetched during validation, because the caller asked for online checks.</summary>
    OnlineOcsp,
    /// <summary>A CRL fetched during validation.</summary>
    OnlineCrl,
}

/// <summary>The revocation status of one certificate in a signer's (or a time-stamping authority's) chain.</summary>
public sealed class PdfCertificateRevocation
{
    public required X509Certificate2 Certificate { get; init; }
    public required PdfRevocationStatus Status { get; init; }
    public PdfRevocationSourceKind Source { get; init; }
    /// <summary>When the certificate was revoked, if it was (even if that was after it signed).</summary>
    public DateTimeOffset? RevocationTime { get; init; }
    /// <summary>The RFC 5280 CRLReason code, when the revocation gives one.</summary>
    public int? RevocationReason { get; init; }
    public DateTimeOffset? ThisUpdate { get; init; }
    public DateTimeOffset? NextUpdate { get; init; }
    /// <summary>Belongs to the chain of a time-stamping authority rather than the signer's.</summary>
    public bool IsTimestampAuthority { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool IsEmbedded => Source is PdfRevocationSourceKind.EmbeddedOcsp or PdfRevocationSourceKind.EmbeddedCrl;
}

/// <summary>The addresses a certificate gives for its issuer and its revocation status (RFC 5280 4.2.2.1, 4.2.1.13).</summary>
public static class PdfCertificateUrls
{
    private const string AuthorityInfoAccessOid = "1.3.6.1.5.5.7.1.1";
    private const string CrlDistributionPointsOid = "2.5.29.31";

    /// <summary>OCSP responder addresses from the authority information access extension.</summary>
    public static IReadOnlyList<Uri> Ocsp(X509Certificate2 certificate) => AuthorityInfoAccess(certificate, ocsp: true);

    /// <summary>Issuer certificate addresses (caIssuers) from the authority information access extension.</summary>
    public static IReadOnlyList<Uri> CaIssuers(X509Certificate2 certificate) => AuthorityInfoAccess(certificate, ocsp: false);

    /// <summary>The full-name URIs of the CRL distribution points extension.</summary>
    public static IReadOnlyList<Uri> CrlDistributionPoints(X509Certificate2 certificate)
    {
        var ext = certificate.Extensions[CrlDistributionPointsOid];
        if (ext == null) return Array.Empty<Uri>();
        var result = new List<Uri>();
        try
        {
            var points = new AsnReader(ext.RawData, AsnEncodingRules.BER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (point.HasData && point.PeekTag().HasSameClassAndValue(Context(0)))
                    result.AddRange(DistributionPointUris(point.ReadSequence(Context(0))));
                // reasons [1] and cRLIssuer [2] are not needed to find the list.
            }
        }
        catch (AsnContentException) { }
        return result;
    }

    /// <summary>URIs in a DistributionPointName (its fullName GeneralNames); a relative name gives none.</summary>
    internal static List<Uri> DistributionPointUris(AsnReader name)
    {
        var result = new List<Uri>();
        if (name.HasData && name.PeekTag().HasSameClassAndValue(Context(0)))
        {
            var names = name.ReadSequence(Context(0));
            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (tag.HasSameClassAndValue(Context(6)))
                {
                    string text = names.ReadCharacterString(UniversalTagNumber.IA5String, Context(6));
                    if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) result.Add(uri);
                }
                else names.ReadEncodedValue();
            }
        }
        return result;
    }

    private static IReadOnlyList<Uri> AuthorityInfoAccess(X509Certificate2 certificate, bool ocsp)
    {
        var ext = certificate.Extensions[AuthorityInfoAccessOid];
        if (ext == null) return Array.Empty<Uri>();
        try
        {
            var aia = new X509AuthorityInformationAccessExtension(ext.RawData, ext.Critical);
            var uris = ocsp ? aia.EnumerateOcspUris() : aia.EnumerateCAIssuersUris();
            return uris.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null).OfType<Uri>().ToList();
        }
        catch (CryptographicException)
        {
            return Array.Empty<Uri>();
        }
    }

    private static Asn1Tag Context(int n) => new(TagClass.ContextSpecific, n, isConstructed: n != 6);
}

/// <summary>OCSP requests (RFC 6960 4.1), for a revocation source that asks a responder.</summary>
public static class PdfOcspRequest
{
    /// <summary>
    /// A DER OCSPRequest for one certificate. The CertID uses SHA-1, which every responder
    /// understands (RFC 5019); it identifies the certificate, it does not protect anything. No
    /// nonce is sent: the response is checked against the signing time, not for freshness, and
    /// responders that pre-produce their answers ignore nonces anyway.
    /// </summary>
    public static byte[] Create(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())             // OCSPRequest
        using (w.PushSequence())             //   TBSRequest
        using (w.PushSequence())             //     requestList
        using (w.PushSequence())             //       Request
        using (w.PushSequence())             //         CertID
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier("1.3.14.3.2.26");
                w.WriteNull();
            }
            w.WriteOctetString(SHA1.HashData(issuer.SubjectName.RawData));
            w.WriteOctetString(PdfX509.KeyHash(issuer));
            PdfCmsSigner.WriteSerial(w, certificate.SerialNumberBytes.Span);
        }
        return w.Encode();
    }
}
