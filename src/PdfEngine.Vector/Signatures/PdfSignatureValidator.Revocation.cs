using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Signatures;

public static partial class PdfSignatureValidator
{
    /// <summary>The validation data a document carries in its DSS, parsed once for every signature.</summary>
    private sealed class ValidationMaterial
    {
        public List<X509Certificate2> Certificates { get; } = new();
        public List<PdfOcspResponse> Ocsps { get; } = new();
        public List<PdfCrl> Crls { get; } = new();

        public static ValidationMaterial Read(PdfVectorDocument doc)
        {
            var material = new ValidationMaterial();
            PdfDss dss;
            try { dss = PdfDss.Read(doc); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return material; }
            // The VRI entries normally point at the same streams as the arrays; either may hold something the other does not.
            var certs = dss.Certs.Concat(dss.VriEntries.Values.SelectMany(v => v.Certs)).Distinct();
            var ocsps = dss.Ocsps.Concat(dss.VriEntries.Values.SelectMany(v => v.Ocsps)).Distinct();
            var crls = dss.Crls.Concat(dss.VriEntries.Values.SelectMany(v => v.Crls)).Distinct();
            foreach (var item in certs)
            {
                try { material.Certificates.Add(X509CertificateLoader.LoadCertificate(item.Data)); }
                catch (CryptographicException) { }
            }
            foreach (var item in ocsps)
                if (PdfOcspResponse.TryParse(item.Data) is { } response) material.Ocsps.Add(response);
            foreach (var item in crls)
                if (PdfCrl.TryParse(item.Data) is { } crl) material.Crls.Add(crl);
            return material;
        }
    }

    /// <summary>What revocation checking of one signature draws on.</summary>
    private sealed record RevocationContext(
        List<PdfOcspResponse> Ocsps, List<PdfCrl> Crls, List<X509Certificate2> Pool,
        HashSet<string> EmbeddedCertificates, PdfLtv.Fetcher? Online, List<string> Problems);

    /// <summary>
    /// Adobe's revocation archive (adbe-revocationInfoArchival, a signed attribute): CRLs and
    /// OCSP responses the signer embedded in the signature itself, which count as embedded
    /// validation data just as the DSS does.
    /// </summary>
    private static void RevocationArchival(SignerInfo signerInfo, List<PdfOcspResponse> ocsps, List<PdfCrl> crls)
    {
        foreach (CryptographicAttributeObject attr in signerInfo.SignedAttributes)
        {
            if (attr.Oid.Value != RevocationArchivalOid || attr.Values.Count == 0) continue;
            try
            {
                var archive = new AsnReader(attr.Values[0].RawData, AsnEncodingRules.BER).ReadSequence();
                while (archive.HasData)
                {
                    var tag = archive.PeekTag();
                    if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue > 1) { archive.ReadEncodedValue(); continue; }
                    var list = archive.ReadSequence(tag).ReadSequence();
                    while (list.HasData)
                    {
                        var value = list.ReadEncodedValue();
                        if (tag.TagValue == 0 && PdfCrl.TryParse(value) is { } crl) crls.Add(crl);
                        else if (tag.TagValue == 1 && PdfOcspResponse.TryParse(value) is { } ocsp) ocsps.Add(ocsp);
                    }
                }
            }
            catch (AsnContentException) { }
        }
    }

    /// <summary>
    /// The revocation status of each certificate in a chain (the self-signed anchor excluded)
    /// at <paramref name="at"/>, and whether the document alone holds everything needed to show
    /// it: every certificate, and verified, current revocation information for each.
    /// </summary>
    private static async Task<(List<PdfCertificateRevocation> Entries, bool Ltv, string Why)> ChainRevocationAsync(
        IReadOnlyList<X509Certificate2> chain, DateTimeOffset at, bool trustedTime, bool timestampAuthority, RevocationContext context)
    {
        var entries = new List<PdfCertificateRevocation>();
        bool ltv = true;
        string why = string.Empty;
        void NotLtv(string reason)
        {
            if (ltv) why = reason;
            ltv = false;
        }

        if (chain.Count == 0) return (entries, false, "The signer's certificate is not in the signature.");
        if (chain.Count == 1 && PdfX509.IsSelfSigned(chain[0]))
            return (entries, false, timestampAuthority
                ? "The time-stamping authority's certificate is self-signed; there is no revocation information for it."
                : "The signer's certificate is self-signed; there is no revocation information for it.");

        var pool = context.Pool.Concat(chain).ToList();
        for (int i = 0; i < chain.Count; i++)
        {
            var cert = chain[i];
            string name = PdfX509.Name(cert);
            if (!context.EmbeddedCertificates.Contains(Convert.ToHexString(SHA256.HashData(cert.RawData))) && i < chain.Count - 1)
                NotLtv($"The certificate \"{name}\" is not included in the document.");
            if (i == chain.Count - 1)
            {
                if (!PdfX509.IsSelfSigned(cert))
                {
                    entries.Add(new PdfCertificateRevocation
                    {
                        Certificate = cert, Status = PdfRevocationStatus.NotChecked, IsTimestampAuthority = timestampAuthority,
                        Detail = $"The certificate that issued \"{name}\" is not available, so its status cannot be checked.",
                    });
                    NotLtv($"The chain of \"{PdfX509.Name(chain[0])}\" is incomplete: the certificate that issued \"{name}\" is not in the document.");
                }
                break; // the trust anchor: nothing vouches for it but trust itself
            }
            var issuer = chain[i + 1];

            var findings = context.Ocsps.Select(o => PdfRevocationChecker.FromOcsp(o, cert, issuer, pool, at, online: false))
                .Concat(context.Crls.Select(c => PdfRevocationChecker.FromCrl(c, cert, issuer, at, online: false)))
                .ToList();
            var best = PdfRevocationChecker.Best(findings, at, trustedTime);
            bool embedded = best is { Status: not PdfRevocationStatus.Unknown };
            if (!embedded && context.Online is { } online)
            {
                var fetched = new List<PdfRevocationFinding?>();
                if (await online.OcspAsync(cert, issuer, context.Problems).ConfigureAwait(false) is { } bytes && PdfOcspResponse.TryParse(bytes) is { } response)
                    fetched.Add(PdfRevocationChecker.FromOcsp(response, cert, issuer, pool.Concat(response.Certificates), at, online: true));
                if (!fetched.Any(f => f is { Status: not PdfRevocationStatus.Unknown }))
                    foreach (var crlBytes in await online.CrlsAsync(cert, issuer, context.Problems).ConfigureAwait(false))
                        if (PdfCrl.TryParse(crlBytes) is { } crl) fetched.Add(PdfRevocationChecker.FromCrl(crl, cert, issuer, at, online: true));
                best = PdfRevocationChecker.Best(fetched, at, trustedTime) ?? best;
            }

            var entry = Entry(cert, best, at, trustedTime, timestampAuthority);
            entries.Add(entry);
            if (!embedded || !entry.IsEmbedded)
                NotLtv($"The document holds no revocation information for \"{name}\".");
            else if (entry.Status != PdfRevocationStatus.Good)
                NotLtv($"The revocation information for \"{name}\" does not show it as good.");
            else if (best?.Responder is { } responder && !PdfX509.SameCertificate(responder, issuer)
                     && responder.Extensions[PdfX509.OcspNoCheckOid] == null
                     && !ResponderStatusEmbedded(responder, issuer, context, pool))
                NotLtv($"The document holds no revocation information for the OCSP responder that answered for \"{name}\".");
        }
        return (entries, ltv, why);
    }

    /// <summary>A delegated OCSP responder without id-pkix-ocsp-nocheck needs its own status in the document too.</summary>
    private static bool ResponderStatusEmbedded(X509Certificate2 responder, X509Certificate2 issuer, RevocationContext context, List<X509Certificate2> pool)
    {
        // Any verified answer from within the responder's own validity shows it was not revoked when it answered.
        var at = new DateTimeOffset(responder.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var findings = context.Crls.Select(c => PdfRevocationChecker.FromCrl(c, responder, issuer, at, online: false))
            .Concat(context.Ocsps.Select(o => PdfRevocationChecker.FromOcsp(o, responder, issuer, pool, at, online: false)));
        return findings.Any(f => f is { Status: PdfRevocationStatus.Good });
    }

    private static PdfCertificateRevocation Entry(X509Certificate2 cert, PdfRevocationFinding? best, DateTimeOffset at, bool trustedTime, bool timestampAuthority)
    {
        if (best == null)
            return new PdfCertificateRevocation
            {
                Certificate = cert, Status = PdfRevocationStatus.NotChecked, IsTimestampAuthority = timestampAuthority,
                Detail = "No revocation information is available for this certificate.",
            };
        string how = best.Kind switch
        {
            PdfRevocationSourceKind.EmbeddedOcsp => "an OCSP response in the document",
            PdfRevocationSourceKind.EmbeddedCrl => "a revocation list in the document",
            PdfRevocationSourceKind.OnlineOcsp => "an OCSP response fetched now",
            _ => "a revocation list fetched now",
        };
        PdfRevocationStatus status;
        string detail;
        if (best.Status == PdfRevocationStatus.Revoked)
        {
            string when = best.RevokedAt is { } r ? $"on {r.LocalDateTime:g}" : "at an unknown time";
            string reason = PdfX509.ReasonText(best.Reason);
            if (PdfRevocationChecker.CountsAgainst(best, at, trustedTime))
            {
                status = PdfRevocationStatus.Revoked;
                detail = best.RevokedAt is { } r2 && r2 > at
                    ? $"Revoked {when} ({reason}), after the signing time, but the signing time is not vouched for and the revocation reason means the key may have been misused (checked with {how})."
                    : $"Revoked {when} ({reason}), before the signing time (checked with {how}).";
            }
            else
            {
                status = PdfRevocationStatus.Good;
                detail = $"Not revoked at the signing time; it was revoked later, {when} ({reason}) (checked with {how}).";
            }
        }
        else if (best.Status == PdfRevocationStatus.Good)
        {
            status = PdfRevocationStatus.Good;
            detail = $"Not revoked (checked with {how}, issued {best.ThisUpdate.LocalDateTime:g}).";
        }
        else
        {
            status = PdfRevocationStatus.Unknown;
            detail = $"The OCSP responder does not know this certificate ({how}).";
        }
        return new PdfCertificateRevocation
        {
            Certificate = cert, Status = status, Source = best.Kind, RevocationTime = best.RevokedAt, RevocationReason = best.Reason,
            ThisUpdate = best.ThisUpdate, NextUpdate = best.NextUpdate, IsTimestampAuthority = timestampAuthority, Detail = detail,
        };
    }

    /// <summary>The overall revocation status of a signature, worst first, with a sentence a reader can act on.</summary>
    private static (PdfRevocationStatus, string) Summarize(IReadOnlyList<PdfCertificateRevocation> entries, bool documentTimestamp)
    {
        if (entries.Count == 0)
            return (PdfRevocationStatus.NotChecked, "Revocation was not checked: the document holds no revocation information for this signature.");
        string Whose(PdfCertificateRevocation e, int index) =>
            e.IsTimestampAuthority && !documentTimestamp ? $"The time-stamping authority's certificate \"{PdfX509.Name(e.Certificate)}\""
            : index == 0 ? documentTimestamp ? "The time-stamping authority's certificate" : "The signer's certificate"
            : $"The certificate \"{PdfX509.Name(e.Certificate)}\" in the chain";
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Status == PdfRevocationStatus.Revoked)
            {
                string when = entries[i].RevocationTime is { } t ? $" on {t.LocalDateTime:g}" : string.Empty;
                return (PdfRevocationStatus.Revoked, $"{Whose(entries[i], i)} was revoked{when} ({PdfX509.ReasonText(entries[i].RevocationReason)}).");
            }
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Status == PdfRevocationStatus.Unknown)
                return (PdfRevocationStatus.Unknown, $"{Whose(entries[i], i)} has an unknown revocation status.");
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Status == PdfRevocationStatus.NotChecked)
                return (PdfRevocationStatus.NotChecked, $"Revocation was not fully checked: there is no revocation information for {Whose(entries[i], i).Replace("The ", "the ", StringComparison.Ordinal)}.");
        bool online = entries.Any(e => !e.IsEmbedded);
        return (PdfRevocationStatus.Good, online
            ? "No certificate in the chain was revoked at the signing time (checked online)."
            : "No certificate in the chain was revoked at the signing time (checked with the revocation information in the document).");
    }
}
