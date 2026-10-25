using System;
using System.Runtime.InteropServices;

namespace PdfViewer.Services;

/// <summary>
/// Tells whether the application runs from an MSIX package (the Microsoft Store build,
/// docs/microsoft-store.md).
///
/// A packaged install differs from the installer build in three ways. The Store updates it, so
/// the application does not check GitHub for new versions. The package manifest declares the
/// .pdf association, so the application does not write HKCU\Software\Classes. The package
/// folder is read-only, so optional components ship inside the package and are never
/// downloaded beside the executable.
/// </summary>
public static class PackagedApp
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    private static bool? _override;
    private static readonly Lazy<bool> Detected = new(Detect);

    /// <summary>True when this process has package identity.</summary>
    public static bool IsPackaged => _override ?? Detected.Value;

    /// <summary>Test seam: forces the answer, or restores detection with null.</summary>
    internal static void SetIsPackagedForTests(bool? value) => _override = value;

    private static bool Detect()
    {
        try
        {
            int length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage && (result == ErrorInsufficientBuffer || result == 0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Windows 7 has no package API. The application does not run there, but the
            // answer is still "not packaged".
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
