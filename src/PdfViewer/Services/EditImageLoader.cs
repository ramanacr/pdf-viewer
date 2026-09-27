using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Editing;

namespace PdfViewer.Services;

/// <summary>
/// Reads a picture file for placing on a page. A plain JPEG (gray or RGB, not turned by its EXIF
/// orientation) is embedded exactly as it is, with no loss; anything else (PNG, TIFF, BMP, GIF,
/// CMYK or turned JPEGs) is decoded, turned upright, and embedded losslessly with its transparency.
/// </summary>
public static class EditImageLoader
{
    /// <summary>Pictures larger than this many pixels are refused rather than filling memory.</summary>
    public const long MaxPixels = 100_000_000;

    public static PdfImageContent Load(string path) => Load(File.ReadAllBytes(path));

    /// <summary>The same, from the file's bytes.</summary>
    public static PdfImageContent Load(byte[] file)
    {
        using var ms = new MemoryStream(file, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new NotSupportedException($"The picture is {frame.PixelWidth} x {frame.PixelHeight} pixels, more than can be placed.");

        int orientation = Orientation(frame);
        if (decoder is JpegBitmapDecoder && orientation == 1 && (frame.Format == PixelFormats.Gray8 || frame.Format == PixelFormats.Bgr24 || frame.Format == PixelFormats.Rgb24 || frame.Format == PixelFormats.Bgr32))
            return new PdfImageContent(frame.PixelWidth, frame.PixelHeight, file, PdfImageEncoding.Jpeg, Components: frame.Format == PixelFormats.Gray8 ? 1 : 3);

        frame = Upright(frame, orientation);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool opaque = true;
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = pixels[i * 4 + 2];
            rgb[i * 3 + 1] = pixels[i * 4 + 1];
            rgb[i * 3 + 2] = pixels[i * 4];
            alpha[i] = pixels[i * 4 + 3];
            opaque &= alpha[i] == 255;
        }
        return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, opaque ? null : alpha);
    }

    /// <summary>EXIF orientation (1 = upright), 1 when absent.</summary>
    private static int Orientation(BitmapSource frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
            {
                foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    if (meta.ContainsQuery(query) && meta.GetQuery(query) is ushort o and >= 1 and <= 8) return o;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        return 1;
    }

    private static BitmapSource Upright(BitmapSource frame, int orientation)
    {
        if (orientation == 1) return frame;
        var group = new TransformGroup();
        bool flip = orientation is 2 or 4 or 5 or 7;
        if (flip) group.Children.Add(new ScaleTransform(-1, 1));
        // Mirrored first, then turned clockwise (TIFF/EXIF orientation 1..8).
        double angle = orientation switch { 3 or 4 => 180, 6 or 7 => 90, 5 or 8 => 270, _ => 0 };
        if (angle != 0) group.Children.Add(new RotateTransform(angle));
        return new TransformedBitmap(frame, group);
    }
}
