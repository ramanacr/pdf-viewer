using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PdfViewer.Services;

/// <summary>
/// Handles Windows Shell File Explorer integration: PDF file association, thumbnail handlers, preview metadata, and context menus.
/// </summary>
public static class ShellIntegrationService
{
    private const string ProgId = "PdfViewer.Document";
    private const string DefaultClassesRoot = @"Software\Classes";

    // Under HKEY_CURRENT_USER. Tests point it at a key of their own: registering and then
    // unregistering under the real one deleted the user's own .pdf association.
    private static string _classesRoot = DefaultClassesRoot;

    /// <summary>The HKCU key that holds the file classes. Test seam only.</summary>
    internal static string ClassesRoot => _classesRoot;

    /// <summary>Test seam: writes the association under another HKCU key.</summary>
    internal static void SetClassesRootForTests(string root) => _classesRoot = root;

    public static void RegisterShellAssociation(string? exePath = null)
    {
        try
        {
            exePath ??= Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            string installDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            string fileIconPath = Path.Combine(installDir, "assets", "pdf_file.ico");
            string iconRef = File.Exists(fileIconPath) ? $"{fileIconPath},0" : $"{exePath},0";

            // 1. Register .pdf Extension
            using (var extKey = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\.pdf"))
            {
                if (extKey != null)
                {
                    extKey.SetValue("Content Type", "application/pdf");
                    extKey.SetValue("PerceivedType", "document");

                    using var openWith = extKey.CreateSubKey("OpenWithProgids");
                    openWith?.SetValue(ProgId, string.Empty);

                    using var openWithList = extKey.CreateSubKey(@"OpenWithList\PdfViewer.exe");
                    openWithList?.SetValue(string.Empty, string.Empty);
                }
            }

            // 2. Register ProgId with Shell Thumbnail & Preview details
            using (var progKey = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\{ProgId}"))
            {
                if (progKey != null)
                {
                    progKey.SetValue(string.Empty, "PDF Document");
                    progKey.SetValue("FriendlyTypeName", "PDF Document");
                    progKey.SetValue("PerceivedType", "document");
                    progKey.SetValue("Treatment", 0, RegistryValueKind.DWord);
                    progKey.SetValue("ThumbnailCutoff", 0, RegistryValueKind.DWord);
                    progKey.SetValue("PreviewDetails", "prop:System.ItemNameDisplay;System.ItemTypeText;System.Size;System.DateModified;System.Author;System.Title");
                    progKey.SetValue("InfoTip", "prop:System.ItemType;System.Size;System.DateModified;System.Author;System.Title");

                    using var iconKey = progKey.CreateSubKey("DefaultIcon");
                    iconKey?.SetValue(string.Empty, iconRef);

                    // Open Verb
                    using var openCmd = progKey.CreateSubKey(@"shell\open\command");
                    openCmd?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");

                    // Print Verb
                    using var printCmd = progKey.CreateSubKey(@"shell\print\command");
                    printCmd?.SetValue(string.Empty, $"\"{exePath}\" /p \"%1\"");
                }
            }

            // 3. Register Application registration in OpenWith
            using (var appKey = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\Applications\PdfViewer.exe"))
            {
                if (appKey != null)
                {
                    appKey.SetValue("FriendlyAppName", "PDF Viewer Native");
                    using var supTypes = appKey.CreateSubKey("SupportedTypes");
                    supTypes?.SetValue(".pdf", string.Empty);

                    // A .pdf chosen through "Open with" or the Default apps page belongs to this
                    // key, not to the ProgId, so it needs the file icon too. Without it Explorer
                    // showed the application icon on every PDF.
                    using var appIcon = appKey.CreateSubKey("DefaultIcon");
                    appIcon?.SetValue(string.Empty, iconRef);

                    using var appOpen = appKey.CreateSubKey(@"shell\open\command");
                    appOpen?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
                }
            }

            // Notify Windows Shell of association changes
            if (_classesRoot == DefaultClassesRoot) SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// Removes every key <see cref="RegisterShellAssociation"/> creates.
    /// Returns false if cleanup failed, rather than swallowing the error - a failed
    /// unregister was previously indistinguishable from a successful one.
    /// </summary>
    public static bool UnregisterShellAssociation()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{_classesRoot}\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"{_classesRoot}\Applications\PdfViewer.exe", throwOnMissingSubKey: false);

            // Registration also writes these two .pdf entries; leaving them behind left a
            // dangling "Open with" entry pointing at a ProgId that no longer exists.
            using (var progIds = Registry.CurrentUser.OpenSubKey($@"{_classesRoot}\.pdf\OpenWithProgids", writable: true))
            {
                progIds?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            using (var openWith = Registry.CurrentUser.OpenSubKey($@"{_classesRoot}\.pdf\OpenWithList", writable: true))
            {
                openWith?.DeleteValue("PdfViewer.exe", throwOnMissingValue: false);
            }

            if (_classesRoot == DefaultClassesRoot) SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UnregisterShellAssociation failed: {ex}");
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
