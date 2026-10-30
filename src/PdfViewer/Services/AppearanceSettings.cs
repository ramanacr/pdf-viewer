using System;
using System.IO;
using System.Text.Json;

namespace PdfViewer.Services;

/// <summary>
/// Remembers the light or dark theme the user picked, in appearance.json beside the viewer's other
/// settings under %LOCALAPPDATA%\PdfViewerNative. Kept on this computer only.
/// </summary>
public static class AppearanceSettings
{
    private static string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfViewerNative");

    private static readonly object FileLock = new();

    private static string SettingsFile => Path.Combine(SettingsDir, "appearance.json");

    /// <summary>Redirects the settings file to a directory the caller owns: a test, or a --startup-probe run.</summary>
    internal static void SetSettingsDirectoryForTests(string directory)
    {
        lock (FileLock) { SettingsDir = directory; }
    }

    /// <summary>The directory currently backing the settings file. Test seam only.</summary>
    internal static string CurrentSettingsDirectory
    {
        get { lock (FileLock) { return SettingsDir; } }
    }

    private sealed class Model
    {
        public string? Theme { get; set; }
    }

    /// <summary>The saved theme, or null when the user has never chosen one or the file cannot be read.</summary>
    public static AppTheme? Theme
    {
        get { lock (FileLock) { return ReadFrom(SettingsFile); } }
    }

    public static void SaveTheme(AppTheme theme)
    {
        lock (FileLock) { WriteTo(SettingsFile, theme); }
    }

    internal static AppTheme? ReadFrom(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(file));
            return Enum.TryParse<AppTheme>(model?.Theme, ignoreCase: true, out var theme)
                && Enum.IsDefined(theme) ? theme : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void WriteTo(string file, AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(
                new Model { Theme = theme.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The theme is a convenience; failing to remember it must not crash the viewer.
        }
    }
}