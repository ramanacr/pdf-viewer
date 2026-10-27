using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfViewer.Services;

/// <summary>
/// The image formats of File → Export to Images: PNG (the default), JPEG with a quality setting,
/// TIFF and BMP, written with WPF's own encoders (no extra package). The portable engine core
/// writes PNG only; the other formats are encoded here, in the viewer.
/// </summary>
public static class PageImageFormats
{
    /// <summary>The JPEG quality (1 to 100) when the caller sets none.</summary>
    public const int DefaultJpegQuality = 95;

    /// <summary>
    /// The file extension for a format name ("PNG", "jpg", "JPEG", "tif", "TIFF", "BMP"; case and a
    /// leading dot are ignored, and an empty name is PNG). Other names throw <see cref="NotSupportedException"/>,
    /// so a file is never written as PNG under another format's name.
    /// </summary>
    public static string Extension(string? format) => (format ?? "").Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "" or "png" => "png",
        "jpg" or "jpeg" => "jpg",
        "tif" or "tiff" => "tiff",
        "bmp" => "bmp",
        _ => throw new NotSupportedException($"Image export format '{format}' is not supported. Supported formats: PNG, JPEG, TIFF, BMP."),
    };

    /// <summary>Writes <paramref name="bitmap"/> to <paramref name="output"/> in the format of <paramref name="extension"/> (from <see cref="Extension"/>).</summary>
    public static void Save(BitmapSource bitmap, string extension, Stream output, int jpegQuality = DefaultJpegQuality)
    {
        BitmapEncoder encoder = extension switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            "bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // JPEG has no alpha, and many readers ignore a BMP's alpha or mishandle it: write opaque 24-bit pixels.
        BitmapSource frame = extension is "jpg" or "bmp" && bitmap.Format != PixelFormats.Bgr24
            ? new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)
            : bitmap;
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(output);
    }
}
