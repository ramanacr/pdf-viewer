using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// A TrueType (glyf-outline) font read from a file or collection: its metrics, its character map,
/// and subsetting to the glyphs a document uses. Subsets keep glyph ids (unused glyphs become
/// empty), so a Type0 font can use CIDToGIDMap /Identity with CID = glyph id.
/// </summary>
public sealed class TrueTypeFontFile
{
    private readonly Dictionary<string, byte[]> _tables;
    private readonly TrueTypeCmap? _cmap;
    private readonly int[] _locaOffsets;
    private readonly int[] _advances;

    public string FamilyName { get; }
    public string SubfamilyName { get; }
    public string PostScriptName { get; }
    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public int Ascender { get; }
    public int Descender { get; }
    public int CapHeight { get; }
    public int Weight { get; }
    public bool IsBold { get; }
    public bool IsItalic { get; }
    public bool IsFixedPitch { get; }
    public bool IsSerif { get; }
    public double ItalicAngle { get; }
    public (int XMin, int YMin, int XMax, int YMax) BBox { get; }
    /// <summary>The font's licence allows embedding (OS/2 fsType is not "restricted licence").</summary>
    public bool EmbeddingAllowed { get; }

    private TrueTypeFontFile(Dictionary<string, byte[]> tables)
    {
        _tables = tables;
        var head = tables["head"];
        UnitsPerEm = Math.Max(16, BigEndian.U16(head, 18));
        BBox = (BigEndian.S16(head, 36), BigEndian.S16(head, 38), BigEndian.S16(head, 40), BigEndian.S16(head, 42));
        int macStyle = BigEndian.U16(head, 44);
        bool longLoca = BigEndian.S16(head, 50) == 1;
        GlyphCount = BigEndian.U16(tables["maxp"], 4);

        var hhea = tables["hhea"];
        int ascender = BigEndian.S16(hhea, 4), descender = BigEndian.S16(hhea, 6);
        int metrics = Math.Max(1, BigEndian.U16(hhea, 34));
        var hmtx = tables["hmtx"];
        _advances = new int[GlyphCount];
        int last = 0;
        for (int g = 0; g < GlyphCount; g++)
        {
            if (g < metrics && g * 4 + 2 <= hmtx.Length) last = BigEndian.U16(hmtx, g * 4);
            _advances[g] = last;
        }

        var loca = tables["loca"];
        _locaOffsets = new int[GlyphCount + 1];
        for (int g = 0; g <= GlyphCount; g++)
            _locaOffsets[g] = longLoca ? (int)Math.Min(BigEndian.U32(loca, g * 4), int.MaxValue) : BigEndian.U16(loca, g * 2) * 2;

        int weight = 400, capHeight = 0, fsType = 0, fsSelection = 0, panoseFamily = 0, panoseSerif = 0;
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            weight = BigEndian.U16(os2, 4);
            fsType = BigEndian.U16(os2, 8);
            panoseFamily = os2[32];
            panoseSerif = os2[33];
            fsSelection = BigEndian.U16(os2, 62);
            // Typographic metrics when USE_TYPO_METRICS says so; otherwise hhea's.
            if ((fsSelection & 0x80) != 0) { ascender = BigEndian.S16(os2, 68); descender = BigEndian.S16(os2, 70); }
            if (os2.Length >= 90 && BigEndian.U16(os2, 0) >= 2) capHeight = BigEndian.S16(os2, 88);
        }
        Ascender = ascender;
        Descender = descender;
        CapHeight = capHeight > 0 ? capHeight : (int)(ascender * 0.7);
        Weight = weight;
        IsBold = (macStyle & 1) != 0 || (fsSelection & 0x20) != 0 || weight >= 600;
        IsItalic = (macStyle & 2) != 0 || (fsSelection & 0x01) != 0;
        // Bit 1 of fsType: restricted licence embedding (no embedding at all).
        EmbeddingAllowed = (fsType & 0x000F) != 0x0002;
        // PANOSE Latin text: serif styles 2..10 are serifs, 11+ sans.
        IsSerif = panoseFamily == 2 && panoseSerif is >= 2 and <= 10;

        if (tables.TryGetValue("post", out var post) && post.Length >= 16)
        {
            ItalicAngle = (int)BigEndian.U32(post, 4) / 65536.0;
            IsFixedPitch = BigEndian.U32(post, 12) != 0;
        }

        FamilyName = NameString(tables, 16) ?? NameString(tables, 1) ?? "Font";
        SubfamilyName = NameString(tables, 17) ?? NameString(tables, 2) ?? "Regular";
        PostScriptName = Sanitize(NameString(tables, 6) ?? (FamilyName + "-" + SubfamilyName));

        var sfnt = Sfnt.Write(Sfnt.TrueTypeVersion, new Dictionary<string, byte[]> { ["cmap"] = tables.TryGetValue("cmap", out var c) ? c : Array.Empty<byte>(), ["head"] = (byte[])head.Clone() });
        _cmap = TrueTypeCmap.TryParse(sfnt);
    }

    /// <summary>Reads font <paramref name="index"/> of a TrueType file or collection; null when it is not a glyf-outline font.</summary>
    public static TrueTypeFontFile? TryLoad(byte[] data, int index = 0)
    {
        try
        {
            var tables = ReadTables(data, index);
            if (tables == null) return null;
            foreach (var required in new[] { "head", "hhea", "hmtx", "maxp", "loca", "glyf", "cmap" })
                if (!tables.ContainsKey(required)) return null;
            if (tables["head"].Length < 54 || tables["hhea"].Length < 36 || tables["maxp"].Length < 6) return null;
            return new TrueTypeFontFile(tables);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Number of fonts in a file (a collection has several).</summary>
    public static int FontCount(byte[] data) =>
        data.Length >= 12 && BigEndian.U32(data, 0) == 0x74746366 ? (int)Math.Min(BigEndian.U32(data, 8), 256) : 1;

    internal static Dictionary<string, byte[]>? ReadTables(byte[] data, int index)
    {
        if (data.Length < 12) return null;
        int dir = 0;
        if (BigEndian.U32(data, 0) == 0x74746366)
        {
            int count = (int)Math.Min(BigEndian.U32(data, 8), 256);
            if (index < 0 || index >= count) return null;
            dir = (int)Math.Min(BigEndian.U32(data, 12 + index * 4), int.MaxValue);
        }
        else if (index != 0) return null;
        uint version = BigEndian.U32(data, dir);
        if (version != 0x00010000 && version != 0x74727565) return null; // TrueType outlines only
        int numTables = BigEndian.U16(data, dir + 4);
        if (numTables == 0 || numTables > 256 || dir + 12 + numTables * 16 > data.Length) return null;
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            int rec = dir + 12 + i * 16;
            string tag = Encoding.ASCII.GetString(data, rec, 4);
            long offset = BigEndian.U32(data, rec + 8), length = BigEndian.U32(data, rec + 12);
            if (offset + length > data.Length) return null;
            tables[tag] = data.AsSpan((int)offset, (int)length).ToArray();
        }
        return tables;
    }

    /// <summary>Reads only the naming strings of font <paramref name="index"/> (fast, for font catalogues).</summary>
    internal static (string Family, string Subfamily, string PostScript, int Weight, bool Italic, string LegacyFamily)? ReadNames(byte[] data, int index)
    {
        try
        {
            return NamesFromTables(ReadTables(data, index));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Number of fonts in a font file, read from its header only.</summary>
    internal static int FontCount(Stream stream)
    {
        var header = ReadAt(stream, 0, 12);
        return header != null && BigEndian.U32(header, 0) == 0x74746366 ? (int)Math.Min(BigEndian.U32(header, 8), 256) : 1;
    }

    /// <summary>
    /// <see cref="ReadNames(byte[], int)"/> reading only the table directory and the name and OS/2
    /// tables, so a catalogue of every installed font does not read hundreds of megabytes of outlines.
    /// </summary>
    internal static (string Family, string Subfamily, string PostScript, int Weight, bool Italic, string LegacyFamily)? ReadNames(Stream stream, int index)
    {
        try
        {
            long dir = 0;
            var header = ReadAt(stream, 0, 12);
            if (header == null) return null;
            if (BigEndian.U32(header, 0) == 0x74746366)
            {
                int count = (int)Math.Min(BigEndian.U32(header, 8), 256);
                if (index < 0 || index >= count || ReadAt(stream, 12 + index * 4, 4) is not { } entry) return null;
                dir = BigEndian.U32(entry, 0);
            }
            else if (index != 0) return null;
            if (ReadAt(stream, dir, 12) is not { } offsetTable) return null;
            uint version = BigEndian.U32(offsetTable, 0);
            if (version != 0x00010000 && version != 0x74727565) return null; // TrueType outlines only
            int numTables = BigEndian.U16(offsetTable, 4);
            if (numTables == 0 || numTables > 256 || ReadAt(stream, dir + 12, numTables * 16) is not { } records) return null;
            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < numTables; i++)
            {
                string tag = Encoding.ASCII.GetString(records, i * 16, 4);
                long offset = BigEndian.U32(records, i * 16 + 8), length = BigEndian.U32(records, i * 16 + 12);
                if (offset + length > stream.Length) return null;
                if (tag == "glyf") tables[tag] = Array.Empty<byte>();
                else if (tag is "name" or "OS/2" && length <= 1024 * 1024 && ReadAt(stream, offset, (int)length) is { } table) tables[tag] = table;
            }
            return NamesFromTables(tables);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or IOException)
        {
            return null;
        }
    }

    private static byte[]? ReadAt(Stream stream, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > stream.Length) return null;
        stream.Position = offset;
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static (string Family, string Subfamily, string PostScript, int Weight, bool Italic, string LegacyFamily)? NamesFromTables(Dictionary<string, byte[]>? tables)
    {
        if (tables == null || !tables.ContainsKey("glyf")) return null;
        string family = NameString(tables, 16) ?? NameString(tables, 1) ?? string.Empty;
        string sub = NameString(tables, 17) ?? NameString(tables, 2) ?? "Regular";
        string ps = NameString(tables, 6) ?? string.Empty;
        int weight = 400;
        bool italic = sub.Contains("Italic", StringComparison.OrdinalIgnoreCase) || sub.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 64)
        {
            weight = BigEndian.U16(os2, 4);
            italic |= (BigEndian.U16(os2, 62) & 1) != 0;
        }
        return (family, sub, ps, weight, italic, NameString(tables, 1) ?? family);
    }

    /// <summary>The glyph for a Unicode scalar value, 0 when the font has none.</summary>
    public int GlyphFor(int codePoint)
    {
        if (_cmap == null) return 0;
        int gid = _cmap.Has(3, 10) ? _cmap.Lookup(3, 10, codePoint) : 0;
        if (gid == 0 && codePoint <= 0xFFFF && _cmap.Has(3, 1)) gid = _cmap.Lookup(3, 1, codePoint);
        if (gid == 0 && _cmap.Has(0, 4)) gid = _cmap.Lookup(0, 4, codePoint);
        if (gid == 0 && codePoint <= 0xFFFF && _cmap.Has(0, 3)) gid = _cmap.Lookup(0, 3, codePoint);
        // Symbol fonts map their characters into the private-use area F0xx.
        if (gid == 0 && codePoint <= 0xFF && _cmap.Has(3, 0)) gid = _cmap.Lookup(3, 0, 0xF000 + codePoint);
        return gid < GlyphCount ? gid : 0;
    }

    /// <summary>Advance width in 1/1000 em.</summary>
    public double Advance(int gid) => gid >= 0 && gid < GlyphCount ? _advances[gid] * 1000.0 / UnitsPerEm : 0;

    public double ToThousandths(int units) => units * 1000.0 / UnitsPerEm;

    private OpenTypeKerning? _kerning;
    private bool _kerningRead;

    /// <summary>How much <paramref name="left"/>'s advance changes (1/1000 em) when <paramref name="right"/> follows it: the font's pair kerning.</summary>
    public double Kerning(int left, int right)
    {
        if (!_kerningRead)
        {
            _kerning = OpenTypeKerning.Read(_tables);
            _kerningRead = true;
        }
        return _kerning != null && left > 0 && right > 0 ? ToThousandths(_kerning.Pair(left, right)) : 0;
    }

    /// <summary>The glyph has an outline (subsets leave the glyphs they dropped empty).</summary>
    public bool HasOutline(int gid) => gid > 0 && gid < GlyphCount && _locaOffsets[gid + 1] > _locaOffsets[gid];

    /// <summary>
    /// A standalone font with only <paramref name="glyphs"/> (and the components of composite
    /// glyphs) kept; every other glyph is empty. Glyph ids are unchanged.
    /// </summary>
    public byte[] Subset(IEnumerable<int> glyphs)
    {
        var keep = new HashSet<int> { 0 };
        var queue = new Queue<int>(glyphs.Where(g => g > 0 && g < GlyphCount));
        var glyf = _tables["glyf"];
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            if (!keep.Add(g)) continue;
            foreach (int component in Components(glyf, g))
                if (component > 0 && component < GlyphCount && !keep.Contains(component)) queue.Enqueue(component);
        }

        var newGlyf = new List<byte>();
        var offsets = new int[GlyphCount + 1];
        for (int g = 0; g < GlyphCount; g++)
        {
            offsets[g] = newGlyf.Count;
            if (!keep.Contains(g)) continue;
            int start = _locaOffsets[g], end = _locaOffsets[g + 1];
            if (end > start && end <= glyf.Length)
            {
                newGlyf.AddRange(glyf.AsSpan(start, end - start).ToArray());
                while ((newGlyf.Count & 3) != 0) newGlyf.Add(0);
            }
        }
        offsets[GlyphCount] = newGlyf.Count;

        var loca = new byte[(GlyphCount + 1) * 4];
        for (int g = 0; g <= GlyphCount; g++) BigEndian.Put32(loca, g * 4, (uint)offsets[g]);
        var head = (byte[])_tables["head"].Clone();
        BigEndian.Put16(head, 50, 1); // long loca offsets

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head, ["hhea"] = (byte[])_tables["hhea"].Clone(), ["hmtx"] = _tables["hmtx"], ["maxp"] = _tables["maxp"],
            ["loca"] = loca, ["glyf"] = newGlyf.ToArray(),
        };
        // Hinting programs, so the text renders as crisply as the original font does.
        foreach (var tag in new[] { "cvt ", "fpgm", "prep", "OS/2", "post", "name", "cmap" })
            if (_tables.TryGetValue(tag, out var t)) tables[tag] = t;
        return Sfnt.Write(Sfnt.TrueTypeVersion, tables);
    }

    /// <summary>
    /// The font with some glyphs' advance widths replaced (font units): every glyph gets its own
    /// metrics entry. PDF readers space text by the widths the document gives, so this changes
    /// nothing drawn; it makes the program agree with those widths.
    /// </summary>
    public static byte[]? WithAdvances(byte[] font, IReadOnlyDictionary<int, int> advances)
    {
        var tables = ReadTables(font, 0);
        if (tables == null || !tables.TryGetValue("hmtx", out var hmtx) || !tables.TryGetValue("hhea", out var hhea) || !tables.TryGetValue("maxp", out var maxp)) return null;
        int count = BigEndian.U16(maxp, 4), metrics = Math.Max(1, BigEndian.U16(hhea, 34));
        var table = new byte[count * 4];
        int lastAdvance = 0, maxAdvance = 0;
        for (int g = 0; g < count; g++)
        {
            int advance, lsb;
            if (g < metrics) { advance = BigEndian.U16(hmtx, g * 4); lsb = BigEndian.S16(hmtx, g * 4 + 2); lastAdvance = advance; }
            else { advance = lastAdvance; lsb = BigEndian.S16(hmtx, metrics * 4 + (g - metrics) * 2); }
            if (advances.TryGetValue(g, out int replaced)) advance = Math.Clamp(replaced, 0, 65535);
            maxAdvance = Math.Max(maxAdvance, advance);
            BigEndian.Put16(table, g * 4, advance);
            BigEndian.Put16(table, g * 4 + 2, lsb);
        }
        var newHhea = (byte[])hhea.Clone();
        BigEndian.Put16(newHhea, 34, count);
        BigEndian.Put16(newHhea, 10, maxAdvance);
        var copy = new Dictionary<string, byte[]>(tables, StringComparer.Ordinal) { ["hmtx"] = table, ["hhea"] = newHhea };
        if (copy.TryGetValue("head", out var head)) copy["head"] = (byte[])head.Clone();
        return Sfnt.Write(Sfnt.TrueTypeVersion, copy);
    }

    private IEnumerable<int> Components(byte[] glyf, int gid)
    {
        int start = _locaOffsets[gid], end = _locaOffsets[gid + 1];
        if (end - start < 10 || end > glyf.Length) yield break;
        if (BigEndian.S16(glyf, start) >= 0) yield break; // simple glyph
        int p = start + 10;
        while (p + 4 <= end)
        {
            int flags = BigEndian.U16(glyf, p);
            yield return BigEndian.U16(glyf, p + 2);
            p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0) p += 2;
            else if ((flags & 0x0040) != 0) p += 4;
            else if ((flags & 0x0080) != 0) p += 8;
            if ((flags & 0x0020) == 0) break;
        }
    }

    private static string? NameString(Dictionary<string, byte[]> tables, int nameId)
    {
        if (!tables.TryGetValue("name", out var name) || name.Length < 6) return null;
        int count = BigEndian.U16(name, 2), storage = BigEndian.U16(name, 4);
        string? mac = null;
        for (int i = 0; i < count; i++)
        {
            int rec = 6 + i * 12;
            if (rec + 12 > name.Length) break;
            int platform = BigEndian.U16(name, rec), encoding = BigEndian.U16(name, rec + 2), language = BigEndian.U16(name, rec + 4);
            if (BigEndian.U16(name, rec + 6) != nameId) continue;
            int length = BigEndian.U16(name, rec + 8), offset = storage + BigEndian.U16(name, rec + 10);
            if (offset + length > name.Length) continue;
            if (platform == 3 && encoding is 1 or 0 or 10)
            {
                string s = Encoding.BigEndianUnicode.GetString(name, offset, length);
                if (language == 0x409 || mac == null) { if (language == 0x409) return s; mac = s; }
            }
            else if (platform == 1 && encoding == 0 && mac == null)
                mac = Encoding.Latin1.GetString(name, offset, length);
        }
        return mac;
    }

    private static string Sanitize(string ps)
    {
        var sb = new StringBuilder();
        foreach (char ch in ps)
            if (ch > 32 && ch < 127 && "[](){}<>/%".IndexOf(ch) < 0) sb.Append(ch);
        return sb.Length > 0 ? sb.ToString() : "Font";
    }
}
