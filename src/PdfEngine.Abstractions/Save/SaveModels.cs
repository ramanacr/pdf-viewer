namespace PdfEngine.Save;

public enum SaveMode
{
    Incremental,
    FullRewrite,
    Flattened,
    Optimized
}

public record SaveOptions
{
    public SaveMode Mode { get; init; } = SaveMode.Incremental;
    public bool RemoveUnusedObjects { get; init; } = true;
    public bool RecompressStreams { get; init; } = false;
    public bool SanitizeMetadata { get; init; } = false;
}

public interface IPdfSaveService
{
    ValueTask SaveAsync(
        Documents.IPdfDocument document,
        string targetPath,
        SaveOptions? options = null,
        CancellationToken cancellationToken = default);

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
