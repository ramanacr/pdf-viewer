using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Signatures;

/// <summary>The overall answer for one signature, in the order a reader cares about.</summary>
public enum PdfSignatureVerdict
{
    /// <summary>Intact, and nothing changed after it.</summary>
    Valid,
    /// <summary>Intact; later revisions only did what signing allows (filled fields, signed again, commented, updated metadata).</summary>
    ValidWithPermittedChanges,
    /// <summary>Intact for its revision, but a later revision changed the document in a way the signature does not allow.</summary>
    ModifiedAfterSigning,
    /// <summary>The signature does not match the bytes it covers, or is not a valid signature at all.</summary>
    Invalid,
    /// <summary>A signature format this validator does not check.</summary>
    Unsupported,
}

public enum PdfSignatureTrust { Trusted, Untrusted, Unknown }

/// <summary>What changed after a signature, most significant kind first.</summary>
[Flags]
public enum PdfLaterChanges
{
    None = 0,
    Signatures = 1,
    FormFields = 2,
    Annotations = 4,
    Metadata = 8,
    /// <summary>Anything else: page content, pages, resources.</summary>
    Other = 16,
    /// <summary>
    /// Long-term validation data added to the Document Security Store (certificates, OCSP
    /// responses, CRLs). Not a change to the document: every signature, and even a no-changes
    /// certification, permits it (ISO 32000-2 12.8.2.2.2).
    /// </summary>
    ValidationData = 32,
    /// <summary>
    /// A document timestamp (ISO 32000-2 12.8.5) added over the document. Like validation data,
    /// it is not a change to the document: no signature, and no certification level, forbids it
    /// (ISO 32000-2 12.8.2.2.2).
    /// </summary>
    DocumentTimestamp = 64,
}

/// <summary>
/// The PAdES baseline level a signature reaches (ETSI EN 319 142-1 6): what it carries to be
/// validated now and later.
/// </summary>
public enum PdfSignatureLevel
{
    /// <summary>B-B: a signature, with only the signer's own claim of when it was made.</summary>
    BaselineB,
    /// <summary>B-T: a trusted time (a signature timestamp, or a later document timestamp) shows it existed then.</summary>
    BaselineT,
    /// <summary>B-LT: also the certificates and revocation information to validate it offline (the DSS).</summary>
    BaselineLT,
    /// <summary>B-LTA: also a document timestamp over that validation data, so it can be validated after the certificates and algorithms age.</summary>
    BaselineLTA,
}

/// <summary>How to validate. The defaults validate offline, with this machine's trusted roots.</summary>
public sealed record PdfSignatureValidationOptions
{
    public string? Password { get; init; }
    /// <summary>Extra trust anchors (tests, an organisation's own roots); the machine's roots are always used.</summary>
    public X509Certificate2Collection? Roots { get; init; }
    /// <summary>
    /// Fetches revocation information the document does not carry. Null (the default) keeps
    /// validation entirely offline: only what is embedded in the document is used. Set it only
    /// when the user has asked for online checks.
    /// </summary>
    public IPdfRevocationSource? OnlineRevocation { get; init; }
}

/// <summary>The checked state of one signature.</summary>
public sealed class PdfSignatureCheck
{
    public required string FieldName { get; init; }
    public required string SubFilter { get; init; }
    public required PdfSignatureVerdict Verdict { get; init; }
    public required string Summary { get; init; }
    public string SignerName { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }
    /// <summary>The time the signer's computer claimed (/M or the CMS signing-time).</summary>
    public DateTimeOffset? ClaimedTime { get; init; }
    /// <summary>The time a time-stamping authority vouched for, when the signature carries a valid timestamp.</summary>
    public DateTimeOffset? TimestampTime { get; init; }
    public string? TimestampAuthority { get; init; }
    /// <summary>A document timestamp (/SubFilter /ETSI.RFC3161) rather than a person's signature.</summary>
    public bool IsDocumentTimestamp { get; init; }
    /// <summary>A certification signature (DocMDP) and the changes it permits: 1 none, 2 form filling and signing, 3 also annotations.</summary>
    public int? CertificationLevel { get; init; }
    public bool IntegrityValid { get; init; }
    public bool CoversWholeFile { get; init; }
    /// <summary>1-based revision the signature signed; the file's last revision when it covers the whole file.</summary>
    public int Revision { get; init; }
    public PdfLaterChanges LaterChanges { get; init; }
    public PdfSignatureTrust Trust { get; init; } = PdfSignatureTrust.Unknown;
    public string TrustDetail { get; init; } = string.Empty;
    public X509Certificate2? Certificate { get; init; }
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = Array.Empty<X509Certificate2>();
    public int PageNumber { get; init; }
    public PdfEngine.Geometry.PdfRect Rect { get; init; }
    /// <summary>
    /// Whether any certificate the signature rests on (the signer's chain, and any timestamp
    /// authority's) was revoked when it mattered, judged from the document's embedded
    /// revocation information (and fetched information, when online checks were asked for).
    /// </summary>
    public PdfRevocationStatus Revocation { get; init; } = PdfRevocationStatus.NotChecked;
    public string RevocationDetail { get; init; } = string.Empty;
    /// <summary>Each certificate's revocation status, signer's chain first, trust anchors left out.</summary>
    public IReadOnlyList<PdfCertificateRevocation> CertificateRevocations { get; init; } = Array.Empty<PdfCertificateRevocation>();
    /// <summary>
    /// Long-term validation enabled: the document itself holds every certificate of the chain
    /// and verified, current revocation information for each (PAdES-B-LT), so the signature can
    /// be validated offline long after its certificates expire.
    /// </summary>
    public bool IsLtvEnabled { get; init; }
    /// <summary>Why the signature is not LTV enabled, when it is not.</summary>
    public string LtvDetail { get; init; } = string.Empty;
    /// <summary>The PAdES baseline level reached; null for a document timestamp itself, or a signature that is not intact.</summary>
    public PdfSignatureLevel? Level { get; internal set; }
    /// <summary>What the level means for this signature, and what would raise it.</summary>
    public string LevelDetail { get; internal set; } = string.Empty;
    /// <summary>
    /// The time of the latest valid document timestamp that protects this signature (or earlier
    /// document timestamp) together with its validation data: B-LTA, or a renewed archive.
    /// </summary>
    public DateTimeOffset? ArchiveTimestampTime { get; internal set; }
    /// <summary>
    /// When that protection runs out: the latest document timestamp's authority certificate
    /// expires then, so the document should be timestamped again before it.
    /// </summary>
    public DateTimeOffset? ArchiveTimestampExpires { get; internal set; }
    public bool IsArchiveTimestamped => ArchiveTimestampTime != null;

    /// <summary>What the LTV finding needs from the DSS: groups of item hashes (SHA-256), one of each group.</summary>
    internal IReadOnlyList<HashSet<string>> LtvEvidence { get; init; } = Array.Empty<HashSet<string>>();
    /// <summary>Where the signed revision ends in the file.</summary>
    internal long SignedEnd { get; init; }
}

/// <summary>
/// Validates the signatures of a document without the network: integrity (the CMS signature
/// and its message digest over the /ByteRange bytes), the signer's chain against this machine's
/// trusted roots at the signing time, embedded and document timestamps, revocation from the
/// validation data the document carries (its DSS, and the signature's own revocation archive),
/// and what later revisions changed, weighed against the certification level (ISO 32000-2
/// 12.8.2.2) when there is one. Revocation information is fetched only when the caller passes
/// an online source in <see cref="PdfSignatureValidationOptions"/>.
/// </summary>
public static partial class PdfSignatureValidator
{
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";
    private const string RevocationArchivalOid = "1.2.840.113583.1.1.8";

    /// <param name="roots">Extra trust anchors (tests, an organisation's own roots); the machine's roots are always used.</param>
    public static Task<IReadOnlyList<PdfSignatureCheck>> ValidateAsync(byte[] file, string? password = null,
        X509Certificate2Collection? roots = null, CancellationToken cancellationToken = default) =>
        ValidateAsync(file, new PdfSignatureValidationOptions { Password = password, Roots = roots }, cancellationToken);

    public static async Task<IReadOnlyList<PdfSignatureCheck>> ValidateAsync(byte[] file, PdfSignatureValidationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? password = options.Password;
        using var doc = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: cancellationToken).ConfigureAwait(false);
        var signed = SignedFields(doc);
        if (signed.Count == 0) return Array.Empty<PdfSignatureCheck>();
        var revisionEnds = RevisionEnds(file);
        int? certification = CertificationLevel(doc);
        var material = ValidationMaterial.Read(doc);
        var online = options.OnlineRevocation is { } source ? new PdfLtv.Fetcher(source, cancellationToken) : null;

        var results = new List<PdfSignatureCheck>();
        foreach (var (field, sig, range) in signed.OrderBy(s => s.Range.Length == 4 ? s.Range[2] + s.Range[3] : long.MaxValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await CheckAsync(file, password, field, sig, range, revisionEnds, certification, options.Roots, material, online, cancellationToken).ConfigureAwait(false));
        }
        await ArchiveLevelsAsync(file, password, results, cancellationToken).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// The PAdES level of each signature, and which document timestamps protect which signatures
    /// and earlier timestamps. A document timestamp protects a signature's long-term validation
    /// only if everything that validation rests on (the DSS certificates, OCSP responses and CRLs
    /// it used) was already in the revision the timestamp covers.
    /// </summary>
    private static async Task ArchiveLevelsAsync(byte[] file, string? password, List<PdfSignatureCheck> results, CancellationToken ct)
    {
        var stamps = new List<(PdfSignatureCheck Check, HashSet<string> Dss)>();
        foreach (var t in results.Where(c => c.IsDocumentTimestamp && c.IntegrityValid && c.Trust == PdfSignatureTrust.Trusted
                                             && c.Revocation != PdfRevocationStatus.Revoked && c.TimestampTime != null))
            stamps.Add((t, await DssHashesAtAsync(file, t.SignedEnd, password, ct).ConfigureAwait(false)));
        var newest = stamps.OrderBy(s => s.Check.SignedEnd).Select(s => s.Check).LastOrDefault();

        foreach (var c in results)
        {
            if (!c.IntegrityValid || c.Verdict is PdfSignatureVerdict.Invalid or PdfSignatureVerdict.Unsupported) continue;
            var covering = stamps.Where(s => s.Check.SignedEnd > c.SignedEnd && !ReferenceEquals(s.Check, c)).ToList();
            var protecting = c.IsLtvEnabled ? covering.Where(s => c.LtvEvidence.All(g => g.Overlaps(s.Dss))).ToList() : new();
            if (protecting.Count > 0)
            {
                c.ArchiveTimestampTime = protecting.Max(s => s.Check.TimestampTime);
                c.ArchiveTimestampExpires = newest?.Certificate is { } tsa ? new DateTimeOffset(tsa.NotAfter.ToUniversalTime(), TimeSpan.Zero) : null;
            }
            if (c.IsDocumentTimestamp) continue;

            bool timed = c.TimestampTime != null || covering.Count > 0;
            string renew = c.ArchiveTimestampExpires is { } until
                ? $" Timestamp the document again before {until.LocalDateTime:d}, when the latest time-stamping authority's certificate expires."
                : string.Empty;
            (c.Level, c.LevelDetail) = protecting.Count > 0
                ? (PdfSignatureLevel.BaselineLTA, $"The signature and its validation data are protected by a document timestamp of {c.ArchiveTimestampTime!.Value.LocalDateTime:g}, so it can be validated long after its certificates expire." + renew)
                : c.IsLtvEnabled && timed
                    ? (PdfSignatureLevel.BaselineLT, covering.Count > 0
                        ? "The document holds the validation data for this signature, but no document timestamp covers that data yet. Add a document timestamp to protect it (B-LTA)."
                        : "The document holds the validation data for this signature. Add a document timestamp to protect it (B-LTA).")
                    : timed
                        ? (PdfSignatureLevel.BaselineT, "A trusted time shows when the signature existed. Add long-term validation data (B-LT) and then a document timestamp (B-LTA) to keep it verifiable.")
                        : (PdfSignatureLevel.BaselineB, "Only the signer's computer vouches for the signing time. A timestamp (B-T), long-term validation data (B-LT) and a document timestamp (B-LTA) would each make the signature last longer.");
        }
    }

    /// <summary>SHA-256 of every item in the DSS of the revision ending at <paramref name="end"/>.</summary>
    private static async Task<HashSet<string>> DssHashesAtAsync(byte[] file, long end, string? password, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (end <= 0 || end > file.Length) return set;
        try
        {
            using var doc = await PdfVectorDocument.OpenAsync(file.AsSpan(0, (int)end).ToArray(), password: password, cancellationToken: ct).ConfigureAwait(false);
            var dss = PdfDss.Read(doc);
            foreach (var item in dss.Certs.Concat(dss.Ocsps).Concat(dss.Crls)
                         .Concat(dss.VriEntries.Values.SelectMany(v => v.Certs.Concat(v.Ocsps).Concat(v.Crls))))
                set.Add(Convert.ToHexString(SHA256.HashData(item.Data)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The covered revision cannot be read on its own: it protects nothing.
        }
        return set;
    }

    /// <summary>The document's signed signature fields, with their signature dictionaries and byte ranges.</summary>
    internal static List<(PdfFormField Field, PdfDictionary Sig, long[] Range)> SignedFields(PdfVectorDocument doc)
    {
        var signed = new List<(PdfFormField Field, PdfDictionary Sig, long[] Range)>();
        var form = PdfAcroForm.Read(doc);
        if (form == null) return signed;
        var r = doc.Resolver;
        foreach (var field in form.Fields.Where(f => f.Kind == PdfFormFieldKind.Signature && f.IsSigned))
        {
            var fieldDict = r.Resolve(field.ObjectNumber) as PdfDictionary;
            if (r.Resolve(fieldDict?["V"]) is not PdfDictionary sig) continue;
            long[] range = (r.Resolve(sig["ByteRange"]) as PdfArray)?.Select(o => r.Resolve(o) is PdfObject v && v.TryGetInteger(out long n) ? n : -1).ToArray() ?? Array.Empty<long>();
            signed.Add((field, sig, range));
        }
        return signed;
    }

    private static async Task<PdfSignatureCheck> CheckAsync(byte[] file, string? password, PdfFormField field, PdfDictionary sig, long[] range,
        IReadOnlyList<long> revisionEnds, int? certification, X509Certificate2Collection? roots, ValidationMaterial material,
        PdfLtv.Fetcher? online, CancellationToken ct)
    {
        string subFilter = sig.GetName("SubFilter") ?? string.Empty;
        var widget = field.Widgets.FirstOrDefault();
        string? reason = Text(sig["Reason"]), location = Text(sig["Location"]), contact = Text(sig["ContactInfo"]);
        DateTimeOffset? claimed = ParsePdfDate(Text(sig["M"]));
        bool isDocTimestamp = subFilter == "ETSI.RFC3161" || sig.GetName("Type") == "DocTimeStamp";
        int? level = IsCertification(sig) ? certification : null;

        PdfSignatureCheck Fail(PdfSignatureVerdict verdict, string summary, string signer = "") => new()
        {
            FieldName = field.FullName, SubFilter = subFilter, Verdict = verdict, Summary = summary, SignerName = signer,
            Reason = reason, Location = location, ContactInfo = contact, ClaimedTime = claimed, IsDocumentTimestamp = isDocTimestamp,
            CertificationLevel = level, PageNumber = widget?.PageNumber ?? 0, Rect = widget?.Rect ?? default,
        };

        if (range.Length != 4 || range[0] != 0 || range.Any(v => v < 0) || range[0] + range[1] > range[2] || range[2] + range[3] > file.Length)
            return Fail(PdfSignatureVerdict.Invalid, "The signature's byte range is damaged or does not lie within the file.");

        // The signed bytes, and the signature itself as it stands in the file (the gap between the ranges).
        byte[] signedBytes = new byte[range[1] + range[3]];
        Array.Copy(file, range[0], signedBytes, 0, range[1]);
        Array.Copy(file, range[2], signedBytes, range[1], range[3]);
        byte[]? blob = ContentsFromGap(file, range[1], range[2], trim: true);
        if (blob == null || blob.Length == 0)
            return Fail(PdfSignatureVerdict.Invalid, "The signature has no signature value.");

        if (subFilter is "adbe.x509.rsa_sha1")
            return Fail(PdfSignatureVerdict.Unsupported, "This signature uses the legacy adbe.x509.rsa_sha1 format, which is not checked.");

        SignedCms cms;
        try
        {
            cms = new SignedCms();
            cms.Decode(blob);
            if (cms.ContentInfo.Content.Length == 0)
            {
                cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
                cms.Decode(blob);
            }
        }
        catch (CryptographicException ex)
        {
            return Fail(PdfSignatureVerdict.Invalid, $"The signature value could not be read: {ex.Message}");
        }
        if (cms.SignerInfos.Count == 0)
            return Fail(PdfSignatureVerdict.Invalid, "The signature has no signer.");
        var signerInfo = cms.SignerInfos[0];
        var certificate = signerInfo.Certificate;
        string signerName = certificate?.GetNameInfo(X509NameType.SimpleName, false) ?? Text(sig["Name"]) ?? string.Empty;

        // Integrity.
        DateTimeOffset? timestampTime = null;
        string? tsa = null;
        var timestampAuthorities = new List<(X509Certificate2 Certificate, DateTimeOffset Time, X509Certificate2Collection Included)>();
        bool integrity;
        string integrityProblem = string.Empty;
        if (isDocTimestamp)
        {
            X509Certificate2? tsaCert = null;
            integrity = Rfc3161TimestampToken.TryDecode(blob, out var token, out _) &&
                        token!.VerifySignatureForData(signedBytes, out tsaCert, cms.Certificates);
            if (integrity)
            {
                timestampTime = token!.TokenInfo.Timestamp;
                tsa = tsaCert?.GetNameInfo(X509NameType.SimpleName, false);
                certificate = tsaCert;
                signerName = tsa ?? signerName;
            }
            else integrityProblem = "The document timestamp does not match the document.";
        }
        else
        {
            integrity = DigestMatches(cms, signerInfo, signedBytes, subFilter, out integrityProblem);
            if (integrity)
            {
                try { cms.CheckSignature(verifySignatureOnly: true); }
                catch (CryptographicException ex) { integrity = false; integrityProblem = $"The signature is not cryptographically valid: {ex.Message}"; }
            }
            if (integrity)
            {
                foreach (CryptographicAttributeObject attr in signerInfo.UnsignedAttributes)
                    if (attr.Oid.Value == TimestampTokenOid && attr.Values.Count > 0 &&
                        Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _) &&
                        token!.VerifySignatureForSignerInfo(signerInfo, out var tsaCert))
                    {
                        timestampTime = token.TokenInfo.Timestamp;
                        tsa = tsaCert?.GetNameInfo(X509NameType.SimpleName, false);
                        if (tsaCert != null) timestampAuthorities.Add((tsaCert, token.TokenInfo.Timestamp, token.AsSignedCms().Certificates));
                    }
                foreach (CryptographicAttributeObject attr in signerInfo.SignedAttributes)
                    if (attr.Oid.Value == SigningTimeOid && attr.Values.Count > 0 && claimed == null)
                        claimed = new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime;
            }
        }

        // Which revision it signed, and what came after.
        long end = range[2] + range[3];
        bool whole = end >= file.Length - 2 || file.AsSpan((int)end).Trim(" \r\n\t\0"u8).IsEmpty;
        int revision = whole ? revisionEnds.Count : Math.Max(1, revisionEnds.Count(e => e <= end + 2));
        var later = PdfLaterChanges.None;
        if (!whole && integrity)
            later = await LaterChangesAsync(file, (int)end, password, ct).ConfigureAwait(false);

        // Trust, at the time the signature was made (a trusted timestamp's time wins over the claimed one).
        // The chain may use the certificates the document's DSS holds, and, with online checks, issuers fetched for it.
        DateTimeOffset signingTime = timestampTime ?? claimed ?? DateTimeOffset.Now;
        var known = new X509Certificate2Collection(cms.Certificates);
        known.AddRange(material.Certificates.ToArray());
        foreach (var t in timestampAuthorities) known.AddRange(t.Included);
        var problems = new List<string>();
        if (online != null && certificate != null)
            foreach (var c in await PdfLtv.BuildPathAsync(certificate, known.Cast<X509Certificate2>().ToList(), online, problems).ConfigureAwait(false))
                if (!known.Cast<X509Certificate2>().Any(k => PdfX509.SameCertificate(k, c))) known.Add(c);
        var (trust, trustDetail, chain) = certificate != null
            ? Trust(certificate, known, signingTime, roots, isDocTimestamp)
            : (PdfSignatureTrust.Unknown, "The signer's certificate is not in the signature.", (IReadOnlyList<X509Certificate2>)Array.Empty<X509Certificate2>());

        // Revocation, from what the document carries (and what online checks fetch, if asked for).
        var revocations = new List<PdfCertificateRevocation>();
        bool ltv = false;
        string ltvDetail = "The signature could not be checked.";
        var evidence = new List<HashSet<string>>();
        if (integrity && certificate != null)
        {
            var embedded = new HashSet<string>(StringComparer.Ordinal);
            var ownCertificates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in cms.Certificates.Cast<X509Certificate2>().Concat(timestampAuthorities.SelectMany(t => t.Included.Cast<X509Certificate2>())))
                ownCertificates.Add(Convert.ToHexString(SHA256.HashData(c.RawData)));
            embedded.UnionWith(ownCertificates);
            foreach (var c in material.Certificates) embedded.Add(Convert.ToHexString(SHA256.HashData(c.RawData)));
            var ocsps = new List<PdfOcspResponse>(material.Ocsps);
            var crls = new List<PdfCrl>(material.Crls);
            if (!isDocTimestamp) RevocationArchival(signerInfo, ocsps, crls);
            var pool = known.Cast<X509Certificate2>().ToList();
            var context = new RevocationContext(ocsps, crls, pool, embedded, online, problems)
            {
                MaterialHashes = material.Hashes, OwnCertificates = ownCertificates, Evidence = evidence,
            };

            IReadOnlyList<X509Certificate2> signerChain = chain.Count > 0 ? chain : new[] { certificate };
            // The signer's own clock cannot vouch for the time; a TSA's can (for a document timestamp, the TSA is the signer).
            var (entries, chainLtv, chainWhy) = await ChainRevocationAsync(signerChain, signingTime, trustedTime: timestampTime != null && !isDocTimestamp,
                timestampAuthority: isDocTimestamp, context).ConfigureAwait(false);
            revocations.AddRange(entries);
            ltv = chainLtv;
            ltvDetail = chainWhy;
            foreach (var (tsaCert, time, included) in timestampAuthorities)
            {
                foreach (var c in included) if (!pool.Any(p => PdfX509.SameCertificate(p, c))) pool.Add(c);
                var tsaChain = await PdfLtv.BuildPathAsync(tsaCert, pool, online, problems).ConfigureAwait(false);
                var (tsaEntries, tsaLtv, tsaWhy) = await ChainRevocationAsync(tsaChain, time, trustedTime: false, timestampAuthority: true, context).ConfigureAwait(false);
                revocations.AddRange(tsaEntries);
                if (ltv && !tsaLtv) ltvDetail = tsaWhy;
                ltv &= tsaLtv;
            }
        }
        var (revocation, revocationDetail) = Summarize(revocations, isDocTimestamp);
        if (revocation == PdfRevocationStatus.Revoked)
        {
            trust = PdfSignatureTrust.Untrusted;
            trustDetail = revocationDetail;
        }
        else if (trust == PdfSignatureTrust.Trusted && !isDocTimestamp)
            trustDetail = revocation switch
            {
                PdfRevocationStatus.Good => $"The signer's certificate was valid on {signingTime.LocalDateTime:g}, chains to a trusted root and was not revoked.",
                PdfRevocationStatus.Unknown => $"The signer's certificate was valid on {signingTime.LocalDateTime:g} and chains to a trusted root, but its revocation status is unknown.",
                _ => trustDetail,
            };

        PdfSignatureVerdict verdict;
        string summary;
        if (!integrity)
        {
            verdict = PdfSignatureVerdict.Invalid;
            summary = integrityProblem.Length > 0 ? integrityProblem : "The signature does not match the document.";
        }
        else if (whole || (later & ~(PdfLaterChanges.ValidationData | PdfLaterChanges.DocumentTimestamp)) == PdfLaterChanges.None)
        {
            verdict = PdfSignatureVerdict.Valid;
            summary = isDocTimestamp ? "The document has not been modified since it was timestamped." : "The document has not been modified since it was signed.";
            summary += (later.HasFlag(PdfLaterChanges.ValidationData), later.HasFlag(PdfLaterChanges.DocumentTimestamp)) switch
            {
                (true, true) => " Long-term validation data and a document timestamp were added afterwards.",
                (true, false) => " Long-term validation data was added afterwards.",
                (false, true) => " A document timestamp was added afterwards.",
                _ => string.Empty,
            };
        }
        else if (Permitted(later, level))
        {
            verdict = PdfSignatureVerdict.ValidWithPermittedChanges;
            summary = "The signed revision is intact. Later, the document was " + Describe(later) + ", which this signature permits.";
        }
        else
        {
            verdict = PdfSignatureVerdict.ModifiedAfterSigning;
            summary = level == 1
                ? "The document was certified to allow no changes, but it was changed after certification (" + Describe(later) + ")."
                : "The document was changed after it was signed (" + Describe(later) + "). The signed revision itself is intact.";
        }

        return new PdfSignatureCheck
        {
            FieldName = field.FullName, SubFilter = subFilter, Verdict = verdict, Summary = summary, SignerName = signerName,
            Reason = reason, Location = location, ContactInfo = contact, ClaimedTime = claimed, TimestampTime = timestampTime,
            TimestampAuthority = tsa, IsDocumentTimestamp = isDocTimestamp, CertificationLevel = level, IntegrityValid = integrity,
            CoversWholeFile = whole, Revision = revision, LaterChanges = later, Trust = trust, TrustDetail = trustDetail,
            Certificate = certificate, Chain = chain, PageNumber = widget?.PageNumber ?? 0, Rect = widget?.Rect ?? default,
            Revocation = revocation, RevocationDetail = revocationDetail, CertificateRevocations = revocations,
            IsLtvEnabled = integrity && ltv, LtvDetail = integrity && ltv ? string.Empty : ltvDetail,
            LtvEvidence = evidence, SignedEnd = end,
        };
    }

    private static bool Permitted(PdfLaterChanges later, int? level)
    {
        later &= ~(PdfLaterChanges.ValidationData | PdfLaterChanges.DocumentTimestamp); // always permitted
        if ((later & PdfLaterChanges.Other) != 0) return false;
        return level switch
        {
            1 => false,
            2 => (later & PdfLaterChanges.Annotations) == 0,
            _ => true, // level 3, or an approval signature
        };
    }

    private static string Describe(PdfLaterChanges later)
    {
        var parts = new List<string>();
        if (later.HasFlag(PdfLaterChanges.FormFields)) parts.Add("filled in");
        if (later.HasFlag(PdfLaterChanges.Signatures)) parts.Add("signed again");
        if (later.HasFlag(PdfLaterChanges.Annotations)) parts.Add("commented on");
        if (later.HasFlag(PdfLaterChanges.Metadata)) parts.Add("given new metadata");
        if (later.HasFlag(PdfLaterChanges.Other)) parts.Add("changed in its content");
        if (later.HasFlag(PdfLaterChanges.ValidationData)) parts.Add("given long-term validation data");
        if (later.HasFlag(PdfLaterChanges.DocumentTimestamp)) parts.Add("timestamped");
        return parts.Count switch
        {
            0 => "changed",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    private static bool DigestMatches(SignedCms cms, SignerInfo signerInfo, byte[] signedBytes, string subFilter, out string problem)
    {
        problem = string.Empty;
        using var hash = HashFor(signerInfo.DigestAlgorithm.Value);
        if (hash == null)
        {
            problem = $"The signature uses an unsupported digest algorithm ({signerInfo.DigestAlgorithm.Value}).";
            return false;
        }
        if (subFilter == "adbe.pkcs7.sha1")
        {
            // The signed content is the SHA-1 of the byte ranges.
            bool ok = CryptographicOperations.FixedTimeEquals(cms.ContentInfo.Content, SHA1.HashData(signedBytes));
            if (!ok) problem = "The document has been modified since it was signed: its digest does not match.";
            return ok;
        }
        byte[] actual = hash.ComputeHash(signedBytes);
        foreach (CryptographicAttributeObject attr in signerInfo.SignedAttributes)
        {
            if (attr.Oid.Value != "1.2.840.113549.1.9.4" || attr.Values.Count == 0) continue;
            byte[] expected = AsnDecoder.ReadOctetString(attr.Values[0].RawData, AsnEncodingRules.BER, out _);
            bool ok = CryptographicOperations.FixedTimeEquals(expected, actual);
            if (!ok) problem = "The document has been modified since it was signed: its digest does not match.";
            return ok;
        }
        // No signed attributes: the signature is over the content directly; CheckSignature decides.
        return true;
    }

    private static HashAlgorithm? HashFor(string? oid) => oid switch
    {
        "1.3.14.3.2.26" => SHA1.Create(),
        "2.16.840.1.101.3.4.2.1" => SHA256.Create(),
        "2.16.840.1.101.3.4.2.2" => SHA384.Create(),
        "2.16.840.1.101.3.4.2.3" => SHA512.Create(),
        _ => null,
    };

    private static (PdfSignatureTrust, string, IReadOnlyList<X509Certificate2>) Trust(X509Certificate2 certificate, X509Certificate2Collection included,
        DateTimeOffset at, X509Certificate2Collection? roots, bool timestampSigner)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // offline: nothing is fetched
        chain.ChainPolicy.DisableCertificateDownloads = true;          // not even missing issuers (AIA)
        chain.ChainPolicy.VerificationTime = at.LocalDateTime;
        chain.ChainPolicy.ExtraStore.AddRange(included);
        if (roots is { Count: > 0 })
        {
            chain.ChainPolicy.ExtraStore.AddRange(roots);
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        }
        bool built = chain.Build(certificate);
        if (!built && roots is { Count: > 0 })
        {
            // Not under the extra roots: try the machine's own roots.
            chain.Reset();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = at.LocalDateTime;
            built = chain.Build(certificate);
        }
        var elements = chain.ChainElements.Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData)).ToList();
        if (built)
            return (PdfSignatureTrust.Trusted, timestampSigner
                ? "The time-stamping authority's certificate chains to a trusted root."
                : $"The signer's certificate was valid on {at.LocalDateTime:g} and chains to a trusted root (revocation not checked).", elements);
        var status = chain.ChainStatus.Select(s => s.Status).Aggregate(X509ChainStatusFlags.NoError, (a, b) => a | b);
        string detail = status.HasFlag(X509ChainStatusFlags.UntrustedRoot) || status.HasFlag(X509ChainStatusFlags.PartialChain)
            ? "The signer's identity is unknown: the certificate does not chain to a root trusted on this computer."
            : status.HasFlag(X509ChainStatusFlags.NotTimeValid)
                ? $"The signer's certificate was not valid on {at.LocalDateTime:g}."
                : status.HasFlag(X509ChainStatusFlags.Revoked)
                    ? "The signer's certificate has been revoked."
                    : "The signer's certificate could not be verified: " + string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0));
        return (PdfSignatureTrust.Untrusted, detail, elements);
    }

    /// <summary>
    /// What later revisions changed: every object whose definition differs between the signed
    /// revision and the final file, classified by what it is.
    /// </summary>
    internal static async Task<PdfLaterChanges> LaterChangesAsync(byte[] file, int revisionEnd, string? password, CancellationToken ct)
    {
        PdfVectorDocument before;
        try
        {
            before = await PdfVectorDocument.OpenAsync(file.AsSpan(0, revisionEnd).ToArray(), password: password, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PdfLaterChanges.Other; // the signed revision cannot be read on its own
        }
        using (before)
        using (var after = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: ct).ConfigureAwait(false))
        {
            var rb = before.Resolver;
            var ra = after.Resolver;
            var appearances = AppearanceObjects(after);
            var dssBefore = DssObjects(before);
            var dssAfter = DssObjects(after);
            int beforeRoot = (before.XrefTable.Trailer?["Root"] as PdfIndirectRef)?.ObjectNumber ?? -1;
            int afterRoot = (after.XrefTable.Trailer?["Root"] as PdfIndirectRef)?.ObjectNumber ?? -1;
            var changes = PdfLaterChanges.None;
            if (beforeRoot != afterRoot) return PdfLaterChanges.Other;

            foreach (var (number, entry) in after.XrefTable.Entries)
            {
                if (!entry.IsInUse) continue;
                before.XrefTable.Entries.TryGetValue(number, out var old);
                if (old.IsInUse && old.ByteOffset == entry.ByteOffset && old.CompressedStreamObjectNumber == entry.CompressedStreamObjectNumber
                    && old.CompressedStreamIndex == entry.CompressedStreamIndex && old.GenerationNumber == entry.GenerationNumber)
                    continue; // same definition
                var now = ra.Resolve(number);
                var was = old.IsInUse ? rb.Resolve(number) : null;
                if (now == null) continue;
                if (was != null && Same(was, now, rb, ra, 0)) continue; // rewritten, but the same (writers reorder keys, recompress streams)
                changes |= Classify(number, was, now, afterRoot, appearances, after, dssBefore, dssAfter);
                if (changes.HasFlag(PdfLaterChanges.Other)) return changes;
            }
            // Pages that disappeared, or were reordered, are content changes.
            if (before.PageCount != after.PageCount) changes |= PdfLaterChanges.Other;
            return changes;
        }
    }

    private static PdfLaterChanges Classify(int number, PdfObject? was, PdfObject now, int root, HashSet<int> appearances, PdfVectorDocument after,
        HashSet<int> dssBefore, HashSet<int> dssAfter)
    {
        var dict = now as PdfDictionary ?? (now as PdfStream)?.Dictionary;
        string? type = dict?.GetName("Type");
        string? subtype = dict?.GetName("Subtype");

        if (type is "XRef" or "ObjStm") return PdfLaterChanges.None;
        if (type is "Sig" or "DocTimeStamp" || dict?["ByteRange"] != null)
            return was == null && IsDocTimestamp(dict) ? PdfLaterChanges.DocumentTimestamp : PdfLaterChanges.Signatures;
        // Validation data: objects of the DSS that are new, or were validation data already. An
        // existing object of any other kind does not become harmless by being listed in the DSS.
        if (dssAfter.Contains(number) && (was == null || dssBefore.Contains(number))) return PdfLaterChanges.ValidationData;
        if (type == "Metadata" || subtype == "XML") return PdfLaterChanges.Metadata;
        if (number == root && was is PdfDictionary oldRoot && dict != null)
            return SameExcept(oldRoot, dict, "AcroForm", "DSS", "Metadata", "Perms", "NeedsRendering") ? PdfLaterChanges.None : PdfLaterChanges.Other;
        if (type == "Page" && was is PdfDictionary oldPage && dict != null)
        {
            if (!SameExcept(oldPage, dict, "Annots")) return PdfLaterChanges.Other;
            return AnnotsChange(oldPage["Annots"], dict["Annots"], after);
        }
        if (dict?["Fields"] != null && was is PdfDictionary oldForm && FormGainedOnlyTimestamps(oldForm, dict, after))
            return PdfLaterChanges.DocumentTimestamp;
        if (subtype == "Widget" || dict?["FT"] != null || dict?["Fields"] != null || IsFieldNode(dict))
            return dict?.GetName("FT") == "Sig" && was == null
                ? IsInvisibleTimestampField(dict, after.Resolver) ? PdfLaterChanges.DocumentTimestamp : PdfLaterChanges.Signatures
                : PdfLaterChanges.FormFields;
        if (type == "Annot" || subtype is "Text" or "FreeText" or "Line" or "Square" or "Circle" or "Polygon" or "PolyLine" or "Highlight"
                or "Underline" or "Squiggly" or "StrikeOut" or "Stamp" or "Caret" or "Ink" or "Popup" or "FileAttachment")
            return PdfLaterChanges.Annotations;
        if (appearances.Contains(number)) return PdfLaterChanges.FormFields; // a widget's or annotation's appearance
        if (now is PdfArray && IsAnnotsArray(number, after)) return AnnotsChange(was, now, after);
        if (IsInfoDictionary(number, after)) return PdfLaterChanges.Metadata;
        return PdfLaterChanges.Other;
    }

    /// <summary>What a page's changed /Annots added or removed: widgets are form (or signature) changes, anything else is commenting.</summary>
    private static PdfLaterChanges AnnotsChange(PdfObject? before, PdfObject? after, PdfVectorDocument doc)
    {
        if (Same(before, after, null, null, 0)) return PdfLaterChanges.None;
        var r = doc.Resolver;
        var old = before as PdfArray;
        var now = r.Resolve(after) as PdfArray;
        if (now == null) return PdfLaterChanges.Annotations;
        var changes = PdfLaterChanges.None;
        var oldItems = old?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
        foreach (var item in now)
        {
            if (oldItems.Any(o => Same(o, item, null, null, 0))) continue;
            var annot = r.Resolve(item) as PdfDictionary;
            changes |= annot?.GetName("Subtype") == "Widget"
                ? annot.GetName("FT") == "Sig"
                    ? IsInvisibleTimestampField(annot, r) ? PdfLaterChanges.DocumentTimestamp : PdfLaterChanges.Signatures
                    : PdfLaterChanges.FormFields
                : PdfLaterChanges.Annotations;
        }
        if (oldItems.Count > 0 && oldItems.Any(o => !now.Any(n => Same(o, n, null, null, 0))))
            changes |= PdfLaterChanges.Annotations; // something was removed
        return changes;
    }

    private static bool IsDocTimestamp(PdfDictionary? sig) =>
        sig != null && (sig.GetName("Type") == "DocTimeStamp" || sig.GetName("SubFilter") == "ETSI.RFC3161");

    /// <summary>
    /// A signature field whose value is a document timestamp and that shows nothing (no
    /// appearance, or no area). A timestamp field that draws something is not let through as a
    /// timestamp: under a no-changes certification, it would be a way to put new content on a page.
    /// </summary>
    private static bool IsInvisibleTimestampField(PdfDictionary? field, Parsing.PdfObjectResolver r)
    {
        if (field == null || !IsDocTimestamp(r.Resolve(field["V"]) as PdfDictionary)) return false;
        if (field["AP"] == null) return true;
        if (r.Resolve(field["Rect"]) is not PdfArray rect || rect.Count < 4) return true;
        double Num(int i) => r.Resolve(rect[i]) is PdfObject o && o.TryGetNumber(out double v) ? v : 0;
        return Math.Abs(Num(2) - Num(0)) < 1e-6 || Math.Abs(Num(3) - Num(1)) < 1e-6;
    }

    /// <summary>The form dictionary changed only by gaining invisible document timestamp fields (and signature flags).</summary>
    private static bool FormGainedOnlyTimestamps(PdfDictionary before, PdfDictionary after, PdfVectorDocument doc)
    {
        if (!SameExcept(before, after, "Fields", "SigFlags")) return false;
        var r = doc.Resolver;
        var oldFields = (before["Fields"] as PdfArray)?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
        if (r.Resolve(after["Fields"]) is not PdfArray newFields) return false;
        if (oldFields.Any(o => !newFields.Any(n => Same(o, n, null, null, 0)))) return false; // a field was removed
        bool added = false;
        foreach (var item in newFields)
        {
            if (oldFields.Any(o => Same(o, item, null, null, 0))) continue;
            if (!IsInvisibleTimestampField(r.Resolve(item) as PdfDictionary, r)) return false;
            added = true;
        }
        return added;
    }

    private static bool IsFieldNode(PdfDictionary? d) => d != null && d["T"] != null && (d["Kids"] != null || d["Parent"] != null) && d["Type"] == null;

    private static bool SameExcept(PdfDictionary a, PdfDictionary b, params string[] keys)
    {
        var skip = new HashSet<string>(keys);
        var ka = a.Entries.Keys.Where(k => !skip.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var kb = b.Entries.Keys.Where(k => !skip.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!ka.SequenceEqual(kb)) return false;
        foreach (var k in ka)
            if (!Same(a.Entries[k], b.Entries[k], null, null, 0)) return false;
        return true;
    }

    /// <summary>
    /// The same PDF object in meaning: dictionaries regardless of key order, numbers by value,
    /// streams by their decoded data. Indirect references compare by number (the objects they
    /// point to are compared on their own).
    /// </summary>
    private static bool Same(PdfObject? a, PdfObject? b, Parsing.PdfObjectResolver? ra, Parsing.PdfObjectResolver? rb, int depth)
    {
        if (depth > 32) return false;
        switch (a, b)
        {
            case (null, null): return true;
            case (null, _) or (_, null): return false;
            case (PdfIndirectRef x, PdfIndirectRef y): return x.ObjectNumber == y.ObjectNumber;
            case (PdfName x, PdfName y): return x.Value == y.Value;
            case (PdfString x, PdfString y): return x.RawBytes.Span.SequenceEqual(y.RawBytes.Span);
            case (PdfBoolean x, PdfBoolean y): return x.Value == y.Value;
            case (PdfNull, PdfNull): return true;
            case (PdfArray x, PdfArray y):
                if (x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++)
                    if (!Same(x[i], y[i], ra, rb, depth + 1)) return false;
                return true;
            case (PdfStream x, PdfStream y):
                if (!SameEntries(x.Dictionary, y.Dictionary, ra, rb, depth, "Length", "Filter", "DecodeParms", "DL"))
                    return false;
                try
                {
                    byte[] dx = new Streams.PdfStreamDecoder(null, ra != null ? ra.Resolve : null).DecodeStream(x);
                    byte[] dy = new Streams.PdfStreamDecoder(null, rb != null ? rb.Resolve : null).DecodeStream(y);
                    return dx.AsSpan().SequenceEqual(dy);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return x.GetRawBytes().Span.SequenceEqual(y.GetRawBytes().Span);
                }
            case (PdfDictionary x, PdfDictionary y):
                return SameEntries(x, y, ra, rb, depth);
            default:
                return a.TryGetNumber(out double na) && b.TryGetNumber(out double nb) && Math.Abs(na - nb) < 1e-6;
        }
    }

    private static bool SameEntries(PdfDictionary x, PdfDictionary y, Parsing.PdfObjectResolver? ra, Parsing.PdfObjectResolver? rb, int depth, params string[] ignore)
    {
        var skip = new HashSet<string>(ignore);
        var kx = x.Entries.Keys.Where(k => !skip.Contains(k)).ToHashSet();
        var ky = y.Entries.Keys.Where(k => !skip.Contains(k)).ToHashSet();
        if (!kx.SetEquals(ky)) return false;
        foreach (var k in kx)
            if (!Same(x.Entries[k], y.Entries[k], ra, rb, depth + 1)) return false;
        return true;
    }

    /// <summary>Object numbers of every annotation appearance stream (and state dictionaries) in the document.</summary>
    private static HashSet<int> AppearanceObjects(PdfVectorDocument doc)
    {
        var set = new HashSet<int>();
        var r = doc.Resolver;
        foreach (var page in doc.PageTree.Pages)
        {
            if (page.Dictionary["Annots"] is PdfIndirectRef annotsRef) set.Add(annotsRef.ObjectNumber);
            if (r.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (var a in annots)
            {
                if (r.Resolve(a) is not PdfDictionary annot || annot["AP"] is not { } apObj) continue;
                if (apObj is PdfIndirectRef apRef) set.Add(apRef.ObjectNumber);
                if (r.Resolve(apObj) is not PdfDictionary ap) continue;
                foreach (var key in new[] { "N", "R", "D" })
                {
                    var entry = ap[key];
                    if (entry is PdfIndirectRef er) set.Add(er.ObjectNumber);
                    if (r.Resolve(entry) is PdfDictionary states)
                        foreach (var s in states.Entries.Values)
                            if (s is PdfIndirectRef sr) set.Add(sr.ObjectNumber);
                }
            }
        }
        return set;
    }

    private static bool IsAnnotsArray(int number, PdfVectorDocument doc) =>
        doc.PageTree.Pages.Any(p => p.Dictionary["Annots"] is PdfIndirectRef a && a.ObjectNumber == number);

    private static bool IsInfoDictionary(int number, PdfVectorDocument doc) =>
        doc.XrefTable.Trailer?["Info"] is PdfIndirectRef info && info.ObjectNumber == number;

    /// <summary>
    /// Object numbers of the Document Security Store: the DSS dictionary, its arrays, its VRI
    /// dictionary and entries, and the certificate, OCSP, CRL and timestamp streams they list.
    /// </summary>
    private static HashSet<int> DssObjects(PdfVectorDocument doc)
    {
        var set = new HashSet<int>();
        var r = doc.Resolver;
        if (r.Resolve(doc.XrefTable.Trailer?["Root"]) is not PdfDictionary root) return set;
        void Note(PdfObject? o) { if (o is PdfIndirectRef ir) set.Add(ir.ObjectNumber); }
        void Lists(PdfDictionary d, params string[] keys)
        {
            foreach (var key in keys)
            {
                Note(d[key]);
                if (r.Resolve(d[key]) is PdfArray list) foreach (var item in list) Note(item);
            }
        }
        Note(root["DSS"]);
        if (r.Resolve(root["DSS"]) is not PdfDictionary dss) return set;
        Lists(dss, "Certs", "OCSPs", "CRLs");
        Note(dss["VRI"]);
        if (r.Resolve(dss["VRI"]) is PdfDictionary vri)
            foreach (var entry in vri.Entries.Values)
            {
                Note(entry);
                if (r.Resolve(entry) is PdfDictionary e) Lists(e, "Cert", "OCSP", "CRL", "TS");
            }
        return set;
    }


    /// <summary>Byte offsets just past each revision's %%EOF, in order.</summary>
    public static List<long> RevisionEnds(byte[] file)
    {
        var ends = new List<long>();
        byte[] eof = "%%EOF"u8.ToArray();
        int at = 0;
        while ((at = file.AsSpan(at).IndexOf(eof) is int i and >= 0 ? at + i : -1) >= 0)
        {
            long end = at + eof.Length;
            while (end < file.Length && (file[end] == '\r' || file[end] == '\n')) end++;
            ends.Add(end);
            at += eof.Length;
        }
        if (ends.Count == 0) ends.Add(file.Length);
        return ends;
    }

    /// <summary>The DocMDP permission level of the document's certification signature, if any.</summary>
    private static int? CertificationLevel(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        if (r.Resolve(doc.XrefTable.Trailer?["Root"]) is not PdfDictionary root) return null;
        if (r.Resolve(root["Perms"]) is not PdfDictionary perms || r.Resolve(perms["DocMDP"]) is not PdfDictionary sig) return null;
        return DocMdpLevel(sig, r) ?? 2;
    }

    private static bool IsCertification(PdfDictionary sig) =>
        sig["Reference"] is not null && DocMdpLevel(sig, null) != null;

    private static int? DocMdpLevel(PdfDictionary sig, Parsing.PdfObjectResolver? r)
    {
        PdfObject? Res(PdfObject? o) => r != null ? r.Resolve(o) : o;
        if (Res(sig["Reference"]) is not PdfArray refs) return null;
        foreach (var item in refs)
            if (Res(item) is PdfDictionary reference && reference.GetName("TransformMethod") == "DocMDP")
            {
                var p = Res(reference["TransformParams"]) is PdfDictionary tp && Res(tp["P"]) is PdfObject pv && pv.TryGetInteger(out long level) ? (int)level : 2;
                return Math.Clamp(p, 1, 3);
            }
        return null;
    }

    /// <summary>
    /// The signature value as written in the file: the hex string between the ranges. Trimmed,
    /// the DER value alone; untrimmed, the whole /Contents string with its padding (what a VRI
    /// key is the SHA-1 of).
    /// </summary>
    internal static byte[]? ContentsFromGap(byte[] file, long gapStart, long gapEnd, bool trim)
    {
        if (gapEnd - gapStart < 2 || file[gapStart] != '<' || file[gapEnd - 1] != '>') return null;
        var hex = new StringBuilder((int)(gapEnd - gapStart));
        for (long i = gapStart + 1; i < gapEnd - 1; i++)
        {
            char c = (char)file[i];
            if (Uri.IsHexDigit(c)) hex.Append(c);
            else if (!char.IsWhiteSpace(c)) return null;
        }
        if (hex.Length % 2 == 1) hex.Append('0');
        byte[] bytes = Convert.FromHexString(hex.ToString());
        if (!trim) return bytes;
        // The placeholder is zero-padded; the DER length says where the value ends.
        try
        {
            AsnDecoder.ReadEncodedValue(bytes, AsnEncodingRules.BER, out _, out _, out int consumed);
            return bytes.AsSpan(0, consumed).ToArray();
        }
        catch (AsnContentException)
        {
            return bytes;
        }
    }

    private static string? Text(PdfObject? o) => o is PdfString s ? s.AsDecodedString() : null;

    /// <summary>A PDF date (7.9.4), or null.</summary>
    internal static DateTimeOffset? ParsePdfDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        int Part(int start, int len, int fallback) =>
            s.Length >= start + len && int.TryParse(s.AsSpan(start, len), out int v) ? v : fallback;
        if (s.Length < 4) return null;
        int year = Part(0, 4, 0), month = Part(4, 2, 1), day = Part(6, 2, 1), hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
        var offset = TimeSpan.Zero;
        if (s.Length > 14 && s[14] is '+' or '-')
        {
            int oh = Part(15, 2, 0), om = s.Length >= 20 ? Part(18, 2, 0) : 0;
            offset = new TimeSpan(oh, om, 0) * (s[14] == '-' ? -1 : 1);
        }
        try { return new DateTimeOffset(year, month, day, hour, minute, second, offset); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
