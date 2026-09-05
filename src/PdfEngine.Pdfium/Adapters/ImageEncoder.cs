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

    /// <summary>
    /// Writes a 24-bit uncompressed BMP. Rows are stored bottom-up and padded to a 4-byte
    /// boundary, per the BMP specification.
    /// </summary>
    public static void SaveAsBmp(Stream outputStream, int width, int height, ReadOnlySpan<byte> bgraPixels, int stride)
    {
        int rowSize = ((width * 3) + 3) & ~3;   // 4-byte aligned
        int pixelDataSize = rowSize * height;
        const int headerSize = 14 + 40;         // BITMAPFILEHEADER + BITMAPINFOHEADER

        using var writer = new BinaryWriter(outputStream, System.Text.Encoding.ASCII, leaveOpen: true);

        // BITMAPFILEHEADER
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(headerSize + pixelDataSize);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(headerSize);

        // BITMAPINFOHEADER
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);          // positive => bottom-up
        writer.Write((short)1);        // planes
        writer.Write((short)24);       // bits per pixel
        writer.Write(0);               // BI_RGB, no compression
        writer.Write(pixelDataSize);
        writer.Write(2835);            // ~72 DPI in pixels/metre
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        byte[] row = new byte[rowSize];
        for (int y = height - 1; y >= 0; y--)   // bottom-up
        {
            int srcRow = y * stride;
            Array.Clear(row, 0, row.Length);

            for (int x = 0; x < width; x++)
            {
                int src = srcRow + (x * 4);
                int dst = x * 3;
                // Source is BGRA; BMP stores BGR in the same order.
                row[dst] = bgraPixels[src];
                row[dst + 1] = bgraPixels[src + 1];
                row[dst + 2] = bgraPixels[src + 2];
            }

            writer.Write(row, 0, rowSize);
        }
    }

    /// <summary>
    /// Writes a baseline (sequential DCT, Huffman) JPEG with 4:4:4 chroma sampling.
    /// </summary>
    /// <param name="quality">1-100; higher is better quality and a larger file.</param>
    public static void SaveAsJpeg(Stream outputStream, int width, int height, ReadOnlySpan<byte> bgraPixels, int stride, int quality = 90)
    {
        quality = Math.Clamp(quality, 1, 100);

        // Standard quality scaling (Annex K): map quality onto a multiplier for the tables.
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);

        byte[] luminanceQuant = ScaleQuantTable(StandardLuminanceQuant, scale);
        byte[] chrominanceQuant = ScaleQuantTable(StandardChrominanceQuant, scale);

        var bits = new JpegBitWriter(outputStream);

        WriteJpegHeaders(outputStream, width, height, luminanceQuant, chrominanceQuant);

        // Per-component DC predictors carry across blocks in the scan.
        int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;

        var blockY = new double[64];
        var blockCb = new double[64];
        var blockCr = new double[64];
        var coefficients = new int[64];

        for (int blockRow = 0; blockRow < height; blockRow += 8)
        {
            for (int blockCol = 0; blockCol < width; blockCol += 8)
            {
                // Extract an 8x8 block, converting BGRA -> YCbCr and level-shifting by -128.
                for (int y = 0; y < 8; y++)
                {
                    // Clamp to the edge for blocks that overhang the image.
                    int srcY = Math.Min(blockRow + y, height - 1);
                    int srcRow = srcY * stride;

                    for (int x = 0; x < 8; x++)
                    {
                        int srcX = Math.Min(blockCol + x, width - 1);
                        int src = srcRow + (srcX * 4);

                        double b = bgraPixels[src];
                        double g = bgraPixels[src + 1];
                        double r = bgraPixels[src + 2];

                        int i = (y * 8) + x;
                        blockY[i] = (0.299 * r) + (0.587 * g) + (0.114 * b) - 128.0;
                        blockCb[i] = (-0.168736 * r) - (0.331264 * g) + (0.5 * b);
                        blockCr[i] = (0.5 * r) - (0.418688 * g) - (0.081312 * b);
                    }
                }

                prevDcY = EncodeBlock(bits, blockY, luminanceQuant, prevDcY, LuminanceDcCodes, LuminanceAcCodes, coefficients);
                prevDcCb = EncodeBlock(bits, blockCb, chrominanceQuant, prevDcCb, ChrominanceDcCodes, ChrominanceAcCodes, coefficients);
                prevDcCr = EncodeBlock(bits, blockCr, chrominanceQuant, prevDcCr, ChrominanceDcCodes, ChrominanceAcCodes, coefficients);
            }
        }

        bits.FlushWithPadding();

        // EOI
        outputStream.WriteByte(0xFF);
        outputStream.WriteByte(0xD9);
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

    #region JPEG baseline encoder

    // ITU T.81 Annex K standard tables.
    private static readonly byte[] StandardLuminanceQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68,109,103, 77,
        24, 35, 55, 64, 81,104,113, 92,
        49, 64, 78, 87,103,121,120,101,
        72, 92, 95, 98,112,100,103, 99
    };

    private static readonly byte[] StandardChrominanceQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    };

    private static readonly int[] ZigZag =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    // Standard Huffman code lengths/values (Annex K).
    private static readonly byte[] LuminanceDcBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] LuminanceDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] ChrominanceDcBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] ChrominanceDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] LuminanceAcBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] LuminanceAcValues =
    {
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
        0x22,0x71,0x14,0x32,0x81,0x91,0xa1,0x08,0x23,0x42,0xb1,0xc1,0x15,0x52,0xd1,0xf0,
        0x24,0x33,0x62,0x72,0x82,0x09,0x0a,0x16,0x17,0x18,0x19,0x1a,0x25,0x26,0x27,0x28,
        0x29,0x2a,0x34,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
        0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
        0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
        0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,
        0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,
        0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,0xe1,0xe2,
        0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    };

    private static readonly byte[] ChrominanceAcBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] ChrominanceAcValues =
    {
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
        0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xa1,0xb1,0xc1,0x09,0x23,0x33,0x52,0xf0,
        0x15,0x62,0x72,0xd1,0x0a,0x16,0x24,0x34,0xe1,0x25,0xf1,0x17,0x18,0x19,0x1a,0x26,
        0x27,0x28,0x29,0x2a,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,
        0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,
        0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x82,0x83,0x84,0x85,0x86,0x87,
        0x88,0x89,0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,
        0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,
        0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,
        0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    };

    // (code, bitLength) indexed by symbol, built once from the tables above.
    private static readonly (ushort Code, byte Length)[] LuminanceDcCodes = BuildHuffmanCodes(LuminanceDcBits, LuminanceDcValues);
    private static readonly (ushort Code, byte Length)[] LuminanceAcCodes = BuildHuffmanCodes(LuminanceAcBits, LuminanceAcValues);
    private static readonly (ushort Code, byte Length)[] ChrominanceDcCodes = BuildHuffmanCodes(ChrominanceDcBits, ChrominanceDcValues);
    private static readonly (ushort Code, byte Length)[] ChrominanceAcCodes = BuildHuffmanCodes(ChrominanceAcBits, ChrominanceAcValues);

    private static (ushort Code, byte Length)[] BuildHuffmanCodes(byte[] bits, byte[] values)
    {
        var table = new (ushort, byte)[256];
        ushort code = 0;
        int k = 0;

        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length - 1]; i++, k++)
            {
                table[values[k]] = (code, (byte)length);
                code++;
            }
            code <<= 1;
        }

        return table;
    }

    private static byte[] ScaleQuantTable(byte[] baseTable, int scale)
    {
        var scaled = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int value = (baseTable[i] * scale + 50) / 100;
            scaled[i] = (byte)Math.Clamp(value, 1, 255);
        }
        return scaled;
    }

    private static void WriteJpegHeaders(Stream stream, int width, int height, byte[] luminanceQuant, byte[] chrominanceQuant)
    {
        void Marker(byte code)
        {
            stream.WriteByte(0xFF);
            stream.WriteByte(code);
        }

        void Length(int length)
        {
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)(length & 0xFF));
        }

        // SOI
        Marker(0xD8);

        // APP0 / JFIF
        Marker(0xE0);
        Length(16);
        stream.Write(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 }); // "JFIF\0"
        stream.WriteByte(1); stream.WriteByte(1);                   // version 1.1
        stream.WriteByte(0);                                        // units: none
        stream.WriteByte(0); stream.WriteByte(1);                   // X density
        stream.WriteByte(0); stream.WriteByte(1);                   // Y density
        stream.WriteByte(0); stream.WriteByte(0);                   // no thumbnail

        // DQT (both tables, written in zig-zag order)
        void WriteQuantTable(byte id, byte[] table)
        {
            Marker(0xDB);
            Length(67);
            stream.WriteByte(id);
            for (int i = 0; i < 64; i++)
            {
                stream.WriteByte(table[ZigZag[i]]);
            }
        }

        WriteQuantTable(0, luminanceQuant);
        WriteQuantTable(1, chrominanceQuant);

        // SOF0 - baseline, 3 components, no subsampling (4:4:4)
        Marker(0xC0);
        Length(17);
        stream.WriteByte(8);                        // 8-bit precision
        stream.WriteByte((byte)(height >> 8)); stream.WriteByte((byte)(height & 0xFF));
        stream.WriteByte((byte)(width >> 8)); stream.WriteByte((byte)(width & 0xFF));
        stream.WriteByte(3);                        // component count
        stream.WriteByte(1); stream.WriteByte(0x11); stream.WriteByte(0);  // Y,  1x1, quant 0
        stream.WriteByte(2); stream.WriteByte(0x11); stream.WriteByte(1);  // Cb, 1x1, quant 1
        stream.WriteByte(3); stream.WriteByte(0x11); stream.WriteByte(1);  // Cr, 1x1, quant 1

        // DHT
        void WriteHuffmanTable(byte id, byte[] bits, byte[] values)
        {
            Marker(0xC4);
            Length(3 + 16 + values.Length);
            stream.WriteByte(id);
            stream.Write(bits, 0, 16);
            stream.Write(values, 0, values.Length);
        }

        WriteHuffmanTable(0x00, LuminanceDcBits, LuminanceDcValues);
        WriteHuffmanTable(0x10, LuminanceAcBits, LuminanceAcValues);
        WriteHuffmanTable(0x01, ChrominanceDcBits, ChrominanceDcValues);
        WriteHuffmanTable(0x11, ChrominanceAcBits, ChrominanceAcValues);

        // SOS
        Marker(0xDA);
        Length(12);
        stream.WriteByte(3);
        stream.WriteByte(1); stream.WriteByte(0x00);   // Y  -> DC 0 / AC 0
        stream.WriteByte(2); stream.WriteByte(0x11);   // Cb -> DC 1 / AC 1
        stream.WriteByte(3); stream.WriteByte(0x11);   // Cr -> DC 1 / AC 1
        stream.WriteByte(0); stream.WriteByte(63); stream.WriteByte(0);
    }

    /// <summary>
    /// Forward DCT, quantize, then Huffman-encode one 8x8 block. Returns the DC value to
    /// use as the predictor for the next block of this component.
    /// </summary>
    private static int EncodeBlock(
        JpegBitWriter bits,
        double[] samples,
        byte[] quantTable,
        int previousDc,
        (ushort Code, byte Length)[] dcCodes,
        (ushort Code, byte Length)[] acCodes,
        int[] coefficients)
    {
        ForwardDct(samples);

        for (int i = 0; i < 64; i++)
        {
            int zz = ZigZag[i];
            double quantized = samples[zz] / quantTable[zz];
            coefficients[i] = (int)Math.Round(quantized);
        }

        // DC: encode the difference from the previous block.
        int dc = coefficients[0];
        int diff = dc - previousDc;
        WriteCoefficient(bits, dcCodes, diff, isDc: true);

        // AC: run-length of zeros + magnitude category.
        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            if (coefficients[i] == 0)
            {
                zeroRun++;
                continue;
            }

            // ZRL (16 zeros) for runs longer than 15.
            while (zeroRun > 15)
            {
                bits.WriteCode(acCodes[0xF0]);
                zeroRun -= 16;
            }

            int magnitude = MagnitudeCategory(coefficients[i]);
            bits.WriteCode(acCodes[(zeroRun << 4) | magnitude]);
            bits.WriteValueBits(coefficients[i], magnitude);
            zeroRun = 0;
        }

        // EOB when the block ends in zeros.
        if (zeroRun > 0)
        {
            bits.WriteCode(acCodes[0x00]);
        }

        return dc;
    }

    private static void WriteCoefficient(JpegBitWriter bits, (ushort Code, byte Length)[] codes, int value, bool isDc)
    {
        int category = MagnitudeCategory(value);
        bits.WriteCode(codes[category]);
        if (category > 0)
        {
            bits.WriteValueBits(value, category);
        }
    }

    /// <summary>Number of bits needed to represent the magnitude (JPEG "SSSS" category).</summary>
    private static int MagnitudeCategory(int value)
    {
        int magnitude = Math.Abs(value);
        int category = 0;
        while (magnitude > 0)
        {
            magnitude >>= 1;
            category++;
        }
        return category;
    }

    /// <summary>
    /// Separable 2-D forward DCT-II, applied to rows then columns, with the standard 1/4
    /// normalization folded in.
    /// </summary>
    private static void ForwardDct(double[] block)
    {
        double[] cos = CosineTable;
        double[] temp = DctScratch;

        // Rows
        for (int y = 0; y < 8; y++)
        {
            int rowBase = y * 8;
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                int cosBase = u * 8;
                for (int x = 0; x < 8; x++)
                {
                    sum += block[rowBase + x] * cos[cosBase + x];
                }
                temp[rowBase + u] = u == 0 ? sum * Sqrt1Over2 : sum;
            }
        }

        // Columns
        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                double sum = 0;
                int cosBase = v * 8;
                for (int y = 0; y < 8; y++)
                {
                    sum += temp[y * 8 + u] * cos[cosBase + y];
                }
                block[v * 8 + u] = (v == 0 ? sum * Sqrt1Over2 : sum) * 0.25;
            }
        }
    }

    private static readonly double Sqrt1Over2 = Math.Sqrt(0.5);

    /// <summary>
    /// cos((2x+1) * u * pi / 16), computed ONCE for the process rather than per block.
    /// Rebuilding it per block made the encoder dominated by 4096 Math.Cos calls per 8x8
    /// block instead of the DCT itself.
    /// </summary>
    private static readonly double[] CosineTable = BuildCosineTable();

    private static double[] BuildCosineTable()
    {
        var table = new double[64];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
            }
        }
        return table;
    }

    /// <summary>Reused row-pass scratch buffer; avoids a 64-double allocation per block.</summary>
    [ThreadStatic]
    private static double[]? _dctScratch;
    private static double[] DctScratch => _dctScratch ??= new double[64];

    /// <summary>
    /// MSB-first bit writer that byte-stuffs 0x00 after every 0xFF, as JPEG entropy-coded
    /// data requires so real markers stay unambiguous.
    /// </summary>
    private sealed class JpegBitWriter
    {
        private readonly Stream _stream;
        private int _buffer;
        private int _bitCount;

        public JpegBitWriter(Stream stream) => _stream = stream;

        public void WriteCode((ushort Code, byte Length) code)
        {
            if (code.Length == 0)
            {
                throw new InvalidOperationException("Attempted to write an undefined Huffman symbol.");
            }
            WriteBits(code.Code, code.Length);
        }

        /// <summary>
        /// Writes the JPEG magnitude bits for a coefficient: positive values directly,
        /// negative values as (value - 1) in two's-complement over the category width.
        /// </summary>
        public void WriteValueBits(int value, int category)
        {
            int encoded = value >= 0 ? value : value - 1;
            WriteBits(encoded & ((1 << category) - 1), category);
        }

        private void WriteBits(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                _bitCount++;

                if (_bitCount == 8)
                {
                    EmitByte((byte)_buffer);
                    _buffer = 0;
                    _bitCount = 0;
                }
            }
        }

        private void EmitByte(byte value)
        {
            _stream.WriteByte(value);
            if (value == 0xFF)
            {
                _stream.WriteByte(0x00);   // byte stuffing
            }
        }

        /// <summary>Pads the final partial byte with 1 bits, per the specification.</summary>
        public void FlushWithPadding()
        {
            while (_bitCount > 0)
            {
                _buffer = (_buffer << 1) | 1;
                _bitCount++;
                if (_bitCount == 8)
                {
                    EmitByte((byte)_buffer);
                    _buffer = 0;
                    _bitCount = 0;
                }
            }
        }
    }

    #endregion
}
