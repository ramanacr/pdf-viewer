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
            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf"))
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
            using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
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
            using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\PdfViewer.exe"))
            {
                if (appKey != null)
                {
                    appKey.SetValue("FriendlyAppName", "PDF Viewer Native");
                    using var supTypes = appKey.CreateSubKey("SupportedTypes");
                    supTypes?.SetValue(".pdf", string.Empty);

                    using var appOpen = appKey.CreateSubKey(@"shell\open\command");
                    appOpen?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
                }
            }

            // Notify Windows Shell of association changes
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public static void UnregisterShellAssociation()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\PdfViewer.exe", throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
