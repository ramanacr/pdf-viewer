using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEngine.Vector.Fonts.Programs;

/// <summary>
/// An embedded font program normalized into a complete sfnt that platform rasterizers load, plus
/// the mapping from PDF character codes / CIDs to glyph indices in that sfnt.
/// </summary>
public sealed class PreparedFontProgram
{
    private readonly Func<int, int, int> _glyphId;

    internal PreparedFontProgram(byte[] sfnt, PdfFontProgramFormat format, Func<int, int, int> glyphId)
    {
        Sfnt = sfnt;
        Format = format;
        _glyphId = glyphId;
    }

    public byte[] Sfnt { get; }

    /// <summary><see cref="PdfFontProgramFormat.TrueType"/> or <see cref="PdfFontProgramFormat.OpenTypeCff"/>.</summary>
    public PdfFontProgramFormat Format { get; }

    /// <summary>Glyph index for a code (simple fonts) or CID (composite fonts); 0 is .notdef.</summary>
    public int GetGlyphId(int code, int cid) => _glyphId(code, cid);
}

/// <summary>
/// Turns every embedded program kind into something a backend can load (ISO 32000-2 9.9):
/// TrueType subsets get the bookkeeping tables they may lack; bare CFF (Type1C, CIDFontType0C) is
/// wrapped in OpenType; Type 1 (FontFile) is converted to CFF first. Returns null when the program
/// is absent or cannot be prepared — the caller then classifies the text as fallback.
/// </summary>
public static class FontProgramPreparer
{
    /// <summary>Largest program accepted for preparation (the stream decoder bounds it earlier too).</summary>
    public const int MaxProgramBytes = 32 * 1024 * 1024;

    public static PreparedFontProgram? Prepare(PdfFont font)
    {
        byte[]? data = font.EmbeddedFontData;
        if (data == null || data.Length == 0 || data.Length > MaxProgramBytes)
            return null;

        try
        {
            return font.EmbeddedProgramKind switch
            {
                PdfFontProgramKind.TrueType => PrepareSfnt(font, data),
                PdfFontProgramKind.OpenType => PrepareSfnt(font, data),
                PdfFontProgramKind.Type1C => PrepareBareCff(font, data),
                PdfFontProgramKind.CidFontType0C => PrepareBareCff(font, data),
                PdfFontProgramKind.Type1 => PrepareType1(font, data),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ sfnt (TrueType / OpenType)

    private static PreparedFontProgram? PrepareSfnt(PdfFont font, byte[] data)
    {
        var tables = Sfnt.ReadTables(data, out uint version);
        if (tables == null)
            return null;

        if (tables.TryGetValue("CFF ", out var cffTable))
        {
            // OpenType with CFF outlines: glyph selection goes through the CFF charset.
            var cff = CffFont.TryParse(cffTable);
            if (cff == null)
                return null;
            var sfnt = WrapCff(font, cffTable, cff, tables);
            return sfnt == null ? null : new PreparedFontProgram(sfnt, PdfFontProgramFormat.OpenTypeCff, CffGlyphMap(font, cff, builtIn: null));
        }

        if (!tables.TryGetValue("head", out var head) || head.Length < 54 ||
            !tables.TryGetValue("maxp", out var maxp) || maxp.Length < 6 ||
            !tables.ContainsKey("glyf") || !tables.TryGetValue("loca", out var loca))
            return null;

        int numGlyphs = BigEndian.U16(maxp, 4);
        int unitsPerEm = BigEndian.U16(head, 18);
        int locFormat = BigEndian.S16(head, 50);
        if (numGlyphs == 0 || unitsPerEm is < 16 or > 16384 || locFormat is not (0 or 1))
            return null;
        if (loca.Length < (numGlyphs + 1) * (locFormat == 0 ? 2 : 4))
            return null; // truncated glyph index: the outlines cannot be located

        BigEndian.Put32(head, 12, 0x5F0F3CF5); // magic number, occasionally zeroed by subsetters

        short xMin = BigEndian.S16(head, 36), yMin = BigEndian.S16(head, 38), xMax = BigEndian.S16(head, 40), yMax = BigEndian.S16(head, 42);
        bool metricsOk = tables.TryGetValue("hhea", out var hhea) && hhea.Length >= 36 && tables.TryGetValue("hmtx", out var hmtx) &&
                         BigEndian.U16(hhea, 34) is int nhm && nhm >= 1 && nhm <= numGlyphs &&
                         hmtx.Length >= nhm * 4 + (numGlyphs - nhm) * 2;
        if (!metricsOk)
        {
            tables["hhea"] = Sfnt.Hhea(yMax, yMin, unitsPerEm, xMax, numGlyphs);
            tables["hmtx"] = Sfnt.Hmtx(numGlyphs, unitsPerEm / 2);
        }

        // Replace the tables subsetters drop or mangle. Glyph indices are unaffected: the viewer
        // selects glyphs by index, and the mapping below reads the ORIGINAL cmap.
        AddBookkeeping(font, tables, unitsPerEm, yMax, yMin, numGlyphs);
        byte[] result = Sfnt.Write(Sfnt.TrueTypeVersion, tables);

        bool hasCmap = TrueTypeCmap.TryParse(data) != null;
        return new PreparedFontProgram(result, PdfFontProgramFormat.TrueType, (code, cid) =>
        {
            int gid = font.GetGlyphId(code, cid);
            if (gid >= 0)
                return gid < numGlyphs ? gid : 0;
            // Symbolic subsets without a cmap: codes are glyph indices in practice (and in PDFium).
            int fallback = font.IsComposite ? cid : code;
            return !hasCmap && fallback >= 0 && fallback < numGlyphs ? fallback : 0;
        });
    }

    private static void AddBookkeeping(PdfFont font, Dictionary<string, byte[]> tables, int unitsPerEm, short ascender, short descender, int numGlyphs)
    {
        string ps = Sanitize(font.PostScriptName ?? font.BaseFont);
        tables["name"] = Sfnt.Name(ps, ps, font.IsBold, font.IsItalic);
        tables["OS/2"] = Sfnt.Os2(unitsPerEm, ascender, descender, font.IsBold, font.IsItalic, unitsPerEm / 2);
        tables["post"] = Sfnt.Post(font.IsFixedPitch);
        tables["cmap"] = Sfnt.PrivateUseCmap(numGlyphs);
        tables.Remove("DSIG"); // a signature over tables we rewrote would be invalid
    }

    private static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is < (char)33 or > (char)126) chars[i] = '_';
        string s = new string(chars);
        return s.Length == 0 ? "PdfEmbedded" : s;
    }

    // ------------------------------------------------------------------ CFF

    private static PreparedFontProgram? PrepareBareCff(PdfFont font, byte[] data)
    {
        var cff = CffFont.TryParse(data);
        if (cff == null)
            return null;
        var sfnt = WrapCff(font, data, cff, existing: null);
        return sfnt == null ? null : new PreparedFontProgram(sfnt, PdfFontProgramFormat.OpenTypeCff, CffGlyphMap(font, cff, builtIn: null));
    }

    private static PreparedFontProgram? PrepareType1(PdfFont font, byte[] data)
    {
        var converted = Type1Converter.TryConvert(data);
        if (converted == null)
            return null;
        var cff = CffFont.TryParse(converted.Cff);
        if (cff == null)
            return null;
        var sfnt = WrapCff(font, converted.Cff, cff, existing: null);
        return sfnt == null ? null : new PreparedFontProgram(sfnt, PdfFontProgramFormat.OpenTypeCff, CffGlyphMap(font, cff, converted.BuiltInEncoding));
    }

    /// <summary>
    /// Code/CID → glyph index for CFF outlines. Composite: CID via the charset (CID-keyed) or as
    /// the index. Simple: the PDF encoding's glyph name through the charset; symbolic fonts prefer
    /// the program's built-in encoding (ISO 32000-2 9.6.5.2).
    /// </summary>
    private static Func<int, int, int> CffGlyphMap(PdfFont font, CffFont cff, string?[]? builtIn)
    {
        return (code, cid) =>
        {
            if (font.IsComposite)
                return cff.GidForCid(cid);

            int BuiltIn()
            {
                if (builtIn != null)
                    return code is >= 0 and < 256 && builtIn[code] is { } n ? cff.GidForName(n) : -1;
                return cff.GidForBuiltInCode(code);
            }

            if (font.IsSymbolic && BuiltIn() is int b and > 0)
                return b;
            if (font.GetGlyphName(code) is { } name && cff.GidForName(name) is int g and > 0)
                return g;
            int fallback = BuiltIn();
            return fallback > 0 ? fallback : 0;
        };
    }

    private static byte[]? WrapCff(PdfFont font, byte[] cffData, CffFont cff, Dictionary<string, byte[]>? existing)
    {
        // OpenType requires the CFF FontMatrix to be a uniform 1/unitsPerEm scale.
        var fm = cff.FontMatrix;
        if (fm.Length < 6 || fm[0] <= 0 || Math.Abs(fm[0] - fm[3]) > 1e-9 || fm[1] != 0 || fm[2] != 0 || fm[4] != 0 || fm[5] != 0)
            return null;
        int unitsPerEm = (int)Math.Round(1.0 / fm[0]);
        if (unitsPerEm is < 16 or > 16384 || Math.Abs(1.0 / unitsPerEm - fm[0]) > 1e-9)
            return null;

        var bb = cff.FontBBox;
        short xMin = Clamp16(bb[0]), yMin = Clamp16(bb[1]), xMax = Clamp16(bb[2]), yMax = Clamp16(bb[3]);
        int n = cff.GlyphCount;

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["CFF "] = cffData,
            ["head"] = Sfnt.Head(unitsPerEm, xMin, yMin, xMax, yMax, font.IsBold, font.IsItalic),
            ["hhea"] = Sfnt.Hhea(yMax, yMin, unitsPerEm, xMax, n),
            ["hmtx"] = Sfnt.Hmtx(n, unitsPerEm / 2),
            ["maxp"] = Sfnt.MaxpCff(n),
        };
        if (existing != null)
        {
            // Keep layout tables of a real OpenType font (GSUB/GPOS are unused, but harmless).
            foreach (var (tag, value) in existing)
            {
                if (tag is "head" or "hhea" or "hmtx" or "maxp")
                    tables[tag] = value;
                else if (!tables.ContainsKey(tag))
                    tables[tag] = value;
            }
        }
        AddBookkeeping(font, tables, unitsPerEm, yMax, yMin, n);
        return Sfnt.Write(Sfnt.OpenTypeCffVersion, tables);
    }

    private static short Clamp16(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
}
