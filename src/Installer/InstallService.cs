using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace PdfViewer.Installer;

public static class InstallService
{
    public static string DefaultInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "PdfViewer");

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "PDF Viewer.lnk");

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "PDF Viewer.lnk");

    public static void Install(
        string targetDirectory,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool associatePdfFiles,
        IProgress<int>? progress = null)
    {
        progress?.Report(10);
        Directory.CreateDirectory(targetDirectory);

        // 1. Extract embedded payload
        var assemblies = new[] { Assembly.GetExecutingAssembly(), Assembly.GetEntryAssembly(), typeof(InstallService).Assembly };
        Stream? zipStream = null;
        string foundName = string.Empty;

        foreach (var asm in assemblies)
        {
            if (asm == null) continue;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("Payload.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipStream = asm.GetManifestResourceStream(name);
                    foundName = name;
                    break;
                }
            }
            if (zipStream != null) break;
        }

        if (zipStream == null)
        {
            var allNames = string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames());
            throw new InvalidOperationException($"Embedded installer payload (Payload.zip) not found in setup package. Available resources: [{allNames}]");
        }

        progress?.Report(30);

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            int totalEntries = archive.Entries.Count;
            int current = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(Path.Combine(targetDirectory, entry.FullName));
                    continue;
                }

                string destinationPath = Path.Combine(targetDirectory, entry.FullName);
                string? destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                entry.ExtractToFile(destinationPath, overwrite: true);
                current++;
                int pct = 30 + (int)((double)current / totalEntries * 40.0);
                progress?.Report(pct);
            }
        }

        progress?.Report(75);

        string mainExePath = Path.Combine(targetDirectory, "PdfViewer.exe");
        string uninstallerPath = Path.Combine(targetDirectory, "Uninstall.exe");

        // Copy current installer as uninstaller
        try
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (File.Exists(currentExe))
            {
                File.Copy(currentExe, uninstallerPath, overwrite: true);
            }
        }
        catch { }

        // 2. Shortcuts
        if (createStartMenuShortcut)
        {
            CreateShortcut(StartMenuShortcutPath, mainExePath, "PDF Viewer Desktop Application");
        }

        if (createDesktopShortcut)
        {
            CreateShortcut(DesktopShortcutPath, mainExePath, "PDF Viewer Desktop Application");
        }

        progress?.Report(85);

        // 3. Register in Windows Uninstall Programs
        RegisterUninstaller(targetDirectory, mainExePath, uninstallerPath);

        // 4. File association
        if (associatePdfFiles)
        {
            RegisterPdfFileAssociation(mainExePath);
        }

        progress?.Report(100);
    }

    public static void Uninstall()
    {
        // 1. Remove shortcuts
        try
        {
            if (File.Exists(StartMenuShortcutPath)) File.Delete(StartMenuShortcutPath);
            if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath);
        }
        catch { }

        // 2. Remove Registry entries
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PdfViewer", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\PdfViewer.Document", throwOnMissingSubKey: false);
        }
        catch { }

        // 3. Delete files & self-cleanup
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        string installDir = Path.GetDirectoryName(currentExe) ?? DefaultInstallPath;

        // Schedule directory deletion via background cmd process
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 & rmdir /s /q \"{installDir}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi);
    }

    public static void CreateShortcut(string shortcutPath, string targetExePath, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExePath;
                shortcut.IconLocation = $"{targetExePath},0";
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath);
                shortcut.Description = description;
                shortcut.Save();
            }
        }
        catch { }
    }

    private static void RegisterUninstaller(string installDir, string mainExePath, string uninstallerPath)
    {
        try
        {
            string version = typeof(InstallService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PdfViewer");
            if (key != null)
            {
                key.SetValue("DisplayName", "PDF Viewer Native");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "PDF Viewer Native Desktop");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", mainExePath);
                key.SetValue("UninstallString", $"\"{uninstallerPath}\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    private static void RegisterPdfFileAssociation(string mainExePath)
    {
        try
        {
            string installDir = Path.GetDirectoryName(mainExePath) ?? string.Empty;
            string fileIconPath = Path.Combine(installDir, "assets", "pdf_file.ico");

            using var progKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\PdfViewer.Document");
            if (progKey != null)
            {
                progKey.SetValue("", "PDF Document");
                using var iconKey = progKey.CreateSubKey("DefaultIcon");
                if (File.Exists(fileIconPath))
                {
                    iconKey?.SetValue("", $"{fileIconPath},0");
                }
                else
                {
                    iconKey?.SetValue("", $"{mainExePath},0");
                }

                using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
                cmdKey?.SetValue("", $"\"{mainExePath}\" \"%1\"");
            }

            using var pdfKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids");
            pdfKey?.SetValue("PdfViewer.Document", string.Empty);
        }
        catch { }
    }
}
