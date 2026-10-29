using System;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// A small OpenType font with CFF outlines ('OTTO', a CID-keyed 'CFF ' table), built in code so
/// that no font file is committed. It stands in for Android's Noto Sans CJK (a CFF face in a
/// .ttc collection), so it carries that family name; its glyphs are plain boxes. Glyphs: 0
/// .notdef, 1 'A' (draws through a local subroutine), 2 '中' (hinted, draws through a global
/// subroutine), 3 space (no outline), 4 no character (calls subroutines of its own, which a
/// subset without it leaves out).
/// </summary>
internal static class CffOpenTypeFont
{
    public const string Family = "Noto Sans CJK SC";
    public const string PostScriptName = "SyntheticCjk-Regular";
    public static readonly int[] Advances = { 500, 600, 1000, 250, 700 };

    public static readonly byte[] UsedLocalSubr = new SyntheticCff.CharString().N(400, 0).Op(5).N(0, 700).Op(5).N(-400, 0).Op(5).Op(11).ToArray();
    public static readonly byte[] UsedGlobalSubr = new SyntheticCff.CharString().N(800, 0).Op(5).N(0, 800).Op(5).N(-800, 0).Op(5).Op(11).ToArray();
    public static readonly byte[] UnusedLocalSubr = new SyntheticCff.CharString().N(777, 777).Op(5).N(-777, 0).Op(5).Op(11).ToArray();
    public static readonly byte[] UnusedGlobalSubr = new SyntheticCff.CharString().N(999, 999).Op(5).N(-999, 0).Op(5).Op(11).ToArray();

    /// <summary>The bare CFF program: two Font DICTs (FD 0 with local subroutines), two global subroutines.</summary>
    public static byte[] Cff()
    {
        // Subroutine numbers are biased by 107 (fewer than 1240 subroutines): -107 calls 0, -106 calls 1.
        var latin = new SyntheticCff.CharString().N(50, 0).Op(21).N(-107).Op(10).Op(14).ToArray();
        // hstemhm with two stems, then a hintmask with one mask byte.
        var han = new SyntheticCff.CharString().N(0, 100, 500, 100).Op(18).Op(19).Raw(0xC0).N(100, 0).Op(21).N(-107).Op(29).Op(14).ToArray();
        var space = new SyntheticCff.CharString().Op(14).ToArray();
        var unused = new SyntheticCff.CharString().N(0, 0).Op(21).N(-106).Op(10).N(-106).Op(29).Op(14).ToArray();
        return SyntheticCff.CidKeyed(null,
            new[] { new SyntheticCff.FontDict(null, new[] { UsedLocalSubr, UnusedLocalSubr }), new SyntheticCff.FontDict(null) },
            new[]
            {
                new SyntheticCff.CidGlyph(101, 0, latin), new SyntheticCff.CidGlyph(102, 1, han),
                new SyntheticCff.CidGlyph(103, 0, space), new SyntheticCff.CidGlyph(104, 0, unused),
            },
            new[] { UsedGlobalSubr, UnusedGlobalSubr });
    }

    /// <summary>The OpenType font (1000 units per em).</summary>
    public static byte[] Font()
    {
        int n = Advances.Length;
        var hmtx = new byte[n * 4];
        for (int g = 0; g < n; g++) BigEndian.Put16(hmtx, g * 4, Advances[g]);
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["CFF "] = Cff(),
            ["head"] = Sfnt.Head(1000, 0, -200, 1000, 900, false, false),
            ["hhea"] = Sfnt.Hhea(900, -200, 1000, 1000, n),
            ["hmtx"] = hmtx,
            ["maxp"] = Sfnt.MaxpCff(n),
            ["cmap"] = Cmap((0x20, 3), (0x41, 1), (0x4E2D, 2)),
            ["OS/2"] = Sfnt.Os2(1000, 900, -200, false, false, 600),
            ["name"] = Sfnt.Name(Family, PostScriptName, false, false),
            ["post"] = Sfnt.Post(false),
        };
        return Sfnt.Write(Sfnt.OpenTypeCffVersion, tables);
    }

    /// <summary>A .ttc collection whose two fonts are both <paramref name="font"/>.</summary>
    public static byte[] Collection(byte[] font)
    {
        const int header = 12 + 2 * 4;
        var data = new byte[header + font.Length];
        "ttcf"u8.CopyTo(data);
        BigEndian.Put32(data, 4, 0x00010000);
        BigEndian.Put32(data, 8, 2);
        BigEndian.Put32(data, 12, header);
        BigEndian.Put32(data, 16, header);
        font.CopyTo(data, header);
        // Table offsets in a collection count from the start of the file.
        int tables = BigEndian.U16(font, 4);
        for (int i = 0; i < tables; i++)
        {
            int at = header + 12 + i * 16 + 8;
            BigEndian.Put32(data, at, BigEndian.U32(data, at) + header);
        }
        return data;
    }

    /// <summary>A cmap with one Windows Unicode BMP (3, 1) format 4 subtable.</summary>
    private static byte[] Cmap(params (int Code, int Gid)[] map)
    {
        var segments = new List<(int Code, int Gid)>(map) { (0xFFFF, 0) };
        int count = segments.Count;
        using var s = new MemoryStream();
        BigEndian.W16(s, 0); BigEndian.W16(s, 1);            // version, one subtable
        BigEndian.W16(s, 3); BigEndian.W16(s, 1); BigEndian.W32(s, 12);
        BigEndian.W16(s, 4); BigEndian.W16(s, 16 + count * 8); BigEndian.W16(s, 0); // format, length, language
        int search = 1, selector = 0;
        while (search * 2 <= count) { search *= 2; selector++; }
        BigEndian.W16(s, count * 2); BigEndian.W16(s, search * 2); BigEndian.W16(s, selector); BigEndian.W16(s, count * 2 - search * 2);
        foreach (var (code, _) in segments) BigEndian.W16(s, code);   // endCode
        BigEndian.W16(s, 0);                                          // reservedPad
        foreach (var (code, _) in segments) BigEndian.W16(s, code);   // startCode
        foreach (var (code, gid) in segments) BigEndian.W16(s, code == 0xFFFF ? 1 : (gid - code) & 0xFFFF); // idDelta
        foreach (var _ in segments) BigEndian.W16(s, 0);              // idRangeOffset
        return s.ToArray();
    }
}
