using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEngine.Vector.Create;

/// <summary>
/// What a JPEG file says about itself, read from its markers without decoding it: the frame size
/// and components, whether a PDF reader's DCTDecode can take it as it is, the ICC profile (APP2
/// chunks put back together), the Adobe colour transform (APP14), the EXIF orientation (APP1) and
/// the JFIF density (APP0).
/// </summary>
public sealed record JpegHeader
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>1 (grey), 3 (YCbCr or RGB) or 4 (CMYK or YCCK).</summary>
    public required int Components { get; init; }
    public required int Precision { get; init; }
    /// <summary>The SOF marker (0xC0 baseline, 0xC1 extended, 0xC2 progressive, ...).</summary>
    public required int FrameMarker { get; init; }
    /// <summary>The file has an Adobe APP14 marker: four-component data is then stored inverted.</summary>
    public bool Adobe { get; init; }
    public byte[]? IccProfile { get; init; }
    /// <summary>EXIF orientation, 1 to 8 (1 when the file has none).</summary>
    public int Orientation { get; init; } = 1;
    /// <summary>Pixels per inch from JFIF or EXIF, or 0 when the file does not say.</summary>
    public double DpiX { get; init; }
    public double DpiY { get; init; }

    /// <summary>
    /// DCTDecode reads Huffman-coded baseline, extended and progressive 8-bit frames of 1, 3 or 4
    /// components; anything else (lossless, arithmetic coding, 12-bit) has to be decoded and written again.
    /// </summary>
    public bool CanPassThrough => FrameMarker is 0xC0 or 0xC1 or 0xC2 && Precision == 8 && Components is 1 or 3 or 4;

    /// <summary>Reads the header of a JPEG file, or null when the data is not one.</summary>
    public static JpegHeader? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        bool adobe = false;
        int orientation = 1;
        double dpiX = 0, dpiY = 0, exifDpiX = 0, exifDpiY = 0;
        var icc = new SortedDictionary<int, byte[]>();
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) return null;
            int marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill bytes
            pos += 2;
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue; // no length
            if (marker == 0xD9 || pos + 2 > data.Length) return null;
            int length = data[pos] << 8 | data[pos + 1];
            if (length < 2 || pos + length > data.Length) return null;
            var segment = data.Slice(pos + 2, length - 2);
            switch (marker)
            {
                case 0xE0 when segment.Length >= 12 && segment[..5].SequenceEqual("JFIF\0"u8):
                {
                    int units = segment[7];
                    int x = segment[8] << 8 | segment[9], y = segment[10] << 8 | segment[11];
                    if (units == 1) { dpiX = x; dpiY = y; }
                    else if (units == 2) { dpiX = x * 2.54; dpiY = y * 2.54; }
                    break;
                }
                case 0xE1 when segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8):
                    ReadExif(segment[6..], ref orientation, ref exifDpiX, ref exifDpiY);
                    break;
                case 0xE2 when segment.Length > 14 && segment[..12].SequenceEqual("ICC_PROFILE\0"u8):
                    icc[segment[12]] = segment[14..].ToArray();
                    break;
                case 0xEE when segment.Length >= 5 && segment[..5].SequenceEqual("Adobe"u8):
                    adobe = true;
                    break;
                case >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC:
                {
                    if (segment.Length < 6) return null;
                    int precision = segment[0];
                    int height = segment[1] << 8 | segment[2], width = segment[3] << 8 | segment[4];
                    int components = segment[5];
                    if (width == 0 || height == 0) return null; // a height given by a DNL marker: not worth supporting
                    if (dpiX <= 0 && exifDpiX > 0) { dpiX = exifDpiX; dpiY = exifDpiY > 0 ? exifDpiY : exifDpiX; }
                    return new JpegHeader
                    {
                        Width = width, Height = height, Components = components, Precision = precision, FrameMarker = marker,
                        Adobe = adobe, IccProfile = Join(icc), Orientation = orientation, DpiX = dpiX, DpiY = dpiY > 0 ? dpiY : dpiX,
                    };
                }
            }
            pos += length;
        }
        return null;
    }

    private static byte[]? Join(SortedDictionary<int, byte[]> chunks)
    {
        if (chunks.Count == 0) return null;
        using var ms = new MemoryStream();
        foreach (var chunk in chunks.Values) ms.Write(chunk);
        return ms.Length >= 128 ? ms.ToArray() : null; // shorter than an ICC header: not a profile
    }

    /// <summary>The orientation (0x0112) and resolution (0x011A, 0x011B, 0x0128) of IFD0.</summary>
    private static void ReadExif(ReadOnlySpan<byte> tiff, ref int orientation, ref double dpiX, ref double dpiY)
    {
        if (tiff.Length < 8) return;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return;
        int U16(ReadOnlySpan<byte> s, int at) => at + 2 > s.Length ? 0 : little ? s[at] | s[at + 1] << 8 : s[at] << 8 | s[at + 1];
        uint U32(ReadOnlySpan<byte> s, int at) => at + 4 > s.Length ? 0 :
            little ? (uint)(s[at] | s[at + 1] << 8 | s[at + 2] << 16 | s[at + 3] << 24) : (uint)(s[at] << 24 | s[at + 1] << 16 | s[at + 2] << 8 | s[at + 3]);
        uint ifd = U32(tiff, 4);
        if (ifd < 8 || ifd + 2 > tiff.Length) return;
        int count = U16(tiff, (int)ifd);
        double xRes = 0, yRes = 0;
        int unit = 2; // inches
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            int tag = U16(tiff, entry);
            switch (tag)
            {
                case 0x0112:
                    int o = U16(tiff, entry + 8);
                    if (o is >= 1 and <= 8) orientation = o;
                    break;
                case 0x011A or 0x011B:
                {
                    uint offset = U32(tiff, entry + 8);
                    if (offset + 8 > tiff.Length) break;
                    uint num = U32(tiff, (int)offset), den = U32(tiff, (int)offset + 4);
                    double value = den == 0 ? 0 : (double)num / den;
                    if (tag == 0x011A) xRes = value; else yRes = value;
                    break;
                }
                case 0x0128:
                    unit = U16(tiff, entry + 8);
                    break;
            }
        }
        double k = unit == 3 ? 2.54 : unit == 2 ? 1 : 0;
        dpiX = xRes * k;
        dpiY = yRes * k;
    }
}
