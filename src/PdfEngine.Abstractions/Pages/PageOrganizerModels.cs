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
}
