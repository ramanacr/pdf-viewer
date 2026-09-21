using System;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// Predictor post-processor for Flate and LZW streams complying with ISO 32000-2 Clause 7.4.4.4.
/// Supports TIFF Predictor 2 and PNG Predictors 10-15 (None, Sub, Up, Average, Paeth).
/// </summary>
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

        if (columns <= 0) columns = 1;
        if (colors <= 0) colors = 1;
        if (bitsPerComponent <= 0) bitsPerComponent = 8;

        int bytesPerPixel = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
        int bytesPerRow = (columns * colors * bitsPerComponent + 7) / 8;

        if (predictor == 2)
        {
            // TIFF Predictor 2 (horizontal differencing)
            byte[] output = input.ToArray();
            int rows = output.Length / bytesPerRow;
            for (int r = 0; r < rows; r++)
            {
                int rowStart = r * bytesPerRow;
                for (int i = bytesPerPixel; i < bytesPerRow; i++)
                {
                    output[rowStart + i] = (byte)(output[rowStart + i] + output[rowStart + i - bytesPerPixel]);
                }
            }
            return output;
        }

        if (predictor >= 10 && predictor <= 15)
        {
            // PNG Predictors: each row is prefixed by 1 filter type byte
            int rowLengthWithTag = bytesPerRow + 1;
            int rowCount = input.Length / rowLengthWithTag;
            byte[] output = new byte[rowCount * bytesPerRow];

            byte[] priorRow = new byte[bytesPerRow];
            byte[] currentRow = new byte[bytesPerRow];

            for (int r = 0; r < rowCount; r++)
            {
                int inOffset = r * rowLengthWithTag;
                byte filterType = input[inOffset];
                ReadOnlySpan<byte> rowData = input.Slice(inOffset + 1, bytesPerRow);

                switch (filterType)
                {
                    case 0: // None
                        rowData.CopyTo(currentRow);
                        break;

                    case 1: // Sub
                        for (int i = 0; i < bytesPerRow; i++)
                        {
                            byte left = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : (byte)0;
                            currentRow[i] = (byte)(rowData[i] + left);
                        }
                        break;

                    case 2: // Up
                        for (int i = 0; i < bytesPerRow; i++)
                        {
                            currentRow[i] = (byte)(rowData[i] + priorRow[i]);
                        }
                        break;

                    case 3: // Average
                        for (int i = 0; i < bytesPerRow; i++)
                        {
                            int left = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : 0;
                            int up = priorRow[i];
                            currentRow[i] = (byte)(rowData[i] + ((left + up) >> 1));
                        }
                        break;

                    case 4: // Paeth
                        for (int i = 0; i < bytesPerRow; i++)
                        {
                            int a = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : 0;
                            int b = priorRow[i];
                            int c = i >= bytesPerPixel ? priorRow[i - bytesPerPixel] : 0;
                            currentRow[i] = (byte)(rowData[i] + PaethPredictor(a, b, c));
                        }
                        break;

                    default: // Fallback to raw copy if unknown filter
                        rowData.CopyTo(currentRow);
                        break;
                }

                Array.Copy(currentRow, 0, output, r * bytesPerRow, bytesPerRow);
                Array.Copy(currentRow, priorRow, bytesPerRow);
            }

            return output;
        }

        return input.ToArray();
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
