using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PdfEngine.Vector.Fonts.Programs;

/// <summary>
/// Read-only view of a bare CFF font program (Adobe TN #5176): enough to map PDF character codes
/// and CIDs to glyph indices and to wrap the program in an OpenType container. Parsing is
/// bounds-checked; <see cref="TryParse"/> returns null rather than throwing on hostile data.
/// </summary>
internal sealed class CffFont
{
    public byte[] Data { get; }
    public string FontName { get; private init; } = "CFF";
    public int GlyphCount { get; private init; }
    public bool IsCidKeyed { get; private init; }
    public double[] FontMatrix { get; private init; } = { 0.001, 0, 0, 0.001, 0, 0 };
    public double[] FontBBox { get; private init; } = { 0, -200, 1000, 800 };

    /// <summary>Glyph index → glyph name (name-keyed) or CID (CID-keyed, as a number string).</summary>
    private int[] _charset = Array.Empty<int>();
    private string[] _strings = Array.Empty<string>();
    private Dictionary<string, int>? _nameToGid;
    private Dictionary<int, int>? _cidToGid;

    /// <summary>Built-in encoding: code → glyph index (from a custom encoding, or StandardEncoding by name).</summary>
    private int[]? _encoding;

    private CffFont(byte[] data) => Data = data;

    public static CffFont? TryParse(byte[] data)
    {
        try
        {
            return Parse(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException
                                       or InvalidDataException or OverflowException or FormatException)
        {
            return null;
        }
    }

    private static CffFont? Parse(byte[] d)
    {
        if (d.Length < 4 || d[0] != 1)
            return null;
        int pos = d[2]; // hdrSize
        var names = ReadIndex(d, ref pos);
        var topDicts = ReadIndex(d, ref pos);
        var strings = ReadIndex(d, ref pos);
        ReadIndex(d, ref pos); // global subrs (kept in place)
        if (names.Count == 0 || topDicts.Count == 0)
            return null;

        var top = ParseDict(d, topDicts[0].Offset, topDicts[0].Length);
        var stringTable = new string[strings.Count];
        for (int i = 0; i < strings.Count; i++)
            stringTable[i] = Encoding.Latin1.GetString(d, strings[i].Offset, strings[i].Length);

        if (!top.TryGetValue(17, out var cs) || cs.Length < 1)
            return null;
        int charStringsPos = (int)cs[0];
        var charStrings = ReadIndex(d, ref charStringsPos);
        int nGlyphs = charStrings.Count;
        if (nGlyphs == 0)
            return null;

        bool cid = top.ContainsKey(1230); // ROS (12 30)
        var font = new CffFont(d)
        {
            FontName = Encoding.Latin1.GetString(d, names[0].Offset, names[0].Length),
            GlyphCount = nGlyphs,
            IsCidKeyed = cid,
            FontMatrix = top.TryGetValue(1207, out var fm) && fm.Length >= 6 ? fm[..6] : new[] { 0.001, 0, 0, 0.001, 0, 0 },
            FontBBox = top.TryGetValue(5, out var bb) && bb.Length >= 4 ? bb[..4] : new double[] { 0, -200, 1000, 800 },
            _strings = stringTable,
        };

        int charsetOffset = top.TryGetValue(15, out var co) && co.Length > 0 ? (int)co[0] : 0;
        font._charset = ReadCharset(d, charsetOffset, nGlyphs, cid);

        if (!cid)
        {
            int encOffset = top.TryGetValue(16, out var eo) && eo.Length > 0 ? (int)eo[0] : 0;
            font._encoding = font.ReadEncoding(d, encOffset);
        }
        return font;
    }

    public string GlyphName(int gid)
    {
        if (IsCidKeyed || gid < 0 || gid >= _charset.Length)
            return string.Empty;
        return Sid(_charset[gid]);
    }

    private string Sid(int sid) =>
        sid < CffStandardStrings.Names.Length ? CffStandardStrings.Names[sid]
        : sid - CffStandardStrings.Names.Length < _strings.Length ? _strings[sid - CffStandardStrings.Names.Length] : string.Empty;

    /// <summary>Glyph index for a glyph name (name-keyed fonts); -1 when absent.</summary>
    public int GidForName(string name)
    {
        if (IsCidKeyed) return -1;
        if (_nameToGid == null)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int g = 0; g < _charset.Length; g++)
                map.TryAdd(Sid(_charset[g]), g);
            _nameToGid = map;
        }
        return _nameToGid.TryGetValue(name, out int gid) ? gid : -1;
    }

    /// <summary>Glyph index for a CID (CID-keyed: via the charset; name-keyed: the CID is the index).</summary>
    public int GidForCid(int cid)
    {
        if (!IsCidKeyed)
            return cid >= 0 && cid < GlyphCount ? cid : 0;
        if (_cidToGid == null)
        {
            var map = new Dictionary<int, int>();
            for (int g = 0; g < _charset.Length; g++)
                map.TryAdd(_charset[g], g);
            _cidToGid = map;
        }
        return _cidToGid.TryGetValue(cid, out int gid) ? gid : 0;
    }

    /// <summary>Glyph index through the font's built-in encoding; -1 when the code is unmapped.</summary>
    public int GidForBuiltInCode(int code) =>
        _encoding != null && code >= 0 && code < _encoding.Length && _encoding[code] > 0 ? _encoding[code] : -1;

    // ------------------------------------------------------------------ structures

    internal readonly record struct IndexEntry(int Offset, int Length);

    internal static List<IndexEntry> ReadIndex(byte[] d, ref int pos)
    {
        var list = new List<IndexEntry>();
        int count = BigEndian.U16(d, pos);
        if (pos + 2 > d.Length)
            throw new InvalidDataException("CFF INDEX beyond data");
        pos += 2;
        if (count == 0)
            return list;
        int offSize = d[pos++];
        if (offSize is < 1 or > 4)
            throw new InvalidDataException("CFF INDEX offSize");
        int offsetsStart = pos;
        long dataStart = offsetsStart + (long)(count + 1) * offSize - 1;
        if (dataStart > d.Length)
            throw new InvalidDataException("CFF INDEX offsets beyond data");
        long prev = ReadOffset(d, offsetsStart, offSize);
        for (int i = 1; i <= count; i++)
        {
            long next = ReadOffset(d, offsetsStart + i * offSize, offSize);
            if (next < prev || dataStart + next > d.Length)
                throw new InvalidDataException("CFF INDEX offset order");
            list.Add(new IndexEntry((int)(dataStart + prev), (int)(next - prev)));
            prev = next;
        }
        pos = (int)(dataStart + prev);
        return list;
    }

    private static long ReadOffset(byte[] d, int pos, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | d[pos + i];
        return v;
    }

    /// <summary>DICT operator → operands. Escaped operators are keyed 1200 + second byte.</summary>
    internal static Dictionary<int, double[]> ParseDict(byte[] d, int offset, int length)
    {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>();
        int end = offset + length;
        int p = offset;
        while (p < end)
        {
            int b0 = d[p];
            if (b0 <= 21)
            {
                int op = b0;
                p++;
                if (b0 == 12) op = 1200 + d[p++];
                result[op] = operands.ToArray();
                operands.Clear();
            }
            else if (b0 == 28) { operands.Add((short)((d[p + 1] << 8) | d[p + 2])); p += 3; }
            else if (b0 == 29) { operands.Add((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]); p += 5; }
            else if (b0 == 30) { operands.Add(ReadReal(d, ref p)); }
            else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
            else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + d[p + 1] + 108); p += 2; }
            else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - d[p + 1] - 108); p += 2; }
            else p++; // reserved
            if (operands.Count > 48) throw new InvalidDataException("CFF DICT operand overflow");
        }
        return result;
    }

    private static double ReadReal(byte[] d, ref int p)
    {
        p++;
        var sb = new StringBuilder();
        while (p < d.Length)
        {
            int b = d[p++];
            foreach (int nibble in new[] { b >> 4, b & 0xF })
            {
                switch (nibble)
                {
                    case <= 9: sb.Append((char)('0' + nibble)); break;
                    case 0xA: sb.Append('.'); break;
                    case 0xB: sb.Append('E'); break;
                    case 0xC: sb.Append("E-"); break;
                    case 0xE: sb.Append('-'); break;
                    case 0xF:
                        return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                }
            }
            if (sb.Length > 64) break;
        }
        return 0;
    }

    private static int[] ReadCharset(byte[] d, int offset, int nGlyphs, bool cid)
    {
        var charset = new int[nGlyphs];
        if (offset is 0 or 1 or 2)
        {
            // Predefined: ISOAdobe (SID == gid for the first 229 glyphs). Expert sets are rare in PDFs;
            // treat them like ISOAdobe rather than guessing.
            for (int g = 0; g < nGlyphs; g++) charset[g] = g;
            return charset;
        }

        int p = offset;
        int format = d[p++];
        int gid = 1;
        switch (format)
        {
            case 0:
                for (; gid < nGlyphs; gid++, p += 2) charset[gid] = BigEndian.U16(d, p);
                break;
            case 1:
            case 2:
                while (gid < nGlyphs && p < d.Length)
                {
                    int first = BigEndian.U16(d, p);
                    int left = format == 1 ? d[p + 2] : BigEndian.U16(d, p + 2);
                    p += format == 1 ? 3 : 4;
                    for (int i = 0; i <= left && gid < nGlyphs; i++) charset[gid++] = first + i;
                }
                break;
            default:
                throw new InvalidDataException("CFF charset format");
        }
        return charset;
    }

    private int[] ReadEncoding(byte[] d, int offset)
    {
        var enc = new int[256];
        if (offset is 0 or 1)
        {
            // Standard (0) / Expert (1): code → SID → glyph name → gid.
            for (int code = 0; code < 256; code++)
            {
                int sid = offset == 0 ? CffStandardStrings.StandardEncoding[code] : 0;
                enc[code] = sid > 0 ? Math.Max(0, GidForName(Sid(sid))) : 0;
            }
            return enc;
        }

        int p = offset;
        int format = d[p++];
        switch (format & 0x7F)
        {
            case 0:
            {
                int n = d[p++];
                for (int i = 1; i <= n && i < GlyphCount; i++) enc[d[p++]] = i;
                break;
            }
            case 1:
            {
                int nRanges = d[p++];
                int gid = 1;
                for (int r = 0; r < nRanges; r++)
                {
                    int first = d[p++], left = d[p++];
                    for (int i = 0; i <= left && gid < GlyphCount; i++)
                    {
                        if (first + i < 256) enc[first + i] = gid;
                        gid++;
                    }
                }
                break;
            }
            default:
                throw new InvalidDataException("CFF encoding format");
        }
        if ((format & 0x80) != 0)
        {
            int nSups = d[p++];
            for (int i = 0; i < nSups; i++, p += 3)
            {
                int code = d[p];
                int g = GidForName(Sid(BigEndian.U16(d, p + 1)));
                if (g > 0) enc[code] = g;
            }
        }
        return enc;
    }
}

/// <summary>Writes a minimal name-keyed CFF (used for Type1 → CFF conversion).</summary>
internal static class CffWriter
{
    /// <param name="glyphNames">Glyph order; index 0 must be ".notdef".</param>
    /// <param name="charStrings">Type 2 charstrings in glyph order.</param>
    public static byte[] Write(string fontName, IReadOnlyList<string> glyphNames, IReadOnlyList<byte[]> charStrings, double[] fontMatrix, double[] fontBBox)
    {
        // Custom strings: glyph names not among the standard strings.
        var standard = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < CffStandardStrings.Names.Length; i++) standard.TryAdd(CffStandardStrings.Names[i], i);
        var custom = new List<string>();
        var customIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int SidOf(string name)
        {
            if (standard.TryGetValue(name, out int sid)) return sid;
            if (!customIndex.TryGetValue(name, out int idx))
            {
                idx = custom.Count;
                custom.Add(name);
                customIndex[name] = idx;
            }
            return CffStandardStrings.Names.Length + idx;
        }
        var sids = new int[glyphNames.Count];
        for (int g = 1; g < glyphNames.Count; g++) sids[g] = SidOf(glyphNames[g]);

        byte[] nameIndex = Index(new[] { Encoding.Latin1.GetBytes(fontName) });
        byte[] stringIndex = Index(custom.ConvertAll(s => Encoding.Latin1.GetBytes(s)));
        byte[] gsubrIndex = Index(Array.Empty<byte[]>());
        byte[] charStringsIndex = Index(charStrings);

        using var charset = new MemoryStream();
        charset.WriteByte(0);
        for (int g = 1; g < sids.Length; g++) BigEndian.W16(charset, sids[g]);
        byte[] charsetBytes = charset.ToArray();

        // Private DICT: widths come from the PDF, so defaults of zero are fine.
        byte[] privateDict = Dict(w =>
        {
            w.Int(0); w.Op(20); // defaultWidthX
            w.Int(0); w.Op(21); // nominalWidthX
        });

        // The Top DICT holds offsets to data that follows it; 5-byte integers make its size fixed.
        byte[] BuildTop(int charsetOff, int charStringsOff, int privateOff) => Dict(w =>
        {
            if (fontMatrix.Length >= 6 && !(fontMatrix[0] == 0.001 && fontMatrix[1] == 0 && fontMatrix[2] == 0 && fontMatrix[3] == 0.001 && fontMatrix[4] == 0 && fontMatrix[5] == 0))
            {
                foreach (double v in fontMatrix[..6]) w.Real(v);
                w.Op(12, 7);
            }
            foreach (double v in fontBBox) w.Int((int)Math.Round(v));
            w.Op(5);
            w.Int5(charsetOff); w.Op(15);
            w.Int5(charStringsOff); w.Op(17);
            w.Int5(privateDict.Length); w.Int5(privateOff); w.Op(18);
        });

        int topSize = Index(new[] { BuildTop(0, 0, 0) }).Length;
        int headerAndIndexes = 4 + nameIndex.Length + topSize + stringIndex.Length + gsubrIndex.Length;
        int charsetOff = headerAndIndexes;
        int charStringsOff = charsetOff + charsetBytes.Length;
        int privateOff = charStringsOff + charStringsIndex.Length;
        byte[] topIndex = Index(new[] { BuildTop(charsetOff, charStringsOff, privateOff) });

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 1, 0, 4, 4 });
        ms.Write(nameIndex);
        ms.Write(topIndex);
        ms.Write(stringIndex);
        ms.Write(gsubrIndex);
        ms.Write(charsetBytes);
        ms.Write(charStringsIndex);
        ms.Write(privateDict);
        return ms.ToArray();
    }

    private static byte[] Index(IReadOnlyList<byte[]> items)
    {
        using var ms = new MemoryStream();
        BigEndian.W16(ms, items.Count);
        if (items.Count == 0)
            return ms.ToArray();
        ms.WriteByte(4); // offSize
        uint off = 1;
        BigEndian.W32(ms, off);
        foreach (var it in items)
        {
            off += (uint)it.Length;
            BigEndian.W32(ms, off);
        }
        foreach (var it in items) ms.Write(it);
        return ms.ToArray();
    }

    private sealed class DictWriter
    {
        public readonly MemoryStream S = new();
        public void Op(int op) => S.WriteByte((byte)op);
        public void Op(int esc, int op) { S.WriteByte((byte)esc); S.WriteByte((byte)op); }
        public void Int5(int v) { S.WriteByte(29); BigEndian.W32(S, (uint)v); }
        public void Int(int v)
        {
            if (v is >= -107 and <= 107) S.WriteByte((byte)(v + 139));
            else if (v is >= 108 and <= 1131) { v -= 108; S.WriteByte((byte)((v >> 8) + 247)); S.WriteByte((byte)v); }
            else if (v is >= -1131 and <= -108) { v = -v - 108; S.WriteByte((byte)((v >> 8) + 251)); S.WriteByte((byte)v); }
            else if (v is >= short.MinValue and <= short.MaxValue) { S.WriteByte(28); BigEndian.W16(S, v); }
            else Int5(v);
        }
        public void Real(double v)
        {
            string s = v.ToString("0.#########", CultureInfo.InvariantCulture);
            var nibbles = new List<int>();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c is >= '0' and <= '9') nibbles.Add(c - '0');
                else if (c == '.') nibbles.Add(0xA);
                else if (c == '-') nibbles.Add(0xE);
                else if (c is 'E' or 'e')
                {
                    if (i + 1 < s.Length && s[i + 1] == '-') { nibbles.Add(0xC); i++; } else nibbles.Add(0xB);
                }
            }
            nibbles.Add(0xF);
            if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
            S.WriteByte(30);
            for (int i = 0; i < nibbles.Count; i += 2) S.WriteByte((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }
    }

    private static byte[] Dict(Action<DictWriter> build)
    {
        var w = new DictWriter();
        build(w);
        return w.S.ToArray();
    }
}
