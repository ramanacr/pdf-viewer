using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Optimize;

namespace PdfViewer.Services;

/// <summary>JPEG decoding and encoding for image downsampling, through the Windows imaging codecs.</summary>
public sealed class WpfJpegCodec : IPdfJpegCodec
{
    public (byte[] Samples, int Width, int Height, int Components)? Decode(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            BitmapSource frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.Format == PixelFormats.Cmyk32) return null; // CMYK JPEGs (often Adobe-inverted) are left as they are
            bool gray = frame.Format == PixelFormats.Gray8;
            BitmapSource source = gray ? frame : new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
            int n = gray ? 1 : 3, w = source.PixelWidth, h = source.PixelHeight;
            var samples = new byte[w * h * n];
            source.CopyPixels(samples, w * n, 0);
            return (samples, w, h, n);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public byte[]? Encode(byte[] samples, int width, int height, int components, int quality)
    {
        if (components is not (1 or 3)) return null;
        var format = components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 72, 72, format, null, samples, width * components)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
