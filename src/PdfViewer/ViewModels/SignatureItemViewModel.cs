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
            _ when !trusted => "Valid, but the signer's identity is unknown",
            PdfSignatureVerdict.ValidWithPermittedChanges => "Valid, with permitted changes since",
            _ => "Valid",
        };
        string level = c.Verdict == PdfSignatureVerdict.Unsupported ? "warn" : !intact ? "bad" : trusted ? "ok" : "warn";

        string when = c.TimestampTime is { } ts
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
            details.Append($"Certificate: {cert.Subject}, issued by {cert.Issuer}, valid {cert.NotBefore:d} to {cert.NotAfter:d}.");

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
