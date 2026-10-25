namespace PdfEngine.Save;

/// <summary>How a document is written.</summary>
public enum SaveMode
{
    /// <summary>Keeps the original bytes and appends changes as incremental updates, so earlier revisions and their signatures stay intact.</summary>
    Incremental,
    /// <summary>Writes a new file with every object.</summary>
    FullRewrite,
    /// <summary>Writes a new file with annotations and form fields merged into the page content.</summary>
    Flattened,
    /// <summary>Writes a new, optimized file. An engine without an optimizer writes it as for <see cref="FullRewrite"/>.</summary>
    Optimized
}

/// <summary>Options for <see cref="IPdfSaveService.SaveAsync"/>.</summary>
public record SaveOptions
{
    /// <summary>How the document is written (default <see cref="SaveMode.Incremental"/>).</summary>
    public SaveMode Mode { get; init; } = SaveMode.Incremental;
    /// <summary>Asks the writer to leave out objects that nothing refers to. An engine can ignore this hint.</summary>
    public bool RemoveUnusedObjects { get; init; } = true;
    /// <summary>Asks the writer to compress the streams again. An engine can ignore this hint.</summary>
    public bool RecompressStreams { get; init; } = false;
    /// <summary>Asks the writer to remove the document information and XMP metadata. An engine can ignore this hint.</summary>
    public bool SanitizeMetadata { get; init; } = false;
}

/// <summary>Saves documents and exports pages as image files.</summary>
public interface IPdfSaveService
{
    /// <summary>Saves <paramref name="document"/> to <paramref name="targetPath"/>.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="targetPath">The file to write.</param>
    /// <param name="options">How to write the file; null uses the defaults.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask SaveAsync(
        Documents.IPdfDocument document,
        string targetPath,
        SaveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Renders a range of pages and writes each one as an image file.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="outputDirectory">The folder for the image files.</param>
    /// <param name="filePrefix">The start of each file name.</param>
    /// <param name="startPage">The first page, starting at 1.</param>
    /// <param name="endPage">The last page (included).</param>
    /// <param name="format">The image format, for example "png" or "jpg".</param>
    /// <param name="dpi">The resolution of the images.</param>
    /// <param name="progress">Receives the fraction done, from 0 to 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ExportPagesToImagesAsync(
        Documents.IPdfDocument document,
        string outputDirectory,
        string filePrefix,
        int startPage,
        int endPage,
        string format = "png",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
