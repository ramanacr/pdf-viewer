using System.Numerics;
using System.Runtime.InteropServices;
using PdfEngine.Vector.Limits;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Images: decoded by the engine (BGRA with any masks and /Decode applied) or still-encoded JPEG
/// decoded here with Skia's codec, cached per renderer, drawn into the unit square of image space.
/// </summary>
internal sealed partial class SkiaReplayer
{
    // Decoded pixels are shared by analysis and drawing within one replayer; images are cached on the renderer.
    private readonly Dictionary<string, (byte[] Pixels, int Width, int Height)> _decoded = new(StringComparer.Ordinal);

    // Images larger than the renderer's cache budget, kept by this replayer instead.
    private readonly Dictionary<string, (SKImage Image, long Bytes)> _uncachedImages = new(StringComparer.Ordinal);

    // Pixels of the images this replayer has decoded, held to the page budget (MaxImagePixelsPerPage)
    // as the interpreter holds the sizes the image dictionaries state: a JPEG frame may be larger.
    private long _decodedPixels;

    private bool TryDecode(PdfImageRef image, out byte[] pixels, out int width, out int height, out PdfFallbackReason reason)
    {
        pixels = Array.Empty<byte>();
        width = height = 0;
        reason = PdfFallbackReason.ImageDecode;
        if (image.Source == null)
        {
            // Raw RGB24 handed over by builders and tests.
            if (!image.ImageData.IsEmpty && image.Width > 0 && image.Height > 0 && image.ImageData.Length >= (long)image.Width * image.Height * 3)
            {
                width = image.Width; height = image.Height;
                pixels = new byte[width * height * 4];
                var rgb = image.ImageData.Span;
                for (int i = 0, p = 0; i < width * height; i++, p += 4)
                {
                    pixels[p] = rgb[i * 3 + 2]; pixels[p + 1] = rgb[i * 3 + 1]; pixels[p + 2] = rgb[i * 3]; pixels[p + 3] = 255;
                }
                return true;
            }
            return false;
        }

        if (_decoded.TryGetValue(image.Source.CacheKey, out var cached))
        {
            (pixels, width, height) = cached;
            return pixels.Length > 0;
        }

        try
        {
            var decoded = image.Source.Decode(_ct);
            width = decoded.Width;
            height = decoded.Height;
            pixels = decoded.Format == PdfDecodedImageFormat.Bgra32 ? decoded.Data.ToArray()
                   : SkiaJpeg.Decode(decoded, out width, out height, _res.Limits, _decodedPixels, _ct);
            _decodedPixels += (long)width * height;
            if (decoded.Format == PdfDecodedImageFormat.Jpeg && decoded.Alpha is ReadOnlyMemory<byte> alpha)
                ApplyAlpha(pixels, width, height, alpha.Span, decoded.Width, decoded.Height);
            if (decoded.JpegMask is { } mask)
            {
                var gray = SkiaJpeg.DecodeGray(mask.Data, out int mw, out int mh, _res.Limits);
                var plane = (mask with { Width = mw, Height = mh }).ToAlpha(gray, 1, mw);
                ApplyAlpha(pixels, width, height, plane, mw, mh);
            }
            _decoded[image.Source.CacheKey] = (pixels, width, height);
            return true;
        }
        catch (Exception ex) when (ex is PdfEngine.Vector.Diagnostics.PdfVectorException or InvalidDataException or IOException
                                       or ArgumentException or OverflowException or NotSupportedException)
        {
            reason = ex is PdfEngine.Vector.Diagnostics.PdfUnsupportedFeatureException u ? u.Reason
                   : ex is PdfEngine.Vector.Diagnostics.PdfResourceLimitException ? PdfFallbackReason.ResourceLimit
                   : PdfFallbackReason.ImageDecode;
            _decoded[image.Source.CacheKey] = (Array.Empty<byte>(), 0, 0);
            return false;
        }
    }

    /// <summary>Multiplies an alpha plane (nearest-neighbour resampled to the image size) into BGRA.</summary>
    private static void ApplyAlpha(byte[] bgra, int w, int h, ReadOnlySpan<byte> alpha, int aw, int ah)
    {
        for (int y = 0; y < h; y++)
        {
            int sy = (int)((long)y * ah / h);
            for (int x = 0; x < w; x++)
            {
                int sx = (int)((long)x * aw / w);
                int ai = sy * aw + sx;
                if (ai < alpha.Length)
                {
                    int p = (y * w + x) * 4 + 3;
                    bgra[p] = (byte)(bgra[p] * alpha[ai] / 255);
                }
            }
        }
    }

    /// <summary>The image as a Skia image: from the renderer's cache, else decoded, premultiplied and cached.</summary>
    private SKImage? ImageOf(PdfImageRef image)
    {
        string key = image.Source?.CacheKey ?? image.ImageId;
        if (_res.GetImage(key) is { } shared)
            return shared;
        if (_uncachedImages.TryGetValue(key, out var own))
            return own.Image;
        if (!TryDecode(image, out var pixels, out int w, out int h, out _))
            return null;
        // Skia premultiplies straight BGRA itself when the pixels say they are unpremultiplied.
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var skImage = SKImage.FromPixelCopy(info, pixels, w * 4);
        if (skImage == null)
            return null;
        long bytes = (long)w * h * 4;
        if (!_res.AddImage(key, skImage, bytes))
            _uncachedImages[key] = (skImage, bytes);
        return skImage;
    }

    private void PaintImage(SKCanvas canvas, DrawImage di, Matrix3x2 full)
    {
        var image = di.Image;
        // The image fills the unit square of its space with the first row at the top (8.9.4).
        var imageToDevice = new Matrix3x2(1, 0, 0, -1, 0, 1) * M(di.Transform) * full;
        if (_shapeMode && image.ColorSpaceName != "ImageMask")
        {
            // An image's shape is its whole parallelogram; soft masks are opacity (11.6.5.3).
            canvas.SetMatrix(ToSk(imageToDevice));
            using var shapePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawRect(new SKRect(0, 0, 1, 1), shapePaint);
            return;
        }
        if (ImageOf(image) is not { } bitmap)
            return;

        canvas.SetMatrix(ToSk(imageToDevice));
        float devicePerSourcePixel = MathF.Sqrt(MathF.Abs(imageToDevice.GetDeterminant()) / Math.Max(1, (float)bitmap.Width * bitmap.Height));
        // Downscaling: trilinear (mipmaps) stands in for Direct2D's high-quality cubic; enlarging an
        // image that does not ask for interpolation: its samples as squares, as Direct2D draws them.
        var sampling = devicePerSourcePixel < 1f ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                     : !image.Interpolate && devicePerSourcePixel > 1.5f ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                     : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha(_shapeMode ? (byte)255 : ToByte(image.Opacity)),
        };
        canvas.DrawImage(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height), new SKRect(0, 0, 1, 1), sampling, paint);
    }
}

/// <summary>JPEG (DCTDecode) data to straight BGRA through Skia's codec, honouring CMYK inversion and colour-space mapping.</summary>
internal static class SkiaJpeg
{
    /// <summary>Decodes; null when the data is not a readable JPEG.</summary>
    public static byte[]? TryDecode(PdfDecodedImage decoded, out int width, out int height)
    {
        try
        {
            return Decode(decoded, out width, out height);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException
                                       or PdfEngine.Vector.Diagnostics.PdfVectorException)
        {
            width = height = 0;
            return null;
        }
    }

    /// <summary>
    /// Decodes a JPEG to straight BGRA. With <see cref="PdfDecodedImage.MapJpegComponents"/> set,
    /// the raw gray or RGB components go to the core, which maps them through the image's colour
    /// space.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a JPEG Skia can decode.</exception>
    /// <exception cref="NotSupportedException">A four-component JPEG with arithmetic coding or more than 8 bits per sample.</exception>
    /// <remarks>
    /// Four-component (CMYK or YCCK) JPEGs are decoded by <see cref="JpegComponentDecoder"/>: Skia's
    /// codec converts them to RGB by itself (SkiaSharp 3.116.1 drew the corpus's as solid black) and
    /// hands back no CMYK samples. They then take the Direct2D backend's path, so both backends show
    /// the same colours. WIC returns the inverse of the stored samples (it undoes the Adobe
    /// convention itself, with or without an APP14 marker), and <see cref="PdfDecodedImage.InvertCmykJpeg"/>
    /// is set for that output; the stored samples are turned into WIC's first.
    /// </remarks>
    public static byte[] Decode(PdfDecodedImage decoded, out int width, out int height) =>
        Decode(decoded, out width, out height, null, 0, CancellationToken.None);

    /// <summary>
    /// <see cref="Decode(PdfDecodedImage, out int, out int)"/> held to <paramref name="limits"/>
    /// (null: <see cref="PdfSecurityLimits.Default"/>), with <paramref name="pagePixels"/> already
    /// spent of the page's image budget.
    /// </summary>
    /// <exception cref="PdfEngine.Vector.Diagnostics.PdfResourceLimitException">The JPEG is over the pixel limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal static byte[] Decode(PdfDecodedImage decoded, out int width, out int height, PdfSecurityLimits? limits, long pagePixels,
        CancellationToken ct)
    {
        var header = PdfEngine.Vector.Create.JpegHeader.TryRead(decoded.Data.Span);
        int components = header?.Components ?? 3;
        if (components == 4)
        {
            var cmyk = JpegComponentDecoder.Decode(decoded.Data.Span, out width, out height, out int n, limits, pagePixels, ct);
            if (n != 4) throw new InvalidDataException("JPEG frame does not have four components.");
            // WIC's samples are 255 − stored, and the backend inverts them again for InvertCmykJpeg:
            // the stored samples stay as they are exactly when InvertCmykJpeg is set.
            if (!decoded.InvertCmykJpeg)
                for (int i = 0; i < cmyk.Length; i++) cmyk[i] = (byte)(255 - cmyk[i]);
            return decoded.MapJpegComponents is { } mapCmyk
                ? mapCmyk(cmyk, 4, width, height)
                : PdfEngine.Vector.Color.PdfColorConversion.CmykToBgra(cmyk, width * height, inverted: false);
        }
        if (decoded.MapJpegComponents is { } map && components is 1 or 3)
        {
            // Raw components (no colour management: no colour space on the destination) for the core.
            if (components == 1)
            {
                var gray = DecodePixels(decoded.Data, SKColorType.Gray8, out width, out height, limits, pagePixels);
                return map(gray, 1, width, height);
            }
            var bgra = DecodePixels(decoded.Data, SKColorType.Bgra8888, out width, out height, limits, pagePixels);
            var rgb = new byte[width * height * 3];
            for (int i = 0, p = 0; i < rgb.Length; i += 3, p += 4)
            {
                rgb[i] = bgra[p + 2]; rgb[i + 1] = bgra[p + 1]; rgb[i + 2] = bgra[p];
            }
            return map(rgb, 3, width, height);
        }
        return DecodePixels(decoded.Data, SKColorType.Bgra8888, out width, out height, limits, pagePixels);
    }

    /// <summary>The luminance (gray JPEG: its samples) of a JPEG, one byte per pixel.</summary>
    internal static byte[] DecodeGray(ReadOnlyMemory<byte> data, out int width, out int height, PdfSecurityLimits? limits = null) =>
        DecodePixels(data, SKColorType.Gray8, out width, out height, limits, 0);

    private static byte[] DecodePixels(ReadOnlyMemory<byte> data, SKColorType colorType, out int width, out int height,
        PdfSecurityLimits? limits, long pagePixels)
    {
        using var skData = SKData.CreateCopy(data.Span);
        using var codec = SKCodec.Create(skData) ?? throw new InvalidDataException("Not a JPEG Skia can read.");
        width = codec.Info.Width;
        height = codec.Info.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("JPEG has no size.");
        limits ??= PdfSecurityLimits.Default;
        long area = (long)width * height;
        if (area > limits.MaxImagePixels)
            throw new PdfEngine.Vector.Diagnostics.PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "JPEG exceeds the pixel limit.");
        if (pagePixels + area > limits.MaxImagePixelsPerPage)
            throw new PdfEngine.Vector.Diagnostics.PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixelsPerPage), "JPEG exceeds the page's image pixel budget.");
        var info = new SKImageInfo(width, height, colorType, colorType == SKColorType.Gray8 ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        var pixels = new byte[checked(info.BytesSize)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            // A truncated stream decodes as far as it goes (the rest stays blank), as WIC does.
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"JPEG could not be decoded ({result}).");
        }
        finally
        {
            handle.Free();
        }
        return pixels;
    }
}
