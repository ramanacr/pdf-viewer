using System;
using System.IO;
using System.Reflection;

namespace PdfViewer.Services;

/// <summary>
/// Manages initialization and verification of the Aspose.Pdf license.
/// </summary>
public static class LicenseService
{
    public static bool IsLicensed { get; private set; }
    public static string LicenseStatusMessage { get; private set; } = "Not initialized";
    public static string LicenseFilePath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        try
        {
            var license = new Aspose.Pdf.License();

            // 1. Try finding Aspose.Total.lic directly in base / working directory or parent directories
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchPaths = 
            {
                Path.Combine(baseDir, "Aspose.Total.lic"),
                Path.Combine(baseDir, "..", "..", "..", "..", "Aspose.Total.lic"),
                Path.Combine(Directory.GetCurrentDirectory(), "Aspose.Total.lic"),
                "Aspose.Total.lic"
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    license.SetLicense(fullPath);
                    IsLicensed = true;
                    LicenseFilePath = fullPath;
                    LicenseStatusMessage = $"Aspose.Total license active (Loaded from {Path.GetFileName(fullPath)})";
                    return;
                }
            }

            // 2. Try loading from Embedded Resource in assembly
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            foreach (var resName in resourceNames)
            {
                if (resName.EndsWith("Aspose.Total.lic", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = assembly.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        license.SetLicense(stream);
                        IsLicensed = true;
                        LicenseFilePath = $"[Embedded: {resName}]";
                        LicenseStatusMessage = "Aspose.Total license active (Embedded Resource)";
                        return;
                    }
                }
            }

            // 3. Fallback attempt with filename directly
            try
            {
                license.SetLicense("Aspose.Total.lic");
                IsLicensed = true;
                LicenseStatusMessage = "Aspose.Total license active";
            }
            catch (Exception ex)
            {
                IsLicensed = false;
                LicenseStatusMessage = $"Evaluation Mode ({ex.Message})";
            }
        }
        catch (Exception ex)
        {
            IsLicensed = false;
            LicenseStatusMessage = $"License Error: {ex.Message}";
        }
    }
}
