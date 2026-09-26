using System;
using System.Collections.Generic;
using PdfEngine.Vector.Images.Jbig2;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// A minimal JBIG2 (T.88) encoder for tests: MQ arithmetic coding (Annex E.2), integer and
/// symbol-ID coding, generic and refinement regions, symbol dictionaries, text regions, pattern
/// dictionaries and halftone regions, and Huffman coding with the decoder's tables. Streams it
/// writes are decoded by PDFium as the oracle, so encoder/decoder symmetry cannot hide bugs.
/// </summary>
internal sealed class Jbig2TestEncoder
{
    // ------------------------------------------------------------------ MQ encoder (E.2)

    private static readonly (int Qe, int Nmps, int Nlps, bool Switch)[] Q =
    {
        (0x5601, 1, 1, true), (0x3401, 2, 6, false), (0x1801, 3, 9, false), (0x0AC1, 4, 12, false),
        (0x0521, 5, 29, false), (0x0221, 38, 33, false), (0x5601, 7, 6, true), (0x5401, 8, 14, false),
        (0x4801, 9, 14, false), (0x3801, 10, 14, false), (0x3001, 11, 17, false), (0x2401, 12, 18, false),
        (0x1C01, 13, 20, false), (0x1601, 29, 21, false), (0x5601, 15, 14, true), (0x5401, 16, 14, false),
        (0x5101, 17, 15, false), (0x4801, 18, 16, false), (0x3801, 19, 17, false), (0x3401, 20, 18, false),
        (0x3001, 21, 19, false), (0x2801, 22, 19, false), (0x2401, 23, 20, false), (0x2201, 24, 21, false),
        (0x1C01, 25, 22, false), (0x1801, 26, 23, false), (0x1601, 27, 24, false), (0x1401, 28, 25, false),
        (0x1201, 29, 26, false), (0x1101, 30, 27, false), (0x0AC1, 31, 28, false), (0x09C1, 32, 29, false),
        (0x08A1, 33, 30, false), (0x0521, 34, 31, false), (0x0441, 35, 32, false), (0x02A1, 36, 33, false),
        (0x0221, 37, 34, false), (0x0141, 38, 35, false), (0x0111, 39, 36, false), (0x0085, 40, 37, false),
        (0x0049, 41, 38, false), (0x0025, 42, 39, false), (0x0015, 43, 40, false), (0x0009, 44, 41, false),
        (0x0005, 45, 42, false), (0x0001, 45, 43, false), (0x5601, 46, 46, false),
    };

    internal sealed class Mq
    {
        private readonly List<byte> _out = new() { 0 }; // index 0: the byte before the first (B at BP = -1)
        private uint _a = 0x8000, _c;
        private int _ct = 12;

        private ref byte B => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_out)[^1];

        public void Encode(byte[] contexts, int cx, int d)
        {
            int index = contexts[cx] >> 1, mps = contexts[cx] & 1;
            var e = Q[index];
            uint qe = (uint)e.Qe;
            if (d == mps)
            {
                _a -= qe;
                if ((_a & 0x8000) == 0)
                {
                    if (_a < qe) _a = qe; else _c += qe;
                    contexts[cx] = (byte)((e.Nmps << 1) | mps);
                    Renorm();
                }
                else
                {
                    _c += qe;
                }
            }
            else
            {
                _a -= qe;
                if (_a < qe) _c += qe; else _a = qe;
                if (e.Switch) mps = 1 - mps;
                contexts[cx] = (byte)((e.Nlps << 1) | mps);
                Renorm();
            }
        }

        private void Renorm()
        {
            do
            {
                _a <<= 1;
                _c <<= 1;
                _ct--;
                if (_ct == 0) ByteOut();
            }
            while ((_a & 0x8000) == 0);
        }

        private void ByteOut()
        {
            if (B == 0xFF)
            {
                _out.Add((byte)(_c >> 20)); _c &= 0xFFFFF; _ct = 7;
            }
            else if (_c < 0x8000000)
            {
                _out.Add((byte)(_c >> 19)); _c &= 0x7FFFF; _ct = 8;
            }
            else
            {
                B++;
                if (B == 0xFF)
                {
                    _c &= 0x7FFFFFF;
                    _out.Add((byte)(_c >> 20)); _c &= 0xFFFFF; _ct = 7;
                }
                else
                {
                    _out.Add((byte)(_c >> 19)); _c &= 0x7FFFF; _ct = 8;
                }
            }
        }

        public byte[] Flush()
        {
            uint temp = _c + _a;
            _c |= 0xFFFF;
            if (_c >= temp) _c -= 0x8000;
            _c <<= _ct; ByteOut();
            _c <<= _ct; ByteOut();
            if (B != 0xFF) _out.Add(0xFF);
            _out.Add(0xAC);
            return _out.GetRange(1, _out.Count - 1).ToArray();
        }
    }

    internal sealed class IntEncoder
    {
        private readonly byte[] _ctx = new byte[512];
        private readonly Mq _mq;
        public IntEncoder(Mq mq) => _mq = mq;

        public void Encode(int? value)
        {
            int prev = 1;
            void Bits(long v, int n)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    int bit = (int)((v >> i) & 1);
                    _mq.Encode(_ctx, prev, bit);
                    prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                }
            }
            if (value == null) { Bits(1, 1); Bits(0, 1); Bits(0, 2); return; } // OOB: negative zero
            long v = value.Value;
            Bits(v < 0 ? 1 : 0, 1);
            v = Math.Abs(v);
            if (v < 4) { Bits(0, 1); Bits(v, 2); }
            else if (v < 20) { Bits(0b10, 2); Bits(v - 4, 4); }
            else if (v < 84) { Bits(0b110, 3); Bits(v - 20, 6); }
            else if (v < 340) { Bits(0b1110, 4); Bits(v - 84, 8); }
            else if (v < 4436) { Bits(0b11110, 5); Bits(v - 340, 12); }
            else { Bits(0b11111, 5); Bits(v - 4436, 32); }
        }
    }

    internal sealed class IdEncoder
    {
        private readonly byte[] _ctx;
        private readonly Mq _mq;
        private readonly int _len;
        public IdEncoder(Mq mq, int len) { _mq = mq; _len = len; _ctx = new byte[1 << (len + 1)]; }
        public void Encode(int id)
        {
            int prev = 1;
            for (int i = _len - 1; i >= 0; i--)
            {
                int bit = (id >> i) & 1;
                _mq.Encode(_ctx, prev, bit);
                prev = (prev << 1) | bit;
            }
        }
    }

    // ------------------------------------------------------------------ regions

    public static void EncodeGeneric(Mq mq, byte[] ctx, Jbig2Bitmap b, int template, (sbyte X, sbyte Y)[] at, bool tpgdon, Jbig2Bitmap? skip = null)
    {
        int sltp = template switch { 0 => 0x9B25, 1 => 0x0795, 2 => 0x00E5, _ => 0x0195 };
        bool ltp = false;
        for (int y = 0; y < b.Height; y++)
        {
            if (tpgdon)
            {
                bool same = y > 0;
                for (int x = 0; same && x < b.Width; x++) same = b.Get(x, y) == b.Get(x, y - 1);
                if (y == 0) { same = true; for (int x = 0; same && x < b.Width; x++) same = b.Get(x, 0) == 0; }
                mq.Encode(ctx, sltp, same != ltp ? 1 : 0);
                ltp = same;
                if (ltp) continue;
            }
            for (int x = 0; x < b.Width; x++)
            {
                if (skip != null && skip.Get(x, y) != 0) continue;
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
                mq.Encode(ctx, cx, b.Get(x, y));
            }
        }
    }

    public static void EncodeRefinement(Mq mq, byte[] ctx, Jbig2Bitmap b, int template, Jbig2Bitmap reference, int dx, int dy, (sbyte X, sbyte Y)[] at)
    {
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int rx = x - dx, ry = y - dy;
                int cx = template == 0
                    ? b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x + at[0].X, y + at[0].Y) << 3
                      | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx - 1, ry + 1) << 6
                      | reference.Get(rx + 1, ry) << 7 | reference.Get(rx, ry) << 8 | reference.Get(rx - 1, ry) << 9
                      | reference.Get(rx + 1, ry - 1) << 10 | reference.Get(rx, ry - 1) << 11
                      | reference.Get(rx + at[1].X, ry + at[1].Y) << 12
                    : b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x - 1, y - 1) << 3
                      | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5
                      | reference.Get(rx + 1, ry) << 6 | reference.Get(rx, ry) << 7 | reference.Get(rx - 1, ry) << 8
                      | reference.Get(rx, ry - 1) << 9;
                mq.Encode(ctx, cx, b.Get(x, y));
            }
    }

    // ------------------------------------------------------------------ Huffman

    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bit;
        public void Put(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (_bit == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(0x80 >> _bit);
                _bit = (_bit + 1) & 7;
            }
        }
        public void Align() => _bit = 0;
        public void Bytes(byte[] data) { Align(); _bytes.AddRange(data); }
        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>Encodes a value (null = OOB) with a table's canonical codes.</summary>
    public static void Huffman(BitWriter w, Jbig2HuffmanTable table, int? value)
    {
        foreach (var (line, code) in table.Codes)
        {
            if (code < 0) continue;
            if (value == null)
            {
                if (line.IsOob) { w.Put(code, line.PrefixLength); return; }
                continue;
            }
            if (line.IsOob) continue;
            long v = value.Value;
            if (line.IsLower)
            {
                if (v <= line.RangeLow) { w.Put(code, line.PrefixLength); w.Put(line.RangeLow - v, 32); return; }
                continue;
            }
            long high = line.RangeLength == 32 ? long.MaxValue : line.RangeLow + (1L << line.RangeLength) - 1;
            if (v >= line.RangeLow && v <= high)
            {
                w.Put(code, line.PrefixLength);
                if (line.RangeLength > 0) w.Put(v - line.RangeLow, line.RangeLength);
                return;
            }
        }
        throw new InvalidOperationException($"value {value} not representable");
    }

    // ------------------------------------------------------------------ segments

    private readonly List<byte> _stream = new();
    private uint _next;

    public Jbig2TestEncoder(uint firstNumber = 0) => _next = firstNumber;

    public uint NextNumber => _next;

    public uint Segment(int type, byte[] data, params uint[] referred)
    {
        uint number = _next++;
        void U32(uint v) { _stream.Add((byte)(v >> 24)); _stream.Add((byte)(v >> 16)); _stream.Add((byte)(v >> 8)); _stream.Add((byte)v); }
        U32(number);
        _stream.Add((byte)type); // page association in one byte
        _stream.Add((byte)(referred.Length << 5));
        foreach (uint r in referred) _stream.Add((byte)r); // numbers stay below 256 in tests
        _stream.Add(1);   // page 1
        U32((uint)data.Length);
        _stream.AddRange(data);
        return number;
    }

    public byte[] ToArray() => _stream.ToArray();

    public static byte[] Be32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    public static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };

    public static byte[] PageInfo(int w, int h, bool defaultBlack = false) =>
        Concat(Be32(w), Be32(h), Be32(0), Be32(0), new[] { (byte)(defaultBlack ? 4 : 0) }, Be16(0));

    public static byte[] RegionInfo(int w, int h, int x, int y, int op) =>
        Concat(Be32(w), Be32(h), Be32(x), Be32(y), new[] { (byte)op });

    public static byte[] Concat(params byte[][] parts)
    {
        var list = new List<byte>();
        foreach (var p in parts) list.AddRange(p);
        return list.ToArray();
    }
}
