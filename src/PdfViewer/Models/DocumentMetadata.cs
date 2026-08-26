using System;

namespace PdfViewer.Models;

/// <summary>
/// Holds metadata and technical properties of an opened PDF document.
/// </summary>
public class DocumentMetadata
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string FileSizeFormatted => FormatFileSize(FileSizeBytes);
    
    public int PageCount { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string Producer { get; set; } = string.Empty;
    public DateTime? CreationDate { get; set; }
    public DateTime? ModDate { get; set; }
    public string PdfFormatVersion { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
    public bool IsLinearized { get; set; }
    
    public double DefaultPageWidthPt { get; set; }
    public double DefaultPageHeightPt { get; set; }
    public string PageDimensionsFormatted => $"{DefaultPageWidthPt:F1} x {DefaultPageHeightPt:F1} pt ({(DefaultPageWidthPt / 72.0):F2}\" x {(DefaultPageHeightPt / 72.0):F2}\")";

    public string LicenseStatus { get; set; } = string.Empty;
    public string ApplicationVersion => typeof(DocumentMetadata).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
