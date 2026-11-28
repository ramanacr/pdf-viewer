using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfViewer.Web.Engine;

/// <summary>
/// Page pictures as TIFF and BMP, the two formats Windows' Export to Images offers besides PNG and
/// JPEG, for hosts without an encoder of their own for them (Android's Skia has none). Input is a
/// rendered page: BGRA, premultiplied and opaque (pages are drawn over paper), <c>width × 4</c>
/// bytes per row. Both are written as 24-bit RGB with the resolution in them.
/// </summary>
public static class ViewerImageEncoders
{
    /// <summary>
    /// A baseline TIFF: one strip of 8-bit RGB, compressed with Deflate (compression 8, which
    /// libtiff, Windows, macOS and print software read), the resolution in dots per inch.
    /// </summary>
    public static byte[] Tiff(ReadOnlySpan<byte> bgra, int width, int height, int dpi)
    {
        Check(bgra, width, height, dpi);
        byte[] strip;
        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[width * 3];
                for (int y = 0; y < height; y++)
                {
                    var source = bgra.Slice(y * width * 4, width * 4);
                    for (int x = 0, o = 0; x < width; x++, o += 3)
                    {
                        row[o] = source[4 * x + 2];
                        row[o + 1] = source[4 * x + 1];
                        row[o + 2] = source[4 * x];
                    }
                    z.Write(row);
                }
            }
            strip = packed.ToArray();
        }

        // Header, then the strip, then the IFD and its out-of-line values (word aligned).
        const int entries = 12;
        int stripOffset = 8;
        int ifdOffset = Align(stripOffset + strip.Length);
        int extra = ifdOffset + 2 + entries * 12 + 4;
        int bitsOffset = extra, xResOffset = bitsOffset + 8, yResOffset = xResOffset + 8;
        var tiff = new byte[yResOffset + 8];
        var span = tiff.AsSpan();
        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)ifdOffset);
        strip.CopyTo(span[stripOffset..]);

        int p = ifdOffset;
        BinaryPrimitives.WriteUInt16LittleEndian(span[p..], entries);
        p += 2;
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            var e = tiff.AsSpan(p, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(e, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], count);
            if (type == 3 && count == 1) BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)value);
            else BinaryPrimitives.WriteUInt32LittleEndian(e[8..], value);
            p += 12;
        }
        const ushort Short = 3, Long = 4, Rational = 5;
        // Tags in ascending order, as TIFF 6.0 asks.
        Entry(256, Long, 1, (uint)width);             // ImageWidth
        Entry(257, Long, 1, (uint)height);            // ImageLength
        Entry(258, Short, 3, (uint)bitsOffset);       // BitsPerSample 8, 8, 8
        Entry(259, Short, 1, 8);                      // Compression: Deflate
        Entry(262, Short, 1, 2);                      // PhotometricInterpretation: RGB
        Entry(273, Long, 1, (uint)stripOffset);       // StripOffsets
        Entry(277, Short, 1, 3);                      // SamplesPerPixel
        Entry(278, Long, 1, (uint)height);            // RowsPerStrip
        Entry(279, Long, 1, (uint)strip.Length);      // StripByteCounts
        Entry(282, Rational, 1, (uint)xResOffset);    // XResolution
        Entry(283, Rational, 1, (uint)yResOffset);    // YResolution
        Entry(296, Short, 1, 2);                      // ResolutionUnit: inch
        BinaryPrimitives.WriteUInt32LittleEndian(span[p..], 0); // no next IFD

        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteUInt16LittleEndian(span[(bitsOffset + 2 * i)..], 8);
        foreach (int offset in new[] { xResOffset, yResOffset })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)dpi);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 1);
        }
        return tiff;
    }

    /// <summary>A Windows bitmap: 24-bit, rows bottom-up and padded to four bytes, the resolution in pixels per metre.</summary>
    public static byte[] Bmp(ReadOnlySpan<byte> bgra, int width, int height, int dpi)
    {
        Check(bgra, width, height, dpi);
        int stride = (width * 3 + 3) & ~3;
        long size = 54L + (long)stride * height;
        if (size > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), "The picture is too large for a BMP file.");
        var bmp = new byte[size];
        var span = bmp.AsSpan();
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], (int)size);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], 54);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], stride * height);
        int perMetre = (int)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], perMetre);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], perMetre);
        for (int y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * width * 4, width * 4);
            var target = span.Slice(54 + (height - 1 - y) * stride, width * 3);
            for (int x = 0; x < width; x++)
            {
                target[3 * x] = source[4 * x];
                target[3 * x + 1] = source[4 * x + 1];
                target[3 * x + 2] = source[4 * x + 2];
            }
        }
        return bmp;
    }

    private static int Align(int offset) => (offset + 1) & ~1;

    private static void Check(ReadOnlySpan<byte> bgra, int width, int height, int dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        if (bgra.Length < (long)width * height * 4) throw new ArgumentException("The pixels are fewer than width × height.", nameof(bgra));
    }
}
