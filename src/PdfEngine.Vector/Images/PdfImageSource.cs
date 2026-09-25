using System;
using System.Threading;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Images;

/// <summary>
/// Lazily decodes an image XObject or inline image into straight BGRA (ISO 32000-2 8.9).
/// </summary>
/// <remarks>
/// Decoding happens when a backend first paints the image, not while the display list is built,
/// so display lists stay small and the backend's bounded image cache owns decoded pixels.
/// JPEG data is handed over still encoded; everything else is converted here so backends do not
/// need to understand PDF colour spaces, bit depths or /Decode arrays.
/// </remarks>
public sealed class PdfImageSource : IPdfImageSource
{
    private readonly PdfStream _stream;
    private readonly PdfColorSpace? _colorSpace;
    private readonly PdfColor _stencilColor;
    private readonly PdfObjectResolver _resolver;
    private readonly PdfStreamDecoder _decoder;
    private readonly PdfSecurityLimits _limits;
    private readonly PdfDictionary? _resources;
    private readonly object _sync = new();

    public string CacheKey { get; }

    public PdfImageSource(
        string cacheKey,
        PdfStream stream,
        PdfColorSpace? colorSpace,
        PdfColor stencilColor,
        PdfObjectResolver resolver,
        PdfStreamDecoder decoder,
        PdfSecurityLimits limits,
        PdfDictionary? resources = null)
    {
        CacheKey = cacheKey;
        _stream = stream;
        _colorSpace = colorSpace;
        _stencilColor = stencilColor;
        _resolver = resolver;
        _decoder = decoder;
        _limits = limits;
        _resources = resources;
    }

    public PdfDecodedImage Decode(CancellationToken cancellationToken = default)
    {
        // Stream decoding goes through the shared resolver/byte source; keep it serialized.
        lock (_sync)
        {
            return DecodeCore(cancellationToken);
        }
    }

    private PdfDecodedImage DecodeCore(CancellationToken ct)
    {
        var dict = _stream.Dictionary;
        int width = checked((int)(dict.GetInteger("Width") ?? 0));
        int height = checked((int)(dict.GetInteger("Height") ?? 0));
        if (width <= 0 || height <= 0 || (long)width * height > _limits.MaxImagePixels)
            throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "Image dimensions are invalid or exceed the pixel limit.");

        bool isMask = _resolver.Resolve(dict["ImageMask"]) is PdfBoolean { Value: true };
        var decoded = _decoder.DecodeImageStream(_stream);
        ct.ThrowIfCancellationRequested();

        byte[]? alpha = BuildAlpha(dict, width, height, ct);

        if (decoded.ImageFilter == "DCTDecode")
        {
            bool invert = IsInvertedDecode(dict, _colorSpace?.NumberOfComponents ?? 3);
            // Adobe CMYK JPEGs store inverted components; a /Decode [1 0 …] flips them back.
            return new PdfDecodedImage(width, height, PdfDecodedImageFormat.Jpeg, decoded.Data, alpha,
                InvertCmykJpeg: _colorSpace?.NumberOfComponents == 4 && !invert);
        }
        if (decoded.ImageFilter != null)
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.UnsupportedImageFilter, $"Image filter {decoded.ImageFilter} is not decoded by the vector core.");

        byte[] bgra = isMask
            ? DecodeStencil(dict, decoded.Data, width, height)
            : DecodeColor(dict, decoded.Data, width, height, ct);

        if (alpha != null)
        {
            for (int i = 0, p = 3; i < alpha.Length; i++, p += 4)
                bgra[p] = (byte)(bgra[p] * alpha[i] / 255);
        }

        return new PdfDecodedImage(width, height, PdfDecodedImageFormat.Bgra32, bgra);
    }

    private static bool IsInvertedDecode(PdfDictionary dict, int comps)
    {
        if (dict["Decode"] is not PdfArray d || d.Count < 2)
            return false;
        return d[0].TryGetNumber(out double a) && d[1].TryGetNumber(out double b) && a > b;
    }

    // ---------------------------------------------------------------- stencil masks (8.9.6.2)

    private byte[] DecodeStencil(PdfDictionary dict, byte[] data, int width, int height)
    {
        // Sample 0 paints (with the current fill colour) unless /Decode is [1 0].
        bool invert = IsInvertedDecode(dict, 1);
        var bits = new BitReader(data, width, 1, 1);
        var bgra = new byte[checked(width * height * 4)];
        byte r = ToByte(_stencilColor.R), g = ToByte(_stencilColor.G), b = ToByte(_stencilColor.B);
        for (int y = 0; y < height; y++)
        {
            bits.StartRow(y);
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int sample = bits.Read();
                bool paint = invert ? sample == 1 : sample == 0;
                int p = row + x * 4;
                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
                bgra[p + 3] = paint ? (byte)255 : (byte)0;
            }
        }
        return bgra;
    }

    // ---------------------------------------------------------------- colour images

    private byte[] DecodeColor(PdfDictionary dict, byte[] data, int width, int height, CancellationToken ct)
    {
        var cs = _colorSpace ?? PdfColorSpace.DeviceRgb;
        int n = Math.Max(1, cs.NumberOfComponents);
        int bpc = (int)(dict.GetInteger("BitsPerComponent") ?? 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16))
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, $"Unsupported BitsPerComponent {bpc}.");

        int maxSample = (1 << Math.Min(bpc, 16)) - 1;
        var (dMin, dMax) = ReadDecode(dict, cs, n, bpc);
        var colorKey = ReadColorKeyMask(dict, n);

        var bgra = new byte[checked(width * height * 4)];
        var bits = new BitReader(data, width, n, bpc);
        var comps = new double[n];
        var raw = new int[n];
        Span<byte> rgb = stackalloc byte[3];

        // One LUT per possible sample for single-component 8-bit-or-less images (gray, indexed,
        // separation): the colour conversion runs at most 256 times instead of once per pixel.
        byte[]? lut = null;
        if (n == 1 && bpc <= 8)
        {
            lut = new byte[(maxSample + 1) * 3];
            for (int s = 0; s <= maxSample; s++)
            {
                comps[0] = dMin[0] + s * (dMax[0] - dMin[0]) / maxSample;
                cs.ToRgb24(comps, rgb);
                lut[s * 3] = rgb[0];
                lut[s * 3 + 1] = rgb[1];
                lut[s * 3 + 2] = rgb[2];
            }
        }

        bool fastRgb8 = cs.Name == "DeviceRGB" && bpc == 8 && IsDefaultDecode(dMin, dMax);
        bool fastCmyk8 = cs.Name == "DeviceCMYK" && bpc == 8 && IsDefaultDecode(dMin, dMax);

        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            bits.StartRow(y);
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < n; c++)
                    raw[c] = bits.Read();

                int p = row + x * 4;
                byte alpha = 255;
                if (colorKey != null)
                {
                    bool masked = true;
                    for (int c = 0; c < n && masked; c++)
                        masked = raw[c] >= colorKey[c * 2] && raw[c] <= colorKey[c * 2 + 1];
                    if (masked) alpha = 0;
                }

                if (lut != null)
                {
                    int s = Math.Min(raw[0], maxSample) * 3;
                    bgra[p] = lut[s + 2];
                    bgra[p + 1] = lut[s + 1];
                    bgra[p + 2] = lut[s];
                }
                else if (fastRgb8)
                {
                    bgra[p] = (byte)raw[2];
                    bgra[p + 1] = (byte)raw[1];
                    bgra[p + 2] = (byte)raw[0];
                }
                else if (fastCmyk8)
                {
                    int k = 255 - raw[3];
                    bgra[p] = (byte)((255 - raw[2]) * k / 255);
                    bgra[p + 1] = (byte)((255 - raw[1]) * k / 255);
                    bgra[p + 2] = (byte)((255 - raw[0]) * k / 255);
                }
                else
                {
                    for (int c = 0; c < n; c++)
                        comps[c] = dMin[c] + Math.Min(raw[c], maxSample) * (dMax[c] - dMin[c]) / maxSample;
                    cs.ToRgb24(comps, rgb);
                    bgra[p] = rgb[2];
                    bgra[p + 1] = rgb[1];
                    bgra[p + 2] = rgb[0];
                }
                bgra[p + 3] = alpha;
            }
        }

        return bgra;
    }

    private static bool IsDefaultDecode(double[] dMin, double[] dMax)
    {
        for (int i = 0; i < dMin.Length; i++)
        {
            if (dMin[i] != 0 || dMax[i] != 1) return false;
        }
        return true;
    }

    /// <summary>/Decode array, defaulting to the colour space's component ranges (8.9.5.2, Table 90).</summary>
    private (double[] Min, double[] Max) ReadDecode(PdfDictionary dict, PdfColorSpace cs, int n, int bpc)
    {
        var min = new double[n];
        var max = new double[n];
        bool indexed = cs.Name == "Indexed";
        for (int i = 0; i < n; i++)
        {
            if (indexed)
            {
                min[i] = 0;
                max[i] = (1 << bpc) - 1;
            }
            else
            {
                (min[i], max[i]) = cs.GetComponentRange(i);
            }
        }

        if (_resolver.Resolve(dict["Decode"]) is PdfArray d && d.Count >= 2 * n)
        {
            for (int i = 0; i < n; i++)
            {
                if (d[2 * i].TryGetNumber(out double a) && d[2 * i + 1].TryGetNumber(out double b))
                {
                    min[i] = a;
                    max[i] = b;
                }
            }
        }
        return (min, max);
    }

    /// <summary>Colour-key masking: /Mask [min0 max0 …] in raw sample values (8.9.6.4).</summary>
    private int[]? ReadColorKeyMask(PdfDictionary dict, int n)
    {
        if (_resolver.Resolve(dict["Mask"]) is not PdfArray mask || mask.Count < 2 * n)
            return null;
        var ranges = new int[2 * n];
        for (int i = 0; i < 2 * n; i++)
        {
            if (!(_resolver.Resolve(mask[i])?.TryGetInteger(out long v) ?? false))
                return null;
            ranges[i] = (int)Math.Clamp(v, 0, 65535);
        }
        return ranges;
    }

    // ---------------------------------------------------------------- alpha: /SMask and stencil /Mask

    private byte[]? BuildAlpha(PdfDictionary dict, int width, int height, CancellationToken ct)
    {
        if (_resolver.Resolve(dict["SMask"]) is PdfStream smask)
            return DecodeMaskPlane(smask, width, height, soft: true, ct);
        if (_resolver.Resolve(dict["Mask"]) is PdfStream stencil)
            return DecodeMaskPlane(stencil, width, height, soft: false, ct);
        return null;
    }

    /// <summary>
    /// Decodes a soft mask (DeviceGray samples = alpha) or explicit stencil mask (1 = masked out)
    /// and resamples it nearest-neighbour to the base image size.
    /// </summary>
    private byte[] DecodeMaskPlane(PdfStream maskStream, int width, int height, bool soft, CancellationToken ct)
    {
        var md = maskStream.Dictionary;
        int mw = checked((int)(md.GetInteger("Width") ?? 0));
        int mh = checked((int)(md.GetInteger("Height") ?? 0));
        if (mw <= 0 || mh <= 0 || (long)mw * mh > _limits.MaxImagePixels)
            throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "Mask dimensions invalid or too large.");

        var decoded = _decoder.DecodeImageStream(maskStream);
        if (decoded.ImageFilter != null)
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.UnsupportedImageFilter, $"Mask filter {decoded.ImageFilter} unsupported.");

        int bpc = soft ? (int)(md.GetInteger("BitsPerComponent") ?? 8) : 1;
        if (bpc is not (1 or 2 or 4 or 8 or 16))
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, $"Mask BitsPerComponent {bpc} unsupported.");
        int maxSample = (1 << bpc) - 1;
        bool invert = IsInvertedDecode(md, 1);

        var plane = new byte[mw * mh];
        var bits = new BitReader(decoded.Data, mw, 1, bpc);
        for (int y = 0; y < mh; y++)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            bits.StartRow(y);
            for (int x = 0; x < mw; x++)
            {
                int s = bits.Read();
                byte a;
                if (soft)
                {
                    a = (byte)(Math.Min(s, maxSample) * 255 / maxSample);
                    if (invert) a = (byte)(255 - a);
                }
                else
                {
                    // Explicit mask: sample 1 masks out unless /Decode is [1 0] (8.9.6.3).
                    bool masked = invert ? s == 0 : s == 1;
                    a = masked ? (byte)0 : (byte)255;
                }
                plane[y * mw + x] = a;
            }
        }

        if (mw == width && mh == height)
            return plane;

        var resampled = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sy = (int)((long)y * mh / height);
            for (int x = 0; x < width; x++)
            {
                int sx = (int)((long)x * mw / width);
                resampled[y * width + x] = plane[sy * mw + sx];
            }
        }
        return resampled;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    /// <summary>
    /// Reads packed samples row by row; rows start on byte boundaries (8.9.2). Truncated data reads
    /// as zero instead of throwing, so a short stream degrades to missing pixels, not a crash.
    /// </summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _bpc;
        private readonly long _rowBytes;
        private long _bitPos;

        public BitReader(byte[] data, int width, int components, int bpc)
        {
            _data = data;
            _bpc = bpc;
            _rowBytes = ((long)width * components * bpc + 7) / 8;
        }

        public void StartRow(int y) => _bitPos = y * _rowBytes * 8;

        public int Read()
        {
            long byteIndex = _bitPos >> 3;
            int value;
            if (_bpc == 8)
            {
                value = byteIndex < _data.Length ? _data[byteIndex] : 0;
            }
            else if (_bpc == 16)
            {
                // Keep 16-bit precision out: callers scale by maxSample (65535).
                int hi = byteIndex < _data.Length ? _data[byteIndex] : 0;
                int lo = byteIndex + 1 < _data.Length ? _data[byteIndex + 1] : 0;
                value = (hi << 8) | lo;
            }
            else
            {
                int b = byteIndex < _data.Length ? _data[byteIndex] : 0;
                int shift = 8 - _bpc - (int)(_bitPos & 7);
                value = (b >> shift) & ((1 << _bpc) - 1);
            }
            _bitPos += _bpc;
            return value;
        }
    }
}
