using System;

namespace PdfViewer.Models;

/// <summary>
/// Holds release metadata retrieved from GitHub Releases API.
/// </summary>
public class UpdateInfo
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseTitle { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public DateTime? PublishedAt { get; set; }
    public string? InstallerDownloadUrl { get; set; }
    public long InstallerSize { get; set; }

    public string FormattedInstallerSize
    {
        get
        {
            if (InstallerSize <= 0) return string.Empty;
            double mb = InstallerSize / (1024.0 * 1024.0);
            return $"{mb:F1} MB";
        }
    }
}
