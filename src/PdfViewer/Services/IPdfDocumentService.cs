using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// Engine-neutral service contract managing PDF document loading, rendering, text search,
/// bookmark extraction, printing, and annotations.
/// </summary>
public interface IPdfDocumentService : IDisposable
{
    /// <summary>
    /// Gets whether a document is currently loaded.
    /// </summary>
    bool IsDocumentLoaded { get; }

    /// <summary>
    /// Gets the full path of the currently loaded document.
    /// </summary>
    string CurrentFilePath { get; }

    /// <summary>
    /// Gets the total number of pages in the currently loaded document.
    /// </summary>
    int PageCount { get; }

    /// <summary>
    /// Opens a PDF document from file path, optionally with password.
    /// </summary>
    Task<DocumentMetadata> OpenDocumentAsync(string filePath, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Closes the currently active document and frees all engine resources.
    /// </summary>
    void CloseDocument();

    /// <summary>
    /// Extracts document metadata and properties.
    /// </summary>
    DocumentMetadata? GetMetadata();

    /// <summary>
    /// Gets original dimensions (points) for a given page.
    /// </summary>
    (double Width, double Height) GetPageDimensions(int pageNumber);

    /// <summary>
    /// Synchronously renders a single PDF page into a frozen WPF BitmapSource.
    /// </summary>
    BitmapSource? RenderPage(int pageNumber, int dpi = 150, int rotationAngle = 0);

    /// <summary>
    /// Asynchronously renders a single PDF page into a frozen WPF BitmapSource with cancellation support.
    /// </summary>
    Task<BitmapSource?> RenderPageAsync(int pageNumber, int dpi = 150, int rotationAngle = 0, CancellationToken ct = default);

    /// <summary>
    /// Extracts the hierarchical bookmarks / outline tree.
    /// </summary>
    ObservableCollection<BookmarkItem> ExtractBookmarks();

    /// <summary>
    /// Searches the document for text query with match coordinates and snippet text.
    /// </summary>
    Task<List<SearchMatch>> SearchTextAsync(string query, bool matchCase = false, CancellationToken ct = default);

    /// <summary>
    /// Extracts all selectable text segments and their normalized bounding boxes from a given page.
    /// </summary>
    List<PageTextSegment> ExtractPageTextSegments(int pageNumber);

    /// <summary>
    /// Asynchronously extracts all selectable text segments from a given page.
    /// </summary>
    Task<List<PageTextSegment>> ExtractPageTextSegmentsAsync(int pageNumber, CancellationToken ct = default);

    /// <summary>
    /// Exports pages to image files (PNG/JPEG) at custom DPI.
    /// </summary>
    Task ExportPagesToImagesAsync(
        string outputDirectory,
        string fileNamePrefix,
        int startPage,
        int endPage,
        string format = "PNG",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Prints the document pages using standard WPF PrintDialog.
    /// </summary>
    void PrintDocument(PrintDialog printDialog, int fromPage = 1, int toPage = -1, int rotationAngle = 0);

    /// <summary>
    /// Loads existing annotations from the current document.
    /// </summary>
    List<AnnotationModel> LoadExistingAnnotations();

    /// <summary>
    /// Saves the document with annotations applied.
    /// </summary>
    Task SaveAnnotatedDocumentAsync(
        string targetPath,
        AnnotationSaveMode mode,
        IEnumerable<AnnotationModel> annotations,
        string? originalPath = null,
        CancellationToken ct = default);
}
