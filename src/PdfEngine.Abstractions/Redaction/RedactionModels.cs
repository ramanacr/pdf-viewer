using PdfEngine.Documents;
using PdfEngine.Geometry;

namespace PdfEngine.Redaction;

public record RedactionArea
{
    public int PageNumber { get; init; }
    public PdfRect Bounds { get; init; }
    public string? OverlayText { get; init; }
    public string FillColor { get; init; } = "#000000"; // Default blackout
}

/// <summary>Keeps a list of pending redactions and applies them, which removes the content under them.</summary>
public interface IPdfRedactionService
{
    /// <summary>Gets the redactions marked on the document but not applied yet.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<IReadOnlyList<RedactionArea>> GetPendingRedactionsAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an area for redaction. Nothing is removed until <see cref="ApplyRedactionsAsync"/>.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="redaction">The area to redact.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask AddPendingRedactionAsync(
        IPdfDocument document,
        RedactionArea redaction,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a copy of the document with the content under <paramref name="redactions"/> removed.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="targetPath">The file to write.</param>
    /// <param name="redactions">The areas to redact.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ApplyRedactionsAsync(
        IPdfDocument document,
        string targetPath,
        IReadOnlyList<RedactionArea> redactions,
        CancellationToken cancellationToken = default);
}
