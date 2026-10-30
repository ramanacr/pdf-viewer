using System;
using System.IO;
using System.Reflection;

namespace PdfViewer;

/// <summary>
/// The sample document (File → Open Sample Demo Document). The app carries samples/SampleDocument.pdf
/// inside its assembly, so the installer, the portable app and the Store package all open the same
/// branded document even when no file was copied next to the app. The document itself is written by
/// <c>vectorpdf sampledoc</c> (eng/vectorpdf/tools/VectorPdf.Tool/SampleDocument.cs); see docs/building.md.
/// </summary>
public static class SamplePdfGenerator
{
    /// <summary>The manifest name of the embedded copy (see PdfViewer.csproj).</summary>
    internal const string ResourceName = "PdfViewer.SampleDocument.pdf";

    /// <summary>Where the sample is written for the app to open when no copy lies next to it.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfViewerNative", "SampleDocument.pdf");

    /// <summary>Writes the sample document to <paramref name="outputPath"/> (replacing what is there) and returns the path.</summary>
    public static string GenerateSamplePdf(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var sample = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The sample document is missing from the app.");
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        sample.CopyTo(output);
        return outputPath;
    }
}
