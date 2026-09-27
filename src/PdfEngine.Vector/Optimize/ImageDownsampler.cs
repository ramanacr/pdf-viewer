using System;
using System.Collections.Generic;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Optimize;

/// <summary>
/// Downsamples an image XObject by averaging (each new pixel is the mean of the source pixels it
/// covers), keeping its colour space and /Decode: 8-bit images in Flate and the other general
/// filters, and JPEG images through the host's codec. Its soft mask is downsampled with it.
/// Indexed and colour-keyed images (averaging would change their colours), stencil masks and
/// images in other codecs are left as they are.
/// </summary>
internal static class ImageDownsampler
{
    public static (PdfStream Image, PdfStream? Mask)? Downsample(PdfObjectResolver r, PdfStream image, double factor, PdfOptimizeOptions options, out bool jpegNeedsCodec)
    {
        jpegNeedsCodec = false;
        if (factor >= 0.95) return null;
        var d = image.Dictionary;
        if (r.Resolve(d["ImageMask"]) is PdfBoolean { Value: true } || r.Resolve(d["Mask"]) is PdfArray) return null;
        if ((r.Resolve(d["BitsPerComponent"]) as PdfInteger)?.Value != 8) return null;
        int components = Components(r, d["ColorSpace"]);
        if (components <= 0) return null;
        bool jpeg = false;
        var downsampled = Resample(r, image, components, factor, options, ref jpeg, out jpegNeedsCodec);
        if (downsampled == null) return null;
        var (samples, width, height) = downsampled.Value;

        var entries = new Dictionary<string, PdfObject>(d.Entries);
        entries.Remove("Length"); entries.Remove("DL"); entries.Remove("DecodeParms"); entries.Remove("Filter");
        entries["Width"] = new PdfInteger(width);
        entries["Height"] = new PdfInteger(height);
        byte[] data;
        if (jpeg)
        {
            if (options.JpegCodec!.Encode(samples, width, height, components, options.JpegQuality) is not { } encoded) return null;
            data = encoded;
            entries["Filter"] = new PdfName("DCTDecode");
        }
        else
        {
            data = PdfOptimizer.Deflate(samples);
            entries["Filter"] = new PdfName("FlateDecode");
        }
        if (data.Length >= image.GetRawBytes().Length) return null;

        PdfStream? mask = null;
        if (r.Resolve(d["SMask"]) is PdfStream smask && (r.Resolve(smask.Dictionary["BitsPerComponent"]) as PdfInteger)?.Value == 8)
        {
            bool maskJpeg = false;
            if (Resample(r, smask, 1, factor, options, ref maskJpeg, out _) is { } m)
            {
                var me = new Dictionary<string, PdfObject>(smask.Dictionary.Entries);
                me.Remove("Length"); me.Remove("DL"); me.Remove("DecodeParms"); me.Remove("Filter");
                me["Width"] = new PdfInteger(m.Width);
                me["Height"] = new PdfInteger(m.Height);
                me["Filter"] = new PdfName("FlateDecode"); // a mask is kept lossless
                mask = PdfObjectWriter.NewStream(me, PdfOptimizer.Deflate(m.Samples));
            }
        }
        return (PdfObjectWriter.NewStream(entries, data), mask);
    }

    private static (byte[] Samples, int Width, int Height)? Resample(PdfObjectResolver r, PdfStream stream, int components, double factor,
        PdfOptimizeOptions options, ref bool jpeg, out bool jpegNeedsCodec)
    {
        jpegNeedsCodec = false;
        var d = stream.Dictionary;
        int w = (int)((r.Resolve(d["Width"]) as PdfInteger)?.Value ?? 0), h = (int)((r.Resolve(d["Height"]) as PdfInteger)?.Value ?? 0);
        if (w <= 1 || h <= 1) return null;
        PdfDecodedStream decoded;
        try { decoded = new PdfStreamDecoder(null, r.Resolve).DecodeImageStream(stream); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        byte[] samples;
        if (decoded.ImageFilter == "DCTDecode")
        {
            if (options.JpegCodec is not { } codec || components == 4) { jpegNeedsCodec = options.JpegCodec == null; return null; }
            if (codec.Decode(decoded.Data) is not { } pixels || pixels.Components != components || pixels.Width != w || pixels.Height != h) return null;
            samples = pixels.Samples;
            jpeg = true;
        }
        else if (decoded.ImageFilter == null)
        {
            samples = decoded.Data;
            if (samples.Length < (long)w * h * components) return null;
        }
        else return null;

        int nw = Math.Max(1, (int)Math.Round(w * factor)), nh = Math.Max(1, (int)Math.Round(h * factor));
        var result = new byte[nw * nh * components];
        var sums = new long[components];
        for (int y = 0; y < nh; y++)
        {
            int y0 = (int)((long)y * h / nh), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * h / nh));
            for (int x = 0; x < nw; x++)
            {
                int x0 = (int)((long)x * w / nw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * w / nw));
                Array.Clear(sums);
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int p = (sy * w + sx) * components;
                        for (int c = 0; c < components; c++) sums[c] += samples[p + c];
                    }
                int count = (y1 - y0) * (x1 - x0), o = (y * nw + x) * components;
                for (int c = 0; c < components; c++) result[o + c] = (byte)((sums[c] + count / 2) / count);
            }
        }
        return (result, nw, nh);
    }

    /// <summary>The number of colour components of an image's colour space; 0 for those averaging would break (Indexed, Pattern) or that are unknown.</summary>
    private static int Components(PdfObjectResolver r, PdfObject? space)
    {
        switch (r.Resolve(space))
        {
            case PdfName n:
                return n.Value switch { "DeviceGray" or "G" or "CalGray" => 1, "DeviceRGB" or "RGB" or "CalRGB" => 3, "DeviceCMYK" or "CMYK" => 4, _ => 0 };
            case PdfArray a when a.Count >= 1 && r.Resolve(a[0]) is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased" when a.Count >= 2 && r.Resolve(a[1]) is PdfStream icc:
                        return (int)((r.Resolve(icc.Dictionary["N"]) as PdfInteger)?.Value ?? 0);
                    case "CalGray": return 1;
                    case "CalRGB" or "Lab": return 3;
                    case "Separation": return 1;
                    case "DeviceN" when a.Count >= 2 && r.Resolve(a[1]) is PdfArray names: return names.Count;
                    default: return 0;
                }
            default:
                return 0;
        }
    }
}
