using System.IO;
using PdfEngine.Vector;
using Vortice.WIC;

namespace PdfEngine.Vector.Direct2D;

/// <summary>JPEG (DCTDecode) data to straight BGRA through WIC, honouring CMYK inversion and colour-space mapping.</summary>
public static class WicJpeg
{
    private static readonly object Gate = new();
    private static IWICImagingFactory? _shared;

    /// <summary>Decodes with a shared factory; null when the data is not a readable JPEG.</summary>
    public static byte[]? TryDecode(PdfDecodedImage decoded, out int width, out int height)
    {
        lock (Gate)
        {
            try
            {
                _shared ??= new IWICImagingFactory();
                return Decode(_shared, decoded, out width, out height);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                width = height = 0;
                return null;
            }
        }
    }

    internal static byte[] Decode(IWICImagingFactory wic, PdfDecodedImage decoded, out int width, out int height)
    {
        using var stream = new MemoryStream(decoded.Data.ToArray(), writable: false);
        using var decoder = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        width = frame.Size.Width;
        height = frame.Size.Height;
        if (decoded.MapJpegComponents is { } map)
        {
            // Raw components for the core to map through the image's colour space.
            int n = frame.PixelFormat == Vortice.WIC.PixelFormat.Format32bppCMYK ? 4
                  : frame.PixelFormat == Vortice.WIC.PixelFormat.Format8bppGray ? 1 : 3;
            var samples = new byte[width * height * n];
            if (n == 3)
            {
                using var rgb = wic.CreateFormatConverter();
                rgb.Initialize(frame, Vortice.WIC.PixelFormat.Format24bppRGB, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
                rgb.CopyPixels((uint)(width * 3), samples);
            }
            else
            {
                frame.CopyPixels((uint)(width * n), samples);
                if (n == 4 && decoded.InvertCmykJpeg)
                    for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(255 - samples[i]);
            }
            return map(samples, n, width, height);
        }
        if (frame.PixelFormat == Vortice.WIC.PixelFormat.Format32bppCMYK)
        {
            // Normally mapped by the core above; a CMYK JPEG without a colour space still converts the same way.
            var cmyk = new byte[width * height * 4];
            frame.CopyPixels((uint)(width * 4), cmyk);
            return PdfEngine.Vector.Color.PdfColorConversion.CmykToBgra(cmyk, width * height, decoded.InvertCmykJpeg);
        }
        var result = new byte[width * height * 4];
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        converter.CopyPixels((uint)(width * 4), result);
        return result;
    }
}
