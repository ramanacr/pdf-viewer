using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace PdfEngine.Vector.Export;

/// <summary>Writes straight BGRA pixels as a PNG: RGB when every pixel is opaque, RGBA otherwise.</summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, ReadOnlySpan<byte> alpha = default)
    {
        bool hasAlpha = false;
        if (alpha.Length == width * height)
        {
            foreach (byte a in alpha) if (a != 255) { hasAlpha = true; break; }
        }
        else
        {
            for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 255) { hasAlpha = true; break; }
        }
        int channels = hasAlpha ? 4 : 3;

        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * channels];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // no filter
                int s = y * width * 4, d = 1;
                for (int x = 0; x < width; x++, s += 4)
                {
                    row[d++] = bgra[s + 2];
                    row[d++] = bgra[s + 1];
                    row[d++] = bgra[s];
                    if (hasAlpha)
                    {
                        byte a = bgra[s + 3];
                        if (alpha.Length == width * height) a = (byte)(a * alpha[y * width + x] / 255);
                        row[d++] = a;
                    }
                }
                z.Write(row);
            }
        }

        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;                      // bit depth
        header[9] = (byte)(hasAlpha ? 6 : 2); // colour type RGBA / RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        Chunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        Span<byte> name = stackalloc byte[4];
        for (int i = 0; i < 4; i++) name[i] = (byte)type[i];
        s.Write(name);
        s.Write(data);
        uint crc = Update(0xFFFFFFFFu, name);
        crc = Update(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        s.Write(four);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
