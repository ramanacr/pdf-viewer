using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// Byte-bounded LRU of decoded, frozen bitmaps keyed by <see cref="IPdfImageSource.CacheKey"/>
/// (08_PERFORMANCE: decoded images are separately budgeted; device resources are evictable).
/// </summary>
internal sealed class WpfImageCache
{
    private readonly long _maxBytes;
    private readonly LinkedList<(string Key, BitmapSource Bitmap, long Bytes)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Bitmap, long Bytes)>> _map = new(StringComparer.Ordinal);
    private long _bytes;

    public WpfImageCache(long maxBytes) => _maxBytes = maxBytes;

    public long CachedBytes
    {
        get { lock (_lru) return _bytes; }
    }

    public BitmapSource GetOrDecode(IPdfImageSource source, System.Threading.CancellationToken ct, bool invert = false)
    {
        string key = invert ? source.CacheKey + "|inverted" : source.CacheKey;
        lock (_lru)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        var decoded = source.Decode(ct);
        var bitmap = ToBitmap(decoded);
        if (invert)
            bitmap = Invert(bitmap);
        long bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

        lock (_lru)
        {
            if (!_map.ContainsKey(key))
            {
                _map[key] = _lru.AddFirst((key, bitmap, bytes));
                _bytes += bytes;
                while (_bytes > _maxBytes && _lru.Count > 1)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _bytes -= last.Value.Bytes;
                }
            }
        }
        return bitmap;
    }

    /// <summary>Colour-inverted copy (alpha kept): the night-mode form of an image.</summary>
    internal static BitmapSource Invert(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        InvertBgra(pixels);
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Inverts straight-alpha BGRA colour channels in place.</summary>
    internal static void InvertBgra(Span<byte> pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(255 - pixels[i]);
            pixels[i + 1] = (byte)(255 - pixels[i + 1]);
            pixels[i + 2] = (byte)(255 - pixels[i + 2]);
        }
    }

    public void Clear()
    {
        lock (_lru)
        {
            _lru.Clear();
            _map.Clear();
            _bytes = 0;
        }
    }

    private static BitmapSource ToBitmap(PdfDecodedImage image)
    {
        BitmapSource bitmap;
        if (image.Format == PdfDecodedImageFormat.Bgra32)
        {
            bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
                image.Data.ToArray(), image.Width * 4);
        }
        else
        {
            using var stream = new MemoryStream(image.Data.ToArray(), writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];

            if (frame.Format == PixelFormats.Cmyk32)
            {
                bitmap = CmykToBgra(frame, image.InvertCmykJpeg);
            }
            else
            {
                bitmap = frame;
            }

            if (image.Alpha is ReadOnlyMemory<byte> alpha)
            {
                bitmap = ApplyAlpha(bitmap, alpha.Span, image.Width, image.Height);
            }
        }

        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CmykToBgra(BitmapSource cmyk, bool invert)
    {
        int w = cmyk.PixelWidth, h = cmyk.PixelHeight;
        var src = new byte[w * h * 4];
        cmyk.CopyPixels(src, w * 4, 0);
        var dst = new byte[w * h * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            int c = src[i], m = src[i + 1], y = src[i + 2], k = src[i + 3];
            if (invert)
            {
                c = 255 - c; m = 255 - m; y = 255 - y; k = 255 - k;
            }
            int kk = 255 - k;
            dst[i] = (byte)((255 - y) * kk / 255);
            dst[i + 1] = (byte)((255 - m) * kk / 255);
            dst[i + 2] = (byte)((255 - c) * kk / 255);
            dst[i + 3] = 255;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
    }

    private static BitmapSource ApplyAlpha(BitmapSource source, ReadOnlySpan<byte> alpha, int width, int height)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        for (int y = 0; y < h; y++)
        {
            int ay = (int)((long)y * height / h);
            for (int x = 0; x < w; x++)
            {
                int ax = (int)((long)x * width / w);
                int ai = ay * width + ax;
                if (ai < alpha.Length)
                {
                    int p = (y * w + x) * 4 + 3;
                    pixels[p] = (byte)(pixels[p] * alpha[ai] / 255);
                }
            }
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
    }
}
