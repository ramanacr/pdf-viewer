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
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfViewerNative");
    
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "recent_files.json");
    private const int MaxRecentFiles = 10;

    public static List<string> LoadRecentFiles()
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

    public static void AddRecentFile(string filePath)
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

    public static void ClearRecentFiles()
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
