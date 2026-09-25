using System;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// Predictor post-processor for Flate and LZW streams complying with ISO 32000-2 Clause 7.4.4.4.
/// Supports TIFF Predictor 2 and PNG Predictors 10-15 (None, Sub, Up, Average, Paeth).
/// </summary>
/// <remarks>
/// Hostile-input rules: row geometry is computed with 64-bit checked arithmetic, row buffers are never
/// larger than the input itself, and a short final row is decoded as far as data allows (truncated)
/// instead of raising <see cref="IndexOutOfRangeException"/>.
/// </remarks>
public static class PredictorDecoder
{
    public static byte[] DecodePredictor(
        ReadOnlySpan<byte> input,
        int predictor,
        int columns,
        int colors = 1,
        int bitsPerComponent = 8)
    {
        if (predictor <= 1 || input.IsEmpty)
            return input.ToArray();

        // Out-of-range parameters fall back to their defaults (Table 8: Colors >= 1, BPC in {1,2,4,8,16}).
        if (columns <= 0) columns = 1;
        if (colors <= 0) colors = 1;
        if (bitsPerComponent is not (1 or 2 or 4 or 8 or 16)) bitsPerComponent = 8;

        long bitsPerPixel = (long)colors * bitsPerComponent;
        int bytesPerPixel = (int)Math.Max(1, Math.Min((bitsPerPixel + 7) / 8, int.MaxValue));
        long bytesPerRowLong;
        try
        {
            bytesPerRowLong = checked(((long)columns * bitsPerPixel + 7) / 8);
        }
        catch (OverflowException)
        {
            bytesPerRowLong = long.MaxValue;
        }

        if (predictor == 2)
            return DecodeTiff(input, bytesPerRowLong, colors, bitsPerComponent);

        if (predictor >= 10 && predictor <= 15)
            return DecodePng(input, bytesPerRowLong, bytesPerPixel);

        return input.ToArray();
    }

    private static byte[] DecodePng(ReadOnlySpan<byte> input, long bytesPerRowLong, int bytesPerPixel)
    {
        // A row can never be longer than the data that is actually present.
        int bytesPerRow = (int)Math.Min(bytesPerRowLong, input.Length - 1L);
        if (bytesPerRow <= 0)
            return Array.Empty<byte>();

        int rowLengthWithTag = bytesPerRow + 1;
        int fullRows = input.Length / rowLengthWithTag;
        int tail = input.Length % rowLengthWithTag;
        int partialBytes = tail > 1 ? tail - 1 : 0;
        byte[] output = new byte[(long)fullRows * bytesPerRow + partialBytes];

        byte[] priorRow = new byte[bytesPerRow];
        byte[] currentRow = new byte[bytesPerRow];
        int rowCount = fullRows + (partialBytes > 0 ? 1 : 0);

        for (int r = 0; r < rowCount; r++)
        {
            int inOffset = r * rowLengthWithTag;
            byte filterType = input[inOffset];
            int rowBytes = Math.Min(bytesPerRow, input.Length - inOffset - 1);
            ReadOnlySpan<byte> rowData = input.Slice(inOffset + 1, rowBytes);

            switch (filterType)
            {
                case 1: // Sub
                    for (int i = 0; i < rowBytes; i++)
                    {
                        byte left = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : (byte)0;
                        currentRow[i] = (byte)(rowData[i] + left);
                    }
                    break;

                case 2: // Up
                    for (int i = 0; i < rowBytes; i++)
                        currentRow[i] = (byte)(rowData[i] + priorRow[i]);
                    break;

                case 3: // Average
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int left = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : 0;
                        currentRow[i] = (byte)(rowData[i] + ((left + priorRow[i]) >> 1));
                    }
                    break;

                case 4: // Paeth
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : 0;
                        int b = priorRow[i];
                        int c = i >= bytesPerPixel ? priorRow[i - bytesPerPixel] : 0;
                        currentRow[i] = (byte)(rowData[i] + PaethPredictor(a, b, c));
                    }
                    break;

                default: // 0 = None; unknown tags are copied raw.
                    rowData.CopyTo(currentRow);
                    break;
            }

            Array.Copy(currentRow, 0, output, (long)r * bytesPerRow, rowBytes);
            (priorRow, currentRow) = (currentRow, priorRow);
        }

        return output;
    }

    private static byte[] DecodeTiff(ReadOnlySpan<byte> input, long bytesPerRowLong, int colors, int bitsPerComponent)
    {
        byte[] output = input.ToArray();
        int bytesPerRow = (int)Math.Min(bytesPerRowLong, output.Length);
        if (bytesPerRow <= 0)
            return output;

        for (int rowStart = 0; rowStart < output.Length; rowStart += bytesPerRow)
        {
            int rowBytes = Math.Min(bytesPerRow, output.Length - rowStart);
            var row = output.AsSpan(rowStart, rowBytes);

            switch (bitsPerComponent)
            {
                case 8:
                    for (int i = colors; i < rowBytes; i++)
                        row[i] = (byte)(row[i] + row[i - colors]);
                    break;

                case 16:
                    {
                        int step = colors * 2;
                        for (int i = step; i + 1 < rowBytes; i += 2)
                        {
                            int value = ((row[i] << 8) | row[i + 1]) + ((row[i - step] << 8) | row[i - step + 1]);
                            row[i] = (byte)(value >> 8);
                            row[i + 1] = (byte)value;
                        }
                        break;
                    }

                default: // 1, 2 or 4 bits per component: difference packed samples within the row.
                    {
                        int mask = (1 << bitsPerComponent) - 1;
                        long samples = (long)rowBytes * 8 / bitsPerComponent;
                        for (long s = colors; s < samples; s++)
                        {
                            int value = (ReadSample(row, s, bitsPerComponent) + ReadSample(row, s - colors, bitsPerComponent)) & mask;
                            WriteSample(row, s, bitsPerComponent, value);
                        }
                        break;
                    }
            }
        }

        return output;
    }

    private static int ReadSample(Span<byte> row, long index, int bits)
    {
        long bitPos = index * bits;
        int shift = 8 - bits - (int)(bitPos & 7);
        return (row[(int)(bitPos >> 3)] >> shift) & ((1 << bits) - 1);
    }

    private static void WriteSample(Span<byte> row, long index, int bits, int value)
    {
        long bitPos = index * bits;
        int shift = 8 - bits - (int)(bitPos & 7);
        int mask = ((1 << bits) - 1) << shift;
        ref byte target = ref row[(int)(bitPos >> 3)];
        target = (byte)((target & ~mask) | ((value << shift) & mask));
    }

    private static byte PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
            return (byte)a;
        if (pb <= pc)
            return (byte)b;
        return (byte)c;
    }
}
