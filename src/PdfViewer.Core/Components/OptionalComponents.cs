// Explicit usings: these files are also compiled into the installer, which has a different
// implicit-using set. Relying on the library's implicit usings would break that build.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace PdfViewer.Core.Components;

/// <summary>
/// A part of the product that is not shipped in the installer and is fetched only if the user
/// asks for it.
///
/// The point is size: someone who never wants their documents read out loud should not carry
/// the speech assembly. The cost of that choice is a download, and a download of code that the
/// application will later load is exactly the kind of thing this product tells users to be
/// suspicious of - so the expected hash is pinned here, in the binary, and a file that does
/// not match it is deleted rather than used.
/// </summary>
public sealed record OptionalComponent
{
    /// <summary>Name shown to the user.</summary>
    public required string DisplayName { get; init; }

    /// <summary>File written next to the application executable.</summary>
    public required string FileName { get; init; }

    /// <summary>SHA-256 of the exact expected file, uppercase hex.</summary>
    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public string SizeText => $"{SizeBytes / 1024.0:N0} KB";

    /// <summary>
    /// Where the file is published. Release assets rather than a docs site: the component has
    /// to match the version of the application that loads it, and release assets are
    /// versioned by tag for free.
    /// </summary>
    public string DownloadUrl(string version) =>
        $"https://github.com/ramanacr/pdf-viewer/releases/download/v{version}/{FileName}";
}

public static class OptionalComponents
{
    /// <summary>
    /// Text-to-speech, from Microsoft's System.Speech package.
    ///
    /// The hash is of the file as it comes out of a win-x64 publish, which is what ships as
    /// the release asset - and is a different, larger assembly than a plain build produces.
    /// The build script re-checks this before packaging, because a pin that does not match
    /// the published file would make the component impossible to install and nothing else
    /// would notice until a user tried.
    /// </summary>
    public static readonly OptionalComponent ReadAloud = new()
    {
        DisplayName = "Read Aloud (text to speech)",
        FileName = "System.Speech.dll",
        Sha256 = "0E3A87AEE550BE22AC42F3BCCAEBAA914A190CD7F8AA5CE39DF2CE35B04F9D4A",
        SizeBytes = 685360
    };

    public static IReadOnlyList<OptionalComponent> All { get; } = new[] { ReadAloud };

    /// <summary>
    /// True when the component is present and is the file we expect. A truncated or swapped
    /// file counts as not installed, so the application will not try to load it.
    /// </summary>
    public static bool IsInstalled(this OptionalComponent component, string directory)
    {
        try
        {
            string path = Path.Combine(directory, component.FileName);
            if (!File.Exists(path)) return false;

            return ComputeSha256(path).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
