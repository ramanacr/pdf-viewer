using System.Runtime.CompilerServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Redaction;
using PdfEngine.Signatures;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Signatures;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Digital signatures in the engine-neutral model, from <see cref="PdfSignatureValidator"/>: the
/// CMS checked against the /ByteRange of the current revision, the certificate chain against the
/// machine's roots, and what changed after each signature. No PDFium.
/// </summary>
/// <remarks>
/// Status follows <c>PdfiumSignatureService</c> where both can tell: a signature whose bytes no
/// longer match is DocumentModified, one that is not a valid signature is Invalid, an intact one
/// over the whole file is Valid or Untrusted by its chain. The difference is later revisions:
/// PDFium calls any signature that does not cover the whole file DocumentModified, while here a
/// later revision that only does what the signature permits (form filling, another signature,
/// comments, validation data) keeps it Valid or Untrusted, as Acrobat reports it.
/// </remarks>
public sealed class PdfSignatureService : IPdfSignatureService
{
    public async ValueTask<IReadOnlyList<SignatureInfo>> GetSignaturesAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        var checks = await PdfSignatureValidator.ValidateAsync(doc.Bytes, doc.Password, cancellationToken: cancellationToken).ConfigureAwait(false);
        return checks.Select(ToInfo).ToList();
    }

    public async ValueTask<SignatureStatus> VerifySignatureAsync(IPdfDocument document, string fieldName, CancellationToken cancellationToken = default)
    {
        var all = await GetSignaturesAsync(document, cancellationToken).ConfigureAwait(false);
        if (all.Count == 0) return SignatureStatus.Unknown;
        var match = all.FirstOrDefault(s => string.Equals(s.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
        return match?.Status ?? SignatureStatus.Unknown;
    }

    internal static SignatureInfo ToInfo(PdfSignatureCheck c) => new()
    {
        FieldName = c.FieldName,
        SignerName = c.SignerName,
        Reason = c.Reason ?? string.Empty,
        Location = c.Location ?? string.Empty,
        ContactInfo = c.ContactInfo ?? string.Empty,
        SigningTime = (c.TimestampTime ?? c.ClaimedTime)?.UtcDateTime,
        Status = Status(c),
        StatusMessage = c.Trust == PdfSignatureTrust.Untrusted && c.Verdict is PdfSignatureVerdict.Valid or PdfSignatureVerdict.ValidWithPermittedChanges
            ? $"{c.Summary} {c.TrustDetail}".Trim()
            : c.Summary,
        PageNumber = c.PageNumber > 0 ? c.PageNumber : 1,
        Bounds = c.Rect,
    };

    public static SignatureStatus Status(PdfSignatureCheck c) => c.Verdict switch
    {
        PdfSignatureVerdict.Unsupported => SignatureStatus.Unknown,
        // A readable signature from a known signer over bytes that no longer match: the document changed.
        PdfSignatureVerdict.Invalid => c.Certificate != null && c.CoversWholeFile ? SignatureStatus.DocumentModified : SignatureStatus.Invalid,
        PdfSignatureVerdict.ModifiedAfterSigning => SignatureStatus.DocumentModified,
        _ => c.Trust == PdfSignatureTrust.Trusted ? SignatureStatus.Valid : SignatureStatus.Untrusted,
    };
}
