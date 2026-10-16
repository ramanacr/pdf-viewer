using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Optimize;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>How images are treated when a document is made smaller.</summary>
public sealed record ReduceSizeImagePreset(string Name, double? Resolution, int JpegQuality)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reduce File Size for the open document: a smaller copy (never over the original) with unused
/// objects removed, identical resources stored once, streams recompressed and objects packed, and,
/// when chosen, images shown above a resolution downsampled to it. An encrypted document's copy is
/// encrypted the same way, so it opens with the same passwords and keeps its permissions.
/// </summary>
public sealed partial class ReduceSizeViewModel : ObservableObject
{
    private readonly byte[] _bytes;
    private readonly string _path;
    private readonly string? _openPassword;
    private readonly string? _unavailableReason;

    /// <param name="openPassword">The password the document was opened with; kept only as long as this view model.</param>
    /// <param name="unavailableReason">Why the document cannot be reduced here (certificate encryption), or null.</param>
    public ReduceSizeViewModel(byte[] bytes, string path, bool isEncrypted, string? openPassword = null, string? unavailableReason = null)
    {
        _bytes = bytes;
        _path = path;
        _openPassword = openPassword;
        _unavailableReason = unavailableReason;
        IsEncrypted = isEncrypted;
        _selectedPreset = Presets[0];
        Summary = unavailableReason
                  ?? $"The document is {Size(bytes.Length)}." +
                     (isEncrypted ? " It is encrypted: the smaller copy keeps the same passwords and permissions." : string.Empty);
    }

    public string FileName => Path.GetFileName(_path);
    public bool IsEncrypted { get; }
    public bool CanReduce => _unavailableReason == null && !IsBusy;

    /// <summary>
    /// Set by the main window: asks for the permissions password when the document's permissions
    /// forbid changing it. Returns the password, or null when the user cancels.
    /// </summary>
    public Func<string?>? RequestOwnerPassword { get; set; }

    public IReadOnlyList<ReduceSizeImagePreset> Presets { get; } = new[]
    {
        new ReduceSizeImagePreset("Keep images as they are (no quality lost)", null, 0),
        new ReduceSizeImagePreset("High quality: images at 300 ppi", 300, 90),
        new ReduceSizeImagePreset("Standard: images at 150 ppi", 150, 80),
        new ReduceSizeImagePreset("Smallest: images at 96 ppi", 96, 70),
    };

    [ObservableProperty]
    private ReduceSizeImagePreset _selectedPreset;

    [ObservableProperty]
    private string _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReduce))]
    private bool _isBusy;

    /// <summary>The smaller copy, once saved.</summary>
    [ObservableProperty]
    private string? _savedPath;

    public string SuggestedOutputPath =>
        Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, Path.GetFileNameWithoutExtension(_path) + "_reduced.pdf");

    /// <summary>Set by the window: asks before a signed document's signatures are broken.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>
    /// Writes the smaller copy to <paramref name="outputPath"/>; false when nothing was written. For an
    /// encrypted document whose permissions forbid changes, <paramref name="ownerPassword"/> (or the
    /// one <see cref="RequestOwnerPassword"/> asks for) must be its permissions password.
    /// </summary>
    public async Task<bool> ReduceAsync(string outputPath, string? ownerPassword = null)
    {
        if (!CanReduce) return false;
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Summary = "Choose a different file: the original is not overwritten.";
            return false;
        }
        var preset = SelectedPreset;
        IsBusy = true;
        Summary = "Reducing the file size...";
        try
        {
            Task<PdfOptimizeResult> Run(string? owner) => Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _openPassword);
                return PdfOptimizer.Optimize(doc, _bytes, new PdfOptimizeOptions
                {
                    ImageResolution = preset.Resolution,
                    JpegQuality = preset.JpegQuality,
                    JpegCodec = preset.Resolution != null ? new WpfJpegCodec() : null,
                    OwnerPassword = owner,
                });
            });
            PdfOptimizeResult result;
            try
            {
                result = await Run(ownerPassword);
            }
            catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired && string.IsNullOrEmpty(ownerPassword) && RequestOwnerPassword != null)
            {
                // The permissions forbid changes: the permissions password allows them.
                if (RequestOwnerPassword() is not { Length: > 0 } asked)
                {
                    Summary = "Nothing was saved: this document's permissions do not allow changing it without its permissions password.";
                    return false;
                }
                result = await Run(asked);
            }
            if (result.SignaturesInvalidated && Confirm != null &&
                !Confirm("This document is signed. A smaller copy is a new file, so its signatures will no longer be valid in it. Save the copy anyway?"))
            {
                Summary = "Nothing was saved.";
                return false;
            }
            if (result.Bytes.Length >= _bytes.Length)
            {
                Summary = $"The document is already compact ({Size(_bytes.Length)}): a copy would not be smaller, so nothing was saved.";
                return false;
            }
            await File.WriteAllBytesAsync(outputPath, result.Bytes);
            SavedPath = outputPath;
            double percent = 100.0 * (1 - (double)result.Bytes.Length / _bytes.Length);
            var details = new List<string>();
            if (result.ImagesDownsampled > 0) details.Add($"{result.ImagesDownsampled} image(s) downsampled");
            if (result.DuplicatesMerged > 0) details.Add($"{result.DuplicatesMerged} duplicate object(s) merged");
            if (result.StreamsRecompressed > 0) details.Add($"{result.StreamsRecompressed} stream(s) recompressed");
            Summary = $"Saved {Path.GetFileName(outputPath)}: {Size(_bytes.Length)} to {Size(result.Bytes.Length)}, {percent:F0}% smaller" +
                      (details.Count > 0 ? $" ({string.Join(", ", details)})." : ".") +
                      (result.Encrypted ? " It is encrypted as the original was, with the same passwords and permissions." : string.Empty) +
                      (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException
                                       or PdfVectorException)
        {
            Summary = $"The file size could not be reduced: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Size(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:F1} MB" : bytes >= 1 << 10 ? $"{bytes / 1024.0:F0} KB" : $"{bytes} bytes";
}
