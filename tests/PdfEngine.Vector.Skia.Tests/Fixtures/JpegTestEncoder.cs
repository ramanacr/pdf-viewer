namespace PdfEngine.Vector.Skia.Tests.Fixtures;

/// <summary>
/// Writes JPEG files of one to four components for decoder tests (ITU T.81, Huffman coding with
/// the Annex K tables): baseline or progressive, with any sampling factors, a restart interval and
/// an optional Adobe APP14 marker. The progressive scans are spectral selection with DC successive
/// approximation (the DC first scan at Al = 1, then its refinement bits); AC refinement is not written.
/// </summary>
internal static class JpegTestEncoder
{
    public sealed record Options
    {
        /// <summary>Per component (h, v); 1 × 1 for every component when null.</summary>
        public (int H, int V)[]? Sampling { get; init; }
        public bool Progressive { get; init; }
        public int RestartInterval { get; init; }
        /// <summary>Adobe APP14 colour transform (0 none, 1 YCbCr, 2 YCCK); null writes no APP14 marker.</summary>
        public int? AdobeTransform { get; init; }
        public int Quality { get; init; } = 95;
    }

    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    private static readonly byte[] Quant =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] DcBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] AcBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] AcValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };

    private static readonly (int Code, int Length)[] DcCodes = Codes(DcBits, DcValues);
    private static readonly (int Code, int Length)[] AcCodes = Codes(AcBits, AcValues);

    private sealed class Plane
    {
        public int H, V, BlocksPerLine, BlocksPerColumn, Stride, Rows;
        public int[][] Blocks = Array.Empty<int[]>(); // natural order, quantized
        public int Pred;
    }

    /// <summary>Encodes interleaved samples, <paramref name="components"/> per pixel, written as they are (no colour transform).</summary>
    public static byte[] Encode(byte[] samples, int width, int height, int components, Options? options = null)
    {
        options ??= new Options();
        var sampling = options.Sampling ?? Enumerable.Repeat((1, 1), components).ToArray();
        int maxH = sampling.Max(s => s.H), maxV = sampling.Max(s => s.V);
        int mcusPerLine = (width + 8 * maxH - 1) / (8 * maxH), mcusPerColumn = (height + 8 * maxV - 1) / (8 * maxV);
        int scale = options.Quality < 50 ? 5000 / options.Quality : 200 - options.Quality * 2;
        var quant = Quant.Select(q => Math.Clamp((q * scale + 50) / 100, 1, 255)).ToArray();

        var planes = new Plane[components];
        for (int c = 0; c < components; c++)
        {
            var (h, v) = sampling[c];
            var p = planes[c] = new Plane
            {
                H = h, V = v,
                BlocksPerLine = ((width * h + maxH - 1) / maxH + 7) / 8,
                BlocksPerColumn = ((height * v + maxV - 1) / maxV + 7) / 8,
                Stride = mcusPerLine * h,
                Rows = mcusPerColumn * v,
            };
            int cw = (width * h + maxH - 1) / maxH, ch = (height * v + maxV - 1) / maxV;
            int fx = maxH / h, fy = maxV / v;
            // The component at its own resolution: box averages, edges repeated into the padding.
            double Sample(int x, int y)
            {
                x = Math.Min(x, cw - 1);
                y = Math.Min(y, ch - 1);
                double sum = 0;
                int n = 0;
                for (int dy = 0; dy < fy; dy++)
                    for (int dx = 0; dx < fx; dx++)
                    {
                        int sx = Math.Min(x * fx + dx, width - 1), sy = Math.Min(y * fy + dy, height - 1);
                        sum += samples[(sy * width + sx) * components + c];
                        n++;
                    }
                return sum / n;
            }
            p.Blocks = new int[p.Stride * p.Rows][];
            var block = new double[64];
            for (int by = 0; by < p.Rows; by++)
                for (int bx = 0; bx < p.Stride; bx++)
                {
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            block[y * 8 + x] = Sample(bx * 8 + x, by * 8 + y) - 128;
                    ForwardDct(block);
                    var q = new int[64];
                    for (int i = 0; i < 64; i++) q[i] = (int)Math.Round(block[i] / quant[i]);
                    p.Blocks[by * p.Stride + bx] = q;
                }
        }

        using var ms = new MemoryStream();
        void Marker(int m) { ms.WriteByte(0xFF); ms.WriteByte((byte)m); }
        void Segment(int m, byte[] body)
        {
            Marker(m);
            ms.WriteByte((byte)((body.Length + 2) >> 8));
            ms.WriteByte((byte)(body.Length + 2));
            ms.Write(body);
        }

        Marker(0xD8);
        if (options.AdobeTransform is int transform)
            Segment(0xEE, new byte[] { (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, (byte)transform });
        Segment(0xDB, new byte[] { 0 }.Concat(Enumerable.Range(0, 64).Select(k => (byte)quant[ZigZag[k]])).ToArray());
        var sof = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components };
        for (int c = 0; c < components; c++) sof.AddRange(new[] { (byte)(c + 1), (byte)(sampling[c].H << 4 | sampling[c].V), (byte)0 });
        Segment(options.Progressive ? 0xC2 : 0xC0, sof.ToArray());
        Segment(0xC4, new byte[] { 0x00 }.Concat(DcBits).Concat(DcValues).ToArray());
        Segment(0xC4, new byte[] { 0x10 }.Concat(AcBits).Concat(AcValues).ToArray());
        if (options.RestartInterval > 0)
            Segment(0xDD, new[] { (byte)(options.RestartInterval >> 8), (byte)options.RestartInterval });

        var all = Enumerable.Range(0, components).ToArray();
        if (!options.Progressive)
        {
            Scan(all, 0, 63, 0, 0);
        }
        else
        {
            Scan(all, 0, 0, 0, 1);
            for (int c = 0; c < components; c++) Scan(new[] { c }, 1, 5, 0, 0);
            for (int c = 0; c < components; c++) Scan(new[] { c }, 6, 63, 0, 0);
            Scan(all, 0, 0, 1, 0);
        }
        Marker(0xD9);
        return ms.ToArray();

        void Scan(int[] comps, int ss, int se, int ah, int al)
        {
            var sos = new List<byte> { (byte)comps.Length };
            foreach (int c in comps) sos.AddRange(new[] { (byte)(c + 1), (byte)0x00 });
            sos.AddRange(new[] { (byte)ss, (byte)se, (byte)(ah << 4 | al) });
            Segment(0xDA, sos.ToArray());
            var bits = new BitWriter(ms);
            foreach (var p in planes) p.Pred = 0;
            int mcus = comps.Length == 1 ? planes[comps[0]].BlocksPerLine * planes[comps[0]].BlocksPerColumn : mcusPerLine * mcusPerColumn;
            int restarts = 0;
            for (int mcu = 0; mcu < mcus; mcu++)
            {
                if (options.RestartInterval > 0 && mcu > 0 && mcu % options.RestartInterval == 0)
                {
                    bits.Flush();
                    Marker(0xD0 + restarts++ % 8);
                    foreach (var p in planes) p.Pred = 0;
                }
                if (comps.Length == 1)
                {
                    var p = planes[comps[0]];
                    Block(bits, p, p.Blocks[mcu / p.BlocksPerLine * p.Stride + mcu % p.BlocksPerLine], ss, se, ah, al);
                    continue;
                }
                int row = mcu / mcusPerLine, col = mcu % mcusPerLine;
                foreach (int c in comps)
                {
                    var p = planes[c];
                    for (int v = 0; v < p.V; v++)
                        for (int h = 0; h < p.H; h++)
                            Block(bits, p, p.Blocks[(row * p.V + v) * p.Stride + col * p.H + h], ss, se, ah, al);
                }
            }
            bits.Flush();
        }
    }

    private static void Block(BitWriter bits, Plane p, int[] coef, int ss, int se, int ah, int al)
    {
        if (ss == 0)
        {
            if (ah > 0)
            {
                bits.Write((coef[0] >> al) & 1, 1); // DC refinement: the next bit down
                return;
            }
            int dc = coef[0] >> al;
            int diff = dc - p.Pred;
            p.Pred = dc;
            int cat = Category(diff);
            bits.Write(DcCodes[cat]);
            if (cat > 0) bits.Value(diff, cat);
            if (se == 0) return;
            ss = 1;
        }
        int run = 0;
        for (int k = ss; k <= se; k++)
        {
            int value = coef[ZigZag[k]];
            if (value == 0) { run++; continue; }
            while (run > 15) { bits.Write(AcCodes[0xF0]); run -= 16; }
            int cat = Category(value);
            bits.Write(AcCodes[run << 4 | cat]);
            bits.Value(value, cat);
            run = 0;
        }
        if (run > 0) bits.Write(AcCodes[0x00]);
    }

    private static int Category(int value)
    {
        int m = Math.Abs(value), c = 0;
        while (m > 0) { m >>= 1; c++; }
        return c;
    }

    private static (int, int)[] Codes(byte[] bits, byte[] values)
    {
        var table = new (int, int)[256];
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++) table[values[k++]] = (code++, len);
            code <<= 1;
        }
        return table;
    }

    private static void ForwardDct(double[] block)
    {
        var tmp = new double[64];
        for (int y = 0; y < 8; y++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int x = 0; x < 8; x++) s += block[y * 8 + x] * Math.Cos((2 * x + 1) * u * Math.PI / 16);
                tmp[y * 8 + u] = s * (u == 0 ? Math.Sqrt(0.5) : 1) / 2;
            }
        for (int u = 0; u < 8; u++)
            for (int v = 0; v < 8; v++)
            {
                double s = 0;
                for (int y = 0; y < 8; y++) s += tmp[y * 8 + u] * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                block[v * 8 + u] = s * (v == 0 ? Math.Sqrt(0.5) : 1) / 2;
            }
    }

    private sealed class BitWriter(Stream stream)
    {
        private int _buffer, _count;

        public void Write((int Code, int Length) code)
        {
            if (code.Length == 0) throw new InvalidOperationException("No Huffman code for the symbol.");
            Write(code.Code, code.Length);
        }

        public void Value(int value, int category) => Write((value >= 0 ? value : value - 1) & ((1 << category) - 1), category);

        public void Write(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                if (++_count == 8) Emit();
            }
        }

        /// <summary>Pads the last byte with ones, as T.81 asks before a marker.</summary>
        public void Flush()
        {
            while (_count != 0) Write(1, 1);
        }

        private void Emit()
        {
            stream.WriteByte((byte)_buffer);
            if (_buffer == 0xFF) stream.WriteByte(0);
            _buffer = _count = 0;
        }
    }
}
