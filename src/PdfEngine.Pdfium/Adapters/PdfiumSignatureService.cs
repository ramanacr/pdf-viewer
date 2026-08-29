using PdfEngine.Documents;
using PdfEngine.Pdfium.Native;
using PdfEngine.Signatures;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Basic signature discovery and verification stub for PDFium documents.
/// </summary>
public sealed class PdfiumSignatureService : IPdfSignatureService
{
    public ValueTask<IReadOnlyList<SignatureInfo>> GetSignaturesAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        var signatures = new List<SignatureInfo>();
        return ValueTask.FromResult<IReadOnlyList<SignatureInfo>>(signatures);
    }

    public ValueTask<SignatureStatus> VerifySignatureAsync(
        IPdfDocument document,
        string fieldName,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(SignatureStatus.Valid);
    }
}
