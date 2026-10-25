using PdfEngine.Geometry;

namespace PdfEngine.Signatures;

public enum SignatureStatus
{
    Unknown,
    Valid,
    Invalid,
    Untrusted,
    DocumentModified
}

public class SignatureInfo
{
    public string FieldName { get; set; } = string.Empty;
    public string SignerName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ContactInfo { get; set; } = string.Empty;
    public DateTime? SigningTime { get; set; }
    public SignatureStatus Status { get; set; } = SignatureStatus.Unknown;
    public string StatusMessage { get; set; } = string.Empty;
    public int PageNumber { get; set; } = 1;
    public PdfRect Bounds { get; set; }
}

/// <summary>Reads and verifies the digital signatures of a document.</summary>
public interface IPdfSignatureService
{
    /// <summary>Lists the signature fields and their signatures.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<IReadOnlyList<SignatureInfo>> GetSignaturesAsync(
        Documents.IPdfDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies the signature in one field.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="fieldName">The name of the signature field.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<SignatureStatus> VerifySignatureAsync(
        Documents.IPdfDocument document,
        string fieldName,
        CancellationToken cancellationToken = default);
}
