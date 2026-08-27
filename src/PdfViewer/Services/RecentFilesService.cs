using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PdfViewer.Services;

/// <summary>
/// Persists and retrieves the list of recently opened PDF files.
/// </summary>
public static class RecentFilesService
{
    private static string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfViewerNative");

    private static string SettingsFile = Path.Combine(SettingsDir, "recent_files.json");
    private const int MaxRecentFiles = 10;
    private static readonly object FileLock = new();

    /// <summary>
    /// Redirects the settings file to a test-owned directory. Test seam only.
    /// </summary>
    internal static void SetSettingsDirectoryForTests(string directory)
    {
        SettingsDir = directory;
        SettingsFile = Path.Combine(directory, "recent_files.json");
    }

    public static List<string> LoadRecentFiles()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        return list.Where(File.Exists).Take(MaxRecentFiles).ToList();
                    }
                }
            }
            catch { }

            return new List<string>();
        }
    }

    public static void AddRecentFile(string filePath)
    {
        lock (FileLock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath)) return;

                var list = LoadRecentFiles();
                list.RemoveAll(p => p.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, filePath);

                if (list.Count > MaxRecentFiles)
                {
                    list = list.Take(MaxRecentFiles).ToList();
                }

                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }

    public static void ClearRecentFiles()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    File.Delete(SettingsFile);
                }
            }
            catch { }
        }
    }
}
