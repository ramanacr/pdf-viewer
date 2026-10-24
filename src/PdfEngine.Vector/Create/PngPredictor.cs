using System;
using System.IO;

namespace PdfEngine.Vector.Create;

/// <summary>
/// PNG prediction for Flate-compressed samples (ISO 32000-2 7.4.4.4, /Predictor 15): each row
/// gets the filter (None, Sub, Up, Average or Paeth) whose output has the smallest sum of
/// absolute values, the heuristic libpng uses. Photographs and screenshots compress far better
/// predicted than raw.
/// </summary>
internal static class PngPredictor
{
    public static void Encode(ReadOnlySpan<byte> samples, int width, int colors, int bitsPerComponent, Stream output)
    {
        int bpp = Math.Max(1, colors * bitsPerComponent / 8);
        int rowBytes = (int)(((long)width * colors * bitsPerComponent + 7) / 8);
        if (rowBytes == 0) return;
        int rows = samples.Length / rowBytes;
        var prior = new byte[rowBytes];
        var candidates = new byte[5][];
        for (int f = 0; f < 5; f++) candidates[f] = new byte[rowBytes + 1];
        for (int y = 0; y < rows; y++)
        {
            var row = samples.Slice(y * rowBytes, rowBytes);
            int best = 0;
            long bestSum = long.MaxValue;
            for (int f = 0; f < 5; f++)
            {
                var c = candidates[f];
                c[0] = (byte)f;
                long sum = 0;
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], cc = i >= bpp ? prior[i - bpp] : 0;
                    int predicted = f switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, cc),
                        _ => 0,
                    };
                    byte v = (byte)(row[i] - predicted);
                    c[i + 1] = v;
                    sum += v < 128 ? v : 256 - v;
                    if (sum >= bestSum) break; // already worse than the best so far
                }
                if (sum < bestSum)
                {
                    bestSum = sum;
                    best = f;
                }
            }
            // The winner may have stopped early only if it was not the winner, so its row is whole.
            output.Write(candidates[best]);
            row.CopyTo(prior);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
