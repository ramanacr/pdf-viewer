using System;
using System.Collections.Generic;
using System.Threading;
using PdfEngine.Vector.Diagnostics;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// CCITTFaxDecode (ISO 32000-2 7.4.6): ITU-T T.4 Group 3 one-dimensional (K = 0, Modified
/// Huffman), mixed one/two-dimensional (K &gt; 0, Modified READ) and T.6 Group 4 (K &lt; 0, MMR).
/// Honours /Columns, /Rows, /EndOfLine, /EncodedByteAlign, /EndOfBlock, /BlackIs1 and
/// /DamagedRowsBeforeError. Output is packed 1-bit rows, 0 = black unless /BlackIs1.
/// A row that fails to decode is left white and decoding resynchronises on the next EOL when
/// the data has EOLs; otherwise the remaining rows stay white.
/// </summary>
internal sealed class CcittFaxDecoder
{
    public sealed record Parameters(int K = 0, bool EndOfLine = false, bool EncodedByteAlign = false, int Columns = 1728,
        int Rows = 0, bool EndOfBlock = true, bool BlackIs1 = false, int DamagedRowsBeforeError = 0);

    // ------------------------------------------------------------------ code tables (T.4 Tables 2, 3)

    private static readonly Dictionary<int, int> White = new(), Black = new();

    private static int Key(int length, int code) => (length << 16) | code;

    private static void AddCodes(Dictionary<int, int> table, string codes, int firstRun, int step)
    {
        int run = firstRun;
        foreach (var bits in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            table[Key(bits.Length, Convert.ToInt32(bits, 2))] = run;
            run += step;
        }
    }

    static CcittFaxDecoder()
    {
        AddCodes(White,
            "00110101 000111 0111 1000 1011 1100 1110 1111 10011 10100 00111 01000 001000 000011 110100 110101 " +
            "101010 101011 0100111 0001100 0001000 0010111 0000011 0000100 0101000 0101011 0010011 0100100 0011000 " +
            "00000010 00000011 00011010 00011011 00010010 00010011 00010100 00010101 00010110 00010111 00101000 " +
            "00101001 00101010 00101011 00101100 00101101 00000100 00000101 00001010 00001011 01010010 01010011 " +
            "01010100 01010101 00100100 00100101 01011000 01011001 01011010 01011011 01001010 01001011 00110010 " +
            "00110011 00110100", 0, 1);
        AddCodes(White,
            "11011 10010 010111 0110111 00110110 00110111 01100100 01100101 01101000 01100111 011001100 011001101 " +
            "011010010 011010011 011010100 011010101 011010110 011010111 011011000 011011001 011011010 011011011 " +
            "010011000 010011001 010011010 011000 010011011", 64, 64);
        AddCodes(Black,
            "0000110111 010 11 10 011 0011 0010 00011 000101 000100 0000100 0000101 0000111 00000100 00000111 " +
            "000011000 0000010111 0000011000 0000001000 00001100111 00001101000 00001101100 00000110111 00000101000 " +
            "00000010111 00000011000 000011001010 000011001011 000011001100 000011001101 000001101000 000001101001 " +
            "000001101010 000001101011 000011010010 000011010011 000011010100 000011010101 000011010110 000011010111 " +
            "000001101100 000001101101 000011011010 000011011011 000001010100 000001010101 000001010110 000001010111 " +
            "000001100100 000001100101 000001010010 000001010011 000000100100 000000110111 000000111000 000000100111 " +
            "000000101000 000001011000 000001011001 000000101011 000000101100 000001011010 000001100110 000001100111", 0, 1);
        AddCodes(Black,
            "0000001111 000011001000 000011001001 000001011011 000000110011 000000110100 000000110101 0000001101100 " +
            "0000001101101 0000001001010 0000001001011 0000001001100 0000001001101 0000001110010 0000001110011 " +
            "0000001110100 0000001110101 0000001110110 0000001110111 0000001010010 0000001010011 0000001010100 " +
            "0000001010101 0000001011010 0000001011011 0000001100100 0000001100101", 64, 64);
        // Extended make-up codes (T.4 Table 3), shared by both colours.
        const string extended = "00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 " +
                                "000000010110 000000010111 000000011100 000000011101 000000011110 000000011111";
        AddCodes(White, extended, 1792, 64);
        AddCodes(Black, extended, 1792, 64);
    }

    // ------------------------------------------------------------------ bit reader

    private readonly byte[] _data;
    private long _bit;
    private readonly long _bits;

    private CcittFaxDecoder(byte[] data, int start = 0, int end = -1)
    {
        _data = data;
        _bit = (long)start * 8;
        _bits = (long)(end < 0 ? data.Length : Math.Min(end, data.Length)) * 8;
    }

    private bool AtEnd => _bit >= _bits;

    private int Peek(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            long b = _bit + i;
            v = (v << 1) | (b < _bits ? (_data[b >> 3] >> (7 - (int)(b & 7))) & 1 : 0);
        }
        return v;
    }

    private int Read(int n)
    {
        int v = Peek(n);
        _bit += n;
        return v;
    }

    private void Align() => _bit = (_bit + 7) & ~7L;

    private sealed class DecodeException : Exception { }

    /// <summary>One run length (make-up codes accumulate until a terminating code).</summary>
    private int ReadRun(bool black)
    {
        var table = black ? Black : White;
        int total = 0;
        while (true)
        {
            int code = 0, found = -1;
            for (int len = 1; len <= 13; len++)
            {
                code = (code << 1) | Read(1);
                if (table.TryGetValue(Key(len, code), out int run)) { found = run; break; }
            }
            if (found < 0)
                throw new DecodeException();
            total += found;
            if (found < 64)
                return total;
            if (total > 1 << 20)
                throw new DecodeException();
        }
    }

    private enum Mode { Pass, Horizontal, V0, VR1, VR2, VR3, VL1, VL2, VL3, Eol }

    private Mode ReadMode()
    {
        if (Read(1) == 1) return Mode.V0;
        int b = Read(2); // 0x
        if (b == 0b11) return Mode.VR1;   // 011
        if (b == 0b10) return Mode.VL1;   // 010
        if (b == 0b01) return Mode.Horizontal; // 001
        if (Read(1) == 1) return Mode.Pass; // 0001
        // 0000xx...
        int c = Read(2);
        if (c == 0b11) return Mode.VR2; // 000011
        if (c == 0b10) return Mode.VL2; // 000010
        if (c == 0b01) return Read(1) == 1 ? Mode.VR3 : Mode.VL3; // 0000011 / 0000010
        // 000000 so far: 0000001xxx is an (unsupported) extension; otherwise an EOL (000000000001).
        if (Read(1) == 1) throw new DecodeException();
        int zeros = 7;
        while (!AtEnd && Peek(1) == 0 && zeros < 64) { _bit++; zeros++; }
        if (zeros >= 11 && !AtEnd) { _bit++; return Mode.Eol; }
        throw new DecodeException();
    }

    // ------------------------------------------------------------------ line decoding

    /// <summary>Changing elements of a 1D (MH) coded line.</summary>
    private void Decode1D(List<int> line, int columns)
    {
        int a0 = 0;
        bool black = false;
        while (a0 < columns)
        {
            int run = ReadRun(black);
            a0 = Math.Min(columns, a0 + run);
            line.Add(a0);
            black = !black;
        }
    }

    /// <summary>Changing elements of a 2D (MR/MMR) coded line against the reference line.</summary>
    private void Decode2D(List<int> line, int[] reference, int refCount, int columns)
    {
        int a0 = -1;
        bool black = false;
        int searchFrom = 0;
        while (a0 < columns)
        {
            // b1: first changing element on the reference line right of a0 with the opposite colour
            // change (even indices change white → black); b2: the next one.
            int i = Math.Max(0, searchFrom - 2);
            int start = a0 < 0 ? -1 : a0;
            while (i < refCount && (reference[i] <= start || (i & 1) != (black ? 1 : 0))) i++;
            searchFrom = i;
            int b1 = i < refCount ? reference[i] : columns;
            int b2 = i + 1 < refCount ? reference[i + 1] : columns;

            var mode = ReadMode();
            switch (mode)
            {
                case Mode.Pass:
                    a0 = b2;
                    break;
                case Mode.Horizontal:
                {
                    int origin = Math.Max(a0, 0);
                    int a1 = Math.Min(columns, origin + ReadRun(black));
                    int a2 = Math.Min(columns, a1 + ReadRun(!black));
                    line.Add(a1);
                    line.Add(a2);
                    a0 = a2;
                    break;
                }
                case Mode.Eol:
                    throw new DecodeException(); // premature EOL inside a line
                default:
                {
                    int d = mode switch
                    {
                        Mode.V0 => 0, Mode.VR1 => 1, Mode.VR2 => 2, Mode.VR3 => 3,
                        Mode.VL1 => -1, Mode.VL2 => -2, _ => -3,
                    };
                    int a1 = b1 + d;
                    if (a1 < 0 || a1 > columns || (a0 >= 0 && a1 < a0))
                        throw new DecodeException();
                    line.Add(a1);
                    a0 = a1;
                    black = !black;
                    break;
                }
            }
        }
    }

    /// <summary>Consumes fill bits and EOL codes at a row start; returns how many EOLs were seen.</summary>
    private int SkipEols()
    {
        int eols = 0;
        while (true)
        {
            long save = _bit;
            int zeros = 0;
            while (!AtEnd && Peek(1) == 0 && zeros < 4096) { _bit++; zeros++; }
            if (zeros >= 11 && !AtEnd && Peek(1) == 1)
            {
                _bit++;
                eols++;
                continue;
            }
            _bit = save;
            return eols;
        }
    }

    // ------------------------------------------------------------------ entry point

    public static byte[] Decode(byte[] data, Parameters p, int height, CancellationToken ct = default) =>
        Decode(data, 0, data.Length, p, height, out _, ct);

    /// <summary>Decodes from <paramref name="start"/>; <paramref name="endByte"/> is the first byte after the consumed data.</summary>
    public static byte[] Decode(byte[] data, int start, int end, Parameters p, int height, out int endByte, CancellationToken ct = default)
    {
        int columns = Math.Clamp(p.Columns, 1, 1 << 20);
        int rows = p.Rows > 0 ? Math.Min(p.Rows, height > 0 ? height : p.Rows) : height;
        if (rows <= 0)
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "CCITT image has no row count.");
        int rowBytes = (columns + 7) / 8;
        var output = new byte[checked((long)rowBytes * rows)];
        byte white = p.BlackIs1 ? (byte)0x00 : (byte)0xFF;
        Array.Fill(output, white);

        var d = new CcittFaxDecoder(data, start, end);
        var reference = new int[columns + 4];
        int refCount = 0;
        reference[refCount++] = columns; // an all-white reference line
        reference[refCount++] = columns;
        var line = new List<int>(64);
        int damaged = 0;

        for (int row = 0; row < rows; row++)
        {
            if ((row & 63) == 0) ct.ThrowIfCancellationRequested();
            if (p.EncodedByteAlign && (p.K < 0 || !p.EndOfLine))
                d.Align();
            int eols = d.SkipEols();
            if (p.EndOfBlock && (eols >= 2 && p.K < 0 || eols >= 6))
                break; // EOFB (T.6) or RTC (T.4)
            if (d.AtEnd)
                break;

            bool twoD = p.K < 0;
            if (p.K > 0)
                twoD = d.Read(1) == 0; // tag bit: 1 = one-dimensional, 0 = two-dimensional

            line.Clear();
            long lineStart = d._bit;
            try
            {
                if (twoD) d.Decode2D(line, reference, refCount, columns);
                else d.Decode1D(line, columns);
            }
            catch (DecodeException)
            {
                // Damaged row: white; resynchronise on the next EOL if the data has them.
                if (++damaged > Math.Max(0, p.DamagedRowsBeforeError) && p.K < 0)
                    break;
                line.Clear();
                if (!p.EndOfLine && p.K < 0)
                    break;
                d._bit = lineStart;
                if (!d.ScanToEol())
                    break;
                refCount = 0;
                reference[refCount++] = columns;
                reference[refCount++] = columns;
                continue;
            }

            // Paint black spans: changing elements alternate white → black → white …
            int rowOffset = row * rowBytes;
            int prev = 0;
            for (int k = 0; k < line.Count; k++)
            {
                int pos = Math.Clamp(line[k], 0, columns);
                if ((k & 1) == 1)
                    SetSpan(output, rowOffset, prev, pos, p.BlackIs1);
                prev = pos;
            }

            refCount = 0;
            foreach (int e in line)
                if (refCount < reference.Length - 2 && (refCount == 0 || e >= reference[refCount - 1]))
                    reference[refCount++] = Math.Min(e, columns);
            reference[refCount++] = columns;
            reference[refCount++] = columns;
        }
        if (p.EndOfBlock)
            d.SkipEols(); // a trailing EOFB/RTC belongs to this data
        endByte = (int)Math.Min((d._bit + 7) >> 3, d._bits >> 3);
        return output;
    }

    /// <summary>Moves to the next EOL code (left for the next row's EOL handling); false at end of data.</summary>
    private bool ScanToEol()
    {
        _bit++;
        while (_bit + 12 <= _bits)
        {
            if (Peek(12) == 1)
                return true;
            _bit++;
        }
        return false;
    }

    private static void SetSpan(byte[] output, int rowOffset, int from, int to, bool blackIs1)
    {
        for (int x = from; x < to; x++)
        {
            int idx = rowOffset + (x >> 3);
            byte mask = (byte)(0x80 >> (x & 7));
            if (blackIs1) output[idx] |= mask;
            else output[idx] &= (byte)~mask;
        }
    }
}
