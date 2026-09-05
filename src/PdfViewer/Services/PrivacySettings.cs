using System;
using System.IO;
using System.Text.Json;

namespace PdfViewer.Services;

/// <summary>
/// Controls the only thing this application ever sends over the network.
///
/// The product promise is that nothing leaves the user's machine. There is no telemetry, no
/// analytics, no account and no cloud sync anywhere in this codebase - the single outbound
/// request is an optional check against the public GitHub releases API, and this type is
/// what governs it.
///
/// It is OFF until the user answers, so a fresh install is silent. A reader that promises
/// privacy must not make its first network call before being asked.
/// </summary>
public static class PrivacySettings
{
    private static string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfViewerNative");

    private static string SettingsFile = Path.Combine(SettingsDir, "privacy.json");

    private static readonly object FileLock = new();

    /// <summary>
    /// Redirects the settings file to a test-owned directory. Test seam only.
    /// </summary>
    internal static void SetSettingsDirectoryForTests(string directory)
    {
        lock (FileLock)
        {
            SettingsDir = directory;
            SettingsFile = Path.Combine(directory, "privacy.json");
        }
    }

    /// <summary>The directory currently backing the settings file. Test seam only.</summary>
    internal static string CurrentSettingsDirectory
    {
        get { lock (FileLock) { return SettingsDir; } }
    }

    private sealed class Model
    {
        public bool? AutomaticUpdateChecksEnabled { get; set; }
    }

    /// <summary>
    /// True only when the user has explicitly allowed automatic update checks.
    /// Null means they have not been asked yet.
    /// </summary>
    public static bool? AutomaticUpdateChecksEnabled
    {
        get
        {
            lock (FileLock)
            {
                try
                {
                    if (!File.Exists(SettingsFile)) return null;
                    var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(SettingsFile));
                    return model?.AutomaticUpdateChecksEnabled;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // Unreadable settings must not be treated as consent.
                    return null;
                }
            }
        }
    }

    /// <summary>Records the user's choice. Passing null returns to the unanswered state.</summary>
    public static void SetAutomaticUpdateChecks(bool? enabled)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(
                    SettingsFile,
                    JsonSerializer.Serialize(new Model { AutomaticUpdateChecksEnabled = enabled },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Failing to persist the choice must not crash the application; the user is
                // simply asked again next time, which errs toward not sending anything.
            }
        }
    }

    /// <summary>
    /// The statement shown to the user. Kept here, beside the only network switch in the
    /// product, so the claim and the code that backs it cannot drift apart.
    /// </summary>
    public const string PrivacyStatement =
        "This application does not collect analytics or telemetry, has no account, and never " +
        "uploads your documents. Everything happens on this machine.\n\n" +
        "The only network request it can make is an update check against the public GitHub " +
        "releases page, and only if you allow it below. It sends no information about you or " +
        "your documents.";
}
