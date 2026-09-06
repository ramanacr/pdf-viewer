using PdfEngine.Documents;
using PdfEngine.Rendering;

namespace PdfEngine.Pages;

public interface IPdfPageOrganizerService
{
    ValueTask RotatePageAsync(
        IPdfDocument document,
        int pageNumber,
        PageRotation newRotation,
        CancellationToken cancellationToken = default);

    ValueTask DeletePageAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    ValueTask InsertBlankPageAsync(
        IPdfDocument document,
        int targetIndex,
        double widthPoints = 612,
        double heightPoints = 792,
        CancellationToken cancellationToken = default);

    ValueTask ExtractPagesAsync(
        IPdfDocument document,
        IReadOnlyList<int> pageNumbers,
        string targetPath,
        CancellationToken cancellationToken = default);

    ValueTask MergeDocumentsAsync(
        IReadOnlyList<string> sourceFiles,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask SplitDocumentAsync(
        IPdfDocument document,
        IReadOnlyList<int> pageNumbersPerSplit,
        string outputDirectory,
        string filePrefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a new document whose pages are exactly <paramref name="arrangement"/>, in order.
    ///
    /// Reordering, deleting and rotating pages are all the same operation expressed this way:
    /// a page moves by changing position in the list, is deleted by not appearing, and is
    /// rotated by its entry saying so. Doing it in one pass means the result is always a
    /// consistent document rather than the outcome of a sequence of edits that could half-fail.
    /// </summary>
    ValueTask ArrangePagesAsync(
        IPdfDocument document,
        IReadOnlyList<PageArrangementEntry> arrangement,
        string targetPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One page of the document being built: which source page it comes from, and how it should
/// sit once it gets there.
/// </summary>
public readonly record struct PageArrangementEntry
{
    public PageArrangementEntry(int sourcePageNumber, PageRotation rotation = PageRotation.Rotate0)
    {
        SourcePageNumber = sourcePageNumber;
        Rotation = rotation;
    }

    /// <summary>1-based page number in the source document.</summary>
    public int SourcePageNumber { get; init; }

    /// <summary>
    /// Rotation to apply on top of whatever the source page already carries. Rotate0 keeps
    /// the page as it is.
    /// </summary>
    public PageRotation Rotation { get; init; }
}
