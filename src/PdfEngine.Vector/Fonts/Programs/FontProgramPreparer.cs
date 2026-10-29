using System;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Fonts.Programs;

/// <summary>
/// An embedded font program normalized into a complete sfnt that platform rasterizers load, plus
/// the mapping from PDF character codes / CIDs to glyph indices in that sfnt.
/// </summary>
internal sealed class PreparedFontProgram
{
    private readonly Func<int, int, int> _glyphId;

    internal PreparedFontProgram(byte[] sfnt, PdfFontProgramFormat format, Func<int, int, int> glyphId, PdfMatrix? glyphMatrix = null)
    {
        Sfnt = sfnt;
        Format = format;
        _glyphId = glyphId;
        GlyphMatrix = glyphMatrix;
    }

    /// <summary>
    /// What remains of the program's FontMatrix after the standard 1/unitsPerEm scale (a skew for
    /// an oblique, a horizontal scale for a narrow face), in em space; null when it is identity.
    /// </summary>
    public PdfMatrix? GlyphMatrix { get; }

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
internal static class FontProgramPreparer
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
            var (sfnt, glyphMatrix) = WrapCff(font, cffTable, cff, tables);
            return sfnt == null ? null : new PreparedFontProgram(sfnt, PdfFontProgramFormat.OpenTypeCff, CffGlyphMap(font, cff, builtIn: null), glyphMatrix);
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
        var (sfnt, glyphMatrix) = WrapCff(font, data, cff, existing: null);
        return sfnt == null ? null : new PreparedFontProgram(sfnt, PdfFontProgramFormat.OpenTypeCff, CffGlyphMap(font, cff, builtIn: null), glyphMatrix);
    }

    private static PreparedFontProgram? PrepareType1(PdfFont font, byte[] data)
    {
        var converted = Type1Converter.TryConvert(data);
        if (converted == null)
            return null;
        var cff = CffFont.TryParse(converted.Cff);
        if (cff == null)
            return null;
        var (sfnt, glyphMatrix) = WrapCff(font, converted.Cff, cff, existing: null);
        return sfnt == null ? null : new PreparedFontProgram(sfnt, PdfFontProgramFormat.OpenTypeCff, CffGlyphMap(font, cff, converted.BuiltInEncoding), glyphMatrix);
    }

    /// <summary>
    /// Advances of a name-keyed CFF program for codes 0-255, in 1/1000 text space: each glyph's
    /// charstring width mapped through the FontMatrix (the x of (w, 0) x M). For simple fonts with
    /// no /Widths; null when the program cannot be read. Glyphs are chosen as for drawing.
    /// </summary>
    internal static List<double>? CffWidths(byte[] data, string?[] glyphNames, bool symbolic, double missingWidth)
    {
        if (data.Length > MaxProgramBytes || CffFont.TryParse(data) is not { IsCidKeyed: false } cff)
            return null;
        try
        {
            double a = cff.FontMatrix[0];
            return CodeWidths(SimpleGlyphMap(cff, glyphNames, symbolic, builtIn: null), gid => cff.GlyphAdvance(gid) * a * 1000.0, missingWidth);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Advances of a Type 1 (FontFile) program for codes 0-255, in 1/1000 text space: each glyph's
    /// hsbw/sbw width mapped through the FontMatrix, glyphs chosen as for drawing (the PDF encoding,
    /// the program's built-in encoding for symbolic fonts). Null when the program cannot be read.
    /// </summary>
    internal static List<double>? Type1Widths(byte[] data, string?[] glyphNames, bool symbolic, double missingWidth)
    {
        if (data.Length > MaxProgramBytes || Type1Converter.TryConvert(data) is not { } converted ||
            CffFont.TryParse(converted.Cff) is not { } cff)
            return null;
        try
        {
            var advances = converted.Advances;
            return CodeWidths(SimpleGlyphMap(cff, glyphNames, symbolic, converted.BuiltInEncoding),
                gid => gid < advances.Length ? advances[gid] : -1, missingWidth);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static List<double> CodeWidths(Func<int, int> gidForCode, Func<int, double> advance, double missingWidth)
    {
        var widths = new List<double>(256);
        for (int code = 0; code < 256; code++)
        {
            int gid = gidForCode(code);
            double w = gid > 0 ? advance(gid) : -1;
            widths.Add(w >= 0 && double.IsFinite(w) ? w : missingWidth);
        }
        return widths;
    }

    /// <summary>
    /// Code/CID → glyph index for CFF outlines. Composite: CID via the charset (CID-keyed) or as
    /// the index. Simple: the PDF encoding's glyph name through the charset; symbolic fonts prefer
    /// the program's built-in encoding (ISO 32000-2 9.6.5.2).
    /// </summary>
    private static Func<int, int, int> CffGlyphMap(PdfFont font, CffFont cff, string?[]? builtIn)
    {
        if (font.IsComposite)
            return (_, cid) => cff.GidForCid(cid);
        var simple = SimpleGlyphMap(cff, font.GlyphNames ?? Array.Empty<string?>(), font.IsSymbolic, builtIn);
        return (code, _) => Math.Max(0, simple(code));
    }

    /// <summary>Simple-font code → glyph index (0 or less when none) shared by drawing and widths.</summary>
    private static Func<int, int> SimpleGlyphMap(CffFont cff, string?[] glyphNames, bool symbolic, string?[]? builtIn)
    {
        return code =>
        {
            int BuiltIn()
            {
                if (builtIn != null)
                    return code is >= 0 and < 256 && builtIn[code] is { } n ? cff.GidForName(n) : -1;
                return cff.GidForBuiltInCode(code);
            }

            if (symbolic && BuiltIn() is int b and > 0)
                return b;
            if (code >= 0 && code < glyphNames.Length && glyphNames[code] is { } name && cff.GidForName(name) is int g and > 0)
                return g;
            return BuiltIn();
        };
    }

    private static (byte[]? Sfnt, PdfMatrix? GlyphMatrix) WrapCff(PdfFont font, byte[] cffData, CffFont cff, Dictionary<string, byte[]>? existing)
    {
        // OpenType requires the CFF FontMatrix to be a uniform 1/unitsPerEm scale. Producers write
        // 1/2048 rounded (0.00048829999), narrow faces scale x (0.00082 0 0 0.001), obliques skew
        // (0.001 0 0.000212 0.001), and some rotate or mirror: the matrix is rewritten to the
        // standard scale and the rest becomes the face's glyph matrix, applied by the renderer.
        var fm = cff.FontMatrix;
        if (fm.Length < 6 || !Invertible(fm))
            return (null, null);
        int unitsPerEm;
        PdfMatrix? glyphMatrix = null;
        if (cff.HasFontDictMatrices)
        {
            // CID-keyed with a FontMatrix in the FDArray: each glyph's outline is mapped by its Font
            // DICT's matrix concatenated with the Top DICT's (FD x Top). DirectWrite concatenates
            // them the same way, FreeType's way (scale, skew and rotation alike; both backends are
            // compared with PDFium in CffFontMatrixTests), and one glyph matrix per face could not
            // carry a different matrix per Font DICT, so the program is kept as it is. The em
            // declared in head follows the matrix of the first real glyph.
            var outline = cff.OutlineMatrix(Math.Min(1, cff.GlyphCount - 1));
            if (!Invertible(outline))
                return (null, null);
            unitsPerEm = UnitsPerEm(outline);
        }
        else
        {
            unitsPerEm = UnitsPerEm(fm);
            bool standard = fm[0] == 1.0 / unitsPerEm && fm[3] == 1.0 / unitsPerEm && fm[1] == 0 && fm[2] == 0 && fm[4] == 0 && fm[5] == 0;
            if (!standard)
            {
                // The exact 1/unitsPerEm may not fit where the producer's shorter number was
                // (0.000488281 for 1/2048): then declare 1000 units per em, whose 0.001 fits any
                // entry that holds two reals, and let the glyph matrix carry the rest of the scale.
                byte[]? rewritten = cff.WithStandardFontMatrix(unitsPerEm);
                if (rewritten == null && unitsPerEm != 1000 && (rewritten = cff.WithStandardFontMatrix(1000)) != null)
                    unitsPerEm = 1000;
                if (rewritten != null)
                {
                    cffData = rewritten;
                    var residual = new PdfMatrix(fm[0] * unitsPerEm, fm[1] * unitsPerEm, fm[2] * unitsPerEm, fm[3] * unitsPerEm, fm[4], fm[5]);
                    const double eps = 1e-4;
                    bool identity = Math.Abs(residual.A - 1) < eps && Math.Abs(residual.B) < eps && Math.Abs(residual.C) < eps &&
                                    Math.Abs(residual.D - 1) < eps && Math.Abs(residual.E) < eps && Math.Abs(residual.F) < eps;
                    glyphMatrix = identity ? null : residual;
                }
                // Otherwise the entry is too short for any standard scale (small integers, such as
                // [1 0 0 1 0 0]): the program keeps its own matrix, which DirectWrite applies.
            }
        }

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
        return (Sfnt.Write(Sfnt.OpenTypeCffVersion, tables), glyphMatrix);
    }

    private static bool Invertible(double[] m)
    {
        double det = m[0] * m[3] - m[1] * m[2];
        return double.IsFinite(det) && Math.Abs(det) > 1e-14 && double.IsFinite(m[4]) && double.IsFinite(m[5]);
    }

    /// <summary>
    /// The em a FontMatrix implies: 1/d for an upright matrix (as before: a narrow face keeps its
    /// 1000 units), else the inverse of its mean scale (a rotated or mirrored one); 1000 when that
    /// is outside what head can declare.
    /// </summary>
    private static int UnitsPerEm(double[] m)
    {
        if (m[1] == 0 && m[3] > 0 && Math.Round(1.0 / m[3]) is >= 16 and <= 16384)
            return (int)Math.Round(1.0 / m[3]);
        double scale = Math.Sqrt(Math.Abs(m[0] * m[3] - m[1] * m[2]));
        double units = Math.Round(1.0 / scale);
        return units is >= 16 and <= 16384 ? (int)units : 1000;
    }

    private static short Clamp16(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
}
