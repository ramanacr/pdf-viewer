using PdfEngine.Geometry;

namespace PdfEngine.Documents;

/// <summary>
/// Immutable metadata describing a loaded PDF document.
/// </summary>
public record DocumentMetadata
{
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string Producer { get; init; } = string.Empty;
    public DateTime? CreationDate { get; init; }
    public DateTime? ModificationDate { get; init; }
    public string PdfVersion { get; init; } = "1.7";
    public int PageCount { get; init; }
    public long FileSizeBytes { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string FileName => string.IsNullOrEmpty(FilePath) ? "Untitled.pdf" : System.IO.Path.GetFileName(FilePath);
    public bool IsEncrypted { get; init; }
    public bool PermissionsAllowPrinting { get; init; } = true;
    public bool PermissionsAllowCopying { get; init; } = true;
    public bool PermissionsAllowModifying { get; init; } = true;
}

/// <summary>
/// Basic dimensional and orientation information for a specific page.
/// </summary>
public record PageInfo
{
    public int PageNumber { get; init; }
    public double WidthPoints { get; init; }
    public double HeightPoints { get; init; }
    public int RotationDegrees { get; init; } // 0, 90, 180, 270

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
    string FilePath { get; }
    DocumentMetadata Metadata { get; }
    int PageCount { get; }
    bool IsOpen { get; }

    ValueTask<PageInfo> GetPageInfoAsync(int pageNumber, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default);
    ValueTask<IPdfPage> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a single page within a PDF document.
/// </summary>
public interface IPdfPage : IAsyncDisposable, IDisposable
{
    int PageNumber { get; }
    PageInfo Info { get; }
}
