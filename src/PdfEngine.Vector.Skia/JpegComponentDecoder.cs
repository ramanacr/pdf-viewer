namespace PdfEngine.Vector.Skia;

/// <summary>
/// A managed JPEG decoder that returns the components themselves (ITU T.81: Huffman-coded
/// baseline, extended and progressive 8-bit frames). Skia's codec converts a four-component JPEG
/// to RGB by itself and gives no raw CMYK samples, so the Skia backend decodes CMYK and YCCK JPEGs
/// here and maps the samples through the core's colour conversion, as the Direct2D backend does
/// with the CMYK samples WIC returns.
/// </summary>
/// <remarks>
/// Samples come back as stored, as libjpeg returns them (no Adobe inversion is undone here). YCCK
/// (Adobe transform 2) is converted to CMYK as libjpeg does it (C = 255 − R of the YCbCr triple, K
/// as stored). Subsampled components are enlarged by replication.
/// </remarks>
internal static class JpegComponentDecoder
{
    /// <summary>Largest image decoded (pixels), so hostile data cannot ask for unbounded memory.</summary>
    private const long MaxPixels = 100_000_000;

    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    private sealed class Huffman
    {
        // Canonical decoding: per code length, the largest code and where its symbols start.
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[18];
        public byte[] Symbols = Array.Empty<byte>();

        public static Huffman Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            var h = new Huffman { Symbols = symbols.ToArray() };
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                int n = counts[len - 1];
                h.ValOffset[len] = k - code;
                code += n;
                k += n;
                h.MaxCode[len] = n > 0 ? code - 1 : -1;
                code <<= 1;
            }
            h.MaxCode[17] = int.MaxValue;
            return h;
        }
    }

    private sealed class Component
    {
        public int Id, H, V, Tq;
        public int BlocksPerLine, BlocksPerColumn; // blocks that hold image data
        public int Stride, Rows;                   // blocks per line and column padded to whole MCUs
        public short[] Coefficients = Array.Empty<short>();
        public Huffman? Dc, Ac;
        public int Pred;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        public int Position;
        private int _buffer, _bits;
        public bool HitMarker;

        public BitReader(ReadOnlySpan<byte> data, int position)
        {
            _data = data;
            Position = position;
            _buffer = _bits = 0;
            HitMarker = false;
        }

        public int ReadBit()
        {
            if (_bits == 0)
            {
                if (HitMarker || Position >= _data.Length)
                {
                    // Past the end of the entropy-coded data: zeros, as libjpeg does for a truncated scan.
                    HitMarker = true;
                    return 0;
                }
                int b = _data[Position];
                if (b == 0xFF)
                {
                    int next = Position + 1 < _data.Length ? _data[Position + 1] : 0xD9;
                    if (next == 0x00)
                    {
                        Position += 2;
                    }
                    else
                    {
                        HitMarker = true; // a marker: the scan's data ends here
                        return 0;
                    }
                }
                else
                {
                    Position++;
                }
                _buffer = b;
                _bits = 8;
            }
            _bits--;
            return (_buffer >> _bits) & 1;
        }

        public int Receive(int length)
        {
            int n = 0;
            while (length-- > 0) n = (n << 1) | ReadBit();
            return n;
        }

        public int ReceiveExtend(int length)
        {
            if (length == 0) return 0;
            if (length == 1) return ReadBit() == 1 ? 1 : -1;
            int n = Receive(length);
            return n >= 1 << (length - 1) ? n : n - (1 << length) + 1;
        }

        public int Decode(Huffman h)
        {
            int code = 0;
            for (int len = 1; len <= 16; len++)
            {
                code = (code << 1) | ReadBit();
                if (code <= h.MaxCode[len]) return h.Symbols[h.ValOffset[len] + code];
            }
            throw new InvalidDataException("Invalid Huffman code in JPEG data.");
        }

        /// <summary>Drops the bits left in the current byte (at a restart marker).</summary>
        public void Align()
        {
            _bits = 0;
            _buffer = 0;
        }
    }

    /// <summary>
    /// Decodes <paramref name="data"/> to interleaved 8-bit samples, <paramref name="components"/>
    /// per pixel, row by row.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a JPEG this decoder reads.</exception>
    /// <exception cref="NotSupportedException">Arithmetic coding, lossless or hierarchical frames, or a precision other than 8 bits.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height, out int components)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG.");
        var quant = new int[4][];
        var dcTables = new Huffman?[4];
        var acTables = new Huffman?[4];
        Component[]? frame = null;
        bool progressive = false;
        int restartInterval = 0, adobeTransform = -1;
        width = height = components = 0;
        int maxH = 1, maxV = 1, mcusPerLine = 0, mcusPerColumn = 0;
        int pos = 2;
        bool scanned = false;

        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                pos++; // stray bytes between segments: skip to the next marker
                continue;
            }
            int marker = data[pos + 1];
            pos += 2;
            if (marker is 0xFF or 0x00) { pos--; continue; }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (marker == 0xD9) break;
            if (pos + 2 > data.Length) break;
            int length = data[pos] << 8 | data[pos + 1];
            if (length < 2 || pos + length > data.Length)
                throw new InvalidDataException("Truncated JPEG segment.");
            var seg = data.Slice(pos + 2, length - 2);
            switch (marker)
            {
                case 0xDB: // quantization tables
                {
                    int i = 0;
                    while (i < seg.Length)
                    {
                        int pq = seg[i] >> 4, tq = seg[i] & 15;
                        i++;
                        if (tq > 3) throw new InvalidDataException("Bad quantization table.");
                        var table = new int[64];
                        for (int k = 0; k < 64; k++)
                        {
                            if (pq == 0) { if (i >= seg.Length) throw new InvalidDataException("Truncated quantization table."); table[ZigZag[k]] = seg[i++]; }
                            else { if (i + 1 >= seg.Length) throw new InvalidDataException("Truncated quantization table."); table[ZigZag[k]] = seg[i] << 8 | seg[i + 1]; i += 2; }
                        }
                        quant[tq] = table;
                    }
                    break;
                }
                case 0xC4: // Huffman tables
                {
                    int i = 0;
                    while (i + 17 <= seg.Length)
                    {
                        int tc = seg[i] >> 4, th = seg[i] & 15;
                        var counts = seg.Slice(i + 1, 16);
                        int total = 0;
                        foreach (byte c in counts) total += c;
                        if (th > 3 || i + 17 + total > seg.Length) throw new InvalidDataException("Bad Huffman table.");
                        var table = Huffman.Build(counts, seg.Slice(i + 17, total));
                        if (tc == 0) dcTables[th] = table; else acTables[th] = table;
                        i += 17 + total;
                    }
                    break;
                }
                case 0xDD:
                    if (seg.Length >= 2) restartInterval = seg[0] << 8 | seg[1];
                    break;
                case 0xEE when seg.Length >= 12 && seg[..5].SequenceEqual("Adobe"u8):
                    adobeTransform = seg[11];
                    break;
                case 0xC0 or 0xC1 or 0xC2:
                {
                    if (frame != null) throw new InvalidDataException("More than one JPEG frame.");
                    if (seg.Length < 6 || seg[0] != 8) throw new NotSupportedException("Only 8-bit JPEG data is decoded.");
                    progressive = marker == 0xC2;
                    height = seg[1] << 8 | seg[2];
                    width = seg[3] << 8 | seg[4];
                    components = seg[5];
                    if (width == 0 || height == 0) throw new NotSupportedException("JPEG height set by a DNL marker.");
                    if ((long)width * height > MaxPixels) throw new InvalidDataException("JPEG image too large.");
                    if (components is not (1 or 3 or 4) || seg.Length < 6 + 3 * components) throw new InvalidDataException("Bad JPEG frame.");
                    frame = new Component[components];
                    for (int c = 0; c < components; c++)
                    {
                        var f = seg.Slice(6 + 3 * c, 3);
                        int h = f[1] >> 4, v = f[1] & 15;
                        if (h is < 1 or > 4 || v is < 1 or > 4 || f[2] > 3) throw new InvalidDataException("Bad JPEG component.");
                        frame[c] = new Component { Id = f[0], H = h, V = v, Tq = f[2] };
                        maxH = Math.Max(maxH, h);
                        maxV = Math.Max(maxV, v);
                    }
                    mcusPerLine = (width + 8 * maxH - 1) / (8 * maxH);
                    mcusPerColumn = (height + 8 * maxV - 1) / (8 * maxV);
                    foreach (var c in frame)
                    {
                        c.BlocksPerLine = ((width * c.H + maxH - 1) / maxH + 7) / 8;
                        c.BlocksPerColumn = ((height * c.V + maxV - 1) / maxV + 7) / 8;
                        c.Stride = mcusPerLine * c.H;
                        c.Rows = mcusPerColumn * c.V;
                        c.Coefficients = new short[checked(c.Stride * c.Rows * 64)];
                    }
                    break;
                }
                case 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF:
                    throw new NotSupportedException("Lossless, hierarchical and arithmetic-coded JPEG data is not decoded.");
                case 0xDA: // start of scan
                {
                    if (frame == null) throw new InvalidDataException("JPEG scan before its frame.");
                    int n = seg.Length > 0 ? seg[0] : 0;
                    if (n < 1 || seg.Length < 1 + 2 * n + 3) throw new InvalidDataException("Bad JPEG scan.");
                    var scan = new Component[n];
                    for (int s = 0; s < n; s++)
                    {
                        int id = seg[1 + 2 * s], tables = seg[2 + 2 * s];
                        var c = Array.Find(frame, x => x.Id == id) ?? throw new InvalidDataException("JPEG scan names an unknown component.");
                        c.Dc = dcTables[tables >> 4];
                        c.Ac = acTables[tables & 15];
                        scan[s] = c;
                    }
                    int ss = seg[1 + 2 * n], se = seg[2 + 2 * n], ah = seg[3 + 2 * n] >> 4, al = seg[3 + 2 * n] & 15;
                    if (!progressive) { ss = 0; se = 63; ah = al = 0; }
                    if (ss > se || se > 63) throw new InvalidDataException("Bad JPEG spectral selection.");
                    int end = DecodeScan(data, pos + length, scan, progressive, ss, se, ah, al, restartInterval, mcusPerLine, mcusPerColumn);
                    scanned = true;
                    pos = end;
                    continue;
                }
            }
            pos += length;
        }

        if (frame == null || !scanned) throw new InvalidDataException("JPEG without image data.");
        return Output(frame, width, height, maxH, maxV, quant, adobeTransform);
    }

    /// <summary>Decodes one scan's entropy-coded data; returns the position of the marker after it.</summary>
    private static int DecodeScan(ReadOnlySpan<byte> data, int start, Component[] scan, bool progressive, int ss, int se, int ah, int al,
        int restartInterval, int mcusPerLine, int mcusPerColumn)
    {
        var reader = new BitReader(data, start);
        foreach (var c in scan) c.Pred = 0;
        int eobrun = 0, acState = 0, acNext = 0, acRun = 0;
        int mcus = scan.Length == 1 ? scan[0].BlocksPerLine * scan[0].BlocksPerColumn : mcusPerLine * mcusPerColumn;
        int interval = restartInterval > 0 ? restartInterval : mcus;

        for (int mcu = 0; mcu < mcus;)
        {
            for (int i = 0; i < interval && mcu < mcus; i++, mcu++)
            {
                if (scan.Length == 1)
                {
                    var c = scan[0];
                    int row = mcu / c.BlocksPerLine, col = mcu % c.BlocksPerLine;
                    DecodeBlock(ref reader, c, 64 * (row * c.Stride + col));
                }
                else
                {
                    int mcuRow = mcu / mcusPerLine, mcuCol = mcu % mcusPerLine;
                    foreach (var c in scan)
                        for (int v = 0; v < c.V; v++)
                            for (int h = 0; h < c.H; h++)
                                DecodeBlock(ref reader, c, 64 * ((mcuRow * c.V + v) * c.Stride + mcuCol * c.H + h));
                }
            }
            if (mcu >= mcus) break;
            // Restart interval: the next restart marker resets the predictions and the bit stream.
            reader.Align();
            int p = FindMarker(data, reader.Position);
            if (p + 1 >= data.Length || data[p + 1] is < 0xD0 or > 0xD7) return p; // data ends early: keep what was decoded
            reader = new BitReader(data, p + 2);
            foreach (var c in scan) c.Pred = 0;
            eobrun = acState = acRun = 0;
        }
        return FindMarker(data, reader.Position);

        void DecodeBlock(ref BitReader r, Component c, int offset)
        {
            var coef = c.Coefficients.AsSpan(offset, 64);
            if (!progressive)
            {
                int t = r.Decode(c.Dc ?? throw new InvalidDataException("JPEG scan without a DC table."));
                c.Pred += r.ReceiveExtend(t);
                coef[0] = (short)c.Pred;
                var ac = c.Ac ?? throw new InvalidDataException("JPEG scan without an AC table.");
                for (int k = 1; k < 64;)
                {
                    int rs = r.Decode(ac), s = rs & 15, run = rs >> 4;
                    if (s == 0)
                    {
                        if (run < 15) break;
                        k += 16;
                        continue;
                    }
                    k += run;
                    if (k > 63) break;
                    coef[ZigZag[k]] = (short)r.ReceiveExtend(s);
                    k++;
                }
                return;
            }

            if (ss == 0)
            {
                // DC scans of a progressive frame.
                if (ah == 0)
                {
                    int t = r.Decode(c.Dc ?? throw new InvalidDataException("JPEG scan without a DC table."));
                    c.Pred += r.ReceiveExtend(t) * (1 << al);
                    coef[0] = (short)c.Pred;
                }
                else if (r.ReadBit() == 1)
                {
                    coef[0] |= (short)(1 << al);
                }
                return;
            }

            var table = c.Ac ?? throw new InvalidDataException("JPEG scan without an AC table.");
            if (ah == 0)
            {
                // First AC scan of a band.
                if (eobrun > 0) { eobrun--; return; }
                for (int k = ss; k <= se;)
                {
                    int rs = r.Decode(table), s = rs & 15, run = rs >> 4;
                    if (s == 0)
                    {
                        if (run < 15)
                        {
                            eobrun = r.Receive(run) + (1 << run) - 1;
                            break;
                        }
                        k += 16;
                        continue;
                    }
                    k += run;
                    if (k > 63) break;
                    coef[ZigZag[k]] = (short)(r.ReceiveExtend(s) * (1 << al));
                    k++;
                }
                return;
            }

            // AC refinement: one correction bit per coefficient already non-zero, new ones of ±1.
            for (int k = ss; k <= se; k++)
            {
                int z = ZigZag[k];
                int sign = coef[z] < 0 ? -1 : 1;
                switch (acState)
                {
                    case 0:
                    {
                        int rs = r.Decode(table), s = rs & 15, run = rs >> 4;
                        if (s == 0)
                        {
                            if (run < 15)
                            {
                                eobrun = r.Receive(run) + (1 << run);
                                acState = 4;
                            }
                            else
                            {
                                acRun = 16; // ZRL: sixteen zero coefficients
                                acState = 1;
                            }
                        }
                        else
                        {
                            if (s != 1) throw new InvalidDataException("Invalid JPEG AC refinement.");
                            acNext = r.ReceiveExtend(1);
                            acState = run > 0 ? 2 : 3;
                            acRun = run;
                        }
                        k--; // this coefficient is handled by the new state
                        continue;
                    }
                    case 1:
                    case 2:
                        if (coef[z] != 0)
                        {
                            coef[z] += (short)(sign * (r.ReadBit() << al));
                        }
                        else
                        {
                            if (--acRun == 0) acState = acState == 2 ? 3 : 0;
                        }
                        break;
                    case 3:
                        if (coef[z] != 0)
                        {
                            coef[z] += (short)(sign * (r.ReadBit() << al));
                        }
                        else
                        {
                            coef[z] = (short)(acNext * (1 << al));
                            acState = 0;
                        }
                        break;
                    case 4:
                        if (coef[z] != 0) coef[z] += (short)(sign * (r.ReadBit() << al));
                        break;
                }
            }
            if (acState == 4 && --eobrun == 0) acState = 0;
        }
    }

    /// <summary>The next marker (0xFF and a byte other than 0x00 or 0xFF) at or after <paramref name="pos"/>.</summary>
    private static int FindMarker(ReadOnlySpan<byte> data, int pos)
    {
        for (int i = pos; i + 1 < data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] is not (0x00 or 0xFF))
                return i;
        return data.Length;
    }

    /// <summary>Dequantizes, transforms and interleaves the components; applies the colour transform.</summary>
    private static byte[] Output(Component[] frame, int width, int height, int maxH, int maxV, int[][] quant, int adobeTransform)
    {
        int n = frame.Length;
        var planes = new byte[n][];
        Span<float> block = stackalloc float[64];
        for (int c = 0; c < n; c++)
        {
            var comp = frame[c];
            var q = quant[comp.Tq] ?? throw new InvalidDataException("JPEG component without a quantization table.");
            int w = comp.Stride * 8;
            var plane = planes[c] = new byte[checked(w * comp.Rows * 8)];
            for (int by = 0; by < comp.Rows; by++)
                for (int bx = 0; bx < comp.Stride; bx++)
                {
                    var coef = comp.Coefficients.AsSpan(64 * (by * comp.Stride + bx), 64);
                    for (int i = 0; i < 64; i++) block[i] = coef[i] * q[i];
                    InverseDct(block);
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            plane[(by * 8 + y) * w + bx * 8 + x] = Clamp(block[y * 8 + x] + 128f);
                }
        }

        // No Adobe marker: three components are YCbCr (JFIF), four are CMYK as stored.
        bool transform = adobeTransform switch
        {
            -1 => n == 3,
            0 => false,
            _ => n >= 3,
        };
        var output = new byte[checked(width * height * n)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * n;
                for (int c = 0; c < n; c++)
                {
                    var comp = frame[c];
                    int sx = x * comp.H / maxH, sy = y * comp.V / maxV;
                    output[o + c] = planes[c][sy * comp.Stride * 8 + sx];
                }
                if (!transform) continue;
                float yy = output[o], cb = output[o + 1] - 128f, cr = output[o + 2] - 128f;
                byte r = Clamp(yy + 1.402f * cr), g = Clamp(yy - 0.344136f * cb - 0.714136f * cr), b = Clamp(yy + 1.772f * cb);
                if (n == 4)
                {
                    // YCCK: the YCbCr triple is the inverted CMY (libjpeg's ycck_cmyk_convert); K stays.
                    output[o] = (byte)(255 - r);
                    output[o + 1] = (byte)(255 - g);
                    output[o + 2] = (byte)(255 - b);
                }
                else
                {
                    output[o] = r;
                    output[o + 1] = g;
                    output[o + 2] = b;
                }
            }
        return output;
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    private static readonly float[] Cosines = BuildCosines();

    private static float[] BuildCosines()
    {
        // Cosines[x * 8 + u] = C(u) / 2 · cos((2x + 1) u π / 16), C(0) = 1/√2.
        var t = new float[64];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                t[x * 8 + u] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return t;
    }

    /// <summary>Separable 8 × 8 inverse DCT in place (rows, then columns).</summary>
    private static void InverseDct(Span<float> block)
    {
        Span<float> tmp = stackalloc float[64];
        var cos = Cosines;
        for (int v = 0; v < 8; v++)
            for (int x = 0; x < 8; x++)
            {
                float s = 0;
                for (int u = 0; u < 8; u++) s += cos[x * 8 + u] * block[v * 8 + u];
                tmp[v * 8 + x] = s;
            }
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                float s = 0;
                for (int v = 0; v < 8; v++) s += cos[y * 8 + v] * tmp[v * 8 + x];
                block[y * 8 + x] = s;
            }
    }
}
