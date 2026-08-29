using System.IO.Compression;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Lightweight pure .NET zero-dependency PNG and BMP image encoder for headless and UI-neutral export.
/// </summary>
public static class ImageEncoder
{
    public static void SaveAsPng(Stream outputStream, int width, int height, ReadOnlySpan<byte> bgraPixels, int stride)
    {
        using var writer = new BinaryWriter(outputStream, System.Text.Encoding.UTF8, leaveOpen: true);
        // PNG Header
        writer.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        // IHDR Chunk
        using (var ms = new MemoryStream())
        using (var chunkWriter = new BinaryWriter(ms))
        {
            chunkWriter.Write(ToBigEndian(width));
            chunkWriter.Write(ToBigEndian(height));
            chunkWriter.Write((byte)8); // Bit depth: 8
            chunkWriter.Write((byte)6); // Color type: 6 (RGBA)
            chunkWriter.Write((byte)0); // Compression method
            chunkWriter.Write((byte)0); // Filter method
            chunkWriter.Write((byte)0); // Interlace method
            WriteChunk(writer, "IHDR", ms.ToArray());
        }

        // IDAT Chunk (zlib compressed scanlines with filter byte 0)
        byte[] uncompressedData = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int scanlineStart = y * (1 + width * 4);
            uncompressedData[scanlineStart] = 0; // None filter
            int srcRowStart = y * stride;

            for (int x = 0; x < width; x++)
            {
                int srcIdx = srcRowStart + (x * 4);
                int dstIdx = scanlineStart + 1 + (x * 4);

                byte b = bgraPixels[srcIdx];
                byte g = bgraPixels[srcIdx + 1];
                byte r = bgraPixels[srcIdx + 2];
                byte a = bgraPixels[srcIdx + 3];

                uncompressedData[dstIdx] = r;
                uncompressedData[dstIdx + 1] = g;
                uncompressedData[dstIdx + 2] = b;
                uncompressedData[dstIdx + 3] = a;
            }
        }

        using (var compressedMs = new MemoryStream())
        {
            // Write zlib stream header
            compressedMs.WriteByte(0x78);
            compressedMs.WriteByte(0x9C);

            using (var deflateStream = new DeflateStream(compressedMs, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflateStream.Write(uncompressedData, 0, uncompressedData.Length);
            }

            uint adler = CalculateAdler32(uncompressedData);
            byte[] adlerBytes = BitConverter.GetBytes(ToBigEndian((int)adler));
            compressedMs.Write(adlerBytes, 0, 4);

            WriteChunk(writer, "IDAT", compressedMs.ToArray());
        }

        // IEND Chunk
        WriteChunk(writer, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(BinaryWriter writer, string type, byte[] data)
    {
        writer.Write(ToBigEndian(data.Length));
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        writer.Write(typeBytes);
        if (data.Length > 0)
        {
            writer.Write(data);
        }

        uint crc = Crc32(typeBytes, data);
        writer.Write(ToBigEndian((int)crc));
    }

    private static int ToBigEndian(int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt32(bytes, 0);
    }

    private static uint CalculateAdler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        const uint mod = 65521;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint[] table = GetCrcTable();
        uint crc = 0xFFFFFFFF;

        for (int i = 0; i < type.Length; i++)
            crc = (crc >> 8) ^ table[(crc ^ type[i]) & 0xFF];
        for (int i = 0; i < data.Length; i++)
            crc = (crc >> 8) ^ table[(crc ^ data[i]) & 0xFF];

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[]? _crcTable;
    private static uint[] GetCrcTable()
    {
        if (_crcTable != null) return _crcTable;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        _crcTable = table;
        return table;
    }
}
