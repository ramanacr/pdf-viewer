using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Xref;

namespace PdfEngine.Vector.Tests.Fonts;

/// <summary>Small builders for PDF objects used by the font tests.</summary>
internal static class Pdf
{
    public static PdfObjectResolver Resolver() =>
        new(new MemoryByteSource(Array.Empty<byte>()), new PdfXrefTable());

    public static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new Dictionary<string, PdfObject>();
        foreach (var (k, v) in entries)
            d[k] = v;
        return new PdfDictionary(d);
    }

    public static PdfName N(string name) => new(name);

    public static PdfInteger I(long v) => new(v);

    public static PdfReal R(double v) => new(v);

    public static PdfArray A(params PdfObject[] items) => new(items);

    public static PdfArray Nums(params double[] values) =>
        new(values.Select(v => (PdfObject)(v == Math.Floor(v) ? new PdfInteger((long)v) : new PdfReal(v))).ToArray());

    public static PdfStream Stream(byte[] bytes, params (string Key, PdfObject Value)[] entries) =>
        new(Dict(entries), 0, bytes.Length, null, bytes);

    public static PdfStream Stream(string ascii, params (string Key, PdfObject Value)[] entries) =>
        Stream(Encoding.ASCII.GetBytes(ascii), entries);
}

/// <summary>Builds a minimal sfnt containing only a <c>cmap</c> table.</summary>
internal static class SfntBuilder
{
    public static byte[] Build(params (int Platform, int Encoding, byte[] Subtable)[] subtables)
    {
        var cmap = new MemoryStream();
        W16(cmap, 0);
        W16(cmap, subtables.Length);
        int offset = 4 + subtables.Length * 8;
        foreach (var (p, e, sub) in subtables)
        {
            W16(cmap, p);
            W16(cmap, e);
            W32(cmap, (uint)offset);
            offset += sub.Length;
        }
        foreach (var s in subtables)
            cmap.Write(s.Subtable);
        byte[] cmapBytes = cmap.ToArray();

        var font = new MemoryStream();
        W32(font, 0x00010000);
        W16(font, 1);
        W16(font, 16);
        W16(font, 0);
        W16(font, 0);
        font.Write("cmap"u8);
        W32(font, 0);
        W32(font, 28);
        W32(font, (uint)cmapBytes.Length);
        font.Write(cmapBytes);
        return font.ToArray();
    }

    public static byte[] Format0(IDictionary<int, int> map)
    {
        var s = new MemoryStream();
        W16(s, 0);
        W16(s, 262);
        W16(s, 0);
        for (int c = 0; c < 256; c++)
            s.WriteByte((byte)(map.TryGetValue(c, out int g) ? g : 0));
        return s.ToArray();
    }

    /// <summary>
    /// Format 4 with delta segments (start, end, firstGlyph) and optional array segments
    /// (start, glyphs[]) that use idRangeOffset.
    /// </summary>
    public static byte[] Format4((int Start, int End, int FirstGlyph)[] deltaSegs, (int Start, int[] Glyphs)[]? arraySegs = null)
    {
        arraySegs ??= Array.Empty<(int, int[])>();
        var segs = new List<(int Start, int End, int Delta, int[]? Glyphs)>();
        foreach (var (s, e, g) in deltaSegs)
            segs.Add((s, e, (g - s) & 0xFFFF, null));
        foreach (var (s, glyphs) in arraySegs)
            segs.Add((s, s + glyphs.Length - 1, 0, glyphs));
        segs.Sort((a, b) => a.End.CompareTo(b.End));
        segs.Add((0xFFFF, 0xFFFF, 1, null));
        int segCount = segs.Count;

        var glyphArray = new List<int>();
        var rangeOffsets = new int[segCount];
        for (int i = 0; i < segCount; i++)
        {
            if (segs[i].Glyphs is { } gl)
            {
                rangeOffsets[i] = (segCount - i) * 2 + glyphArray.Count * 2;
                glyphArray.AddRange(gl);
            }
        }

        var s2 = new MemoryStream();
        W16(s2, 4);
        W16(s2, 16 + segCount * 8 + glyphArray.Count * 2);
        W16(s2, 0);
        W16(s2, segCount * 2);
        W16(s2, 0); W16(s2, 0); W16(s2, 0);
        foreach (var seg in segs) W16(s2, seg.End);
        W16(s2, 0);
        foreach (var seg in segs) W16(s2, seg.Start);
        foreach (var seg in segs) W16(s2, seg.Delta);
        foreach (int ro in rangeOffsets) W16(s2, ro);
        foreach (int g in glyphArray) W16(s2, g);
        return s2.ToArray();
    }

    public static byte[] Format12(params (uint Start, uint End, uint FirstGlyph)[] groups)
    {
        var s = new MemoryStream();
        W16(s, 12);
        W16(s, 0);
        W32(s, (uint)(16 + groups.Length * 12));
        W32(s, 0);
        W32(s, (uint)groups.Length);
        foreach (var (st, en, g) in groups)
        {
            W32(s, st);
            W32(s, en);
            W32(s, g);
        }
        return s.ToArray();
    }

    private static void W16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void W32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
}
