using System.Text;
using PdfEngine.Vector.Signatures;

namespace PdfViewer.ViewModels;

/// <summary>One signature as the signature panel shows it.</summary>
public sealed class SignatureItemViewModel
{
    public required PdfSignatureCheck Check { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    /// <summary>"ok", "warn" or "bad": the colour of the status mark.</summary>
    public required string Level { get; init; }
    public required string Summary { get; init; }
    public required string Identity { get; init; }
    public required string When { get; init; }
    public string Details { get; init; } = string.Empty;
    /// <summary>"LTV enabled" or "Not LTV enabled": whether the document alone can prove the signature valid years from now.</summary>
    public string Ltv => Check.IsLtvEnabled ? "LTV enabled" : "Not LTV enabled";

    /// <summary>The PAdES level reached ("PAdES B-LTA"), or for a document timestamp whether a later one renews it; empty when neither applies.</summary>
    public string PadesLevel => LevelSuffix(Check);
    /// <summary>What LTV means here, and why it is missing when it is.</summary>
    public string LtvDetail => (Check.IsLtvEnabled
        ? "The document holds the certificates and revocation information needed to validate this signature without contacting anyone."
        : Check.LtvDetail.Length > 0 ? Check.LtvDetail : "The document does not hold the revocation information needed to validate this signature in the long term.")
        + (Check.LevelDetail.Length > 0 ? " " + Check.LevelDetail : string.Empty);

    /// <summary>"PAdES B-B" .. "PAdES B-LTA", as ETSI EN 319 142-1 names the levels.</summary>
    public static string LevelName(PdfSignatureLevel level) => level switch
    {
        PdfSignatureLevel.BaselineB => "PAdES B-B",
        PdfSignatureLevel.BaselineT => "PAdES B-T",
        PdfSignatureLevel.BaselineLT => "PAdES B-LT",
        _ => "PAdES B-LTA",
    };

    private static string LevelSuffix(PdfSignatureCheck c) =>
        c.Level is { } level ? LevelName(level)
        : c.IsDocumentTimestamp && c.IsArchiveTimestamped ? "Renewed by a later document timestamp"
        : string.Empty;
    public int PageNumber => Check.PageNumber;
    public bool HasPage => Check.PageNumber > 0 && Check.Rect.Width > 0;

    public static SignatureItemViewModel From(PdfSignatureCheck c, int revisions)
    {
        bool intact = c.Verdict is PdfSignatureVerdict.Valid or PdfSignatureVerdict.ValidWithPermittedChanges;
        bool trusted = c.Trust == PdfSignatureTrust.Trusted;
        string title = c.IsDocumentTimestamp ? "Document timestamp"
            : c.CertificationLevel != null ? $"Certified by {c.SignerName}" : $"Signed by {c.SignerName}";
        string status = c.Verdict switch
        {
            PdfSignatureVerdict.Invalid => "Invalid signature",
            PdfSignatureVerdict.ModifiedAfterSigning => "Document changed after signing",
            PdfSignatureVerdict.Unsupported => "Not checked",
            _ when c.Revocation == PdfRevocationStatus.Revoked => "Invalid: a certificate it relies on was revoked",
            _ when !trusted => "Valid, but the signer's identity is unknown",
            PdfSignatureVerdict.ValidWithPermittedChanges => "Valid, with permitted changes since",
            _ => "Valid",
        };
        string level = c.Verdict == PdfSignatureVerdict.Unsupported ? "warn"
            : !intact || c.Revocation == PdfRevocationStatus.Revoked ? "bad" : trusted ? "ok" : "warn";

        string when = c.IsDocumentTimestamp && c.TimestampTime is { } dts
            ? $"Timestamped {dts.LocalDateTime:f} by {c.TimestampAuthority ?? "a time-stamping authority"}"
            : c.TimestampTime is { } ts
            ? $"Signed {ts.LocalDateTime:f}, timestamped by {c.TimestampAuthority ?? "a time-stamping authority"}"
            : c.ClaimedTime is { } claimed ? $"Signed {claimed.LocalDateTime:f} (time from the signer's computer)" : "Signing time not given";

        var details = new StringBuilder();
        if (c.CertificationLevel is int cl)
            details.AppendLine(cl switch
            {
                1 => "Certified: no changes are allowed.",
                2 => "Certified: filling in forms and signing are allowed.",
                _ => "Certified: filling in forms, signing and commenting are allowed.",
            });
        if (!string.IsNullOrWhiteSpace(c.Reason)) details.AppendLine($"Reason: {c.Reason}");
        if (!string.IsNullOrWhiteSpace(c.Location)) details.AppendLine($"Location: {c.Location}");
        if (!string.IsNullOrWhiteSpace(c.ContactInfo)) details.AppendLine($"Contact: {c.ContactInfo}");
        details.AppendLine($"Signed revision {c.Revision} of {revisions}, field \"{c.FieldName}\" ({c.SubFilter}).");
        if (c.Certificate is { } cert)
            details.AppendLine($"Certificate: {cert.Subject}, issued by {cert.Issuer}, valid {cert.NotBefore:d} to {cert.NotAfter:d}.");
        if (!string.IsNullOrEmpty(c.RevocationDetail)) details.AppendLine($"Revocation: {c.RevocationDetail}");
        foreach (var r in c.CertificateRevocations)
            details.AppendLine($"  {r.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)}: {r.Detail}");
        if (!c.IsLtvEnabled && c.LtvDetail.Length > 0) details.AppendLine($"Not LTV enabled: {c.LtvDetail}");
        if (c.Level is { } lvl) details.AppendLine($"{LevelName(lvl)}: {c.LevelDetail}");
        if (c.ArchiveTimestampTime is { } archive)
            details.AppendLine(c.ArchiveTimestampExpires is { } until
                ? $"Protected by a document timestamp of {archive.LocalDateTime:g}; timestamp the document again before {until.LocalDateTime:d} to keep it protected."
                : $"Protected by a document timestamp of {archive.LocalDateTime:g}.");

        return new SignatureItemViewModel
        {
            Check = c,
            Title = title,
            Status = status,
            Level = level,
            Summary = c.Summary,
            Identity = c.TrustDetail,
            When = when,
            Details = details.ToString().TrimEnd(),
        };
    }
}
