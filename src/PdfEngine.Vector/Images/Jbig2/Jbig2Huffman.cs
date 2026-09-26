using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Images.Jbig2;

/// <summary>MSB-first bit reader over segment data (Huffman-coded segments, T.88 Annex B).</summary>
internal sealed class Jbig2BitReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private long _bit;

    public Jbig2BitReader(byte[] data, int start, int end)
    {
        _data = data;
        _end = Math.Min(end, data.Length);
        _bit = (long)start * 8;
    }

    public int BytePosition => (int)((_bit + 7) >> 3);

    public int ReadBit()
    {
        if (_bit >= (long)_end * 8)
            throw new Jbig2Exception("JBIG2 Huffman data ended early.");
        int v = (_data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1;
        _bit++;
        return v;
    }

    public uint ReadBits(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++) v = (v << 1) | (uint)ReadBit();
        return v;
    }

    public void Align() => _bit = (_bit + 7) & ~7L;

    public void SkipTo(int bytePosition) => _bit = (long)bytePosition * 8;
}

/// <summary>
/// A Huffman table (T.88 B.3): lines of (PREFLEN, RANGELEN, RANGELOW) with optional lower-range,
/// upper-range and OOB lines; prefix codes are assigned canonically from the lengths in line order.
/// </summary>
internal sealed class Jbig2HuffmanTable
{
    public readonly record struct Line(int PrefixLength, int RangeLength, int RangeLow, bool IsLower = false, bool IsOob = false);

    private readonly Dictionary<long, Line> _byCode = new();
    private readonly int _maxLength;

    /// <summary>Assigned prefix code per line (exposed for verification against Annex B).</summary>
    public IReadOnlyList<(Line Line, int Code)> Codes { get; }

    public Jbig2HuffmanTable(IReadOnlyList<Line> lines)
    {
        int max = 0;
        foreach (var l in lines) max = Math.Max(max, l.PrefixLength);
        _maxLength = max;
        var lenCount = new int[max + 2];
        foreach (var l in lines) if (l.PrefixLength > 0) lenCount[l.PrefixLength]++;
        var firstCode = new int[max + 2];
        var codes = new List<(Line, int)>();
        int[] assigned = new int[lines.Count];
        for (int len = 1, code = 0; len <= max; len++)
        {
            code = (code + lenCount[len - 1]) << 1;
            if (len == 1) code = 0;
            firstCode[len] = code;
            int cur = code;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].PrefixLength != len) continue;
                assigned[i] = cur;
                _byCode[((long)len << 32) | (uint)cur] = lines[i];
                cur++;
            }
        }
        for (int i = 0; i < lines.Count; i++) codes.Add((lines[i], lines[i].PrefixLength > 0 ? assigned[i] : -1));
        Codes = codes;
    }

    /// <summary>The decoded value, or null for OOB.</summary>
    public int? Decode(Jbig2BitReader r)
    {
        int code = 0;
        for (int len = 1; len <= _maxLength; len++)
        {
            code = (code << 1) | r.ReadBit();
            if (!_byCode.TryGetValue(((long)len << 32) | (uint)code, out var line))
                continue;
            if (line.IsOob) return null;
            long offset = line.RangeLength > 0 ? r.ReadBits(line.RangeLength) : 0;
            long v = line.IsLower ? line.RangeLow - offset : line.RangeLow + offset;
            return (int)Math.Clamp(v, int.MinValue, int.MaxValue);
        }
        throw new Jbig2Exception("JBIG2 Huffman code not in table.");
    }

    public int DecodeRequired(Jbig2BitReader r, string what) => Decode(r) ?? throw new Jbig2Exception($"JBIG2 {what} is out of band.");

    // ------------------------------------------------------------------ standard tables (Annex B.5)

    private static readonly Jbig2HuffmanTable?[] Standard = new Jbig2HuffmanTable?[16];

    private static Line L(int low, int prefix, int range) => new(prefix, range, low);
    private static Line Lower(int low, int prefix) => new(prefix, 32, low, IsLower: true);
    private static Line Upper(int low, int prefix) => new(prefix, 32, low);
    private static Line Oob(int prefix) => new(prefix, 0, 0, IsOob: true);

    public static Jbig2HuffmanTable Get(int number)
    {
        lock (Standard)
        {
            return Standard[number] ??= new Jbig2HuffmanTable(number switch
            {
                1 => new[] { L(0, 1, 4), L(16, 2, 8), L(272, 3, 16), Upper(65808, 3) },
                2 => new[] { L(0, 1, 0), L(1, 2, 0), L(2, 3, 0), L(3, 4, 3), L(11, 5, 6), Upper(75, 6), Oob(6) },
                3 => new[] { L(-256, 8, 8), L(0, 1, 0), L(1, 2, 0), L(2, 3, 0), L(3, 4, 3), L(11, 5, 6), Lower(-257, 8), Upper(75, 7), Oob(6) },
                4 => new[] { L(1, 1, 0), L(2, 2, 0), L(3, 3, 0), L(4, 4, 3), L(12, 5, 6), Upper(76, 5) },
                5 => new[] { L(-255, 7, 8), L(1, 1, 0), L(2, 2, 0), L(3, 3, 0), L(4, 4, 3), L(12, 5, 6), Lower(-256, 7), Upper(76, 6) },
                6 => new[] { L(-2048, 5, 10), L(-1024, 4, 9), L(-512, 4, 8), L(-256, 4, 7), L(-128, 5, 6), L(-64, 5, 5), L(-32, 4, 5),
                             L(0, 2, 7), L(128, 3, 7), L(256, 3, 8), L(512, 4, 9), L(1024, 4, 10), Lower(-2049, 6), Upper(2048, 6) },
                7 => new[] { L(-1024, 4, 9), L(-512, 3, 8), L(-256, 4, 7), L(-128, 5, 6), L(-64, 5, 5), L(-32, 4, 5), L(0, 4, 5),
                             L(32, 5, 5), L(64, 5, 6), L(128, 4, 7), L(256, 3, 8), L(512, 3, 9), L(1024, 3, 10), Lower(-1025, 5), Upper(2048, 5) },
                8 => new[] { L(-15, 8, 3), L(-7, 9, 1), L(-5, 8, 1), L(-3, 9, 0), L(-2, 7, 0), L(-1, 4, 0), L(0, 2, 1), L(2, 5, 0),
                             L(3, 6, 0), L(4, 3, 4), L(20, 6, 1), L(22, 4, 4), L(38, 4, 5), L(70, 5, 6), L(134, 5, 7), L(262, 6, 7),
                             L(390, 7, 8), L(646, 6, 10), Lower(-16, 9), Upper(1670, 9), Oob(2) },
                9 => new[] { L(-31, 8, 4), L(-15, 9, 2), L(-11, 8, 2), L(-7, 9, 1), L(-5, 7, 1), L(-3, 4, 1), L(-1, 3, 1), L(1, 3, 1),
                             L(3, 5, 1), L(5, 6, 1), L(7, 3, 5), L(39, 6, 2), L(43, 4, 5), L(75, 4, 6), L(139, 5, 7), L(267, 5, 8),
                             L(523, 6, 8), L(779, 7, 9), L(1291, 6, 11), Lower(-32, 9), Upper(3339, 9), Oob(2) },
                10 => new[] { L(-21, 7, 4), L(-5, 8, 0), L(-4, 7, 0), L(-3, 5, 0), L(-2, 2, 2), L(2, 5, 0), L(3, 6, 0), L(4, 7, 0),
                              L(5, 8, 0), L(6, 2, 6), L(70, 5, 5), L(102, 6, 5), L(134, 6, 6), L(198, 6, 7), L(326, 6, 8), L(582, 6, 9),
                              L(1094, 6, 10), L(2118, 7, 11), Lower(-22, 8), Upper(4166, 8), Oob(2) },
                11 => new[] { L(1, 1, 0), L(2, 2, 1), L(4, 4, 0), L(5, 4, 1), L(7, 5, 1), L(9, 5, 2), L(13, 6, 2), L(17, 7, 2),
                              L(21, 7, 3), L(29, 7, 4), L(45, 7, 5), L(77, 7, 6), Upper(141, 7) },
                12 => new[] { L(1, 1, 0), L(2, 2, 0), L(3, 3, 1), L(5, 5, 0), L(6, 5, 1), L(8, 6, 1), L(10, 7, 0), L(11, 7, 1),
                              L(13, 7, 2), L(17, 7, 3), L(25, 7, 4), L(41, 8, 5), Upper(73, 8) },
                13 => new[] { L(1, 1, 0), L(2, 3, 0), L(3, 4, 0), L(4, 5, 0), L(5, 4, 1), L(7, 3, 3), L(15, 6, 1), L(17, 6, 2),
                              L(21, 6, 3), L(29, 6, 4), L(45, 6, 5), L(77, 7, 6), Upper(141, 7) },
                14 => new[] { L(-2, 3, 0), L(-1, 3, 0), L(0, 1, 0), L(1, 3, 0), L(2, 3, 0) },
                15 => new[] { L(-24, 7, 4), L(-8, 6, 2), L(-4, 5, 1), L(-2, 4, 0), L(-1, 3, 0), L(0, 1, 0), L(1, 3, 0), L(2, 4, 0),
                              L(3, 5, 1), L(5, 6, 2), L(9, 7, 4), Lower(-25, 7), Upper(25, 7) },
                _ => throw new Jbig2Exception($"Standard Huffman table B.{number} does not exist."),
            });
        }
    }

    /// <summary>A user-supplied table from a tables segment (type 53, 7.4.13 / B.2).</summary>
    public static Jbig2HuffmanTable Parse(byte[] data, int start, int end)
    {
        if (end - start < 9) throw new Jbig2Exception("JBIG2 table segment is too short.");
        byte flags = data[start];
        bool hasOob = (flags & 1) != 0;
        int prefixBits = ((flags >> 1) & 7) + 1, rangeBits = ((flags >> 4) & 7) + 1;
        int low = BigEndian(data, start + 1), high = BigEndian(data, start + 5);
        var r = new Jbig2BitReader(data, start + 9, end);
        var lines = new List<Line>();
        int cur = low;
        while (cur < high)
        {
            int preflen = (int)r.ReadBits(prefixBits), rangelen = (int)r.ReadBits(rangeBits);
            lines.Add(new Line(preflen, rangelen, cur));
            if (rangelen > 31) throw new Jbig2Exception("JBIG2 table range too large.");
            cur += 1 << rangelen;
            if (lines.Count > 65536) throw new Jbig2Exception("JBIG2 table has too many lines.");
        }
        lines.Add(new Line((int)r.ReadBits(prefixBits), 32, low - 1, IsLower: true));
        lines.Add(new Line((int)r.ReadBits(prefixBits), 32, high));
        if (hasOob) lines.Add(new Line((int)r.ReadBits(prefixBits), 0, 0, IsOob: true));
        return new Jbig2HuffmanTable(lines);
    }

    internal static int BigEndian(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
}
