using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Create;

namespace PdfEngine.Vector.Windows.Create;

/// <summary>How images are compressed in the new document.</summary>
public enum ImageCompression
{
    /// <summary>A JPEG is written as it is when nothing about it has to change; everything else is compressed losslessly.</summary>
    Automatic,
    /// <summary>Photographic images are compressed again as JPEG at <see cref="ImageConversionOptions.JpegQuality"/>; JPEGs that need no change still pass through.</summary>
    Jpeg,
}

/// <summary>The colour the pages keep.</summary>
public enum ImageColorMode
{
    Keep,
    Grayscale,
}

public sealed record ImageConversionOptions
{
    public ImageCompression Compression { get; init; } = ImageCompression.Automatic;
    /// <summary>1 to 100, for <see cref="ImageCompression.Jpeg"/>.</summary>
    public int JpegQuality { get; init; } = 85;
    public ImageColorMode Color { get; init; } = ImageColorMode.Keep;
}

/// <summary>
/// Reads image files for new pages with Windows Imaging Component (the decoders built into
/// Windows, plus any the user has installed, such as HEIF or WebP): JPEG, PNG, TIFF (every
/// frame), BMP, GIF, ICO, JPEG XR and the rest. A JPEG that needs no change is not decoded at all:
/// its bytes become the page's image. Other images are decoded a frame at a time, with their ICC
/// profile, resolution and EXIF orientation, into samples the PDF writer compresses losslessly
/// (or as JPEG, when asked). Frames are produced lazily, so a 500-page TIFF is never all in memory.
/// </summary>
public static class WicImageReader
{
    /// <summary>The largest decoded frame accepted (samples of one frame), well inside a managed array.</summary>
    public const long MaxFrameBytes = 1L << 30;

    public static IEnumerable<PdfCreateImage> ReadFrames(Stream stream, ImageConversionOptions? options = null)
    {
        options ??= new ImageConversionOptions();
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }

        // A JPEG first: when it can be carried as it is, no decoder is involved.
        long start = stream.Position;
        var signature = new byte[3];
        int read = stream.Read(signature);
        stream.Position = start;
        if (read == 3 && signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF && stream.Length - start <= int.MaxValue)
        {
            var bytes = new byte[stream.Length - start];
            stream.ReadExactly(bytes);
            stream.Position = start;
            if (JpegHeader.TryRead(bytes) is { CanPassThrough: true } header && (options.Color == ImageColorMode.Keep || header.Components == 1))
            {
                yield return new PdfCreateImage
                {
                    Width = header.Width, Height = header.Height, Kind = PdfImageData.Jpeg, Data = bytes,
                    Color = header.Components switch { 1 => PdfImageColor.Gray, 3 => PdfImageColor.Rgb, _ => PdfImageColor.Cmyk },
                    IccProfile = header.IccProfile, InvertedCmyk = header.Adobe && header.Components == 4,
                    DpiX = header.DpiX, DpiY = header.DpiY, Orientation = header.Orientation, Unchanged = true,
                };
                yield break;
            }
        }

        BitmapDecoder decoder;
        try
        {
            // The image's own pixels and colour profile: colour management is the PDF reader's job.
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException("Windows has no decoder for this kind of image, or the file is damaged.", ex);
        }

        int frames = decoder.Frames.Count;
        if (frames == 0) throw new InvalidDataException("The image file has no pictures in it.");
        for (int i = 0; i < frames; i++)
        {
            PdfCreateImage image;
            try
            {
                image = Convert(decoder.Frames[i], options);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException)
            {
                throw new InvalidDataException(frames > 1 ? $"Picture {i + 1} of the file could not be decoded." : "The picture could not be decoded.", ex);
            }
            yield return image;
        }
    }

    /// <summary>The frames a file holds, read from its header (no pixels are decoded).</summary>
    public static int CountFrames(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            return decoder.Frames.Count;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException("Windows has no decoder for this kind of image, or the file is damaged.", ex);
        }
    }

    private static PdfCreateImage Convert(BitmapFrame frame, ImageConversionOptions options)
    {
        int width = frame.PixelWidth, height = frame.PixelHeight;
        int orientation = Orientation(frame);
        double dpiX = frame.DpiX, dpiY = frame.DpiY;
        var format = frame.Format;
        bool gray = options.Color == ImageColorMode.Grayscale;
        byte[]? icc = gray ? null : Profile(frame);

        PdfCreateImage Samples(BitmapSource source, PdfImageColor color, int bpc, byte[]? palette = null, byte[]? alpha = null)
        {
            byte[] data = Pixels(source, out _);
            if (bpc == 16) SwapPairs(data);
            return new PdfCreateImage
            {
                Width = width, Height = height, Color = color, BitsPerComponent = bpc, Kind = PdfImageData.Samples, Data = data,
                Palette = palette, Alpha = alpha, IccProfile = palette == null ? icc : null, DpiX = dpiX, DpiY = dpiY, Orientation = orientation,
            };
        }

        PdfCreateImage Jpeg(BitmapSource source, PdfImageColor color, byte[]? alpha = null)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(options.JpegQuality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return new PdfCreateImage
            {
                Width = width, Height = height, Color = color, Kind = PdfImageData.Jpeg, Data = ms.ToArray(),
                Alpha = alpha, IccProfile = icc, DpiX = dpiX, DpiY = dpiY, Orientation = orientation,
            };
        }

        bool jpeg = options.Compression == ImageCompression.Jpeg;

        // Bilevel images stay one bit a pixel: fax scans and line art (0 is black, as in DeviceGray).
        if (format == PixelFormats.BlackWhite) return Samples(frame, PdfImageColor.Gray, 1);

        if (gray || IsGray(format))
        {
            if (!gray && format == PixelFormats.Gray16) return Samples(frame, PdfImageColor.Gray, 16);
            if (!gray && !jpeg && (format == PixelFormats.Gray2 || format == PixelFormats.Gray4))
                return Samples(frame, PdfImageColor.Gray, format.BitsPerPixel);
            var g8 = new FormatConvertedBitmap(Flatten(frame), PixelFormats.Gray8, null, 0);
            return jpeg ? Jpeg(g8, PdfImageColor.Gray) : Samples(g8, PdfImageColor.Gray, 8);
        }

        // Palette images (GIF, 8-bit PNG and BMP) keep their palette when no entry is transparent.
        if (format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8)
        {
            var colors = frame.Palette?.Colors;
            if (colors is { Count: > 0 and <= 256 } && colors.All(c => c.A == 255))
            {
                var palette = new byte[colors.Count * 3];
                for (int k = 0; k < colors.Count; k++) { palette[3 * k] = colors[k].R; palette[3 * k + 1] = colors[k].G; palette[3 * k + 2] = colors[k].B; }
                return Samples(frame, PdfImageColor.Rgb, format.BitsPerPixel, palette);
            }
        }

        if (format == PixelFormats.Cmyk32) return Samples(frame, PdfImageColor.Cmyk, 8);
        if (format == PixelFormats.Rgb48 && !jpeg) return Samples(frame, PdfImageColor.Rgb, 16);

        if (HasAlpha(format))
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            byte[] pixels = Pixels(bgra, out _);
            var rgb = new byte[(long)width * height * 3];
            var alpha = new byte[(long)width * height];
            bool opaque = true;
            for (long p = 0, q = 0, a = 0; p < pixels.LongLength; p += 4, q += 3, a++)
            {
                rgb[q] = pixels[p + 2]; rgb[q + 1] = pixels[p + 1]; rgb[q + 2] = pixels[p];
                alpha[a] = pixels[p + 3];
                opaque &= pixels[p + 3] == 255;
            }
            if (jpeg)
            {
                var bgr = new FormatConvertedBitmap(bgra, PixelFormats.Bgr24, null, 0);
                return Jpeg(bgr, PdfImageColor.Rgb, opaque ? null : alpha);
            }
            return new PdfCreateImage
            {
                Width = width, Height = height, Color = PdfImageColor.Rgb, Kind = PdfImageData.Samples, Data = rgb,
                Alpha = opaque ? null : alpha, IccProfile = icc, DpiX = dpiX, DpiY = dpiY, Orientation = orientation,
            };
        }

        var bgr24 = format == PixelFormats.Bgr24 ? (BitmapSource)frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        if (jpeg) return Jpeg(bgr24, PdfImageColor.Rgb);
        byte[] data = Pixels(bgr24, out _);
        for (long p = 0; p + 2 < data.LongLength; p += 3) (data[p], data[p + 2]) = (data[p + 2], data[p]);
        return new PdfCreateImage
        {
            Width = width, Height = height, Color = PdfImageColor.Rgb, Kind = PdfImageData.Samples, Data = data,
            IccProfile = icc, DpiX = dpiX, DpiY = dpiY, Orientation = orientation,
        };
    }

    /// <summary>Transparent pixels made white before going grey (a greyscale page has no soft mask).</summary>
    private static BitmapSource Flatten(BitmapSource source)
    {
        if (!HasAlpha(source.Format) && !(source.Format == PixelFormats.Indexed8 || source.Format == PixelFormats.Indexed4
                                          || source.Format == PixelFormats.Indexed2 || source.Format == PixelFormats.Indexed1))
            return source;
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = Pixels(bgra, out int stride);
        for (long p = 0; p < pixels.LongLength; p += 4)
        {
            int a = pixels[p + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) pixels[p + c] = (byte)((pixels[p + c] * a + 255 * (255 - a)) / 255);
            pixels[p + 3] = 255;
        }
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static bool IsGray(PixelFormat f) =>
        f == PixelFormats.Gray2 || f == PixelFormats.Gray4 || f == PixelFormats.Gray8 || f == PixelFormats.Gray16 || f == PixelFormats.Gray32Float;

    private static bool HasAlpha(PixelFormat f) =>
        f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
        || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float;

    /// <summary>The frame's samples, rows packed with no padding.</summary>
    private static byte[] Pixels(BitmapSource source, out int stride)
    {
        long rowBytes = ((long)source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        long total = rowBytes * source.PixelHeight;
        if (total > MaxFrameBytes) throw new InvalidDataException("The picture is too large to put on a page.");
        stride = (int)rowBytes;
        var data = new byte[total];
        source.CopyPixels(data, stride, 0);
        return data;
    }

    private static void SwapPairs(byte[] data)
    {
        for (long p = 0; p + 1 < data.LongLength; p += 2) (data[p], data[p + 1]) = (data[p + 1], data[p]);
    }

    /// <summary>EXIF orientation 1 to 8 (JPEG, TIFF, HEIF and others that carry it), 1 when the image has none.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort o && o is >= 1 and <= 8)
                return o;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
        }
        return 1;
    }

    /// <summary>The frame's embedded ICC profile, if it has one.</summary>
    private static byte[]? Profile(BitmapFrame frame)
    {
        try
        {
            if (frame.ColorContexts is { Count: > 0 } contexts)
            {
                using var profile = contexts[0].OpenProfileStream();
                if (profile == null) return null;
                using var ms = new MemoryStream();
                profile.CopyTo(ms);
                return ms.Length >= 132 ? ms.ToArray() : null;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }
}
