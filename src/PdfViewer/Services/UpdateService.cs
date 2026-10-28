using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// Service that interacts with the public GitHub API to check for new releases,
/// download updated installer payloads, and trigger self-contained upgrades.
/// </summary>
public static class UpdateService
{
    public const string GitHubRepoUrl = "https://github.com/ramanacr/pdf-viewer";
    public const string GitHubReleasesUrl = "https://github.com/ramanacr/pdf-viewer/releases";
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/ramanacr/pdf-viewer/releases/latest";

    /// <summary>The Microsoft Store listing. The Store installs and updates a packaged copy.</summary>
    public const string MicrosoftStoreListingUrl = "https://apps.microsoft.com/detail/9N6QDM774R62";

    /// <summary>What a packaged (Store) install says in place of the GitHub update check.</summary>
    public const string StoreUpdatesMessage =
        "This copy is from the Microsoft Store. The Store installs updates, so the application does not check GitHub for them.";

    /// <summary>What the application does about updates when it starts.</summary>
    public enum StartupUpdateAction
    {
        /// <summary>No question and no network request.</summary>
        None,
        /// <summary>Ask the user first (the privacy dialog). Nothing is sent before the answer.</summary>
        AskConsent,
        /// <summary>The user allowed it: check GitHub in the background.</summary>
        Check,
    }

    /// <summary>
    /// The startup decision. A packaged (Store) install never asks and never checks, because
    /// the Store updates it. An unpackaged install checks only after the user allows it.
    /// </summary>
    public static StartupUpdateAction DecideStartupAction(bool isPackaged, bool? userAllowsChecks)
    {
        if (isPackaged) return StartupUpdateAction.None;
        return userAllowsChecks switch
        {
            null => StartupUpdateAction.AskConsent,
            true => StartupUpdateAction.Check,
            false => StartupUpdateAction.None,
        };
    }

    /// <summary>True when this install may ask GitHub for a new version (not a Store install).</summary>
    public static bool IsGitHubUpdateCheckAvailable(bool isPackaged) => !isPackaged;

    private static readonly HttpClient HttpClient;

    // The installer is about 10 MB: the 15-second limit of the release check would fail it on a
    // slow line, so the download has a client of its own with a limit of minutes.
    private static readonly HttpClient DownloadClient;

    /// <summary>The longest the installer download may take.</summary>
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    static UpdateService()
    {
        // Both connect through ResilientHttp: one unreachable GitHub address no longer stalls them.
        HttpClient = new HttpClient(PdfViewer.Core.Components.ResilientHttp.CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PdfViewer-App", "1.0"));
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        DownloadClient = new HttpClient(PdfViewer.Core.Components.ResilientHttp.CreateHandler())
        {
            Timeout = DownloadTimeout
        };
        DownloadClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PdfViewer-App", "1.0"));
    }

    /// <summary>
    /// Parses a semantic version string from a release tag (e.g. "v1.0.12" -> Version(1, 0, 12)).
    /// </summary>
    public static Version ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return new Version(0, 0, 0);

        string clean = tag.Trim().TrimStart('v', 'V').Split('-')[0].Split('+')[0];
        var parts = clean.Split('.');

        if (parts.Length == 1 && int.TryParse(parts[0], out int majorOnly))
            return new Version(majorOnly, 0, 0);

        if (parts.Length == 2 && int.TryParse(parts[0], out int maj) && int.TryParse(parts[1], out int min))
            return new Version(maj, min, 0);

        if (Version.TryParse(clean, out var parsed))
            return parsed;

        return new Version(0, 0, 0);
    }

    /// <summary>
    /// Checks the public GitHub Releases API for the latest release metadata.
    /// </summary>
    public static async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        // The Store updates a packaged install: GitHub is never asked from one.
        if (!IsGitHubUpdateCheckAvailable(PackagedApp.IsPackaged))
            throw new InvalidOperationException(StoreUpdatesMessage);

        var asm = typeof(UpdateService).Assembly;
        var currentVersion = asm.GetName().Version ?? new Version(1, 0, 0);

        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestReleaseUrl);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API release check failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        return ParseReleaseJson(json, currentVersion);
    }

    /// <summary>
    /// Parses GitHub release JSON and compares against the current running version.
    /// </summary>
    public static UpdateInfo ParseReleaseJson(string json, Version currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? string.Empty : string.Empty;
        var latestVersion = ParseVersion(tagName);

        string releaseTitle = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? tagName : tagName;
        string releaseNotes = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? string.Empty : string.Empty;
        string releaseUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? GitHubReleasesUrl : GitHubReleasesUrl;

        DateTime? publishedAt = null;
        if (root.TryGetProperty("published_at", out var pubElem) && pubElem.TryGetDateTime(out var dt))
        {
            publishedAt = dt;
        }

        string? installerDownloadUrl = null;
        long installerSize = 0;

        if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElem.EnumerateArray())
            {
                string assetName = asset.TryGetProperty("name", out var aName) ? aName.GetString() ?? string.Empty : string.Empty;
                if (assetName.Equals("PdfViewerSetup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerDownloadUrl = asset.TryGetProperty("browser_download_url", out var bUrl) ? bUrl.GetString() : null;
                    installerSize = asset.TryGetProperty("size", out var sElem) ? sElem.GetInt64() : 0;
                    break;
                }
            }
        }

        // Compare versions (ignore revision/build if unspecified)
        bool isNewer = CompareVersions(latestVersion, currentVersion) > 0;

        return new UpdateInfo
        {
            IsUpdateAvailable = isNewer,
            CurrentVersion = FormatVersion(currentVersion),
            LatestVersion = FormatVersion(latestVersion),
            ReleaseTitle = string.IsNullOrWhiteSpace(releaseTitle) ? tagName : releaseTitle,
            ReleaseNotes = releaseNotes,
            ReleaseUrl = releaseUrl,
            PublishedAt = publishedAt,
            InstallerDownloadUrl = installerDownloadUrl,
            InstallerSize = installerSize
        };
    }

    /// <summary>
    /// Compares two versions focusing on Major, Minor, and Build components.
    /// </summary>
    public static int CompareVersions(Version v1, Version v2)
    {
        int v1Major = Math.Max(0, v1.Major);
        int v2Major = Math.Max(0, v2.Major);
        if (v1Major != v2Major) return v1Major.CompareTo(v2Major);

        int v1Minor = Math.Max(0, v1.Minor);
        int v2Minor = Math.Max(0, v2.Minor);
        if (v1Minor != v2Minor) return v1Minor.CompareTo(v2Minor);

        int v1Build = Math.Max(0, v1.Build);
        int v2Build = Math.Max(0, v2.Build);
        return v1Build.CompareTo(v2Build);
    }

    private static string FormatVersion(Version v)
    {
        return v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}.0";
    }

    /// <summary>
    /// Downloads the latest installer executable with real-time stream progress reporting.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<(long BytesRead, long TotalBytes, double Percent)>? progress = null,
        CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PdfViewerUpdate");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, "PdfViewerSetup.exe");

        using var response = await DownloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            double pct = totalBytes > 0 ? (double)totalRead / totalBytes * 100.0 : 0;
            progress?.Report((totalRead, totalBytes, pct));
        }

        return tempFile;
    }

    /// <summary>
    /// Launches the downloaded installer executable and terminates the running instance.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath, bool silent = true)
    {
        string args = silent ? "-silent -launch" : "";
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = args,
            UseShellExecute = true
        };

        Process.Start(psi);

        Application.Current?.Dispatcher?.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
    }

    /// <summary>
    /// Opens the specified URL in the default system web browser.
    /// </summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
