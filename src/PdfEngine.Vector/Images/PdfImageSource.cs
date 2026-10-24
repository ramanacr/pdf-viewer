using System;
using System.IO;
using System.Threading;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Images.Jbig2;
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
        int components = isMask ? 1 : Math.Max(1, _colorSpace?.NumberOfComponents ?? 3);
        int bits = isMask ? 1 : (int)Math.Clamp(dict.GetInteger("BitsPerComponent") ?? 8, 1, 16);
        long expected = ((long)width * components * bits + 7) / 8 * height;
        var decoded = _decoder.DecodeImageStream(_stream, expected);
        ct.ThrowIfCancellationRequested();

        // The soft mask stream is decoded once, for both the alpha plane and a JPEG mask.
        var smask = _resolver.Resolve(dict["SMask"]) as PdfStream;
        var smaskData = smask != null ? _decoder.DecodeImageStream(smask) : null;
        byte[]? alpha = BuildAlpha(dict, width, height, smask, smaskData, ct);
        var jpegMask = JpegSoftMask(smask, smaskData);

        if (decoded.ImageFilter == "DCTDecode")
        {
            bool invert = IsInvertedDecode(dict, _colorSpace?.NumberOfComponents ?? 3);
            bool cmyk = _colorSpace?.NumberOfComponents == 4;
            // A JPEG's samples are colour-space components like any image's: a /Separation or
            // /DeviceN JPEG holds tints (1 = full ink), and shown as gray it is a negative. Gray and
            // RGB device spaces keep the backend's fast path; everything else, CMYK and colour-managed
            // ICC spaces included, is mapped by the core so every backend converts colour the same way.
            bool mapped = _colorSpace is { } space && (space.Name is not ("DeviceGray" or "DeviceRGB" or "ICCBased")
                                                      || cmyk
                                                      || space is PdfColorSpace.IccBasedColorSpace { UsesProfile: true }
                                                      || invert);
            // The JPEG's own colours are a fair rendition when only colour management is missing from
            // them (device and ICC spaces): exporters without the PDF's colour mapping may embed it as is.
            bool selfContained = _colorSpace is not { } own
                                 || (own.Name is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "ICCBased" && !(invert && !cmyk));
            Func<byte[], int, int, int, byte[]>? map = mapped
                ? (samples, components, w, h) =>
                {
                    // A JPEG whose components do not match its colour space (an /Indexed space over
                    // an RGB JPEG) is shown in its own colours, as PDFium shows it.
                    if (components != Math.Max(1, _colorSpace!.NumberOfComponents))
                        return PdfColorConversion.DeviceSamplesToBgra(samples, components, w * h);
                    return DecodeColor(dict, samples, w, h, CancellationToken.None, bitsPerComponent: 8);
                }
                : null;
            // Adobe CMYK JPEGs store inverted components; a /Decode [1 0 …] flips them back.
            return new PdfDecodedImage(width, height, PdfDecodedImageFormat.Jpeg, ValidJpeg(decoded.Data), alpha,
                InvertCmykJpeg: cmyk && !invert, MapJpegComponents: map) { JpegMask = jpegMask, JpegIsSelfContained = selfContained };
        }
        if (decoded.ImageFilter == "JPXDecode" && !isMask)
            return DecodeJpx(dict, decoded.Data, alpha, ct) with { JpegMask = jpegMask };
        byte[] samples = DecodeSamples(decoded, width, height, ct);

        byte[] bgra = isMask
            ? DecodeStencil(dict, samples, width, height)
            : DecodeColor(dict, samples, width, height, ct);

        if (alpha != null)
        {
            for (int i = 0, p = 3; i < alpha.Length; i++, p += 4)
                bgra[p] = (byte)(bgra[p] * alpha[i] / 255);
        }

        return new PdfDecodedImage(width, height, PdfDecodedImageFormat.Bgra32, bgra) { JpegMask = jpegMask };
    }

    /// <summary>A soft mask compressed as JPEG, which the backend decodes (the core has no JPEG codec); null otherwise.</summary>
    private PdfJpegMask? JpegSoftMask(PdfStream? smask, PdfDecodedStream? decoded)
    {
        if (smask == null || decoded == null) return null;
        var md = smask.Dictionary;
        int mw = checked((int)(md.GetInteger("Width") ?? 0)), mh = checked((int)(md.GetInteger("Height") ?? 0));
        if (mw <= 0 || mh <= 0 || (long)mw * mh > _limits.MaxImagePixels) return null;
        return decoded.ImageFilter == "DCTDecode" ? new PdfJpegMask(ValidJpeg(decoded.Data), mw, mh, IsInvertedDecode(md, 1)) : null;
    }

    /// <summary>
    /// Packed samples of a stream whose image codec the vector core decodes itself: CCITT fax and
    /// JBIG2 (bilevel), or none. Used for images and for their masks alike.
    /// </summary>
    private byte[] DecodeSamples(PdfDecodedStream decoded, int width, int height, CancellationToken ct)
    {
        if (decoded.ImageFilter == "CCITTFaxDecode")
        {
            // Bilevel codec: its output is ordinary 1-bit samples (0 = black unless /BlackIs1).
            var parms = decoded.ImageFilterParms;
            var ccitt = new CcittFaxDecoder.Parameters(
                K: (int)(parms?.GetInteger("K") ?? 0),
                EndOfLine: ReadBool(parms, "EndOfLine", false),
                EncodedByteAlign: ReadBool(parms, "EncodedByteAlign", false),
                Columns: (int)(parms?.GetInteger("Columns") ?? 1728),
                Rows: (int)(parms?.GetInteger("Rows") ?? 0),
                EndOfBlock: ReadBool(parms, "EndOfBlock", true),
                BlackIs1: ReadBool(parms, "BlackIs1", false),
                DamagedRowsBeforeError: (int)(parms?.GetInteger("DamagedRowsBeforeError") ?? 0));
            if (ccitt.Columns != width)
                throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "CCITT /Columns differs from the image width.");
            return CcittFaxDecoder.Decode(decoded.Data, ccitt, height, ct);
        }
        if (decoded.ImageFilter == "JBIG2Decode")
        {
            var globals = decoded.ImageFilterParms is { } jp && _resolver.Resolve(jp["JBIG2Globals"]) is PdfStream g
                ? _decoder.DecodeStream(g)
                : null;
            return Jbig2Decoder.Decode(decoded.Data, globals, width, height, ct);
        }
        if (decoded.ImageFilter != null)
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.UnsupportedImageFilter, $"Image filter {decoded.ImageFilter} is not decoded by the vector core.");
        return decoded.Data;
    }

    // ---------------------------------------------------------------- JPEG 2000 (8.9.5.3 / 7.4.9)

    /// <summary>
    /// Decodes a JPXDecode image with CoreJ2K (managed). The codestream's dimensions and bit depths
    /// win over the dictionary's; without /ColorSpace the component count selects Gray/RGB/CMYK;
    /// /SMaskInData 1 or 2 takes opacity from the extra component; /Decode is ignored (7.4.9).
    /// </summary>
    private PdfDecodedImage DecodeJpx(PdfDictionary dict, byte[] data, byte[]? alpha, CancellationToken ct)
    {
        // The decoder allocates from the header: check the SIZ marker against the limits first.
        if (!TryReadJpxSize(data, out long sizW, out long sizH, out int sizComponents))
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "JPEG 2000 data has no image size (SIZ) marker.");
        if (sizW <= 0 || sizH <= 0 || sizW * sizH > _limits.MaxImagePixels || sizComponents is <= 0 or > 16)
            throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "JPEG 2000 size exceeds the image limits.");

        // With an /Indexed space the samples are indices into the PDF's palette (8.9.5.3), so a JP2
        // file's own palette (pclr box) must not be applied: decode its bare codestream instead.
        // Applying it (seen in real producer output) turned a white map black.
        if (_colorSpace?.Name == "Indexed" && TryGetJp2Codestream(data) is { } codestream)
            data = codestream;

        JpxPlanes image;
        try
        {
            image = ReadJpx(data, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, $"JPEG 2000 codestream could not be decoded ({ex.GetType().Name}).");
        }
        {
            ct.ThrowIfCancellationRequested();
            int width = image.Width, height = image.Height, count = image.Planes.Length;
            if (width <= 0 || height <= 0 || (long)width * height > _limits.MaxImagePixels || count <= 0)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "JPEG 2000 dimensions are invalid or exceed the pixel limit.");

            long smaskInData = dict.GetInteger("SMaskInData") ?? 0;
            int colourCount = smaskInData != 0 && count > 1 ? count - 1 : count; // the opacity channel is not colour
            var cs = _colorSpace ?? (colourCount >= 4 ? PdfColorSpace.DeviceCmyk : colourCount >= 3 ? PdfColorSpace.DeviceRgb : PdfColorSpace.DeviceGray);
            int n = Math.Max(1, cs.NumberOfComponents);
            if (count < n)
                throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "JPEG 2000 has fewer components than its colour space.");
            bool indexed = cs.Name == "Indexed";
            int alphaComponent = smaskInData != 0 && count > n && alpha == null ? n : -1;

            var planes = new int[count][];
            var scale = new double[count];
            for (int c = 0; c < count; c++)
            {
                planes[c] = image.Planes[c];
                int depth = Math.Clamp(image.Depths[c], 1, 31);
                scale[c] = 1.0 / ((1L << depth) - 1);
                if (planes[c].Length < width * height)
                    throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "JPEG 2000 components are subsampled.");
            }

            var bgra = new byte[checked(width * height * 4)];
            var comps = new double[n];
            Span<byte> rgb = stackalloc byte[3];
            // Each distinct sample tuple is converted once (see DecodeColor).
            int keyBits = 1;
            for (int c = 0; c < n; c++) keyBits = Math.Max(keyBits, Math.Clamp(image.Depths[c], 1, 31));
            var memo = n * keyBits < 64 ? new SampleColorMemo() : null;
            int keyLimit = memo != null ? 1 << keyBits : 0;
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    long key = 0;
                    bool keyed = memo != null;
                    for (int c = 0; c < n && keyed; c++)
                    {
                        int v = planes[c][i];
                        keyed = v >= 0 && v < keyLimit;
                        key = (key << keyBits) | (uint)v;
                    }
                    if (!keyed || !memo!.TryGet(key, out int packed))
                    {
                        for (int c = 0; c < n; c++)
                            comps[c] = indexed ? planes[c][i] : Math.Clamp(planes[c][i] * scale[c], 0, 1);
                        cs.ToRgb24(comps, rgb);
                        packed = rgb[2] | rgb[1] << 8 | rgb[0] << 16;
                        if (keyed) memo!.Set(key, packed);
                    }
                    int p = i * 4;
                    bgra[p] = (byte)packed;
                    bgra[p + 1] = (byte)(packed >> 8);
                    bgra[p + 2] = (byte)(packed >> 16);
                    bgra[p + 3] = alphaComponent >= 0 ? (byte)Math.Round(Math.Clamp(planes[alphaComponent][i] * scale[alphaComponent], 0, 1) * 255) : (byte)255;
                }
            }

            if (alpha != null && alpha.Length == width * height)
            {
                for (int i = 0, p = 3; i < alpha.Length; i++, p += 4)
                    bgra[p] = (byte)(bgra[p] * alpha[i] / 255);
            }
            return new PdfDecodedImage(width, height, PdfDecodedImageFormat.Bgra32, bgra);
        }
    }

    /// <summary>A decoded JPEG 2000 image: each component's samples and bit depth.</summary>
    internal sealed record JpxPlanes(int Width, int Height, int[][] Planes, int[] Depths);

    // CoreJ2K before 2.4 rented its wavelet reconstruction buffers from the shared ArrayPool
    // without clearing them, and code-blocks with no coded passes (most of the chroma subbands of a
    // lossy colour image) never wrote their area. Those areas kept whatever the pool last held, so
    // the same codestream decoded differently from run to run. CoreJ2K 2.4 clears the buffers on
    // rent and decodes the same codestream bit-identically, on any thread and in parallel. Buffers
    // are also cleared when returned, so samples of one document do not stay in the shared pool.
    static PdfImageSource() => CoreJ2K.j2k.wavelet.synthesis.InvWTFull.ClearArrayPoolBuffersOnReturn = true;

    /// <summary>Decodes a JPEG 2000 codestream or JP2 file into its components.</summary>
    internal static JpxPlanes ReadJpx(byte[] data, CancellationToken ct = default)
    {
        var config = new CoreJ2K.Configuration.J2KDecoderConfiguration().WithVerbose(false).WithCancellationToken(ct);
        using var image = CoreJ2K.J2kImage.FromBytes(data, config);
        int count = image.NumberOfComponents;
        var planes = new int[count][];
        var depths = new int[count];
        for (int c = 0; c < count; c++)
        {
            planes[c] = (int[])image.GetComponent(c).Clone(); // a copy: the decoder may reuse the array it returned
            depths[c] = image.GetBitDepth(c);
        }
        return new JpxPlanes(image.Width, image.Height, planes, depths);
    }
    /// <summary>The contiguous codestream (jp2c box) of a JP2 file (ISO/IEC 15444-1 I.5), or null for a bare codestream.</summary>
    internal static byte[]? TryGetJp2Codestream(byte[] data)
    {
        // JP2 signature box: length 12, type 'jP  ', content 0D 0A 87 0A.
        if (data.Length < 12 || data[4] != (byte)'j' || data[5] != (byte)'P' || data[6] != (byte)' ' || data[7] != (byte)' ')
            return null;
        long pos = 0;
        while (pos + 8 <= data.Length)
        {
            long len = (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]);
            int header = 8;
            if (len == 1)
            {
                if (pos + 16 > data.Length) return null;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | data[pos + 8 + i];
                header = 16;
            }
            else if (len == 0)
            {
                len = data.Length - pos; // last box runs to the end of the file
            }
            if (len < header || pos + len > data.Length) return null;
            if (data[pos + 4] == 'j' && data[pos + 5] == 'p' && data[pos + 6] == '2' && data[pos + 7] == 'c')
                return data.AsSpan((int)(pos + header), (int)(len - header)).ToArray();
            pos += len;
        }
        return null;
    }

    /// <summary>Image size and component count from the codestream's SIZ marker (ISO/IEC 15444-1 A.5.1).</summary>
    internal static bool TryReadJpxSize(ReadOnlySpan<byte> data, out long width, out long height, out int components)
    {
        width = height = 0;
        components = 0;
        // SOC (FF 4F) is immediately followed by SIZ (FF 51) in a codestream, also inside a JP2 jp2c box.
        for (int i = 0; i + 42 <= data.Length; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0x4F || data[i + 2] != 0xFF || data[i + 3] != 0x51)
                continue;
            var siz = data[(i + 4)..];
            // Lsiz(2) Rsiz(2) Xsiz(4) Ysiz(4) XOsiz(4) YOsiz(4) XTsiz YTsiz XTOsiz YTOsiz (16) Csiz(2)
            long xsiz = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(siz[4..]);
            long ysiz = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(siz[8..]);
            long xo = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(siz[12..]);
            long yo = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(siz[16..]);
            width = xsiz - xo;
            height = ysiz - yo;
            components = siz[36] << 8 | siz[37];
            return true;
        }
        return false;
    }

    /// <summary>
    /// JPEG data from its SOI marker: bytes before it (a stray end-of-line after <c>stream</c>, for
    /// example) are skipped, as libjpeg does when it scans for the first marker.
    /// </summary>
    internal static byte[] FromStartOfImage(byte[] data)
    {
        int limit = Math.Min(data.Length - 1, 1024);
        for (int i = 0; i < limit; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD8)
                return i == 0 ? data : data.AsSpan(i).ToArray();
        }
        return data;
    }

    /// <summary>
    /// JPEG data from its SOI marker, refused (a typed ImageDecode failure) when it holds no image:
    /// no frame header (SOF) and scan (SOS) before its end. Such data never reaches a backend codec,
    /// whose native error recovery (libjpeg's "JPEG datastream contains no image" inside WIC) would
    /// otherwise run on every render of the page.
    /// </summary>
    internal static byte[] ValidJpeg(byte[] data)
    {
        var jpeg = FromStartOfImage(data);
        if (!HasFrameAndScan(jpeg))
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "JPEG data has no frame (SOF) and scan (SOS) before its end.");
        return jpeg;
    }

    /// <summary>
    /// Walks the JPEG markers from SOI the way libjpeg reads them (stray bytes between segments are
    /// skipped) and tells whether a frame header comes before the first scan.
    /// </summary>
    internal static bool HasFrameAndScan(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return false;
        bool frame = false;
        int i = 2;
        while (i < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; } // extraneous data before a marker
            while (i < jpeg.Length && jpeg[i] == 0xFF) i++; // fill bytes
            if (i >= jpeg.Length) return false;
            byte marker = jpeg[i++];
            if (marker == 0x00 || marker == 0x01 || marker is >= 0xD0 and <= 0xD8) continue; // no length
            if (marker == 0xD9) return false; // EOI before any scan
            if (marker == 0xDA) return frame;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) frame = true;
            if (i + 1 >= jpeg.Length) return false;
            int length = (jpeg[i] << 8) | jpeg[i + 1];
            if (length < 2) return false;
            i += length;
        }
        return false;
    }

    private bool ReadBool(PdfDictionary? parms, string key, bool fallback) =>
        parms != null && _resolver.Resolve(parms[key]) is PdfBoolean b ? b.Value : fallback;

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
        var bgra = new byte[checked(width * height * 4)];
        byte r = ToByte(_stencilColor.R), g = ToByte(_stencilColor.G), b = ToByte(_stencilColor.B);
        // The colour is the same everywhere; only alpha follows the samples.
        uint colour = b | (uint)g << 8 | (uint)r << 16;
        uint painted = colour | 0xFF000000u;
        long rowBytes = ((long)width + 7) / 8;
        for (int y = 0; y < height; y++)
        {
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(y * width * 4, width * 4));
            long start = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                long i = start + (x >> 3);
                int sample = i < data.Length ? (data[i] >> (7 - (x & 7))) & 1 : 0;
                bool paint = invert ? sample == 1 : sample == 0;
                dst[x] = paint ? painted : colour;
            }
        }
        return bgra;
    }

    // ---------------------------------------------------------------- colour images

    private byte[] DecodeColor(PdfDictionary dict, byte[] data, int width, int height, CancellationToken ct, int? bitsPerComponent = null)
    {
        var cs = _colorSpace ?? PdfColorSpace.DeviceRgb;
        int n = Math.Max(1, cs.NumberOfComponents);
        int bpc = bitsPerComponent ?? (int)(dict.GetInteger("BitsPerComponent") ?? 8);
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

        // An ICCBased space without a profile transform (an sRGB profile, or none usable) draws
        // through its DeviceRGB alternate, which passes 8-bit samples through unchanged.
        bool deviceRgb = cs.Name == "DeviceRGB"
                         || cs is PdfColorSpace.IccBasedColorSpace { UsesProfile: false, AlternateColorSpace.Name: "DeviceRGB" };
        bool fastRgb8 = deviceRgb && bpc == 8 && IsDefaultDecode(dMin, dMax);

        // Colour-managed 8-bit images (CMYK, ICC) convert through the space's sampled lookup table.
        if (lut == null && !fastRgb8 && bpc == 8 && cs.ImageLut is { } imageLut && imageLut.Channels == n
            && DecodeDirection(dMin, dMax) is { } inverted)
        {
            return DecodeWithLut(imageLut, data, width, height, n, inverted, colorKey, ct);
        }

        if (colorKey == null && bpc == 8 && cs is PdfColorSpace.CalRgbColorSpace calRgb && IsDefaultDecode(dMin, dMax))
        {
            calRgb.ToBgra8(data, bgra, width, height, ct);
            return bgra;
        }

        if (colorKey == null && (lut != null || fastRgb8))
        {
            FillRows(data, bgra, width, height, bpc, lut, ct);
            return bgra;
        }

        // Every other space converts each distinct sample tuple once: photographs repeat colours
        // heavily, and a CalRGB or Lab conversion costs several powers per pixel.
        var memo = n <= 4 && n * bpc < 64 ? new SampleColorMemo() : null;

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
                else
                {
                    long key = 0;
                    if (memo != null)
                        for (int c = 0; c < n; c++) key = (key << bpc) | (uint)raw[c];
                    if (memo == null || !memo.TryGet(key, out int packed))
                    {
                        for (int c = 0; c < n; c++)
                            comps[c] = dMin[c] + Math.Min(raw[c], maxSample) * (dMax[c] - dMin[c]) / maxSample;
                        cs.ToRgb24(comps, rgb);
                        packed = rgb[2] | rgb[1] << 8 | rgb[0] << 16;
                        memo?.Set(key, packed);
                    }
                    bgra[p] = (byte)packed;
                    bgra[p + 1] = (byte)(packed >> 8);
                    bgra[p + 2] = (byte)(packed >> 16);
                }
                bgra[p + 3] = alpha;
            }
        }

        return bgra;
    }

    /// <summary>
    /// Opaque pixels of a single-component image through its sample table, or of DeviceRGB 8-bit
    /// samples, row by row and in parallel for large images. Reads samples as <see cref="BitReader"/>
    /// does: rows start on byte boundaries and data past the end reads as zero.
    /// </summary>
    private static void FillRows(byte[] data, byte[] bgra, int width, int height, int bpc, byte[]? lut, CancellationToken ct)
    {
        uint[]? table = null;
        if (lut != null)
        {
            table = new uint[lut.Length / 3];
            for (int s = 0; s < table.Length; s++)
                table[s] = lut[s * 3 + 2] | (uint)lut[s * 3 + 1] << 8 | (uint)lut[s * 3] << 16 | 0xFF000000u;
        }
        long rowBytes = table != null ? ((long)width * bpc + 7) / 8 : (long)width * 3;
        int mask = (1 << Math.Min(bpc, 8)) - 1;

        void Rows(int from, int to)
        {
            for (int y = from; y < to; y++)
            {
                var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(y * width * 4, width * 4));
                long start = y * rowBytes;
                if (table == null)
                {
                    bool whole = start + rowBytes <= data.Length;
                    for (int x = 0; x < width; x++)
                    {
                        long i = start + 3L * x;
                        uint r, g, b;
                        if (whole) { r = data[i]; g = data[i + 1]; b = data[i + 2]; }
                        else
                        {
                            r = i < data.Length ? data[i] : 0u;
                            g = i + 1 < data.Length ? data[i + 1] : 0u;
                            b = i + 2 < data.Length ? data[i + 2] : 0u;
                        }
                        dst[x] = b | g << 8 | r << 16 | 0xFF000000u;
                    }
                }
                else if (bpc == 8)
                {
                    for (int x = 0; x < width; x++)
                    {
                        long i = start + x;
                        dst[x] = table[i < data.Length ? data[i] : 0];
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        long bit = (long)x * bpc;
                        long i = start + (bit >> 3);
                        int b = i < data.Length ? data[i] : 0;
                        dst[x] = table[(b >> (8 - bpc - (int)(bit & 7))) & mask];
                    }
                }
            }
        }

        const int band = 64;
        if ((long)width * height < 1 << 18)
        {
            for (int y = 0; y < height; y += band)
            {
                ct.ThrowIfCancellationRequested();
                Rows(y, Math.Min(height, y + band));
            }
            return;
        }
        System.Threading.Tasks.Parallel.For(0, (height + band - 1) / band, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct },
            i => Rows(i * band, Math.Min(height, (i + 1) * band)));
    }

    /// <summary>
    /// A small direct-mapped cache from packed raw samples to the colour they convert to. Bounded
    /// (64 K entries); a colliding colour simply replaces the older one.
    /// </summary>
    private sealed class SampleColorMemo
    {
        private const int Bits = 16;
        private readonly long[] _keys = new long[1 << Bits];
        private readonly int[] _values = new int[1 << Bits];

        public SampleColorMemo() => Array.Fill(_keys, -1L);

        private static int Slot(long key) => (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15UL)) >> (64 - Bits));

        public bool TryGet(long key, out int value)
        {
            int slot = Slot(key);
            value = _values[slot];
            return _keys[slot] == key;
        }

        public void Set(long key, int value)
        {
            int slot = Slot(key);
            _keys[slot] = key;
            _values[slot] = value;
        }
    }

    /// <summary>
    /// Converts 8-bit samples through <paramref name="lut"/>; <paramref name="inverted"/> applies a
    /// /Decode of [1 0 ...] first. Short data reads as zero samples, as <see cref="BitReader"/> does.
    /// </summary>
    private static byte[] DecodeWithLut(ColorLut8 lut, byte[] data, int width, int height, int n, bool inverted, int[]? colorKey, CancellationToken ct)
    {
        int pixels = checked(width * height);
        int sampleCount = checked(pixels * n);
        byte[] samples = data;
        if (data.Length < sampleCount || inverted)
        {
            samples = new byte[sampleCount];
            data.AsSpan(0, Math.Min(data.Length, sampleCount)).CopyTo(samples);
            if (inverted)
                for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(255 - samples[i]);
        }
        ct.ThrowIfCancellationRequested();
        var bgra = new byte[checked(pixels * 4)];
        lut.ToBgra(samples, 0, bgra, 0, pixels);
        ct.ThrowIfCancellationRequested();

        if (colorKey != null)
        {
            // Colour-key masking compares the raw samples, before /Decode.
            for (int p = 0; p < pixels; p++)
            {
                bool masked = true;
                for (int c = 0; c < n && masked; c++)
                {
                    int raw = p * n + c < data.Length ? data[p * n + c] : 0;
                    masked = raw >= colorKey[c * 2] && raw <= colorKey[c * 2 + 1];
                }
                if (masked) bgra[p * 4 + 3] = 0;
            }
        }
        return bgra;
    }

    /// <summary>False for the default /Decode, true for a fully inverted one, null for any other mapping.</summary>
    private static bool? DecodeDirection(double[] dMin, double[] dMax)
    {
        if (IsDefaultDecode(dMin, dMax)) return false;
        for (int i = 0; i < dMin.Length; i++)
        {
            if (dMin[i] != 1 || dMax[i] != 0) return null;
        }
        return true;
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

    private byte[]? BuildAlpha(PdfDictionary dict, int width, int height, PdfStream? smask, PdfDecodedStream? smaskData, CancellationToken ct)
    {
        if (smask != null && smaskData != null)
            return smaskData.ImageFilter == "DCTDecode" ? null // the backend applies it (JpegSoftMask)
                : DecodeMaskPlane(smask, width, height, soft: true, ct, smaskData);
        if (_resolver.Resolve(dict["Mask"]) is PdfStream stencil)
            return DecodeMaskPlane(stencil, width, height, soft: false, ct);
        return null;
    }

    /// <summary>
    /// Decodes a soft mask (DeviceGray samples = alpha) or explicit stencil mask (1 = masked out)
    /// and resamples it nearest-neighbour to the base image size.
    /// </summary>
    /// <param name="decoded">The mask stream already decoded, or null to decode it here.</param>
    private byte[] DecodeMaskPlane(PdfStream maskStream, int width, int height, bool soft, CancellationToken ct, PdfDecodedStream? decoded = null)
    {
        var md = maskStream.Dictionary;
        int mw = checked((int)(md.GetInteger("Width") ?? 0));
        int mh = checked((int)(md.GetInteger("Height") ?? 0));
        if (mw <= 0 || mh <= 0 || (long)mw * mh > _limits.MaxImagePixels)
            throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "Mask dimensions invalid or too large.");

        decoded ??= _decoder.DecodeImageStream(maskStream);
        if (soft && decoded.ImageFilter == "JPXDecode")
            return Resample(DecodeJpxGray(decoded.Data, mw, mh, IsInvertedDecode(md, 1), ct), mw, mh, width, height);
        if (decoded.ImageFilter is "DCTDecode" or "JPXDecode")
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.UnsupportedImageFilter, $"Mask filter {decoded.ImageFilter} unsupported.");
        // CCITT fax and JBIG2 stencil masks (common in scanned and exported documents).
        byte[] maskSamples = DecodeSamples(decoded, mw, mh, ct);

        int bpc = soft ? (int)(md.GetInteger("BitsPerComponent") ?? 8) : 1;
        if (bpc is not (1 or 2 or 4 or 8 or 16))
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, $"Mask BitsPerComponent {bpc} unsupported.");
        int maxSample = (1 << bpc) - 1;
        bool invert = IsInvertedDecode(md, 1);

        var plane = new byte[mw * mh];
        var bits = new BitReader(maskSamples, mw, 1, bpc);
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

        return Resample(plane, mw, mh, width, height);
    }

    /// <summary>Nearest-neighbour resampling of a mask plane to the base image size.</summary>
    private static byte[] Resample(byte[] plane, int mw, int mh, int width, int height)
    {
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

    /// <summary>A JPEG 2000 soft mask: the first component, scaled to 0-255 (opacity).</summary>
    private byte[] DecodeJpxGray(byte[] data, int mw, int mh, bool invert, CancellationToken ct)
    {
        if (!TryReadJpxSize(data, out long w, out long h, out _) || w * h > _limits.MaxImagePixels)
            throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "JPEG 2000 mask size exceeds the image limits.");
        JpxPlanes image;
        try { image = ReadJpx(data, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, $"JPEG 2000 mask could not be decoded ({ex.GetType().Name}).");
        }
        {
            ct.ThrowIfCancellationRequested();
            if (image.Width != mw || image.Height != mh || image.Planes.Length == 0)
                throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "JPEG 2000 mask size differs from its dictionary.");
            var plane = image.Planes[0];
            double scale = 255.0 / ((1L << Math.Clamp(image.Depths[0], 1, 31)) - 1);
            var result = new byte[mw * mh];
            for (int i = 0; i < result.Length; i++)
            {
                byte a = (byte)Math.Clamp(Math.Round(plane[i] * scale), 0, 255);
                result[i] = invert ? (byte)(255 - a) : a;
            }
            return result;
        }
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
