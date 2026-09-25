using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfEngine.Vector.Fonts.Programs;

/// <summary>Big-endian byte helpers for font tables. Reads are bounds-checked and never throw.</summary>
internal static class BigEndian
{
    public static int U16(ReadOnlySpan<byte> d, int o) => o >= 0 && o + 2 <= d.Length ? (d[o] << 8) | d[o + 1] : 0;
    public static short S16(ReadOnlySpan<byte> d, int o) => (short)U16(d, o);
    public static uint U32(ReadOnlySpan<byte> d, int o) =>
        o >= 0 && o + 4 <= d.Length ? ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3] : 0;

    public static void W16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    public static void W32(Stream s, uint v) { W16(s, (int)(v >> 16)); W16(s, (int)(v & 0xFFFF)); }
    public static void Put16(byte[] d, int o, int v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
    public static void Put32(byte[] d, int o, uint v) { Put16(d, o, (int)(v >> 16)); Put16(d, o + 2, (int)(v & 0xFFFF)); }
}

/// <summary>
/// Minimal sfnt (TrueType / OpenType) container reader and writer, plus generators for the
/// bookkeeping tables platform rasterizers insist on. Embedded PDF font programs are routinely
/// subset without 'name', 'OS/2', 'post' or 'cmap' — valid for PDF (ISO 32000-2 9.9) but rejected
/// by WPF/DirectWrite.
/// </summary>
internal static class Sfnt
{
    public const uint TrueTypeVersion = 0x00010000;
    public const uint OpenTypeCffVersion = 0x4F54544F; // 'OTTO'

    /// <summary>Tables of an sfnt (first font of a collection). Null when the directory is invalid.</summary>
    public static Dictionary<string, byte[]>? ReadTables(byte[] font, out uint version)
    {
        version = 0;
        if (font.Length < 12)
            return null;
        int dir = 0;
        if (BigEndian.U32(font, 0) == 0x74746366) // 'ttcf'
            dir = (int)Math.Min(BigEndian.U32(font, 12), int.MaxValue);
        version = BigEndian.U32(font, dir);
        int numTables = BigEndian.U16(font, dir + 4);
        if (numTables == 0 || numTables > 256 || dir + 12 + numTables * 16 > font.Length)
            return null;

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            int rec = dir + 12 + i * 16;
            string tag = Encoding.ASCII.GetString(font, rec, 4);
            long offset = BigEndian.U32(font, rec + 8);
            long length = BigEndian.U32(font, rec + 12);
            if (offset < 0 || length < 0 || offset + length > font.Length)
            {
                // Truncated subsets: keep what exists rather than failing the whole font.
                if (offset >= font.Length) continue;
                length = font.Length - offset;
            }
            tables[tag] = font.AsSpan((int)offset, (int)length).ToArray();
        }
        return tables;
    }

    /// <summary>Writes tables with correct directory, padding, checksums and head.checkSumAdjustment.</summary>
    public static byte[] Write(uint version, IReadOnlyDictionary<string, byte[]> tables)
    {
        var tags = new List<string>(tables.Keys);
        tags.Sort(StringComparer.Ordinal);
        int n = tags.Count;
        int entrySelector = 0;
        while ((1 << (entrySelector + 1)) <= n) entrySelector++;
        int searchRange = (1 << entrySelector) * 16;

        using var ms = new MemoryStream();
        BigEndian.W32(ms, version);
        BigEndian.W16(ms, n);
        BigEndian.W16(ms, searchRange);
        BigEndian.W16(ms, entrySelector);
        BigEndian.W16(ms, n * 16 - searchRange);

        long offset = 12 + n * 16;
        var offsets = new long[n];
        for (int i = 0; i < n; i++)
        {
            offsets[i] = offset;
            offset += (tables[tags[i]].Length + 3) & ~3;
        }

        int headIndex = -1;
        for (int i = 0; i < n; i++)
        {
            byte[] data = tables[tags[i]];
            if (tags[i] == "head")
            {
                headIndex = i;
                if (data.Length >= 12) BigEndian.Put32(data, 8, 0); // checksum excludes the adjustment
            }
            ms.Write(Encoding.ASCII.GetBytes(tags[i]));
            BigEndian.W32(ms, Checksum(data));
            BigEndian.W32(ms, (uint)offsets[i]);
            BigEndian.W32(ms, (uint)data.Length);
        }
        foreach (var tag in tags)
        {
            byte[] data = tables[tag];
            ms.Write(data);
            for (int p = data.Length; (p & 3) != 0; p++) ms.WriteByte(0);
        }

        byte[] result = ms.ToArray();
        if (headIndex >= 0 && tables["head"].Length >= 12)
        {
            uint adjust = 0xB1B0AFBA - Checksum(result);
            BigEndian.Put32(result, (int)offsets[headIndex] + 8, adjust);
        }
        return result;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
            sum += BigEndian.U32(data, i);
        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            data[i..].CopyTo(tail);
            sum += BigEndian.U32(tail, 0);
        }
        return sum;
    }

    // ------------------------------------------------------------------ minimal tables

    public static byte[] Head(int unitsPerEm, short xMin, short yMin, short xMax, short yMax, bool bold, bool italic, int indexToLocFormat = 0)
    {
        var d = new byte[54];
        BigEndian.Put32(d, 0, 0x00010000);        // version
        BigEndian.Put32(d, 4, 0x00010000);        // fontRevision
        BigEndian.Put32(d, 12, 0x5F0F3CF5);       // magicNumber
        BigEndian.Put16(d, 16, 0x000B);           // flags: baseline/lsb at 0, integer ppem
        BigEndian.Put16(d, 18, unitsPerEm);
        // created / modified (28..43) left zero
        BigEndian.Put16(d, 36, xMin);
        BigEndian.Put16(d, 38, yMin);
        BigEndian.Put16(d, 40, xMax);
        BigEndian.Put16(d, 42, yMax);
        BigEndian.Put16(d, 44, (bold ? 1 : 0) | (italic ? 2 : 0)); // macStyle
        BigEndian.Put16(d, 46, 8);                // lowestRecPPEM
        BigEndian.Put16(d, 48, 2);                // fontDirectionHint
        BigEndian.Put16(d, 50, indexToLocFormat);
        return d;
    }

    public static byte[] Hhea(short ascender, short descender, int advanceMax, short xMax, int numberOfHMetrics)
    {
        var d = new byte[36];
        BigEndian.Put32(d, 0, 0x00010000);
        BigEndian.Put16(d, 4, ascender);
        BigEndian.Put16(d, 6, descender);
        BigEndian.Put16(d, 10, advanceMax);
        BigEndian.Put16(d, 16, xMax);             // xMaxExtent
        BigEndian.Put16(d, 18, 1);                // caretSlopeRise
        BigEndian.Put16(d, 34, Math.Max(1, numberOfHMetrics));
        return d;
    }

    public static byte[] Hmtx(int numGlyphs, int advance)
    {
        var d = new byte[Math.Max(1, numGlyphs) * 4];
        for (int i = 0; i < Math.Max(1, numGlyphs); i++)
            BigEndian.Put16(d, i * 4, advance);
        return d;
    }

    public static byte[] MaxpCff(int numGlyphs)
    {
        var d = new byte[6];
        BigEndian.Put32(d, 0, 0x00005000);
        BigEndian.Put16(d, 4, numGlyphs);
        return d;
    }

    /// <summary>Format 3 'post' (no glyph names): all a rasterizer needs.</summary>
    public static byte[] Post(bool fixedPitch)
    {
        var d = new byte[32];
        BigEndian.Put32(d, 0, 0x00030000);
        BigEndian.Put16(d, 8, unchecked((short)-100)); // underlinePosition
        BigEndian.Put16(d, 10, 50);                     // underlineThickness
        BigEndian.Put32(d, 12, fixedPitch ? 1u : 0u);
        return d;
    }

    /// <summary>
    /// A (3,1) Unicode cmap mapping private-use U+E000+gid → gid. The viewer draws embedded glyphs
    /// by index and never looks characters up, but WPF's font loader requires a non-empty Unicode
    /// map (an empty or symbol-only cmap throws inside FontFaceLayoutInfo).
    /// </summary>
    public static byte[] PrivateUseCmap(int numGlyphs)
    {
        int n = Math.Clamp(numGlyphs, 1, 0x1900); // stay inside the BMP private use area E000–F8FF
        const int segCount = 2;
        var d = new byte[4 + 8 + 16 + segCount * 8];
        BigEndian.Put16(d, 0, 0);   // version
        BigEndian.Put16(d, 2, 1);   // numTables
        BigEndian.Put16(d, 4, 3);   // platform: Windows
        BigEndian.Put16(d, 6, 1);   // encoding: Unicode BMP
        BigEndian.Put32(d, 8, 12);  // offset
        int s = 12;
        int length = 16 + segCount * 8;
        BigEndian.Put16(d, s, 4);                 // format
        BigEndian.Put16(d, s + 2, length);
        BigEndian.Put16(d, s + 6, segCount * 2);  // segCountX2
        BigEndian.Put16(d, s + 8, 4);             // searchRange = 2 * 2^floor(log2(segCount))
        BigEndian.Put16(d, s + 10, 1);            // entrySelector
        BigEndian.Put16(d, s + 12, 0);            // rangeShift
        int end = s + 14, start = end + segCount * 2 + 2, delta = start + segCount * 2, rangeOffset = delta + segCount * 2;
        BigEndian.Put16(d, end, 0xE000 + n - 1);
        BigEndian.Put16(d, end + 2, 0xFFFF);
        BigEndian.Put16(d, start, 0xE000);
        BigEndian.Put16(d, start + 2, 0xFFFF);
        BigEndian.Put16(d, delta, (0x10000 - 0xE000) & 0xFFFF); // gid = code - 0xE000 (mod 65536)
        BigEndian.Put16(d, delta + 2, 1);
        BigEndian.Put16(d, rangeOffset, 0);
        BigEndian.Put16(d, rangeOffset + 2, 0);
        return d;
    }

    public static byte[] Name(string family, string postScriptName, bool bold, bool italic)
    {
        string style = bold ? (italic ? "Bold Italic" : "Bold") : (italic ? "Italic" : "Regular");
        var records = new (int Id, string Value)[]
        {
            (1, family), (2, style), (3, postScriptName + ";pdf-embedded"), (4, family + " " + style), (5, "Version 1.0"), (6, postScriptName),
        };
        using var strings = new MemoryStream();
        var entries = new List<(int Platform, int Encoding, int Language, int Id, int Offset, int Length)>();
        foreach (var (id, value) in records)
        {
            byte[] mac = Encoding.ASCII.GetBytes(Ascii(value));
            entries.Add((1, 0, 0, id, (int)strings.Length, mac.Length));
            strings.Write(mac);
        }
        foreach (var (id, value) in records)
        {
            byte[] win = Encoding.BigEndianUnicode.GetBytes(Ascii(value));
            entries.Add((3, 1, 0x409, id, (int)strings.Length, win.Length));
            strings.Write(win);
        }

        using var ms = new MemoryStream();
        BigEndian.W16(ms, 0);
        BigEndian.W16(ms, entries.Count);
        BigEndian.W16(ms, 6 + entries.Count * 12);
        foreach (var e in entries)
        {
            BigEndian.W16(ms, e.Platform);
            BigEndian.W16(ms, e.Encoding);
            BigEndian.W16(ms, e.Language);
            BigEndian.W16(ms, e.Id);
            BigEndian.W16(ms, e.Length);
            BigEndian.W16(ms, e.Offset);
        }
        strings.Position = 0;
        strings.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c is >= (char)33 and <= (char)126 && c is not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%') ? c : c == ' ' ? ' ' : '_');
        string r = sb.ToString().Trim();
        return r.Length == 0 ? "PdfEmbedded" : r.Length > 63 ? r[..63] : r;
    }

    public static byte[] Os2(int unitsPerEm, short ascender, short descender, bool bold, bool italic, int avgWidth)
    {
        var d = new byte[96]; // version 4
        BigEndian.Put16(d, 0, 4);
        BigEndian.Put16(d, 2, avgWidth);
        BigEndian.Put16(d, 4, bold ? 700 : 400);          // usWeightClass
        BigEndian.Put16(d, 6, 5);                         // usWidthClass: medium
        BigEndian.Put16(d, 8, 0);                         // fsType: installable
        int sub = unitsPerEm * 65 / 100, off = unitsPerEm * 14 / 100;
        BigEndian.Put16(d, 10, sub); BigEndian.Put16(d, 12, sub); BigEndian.Put16(d, 16, off);   // subscript
        BigEndian.Put16(d, 18, sub); BigEndian.Put16(d, 20, sub); BigEndian.Put16(d, 24, unitsPerEm * 48 / 100); // superscript
        BigEndian.Put16(d, 26, unitsPerEm / 20);          // yStrikeoutSize
        BigEndian.Put16(d, 28, unitsPerEm * 26 / 100);    // yStrikeoutPosition
        Encoding.ASCII.GetBytes("PDFV").CopyTo(d, 58);    // achVendID
        int fsSelection = (italic ? 0x01 : 0) | (bold ? 0x20 : 0);
        if (fsSelection == 0) fsSelection = 0x40;         // REGULAR
        BigEndian.Put16(d, 62, fsSelection);
        BigEndian.Put16(d, 64, 0x20);                     // usFirstCharIndex
        BigEndian.Put16(d, 66, 0xFFFF);                   // usLastCharIndex
        BigEndian.Put16(d, 68, ascender);                 // sTypoAscender
        BigEndian.Put16(d, 70, descender);                // sTypoDescender
        BigEndian.Put16(d, 72, unitsPerEm / 10);          // sTypoLineGap
        BigEndian.Put16(d, 74, Math.Max(0, (int)ascender)); // usWinAscent
        BigEndian.Put16(d, 76, Math.Max(0, -(int)descender)); // usWinDescent
        BigEndian.Put32(d, 78, 1);                        // ulCodePageRange1: Latin 1
        BigEndian.Put16(d, 86, unitsPerEm / 2);           // sxHeight
        BigEndian.Put16(d, 88, unitsPerEm * 7 / 10);      // sCapHeight
        BigEndian.Put16(d, 92, 0x20);                     // usBreakChar
        BigEndian.Put16(d, 94, 1);                        // usMaxContext
        return d;
    }
}
