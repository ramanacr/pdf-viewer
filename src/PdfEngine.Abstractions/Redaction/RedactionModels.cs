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

public interface IPdfRedactionService
{
    ValueTask<IReadOnlyList<RedactionArea>> GetPendingRedactionsAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default);

    ValueTask AddPendingRedactionAsync(
        IPdfDocument document,
        RedactionArea redaction,
        CancellationToken cancellationToken = default);

    ValueTask ApplyRedactionsAsync(
        IPdfDocument document,
        string targetPath,
        IReadOnlyList<RedactionArea> redactions,
        CancellationToken cancellationToken = default);
}
