using PdfEngine.Documents;
using PdfEngine.Rendering;

namespace PdfEngine.Pages;

/// <summary>Rotates, deletes, inserts, extracts, merges, splits and arranges pages.</summary>
public interface IPdfPageOrganizerService
{
    /// <summary>Sets the rotation of a page in the open document.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="newRotation">The new rotation of the page. It replaces the current rotation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask RotatePageAsync(
        IPdfDocument document,
        int pageNumber,
        PageRotation newRotation,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a page from the open document.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask DeletePageAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts an empty page into the open document.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="targetIndex">The position of the new page, starting at 0 (0 inserts it before the first page).</param>
    /// <param name="widthPoints">The page width in points (default 612, US Letter).</param>
    /// <param name="heightPoints">The page height in points (default 792, US Letter).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask InsertBlankPageAsync(
        IPdfDocument document,
        int targetIndex,
        double widthPoints = 612,
        double heightPoints = 792,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a new document that contains the given pages.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="pageNumbers">The pages to copy, starting at 1, in the order of the new document.</param>
    /// <param name="targetPath">The file to write.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ExtractPagesAsync(
        IPdfDocument document,
        IReadOnlyList<int> pageNumbers,
        string targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a new document that contains all pages of the source files, in order.</summary>
    /// <param name="sourceFiles">The PDF files to merge.</param>
    /// <param name="targetPath">The file to write.</param>
    /// <param name="progress">Receives the fraction done, from 0 to 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask MergeDocumentsAsync(
        IReadOnlyList<string> sourceFiles,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Splits the document into several files.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="pageNumbersPerSplit">The number of pages of each part, in order.</param>
    /// <param name="outputDirectory">The folder for the new files.</param>
    /// <param name="filePrefix">The start of each file name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
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
