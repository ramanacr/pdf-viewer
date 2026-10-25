using PdfEngine.Geometry;

namespace PdfEngine.Documents;

/// <summary>
/// Immutable metadata describing a loaded PDF document.
/// </summary>
public record DocumentMetadata
{
    /// <summary>The document title (Info dictionary /Title), or an empty string.</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>The author (/Author), or an empty string.</summary>
    public string Author { get; init; } = string.Empty;
    /// <summary>The subject (/Subject), or an empty string.</summary>
    public string Subject { get; init; } = string.Empty;
    /// <summary>The keywords (/Keywords) as one string, or an empty string.</summary>
    public string Keywords { get; init; } = string.Empty;
    /// <summary>The application that created the original document (/Creator), or an empty string.</summary>
    public string Creator { get; init; } = string.Empty;
    /// <summary>The application that wrote the PDF (/Producer), or an empty string.</summary>
    public string Producer { get; init; } = string.Empty;
    /// <summary>The creation date (/CreationDate), or null when the file has none or it cannot be read.</summary>
    public DateTime? CreationDate { get; init; }
    /// <summary>The last modification date (/ModDate), or null when the file has none or it cannot be read.</summary>
    public DateTime? ModificationDate { get; init; }
    /// <summary>The PDF version, for example "1.7".</summary>
    public string PdfVersion { get; init; } = "1.7";
    /// <summary>The number of pages.</summary>
    public int PageCount { get; init; }
    /// <summary>The size of the PDF data in bytes.</summary>
    public long FileSizeBytes { get; init; }
    /// <summary>The path the document was opened from, or an empty string for a document opened from memory.</summary>
    public string FilePath { get; init; } = string.Empty;
    /// <summary>The file name part of <see cref="FilePath"/>, or "Untitled.pdf" when there is no path.</summary>
    public string FileName => string.IsNullOrEmpty(FilePath) ? "Untitled.pdf" : System.IO.Path.GetFileName(FilePath);
    /// <summary>True when the file is encrypted.</summary>
    public bool IsEncrypted { get; init; }
    /// <summary>True when the document permissions allow printing. An engine that does not read the permissions leaves the default, true.</summary>
    public bool PermissionsAllowPrinting { get; init; } = true;
    /// <summary>True when the document permissions allow copying text and graphics (default true).</summary>
    public bool PermissionsAllowCopying { get; init; } = true;
    /// <summary>True when the document permissions allow changes to the document (default true).</summary>
    public bool PermissionsAllowModifying { get; init; } = true;
}

/// <summary>
/// Basic dimensional and orientation information for a specific page.
/// </summary>
public record PageInfo
{
    /// <summary>The page number, starting at 1.</summary>
    public int PageNumber { get; init; }
    /// <summary>The page width in points (1/72 inch), before the page's /Rotate is applied.</summary>
    public double WidthPoints { get; init; }
    /// <summary>The page height in points (1/72 inch), before the page's /Rotate is applied.</summary>
    public double HeightPoints { get; init; }
    /// <summary>The page's /Rotate value: 0, 90, 180 or 270 degrees clockwise.</summary>
    public int RotationDegrees { get; init; } // 0, 90, 180, 270

    /// <summary>Width divided by height (1.0 when the height is 0).</summary>
    public double AspectRatio => HeightPoints > 0 ? WidthPoints / HeightPoints : 1.0;
}

/// <summary>
/// Represents a hierarchical bookmark (outline item) in a PDF document.
/// </summary>
public class BookmarkItem
{
    public string Title { get; set; } = string.Empty;
    public int TargetPageNumber { get; set; } = 1;
    public double? TargetX { get; set; }
    public double? TargetY { get; set; }
    public double? TargetZoom { get; set; }
    public List<BookmarkItem> Children { get; set; } = new();
}

/// <summary>
/// Represents an open PDF document contract independent of any UI or native implementation.
/// </summary>
public interface IPdfDocument : IAsyncDisposable, IDisposable
{
    /// <summary>The path the document was opened from; for a document opened from memory, the name given at open time.</summary>
    string FilePath { get; }
    /// <summary>The document information and permissions read at open time.</summary>
    DocumentMetadata Metadata { get; }
    /// <summary>The number of pages.</summary>
    int PageCount { get; }
    /// <summary>False after the document is disposed.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the size and rotation of a page.</summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> is less than 1 or greater than <see cref="PageCount"/>.</exception>
    ValueTask<PageInfo> GetPageInfoAsync(int pageNumber, CancellationToken cancellationToken = default);
    /// <summary>Gets the document outline (bookmarks) as a tree. The list is empty when the document has no outline.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets a page object.</summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> is less than 1 or greater than <see cref="PageCount"/>.</exception>
    ValueTask<IPdfPage> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a single page within a PDF document.
/// </summary>
public interface IPdfPage : IAsyncDisposable, IDisposable
{
    /// <summary>The page number, starting at 1.</summary>
    int PageNumber { get; }
    /// <summary>The size and rotation of the page.</summary>
    PageInfo Info { get; }
}
