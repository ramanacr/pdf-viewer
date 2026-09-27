using System;
using System.IO;
using System.Text.Json;

namespace PdfViewer.Services;

/// <summary>
/// What the signing dialog remembers between signatures: the last reason, location and contact,
/// and the timestamp server. A timestamp is the only network request signing can make; it is
/// off until the user types a server address and ticks the box, and it is never sent anywhere
/// else (see <see cref="PrivacySettings"/>).
/// </summary>
public sealed class SigningSettings
{
    public string? Reason { get; set; }
    public string? Location { get; set; }
    public string? ContactInfo { get; set; }
    public string? TimestampServer { get; set; }
    public bool UseTimestamp { get; set; }
    /// <summary>How the last visible signature was laid out (a PdfSignatureLayout name).</summary>
    public string? AppearanceLayout { get; set; }
    public bool ShowDate { get; set; } = true;
    public bool ShowReason { get; set; } = true;
    public bool ShowLocation { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    /// <summary>
    /// The user asked for their signature image to be remembered on this computer (see
    /// <see cref="SignatureImageStore"/>). Off unless they tick it; unticking it deletes the image.
    /// </summary>
    public bool RememberSignatureImage { get; set; }

    private static string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfViewerNative");

    /// <summary>Where the viewer keeps its signing settings (under %LOCALAPPDATA%).</summary>
    internal static string Directory => _directory;

    private static string FilePath => Path.Combine(_directory, "signing.json");

    /// <summary>Test and startup-probe seam: where the settings file lives.</summary>
    internal static void SetDirectoryForTests(string directory) => _directory = directory;

    public static SigningSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SigningSettings>(File.ReadAllText(FilePath)) ?? new SigningSettings()
                : new SigningSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SigningSettings();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering the fields is a convenience; signing does not depend on it.
        }
    }
}
