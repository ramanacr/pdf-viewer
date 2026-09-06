// Explicit usings: these files are also compiled into the installer, which has a different
// implicit-using set. Relying on the library's implicit usings would break that build.
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PdfViewer.Core.Components;

/// <summary>
/// Raised when an optional component could not be installed. Nothing is left on disk when
/// this is thrown.
/// </summary>
public class ComponentInstallException : Exception
{
    public ComponentInstallException(string message) : base(message) { }
    public ComponentInstallException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Fetches an optional component and puts it beside the application.
///
/// The download is over HTTPS from the project's own release assets, and the file is checked
/// against the hash pinned in this binary before it is allowed anywhere near the install
/// directory. A mismatch is treated as an attack, not as a retry: the file is deleted and the
/// caller is told plainly.
/// </summary>
public sealed class ComponentDownloader
{
    private readonly HttpClient _http;

    /// <summary>A component this size should never take long; a hung connection should not hang the caller.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Refuses a response far larger than the component, before reading it all into memory.</summary>
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    public ComponentDownloader(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = DownloadTimeout };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PdfViewerNative-ComponentInstaller");
        }
    }

    /// <summary>
    /// Downloads the component and installs it into <paramref name="targetDirectory"/>.
    /// Returns the path it was written to.
    /// </summary>
    public async Task<string> InstallAsync(
        OptionalComponent component,
        string version,
        string targetDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be empty.", nameof(targetDirectory));

        string destination = Path.Combine(targetDirectory, component.FileName);

        // Already there and already correct: nothing to do, and nothing to download.
        if (component.IsInstalled(targetDirectory)) return destination;

        string url = component.DownloadUrl(version);
        string staging = destination + ".download";

        try
        {
            Directory.CreateDirectory(targetDirectory);
            progress?.Report(5);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ComponentInstallException(
                    $"{component.DisplayName} could not be downloaded ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes)
            {
                throw new ComponentInstallException(
                    $"{component.DisplayName} was offered as {declared} bytes, which is far larger than expected. It was not downloaded.");
            }

            await WriteToStagingAsync(response, staging, declared, progress, cancellationToken);

            // The gate. Anything that is not byte-for-byte the expected component is deleted.
            string actual = OptionalComponents.ComputeSha256(staging);
            if (!actual.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ComponentInstallException(
                    $"The downloaded {component.DisplayName} did not match its expected checksum, so it was discarded. " +
                    "Nothing was installed.");
            }

            File.Move(staging, destination, overwrite: true);
            progress?.Report(100);
            return destination;
        }
        catch (ComponentInstallException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ComponentInstallException(
                $"{component.DisplayName} could not be installed: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static async Task WriteToStagingAsync(
        HttpResponseMessage response,
        string staging,
        long? declaredLength,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            written += read;

            // A server that ignores Content-Length must not be able to fill the disk.
            if (written > MaxDownloadBytes)
            {
                throw new ComponentInstallException(
                    "The download was larger than expected and was stopped.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (declaredLength is > 0)
            {
                progress?.Report((int)Math.Min(95, 5 + (written * 90 / declaredLength.Value)));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging file is untidy, not dangerous - it is never loaded.
        }
    }
}
