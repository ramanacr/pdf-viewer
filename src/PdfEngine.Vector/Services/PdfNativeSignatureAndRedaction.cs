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
public sealed class PdfNativeSignatureService : IPdfSignatureService
{
    public async ValueTask<IReadOnlyList<SignatureInfo>> GetSignaturesAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
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

    public static SignatureInfo ToInfo(PdfSignatureCheck c) => new()
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

/// <summary>
/// Redaction in the engine-neutral model, on <see cref="PdfRedactor"/>: what lies under each area
/// (glyphs, image pixels, path segments, annotations and form fields) is removed from the content,
/// not covered, and the result is written to a new file. Areas are normalized to the unrotated crop
/// box from its top-left corner, like annotations.
/// </summary>
public sealed class PdfNativeRedactionService : IPdfRedactionService
{
    private readonly ConditionalWeakTable<IPdfDocument, List<RedactionArea>> _pending = new();

    /// <summary>Decodes JPEG samples so that only the pixels under an area are removed (else a JPEG under one goes whole).</summary>
    public Func<PdfEngine.Vector.PdfDecodedImage, byte[]?>? JpegDecoder { get; init; }

    public ValueTask<IReadOnlyList<RedactionArea>> GetPendingRedactionsAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        PdfNativeEngine.Require(document);
        lock (_pending)
            return ValueTask.FromResult<IReadOnlyList<RedactionArea>>(_pending.TryGetValue(document, out var list) ? list.ToList() : Array.Empty<RedactionArea>());
    }

    public ValueTask AddPendingRedactionAsync(IPdfDocument document, RedactionArea redaction, CancellationToken cancellationToken = default)
    {
        PdfNativeEngine.Require(document);
        ArgumentNullException.ThrowIfNull(redaction);
        lock (_pending) _pending.GetOrCreateValue(document).Add(redaction);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ApplyRedactionsAsync(IPdfDocument document, string targetPath, IReadOnlyList<RedactionArea> redactions, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (!string.IsNullOrEmpty(doc.FilePath) && string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(doc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException("Target path cannot overwrite currently open document directly.", targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        var result = Apply(doc.Vector, redactions ?? Array.Empty<RedactionArea>(), JpegDecoder);
        await PdfNativeEngine.WriteAsync(targetPath, result.Bytes, cancellationToken).ConfigureAwait(false);
        lock (_pending) _pending.Remove(document);
    }

    /// <summary>The redacted document. Fill colour and overlay text come from the first area that sets them.</summary>
    public static PdfRedactionResult Apply(PdfVectorDocument document, IReadOnlyList<RedactionArea> redactions,
        Func<PdfEngine.Vector.PdfDecodedImage, byte[]?>? jpegDecoder = null)
    {
        var areas = new List<PdfRedactionArea>();
        foreach (var a in redactions)
        {
            if (a.PageNumber < 1 || a.PageNumber > document.PageCount) continue;
            var crop = document.PageTree.Pages[a.PageNumber - 1].CropBox;
            double x = crop.Left + a.Bounds.X * crop.Width;
            double top = crop.Top + (1 - a.Bounds.Y) * crop.Height;
            double h = a.Bounds.Height * crop.Height;
            areas.Add(new PdfRedactionArea(a.PageNumber, new PdfRect(x, top - h, a.Bounds.Width * crop.Width, h)));
        }
        var first = redactions.FirstOrDefault();
        return PdfRedactor.Apply(document, areas, new PdfRedactionOptions
        {
            FillColor = PdfNativeAnnotationService.ParseHex(first?.FillColor) ?? new double[] { 0, 0, 0 },
            OverlayText = redactions.Select(r => r.OverlayText).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            JpegDecoder = jpegDecoder,
        });
    }
}
