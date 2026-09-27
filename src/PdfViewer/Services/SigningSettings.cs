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

    private static string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfViewerNative");

    private static string FilePath => Path.Combine(_directory, "signing.json");

    /// <summary>Test seam: where the settings file lives.</summary>
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
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering the fields is a convenience; signing does not depend on it.
        }
    }
}
