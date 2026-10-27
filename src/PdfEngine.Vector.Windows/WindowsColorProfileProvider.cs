using System;
using System.IO;
using PdfEngine.Vector.Color;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// The CMYK profiles a Windows machine has, for hosts on Windows to register with
/// <see cref="ColorProfiles.Register"/> (the desktop app does at start-up). With it the engine converts
/// colour exactly as it did when the core looked these profiles up itself.
/// </summary>
public sealed class WindowsColorProfileProvider : IColorProfileProvider
{
    /// <summary>The single instance.</summary>
    public static WindowsColorProfileProvider Instance { get; } = new();

    private WindowsColorProfileProvider() { }

    /// <summary>
    /// Adobe's U.S. Web Coated (SWOP) v2 profile, which Acrobat and other Adobe applications install and
    /// convert DeviceCMYK with by default; null when no Adobe application installed it. The SWOP profile
    /// Windows installs (RSWOP.icm) is deliberately not used for display (see <see cref="GetOutputIntentCmykProfile"/>).
    /// </summary>
    public byte[]? GetDisplayCmykProfile()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && ColorProfiles.ReadProfileFile(Path.Combine(root, "Adobe", "Color", "Profiles", "Recommended", "USWebCoatedSWOP.icc")) is { } bytes)
                return bytes;
        }
        return null;
    }

    /// <summary>The SWOP profile Windows installs (RSWOP.icm), or null when it is not available.</summary>
    public byte[]? GetOutputIntentCmykProfile() =>
        ColorProfiles.ReadProfileFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm"));
}
