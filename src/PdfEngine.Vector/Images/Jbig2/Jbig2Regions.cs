using System;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Images.Jbig2;

/// <summary>Generic region (6.2) and generic refinement region (6.3) decoding procedures.</summary>
internal static class Jbig2Regions
{
    /// <summary>Contexts per template: 2^16, 2^13, 2^10, 2^10.</summary>
    public static byte[] NewGenericContexts(int template) => new byte[template == 0 ? 1 << 16 : template == 1 ? 1 << 13 : 1 << 10];

    public static byte[] NewRefinementContexts(int template) => new byte[template == 0 ? 1 << 13 : 1 << 10];

    /// <summary>Default adaptive-template pixels (6.2.5.3 / 7.4.6.3).</summary>
    public static (sbyte X, sbyte Y)[] DefaultAt(int template) => template == 0
        ? new (sbyte, sbyte)[] { (3, -1), (-3, -1), (2, -2), (-2, -2) }
        : new (sbyte, sbyte)[] { (template == 1 ? (sbyte)3 : (sbyte)2, -1) };

    /// <summary>
    /// Arithmetic generic region decoding (6.2.5.7) with the context bit layout of 6.2.5.3, typical
    /// prediction (TPGDON) and the optional skip bitmap.
    /// </summary>
    public static Jbig2Bitmap DecodeGeneric(MqDecoder mq, byte[] contexts, int width, int height, int template,
        (sbyte X, sbyte Y)[] at, bool tpgdon, Jbig2Bitmap? skip, System.Threading.CancellationToken ct)
    {
        var b = new Jbig2Bitmap(width, height);
        int sltpContext = template switch { 0 => 0x9B25, 1 => 0x0795, 2 => 0x00E5, _ => 0x0195 };
        bool ltp = false;
        for (int y = 0; y < height; y++)
        {
            if ((y & 31) == 0) ct.ThrowIfCancellationRequested();
            if (tpgdon)
            {
                ltp ^= mq.Decode(contexts, sltpContext) == 1;
                if (ltp)
                {
                    if (y > 0) Array.Copy(b.Px, (y - 1) * width, b.Px, y * width, width);
                    continue;
                }
            }
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (skip != null && skip.Get(x, y) != 0)
                    continue;
                int cx = template switch
                {
                    0 => b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2 | b.Get(x - 4, y) << 3
                         | b.Get(x + at[0].X, y + at[0].Y) << 4
                         | b.Get(x + 2, y - 1) << 5 | b.Get(x + 1, y - 1) << 6 | b.Get(x, y - 1) << 7 | b.Get(x - 1, y - 1) << 8 | b.Get(x - 2, y - 1) << 9
                         | b.Get(x + at[1].X, y + at[1].Y) << 10 | b.Get(x + at[2].X, y + at[2].Y) << 11
                         | b.Get(x + 1, y - 2) << 12 | b.Get(x, y - 2) << 13 | b.Get(x - 1, y - 2) << 14
                         | b.Get(x + at[3].X, y + at[3].Y) << 15,
                    1 => b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2
                         | b.Get(x + at[0].X, y + at[0].Y) << 3
                         | b.Get(x + 2, y - 1) << 4 | b.Get(x + 1, y - 1) << 5 | b.Get(x, y - 1) << 6 | b.Get(x - 1, y - 1) << 7 | b.Get(x - 2, y - 1) << 8
                         | b.Get(x + 2, y - 2) << 9 | b.Get(x + 1, y - 2) << 10 | b.Get(x, y - 2) << 11 | b.Get(x - 1, y - 2) << 12,
                    2 => b.Get(x - 1, y) | b.Get(x - 2, y) << 1
                         | b.Get(x + at[0].X, y + at[0].Y) << 2
                         | b.Get(x + 1, y - 1) << 3 | b.Get(x, y - 1) << 4 | b.Get(x - 1, y - 1) << 5 | b.Get(x - 2, y - 1) << 6
                         | b.Get(x + 1, y - 2) << 7 | b.Get(x, y - 2) << 8 | b.Get(x - 1, y - 2) << 9,
                    _ => b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2 | b.Get(x - 4, y) << 3
                         | b.Get(x + at[0].X, y + at[0].Y) << 4
                         | b.Get(x + 1, y - 1) << 5 | b.Get(x, y - 1) << 6 | b.Get(x - 1, y - 1) << 7 | b.Get(x - 2, y - 1) << 8 | b.Get(x - 3, y - 1) << 9,
                };
                b.Px[row + x] = (byte)mq.Decode(contexts, cx);
            }
        }
        return b;
    }

    /// <summary>MMR generic region: T.6 coded rows (1 = black); <paramref name="end"/> is updated to the byte after the data.</summary>
    public static Jbig2Bitmap DecodeMmr(byte[] data, int start, int end, int width, int height, out int consumedTo, System.Threading.CancellationToken ct)
    {
        var b = new Jbig2Bitmap(width, height);
        if (width == 0 || height == 0)
        {
            consumedTo = start;
            return b;
        }
        var packed = CcittFaxDecoder.Decode(data, start, end,
            new CcittFaxDecoder.Parameters(K: -1, Columns: width, Rows: height, EndOfBlock: true, BlackIs1: true), height, out consumedTo, ct);
        int rowBytes = (width + 7) / 8;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                b.Px[y * width + x] = (byte)((packed[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1);
        return b;
    }

    /// <summary>
    /// Generic refinement region decoding (6.3.5.6) against <paramref name="reference"/> placed at
    /// (<paramref name="dx"/>, <paramref name="dy"/>). Typical prediction (TPGRON) is supported.
    /// </summary>
    public static Jbig2Bitmap DecodeRefinement(MqDecoder mq, byte[] contexts, int width, int height, int template,
        Jbig2Bitmap reference, int dx, int dy, (sbyte X, sbyte Y)[] at, bool tpgron, System.Threading.CancellationToken ct)
    {
        var b = new Jbig2Bitmap(width, height);
        int Ctx(int x, int y)
        {
            int rx = x - dx, ry = y - dy;
            return template == 0
                ? b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x + at[0].X, y + at[0].Y) << 3
                  | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx - 1, ry + 1) << 6
                  | reference.Get(rx + 1, ry) << 7 | reference.Get(rx, ry) << 8 | reference.Get(rx - 1, ry) << 9
                  | reference.Get(rx + 1, ry - 1) << 10 | reference.Get(rx, ry - 1) << 11
                  | reference.Get(rx + at[1].X, ry + at[1].Y) << 12
                : b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x - 1, y - 1) << 3
                  | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5
                  | reference.Get(rx + 1, ry) << 6 | reference.Get(rx, ry) << 7 | reference.Get(rx - 1, ry) << 8
                  | reference.Get(rx, ry - 1) << 9;
        }
        // SLTP context of 6.3.5.6 expressed in the bit layout above (the layout and these values
        // match jbig2dec, which passes the T.88 conformance streams).
        int sltpContext = template == 0 ? 0x100 : 0x80;
        bool ltp = false;
        for (int y = 0; y < height; y++)
        {
            if ((y & 31) == 0) ct.ThrowIfCancellationRequested();
            if (tpgron)
                ltp ^= mq.Decode(contexts, sltpContext) == 1;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (ltp)
                {
                    // Typical prediction: a pixel whose 3 × 3 reference neighbourhood is uniform copies it.
                    int rx = x - dx, ry = y - dy, v = reference.Get(rx, ry);
                    bool uniform = true;
                    for (int j = -1; j <= 1 && uniform; j++)
                        for (int i = -1; i <= 1 && uniform; i++)
                            uniform = reference.Get(rx + i, ry + j) == v;
                    if (uniform)
                    {
                        b.Px[row + x] = (byte)v;
                        continue;
                    }
                }
                b.Px[row + x] = (byte)mq.Decode(contexts, Ctx(x, y));
            }
        }
        return b;
    }
}
